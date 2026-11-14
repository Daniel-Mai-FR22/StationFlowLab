#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace StationFlow.HumanAnimation.Editor
{
    [InitializeOnLoad]
    public static class HumanAnimationCommandBridge
    {
        static double nextPoll;
        static HumanAnimationCommandBridge() { EditorApplication.update += Poll; }
        static void Poll()
        {
            if(EditorApplication.timeSinceStartup<nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)return;
            nextPoll=EditorApplication.timeSinceStartup+2;
            const string path="BuildRequests/human-animation-command.txt";
            if(!File.Exists(path))return;
            var lines=File.ReadAllLines(path);File.Delete(path);
            string command=lines.Length>0?lines[0].Trim():"";
            try
            {
                if(command=="build")HumanAnimationAuthoring.BuildAll();
                else if(command=="open")HumanAnimationAuthoring.OpenLab();
                else if(command=="validate")HumanAnimationAuthoring.Validate();
                else if(command=="layout")HumanAnimationAuthoring.RefineLabViews();
                else if(command=="repair-rigs")
                {
                    var method=typeof(HumanDiagnosticModels).GetMethod("RepairDiagnosticRigs",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static);
                    if(method==null)throw new InvalidOperationException("Diagnostic rig repair is unavailable.");
                    method.Invoke(null,null);
                }
                else if(command=="import")
                {
                    if(lines.Length<2)throw new ArgumentException("Expected absolute FBX source directory on line two.");
                    HumanMixamoImporter.ImportFolder(lines[1]);HumanAnimationAuthoring.RebuildMixamoLibrary();
                }
                else if(command=="play-check")InvokeVerification("BeginPlayCheck");
                else if(command=="capture")HumanAnimationCapture.Capture();
                else if(command=="editor-check")InvokeVerification("CheckEditorPreview");
                else throw new ArgumentException("Unknown human animation command: "+command);
                File.WriteAllText("BuildRequests/human-animation-result.json",JsonUtility.ToJson(new Result{success=true,command=command}));
            }
            catch(Exception error)
            {
                Debug.LogException(error);File.WriteAllText("BuildRequests/human-animation-result.json",JsonUtility.ToJson(new Result{success=false,command=command,error=error.ToString()},true));
            }
        }
        static void InvokeVerification(string method)
        {
            var type=typeof(HumanAnimationCommandBridge).Assembly.GetType("StationFlow.HumanAnimation.Editor.HumanAnimationVerification");
            var action=type?.GetMethod(method,System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static);
            if(action==null)throw new InvalidOperationException("Verification method unavailable: "+method);
            action.Invoke(null,null);
        }
        [Serializable]class Result {public bool success;public string command,error;}
    }
}
#endif
