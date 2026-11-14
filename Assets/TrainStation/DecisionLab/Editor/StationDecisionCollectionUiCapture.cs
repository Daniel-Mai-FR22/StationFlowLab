#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
namespace StationFlow.TrainStation.DecisionLab.Editor
{
    public static class StationDecisionCollectionUiCapture
    {
        public static void Capture(string dropdown)
        {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Run Daily before UI capture.");
            var panel = UnityEngine.Object.FindFirstObjectByType<StationDecisionCollectionPanel>();
            if (!panel) throw new InvalidOperationException("Collection panel is missing.");
            panel.SetOptions(new StationDecisionCaptureOptions { selectedCameraIds = new[] { "HALL-01", "HALL-02", "HALL-03", "HALL-04" } });
            if (dropdown == "rates")
            {
                var world = UnityEngine.Object.FindFirstObjectByType<StationDecisionWorld>();
                if (!world || !world.BeginDataCollection(panel.SelectedOptions)) throw new InvalidOperationException("Cannot start explicit FPS UI preview.");
                panel.Hide();
            }
            else panel.Show();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(StationDecisionCollectionPanel).GetField("scroll", flags).SetValue(panel, new Vector2(0, 210));
            typeof(StationDecisionCollectionPanel).GetField("resolutionOpen", flags).SetValue(panel, dropdown == "resolution");
            string folder = "Reports/TrainStationDaily/VideoOptions";
            Directory.CreateDirectory(folder);
            double deadline = EditorApplication.timeSinceStartup + (dropdown == "rates" ? 3 : 1);
            EditorApplication.CallbackFunction callback = null;
            callback = () => {
                if (EditorApplication.timeSinceStartup < deadline) return;
                EditorApplication.update -= callback;
                if (EditorApplication.isPlaying && panel)
                    ScreenCapture.CaptureScreenshot(Path.GetFullPath(folder + "/ui-" + dropdown + ".png"));
            };
            EditorApplication.update += callback;
        }
    }
}
#endif