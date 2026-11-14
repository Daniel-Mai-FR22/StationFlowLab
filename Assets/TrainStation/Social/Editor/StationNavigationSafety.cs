#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Saved navigation permits real floors; furniture never supplies a walking surface.</summary>
    public static class StationNavigationSafety
    {
        public const string ReportPath = "Reports/TrainStationSocial/navigation-safety.json";
        public const string NavigationPath = "Assets/TrainStation/Social/Navigation/Station_Floor_Only.asset";
        const float FloorTolerance = .085f;
        const float FurnitureMargin = .04f;
        static readonly HashSet<string> FloorNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Hall slab 72 x 56 m", "Cross concourse — step-free access to all platforms", "Sol_du_local",
            "Platform structural slab", "Walking surface", "Pedestrian forecourt", "Paved side court",
            "Gallery emergency exit apron", "Rail-side public path", "Forecourt outer path link",
            "West urban footpath", "East urban footpath", "West service walkway", "East service walkway", "Rear service crossing"
        };

        [Serializable] public sealed class Report
        {
            public string checkedUtc, scene, navigationAsset, previousNavigationAsset, backup, error;
            public bool success, savedSceneClean, transformsPreserved, floorOnlyDefault, linksDisabled;
            public int objectsBefore, objectsAfter, floorColliders, benches, planters, furnitureVolumes;
            public int topProbes, footprintProbes, floorEndpoints, completePaths, pathSegmentSamples, trackProbes;
            public int verticesBefore, elevatedVerticesBefore, verticesAfter, elevatedVerticesAfter, trianglesAfter;
            public float minHeightBefore, maxHeightBefore, minHeightAfter, maxHeightAfter;
            public float buildRadius, buildHeight, buildClimb, buildSlope;
            public List<FurnitureRecord> furniture = new List<FurnitureRecord>();
            public List<string> floors = new List<string>();
        }
        [Serializable] public sealed class FurnitureRecord
        {
            public string name, hierarchy;
            public Vector3 worldCenter, worldSize;
            public int topProbes, footprintProbes;
        }
        sealed class Pose
        {
            public Transform target; public Vector3 position, scale; public Quaternion rotation;
            public Pose(Transform transform) { target = transform; position = transform.localPosition; scale = transform.localScale; rotation = transform.localRotation; }
            public bool Preserved => target && target.localPosition == position && target.localScale == scale && target.localRotation == rotation;
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Apply floor-only navigation safety")]
        public static void Apply()
        {
            var scene = RequireSavedStation();
            var report = NewReport(scene);
            string createdAsset = null;
            bool sceneSaved = false;
            try
            {
                var surface = Components<NavMeshSurface>(scene).Single();
                report.previousNavigationAsset = AssetDatabase.GetAssetPath(surface.navMeshData);
                report.backup = "BuildRequests/Backups/StationNavigation-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".unity";
                Directory.CreateDirectory(Path.GetDirectoryName(report.backup));
                File.Copy(StationSocialAuthoring.ScenePath, report.backup, false);
                var poses = Components<Transform>(scene).Select(t => new Pose(t)).ToArray();
                report.objectsBefore = poses.Length;
                InspectHeights(report, true);
                Physics.SyncTransforms();
                ConfigureGeometry(scene, surface, report);
                Physics.SyncTransforms();
                surface.BuildNavMesh();
                Require(surface.navMeshData, "Unity produced no floor navigation data.");
                createdAsset = AssetDatabase.GenerateUniqueAssetPath(NavigationPath);
                AssetDatabase.CreateAsset(surface.navMeshData, createdAsset);
                surface.RemoveData(); surface.AddData();
                ValidateCore(scene, surface, report);
                report.objectsAfter = Components<Transform>(scene).Length;
                report.transformsPreserved = report.objectsBefore == report.objectsAfter && poses.All(p => p.Preserved);
                Require(report.transformsPreserved, "Navigation migration changed an authored object or its transform.");
                AssetDatabase.SaveAssets();
                Require(EditorSceneManager.SaveScene(scene), "Could not save floor-only station navigation.");
                sceneSaved = true;
                report.savedSceneClean = !scene.isDirty;
                report.success = true;
            }
            catch (Exception error)
            {
                report.error = error.ToString();
                // The entry guard required a clean station. All in-memory changes belong to this synchronous migration.
                if (!sceneSaved)
                {
                    EditorSceneManager.OpenScene(StationSocialAuthoring.ScenePath, OpenSceneMode.Single);
                    if (!string.IsNullOrEmpty(createdAsset)) AssetDatabase.DeleteAsset(createdAsset);
                }
                throw;
            }
            finally { Write(report); }
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Validate floor-only navigation safety")]
        public static void Validate()
        {
            var scene = RequireSavedStation(); var report = NewReport(scene);
            try
            {
                report.objectsBefore = Components<Transform>(scene).Length;
                var surface = Components<NavMeshSurface>(scene).Single();
                ValidateCore(scene, surface, report);
                report.objectsAfter = Components<Transform>(scene).Length;
                report.transformsPreserved = report.objectsBefore == report.objectsAfter;
                report.savedSceneClean = !scene.isDirty;
                report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally { Write(report); }
        }

        static void ConfigureGeometry(Scene scene, NavMeshSurface surface, Report report)
        {
            // Keep colliders in the bake: ignoring furniture would let agents walk through its footprint.
            surface.defaultArea = 1;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.overrideVoxelSize = true; surface.voxelSize = .08f;
            var serialized = new SerializedObject(surface);
            serialized.FindProperty("m_GenerateLinks").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            foreach (var collider in FloorColliders(scene))
            {
                Require(collider is BoxCollider && Mathf.Abs(collider.transform.up.y) > .999f,
                    "Floor allowlist requires a horizontal box collider: " + Hierarchy(collider.transform));
                Require(collider.bounds.max.y <= .025f && collider.bounds.max.y >= -.09f,
                    "An allowlisted floor was moved away from level zero: " + collider.name);
                var modifier = collider.GetComponent<NavMeshModifier>();
                if (!modifier) modifier = collider.gameObject.AddComponent<NavMeshModifier>();
                modifier.overrideArea = true; modifier.area = 0; modifier.ignoreFromBuild = false;
                modifier.applyToChildren = false; modifier.overrideGenerateLinks = true; modifier.generateLinks = false;
                Record(modifier);
            }
            foreach (var furniture in Furniture(scene))
            {
                var modifier = furniture.GetComponent<NavMeshModifier>();
                if (!modifier) modifier = furniture.gameObject.AddComponent<NavMeshModifier>();
                modifier.overrideArea = true; modifier.area = 1; modifier.ignoreFromBuild = false; modifier.applyToChildren = true;
                modifier.overrideGenerateLinks = true; modifier.generateLinks = false;
                // A vertical volume closes gaps between slats/legs as well as the furniture top.
                var bounds = LocalColliderBounds(furniture);
                Vector3 minimum = bounds.min, maximum = bounds.max;
                minimum.x -= FurnitureMargin; minimum.z -= FurnitureMargin;
                maximum.x += FurnitureMargin; maximum.z += FurnitureMargin;
                minimum.y = Mathf.Min(minimum.y, furniture.InverseTransformPoint(new Vector3(furniture.position.x,-.5f,furniture.position.z)).y);
                maximum.y = Mathf.Max(maximum.y + .2f, furniture.InverseTransformPoint(new Vector3(furniture.position.x,2.2f,furniture.position.z)).y);
                var volume = furniture.GetComponent<NavMeshModifierVolume>();
                if (!volume) volume = furniture.gameObject.AddComponent<NavMeshModifierVolume>();
                volume.area = 1; volume.center = (minimum + maximum) * .5f; volume.size = maximum - minimum;
                Record(modifier); Record(volume);
            }
            Record(surface);
        }

        static void ValidateCore(Scene scene, NavMeshSurface surface, Report report)
        {
            Physics.SyncTransforms();
            report.navigationAsset = AssetDatabase.GetAssetPath(surface.navMeshData);
            Require(!string.IsNullOrEmpty(report.navigationAsset), "Navigation is not a saved asset.");
            report.floorOnlyDefault = surface.defaultArea == 1;
            Require(report.floorOnlyDefault, "Unknown collider surfaces must default to Not Walkable.");
            report.linksDisabled = !new SerializedObject(surface).FindProperty("m_GenerateLinks").boolValue;
            Require(report.linksDisabled, "Automatic jump/drop links must be disabled.");
            var settings = surface.GetBuildSettings();
            report.buildRadius = settings.agentRadius; report.buildHeight = settings.agentHeight;
            report.buildClimb = settings.agentClimb; report.buildSlope = settings.agentSlope;
            foreach (var collider in FloorColliders(scene))
            {
                var modifier = collider.GetComponent<NavMeshModifier>();
                Require(modifier && modifier.enabled && modifier.overrideArea && modifier.area == 0 && !modifier.ignoreFromBuild && !modifier.applyToChildren,
                    "Floor lacks an explicit local-only Walkable modifier: " + Hierarchy(collider.transform));
                report.floorColliders++; report.floors.Add(Hierarchy(collider.transform));
            }
            Require(report.floorColliders >= 18, "Expected hall, concourse, shops, platforms and external floor colliders.");
            foreach (var modifier in Components<NavMeshModifier>(scene).Where(m => m.enabled && m.overrideArea && m.area == 0 && !m.ignoreFromBuild))
                Require(FloorNames.Contains(modifier.name) && !modifier.applyToChildren,
                    "An unexpected object could re-enable walkable furniture: " + Hierarchy(modifier.transform));
            foreach (var furniture in Furniture(scene))
            {
                if (IsBench(furniture)) report.benches++; else report.planters++;
                var modifier = furniture.GetComponent<NavMeshModifier>();
                var volume = furniture.GetComponent<NavMeshModifierVolume>();
                Require(modifier && modifier.enabled && modifier.overrideArea && modifier.area == 1 && !modifier.ignoreFromBuild,
                    "Furniture is not retained as a solid nonwalkable obstacle: " + Hierarchy(furniture));
                Require(volume && volume.enabled && volume.area == 1, "Furniture footprint exclusion is missing: " + Hierarchy(furniture));
                report.furnitureVolumes++;
                var bounds = LocalColliderBounds(furniture);
                var record = new FurnitureRecord { name = furniture.name, hierarchy = Hierarchy(furniture), worldCenter = furniture.TransformPoint(bounds.center), worldSize = Vector3.Scale(bounds.size,furniture.lossyScale) };
                foreach (float x in new[] { -.75f,0,.75f }) foreach (float z in new[] { -.75f,0,.75f })
                {
                    var at = furniture.TransformPoint(new Vector3(bounds.center.x + bounds.extents.x * x,bounds.max.y,bounds.center.z + bounds.extents.z * z));
                    Require(!NavMesh.SamplePosition(at,out _,.09f,NavMesh.AllAreas), "Walkable furniture top at " + at + ": " + record.hierarchy);
                    record.topProbes++; report.topProbes++;
                    at.y = .02f;
                    Require(!NavMesh.SamplePosition(at,out _,.055f,NavMesh.AllAreas), "Walking corridor intersects furniture footprint at " + at + ": " + record.hierarchy);
                    record.footprintProbes++; report.footprintProbes++;
                }
                report.furniture.Add(record);
            }
            Require(report.benches > 40 && report.planters >= 9, "Bench/planter inventory is incomplete.");
            InspectHeights(report, false);
            Require(report.trianglesAfter > 100 && report.elevatedVerticesAfter == 0,
                "Navigation contains raised walking surfaces: " + report.elevatedVerticesAfter + " vertices, height " + report.minHeightAfter + ".." + report.maxHeightAfter);
            var world = Components<StationSocialWorld>(scene).Single();
            var services = new[] { world.informationDesk }.Concat(world.shopDesks).Where(d => d).ToArray();
            Require(services.Length == 4, "Expected accueil and three shops.");
            Vector3 hub = FloorPoint(world.informationDesk.queueSlots[0].position,"Accueil service front",report);
            foreach (var service in services)
                foreach (var point in service.queueSlots) CheckPath(point.position,hub,service.name + "/" + point.name,report);
            foreach (var seat in world.seats) CheckPath(seat.ApproachPosition,hub,seat.name,report);
            foreach (var passenger in world.passengers)
            {
                var start = passenger.initialSeat ? passenger.initialSeat.ApproachPosition : passenger.transform.position;
                foreach (var service in services) CheckPath(start,service.queueSlots[0].position,passenger.name + " to " + service.name,report);
                foreach (var point in passenger.wanderPoints ?? Array.Empty<Transform>()) CheckPath(point.position,hub,point.name,report);
                if (passenger.homePoint) CheckPath(passenger.homePoint.position,hub,passenger.name + " home",report);
            }
            foreach (var group in world.walkingGroups)
                foreach (var point in group.waypoints) CheckPath(point.position,hub,group.name + "/" + point.name,report);
            foreach (var cleaner in world.cleaners)
                foreach (var point in cleaner.workPoints) CheckPath(point.position,hub,cleaner.name + "/" + point.name,report);
            foreach (float x in new[] { -41.7f,-30.3f,-17.7f,-6.3f,6.3f,17.7f,30.3f,41.7f })
                foreach (float z in new[] { 60f,100f,150f,200f })
                {
                    Require(!NavMesh.SamplePosition(new Vector3(x,0,z),out _,.5f,NavMesh.AllAreas), "Track exclusion was lost at " + x + "," + z);
                    report.trackProbes++;
                }
        }

        static void CheckPath(Vector3 from, Vector3 to, string label, Report report)
        {
            from = FloorPoint(from,label + " start",report); to = FloorPoint(to,label + " destination",report);
            var path = new NavMeshPath();
            Require(NavMesh.CalculatePath(from,to,NavMesh.AllAreas,path) && path.status == NavMeshPathStatus.PathComplete,
                "Incomplete floor route " + label + " from " + from + " to " + to);
            foreach (var point in path.corners) Require(Mathf.Abs(point.y) <= FloorTolerance,"Route climbs above the floor: " + label);
            for (int i = 1; i < path.corners.Length; i++)
            {
                var a = path.corners[i-1]; var b = path.corners[i];
                int samples = Mathf.Max(1,Mathf.CeilToInt(Vector3.Distance(a,b) / 1.5f));
                for (int j = 0; j <= samples; j++)
                {
                    var point = Vector3.Lerp(a,b,j/(float)samples);
                    Require(Mathf.Abs(point.y) <= FloorTolerance && NavMesh.SamplePosition(point,out var hit,.06f,NavMesh.AllAreas) && Mathf.Abs(hit.position.y) <= FloorTolerance,
                        "Route segment left the safe floor: " + label + " at " + point);
                    report.pathSegmentSamples++;
                }
            }
            report.completePaths++;
        }
        static Vector3 FloorPoint(Vector3 point, string label, Report report)
        {
            Require(Mathf.Abs(point.y) <= .15f,"Authored endpoint is above floor: " + label + " at " + point);
            Require(NavMesh.SamplePosition(point,out var hit,.25f,NavMesh.AllAreas) && Mathf.Abs(hit.position.y) <= FloorTolerance &&
                Vector2.Distance(new Vector2(point.x,point.z),new Vector2(hit.position.x,hit.position.z)) <= .18f,
                "Authored endpoint is not on the safe floor: " + label + " at " + point);
            report.floorEndpoints++; return hit.position;
        }
        static void InspectHeights(Report report, bool before)
        {
            var triangles = NavMesh.CalculateTriangulation(); var vertices = triangles.vertices;
            float min = vertices.Length == 0 ? 0 : vertices.Min(v => v.y), max = vertices.Length == 0 ? 0 : vertices.Max(v => v.y);
            int elevated = vertices.Count(v => Mathf.Abs(v.y) > FloorTolerance);
            if (before) { report.verticesBefore = vertices.Length; report.elevatedVerticesBefore = elevated; report.minHeightBefore = min; report.maxHeightBefore = max; }
            else { report.verticesAfter = vertices.Length; report.elevatedVerticesAfter = elevated; report.minHeightAfter = min; report.maxHeightAfter = max; report.trianglesAfter = triangles.indices.Length / 3; }
        }
        /// <summary>Only authored level-zero floor geometry; furniture/counter tops cannot satisfy ground checks.</summary>
        public static bool IsStationFloor(Collider collider) => collider && collider.enabled && !collider.isTrigger &&
            collider.gameObject.activeInHierarchy && collider is BoxCollider && FloorNames.Contains(collider.name) &&
            Mathf.Abs(collider.transform.up.y) > .999f && collider.bounds.max.y <= .025f && collider.bounds.max.y >= -.09f;
        static Collider[] FloorColliders(Scene scene) => Components<Collider>(scene).Where(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy && FloorNames.Contains(c.name)).ToArray();
        static bool IsBench(Transform t) => t.name.StartsWith("Banquette_",StringComparison.Ordinal) || t.name.StartsWith("Bench / Banc",StringComparison.Ordinal);
        static Transform[] Furniture(Scene scene) => Components<Transform>(scene).Where(t => IsBench(t) || t.name == "Jardiniere" || t.name.StartsWith("Tree planter / Arbre",StringComparison.Ordinal)).ToArray();
        static Bounds LocalColliderBounds(Transform root)
        {
            var colliders = root.GetComponentsInChildren<Collider>(true).Where(c => c.enabled && !c.isTrigger).ToArray();
            Require(colliders.Length > 0,"Furniture has no collider geometry: " + Hierarchy(root));
            var result = new Bounds(); bool initialized = false;
            foreach (var collider in colliders)
            {
                var bounds = collider.bounds;
                foreach (int x in new[] { -1,1 }) foreach (int y in new[] { -1,1 }) foreach (int z in new[] { -1,1 })
                {
                    var point = root.InverseTransformPoint(bounds.center + Vector3.Scale(bounds.extents,new Vector3(x,y,z)));
                    if (!initialized) { result = new Bounds(point,Vector3.zero); initialized = true; } else result.Encapsulate(point);
                }
            }
            return result;
        }
        static Scene RequireSavedStation()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode,"Stop Play before changing saved navigation.");
            for (int i=0;i<SceneManager.sceneCount;i++) Require(!SceneManager.GetSceneAt(i).isDirty,"Save scene edits before navigation migration.");
            var scene = SceneManager.GetActiveScene();
            if (scene.path != StationSocialAuthoring.ScenePath) scene = EditorSceneManager.OpenScene(StationSocialAuthoring.ScenePath,OpenSceneMode.Single);
            Require(SceneManager.sceneCount == 1,"Navigation verification requires only TrainStation loaded.");
            return scene;
        }
        static Report NewReport(Scene scene) => new Report { checkedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path };
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        static void Record(UnityEngine.Object value) { EditorUtility.SetDirty(value); if (PrefabUtility.IsPartOfPrefabInstance(value)) PrefabUtility.RecordPrefabInstancePropertyModifications(value); }
        static string Hierarchy(Transform t) { string path=t.name; while(t.parent) { t=t.parent; path=t.name+"/"+path; } return path; }
        static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
        static void Write(Report report) { Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true)); }
    }
}
#endif
