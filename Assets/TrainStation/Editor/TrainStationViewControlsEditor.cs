using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StationFlow.TrainStation.Editor
{
    /// <summary>Runs the same saved presentation controls directly in edit mode, with Undo.</summary>
    [CustomEditor(typeof(TrainStationViewControls))]
    public sealed class TrainStationViewControlsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            TrainStationViewControls controls = (TrainStationViewControls)target;
            EditorGUILayout.LabelField("Points de vue · disponibles dans l’Editor", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Choisissez un point de vue puis ouvrez l’onglet Game. Ces commandes fonctionnent aussi sans lancer Play. La scène, les caméras et l’interface sont des objets enregistrés.", MessageType.Info);

            for (int row = 0; row < (controls.ViewCount + 1) / 2; row++)
            {
                using (new EditorGUILayout.HorizontalScope())
                    for (int column = 0; column < 2; column++)
                    {
                        int index = row * 2 + column;
                        if (index >= controls.ViewCount) break;
                        Color previous = GUI.backgroundColor;
                        if (controls.selectedView == index) GUI.backgroundColor = new Color(.5f, .93f, .8f);
                        using (new EditorGUI.DisabledScope(controls.viewpoints == null || index >= controls.viewpoints.Length || controls.viewpoints[index] == null))
                            if (GUILayout.Button(controls.GetViewName(index), GUILayout.Height(30)))
                                Apply(controls, "TrainStation · changer le point de vue", () => controls.SelectView(index));
                        GUI.backgroundColor = previous;
                    }
            }

            EditorGUILayout.Space(5);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(controls.ViewCount == 0))
                {
                    if (GUILayout.Button("Camera precedente", GUILayout.Height(27)))
                        Apply(controls, "TrainStation · camera precedente", controls.PreviousView);
                    if (GUILayout.Button("Camera suivante", GUILayout.Height(27)))
                        Apply(controls, "TrainStation · camera suivante", controls.NextView);
                    if (GUILayout.Button("Recentrer le hall", GUILayout.Height(27)))
                        Apply(controls, "TrainStation · recentrer le hall", controls.ResetGameView);
                }
            }
            using (new EditorGUI.DisabledScope(controls.cameraCatalog == null))
                if (GUILayout.Button("Afficher / masquer le catalogue des cameras", GUILayout.Height(27)))
                    Apply(controls, "TrainStation · catalogue des cameras", controls.ToggleCameraCatalog);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(controls.roofGroups == null || controls.roofGroups.Length == 0))
                    if (GUILayout.Button("Afficher / masquer les toitures", GUILayout.Height(27)))
                        Apply(controls, "TrainStation · visibilité des toitures", controls.ToggleRoofs);
                using (new EditorGUI.DisabledScope(controls.interfaceRoot == null))
                    if (GUILayout.Button("Afficher / masquer l’interface", GUILayout.Height(27)))
                        Apply(controls, "TrainStation · visibilité de l’interface", controls.ToggleInterface);
            }
            EditorGUILayout.Space(10);
            DrawDefaultInspector();
        }

        static void Apply(TrainStationViewControls controls, string description, System.Action action)
        {
            var objects = new List<Object> { controls };
            if (controls.interfaceRoot != null) objects.Add(controls.interfaceRoot);
            if (controls.viewLabel != null) objects.Add(controls.viewLabel);
            if (controls.controlHintLabel != null) objects.Add(controls.controlHintLabel);
            if (controls.viewDropdown != null) objects.Add(controls.viewDropdown);
            if (controls.cameraCatalog != null) objects.Add(controls.cameraCatalog);
            if (controls.roofButtonLabel != null) objects.Add(controls.roofButtonLabel);
            if (controls.roofGroups != null)
                foreach (GameObject group in controls.roofGroups)
                    if (group != null) objects.Add(group);
            if (controls.viewButtons != null)
                foreach (UnityEngine.UI.Image image in controls.viewButtons)
                    if (image != null) objects.Add(image);
            if (controls.viewpoints != null)
                foreach (Camera camera in controls.viewpoints)
                    if (camera != null)
                    {
                        objects.Add(camera);
                        objects.Add(camera.gameObject);
                        objects.Add(camera.transform);
                        AudioListener listener = camera.GetComponent<AudioListener>();
                        if (listener != null) objects.Add(listener);
                        TrainStationOrbitCamera orbit = camera.GetComponent<TrainStationOrbitCamera>();
                        if (orbit != null) objects.Add(orbit);
                    }

            Undo.RecordObjects(objects.ToArray(), description);
            action();
            foreach (Object obj in objects) EditorUtility.SetDirty(obj);
            if (!Application.isPlaying)
                EditorSceneManager.MarkSceneDirty(controls.gameObject.scene);
            SceneView.RepaintAll();
            EditorApplication.QueuePlayerLoopUpdate();
        }
    }
}
