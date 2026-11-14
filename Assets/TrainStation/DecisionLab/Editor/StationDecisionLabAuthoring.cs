#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using StationFlow.TrainStation.RailOperations;
using StationFlow.TrainStation.Social;
using TMPro;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Creates one isolated experiment. Shop geometry changes are scene overrides and navigation is copied into Lab-owned assets.</summary>
    public static class StationDecisionLabAuthoring
    {
        public const string SourceScene = "Assets/TrainStation/Scenes/TrainStation.unity";
        public const string ScenePath = "Assets/TrainStation/Scenes/TrainStationDecisionLab.unity";
        public const string SourceSchedule = "Assets/TrainStation/RailOperations/Resources/TrainStationRail/StationRailSchedule.asset";
        public const string SchedulePath = "Assets/TrainStation/DecisionLab/Resources/StationDecisionLabSchedule.asset";
        public const string ReportDirectory = "Reports/TrainStationDecisionLab";
        public const string ManifestPath = ReportDirectory + "/authoring.json";
        public const int ParticipantCount = 24;
        public const int BackgroundCount = 60;

        [Serializable] public sealed class Manifest
        {
            public bool success, originalScenePreserved, originalSchedulePreserved;
            public string createdUtc, scene, sourceSceneSha256, sourceScheduleSha256, error;
            public int participants, activeBackground, activeStaff, poiCount;
            public string timeContract = "One scaled Unity second equals one station second; global timeScale accelerates the entire experiment.";
            public string boardingContract = "Virtual boarding at a safe platform anchor while the assigned train doors are open; no physical train entry.";
            public string toiletContract = "Timed visit at the accessible toilet entrance; no cubicle interaction.";
            public string restContract = "Reserved seating with the station's existing sit/idle/stand motions; no dedicated drinking or reading hand gestures.";
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Create isolated experiment")]
        public static void Create()
        {
            RequireEditAndClean();
            if (File.Exists(ScenePath))
            {
                Open();
                StationDecisionLabVerification.ValidateSavedScene();
                return;
            }
            var manifest = new Manifest { createdUtc = DateTime.UtcNow.ToString("O"), scene = ScenePath,
                sourceSceneSha256 = Hash(SourceScene), sourceScheduleSha256 = Hash(SourceSchedule) };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SchedulePath));
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                if (File.Exists(SchedulePath)) throw new InvalidOperationException("An experiment schedule already exists without its scene; preserve it and resolve the incomplete creation first.");
                Require(AssetDatabase.CopyAsset(SourceScene, ScenePath), "Could not copy the original scene.");
                Require(AssetDatabase.CopyAsset(SourceSchedule, SchedulePath), "Could not duplicate the rail schedule.");
                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                var rail = Components<StationRailOperations>(scene).Single();
                var social = Components<StationSocialWorld>(scene).Single();
                rail.schedule = AssetDatabase.LoadAssetAtPath<StationRailSchedule>(SchedulePath);
                rail.simulationRate = 1f;
                rail.playOnStart = true;
                rail.schedule.cycleDurationSeconds = 3600f;
                var departures = new[] { 180f, 240f, 330f, 420f };
                Require(rail.schedule.services.Length == departures.Length, "Expected the four saved station services.");
                for (int i = 0; i < departures.Length; ++i)
                {
                    rail.schedule.services[i].arrivalOffsetSeconds = -60f + i * 10f;
                    rail.schedule.services[i].departureOffsetSeconds = departures[i];
                    rail.schedule.services[i].doorDurationSeconds = 8f;
                }
                EditorUtility.SetDirty(rail.schedule);
                var root = new GameObject("13 Decision Lab — conditional passenger intentions");
                SceneManager.MoveGameObjectToScene(root, scene);
                var world = root.AddComponent<StationDecisionWorld>();
                world.rail = rail;
                world.scenarioName = "Conditional activity experiment — 24 travellers";
                world.seed = 20261005;
                world.loggingEnabled = true;
                world.trajectorySampleRate = 5f;
                world.events = new List<StationDecisionScenarioEvent> {
                    new StationDecisionScenarioEvent { atSeconds = 55f, serviceId = rail.schedule.services[0].serviceId,
                        kind = DecisionScenarioEventKind.Delay, delaySeconds = 75f, announceImmediately = true } };
                var selected = social.passengers.Where(p => p && p.activity == StationPassengerActivity.SoloWalking)
                    .OrderBy(p => Hierarchy(p.transform), StringComparer.Ordinal).Take(ParticipantCount).ToArray();
                Require(selected.Length == ParticipantCount, "Expected 24 saved solo travellers to opt into the experiment.");
                ConfigureBackground(social, selected);
                world.pois = CreatePois(root.transform, social, rail);
                var participants = new List<StationDecisionPassenger>();
                for (int i = 0; i < selected.Length; ++i)
                {
                    var original = selected[i];
                    original.gameObject.SetActive(true);
                    original.automaticRequests = false;
                    original.externalActivityControl = true;
                    original.transform.SetPositionAndRotation(Snap(new Vector3(-8f + (i % 6) * 3.2f, 0f, -25f + (i / 6) * 2.2f)), Quaternion.identity);
                    var traveller = original.gameObject.AddComponent<StationDecisionPassenger>();
                    traveller.world = world;
                    traveller.motion = original.motion;
                    traveller.passenger = original;
                    traveller.passengerId = "lab-" + (i + 1).ToString("000");
                    var service = rail.schedule.services[i % departures.Length];
                    traveller.serviceId = service.serviceId;
                    traveller.departureOffsetSeconds = service.departureOffsetSeconds;
                    traveller.startDelaySeconds = i * .8f;
                    traveller.safetyMarginSeconds = 12f + i % 4 * 3f;
                    traveller.familiarity = .25f + (i % 4) * .2f;
                    traveller.walkSpeed = 1.05f + (i % 3) * .1f;
                    traveller.fastWalkSpeed = 1.55f + (i % 3) * .05f;
                    traveller.platformKnownInitially = false;
                    traveller.needs = new DecisionNeeds {
                        thirst = i < 4 ? .9f : .1f,
                        toilet = i >= 4 && i < 8 ? .9f : .1f,
                        fatigue = i >= 8 && i < 12 ? .9f : .1f,
                        shopping = i >= 12 && i < 16 ? .9f : .1f,
                        information = i % 6 == 0 ? .7f : .15f };
                    var labelObject = new GameObject("Intent — " + traveller.passengerId);
                    labelObject.transform.SetParent(original.transform, false);
                    labelObject.transform.localPosition = new Vector3(0, 2.1f, 0);
                    var label = labelObject.AddComponent<TextMeshPro>();
                    label.font = TMP_Settings.defaultFontAsset;
                    label.text = traveller.passengerId + "\nDisplay";
                    label.fontSize = 1.6f;
                    label.alignment = TextAlignmentOptions.Center;
                    label.rectTransform.sizeDelta = new Vector2(2.4f, .65f);
                    label.textWrappingMode = TextWrappingModes.NoWrap;
                    label.color = i < 16 ? new Color(1f, .8f, .3f) : new Color(.35f, .95f, 1f);
                    label.raycastTarget = false;
                    traveller.intentLabel = label;
                    original.name = "Decision traveller " + (i + 1).ToString("00") + " — " + service.destination;
                    Record(original); Record(traveller); Record(original.transform);
                    participants.Add(traveller);
                }
                world.participants = participants.ToArray();
                root.AddComponent<StationDecisionDashboard>().world = world;
                foreach (var dashboard in Components<StationSocialDashboard>(scene)) dashboard.gameObject.SetActive(false);
                foreach (var item in Components<Transform>(scene).Where(t => t.name == "02 · Catalogue des espaces"))
                    item.gameObject.SetActive(false);
                foreach (var button in Components<Button>(scene))
                {
                    bool railPause = false, railReset = false;
                    for (int i = 0; i < button.onClick.GetPersistentEventCount(); ++i)
                    {
                        if (!(button.onClick.GetPersistentTarget(i) is StationTimetableDisplay)) continue;
                        railPause |= button.onClick.GetPersistentMethodName(i) == "TogglePause";
                        railReset |= button.onClick.GetPersistentMethodName(i) == "ResetSimulation";
                    }
                    if (!railPause && !railReset) continue;
                    for (int i = button.onClick.GetPersistentEventCount() - 1; i >= 0; --i) UnityEventTools.RemovePersistentListener(button.onClick, i);
                    if (railPause) UnityEventTools.AddPersistentListener(button.onClick, world.TogglePause);
                    else UnityEventTools.AddPersistentListener(button.onClick, world.RestartScene);
                    Record(button);
                }
                rail.EvaluatePreview();
                foreach (var display in Components<StationTimetableDisplay>(scene)) { display.RefreshDisplay(); Record(display); }
                Record(rail); Record(world); Record(social);
                EditorSceneManager.MarkSceneDirty(scene);
                AssetDatabase.SaveAssets();
                Require(EditorSceneManager.SaveScene(scene, ScenePath), "Could not save the experiment scene.");
                EnsureSceneRegistered();
                manifest.participants = participants.Count;
                manifest.activeBackground = social.passengers.Count(p => p && p.gameObject.activeInHierarchy && !selected.Contains(p));
                manifest.activeStaff = Components<StationActorMotion>(scene).Count(m => m.gameObject.activeInHierarchy && !m.GetComponent<StationPassenger>());
                manifest.poiCount = world.pois.Length;
                manifest.originalScenePreserved = Hash(SourceScene) == manifest.sourceSceneSha256;
                manifest.originalSchedulePreserved = Hash(SourceSchedule) == manifest.sourceScheduleSha256;
                Require(manifest.originalScenePreserved && manifest.originalSchedulePreserved, "Original scene or schedule changed during isolated authoring.");
                manifest.success = true;
                Write(ManifestPath, manifest);
                StationDecisionLabPresentation.UpgradeOpenScene();
                StationDecisionLabGaitRepair.ApplyOpenLabScene();
                StationDecisionLabAmenitiesAuthoring.UpgradeOpenScene();
                StationDecisionLabVerification.ValidateSavedScene();
            }
            catch (Exception error)
            {
                manifest.success = false;
                manifest.error = error.ToString();
                manifest.originalScenePreserved = Hash(SourceScene) == manifest.sourceSceneSha256;
                manifest.originalSchedulePreserved = Hash(SourceSchedule) == manifest.sourceScheduleSha256;
                Write(ManifestPath, manifest);
                throw;
            }
        }

        static void ConfigureBackground(StationSocialWorld social, StationPassenger[] selected)
        {
            var active = new HashSet<StationPassenger>(selected);
            foreach (var p in social.passengers.Where(p => p && p.activity == StationPassengerActivity.Seated).Take(18)) active.Add(p);
            foreach (var group in social.conversationGroups.Take(8))
                foreach (var member in group.members) if (member && member.passenger) active.Add(member.passenger);
            foreach (var group in social.walkingGroups.Take(6))
                foreach (var member in group.members) if (member) active.Add(member);
            foreach (var passenger in social.passengers)
            {
                if (!passenger) continue;
                passenger.automaticRequests = false;
                passenger.gameObject.SetActive(active.Contains(passenger));
                Record(passenger); Record(passenger.gameObject);
            }
            Require(active.Count == ParticipantCount + BackgroundCount, "The background selection must retain complete groups and exactly 60 actors.");
            foreach (var group in social.conversationGroups) group.enabled = group.members.Any(m => m && m.gameObject.activeInHierarchy);
            foreach (var group in social.walkingGroups) group.enabled = group.members.Any(m => m && m.gameObject.activeInHierarchy);
        }

        static StationDecisionPoi[] CreatePois(Transform parent, StationSocialWorld social, StationRailOperations rail)
        {
            var pois = new List<StationDecisionPoi>();
            pois.Add(Poi(parent, "display-hall", DecisionActivity.Display, new Vector3(0,0,7), 24, 3f, 1f, 3.8f));
            foreach (var desk in new[] { social.informationDesk }.Concat(social.shopDesks))
            {
                var kind = desk == social.informationDesk ? DecisionActivity.Info :
                    desk.serviceName.IndexOf("Cafe", StringComparison.OrdinalIgnoreCase) >= 0 ? DecisionActivity.Cafe : DecisionActivity.Shop;
                // Billetterie remains a working background resource, without adding a ticket-purchase decision in this first experiment.
                if (desk.serviceName.IndexOf("Billetterie", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string id = kind == DecisionActivity.Info ? "info-accueil" : kind == DecisionActivity.Cafe ? "cafe" : "shop-relay";
                var poi = Poi(parent, id, kind, desk.queueSlots[0].position, desk.queueSlots.Length, kind == DecisionActivity.Cafe ? 14f : 9f, 3f, 0f);
                poi.service = desk;
                pois.Add(poi);
            }
            pois.Add(Poi(parent, "toilet-entrance", DecisionActivity.Toilet, new Vector3(20.3f,0,-18), 2, 180f, 60f, .6f));
            pois.Add(Poi(parent, "rest-lounge-entrance", DecisionActivity.Rest, new Vector3(20.3f,0,-6), 4, 20f, 6f, 1.3f));
            foreach (var service in rail.schedule.services)
            {
                float x = -36f + ((service.trackNumber - 1) / 2) * 24f;
                var poi = Poi(parent, "platform-track-" + service.trackNumber, DecisionActivity.Platform,
                    new Vector3(x,0,82), 16, 0f, 0f, 1.5f);
                poi.trackNumber = service.trackNumber;
                pois.Add(poi);
            }
            // This is an exit boundary in the walkable hall, keeping the fixed existing NavMesh intact.
            pois.Add(Poi(parent, "exit-hall", DecisionActivity.Exit, new Vector3(0,0,-25.5f), 24, 0f, 0f, 2f));
            return pois.ToArray();
        }

        static StationDecisionPoi Poi(Transform parent, string id, DecisionActivity activity, Vector3 position,
            int capacity, float duration, float spread, float radius)
        {
            var item = new GameObject("POI — " + id);
            item.transform.SetParent(parent, false);
            item.transform.position = Snap(position);
            var poi = item.AddComponent<StationDecisionPoi>();
            poi.poiId = id; poi.activity = activity; poi.interactionPoint = item.transform;
            poi.capacity = capacity; poi.meanDurationSeconds = duration; poi.durationSpreadSeconds = spread; poi.spreadRadius = radius;
            return poi;
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Open experiment")]
        public static void Open()
        {
            RequireEditAndClean();
            Require(File.Exists(ScenePath), "Create the isolated experiment first.");
            if (SceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EnsureSceneRegistered();
            StationDecisionLabPresentation.UpgradeOpenScene();
            StationDecisionLabGaitRepair.ApplyOpenLabScene();
        }

        /// <summary>Retain the user's scene ordering, and persist this scene for restart after reopening Unity.</summary>
        public static void EnsureSceneRegistered()
        {
            RequireEditAndClean();
            Require(File.Exists(ScenePath), "Create the isolated experiment before registering its restart scene.");
            // Unity 6 exposes a profile-dependent scenes property. Write the explicit global list so
            // ProjectSettings/EditorBuildSettings.asset remains sufficient after an Editor restart.
            EditorBuildSettings.globalScenes = WithLabEnabled(EditorBuildSettings.globalScenes);
            var profile = BuildProfile.GetActiveBuildProfile();
            if (profile && profile.overrideGlobalScenes)
            {
                profile.scenes = WithLabEnabled(profile.scenes);
                EditorUtility.SetDirty(profile);
            }
            AssetDatabase.SaveAssets();
            string guid = AssetDatabase.AssetPathToGUID(ScenePath);
            string persisted = File.ReadAllText("ProjectSettings/EditorBuildSettings.asset");
            string entry = @"(?m)^  - enabled: 1\r?\n    path: " + Regex.Escape(ScenePath) +
                @"\r?\n    guid: " + Regex.Escape(guid) + @"\r?$";
            Require(!string.IsNullOrEmpty(guid) && Regex.IsMatch(persisted, entry),
                "Decision Lab is registered in memory but its enabled scene path and GUID were not persisted in ProjectSettings/EditorBuildSettings.asset.");
        }
        static EditorBuildSettingsScene[] WithLabEnabled(EditorBuildSettingsScene[] scenes)
        {
            var result = (scenes ?? Array.Empty<EditorBuildSettingsScene>()).ToList();
            var existing = result.FirstOrDefault(s => s.path == ScenePath);
            if (existing == null) result.Add(new EditorBuildSettingsScene(ScenePath, true));
            else existing.enabled = true;
            return result.ToArray();
        }

        public static Vector3 Snap(Vector3 position)
        {
            Require(NavMesh.SamplePosition(position, out var hit, 2.5f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y) < .15f,
                "No safe saved NavMesh near " + position);
            return hit.position;
        }
        public static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>(true)).ToArray();
        public static string Hash(string path)
        {
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static void Write(string path, object value)
        { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, JsonUtility.ToJson(value, true)); }
        public static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        public static void RequireEditAndClean()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Finish Play mode before authoring the experiment.");
            for (int i = 0; i < SceneManager.sceneCount; ++i)
                Require(!SceneManager.GetSceneAt(i).isDirty, "Save or discard the currently edited scene before switching experiments.");
        }
        static string Hierarchy(Transform item) => item.parent ? Hierarchy(item.parent) + "/" + item.name : item.name;
        static void Record(UnityEngine.Object item)
        { EditorUtility.SetDirty(item); if (PrefabUtility.IsPartOfPrefabInstance(item)) PrefabUtility.RecordPrefabInstancePropertyModifications(item); }
    }
}
#endif
