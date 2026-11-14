#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StationFlow.TrainStation.RailOperations.Editor
{
    [CustomEditor(typeof(StationRailOperations))]
    public sealed class StationRailOperationsInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var operations = (StationRailOperations)target;
            if (Application.isPlaying && operations.schedule && operations.schedule.finiteOperatingDay)
            {
                using (new EditorGUI.DisabledScope(true)) DrawDefaultInspector();
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Station clock", StationTimetableDisplay.FormatTime(operations.ClockSeconds,true));
                EditorGUILayout.HelpBox("This dated scene shares its clock with passenger journeys. Use the Dashboard Pause / Recommencer controls so trains, passengers and animation remain synchronized. Stop Play before editing the saved timetable.",MessageType.Info);
                if (!string.IsNullOrEmpty(operations.ConfigurationIssue)) EditorGUILayout.HelpBox(operations.ConfigurationIssue,MessageType.Error);
                Repaint();
                return;
            }
            DrawDefaultInspector();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Station clock", StationTimetableDisplay.FormatTime(operations.ClockSeconds,true));
            if (!string.IsNullOrEmpty(operations.ConfigurationIssue)) EditorGUILayout.HelpBox(operations.ConfigurationIssue,MessageType.Error);
            if (Application.isPlaying)
            {
                if (GUILayout.Button(operations.IsPaused ? "Resume rail simulation" : "Pause rail simulation")) operations.TogglePause();
                if (GUILayout.Button("Restart rail timetable")) operations.ResetSimulation();
                Repaint();
            }
            else if (GUILayout.Button("Preview initial timetable and saved train poses"))
            {
                Undo.RegisterFullObjectHierarchyUndo(operations.gameObject,"Preview initial rail timetable");
                var displays = operations.gameObject.scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<StationTimetableDisplay>(true)).ToArray();
                foreach (var display in displays)
                {
                    Undo.RegisterFullObjectHierarchyUndo(display.gameObject,"Preview station timetable");
                    if (display.hourHand) Undo.RecordObject(display.hourHand,"Preview station clock");
                    if (display.minuteHand) Undo.RecordObject(display.minuteHand,"Preview station clock");
                }
                operations.EvaluatePreview(0);
                foreach (var display in displays) display.RefreshDisplay();
                EditorSceneManager.MarkSceneDirty(operations.gameObject.scene);
            }
        }
    }
}
#endif
