#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Read-only native serialized-binding and shader checks after project identity migration.</summary>
    public static class StationFlowProjectVerification
    {
        public const string ReportPath = "Reports/ProjectIdentity/native-bindings.json";
        static readonly string[] ScenePaths = { "Assets/TrainStation/Scenes/TrainStationDaily.unity", "Assets/TrainStation/Scenes/TrainStation.unity", "Assets/TrainStation/Scenes/TrainStationDecisionLab.unity" };
        [Serializable] sealed class FileEvidence { public string path, before, after; public bool unchanged; }
        [Serializable] sealed class BindingEvidence
        {
            public string path, kind;
            public int gameObjects, behaviours, projectBehaviours, missingScripts, scriptTypeMismatches, brokenObjectReferences, importErrors;
            public List<string> problems = new List<string>();
        }
        [Serializable] sealed class ShaderEvidence
        {
            public string path, name;
            public bool shaderLoaded, shaderHasError;
            public int compilerErrors, compilerWarnings, importErrors, importWarnings;
            public List<string> messages = new List<string>();
        }
        [Serializable] sealed class Report
        {
            public bool success, stoppedEditor, originalSceneSetupRestored, savedFilesUnchanged, namespaceChecksPassed;
            public string startedUtc, completedUtc, projectRoot, unityVersion, activeSceneBefore, activeSceneAfter;
            public int runtimeTypes, projectRuntimeTypes, scenesChecked, prefabsChecked, shadersChecked, missingScripts, scriptTypeMismatches, brokenObjectReferences, importErrors;
            public string[] runtimeNamespaces;
            public List<BindingEvidence> bindings = new List<BindingEvidence>();
            public List<ShaderEvidence> shaders = new List<ShaderEvidence>();
            public List<FileEvidence> files = new List<FileEvidence>();
            public List<string> errors = new List<string>();
            public string scope = "Native compiled namespaces, three saved scenes, every Assets prefab and owned shader compiler/import diagnostics. Scene and prefab previews are opened only for inspection; no saved asset is written. This does not compile every possible shader variant or certify runtime navigation throughput.";
        }
        public static void Run()
        {
            var report = new Report { startedUtc=DateTime.UtcNow.ToString("O"),projectRoot=Path.GetFullPath("."),unityVersion=Application.unityVersion };
            var originalSetup=EditorSceneManager.GetSceneManagerSetup(); var originalActive=SceneManager.GetActiveScene();
            report.activeSceneBefore=originalActive.path; var opened=new List<Scene>();
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !BuildPipeline.isBuildingPlayer,"Binding verification requires stopped, compiled Edit mode.");
                for(int i=0;i<SceneManager.sceneCount;++i) Require(!SceneManager.GetSceneAt(i).isDirty,"Save existing scene changes before read-only binding verification.");
                report.stoppedEditor=true;
                var prefabs=AssetDatabase.FindAssets("t:Prefab",new[]{"Assets"}).Select(AssetDatabase.GUIDToAssetPath).Where(p=>p.EndsWith(".prefab",StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(p=>p,StringComparer.Ordinal).ToArray();
                var shaders=AssetDatabase.FindAssets("t:Shader",new[]{"Assets"}).Select(AssetDatabase.GUIDToAssetPath).Where(p=>p.EndsWith(".shader",StringComparison.OrdinalIgnoreCase)||p.EndsWith(".shadergraph",StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(p=>p,StringComparer.Ordinal).ToArray();
                foreach(string path in ScenePaths.Concat(prefabs).Concat(shaders).Distinct())
                { Require(File.Exists(path),"A binding fixture source file is missing: "+path); report.files.Add(new FileEvidence{path=path,before=Digest(path)}); }
                CheckRuntimeNamespaces(report);
                foreach(string path in ScenePaths)
                {
                    Scene scene=SceneManager.GetSceneByPath(path); bool added=!scene.IsValid()||!scene.isLoaded;
                    try
                    {
                        if(added){scene=EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);opened.Add(scene);}
                        var evidence=new BindingEvidence{path=path,kind="scene"};
                        foreach(var root in scene.GetRootGameObjects()) CheckHierarchy(root,evidence);
                        evidence.importErrors=ImportErrors(path,evidence.problems); CompleteBinding(evidence,report); ++report.scenesChecked;
                    }
                    finally
                    {
                        if(added&&scene.IsValid()&&scene.isLoaded){EditorSceneManager.CloseScene(scene,true);opened.RemoveAll(s=>s.handle==scene.handle);}
                        if(originalActive.IsValid()&&originalActive.isLoaded) SceneManager.SetActiveScene(originalActive);
                    }
                }
                foreach(string path in prefabs)
                {
                    GameObject preview=null;
                    try
                    {
                        preview=PrefabUtility.LoadPrefabContents(path); Require(preview,"Could not load saved prefab: "+path);
                        var evidence=new BindingEvidence{path=path,kind="prefab"}; CheckHierarchy(preview,evidence);
                        evidence.importErrors=ImportErrors(path,evidence.problems);CompleteBinding(evidence,report);++report.prefabsChecked;
                    }
                    finally {if(preview) PrefabUtility.UnloadPrefabContents(preview);}
                }
                foreach(string path in shaders)
                {
                    var shader=AssetDatabase.LoadAllAssetsAtPath(path).OfType<Shader>().FirstOrDefault();
                    var evidence=new ShaderEvidence{path=path,shaderLoaded=shader,name=shader?shader.name:""};
                    if(!shader) report.errors.Add("No imported Shader object exists for saved source: "+path);
                    else
                    {
                        evidence.shaderHasError=ShaderUtil.ShaderHasError(shader);
                        foreach(var message in ShaderUtil.GetShaderMessages(shader))
                        {
                            if(message.severity.ToString()=="Error")++evidence.compilerErrors;else++evidence.compilerWarnings;
                            evidence.messages.Add(message.severity+": "+message.message+" ("+message.file+":"+message.line+")");
                        }
                    }
                    var import=AssetImporter.GetImportLog(path);
                    if(import!=null&&import.logEntries!=null) foreach(var message in import.logEntries)
                    {
                        if((message.flags&ImportLogFlags.Error)!=0)++evidence.importErrors;
                        if((message.flags&ImportLogFlags.Warning)!=0)++evidence.importWarnings;
                        evidence.messages.Add("Import "+message.flags+": "+message.message);
                    }
                    if(evidence.shaderHasError||evidence.compilerErrors>0||evidence.importErrors>0)report.errors.Add("Owned shader has compiler or import errors: "+path);
                    report.importErrors+=evidence.importErrors;report.shaders.Add(evidence);++report.shadersChecked;
                }
                Require(report.scenesChecked==ScenePaths.Length&&report.prefabsChecked>0&&report.shadersChecked>0,"Expected saved scenes, prefabs or shader sources were not inspected.");
            }
            catch(Exception error){report.errors.Add(error.ToString());}
            finally
            {
                foreach(var scene in opened.AsEnumerable().Reverse())if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
                if(originalActive.IsValid()&&originalActive.isLoaded)SceneManager.SetActiveScene(originalActive);
                report.originalSceneSetupRestored=SetupKey(originalSetup)==SetupKey(EditorSceneManager.GetSceneManagerSetup());
                report.activeSceneAfter=SceneManager.GetActiveScene().path;
                if(!report.originalSceneSetupRestored)report.errors.Add("The original editor scene setup was not restored.");
                foreach(var file in report.files){file.after=File.Exists(file.path)?Digest(file.path):"missing";file.unchanged=file.before==file.after;}
                report.savedFilesUnchanged=report.files.Count>0&&report.files.All(f=>f.unchanged);
                if(!report.savedFilesUnchanged)report.errors.Add("A saved scene, prefab or shader source changed during read-only verification.");
                report.completedUtc=DateTime.UtcNow.ToString("O");report.success=report.errors.Count==0;
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true));
            }
            Require(report.success,string.Join("\n",report.errors));
        }
        static void CheckRuntimeNamespaces(Report report)
        {
            var assembly=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Assembly-CSharp");
            var types=assembly.GetTypes();report.runtimeTypes=types.Length;
            report.runtimeNamespaces=types.Where(t=>!string.IsNullOrEmpty(t.Namespace)).Select(t=>t.Namespace).Distinct().OrderBy(n=>n,StringComparer.Ordinal).ToArray();
            string[] engineRoots={"Unity","TMPro","System","Microsoft","JetBrains"};
            foreach(string space in report.runtimeNamespaces)
                if(!space.StartsWith("StationFlow",StringComparison.Ordinal)&&!engineRoots.Any(n=>space.StartsWith(n,StringComparison.Ordinal)))report.errors.Add("Authored runtime namespace is outside current project identity: "+space);
            report.projectRuntimeTypes=types.Count(t=>t.Namespace!=null&&(t.Namespace=="StationFlow"||t.Namespace.StartsWith("StationFlow.",StringComparison.Ordinal)));
            foreach(string type in new[]{"StationFlow.TrainStation.DecisionLab.StationDecisionWorld","StationFlow.TrainStation.DecisionLab.StationDecisionPassenger","StationFlow.TrainStation.Social.StationActorMotion","StationFlow.TrainStation.StationSurveillanceCamera","StationFlow.HumanAnimation.HumanAnimationPlayer"})
                Require(assembly.GetType(type,false)!=null,"A required compiled project type is missing: "+type);
            Require(report.projectRuntimeTypes>0,"No compiled runtime project types use current namespace.");report.namespaceChecksPassed=report.errors.Count==0;
        }
        static void CheckHierarchy(GameObject root,BindingEvidence evidence)
        {
            foreach(var transform in root.GetComponentsInChildren<Transform>(true))
            {
                ++evidence.gameObjects;int missing=GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);evidence.missingScripts+=missing;
                if(missing>0)evidence.problems.Add("Missing script at "+HierarchyPath(transform));
                foreach(var component in transform.GetComponents<MonoBehaviour>())
                {
                    if(!component)continue;++evidence.behaviours;var type=component.GetType();
                    if(type.Namespace!=null&&type.Namespace.StartsWith("StationFlow",StringComparison.Ordinal))++evidence.projectBehaviours;
                    var script=MonoScript.FromMonoBehaviour(component);
                    if(!script||script.GetClass()!=type){++evidence.scriptTypeMismatches;evidence.problems.Add("Script/type mismatch at "+HierarchyPath(transform)+" / "+type.FullName);}
                    var serialized=new SerializedObject(component);var property=serialized.GetIterator();
                    while(property.NextVisible(true))if(property.propertyType==SerializedPropertyType.ObjectReference&&property.objectReferenceValue==null&&property.objectReferenceEntityIdValue!=EntityId.None)
                    {++evidence.brokenObjectReferences;evidence.problems.Add("Missing saved reference at "+HierarchyPath(transform)+" / "+type.Name+"."+property.propertyPath);}
                }
            }
        }
        static void CompleteBinding(BindingEvidence evidence,Report report)
        {
            report.missingScripts+=evidence.missingScripts;report.scriptTypeMismatches+=evidence.scriptTypeMismatches;report.brokenObjectReferences+=evidence.brokenObjectReferences;report.importErrors+=evidence.importErrors;
            if(evidence.missingScripts>0||evidence.scriptTypeMismatches>0||evidence.brokenObjectReferences>0||evidence.importErrors>0)report.errors.Add("Saved "+evidence.kind+" has broken bindings or import errors: "+evidence.path);
            report.bindings.Add(evidence);
        }
        static int ImportErrors(string path,List<string> problems)
        {
            int count=0;var import=AssetImporter.GetImportLog(path);
            if(import!=null&&import.logEntries!=null)foreach(var entry in import.logEntries)if((entry.flags&ImportLogFlags.Error)!=0){++count;problems.Add("Import error: "+entry.message);}return count;
        }
        static string HierarchyPath(Transform transform){var names=new List<string>();for(var item=transform;item;item=item.parent)names.Add(item.name);names.Reverse();return string.Join("/",names);}
        static string SetupKey(SceneSetup[] setup)=>string.Join("\n",setup.Select(s=>s.path+"|"+s.isLoaded+"|"+s.isActive));
        static string Digest(string path){using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
#endif