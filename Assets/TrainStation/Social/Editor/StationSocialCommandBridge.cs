#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace StationFlow.TrainStation.Social.Editor
{
    [InitializeOnLoad]
    public static class StationSocialCommandBridge
    {
        static double nextPoll;
        static StationSocialCommandBridge() { EditorApplication.update += Poll; }
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            nextPoll = EditorApplication.timeSinceStartup + 2;
            const string inbox = "BuildRequests/station-social-command.txt";
            if (!File.Exists(inbox)) return;
            string command = File.ReadAllText(inbox).Trim(); File.Delete(inbox);
            var result = new Result { command = command };
            try
            {
                switch (command)
                {
                    case "refresh": AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport); break;
                    case "calibrate-walk": Invoke("StationLocomotionCalibration", "Calibrate"); break;
                    case "measure-walk": Invoke("StationLocomotionCalibration", "Measure"); break;
                    case "gait-audit": Invoke("StationGaitAudit", "Measure"); break;
                    case "gait-audit-restore": Invoke("StationGaitAudit", "RestoreSceneAfterAudit"); break;
                    case "gait-upgrade": Invoke("StationGaitAuthoring", "Apply"); break;
                    case "gait-play-review": Invoke("StationGaitPlayReview", "BeginPlayCheck"); break;
                    case "navigation-safe": Invoke("StationNavigationSafety", "Apply"); break;
                    case "navigation-validate": Invoke("StationNavigationSafety", "Validate"); break;
                    case "density-300": Invoke("StationDensityAuthoring", "Populate"); break;
                    case "density-validate": Invoke("StationDensityAuthoring", "Validate"); break;
                    case "crowd-stress-check": Invoke("StationCrowdStressVerification", "BeginPlayCheck"); break;
                    case "crowd-upgrade-play-check": Invoke("StationNativeLocomotionVerification", "BeginPlayCheck"); break;
                    case "crowd-upgrade": Invoke("StationCrowdUpgradeAuthoring", "Apply"); break;
                    case "native-navigation": Invoke("StationNativeNavigationAuthoring", "Apply"); break;
                    case "native-navigation-play-check": Invoke("StationNativeLocomotionVerification", "BeginPlayCheck"); break;
                    case "directional-measure": Invoke("StationDirectionalLocomotionAuthoring", "Measure"); break;
                    case "queue-natural": Invoke("StationQueueAuthoring", "Apply"); break;
                    case "queue-validate": Invoke("StationQueueAuthoring", "Validate"); break;
                    case "queue-play-check": Invoke("StationQueueVerification", "BeginPlayCheck"); break;
                    case "crowd": Invoke("StationCrowdAuthoring", "Populate"); break;
                    case "mixed-crowd": Invoke("StationMixedCrowdAuthoring", "Populate"); break;
                    case "mixed-validate": Invoke("StationMixedCrowdAuthoring", "Validate"); break;
                    case "mixed-lighting": Invoke("StationMixedCrowdAuthoring", "RefreshShopLighting"); break;
                    case "mixed-saved-check": Invoke("StationMixedCrowdVerification", "ValidateSavedScene"); break;
                    case "mixed-play-check": Invoke("StationMixedCrowdVerification", "BeginPlayCheck"); break;
                    case "animations": Invoke("StationSocialAnimationAssets", "EnsureResources"); break;
                    case "repair-luggage": Invoke("StationSocialAuthoring", "UpgradePassengerLuggage"); break;
                    case "no-luggage": Invoke("StationNoLuggageAuthoring", "RemoveAll"); break;
                    case "repair-cleaners": Invoke("StationSocialAuthoring", "RefreshCleanerPose"); break;
                    case "build": Invoke("StationSocialAuthoring", "Build"); break;
                    case "bake": Invoke("StationSocialAuthoring", "BakeNavigation"); break;
                    case "validate": Invoke("StationSocialVerification", "ValidateSavedScene"); break;
                    case "play-check": Invoke("StationSocialVerification", "BeginPlayCheck"); break;
                    case "capture": Invoke("StationSocialCapture", "Capture"); break;
                    default: throw new ArgumentException("Unknown station social command: " + command);
                }
                result.success = true;
            }
            catch (Exception error) { result.error = error.ToString(); Debug.LogException(error); }
            File.WriteAllText("BuildRequests/station-social-result.json", JsonUtility.ToJson(result, true));
        }
        static void Invoke(string typeName, string method)
        {
            var type = typeof(StationSocialCommandBridge).Assembly.GetType("StationFlow.TrainStation.Social.Editor." + typeName);
            var action = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (action == null) throw new InvalidOperationException(typeName + "." + method + " is not yet available.");
            action.Invoke(null, null);
        }
        [Serializable] sealed class Result { public bool success; public string command, error; }
    }
}
#endif
