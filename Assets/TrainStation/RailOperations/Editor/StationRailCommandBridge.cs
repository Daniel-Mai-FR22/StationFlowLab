#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.RailOperations.Editor
{
    [InitializeOnLoad]
    public static class StationRailCommandBridge
    {
        const string Inbox = "BuildRequests/station-rail-command.txt";
        static double nextPoll;
        static StationRailCommandBridge() { EditorApplication.update += Poll; }
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            nextPoll = EditorApplication.timeSinceStartup + 1;
            if (!File.Exists(Inbox)) return;
            string command = File.ReadAllText(Inbox).Trim();
            if (command != "status" && EditorApplication.isPlayingOrWillChangePlaymode) return;
            File.Delete(Inbox);
            var result = new Result { command = command, playing = EditorApplication.isPlaying, scene = SceneManager.GetActiveScene().path,
                textSubmeshes = SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<TMPro.TMP_SubMeshUI>(true))
                    .Select(m => m.transform.parent.name + ": " + m.transform.parent.GetComponent<TMPro.TMP_Text>().text).ToArray() };
            try
            {
                switch (command)
                {
                    case "status": break;
                    case "refresh": AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport); break;
                    case "build": Invoke("StationRailAuthoring", "Populate"); break;
                    case "repair-doors": Invoke("StationRailAuthoring", "RepairDoorLayers"); break;
                    case "presentation": StationRailPresentation.RefreshSavedPresentation(); break;
                    case "validate": Invoke("StationRailVerification", "ValidateSavedScene"); break;
                    case "play-check": Invoke("StationRailVerification", "BeginPlayCheck"); break;
                    case "capture": Invoke("StationRailCapture", "Capture"); break;
                    default: throw new ArgumentException("Unknown rail command: " + command);
                }
                result.success = true;
            }
            catch (Exception error) { result.error = error.ToString(); Debug.LogException(error); }
            File.WriteAllText("BuildRequests/station-rail-result.json", JsonUtility.ToJson(result, true));
        }
        static void Invoke(string type, string method)
        {
            var target = typeof(StationRailCommandBridge).Assembly.GetType("StationFlow.TrainStation.RailOperations.Editor." + type);
            var action = target?.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (action == null) throw new InvalidOperationException(type + "." + method + " is not ready.");
            action.Invoke(null, null);
        }
        [Serializable] sealed class Result { public bool success, playing; public string command, scene, error; public string[] textSubmeshes; }
    }
}
#endif
