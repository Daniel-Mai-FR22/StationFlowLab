#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Restore native navigation on existing saved actors without changing their population or nominal speeds.</summary>
    public static class StationNativeNavigationAuthoring
    {
        public const string ReportPath = "Reports/TrainStationSocial/AgentOnly/authoring.json";
        const string RetiredCoordinatorPrefab = "Assets/TrainStation/Social/Prefabs/Station_CrowdCoordinator.prefab";
        [Serializable] sealed class RoleSpeeds
        {
            public string role;
            public int actors;
            public float[] before, after;
        }
        [Serializable] sealed class Report
        {
            public string completedUtc, scene, backup, error, archivedCoordinatorPrefab;
            public bool success, sceneSavedClean, transformsPreserved, populationPreserved,
                nominalSpeedsPreserved, agentShapeAndPriorityPreserved, highQualityAvoidancePreserved,
                prefabTransformsPreserved = true, prefabAgentSettingsPreserved = true;
            public int passengersBefore, passengers, actors, objectsBefore, objectsAfter, coordinators, updatedPrefabs, remainingCoordinators;
            public List<RoleSpeeds> roleSpeeds = new List<RoleSpeeds>();
        }
        sealed class Poses
        {
            readonly Transform[] transforms, parents;
            readonly Vector3[] positions, scales;
            readonly Quaternion[] rotations;
            public Poses(Transform[] values)
            {
                transforms = values; parents = values.Select(t => t.parent).ToArray();
                positions = values.Select(t => t.localPosition).ToArray();
                rotations = values.Select(t => t.localRotation).ToArray();
                scales = values.Select(t => t.localScale).ToArray();
            }
            public bool Preserved => transforms.Select((t, i) => t && t.parent == parents[i] &&
                t.localPosition == positions[i] && t.localRotation == rotations[i] && t.localScale == scales[i]).All(v => v);
        }
        sealed class AgentState
        {
            public NavMeshAgent agent;
            public string role;
            public float speed, radius, height;
            public int priority;
            public ObstacleAvoidanceType avoidance;
            public AgentState(StationActorMotion motion)
            {
                agent = motion.agent;
                if (!agent) throw new InvalidOperationException("Missing NavMeshAgent on " + motion.name);
                var passenger = motion.GetComponent<StationPassenger>();
                role = passenger ? passenger.activity.ToString() : motion.GetComponent<StationCleaner>() ? "Cleaner" :
                    (PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(motion.gameObject) ?? "").IndexOf("ShopClerk", StringComparison.OrdinalIgnoreCase) >= 0 ? "ShopClerk" : "Receptionist";
                speed = agent.speed; radius = agent.radius; height = agent.height;
                priority = agent.avoidancePriority; avoidance = agent.obstacleAvoidanceType;
            }
            public bool SpeedPreserved => agent && agent.speed == speed;
            public bool ShapeAndPriorityPreserved => agent && agent.radius == radius && agent.height == height && agent.avoidancePriority == priority;
            public bool AvoidancePreserved => agent && agent.obstacleAvoidanceType == avoidance && avoidance == ObstacleAvoidanceType.HighQualityObstacleAvoidance;
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Restore native navigation and walking")]
        public static void Apply()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode())
                throw new InvalidOperationException("Stop Play/animation preview before saving native navigation.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save existing scene edits first.");
            var scene = SceneManager.GetSceneByPath(StationSocialAuthoring.ScenePath);
            if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Open the existing TrainStation scene first.");
            var report = new Report { scene = scene.path };
            try
            {
                var world = Components<StationSocialWorld>(scene).Single();
                var passengers = Components<StationPassenger>(scene);
                var registeredPassengers = world.passengers.ToArray();
                var motions = Components<StationActorMotion>(scene);
                var agents = motions.Select(m => new AgentState(m)).ToArray();
                var poses = new Poses(Components<Transform>(scene));
                report.objectsBefore = Components<Transform>(scene).Length;
                report.passengersBefore = passengers.Length; report.actors = motions.Length;
                Require(passengers.Length == 300 && registeredPassengers.Length == 300, "Expected the existing 300 saved passengers.");
                Require(agents.All(a => a.AvoidancePreserved), "Expected saved High Quality native avoidance on all actors.");
                var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { StationSocialAuthoring.AssetRoot + "/Prefabs" })
                    .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p).ToArray();
                report.backup = "BuildRequests/Backups/StationAgentOnly-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                Directory.CreateDirectory(report.backup);
                Backup(scene.path, report.backup);
                foreach (string path in prefabs) Backup(path, report.backup);

                foreach (var motion in motions) ConfigureMotion(motion);
                foreach (var coordinator in Components<StationCrowdCoordinator>(scene))
                { ConfigureCoordinator(coordinator); report.coordinators++; }
                foreach (string path in prefabs)
                {
                    if (path == RetiredCoordinatorPrefab) continue;
                    var root = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        var prefabMotions = root.GetComponentsInChildren<StationActorMotion>(true);
                        var coordinators = root.GetComponentsInChildren<StationCrowdCoordinator>(true);
                        if (prefabMotions.Length == 0 && coordinators.Length == 0) continue;
                        var prefabPoses = new Poses(root.GetComponentsInChildren<Transform>(true));
                        int objectCount = root.GetComponentsInChildren<Transform>(true).Length;
                        var prefabAgents = prefabMotions.Select(m => new AgentState(m)).ToArray();
                        foreach (var motion in prefabMotions) ConfigureMotion(motion);
                        foreach (var coordinator in coordinators) ConfigureCoordinator(coordinator);
                        report.prefabTransformsPreserved &= prefabPoses.Preserved && root.GetComponentsInChildren<Transform>(true).Length == objectCount;
                        report.prefabAgentSettingsPreserved &= prefabAgents.All(a => a.SpeedPreserved && a.ShapeAndPriorityPreserved && a.AvoidancePreserved);
                        Require(report.prefabTransformsPreserved && report.prefabAgentSettingsPreserved, "Prefab preservation check failed: " + path);
                        Require(PrefabUtility.SaveAsPrefabAsset(root, path), "Could not save prefab: " + path);
                        report.updatedPrefabs++;
                    }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                }
                report.objectsAfter = Components<Transform>(scene).Length;
                report.passengers = Components<StationPassenger>(scene).Length;
                report.transformsPreserved = poses.Preserved && report.objectsBefore == report.objectsAfter;
                report.populationPreserved = passengers.SequenceEqual(Components<StationPassenger>(scene)) &&
                    registeredPassengers.SequenceEqual(world.passengers) && report.passengers == report.passengersBefore;
                report.nominalSpeedsPreserved = agents.All(a => a.SpeedPreserved);
                report.agentShapeAndPriorityPreserved = agents.All(a => a.ShapeAndPriorityPreserved);
                report.highQualityAvoidancePreserved = agents.All(a => a.AvoidancePreserved);
                report.remainingCoordinators = Components<StationCrowdCoordinator>(scene).Length;
                foreach (var role in agents.GroupBy(a => a.role).OrderBy(g => g.Key))
                    report.roleSpeeds.Add(new RoleSpeeds { role = role.Key, actors = role.Count(),
                        before = role.Select(a => a.speed).Distinct().OrderBy(v => v).ToArray(),
                        after = role.Select(a => a.agent.speed).Distinct().OrderBy(v => v).ToArray() });
                Require(report.transformsPreserved && report.populationPreserved && report.nominalSpeedsPreserved &&
                    report.agentShapeAndPriorityPreserved && report.highQualityAvoidancePreserved && report.remainingCoordinators == 0,
                    "Native navigation changed preserved scene data or retained a custom coordinator.");
                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkSceneDirty(scene);
                Require(EditorSceneManager.SaveScene(scene), "Could not save TrainStation.");
                if (File.Exists(RetiredCoordinatorPrefab))
                {
                    const string archive = "Documentation/Archive/CustomCrowdControl";
                    Directory.CreateDirectory(archive);
                    report.archivedCoordinatorPrefab = archive + "/Station_CrowdCoordinator.prefab.txt";
                    File.Copy(RetiredCoordinatorPrefab, report.archivedCoordinatorPrefab, true);
                    if (File.Exists(RetiredCoordinatorPrefab + ".meta"))
                        File.Copy(RetiredCoordinatorPrefab + ".meta", report.archivedCoordinatorPrefab + ".meta.txt", true);
                    Require(AssetDatabase.DeleteAsset(RetiredCoordinatorPrefab), "Could not retire the unused coordinator prefab.");
                }
                report.sceneSavedClean = !scene.isDirty; report.success = report.sceneSavedClean;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                report.completedUtc = DateTime.UtcNow.ToString("O");
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            }
        }

        public static void ConfigureMotion(StationActorMotion actor)
        {
            actor.directionalLocomotion = false;
            actor.enableStationaryDetours = false;
            if (actor.agent)
            {
                actor.agent.acceleration = 5f; actor.agent.angularSpeed = 240f;
                actor.agent.autoRepath = true; actor.agent.autoBraking = true;
                actor.agent.updatePosition = true; actor.agent.updateRotation = true; actor.agent.updateUpAxis = false;
                Record(actor.agent);
            }
            Record(actor);
        }
        public static void ConfigureCoordinator(StationCrowdCoordinator coordinator)
        {
            if (coordinator) UnityEngine.Object.DestroyImmediate(coordinator);
        }
        static void Backup(string path, string directory)
        {
            File.Copy(path, Path.Combine(directory, Path.GetFileName(path)));
            if (File.Exists(path + ".meta")) File.Copy(path + ".meta", Path.Combine(directory, Path.GetFileName(path) + ".meta"));
        }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        static void Record(UnityEngine.Object value)
        { EditorUtility.SetDirty(value); if (PrefabUtility.IsPartOfPrefabInstance(value)) PrefabUtility.RecordPrefabInstancePropertyModifications(value); }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
