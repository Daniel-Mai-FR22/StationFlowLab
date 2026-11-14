#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Daily-owned street arrivals and two real openings in the existing facade.</summary>
    public static class StationDecisionDayEntranceAuthoring
    {
        public const string RootName = "19 Daily street arrivals and open facade";
        public const string ReportDirectory = "Reports/TrainStationDaily/Entrances";
        public const float FacadeZ = -28f;
        public static readonly string[] InboundIds = { "entry-west", "entry-mid-west", "entry-mid-east", "entry-east" };
        public static readonly float[] InboundX = { -24f, -9f, 9f, 24f };
        static readonly float[] SpawnRows = { -60.6f, -59.35f };
        const float BodyRadius = .4f;

        [Serializable] public sealed class FileEvidence { public string path, before, after; }
        [Serializable] public sealed class PortalEvidence
        {
            public string entranceId;
            public double weight;
            public Vector3[] spawnPoints;
            public float minimumPointSeparation;
        }
        [Serializable] public sealed class MouthEvidence
        {
            public float centerX, physicallyClearWidth, physicallyClearHeight;
            public bool newlyOpened;
            public Vector3 outside, inside;
            public int crossSectionBodyChecks;
        }
        [Serializable] public sealed class SubgradeEvidence
        {
            public string name;
            public Vector3 physicalCenter, physicalSize;
            public float physicalTopY, pedestrianFloorTopY, verticalGap, voxelSize;
            public bool physicalColliderRetained, ignoredFromNavigation;
        }
        [Serializable] public sealed class AuthoringReport
        {
            public string configuredUtc, scene, error;
            public bool success, navigationBakeAndSavePending;
            public Vector3 forecourtCenter, forecourtSize;
            public Vector3 previousCollectionCenter, previousCollectionSize, collectionCenter, collectionSize;
            public int positiveInboundPortals, streetSpawnPoints, physicalFacadeMouths;
            public string[] excludedSubgradeNavigationSources;
            public List<SubgradeEvidence> subgradeEvidence = new List<SubgradeEvidence>();
            public List<PortalEvidence> portals = new List<PortalEvidence>();
            public List<FileEvidence> protectedFiles = new List<FileEvidence>();
            public string scope = "Saved-scene authoring preparation only. Root must rebuild the Daily-owned native NavMesh, save, then call Validate. Existing pedestrian forecourt and street geometry are reused; no vehicles, steering or forced doorway waypoints are added.";
        }
        [Serializable] public sealed class ValidationReport
        {
            public string checkedUtc, scene, sceneHash, navigationHash;
            public bool success, savedSceneClean, protectedFilesPreserved;
            public int physicalBodyChecks, exactFloorChecks, spawnToPoiRoutes, doorRoutes, freeExitRoutes;
            public float minimumSpawnSeparation = float.MaxValue, minimumSpawnDistanceFromFacade = float.MaxValue;
            public List<PortalEvidence> portals = new List<PortalEvidence>();
            public List<MouthEvidence> mouths = new List<MouthEvidence>();
            public List<FileEvidence> protectedFiles = new List<FileEvidence>();
            public List<SubgradeEvidence> subgradeEvidence = new List<SubgradeEvidence>();
            public List<string> errors = new List<string>();
            public string scope = "Five real facade mouths, four equally weighted street arrival zones, twelve mutually separated roadside spawn positions per active zone, exact saved native floor and complete native routes. Door proofs establish physical access independently; actual direct-to-POI NavMesh routing may choose any open mouth. entry-main weight zero disables inbound assignment, without making its physical door one-way. No runtime density, FIFO order or full-day behavior is certified by this static check.";
        }
        [Serializable] public sealed class CaptureReport
        {
            public string capturedUtc, scene, mode;
            public float elapsedSimulationSeconds;
            public CameraView[] views;
            public string scope = "Native Unity camera pixels with recorded camera calibration; UI is hidden only for capture and restored immediately. These images do not certify pedestrian routing or arrival distribution.";
        }
        [Serializable] public sealed class CameraView
        {
            public string image;
            public Vector3 position, eulerAngles;
            public float verticalFieldOfView;
            public int width, height;
            public Matrix4x4 worldToCamera, projection;
        }

        /// <summary>Mutates the open Daily scene only; the command owner performs its single bake/save.</summary>
        public static void Apply()
        {
            var scene = RequireDaily(false);
            var surface = Components<NavMeshSurface>(scene).Single();
            Require(AssetDatabase.GetAssetPath(surface.navMeshData) == StationDecisionGateAuthoring.NavigationPath,
                "Street arrivals must use the Daily-owned navigation asset.");
            Require(surface.defaultArea == 1 && surface.useGeometry == NavMeshCollectGeometry.PhysicsColliders,
                "Keep the existing physical floor-only navigation policy.");
            Require(surface.collectObjects == CollectObjects.Volume, "Keep the Daily native bake bounded by its saved collection volume.");
            var controller = Components<StationDecisionArrivalController>(scene).Single();
            Require(controller.world && controller.world.IsDaily, "Street arrivals require the Daily controller.");
            var report = new AuthoringReport { configuredUtc = DateTime.UtcNow.ToString("O"), scene = scene.path,
                navigationBakeAndSavePending = true, positiveInboundPortals = 4, streetSpawnPoints = 48, physicalFacadeMouths = 5 };
            Directory.CreateDirectory(ReportDirectory);
            foreach (var path in ProtectedPaths())
                if (File.Exists(path)) report.protectedFiles.Add(new FileEvidence { path = path, before = Hash(path) });
            try
            {
                report.previousCollectionCenter = surface.center; report.previousCollectionSize = surface.size;
                var collectionBounds = new Bounds(surface.center, surface.size);
                // The previous volume stopped at z=-26 and omitted the entire
                // actual forecourt even though its physical slab was Walkable.
                // Extend only south; original hall/platform bounds stay intact.
                collectionBounds.Encapsulate(surface.transform.InverseTransformPoint(new Vector3(-31, .02f, -63)));
                collectionBounds.Encapsulate(surface.transform.InverseTransformPoint(new Vector3(31, .02f, -63)));
                surface.center = collectionBounds.center; surface.size = collectionBounds.size; Record(surface);
                report.collectionCenter = surface.center; report.collectionSize = surface.size;
                var floor = Components<Collider>(scene).Single(c => c.name == "Pedestrian forecourt");
                Require(floor is BoxCollider && floor.bounds.max.y >= -.02f && floor.bounds.max.y <= .025f,
                    "The existing forecourt must remain a horizontal level-zero physical floor.");
                var modifier = floor.GetComponent<NavMeshModifier>() ?? floor.gameObject.AddComponent<NavMeshModifier>();
                modifier.overrideArea = true; modifier.area = 0; modifier.ignoreFromBuild = false;
                modifier.applyToChildren = false; modifier.overrideGenerateLinks = true; modifier.generateLinks = false;
                Record(modifier);
                report.forecourtCenter = floor.bounds.center; report.forecourtSize = floor.bounds.size;
                // This buried construction slab overlaps the pedestrian slab
                // within the .08 m raster voxel. Its default NotWalkable top
                // must not erase the actual level-zero pedestrian surface.
                // Keep its physical collider/rendering; omit only this known
                // subgrade source, never furniture or road barriers.
                var foundation = Components<Collider>(scene).Single(c => c.name == "Urban ground foundation");
                Require(foundation is BoxCollider && foundation.bounds.max.y < floor.bounds.max.y && foundation.bounds.max.y <= 0f,
                    "The urban foundation must remain buried beneath the actual pedestrian floor.");
                float rasterGap = floor.bounds.max.y - foundation.bounds.max.y;
                Require(surface.overrideVoxelSize && rasterGap > 0f && rasterGap <= surface.voxelSize,
                    "Only a demonstrated overlapping subgrade raster source may be omitted.");
                Require(foundation.bounds.Contains(new Vector3(floor.bounds.min.x, foundation.bounds.center.y, floor.bounds.min.z)) &&
                    foundation.bounds.Contains(new Vector3(floor.bounds.max.x, foundation.bounds.center.y, floor.bounds.max.z)),
                    "The named urban foundation does not cover the pedestrian forecourt in horizontal bounds.");
                var foundationModifier = foundation.GetComponent<NavMeshModifier>() ?? foundation.gameObject.AddComponent<NavMeshModifier>();
                foundationModifier.overrideArea = true; foundationModifier.area = 1; foundationModifier.ignoreFromBuild = true;
                foundationModifier.applyToChildren = false; foundationModifier.overrideGenerateLinks = true; foundationModifier.generateLinks = false;
                Record(foundationModifier);
                report.excludedSubgradeNavigationSources = new[] { foundation.name };
                report.subgradeEvidence.Add(Subgrade(foundation, floor, surface));

                var existing = scene.GetRootGameObjects().SingleOrDefault(g => g.name == RootName);
                foreach (var old in controller.portals.Where(p => p.trackNumber == 0))
                    foreach (var area in old.spawnPoints.Where(t => t).Select(t => t.parent).Distinct())
                        if (area && (!existing || !area.IsChildOf(existing.transform)))
                        { area.gameObject.SetActive(false); Record(area.gameObject); }
                if (existing) UnityEngine.Object.DestroyImmediate(existing);
                var root = new GameObject(RootName).transform;
                SceneManager.MoveGameObjectToScene(root.gameObject, scene);
                OpenIntermediateMouths(scene, root);

                var portals = new List<StationDecisionSpawnPortal>();
                for (int portalIndex = 0; portalIndex < InboundIds.Length; ++portalIndex)
                {
                    var portal = new StationDecisionSpawnPortal { entranceId = InboundIds[portalIndex], trackNumber = 0, spawnPoints = new Transform[12] };
                    var area = Child(root, portal.entranceId + " — roadside pedestrian arrival zone");
                    for (int row = 0; row < 2; ++row)
                        for (int col = 0; col < 6; ++col)
                            portal.spawnPoints[row * 6 + col] = Marker(area, "Street arrival " + (row * 6 + col + 1).ToString("00"),
                                new Vector3(InboundX[portalIndex] + (col - 2.5f) * 1.1f, .02f, SpawnRows[row]));
                    portals.Add(portal);
                }
                // Keep this authored zone for existing saved-layout route contracts.
                // Its zero weight prevents incoming parties sharing central outgoing traffic.
                var main = new StationDecisionSpawnPortal { entranceId = "entry-main", trackNumber = 0, spawnPoints = new Transform[6] };
                var mainArea = Child(root, "entry-main — inactive inbound compatibility zone");
                for (int row = 0; row < 3; ++row)
                    for (int col = 0; col < 2; ++col)
                        main.spawnPoints[row * 2 + col] = Marker(mainArea, "Unused central arrival " + (row * 2 + col + 1).ToString("00"),
                            new Vector3(col == 0 ? -3.05f : 3.05f, .02f, SpawnRows[0] + row * 1.25f));
                portals.Insert(2, main);
                controller.portals = portals.Concat(controller.portals.Where(p => p.trackNumber > 0)).ToArray();
                controller.entranceWeights = portals.Select(p => new DecisionEntranceWeight { entranceId = p.entranceId, weight = p.entranceId == "entry-main" ? 0d : 1d }).ToArray();
                Record(controller);

                var white = Components<Renderer>(scene).First(r => r.name == "Pier capital").sharedMaterial;
                for (int i = 0; i < InboundIds.Length; ++i)
                {
                    var sign = Child(root, InboundIds[i] + " — taxi drop-off identification");
                    // Ground paint and lettering use the existing plaza; nothing blocks walking.
                    Box(sign, "Pedestrian arrival marking", new Vector3(InboundX[i], .011f, -60.6f), new Vector3(6.2f, .012f, .10f), white, false);
                    Label(sign, "Arrival zone", "DEPOSE MINUTE · " + (i + 1), new Vector3(InboundX[i], .03f, -58.65f), 2.2f,
                        Quaternion.Euler(90, 0, 0), new Color(.03f, .12f, .3f));
                }
                Physics.SyncTransforms();
                foreach (var portal in portals)
                {
                    foreach (var marker in portal.spawnPoints)
                    {
                        Require(collectionBounds.Contains(surface.transform.InverseTransformPoint(marker.position)), "Street anchor lies outside the expanded native bake collection volume.");
                        Clear(marker.position, null, portal.entranceId + "/" + marker.name);
                    }
                    report.portals.Add(Portal(portal, controller.entranceWeights));
                }
                EditorSceneManager.MarkSceneDirty(scene);
                report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally { Write(ReportDirectory + "/entrance-authoring.json", report); }
        }

        /// <summary>Read-only proof against the saved NavMesh after the command owner's bake/save.</summary>
        public static void Validate()
        {
            var report = new ValidationReport { checkedUtc = DateTime.UtcNow.ToString("O"), scene = StationDecisionDayAuthoring.ScenePath };
            try
            {
                var scene = RequireDaily(true);
                var controller = Components<StationDecisionArrivalController>(scene).Single();
                var world = controller.world;
                var surface = Components<NavMeshSurface>(scene).Single();
                Require(AssetDatabase.GetAssetPath(surface.navMeshData) == StationDecisionGateAuthoring.NavigationPath,
                    "Entrance validation requires the owned saved Daily NavMesh.");
                Require(surface.defaultArea == 1 && surface.useGeometry == NavMeshCollectGeometry.PhysicsColliders,
                    "Entrance validation must preserve physical floor-only navigation.");
                Require(surface.collectObjects == CollectObjects.Volume, "The native street floor must remain inside a bounded collection volume.");
                var collectionBounds = new Bounds(surface.center, surface.size);
                var floor = Components<Collider>(scene).Single(c => c.name == "Pedestrian forecourt");
                var floorModifier = floor.GetComponent<NavMeshModifier>();
                Require(floorModifier && floorModifier.overrideArea && floorModifier.area == 0 && !floorModifier.ignoreFromBuild,
                    "The existing forecourt is not retained as walkable floor.");
                var foundation = Components<Collider>(scene).Single(c => c.name == "Urban ground foundation");
                Require(foundation.enabled && foundation.GetComponent<NavMeshModifier>() && foundation.GetComponent<NavMeshModifier>().ignoreFromBuild,
                    "The buried foundation must retain its physical collider without overriding actual pedestrian navigation.");
                report.subgradeEvidence.Add(Subgrade(foundation, floor, surface));
                Require(controller.portals.Count(p => p.trackNumber == 0) == 5, "Expected four active street zones and the inactive main zone.");
                Require(controller.entranceWeights.Length == 5 && controller.entranceWeights.Select(w => w.entranceId).Distinct().Count() == 5,
                    "The saved weighted portal catalog must contain five unique entries.");
                int closedMask = StationDecisionPlatformGate.DefaultAreaMask(NavMesh.AllAreas);
                Physics.SyncTransforms();
                var allSpawn = new List<Vector3>();
                foreach (var portal in controller.portals.Where(p => p.trackNumber == 0))
                {
                    bool active = InboundIds.Contains(portal.entranceId);
                    Require(active || portal.entranceId == "entry-main", "Unknown street arrival zone.");
                    Require(portal.spawnPoints.Length == (active ? 12 : 6) && portal.spawnPoints.All(t => t), "Missing roadside spawn anchors: " + portal.entranceId);
                    var weight = controller.entranceWeights.Single(w => w.entranceId == portal.entranceId).weight;
                    Require(weight == (active ? 1d : 0d), "Street flow weights must be equal for active zones and zero for main.");
                    var evidence = Portal(portal, controller.entranceWeights); report.portals.Add(evidence);
                    Require(evidence.minimumPointSeparation >= .9f, "Street anchors are not mutually usable: " + portal.entranceId);
                    report.minimumSpawnSeparation = Mathf.Min(report.minimumSpawnSeparation, evidence.minimumPointSeparation);
                    foreach (var marker in portal.spawnPoints)
                    {
                        Require(collectionBounds.Contains(surface.transform.InverseTransformPoint(marker.position)), "Street anchor lies outside the native bake collection volume.");
                        var start = Exact(marker.position, closedMask, report, portal.entranceId);
                        Require(marker.position.z <= -58f && marker.position.z >= -61f, "Street arrival must begin near the curb, far from the doors.");
                        Require(floor.bounds.Contains(new Vector3(marker.position.x, floor.bounds.center.y, marker.position.z)), "Street anchor is outside the actual forecourt slab.");
                        Clear(start, report, portal.entranceId);
                        foreach (var previous in allSpawn) Require(Flat(previous, start) >= .9f, "Different roadside zones share physical spawn space.");
                        allSpawn.Add(start);
                        report.minimumSpawnDistanceFromFacade = Mathf.Min(report.minimumSpawnDistanceFromFacade, Mathf.Abs(start.z - FacadeZ));
                        foreach (var poi in world.pois.Where(p => p))
                        {
                            var gate = poi.activity == DecisionActivity.Platform ? world.platformGates.FirstOrDefault(g => g && g.trackNumber == poi.trackNumber) : null;
                            Path(start, poi.Position, gate ? gate.PlanningMask(closedMask) : closedMask, portal.entranceId + " → " + poi.poiId);
                            ++report.spawnToPoiRoutes;
                        }
                        if (active)
                        {
                            int index = Array.IndexOf(InboundIds, portal.entranceId);
                            Path(start, Exact(new Vector3(InboundX[index], .02f, -29.3f), closedMask, report, "Outside doorway"), closedMask,
                                portal.entranceId + " → corresponding physical mouth");
                            ++report.doorRoutes;
                        }
                    }
                }
                foreach (float x in new[] { -24f, -9f, 0f, 9f, 24f })
                {
                    float clearWidth = x == 0 ? 7.6f : Mathf.Abs(x) == 9 ? 4.1f : 3.6f;
                    var mouth = new MouthEvidence { centerX = x, physicallyClearWidth = clearWidth, physicallyClearHeight = Mathf.Abs(x) == 9 ? 3.5f : 5.3f,
                        newlyOpened = Mathf.Abs(x) == 9, outside = new Vector3(x, .02f, -29.3f), inside = new Vector3(x, .02f, -26.7f) };
                    // Sample the usable width as complete upright body capsules through the facade plane.
                    float half = clearWidth * .5f - BodyRadius - .04f;
                    for (int lane = 0; lane < 5; ++lane)
                    {
                        float laneX = x + Mathf.Lerp(-half, half, lane / 4f);
                        for (int depth = 0; depth < 5; ++depth)
                        {
                            var point = new Vector3(laneX, .02f, Mathf.Lerp(-28.5f, -27.5f, depth / 4f));
                            Clear(point, report, "Facade body throat at x=" + x); ++mouth.crossSectionBodyChecks;
                            Exact(point, closedMask, report, "Facade floor throat at x=" + x);
                        }
                    }
                    mouth.outside = Exact(mouth.outside, closedMask, report, "Outside mouth");
                    mouth.inside = Exact(mouth.inside, closedMask, report, "Inside mouth");
                    Path(mouth.outside, mouth.inside, closedMask, "Open physical mouth x=" + x); ++report.doorRoutes;
                    Path(mouth.inside, mouth.outside, closedMask, "Free reverse mouth x=" + x); ++report.freeExitRoutes;
                    foreach (var poi in world.pois.Where(p => p && (p.activity == DecisionActivity.Display || p.activity == DecisionActivity.Exit)))
                    { Path(mouth.inside, poi.Position, closedMask, "Facade mouth → " + poi.poiId); ++report.doorRoutes; }
                    report.mouths.Add(mouth);
                }
                var exit = world.pois.Single(p => p && p.activity == DecisionActivity.Exit);
                Path(exit.Position, Exact(new Vector3(0, .02f, -60f), closedMask, report, "Central outbound forecourt"), closedMask, "Hall Exit → street forecourt");
                ++report.freeExitRoutes;
                var authored = JsonUtility.FromJson<AuthoringReport>(File.ReadAllText(ReportDirectory + "/entrance-authoring.json"));
                foreach (var before in authored.protectedFiles)
                {
                    var evidence = new FileEvidence { path = before.path, before = before.before, after = Hash(before.path) };
                    report.protectedFiles.Add(evidence);
                    Require(evidence.before == evidence.after, "Street authoring changed a protected asset: " + before.path);
                }
                report.protectedFilesPreserved = true; report.savedSceneClean = !scene.isDirty;
                report.sceneHash = Hash(scene.path); report.navigationHash = Hash(StationDecisionGateAuthoring.NavigationPath);
                report.success = true;
            }
            catch (Exception error) { report.errors.Add(error.ToString()); }
            if (report.minimumSpawnSeparation == float.MaxValue) report.minimumSpawnSeparation = -1;
            if (report.minimumSpawnDistanceFromFacade == float.MaxValue) report.minimumSpawnDistanceFromFacade = -1;
            Write(ReportDirectory + "/entrance-layout-check.json", report);
            Require(report.success, string.Join("\n", report.errors));
        }

        public static void Capture()
        {
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == StationDecisionDayAuthoring.ScenePath, "Entrance capture requires the Daily scene.");
            var camera = Components<Camera>(scene).First(c => c.isActiveAndEnabled && !c.targetTexture);
            var position = camera.transform.position; var rotation = camera.transform.rotation;
            float fov = camera.fieldOfView; bool orthographic = camera.orthographic;
            var texture = camera.targetTexture; var rect = camera.rect; var active = RenderTexture.active;
            var canvases = Components<Canvas>(scene); var enabled = canvases.Select(c => c.enabled).ToArray();
            bool dirty = scene.isDirty; RenderTexture target = null;
            try
            {
                Directory.CreateDirectory(ReportDirectory);
                foreach (var canvas in canvases) canvas.enabled = false;
                target = new RenderTexture(1600, 1000, 24, RenderTextureFormat.ARGB32); target.Create();
                camera.targetTexture = target; camera.rect = new Rect(0, 0, 1, 1); camera.orthographic = false; camera.fieldOfView = 60f;
                string mode = Application.isPlaying ? "live" : "saved";
                var overview = Render(camera, target, new Vector3(0, 19, -92), new Vector3(0, .9f, -32),
                    ReportDirectory + "/entrances-" + mode + "-overview.png");
                camera.fieldOfView = 65f;
                var oblique = Render(camera, target, new Vector3(-45, 15, -76), new Vector3(0, .6f, -43),
                    ReportDirectory + "/entrances-" + mode + "-oblique.png");
                var world = Components<StationDecisionWorld>(scene).Single();
                Write(ReportDirectory + "/capture-" + mode + ".json", new CaptureReport { capturedUtc = DateTime.UtcNow.ToString("O"),
                    scene = scene.path, mode = mode, elapsedSimulationSeconds = Application.isPlaying ? world.ElapsedSeconds : 0f,
                    views = new[] { overview, oblique } });
            }
            finally
            {
                for (int i = 0; i < canvases.Length; ++i) if (canvases[i]) canvases[i].enabled = enabled[i];
                camera.transform.SetPositionAndRotation(position, rotation); camera.fieldOfView = fov; camera.orthographic = orthographic;
                camera.targetTexture = texture; camera.rect = rect; RenderTexture.active = active;
                if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            }
            Require(scene.isDirty == dirty, "Temporary entrance capture changed the saved scene's dirty state.");
        }

        static CameraView Render(Camera camera, RenderTexture target, Vector3 lens, Vector3 aim, string path)
        {
            camera.transform.SetPositionAndRotation(lens, Quaternion.LookRotation(aim - lens, Vector3.up));
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target; var pixels = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            try { pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply(); File.WriteAllBytes(path, pixels.EncodeToPNG()); }
            finally { UnityEngine.Object.DestroyImmediate(pixels); }
            return new CameraView { image = path, position = camera.transform.position, eulerAngles = camera.transform.eulerAngles,
                verticalFieldOfView = camera.fieldOfView, width = target.width, height = target.height,
                worldToCamera = camera.worldToCameraMatrix, projection = camera.projectionMatrix };
        }

        static void OpenIntermediateMouths(Scene scene, Transform root)
        {
            var facade = Components<Transform>(scene).Single(t => t.name == "02 Façade — three open entrance portals");
            var steel = facade.GetComponentsInChildren<Renderer>().First(r => r.name == "Entrance header").sharedMaterial;
            foreach (float x in new[] { -9f, 9f })
            {
                var glazing = facade.Cast<Transform>().Single(t => t.name == "Front glazing" && Mathf.Abs(t.position.x - x) < .1f);
                var mullion = facade.Cast<Transform>().Single(t => t.name == "Window mullion" && Mathf.Abs(t.position.x - x) < .1f);
                var glass = glazing.GetComponent<Renderer>().sharedMaterial;
                glazing.position = new Vector3(x, 5.425f, -28f); glazing.localScale = new Vector3(5f, 3.65f, .14f); Record(glazing);
                mullion.position = new Vector3(x, 5.425f, -28.12f); mullion.localScale = new Vector3(.09f, 3.65f, .18f); Record(mullion);
                var added = Child(root, x < 0 ? "Additional west open doorway" : "Additional east open doorway");
                foreach (int side in new[] { -1, 1 })
                    Box(added, "Folded sliding glass leaf " + side, new Vector3(x + side * 2.25f, 1.8f, -28.08f), new Vector3(.4f, 3.5f, .09f), glass, true);
                Box(added, "New doorway header", new Vector3(x, 3.68f, -28f), new Vector3(4.9f, .16f, .24f), steel, true);
                Label(added, "Additional entrance lettering", "ENTREE " + (x < 0 ? "OUEST" : "EST"), new Vector3(x, 4.05f, -28.22f), 3.1f, Quaternion.identity, new Color(.02f, .11f, .24f));
            }
        }
        static Scene RequireDaily(bool clean)
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play before authoring or validating street arrivals.");
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == StationDecisionDayAuthoring.ScenePath && (!clean || !scene.isDirty), "Open the saved Daily scene; save its entrance/NavMesh changes before validation.");
            return scene;
        }
        static string[] ProtectedPaths() => new[] { SourceScene, ScenePath, StationDecisionDayAuthoring.SchedulePath,
            SchedulePath, StationDecisionLabAmenitiesAuthoring.NavigationPath, "ProjectSettings/NavMeshAreas.asset" };
        static PortalEvidence Portal(StationDecisionSpawnPortal portal, DecisionEntranceWeight[] weights)
        {
            float minimum = float.MaxValue; var points = portal.spawnPoints.Select(t => t.position).ToArray();
            for (int i = 0; i < points.Length; ++i) for (int j = i + 1; j < points.Length; ++j) minimum = Mathf.Min(minimum, Flat(points[i], points[j]));
            return new PortalEvidence { entranceId = portal.entranceId, weight = weights.Single(w => w.entranceId == portal.entranceId).weight,
                spawnPoints = points, minimumPointSeparation = minimum == float.MaxValue ? -1 : minimum };
        }
        static SubgradeEvidence Subgrade(Collider foundation, Collider floor, NavMeshSurface surface) => new SubgradeEvidence
        {
            name = foundation.name, physicalCenter = foundation.bounds.center, physicalSize = foundation.bounds.size,
            physicalTopY = foundation.bounds.max.y, pedestrianFloorTopY = floor.bounds.max.y,
            verticalGap = floor.bounds.max.y - foundation.bounds.max.y, voxelSize = surface.voxelSize,
            physicalColliderRetained = foundation.enabled,
            ignoredFromNavigation = foundation.GetComponent<NavMeshModifier>() && foundation.GetComponent<NavMeshModifier>().ignoreFromBuild
        };
        static Vector3 Exact(Vector3 point, int mask, ValidationReport report, string label)
        {
            ++report.exactFloorChecks;
            Require(NavMesh.SamplePosition(point, out var hit, .18f, mask) && Flat(point, hit.position) <= .18f && Mathf.Abs(hit.position.y - point.y) <= .15f,
                "Position is off its exact saved native floor: " + label + " / " + point);
            return hit.position;
        }
        static void Clear(Vector3 point, ValidationReport report, string label)
        {
            if (report != null) ++report.physicalBodyChecks;
            var blockers = Physics.OverlapCapsule(point + Vector3.up * (BodyRadius + .02f), point + Vector3.up * 1.45f, BodyRadius, ~0, QueryTriggerInteraction.Ignore)
                .Where(c => c && c.enabled && c.gameObject.activeInHierarchy && c.bounds.max.y > point.y + .20f).ToArray();
            Require(blockers.Length == 0, "Physical pedestrian body overlaps " + label + ": " + string.Join(", ", blockers.Select(c => c.name)));
        }
        static void Path(Vector3 from, Vector3 to, int mask, string label)
        {
            var path = new NavMeshPath();
            Require(NavMesh.CalculatePath(from, to, mask, path) && path.status == NavMeshPathStatus.PathComplete, "Incomplete native route: " + label);
        }
        static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
        static Transform Child(Transform parent, string name) { var t = new GameObject(name).transform; t.SetParent(parent, false); return t; }
        static Transform Marker(Transform parent, string name, Vector3 position) { var t = Child(parent, name); t.position = position; return t; }
        static GameObject Box(Transform parent, string name, Vector3 position, Vector3 size, Material material, bool collision)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name; go.transform.SetParent(parent, false);
            go.transform.position = position; go.transform.localScale = size; go.GetComponent<Renderer>().sharedMaterial = material; go.isStatic = true;
            if (!collision) UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }
        static void Label(Transform parent, string name, string text, Vector3 position, float fontSize, Quaternion rotation, Color color)
        {
            // TextMeshPro needs a RectTransform. Creating one up front avoids
            // AddComponent replacing a previously cached plain Transform.
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false); go.transform.position = position; go.transform.rotation = rotation;
            var label = go.AddComponent<TextMeshPro>(); label.font = TMP_Settings.defaultFontAsset;
            label.text = text; label.fontSize = fontSize; label.color = color; label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap; label.overflowMode = TextOverflowModes.Overflow;
            label.rectTransform.sizeDelta = new Vector2(15, 3); label.raycastTarget = false; go.isStatic = true;
        }
        static void Record(UnityEngine.Object target)
        {
            EditorUtility.SetDirty(target);
            if (PrefabUtility.IsPartOfPrefabInstance(target)) PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }
    }
}
#endif
