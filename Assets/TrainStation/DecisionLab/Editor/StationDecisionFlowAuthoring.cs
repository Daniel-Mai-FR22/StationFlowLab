#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Save roadside arrivals and post-train service visits as one Daily configuration.</summary>
    public static class StationDecisionFlowAuthoring
    {
        const string Output = "Reports/TrainStationDaily/Entrances";
        [Serializable] sealed class Receipt
        {
            public string configuredUtc, scene, backupDirectory, error;
            public bool success, protectedFilesPreserved, openSceneWasDirty;
            public int boardingPlans, alightingPlans, plannedServiceVisitors;
            public DecisionEntranceWeight[] entranceWeights;
            public DecisionAlightingStopoverSettings alightingStopovers;
            public List<EntranceCount> assignments = new List<EntranceCount>();
            public List<FileEvidence> protectedFiles = new List<FileEvidence>();
        }
        [Serializable] sealed class EntranceCount { public string entranceId; public int actors, groups; }
        [Serializable] sealed class FileEvidence { public string path, before, after; }

        [MenuItem("Efrei Research Lab/Train Station/Daily/Configure roadside arrivals and alighting services")]
        public static void Apply()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play before changing Daily flows.");
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == StationDecisionDayAuthoring.ScenePath, "Open Daily before changing entrances.");
            var controller = Components<StationDecisionArrivalController>(scene).Single();
            Require(controller.scenario, "Daily arrival scenario is missing.");
            string scenarioPath = AssetDatabase.GetAssetPath(controller.scenario);
            string navPath = AssetDatabase.GetAssetPath(Components<NavMeshSurface>(scene).Single().navMeshData);
            Require(scenarioPath == StationDecisionDayAuthoring.ScenarioPath && navPath == StationDecisionGateAuthoring.NavigationPath,
                "Use only the Daily-owned scenario and navigation.");
            var report = new Receipt { configuredUtc = DateTime.UtcNow.ToString("O"), scene = scene.path,
                openSceneWasDirty = scene.isDirty, backupDirectory = Output + "/Before-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") };
            Directory.CreateDirectory(report.backupDirectory);
            File.Copy(scene.path, report.backupDirectory + "/TrainStationDaily.unity");
            File.Copy(scenarioPath, report.backupDirectory + "/StationDecisionDayScenario.asset");
            File.Copy(navPath, report.backupDirectory + "/Daily_Controlled_Platforms.asset");
            if (scene.isDirty)
                Require(EditorSceneManager.SaveScene(scene, report.backupDirectory + "/TrainStationDaily.open-scene.unity", true),
                    "Could not preserve the open scene's pending changes.");
            foreach (string path in new[] { SourceScene, ScenePath, SchedulePath, StationDecisionDayAuthoring.SchedulePath,
                StationDecisionLabAmenitiesAuthoring.NavigationPath, "ProjectSettings/NavMeshAreas.asset" })
                if (File.Exists(path)) report.protectedFiles.Add(new FileEvidence { path = path, before = Hash(path) });
            try
            {
                StationDecisionDayEntranceAuthoring.Apply();
                if (controller.scenario.alightingStopovers == null)
                    controller.scenario.alightingStopovers = new DecisionAlightingStopoverSettings();
                controller.scenario.alightingStopovers.Validate();
                EditorUtility.SetDirty(controller.scenario);
                AssetDatabase.SaveAssetIfDirty(controller.scenario);
                StationDecisionHallFountainAuthoring.Apply();
                StationDecisionDayEntranceAuthoring.Validate();
                var settings = controller.scenario.PlannerSettings();
                settings.scenarioDate = controller.world.rail.schedule.operatingDateIso;
                settings.entranceWeights = controller.entranceWeights;
                var services = controller.world.rail.schedule.services.Select(s => new DecisionArrivalService
                { serviceId = s.serviceId, trainNumber = s.trainNumber, trackNumber = s.trackNumber,
                    arrivalSeconds = s.arrivalOffsetSeconds, departureSeconds = s.departureOffsetSeconds,
                    doorSeconds = s.doorDurationSeconds, expectedBoarding = s.expectedBoarding, expectedAlighting = s.expectedAlighting,
                    coachSeatCapacities = s.coachSeatCapacities, throughPassengers = s.throughPassengers }).ToArray();
                var plan = DecisionArrivalPlanner.Generate(services, controller.portals.Where(p => p.trackNumber == 0).Select(p => p.entranceId).ToArray(), settings);
                report.boardingPlans = plan.Count(p => p.purpose == DecisionJourneyPurpose.Boarding);
                report.alightingPlans = plan.Count(p => p.purpose == DecisionJourneyPurpose.Alighting);
                report.plannedServiceVisitors = plan.Count(p => p.alightingServiceVisitor);
                report.entranceWeights = controller.entranceWeights;
                report.alightingStopovers = controller.scenario.alightingStopovers;
                foreach (var group in plan.Where(p => p.purpose == DecisionJourneyPurpose.Boarding).GroupBy(p => p.entranceId))
                    report.assignments.Add(new EntranceCount { entranceId = group.Key, actors = group.Count(),
                        groups = group.Where(p => !string.IsNullOrEmpty(p.groupId)).Select(p => p.groupId).Distinct().Count() });
                Require(report.assignments.Count == 4 && report.assignments.All(a => a.entranceId != "entry-main" && a.actors > 0),
                    "All four roadside arrivals must receive boarding demand; the central compatibility portal must stay unassigned.");
                Require(report.plannedServiceVisitors > 0 && report.plannedServiceVisitors < report.alightingPlans,
                    "Alighters must include both station visitors and direct exits.");
                foreach (var item in report.protectedFiles)
                { item.after = Hash(item.path); Require(item.before == item.after, "Flow configuration changed a protected resource: " + item.path); }
                report.protectedFilesPreserved = report.success = true;
                StationDecisionDayEntranceAuthoring.Capture();
            }
            catch (Exception error)
            {
                report.success = false;
                report.error = error.ToString();
                File.Copy(report.backupDirectory + "/StationDecisionDayScenario.asset", scenarioPath, true);
                File.Copy(report.backupDirectory + "/Daily_Controlled_Platforms.asset", navPath, true);
                File.Copy(report.backupDirectory + (report.openSceneWasDirty ? "/TrainStationDaily.open-scene.unity" : "/TrainStationDaily.unity"), StationDecisionDayAuthoring.ScenePath, true);
                AssetDatabase.ImportAsset(scenarioPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(navPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(StationDecisionDayAuthoring.ScenePath, ImportAssetOptions.ForceUpdate);
                EditorSceneManager.OpenScene(StationDecisionDayAuthoring.ScenePath, OpenSceneMode.Single);
                throw;
            }
            finally { File.WriteAllText(Output + "/flow-authoring.json", JsonUtility.ToJson(report, true)); }
        }
    }
}
#endif
