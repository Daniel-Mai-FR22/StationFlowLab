#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Removes owned suitcases from saved objects and their reusable prefabs, including inactive instances.</summary>
    public static class StationNoLuggageAuthoring
    {
        public const string ReportPath = "Reports/TrainStationSocial/no-luggage-authoring.json";
        static readonly string[] PassengerPrefabs =
        {
            StationSocialAuthoring.PassengerPrefabPath,
            StationMixedCrowdAuthoring.SoloPrefabPath,
            StationMixedCrowdAuthoring.WalkingMemberPrefabPath,
            StationCrowdAuthoring.MemberPrefabPath,
            StationCrowdAuthoring.GroupPrefabPath,
            StationMixedCrowdAuthoring.WalkingGroupPrefabPath
        };

        [Serializable] public sealed class RemovalReport
        {
            public bool success, deletedDedicatedLuggagePrefab;
            public string completedUtc, scene, backupDirectory, error;
            public int initialSceneSuitcases, initialVisibleSuitcases, initialInactiveSuitcases, removedPrefabSuitcases, removedSeatMarkers;
            public int remainingSceneSuitcases, remainingPassengerReferences, remainingSeatReferences, remainingPrefabSuitcases;
            public int passengers, animatedActorsBefore, animatedActorsAfter, objectsBefore, objectsAfter, missingScriptsBefore, missingScriptsAfter;
            public int preservedEmptyBaggageTrolleys, preservedStorageLockers, preservedCleaningCollectionBags;
            public List<string> updatedPrefabs = new List<string>();
            public List<string> remainingAssetDependencies = new List<string>();
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Remove all saved passenger luggage")]
        public static void RemoveAll()
        {
            RequireCleanEditor();
            var report = new RemovalReport { scene = StationSocialAuthoring.ScenePath };
            try
            {
                var scene = SceneManager.GetSceneByPath(StationSocialAuthoring.ScenePath);
                if (!scene.IsValid() || !scene.isLoaded) scene = EditorSceneManager.OpenScene(StationSocialAuthoring.ScenePath,OpenSceneMode.Single);
                var roots = scene.GetRootGameObjects();
                var world = roots.SelectMany(g => g.GetComponentsInChildren<StationSocialWorld>(true)).Single();
                var originalBags = roots.SelectMany(BagRoots).ToArray();
                report.initialSceneSuitcases = originalBags.Length;
                report.initialVisibleSuitcases = originalBags.Count(t => t.gameObject.activeInHierarchy);
                report.initialInactiveSuitcases = originalBags.Length - report.initialVisibleSuitcases;
                report.objectsBefore = roots.Sum(g => g.GetComponentsInChildren<Transform>(true).Length);
                report.animatedActorsBefore = roots.Sum(g => g.GetComponentsInChildren<StationActorMotion>(true).Length);
                report.missingScriptsBefore = MissingScripts(roots);
                report.backupDirectory = "BuildRequests/Backups/NoLuggage-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                Directory.CreateDirectory(report.backupDirectory);
                Backup(StationSocialAuthoring.ScenePath,report.backupDirectory);
                foreach (var path in PassengerPrefabs.Concat(new[] { StationSocialAuthoring.LuggagePrefabPath })) Backup(path,report.backupDirectory);

                // Source prefabs first: their nested objects disappear from all inherited scene instances as well.
                foreach (string path in PassengerPrefabs)
                {
                    if (!AssetDatabase.LoadAssetAtPath<GameObject>(path)) continue;
                    var prefab = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        bool changed = false;
                        foreach (var passenger in prefab.GetComponentsInChildren<StationPassenger>(true))
                            if (passenger.luggage) { passenger.luggage = null; changed = true; }
                        foreach (var bag in BagRoots(prefab))
                        {
                            UnityEngine.Object.DestroyImmediate(bag.gameObject);
                            report.removedPrefabSuitcases++; changed = true;
                        }
                        if (changed)
                        {
                            Record(prefab); PrefabUtility.SaveAsPrefabAsset(prefab,path);
                            report.updatedPrefabs.Add(path);
                        }
                    }
                    finally { PrefabUtility.UnloadPrefabContents(prefab); }
                }
                foreach (var passenger in world.passengers)
                {
                    passenger.luggage = null;
                    passenger.name = passenger.name.Replace(" with suitcase","");
                    if (PrefabUtility.IsPartOfPrefabInstance(passenger)) PrefabUtility.RecordPrefabInstancePropertyModifications(passenger);
                    if (PrefabUtility.IsPartOfPrefabInstance(passenger.gameObject)) PrefabUtility.RecordPrefabInstancePropertyModifications(passenger.gameObject);
                }
                foreach (var root in roots) foreach (var bag in BagRoots(root)) UnityEngine.Object.DestroyImmediate(bag.gameObject);
                var markers = world.seats.Where(s => s && s.luggagePoint).Select(s => s.luggagePoint).Distinct().ToArray();
                foreach (var seat in world.seats) if (seat) { seat.luggagePoint = null; EditorUtility.SetDirty(seat); }
                foreach (var marker in markers) { UnityEngine.Object.DestroyImmediate(marker.gameObject); report.removedSeatMarkers++; }
                var emptyRoot = world.transform.Find("06 Luggage — owned objects and parking");
                if (emptyRoot && emptyRoot.childCount == 0) UnityEngine.Object.DestroyImmediate(emptyRoot.gameObject);
                var soloRoot = world.transform.Find(StationMixedCrowdAuthoring.RootName + "/04 Solo travellers — optional luggage");
                if (soloRoot) soloRoot.name = "04 Solo travellers";
                EditorUtility.SetDirty(world); AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save the no-luggage station scene.");

                // Delete only the dedicated prop asset after every scene/prefab reference has actually been removed.
                if (AssetDatabase.LoadAssetAtPath<GameObject>(StationSocialAuthoring.LuggagePrefabPath))
                {
                    foreach (var path in AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith("Assets/",StringComparison.Ordinal) &&
                        p != StationSocialAuthoring.LuggagePrefabPath && (p.EndsWith(".prefab",StringComparison.OrdinalIgnoreCase) || p.EndsWith(".unity",StringComparison.OrdinalIgnoreCase) || p.EndsWith(".asset",StringComparison.OrdinalIgnoreCase))))
                        if (AssetDatabase.GetDependencies(path,false).Contains(StationSocialAuthoring.LuggagePrefabPath)) report.remainingAssetDependencies.Add(path);
                    if (report.remainingAssetDependencies.Count != 0) throw new InvalidOperationException("The standalone luggage asset still has serialized dependencies: " + string.Join(", ",report.remainingAssetDependencies));
                    if (!AssetDatabase.DeleteAsset(StationSocialAuthoring.LuggagePrefabPath)) throw new IOException("Could not remove the unused dedicated luggage prefab asset.");
                    report.deletedDedicatedLuggagePrefab = true;
                }
                AssetDatabase.SaveAssets();
                ValidateSceneAndPrefabs(scene,world,report);
                report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                report.completedUtc = DateTime.UtcNow.ToString("O");
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true));
            }
        }

        public static Transform[] BagRoots(GameObject root)
        {
            var candidates = root.GetComponentsInChildren<Transform>(true).Where(IsSuitcase).ToArray();
            return candidates.Where(t => !candidates.Any(parent => parent != t && t.IsChildOf(parent))).ToArray();
        }
        static bool IsSuitcase(Transform item)
        {
            if (item.name.StartsWith("Luggage parking",StringComparison.Ordinal) || item.name.StartsWith("06 Luggage",StringComparison.Ordinal)) return false;
            if (item.name.StartsWith("Luggage —",StringComparison.Ordinal) || item.name.StartsWith("Optional luggage —",StringComparison.Ordinal) ||
                item.name == "Station_Luggage" || item.name == "Valise rigide") return true;
            return PrefabUtility.IsAnyPrefabInstanceRoot(item.gameObject) && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(item.gameObject) == StationSocialAuthoring.LuggagePrefabPath;
        }
        static void ValidateSceneAndPrefabs(Scene scene, StationSocialWorld world, RemovalReport report)
        {
            var roots = scene.GetRootGameObjects();
            report.remainingSceneSuitcases = roots.Sum(g => BagRoots(g).Length);
            report.remainingPassengerReferences = roots.SelectMany(g => g.GetComponentsInChildren<StationPassenger>(true)).Count(p => p.luggage);
            report.remainingSeatReferences = roots.SelectMany(g => g.GetComponentsInChildren<StationSeat>(true)).Count(s => s.luggagePoint);
            report.passengers = world.passengers.Length;
            report.animatedActorsAfter = roots.Sum(g => g.GetComponentsInChildren<StationActorMotion>(true).Length);
            report.objectsAfter = roots.Sum(g => g.GetComponentsInChildren<Transform>(true).Length);
            report.missingScriptsAfter = MissingScripts(roots);
            var all = roots.SelectMany(g => g.GetComponentsInChildren<Transform>(true)).ToArray();
            report.preservedEmptyBaggageTrolleys = all.Count(t => t.parent && t.parent.name == "Chariots_bagages_stationnes");
            report.preservedStorageLockers = all.Count(t => t.name.StartsWith("Casier_",StringComparison.Ordinal));
            report.preservedCleaningCollectionBags = all.Count(t => t.name == "Sac de collecte");
            foreach (string path in PassengerPrefabs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path); if (!prefab) continue;
                report.remainingPrefabSuitcases += BagRoots(prefab).Length;
                if (prefab.GetComponentsInChildren<StationPassenger>(true).Any(p => p.luggage)) throw new InvalidOperationException("A passenger prefab still refers to luggage: " + path);
            }
            if (report.remainingSceneSuitcases != 0 || report.remainingPassengerReferences != 0 || report.remainingSeatReferences != 0 || report.remainingPrefabSuitcases != 0)
                throw new InvalidOperationException("Saved luggage removal is incomplete.");
            if (report.animatedActorsAfter != report.animatedActorsBefore || (report.passengers != 60 && report.passengers != 300) || report.missingScriptsAfter != report.missingScriptsBefore)
                throw new InvalidOperationException("Removing luggage changed the passenger population or script integrity.");
            if (report.preservedEmptyBaggageTrolleys != 3 || report.preservedStorageLockers != 16 || report.preservedCleaningCollectionBags != 3)
                throw new InvalidOperationException("Functional storage or cleaning equipment was not preserved.");
        }
        static int MissingScripts(IEnumerable<GameObject> roots) => roots.SelectMany(g => g.GetComponentsInChildren<Transform>(true)).Sum(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject));
        static void Record(GameObject root)
        { foreach (var component in root.GetComponentsInChildren<Component>(true)) if (component && PrefabUtility.IsPartOfPrefabInstance(component)) PrefabUtility.RecordPrefabInstancePropertyModifications(component); }
        static void Backup(string path, string directory)
        {
            if (File.Exists(path)) File.Copy(path,Path.Combine(directory,Path.GetFileName(path)),false);
            if (File.Exists(path + ".meta")) File.Copy(path + ".meta",Path.Combine(directory,Path.GetFileName(path) + ".meta"),false);
        }
        static void RequireCleanEditor()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode()) throw new InvalidOperationException("Stop Play and animation preview before removing saved baggage.");
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save existing scene edits first. Luggage removal preserves unsaved user work.");
        }
    }
}
#endif
