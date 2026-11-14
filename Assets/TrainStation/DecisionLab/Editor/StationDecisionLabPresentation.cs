#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.TrainStation.Social;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Upgrades only the isolated experiment's saved presentation.</summary>
    public static class StationDecisionLabPresentation
    {
        public static readonly string[] LegacyPanelNames = {
            "01 · En-tête", "02 · Catalogue des espaces", "03 · Point de vue actuel",
            "04 · Navigation entre points de vue", "Circulations — horaires et commandes éditables" };

        [Serializable] public sealed class Report
        {
            public string checkedUtc, scene, originalSceneHash, originalScheduleHash;
            public bool success, originalScenePreserved, originalSchedulePreserved, catalogRetained, bindingsValid;
            public int hiddenLegacyPanels, savedCameras, followComponents;
            public List<string> hiddenNames = new List<string>(), errors = new List<string>();
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Upgrade experiment presentation")]
        public static void UpgradePresentation() => Open();

        internal static void UpgradeOpenScene()
        {
            RequireEditAndClean();
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == ScenePath, "Presentation upgrades are restricted to the isolated Decision Lab scene.");
            var report = new Report { checkedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path,
                originalSceneHash = Hash(SourceScene), originalScheduleHash = Hash(SourceSchedule) };
            try
            {
                var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestPath));
                Require(report.originalSceneHash == manifest.sourceSceneSha256 && report.originalScheduleHash == manifest.sourceScheduleSha256,
                    "The original station scene or schedule differs from the preserved creation baseline.");
                var world = Components<StationDecisionWorld>(scene).Single();
                var controls = Components<TrainStationViewControls>(scene).Single();
                var dashboard = Components<StationDecisionDashboard>(scene).Single();
                Require(controls.ViewCount > 1 && controls.viewpoints[0] && controls.cameraCatalog && controls.interfaceRoot,
                    "The saved Hall camera and observation camera catalog are required.");
                var hall = controls.viewpoints[0];
                var orbit = hall.GetComponent<TrainStationOrbitCamera>();
                Require(orbit, "The Hall camera must retain its saved overview orbit component.");
                var existing = Components<StationDecisionFollowCamera>(scene);
                Require(existing.Length <= 1, "Duplicate follow controllers require manual review before saving.");
                bool firstUpgrade = existing.Length == 0;
                var follow = firstUpgrade ? world.gameObject.AddComponent<StationDecisionFollowCamera>() : existing[0];
                follow.world = world; follow.viewControls = controls; follow.controlledCamera = hall;
                follow.overviewOrbit = orbit; follow.dashboard = dashboard;
                dashboard.world = world; dashboard.viewControls = controls; dashboard.followCamera = follow;
                if (firstUpgrade)
                { dashboard.visible = true; dashboard.rosterExpanded = true; dashboard.interfaceScale = 1f; }
                foreach (var item in Components<Transform>(scene).Where(t => LegacyPanelNames.Contains(t.name)))
                {
                    Require(!controls.cameraCatalog.transform.IsChildOf(item), "A legacy panel contains the camera catalog; keep it accessible.");
                    item.gameObject.SetActive(false); Record(item.gameObject);
                    report.hiddenNames.Add(item.name);
                }
                foreach (var social in Components<StationSocialDashboard>(scene))
                { social.gameObject.SetActive(false); Record(social.gameObject); }
                if (firstUpgrade)
                { controls.SelectView(0); controls.cameraCatalog.SetActive(false); follow.enabled = true; orbit.enabled = true; }
                Record(follow); Record(dashboard); Record(controls); Record(orbit); Record(controls.cameraCatalog);
                foreach (var camera in controls.viewpoints.Where(c => c))
                { Record(camera); Record(camera.gameObject); var listener = camera.GetComponent<AudioListener>(); if (listener) Record(listener); }
                report.hiddenLegacyPanels = report.hiddenNames.Count;
                report.savedCameras = controls.ViewCount;
                report.followComponents = Components<StationDecisionFollowCamera>(scene).Length;
                report.catalogRetained = controls.cameraCatalog && controls.cameraCatalog.transform.IsChildOf(controls.interfaceRoot.transform);
                report.bindingsValid = follow.world == world && follow.dashboard == dashboard && dashboard.followCamera == follow &&
                    follow.controlledCamera == hall && follow.overviewOrbit == orbit && dashboard.viewControls == controls;
                Require(report.hiddenLegacyPanels == LegacyPanelNames.Length, "One or more expected legacy UI panels were not found.");
                Require(report.catalogRetained && report.bindingsValid && report.followComponents == 1, "Saved presentation bindings are incomplete.");
                EditorSceneManager.MarkSceneDirty(scene);
                Require(EditorSceneManager.SaveScene(scene, ScenePath), "Could not save the isolated experiment presentation.");
                AssetDatabase.SaveAssets(); EnsureSceneRegistered();
                report.originalScenePreserved = Hash(SourceScene) == report.originalSceneHash;
                report.originalSchedulePreserved = Hash(SourceSchedule) == report.originalScheduleHash;
                Require(report.originalScenePreserved && report.originalSchedulePreserved, "A preserved source asset changed during presentation authoring.");
                report.success = true;
            }
            catch (Exception error) { report.errors.Add(error.ToString()); throw; }
            finally { Write(ReportDirectory + "/presentation-authoring.json", report); }
        }

        static void Record(UnityEngine.Object item)
        {
            EditorUtility.SetDirty(item);
            if (PrefabUtility.IsPartOfPrefabInstance(item)) PrefabUtility.RecordPrefabInstancePropertyModifications(item);
        }
    }
}
#endif
