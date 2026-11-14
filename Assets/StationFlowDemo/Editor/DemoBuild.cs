#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StationFlow.SyntheticTraffic.Editor
{
    /// <summary>Visible menu actions and an opt-in file command for repeatable local build verification.</summary>
    [InitializeOnLoad]
    public static class DemoBuild
    {
        const string ScenePath="Assets/StationFlowDemo/Scenes/JunctionLab.unity";
        static double nextPoll;
        static DemoBuild(){EditorApplication.update+=Poll;}
        static void Poll()
        {
            if(EditorApplication.timeSinceStartup<nextPoll||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            nextPoll=EditorApplication.timeSinceStartup+2;
            string path=Path.GetFullPath("BuildRequests/command.txt");if(!File.Exists(path))return;
            string command=File.ReadAllText(path).Trim();File.Delete(path);
            try
            {
                if(command=="build")BuildWindows();else if(command=="scene")CreateScene();else throw new ArgumentException("Unknown build command");
                File.WriteAllText("BuildRequests/result.json","{\"success\":true,\"command\":\""+command+"\"}");
            }
            catch(Exception error){Debug.LogException(error);File.WriteAllText("BuildRequests/result.json",JsonUtility.ToJson(new Failure{error=error.ToString()}));}
        }
        [Serializable]class Failure{public bool success=false;public string error;}
        [MenuItem("Efrei Research Lab/Create demo scene")]
        public static void CreateScene()
        {
            Directory.CreateDirectory("Assets/StationFlowDemo/Scenes");Directory.CreateDirectory("Assets/StationFlowDemo/Resources");
            KeepShader("Universal Render Pipeline/Lit","DemoLit");KeepShader("Skybox/Procedural","DemoSky");KeepShader("Skybox/Panoramic","PanoramicSky");
            ConfigureRenderAssets();
            if(!File.Exists(ScenePath))
            {
                Scene scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                GameObject root=new GameObject("Efrei Research Lab");SceneManager.MoveGameObjectToScene(root,scene);root.AddComponent<DemoController>();
                EditorSceneManager.SaveScene(scene,ScenePath);EditorSceneManager.CloseScene(scene,true);
            }
            if(!EditorBuildSettings.scenes.Any(scene=>scene.path==ScenePath))
                EditorBuildSettings.scenes=EditorBuildSettings.scenes.Concat(new[]{new EditorBuildSettingsScene(ScenePath,true)}).ToArray();
            PlayerSettings.companyName="Efrei Research Lab";PlayerSettings.productName="GareFlowDemo";
            PlayerSettings.defaultScreenWidth=1600;PlayerSettings.defaultScreenHeight=1000;PlayerSettings.fullScreenMode=FullScreenMode.Windowed;
            PlayerSettings.runInBackground=true;PlayerSettings.resizableWindow=true;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
            AssetDatabase.SaveAssets();
        }
        static void KeepShader(string shader,string name)
        {
            string path="Assets/StationFlowDemo/Resources/"+name+".mat";
            if(!File.Exists(path))AssetDatabase.CreateAsset(new Material(Shader.Find(shader)),path);
        }
        static void ConfigureRenderAssets()
        {
            var logo=AssetImporter.GetAtPath("Assets/StationFlowDemo/Resources/Branding/EfreiResearchLab.png") as TextureImporter;
            if(logo!=null&&(logo.mipmapEnabled||logo.textureCompression!=TextureImporterCompression.Uncompressed||logo.npotScale!=TextureImporterNPOTScale.None))
            {logo.mipmapEnabled=false;logo.textureCompression=TextureImporterCompression.Uncompressed;logo.npotScale=TextureImporterNPOTScale.None;logo.filterMode=FilterMode.Bilinear;logo.SaveAndReimport();}
            // Runtime citizens need lit cutouts, normal maps and lamps. Serialized materials retain those variants.
            for(int mask=1;mask<16;mask++)
            {
                string variantPath="Assets/StationFlowDemo/Resources/UrbanLit"+mask+".mat";
                var material=AssetDatabase.LoadAssetAtPath<Material>(variantPath);
                if(material==null){material=new Material(Shader.Find("Universal Render Pipeline/Lit"));AssetDatabase.CreateAsset(material,variantPath);}
                if((mask&1)!=0)material.EnableKeyword("_NORMALMAP");
                if((mask&2)!=0){material.EnableKeyword("_ALPHATEST_ON");material.SetFloat("_AlphaClip",1);material.renderQueue=2450;}
                if((mask&4)!=0){material.EnableKeyword("_EMISSION");material.SetColor("_EmissionColor",Color.white);}
                if((mask&8)!=0)material.EnableKeyword("_METALLICSPECGLOSSMAP");
                EditorUtility.SetDirty(material);
            }
            const string path="Assets/StationFlowDemo/Resources/UrbanLook.asset";
            var profile=AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if(profile==null){profile=ScriptableObject.CreateInstance<VolumeProfile>();AssetDatabase.CreateAsset(profile,path);}
            if(!profile.TryGet<Tonemapping>(out var tone)){tone=profile.Add<Tonemapping>(true);AssetDatabase.AddObjectToAsset(tone,profile);}tone.mode.Override(TonemappingMode.ACES);
            if(!profile.TryGet<Bloom>(out var bloom)){bloom=profile.Add<Bloom>(true);AssetDatabase.AddObjectToAsset(bloom,profile);}bloom.intensity.Override(.12f);bloom.threshold.Override(1.4f);
            if(!profile.TryGet<ColorAdjustments>(out var color)){color=profile.Add<ColorAdjustments>(true);AssetDatabase.AddObjectToAsset(color,profile);}color.postExposure.Override(-.15f);
            if(!profile.TryGet<WhiteBalance>(out var balance)){balance=profile.Add<WhiteBalance>(true);AssetDatabase.AddObjectToAsset(balance,profile);}balance.temperature.Override(3);
            EditorUtility.SetDirty(profile);
            var sky=AssetImporter.GetAtPath("Assets/StationFlowDemo/Rendering/Resources/StationFlowLighting/clouds.hdr") as TextureImporter;
            if(sky!=null&&(sky.textureShape!=TextureImporterShape.Texture2D||sky.sRGBTexture||sky.maxTextureSize!=2048))
            {sky.textureShape=TextureImporterShape.Texture2D;sky.sRGBTexture=false;sky.maxTextureSize=2048;sky.mipmapEnabled=true;sky.filterMode=FilterMode.Trilinear;sky.SaveAndReimport();}
            var urp=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
            if(urp!=null){urp.supportsHDR=true;urp.shadowDistance=115;urp.renderScale=1;urp.msaaSampleCount=1;EditorUtility.SetDirty(urp);}
            var data=AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/PC_Renderer.asset");
            if(data!=null)foreach(var feature in data.rendererFeatures)
            {
                if(feature==null||!feature.name.Contains("AmbientOcclusion"))continue;
                var serialized=new SerializedObject(feature);serialized.FindProperty("m_Settings.Intensity").floatValue=.7f;serialized.FindProperty("m_Settings.Radius").floatValue=.38f;
                serialized.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(feature);
            }
        }
        [MenuItem("Efrei Research Lab/Build Windows demo")]
        public static void BuildWindows()
        {
            CreateScene();Directory.CreateDirectory("Builds/EfreiResearchLab-LivingStreets");Directory.CreateDirectory("BuildRequests");
            WriteBuildInfo();AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{ScenePath},locationPathName="Builds/EfreiResearchLab-LivingStreets/EfreiResearchLab.exe",target=BuildTarget.StandaloneWindows64,options=BuildOptions.None});
            string json="{\"result\":\""+report.summary.result+"\",\"errors\":"+report.summary.totalErrors+",\"warnings\":"+report.summary.totalWarnings+",\"bytes\":"+report.summary.totalSize+",\"seconds\":"+report.summary.totalTime.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)+"}";
            File.WriteAllText("BuildRequests/build-report.json",json);
            if(report.summary.result!=BuildResult.Succeeded)throw new Exception("Unity build failed: "+report.summary.result);
        }
        [Serializable]class SourceHash{public string path,sha256;}
        [Serializable]class BuildInfo{public string schema="stationflow.build.v1",unityVersion,createdUtc;public SourceHash[] sourceFiles;}
        static void WriteBuildInfo()
        {
            var info=new BuildInfo{unityVersion=Application.unityVersion,createdUtc=DateTime.UtcNow.ToString("O"),sourceFiles=Directory.GetFiles("Assets/StationFlowDemo","*",SearchOption.AllDirectories).Where(p=>p.EndsWith(".cs")||p.EndsWith(".shader")).OrderBy(p=>p).Select(p=>new SourceHash{path=p.Replace('\\','/'),sha256=Hash(p)}).ToArray()};
            File.WriteAllText("Assets/StationFlowDemo/Resources/build-info.json",JsonUtility.ToJson(info,true));
        }
        static string Hash(string path){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant();}
        [MenuItem("Efrei Research Lab/Open demo scene")]
        public static void OpenDemo(){CreateScene();EditorSceneManager.OpenScene(ScenePath);}
    }
}
#endif
