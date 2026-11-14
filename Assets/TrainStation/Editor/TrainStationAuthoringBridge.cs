#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StationFlow.TrainStation.Editor
{
    /// <summary>Local editor command inbox for repeatable authoring and visual verification.</summary>
    [InitializeOnLoad]
    public static class TrainStationAuthoringBridge
    {
        static double nextPoll;
        static TrainStationAuthoringBridge() { EditorApplication.update += Poll; }
        static void Poll()
        {
            if(EditorApplication.timeSinceStartup<nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)return;
            nextPoll=EditorApplication.timeSinceStartup+2;
            const string path="BuildRequests/trainstation-command.txt";
            if(!File.Exists(path))return;
            string command=File.ReadAllText(path).Trim();File.Delete(path);
            try
            {
                if(command=="create")TrainStationBuilder.Create();
                else if(command=="refresh")AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                else if(command=="cameras")InvokeCamera("StationCameraAuthoring","Populate");
                else if(command=="cameras-validate")InvokeCamera("StationCameraAuthoring","Validate");
                else if(command=="cameras-refine")InvokeCamera("StationCameraAuthoring","RefineCoverage");
                else if(command=="cameras-saved-check")InvokeCamera("StationCameraVerification","ValidateSavedScene");
                else if(command=="cameras-play-check")InvokeCamera("StationCameraVerification","BeginPlayCheck");
                else if(command=="cameras-capture")InvokeCamera("StationCameraCapture","Capture");
                else if(command=="capture")TrainStationReview.Capture();
                else if(command=="validate")TrainStationBuilder.ValidateActive();
                else if(command=="access")TrainStationAccessCheck.Check();
                else if(command=="play-check")TrainStationPlayModeCheck.Begin();
                else if(command=="open")TrainStationBuilder.Open();
                else throw new ArgumentException("Unknown TrainStation command: "+command);
                File.WriteAllText("BuildRequests/trainstation-result.json","{\"success\":true,\"command\":\""+command+"\"}");
            }
            catch(Exception error)
            {
                Debug.LogException(error);File.WriteAllText("BuildRequests/trainstation-result.json",JsonUtility.ToJson(new Failure{error=error.ToString()},true));
            }
        }
        [Serializable]class Failure{public bool success=false;public string error;}
        static void InvokeCamera(string name,string method)
        {
            var type=typeof(TrainStationAuthoringBridge).Assembly.GetType("StationFlow.TrainStation.Editor."+name);
            var action=type?.GetMethod(method,System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static,null,Type.EmptyTypes,null);
            if(action==null)throw new InvalidOperationException(name+"."+method+" is not available.");
            action.Invoke(null,null);
        }
    }
}
#endif
