#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TMPro;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    public static class StationDecisionGateAuthoring
    {
        public const string RootName = "16 Controlled platforms — ticket scanners and free exits";
        public const string NavigationPath = "Assets/TrainStation/DecisionLab/Navigation/Daily_Controlled_Platforms.asset";
        public const string ReportPath = "Reports/TrainStationDaily/ControlledAccess/gate-authoring.json";
        static readonly int[] Tracks = { 2, 4, 5, 7 };
        static readonly int[] Areas = { 3, 5, 6, 7 };

        [Serializable] public sealed class Report
        {
            public string checkedUtc, error, scene, navigation, sourceNavigation, sourceNavigationHash, sourceLabHash, originalSceneHash,
                sceneBackup, areaSettingsBackup, scheduleBackup;
            public bool success, sourceNavigationPreserved, sourceLabPreserved, originalPreserved;
            public int gates, queueAnchors, holdingAnchors, links, blockedIngressRoutes, permittedIngressRoutes, freeExitRoutes,
                rejectedReverseExitRoutes, physicalAnchorChecks, markerPathChecks, platformFloorProbes, floorTriangles, alightingMarkers, migratedServices,
                coachQueues, coachQueueAnchors, coachQueuePaths;
            public List<GateRecord> platforms = new List<GateRecord>();
        }
        [Serializable] public sealed class GateRecord
        { public int track, ingressArea, queuedActorCapacity; public float x, gateZ, exitWidth, scanSecondsPerActor; }

        [MenuItem("Efrei Research Lab/Train Station/Daily/Install ticket gates and coach access")]
        public static void Apply()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play before installing saved platform gates.");
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == StationDecisionDayAuthoring.ScenePath && !scene.isDirty, "Open the clean saved Daily scene before installing gates.");
            Require(!scene.GetRootGameObjects().Any(g => g.name == RootName), "The saved gate installation already exists. Validate it without rebuilding saved changes.");
            Require(!File.Exists(NavigationPath), "The Daily gate NavMesh already exists; installation will not overwrite it.");
            var surface = Components<NavMeshSurface>(scene).Single();
            var world = Components<StationDecisionWorld>(scene).Single();
            Require(world.IsDaily && world.arrivalController, "Platform gates are opt-in to the dated Daily scene.");
            var report = new Report { checkedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path, navigation = NavigationPath,
                sourceNavigation = AssetDatabase.GetAssetPath(surface.navMeshData), sourceLabHash = Hash(StationDecisionLabAuthoring.ScenePath),
                originalSceneHash = Hash(StationDecisionLabAuthoring.SourceScene) };
            Require(!string.IsNullOrEmpty(report.sourceNavigation), "The existing floor-only navigation must be a saved asset.");
            report.sourceNavigationHash = Hash(report.sourceNavigation);
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
            report.sceneBackup = "Reports/TrainStationDaily/ControlledAccess/TrainStationDaily.before-gates-" + stamp + ".unity";
            File.Copy(scene.path, report.sceneBackup, false);
            report.scheduleBackup = "Reports/TrainStationDaily/ControlledAccess/StationDecisionDaySchedule.before-gates-" + stamp + ".asset";
            File.Copy(StationDecisionDayAuthoring.SchedulePath, report.scheduleBackup, false);
            string areasBefore = File.ReadAllText("ProjectSettings/NavMeshAreas.asset");
            report.areaSettingsBackup = "Reports/TrainStationDaily/ControlledAccess/NavMeshAreas.before-gates-" + stamp + ".asset";
            File.WriteAllText(report.areaSettingsBackup, areasBefore);
            bool createdNavigation = false, sceneSaved = false;
            try
            {
                foreach (var service in world.rail.schedule.services)
                    if (service.coachSeatCapacities == null || service.coachSeatCapacities.Length == 0)
                    {
                        service.coachSeatCapacities = new[] { 55, 55, 55, 55 }; service.seatedCapacity = 220;
                        service.platformAccessLeadSeconds = 1200f; ++report.migratedServices;
                    }
                if (report.migratedServices > 0) EditorUtility.SetDirty(world.rail.schedule);
                Require(world.rail.schedule.Validate().Count == 0, "The saved coach composition or access window is invalid: " + string.Join("; ", world.rail.schedule.Validate()));
                ConfigureAreas();
                Require(AssetDatabase.CopyAsset(report.sourceNavigation, NavigationPath), "Could not clone the Lab NavMesh into the Daily-owned asset.");
                createdNavigation = true;
                var ownedClone = AssetDatabase.LoadAssetAtPath<NavMeshData>(NavigationPath);
                surface.RemoveData(); surface.navMeshData = ownedClone; surface.AddData();
                var root = new GameObject(RootName).transform;
                SceneManager.MoveGameObjectToScene(root.gameObject, scene);
                var metal = Material(root, "Existing rail trim", "Assets/TrainStation/RailOperations/Materials/Rail_Trim.mat");
                var accent = Material(root, "Existing blue rail body", "Assets/TrainStation/RailOperations/Materials/Rail_Blue.mat");
                if (!accent) accent = AssetDatabase.LoadAssetAtPath<Material>("Assets/TrainStation/RailOperations/Materials/Rail_Body.mat");
                for (int i = 0; i < Tracks.Length; ++i)
                {
                    float x = -36f + i * 24f;
                    var item = Child(root, "Voie " + Tracks[i] + " — contrôle billets"); item.position = new Vector3(x, 0, 46f);
                    var gate = item.gameObject.AddComponent<StationDecisionPlatformGate>(); gate.world = world;
                    gate.trackNumber = Tracks[i]; gate.ingressArea = Areas[i]; gate.scanSecondsPerActor = 1f;
                    var cut = Child(item, "Floor exclusion — no unscanned shortcut");
                    var volume = cut.gameObject.AddComponent<NavMeshModifierVolume>(); volume.area = 1;
                    volume.center = new Vector3(0, 1f, 0); volume.size = new Vector3(8.6f, 3f, .95f);
                    BuildFence(item, metal);
                    var hinge = Child(item, "Automatic ingress leaf hinge"); hinge.localPosition = new Vector3(-2.15f, .88f, 0);
                    var leaf = Box(hinge, "Automatic scanner leaf", new Vector3(.75f, 0, 0), new Vector3(1.5f, .52f, .07f), accent ? accent : metal);
                    var leafModifier = leaf.gameObject.AddComponent<NavMeshModifier>(); leafModifier.ignoreFromBuild = true;
                    gate.ingressLeaf = hinge; gate.ingressLeafCollider = leaf.GetComponent<Collider>();
                    var light = Box(item, "Scanner status indicator", new Vector3(-2.2f, 1.13f, -.15f), new Vector3(.18f, .12f, .08f), accent ? accent : metal);
                    UnityEngine.Object.DestroyImmediate(light.GetComponent<Collider>()); gate.indicator = light.GetComponent<Renderer>();
                    Label(item, "Access rules", new Vector3(-1.4f, 2.2f, 0), "VOIE " + Tracks[i] + "\nBILLETS · ACCÈS CONTRÔLÉ");
                    Label(item, "Free exit", new Vector3(1.3f, 2.1f, .3f), "SORTIE LIBRE\n↓");
                    gate.hallCrossingPoint = Marker(item, "Ingress hall endpoint", new Vector3(x - 1.4f, .02f, 44.8f));
                    gate.platformCrossingPoint = Marker(item, "Ingress platform endpoint", new Vector3(x - 1.4f, .02f, 47.2f));
                    gate.ingressLink = Link(item, "One-way ticketed ingress", gate.hallCrossingPoint, gate.platformCrossingPoint, .85f, Areas[i], surface.agentTypeID);
                    var exitStart = Marker(item, "Wide free exit — platform", new Vector3(x + 1.3f, .02f, 47.2f));
                    var exitEnd = Marker(item, "Wide free exit — hall", new Vector3(x + 1.3f, .02f, 44.8f));
                    gate.egressLink = Link(item, "One-way free egress — never reversed", exitStart, exitEnd, 2.2f, StationDecisionPlatformGate.EgressArea, surface.agentTypeID);
                    gate.queueAnchors = new Transform[32];
                    for (int q = 0; q < gate.queueAnchors.Length; ++q)
                    {
                        // Two parallel legs form a compact serpentine queue. No queue furniture blocks the exit lane.
                        int row = q < 16 ? q : 31 - q;
                        float queueX = q < 16 ? x - 1.4f : x - 2.5f;
                        gate.queueAnchors[q] = Marker(item, "Queue FIFO " + q.ToString("00"), new Vector3(queueX, .02f, 43.1f - row * .95f));
                    }
                    gate.holdingAnchors = new Transform[64];
                    for (int h = 0; h < gate.holdingAnchors.Length; ++h)
                        gate.holdingAnchors[h] = Marker(item, "Outside access window — actor slot " + h.ToString("00"),
                            new Vector3(x - 11.1f + h % 8 * 1.1f, .02f, 31f + h / 8 * 1.1f));
                    foreach (var actor in world.arrivalController.actorPool)
                        if (actor && actor.motion && actor.motion.agent)
                        { actor.motion.agent.areaMask = StationDecisionPlatformGate.DefaultAreaMask(actor.motion.agent.areaMask); EditorUtility.SetDirty(actor.motion.agent); }
                    EditorUtility.SetDirty(gate);
                    report.platforms.Add(new GateRecord { track = Tracks[i], ingressArea = Areas[i], x = x, gateZ = 46, exitWidth = 2.2f,
                        queuedActorCapacity = gate.queueAnchors.Length, scanSecondsPerActor = gate.scanSecondsPerActor });
                }
                world.platformGates = root.GetComponentsInChildren<StationDecisionPlatformGate>(); EditorUtility.SetDirty(world);
                Physics.SyncTransforms();
                // Bake a replacement of the Daily clone only; no existing scene navigation is updated.
                surface.BuildNavMesh(); Require(surface.navMeshData, "The controlled-platform floor bake returned no data.");
                var baked = surface.navMeshData;
                surface.RemoveData(); surface.navMeshData = ownedClone;
                EditorUtility.CopySerialized(baked, ownedClone); EditorUtility.SetDirty(ownedClone);
                UnityEngine.Object.DestroyImmediate(baked); surface.AddData();
                foreach (var link in root.GetComponentsInChildren<NavMeshLink>()) link.UpdateLink();
                SnapMarkers(root);
                StationDecisionDayRailAuthoring.ConfigureCoachAnchors(world.rail);
                StationDecisionDayRailAuthoring.ConfigureCoachQueues(world.rail, world);
                foreach (var line in world.coachQueues)
                    Label(line.transform, "Visible coach number", line.transform.InverseTransformPoint(line.doorAnchor.position + new Vector3(-1.5f, 2.15f, 0)),
                        "VOITURE " + line.coachNumber);
                ConfigureCoachAlighting(root, world);
                report.floorTriangles = NavMesh.CalculateTriangulation().indices.Length / 3;
                Inspect(scene, world, report);
                report.sourceNavigationPreserved = Hash(report.sourceNavigation) == report.sourceNavigationHash;
                report.sourceLabPreserved = Hash(StationDecisionLabAuthoring.ScenePath) == report.sourceLabHash;
                report.originalPreserved = Hash(StationDecisionLabAuthoring.SourceScene) == report.originalSceneHash;
                Require(report.sourceNavigationPreserved && report.sourceLabPreserved && report.originalPreserved, "Gate authoring changed a preserved source asset.");
                AssetDatabase.SaveAssets(); EditorUtility.SetDirty(surface); EditorSceneManager.MarkSceneDirty(scene);
                Require(EditorSceneManager.SaveScene(scene), "Could not save the Daily gate scene."); sceneSaved = true;
                report.success = true;
            }
            catch (Exception error)
            {
                report.error = error.ToString();
                if (!sceneSaved)
                {
                    EditorSceneManager.OpenScene(StationDecisionDayAuthoring.ScenePath, OpenSceneMode.Single);
                    if (createdNavigation) AssetDatabase.DeleteAsset(NavigationPath);
                    RestoreAreaNames(areasBefore);
                    File.Copy(report.scheduleBackup, StationDecisionDayAuthoring.SchedulePath, true);
                    AssetDatabase.ImportAsset(StationDecisionDayAuthoring.SchedulePath, ImportAssetOptions.ForceUpdate);
                }
                throw;
            }
            finally { File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true)); }
        }

        [MenuItem("Efrei Research Lab/Train Station/Daily/Validate controlled platform paths")]
        public static void Validate()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play before validating the saved gate geometry.");
            var scene = SceneManager.GetActiveScene(); Require(scene.path == StationDecisionDayAuthoring.ScenePath && !scene.isDirty, "Open the clean Daily scene.");
            var report = new Report { checkedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path, navigation = NavigationPath };
            try { Inspect(scene, Components<StationDecisionWorld>(scene).Single(), report); report.success = true; }
            catch (Exception e) { report.error = e.ToString(); throw; }
            finally { Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText("Reports/TrainStationDaily/ControlledAccess/gate-saved-check.json", JsonUtility.ToJson(report, true)); }
        }

        static void Inspect(Scene scene, StationDecisionWorld world, Report report)
        {
            Physics.SyncTransforms();
            var gates = Components<StationDecisionPlatformGate>(scene).Where(g => g.isActiveAndEnabled).OrderBy(g => g.trackNumber).ToArray();
            Require(gates.Length == 4 && gates.Select(g => g.trackNumber).SequenceEqual(Tracks), "There must be exactly one saved gate per operational platform.");
            foreach (var gate in gates)
            {
                ++report.gates; report.queueAnchors += gate.queueAnchors.Length; report.holdingAnchors += gate.holdingAnchors.Length; report.links += 2;
                Require(gate.world == world && gate.ingressLink && gate.egressLink && !gate.ingressLink.bidirectional && !gate.egressLink.bidirectional,
                    "Both gate crossings must be one-way native links.");
                Require(gate.ingressLink.area == gate.ingressArea && gate.egressLink.area == StationDecisionPlatformGate.EgressArea && gate.egressLink.width >= 2f,
                    "Ticketed ingress and the wide free exit must have separate navigation permissions.");
                var platform = world.GetPlatform(gate.trackNumber); Require(platform, "Missing assigned platform POI.");
                int closedMask = StationDecisionPlatformGate.DefaultAreaMask(NavMesh.AllAreas);
                var hall = gate.queueAnchors[0].position;
                Require(!Complete(hall, platform.Position, closedMask), "An unscanned route bypasses the controlled platform: track " + gate.trackNumber);
                ++report.blockedIngressRoutes;
                Require(Complete(hall, platform.Position, gate.PlanningMask(closedMask)), "The permitted scanner link does not reach its platform."); ++report.permittedIngressRoutes;
                Require(Complete(platform.Position, world.pois.First(p => p.activity == DecisionActivity.Exit).Position, closedMask), "Alighters cannot use the free exit."); ++report.freeExitRoutes;
                Require(!Complete(gate.egressLink.endTransform.position, gate.egressLink.startTransform.position, 1 | 1 << StationDecisionPlatformGate.EgressArea),
                    "The free exit can be traversed backwards without a ticket."); ++report.rejectedReverseExitRoutes;
                foreach (var other in gates.Where(g => g != gate))
                    Require(!Complete(hall, world.GetPlatform(other.trackNumber).Position, gate.PlanningMask(closedMask)), "A ticket permission opens another platform.");
                foreach (var anchor in gate.queueAnchors.Concat(gate.holdingAnchors))
                {
                    ExactFloor(anchor.position);
                    Require(Physics.OverlapCapsule(anchor.position + Vector3.up * .4f, anchor.position + Vector3.up * 1.5f, .27f, ~0,
                        QueryTriggerInteraction.Ignore).Length == 0, "A queue or holding anchor intersects physical furniture: " + anchor.name); ++report.physicalAnchorChecks;
                    Require(Complete(anchor.position, hall, closedMask), "A hall-side queue marker cannot reach the scanner."); ++report.markerPathChecks;
                }
                foreach (var coach in world.rail.trains.Single(t => t.trackNumber == gate.trackNumber).coachBoardingAnchors)
                {
                    Require(Complete(gate.platformCrossingPoint.position, coach.position, closedMask), "A scanned traveller cannot walk to its saved coach door."); ++report.markerPathChecks;
                    Require(Complete(coach.position, hall, closedMask), "An alighter cannot walk from its coach to the free exit."); ++report.markerPathChecks;
                    CheckPlatformFloorPath(gate.platformCrossingPoint.position, coach.position, closedMask, report);
                }
                var coachLines = world.coachQueues.Where(q => q && q.trackNumber == gate.trackNumber).OrderBy(q => q.coachNumber).ToArray();
                Require(coachLines.Length == 4 && coachLines.Select(q => q.coachNumber).SequenceEqual(new[] { 1, 2, 3, 4 }), "Each saved coach needs its own boarding FIFO.");
                foreach (var line in coachLines)
                {
                    ++report.coachQueues;
                    Require(line.world == world && line.doorAnchor && line.queueAnchors.Length == 12 && line.queueAnchors[0] == line.doorAnchor,
                        "Coach FIFO head must be the assigned coach door.");
                    foreach (var anchor in line.queueAnchors)
                    {
                        ExactFloor(anchor.position);
                        Require(Physics.OverlapCapsule(anchor.position + Vector3.up * .4f, anchor.position + Vector3.up * 1.5f, .27f, ~0,
                            QueryTriggerInteraction.Ignore).Length == 0, "A coach FIFO anchor intersects physical furniture: " + line.name + " / " + anchor.name);
                        ++report.physicalAnchorChecks; ++report.coachQueueAnchors;
                        Require(Complete(gate.platformCrossingPoint.position, anchor.position, closedMask), "A scanned traveller cannot reach its coach FIFO position."); ++report.coachQueuePaths;
                        Require(Complete(anchor.position, hall, closedMask), "A coach FIFO position blocks the free alighting exit."); ++report.coachQueuePaths;
                    }
                }
                var portal = world.arrivalController.portals.Single(p => p.trackNumber == gate.trackNumber);
                Require(portal.spawnPointsPerCoach == 6 && portal.spawnPoints.Length == 24, "Each coach needs six independent alighting floor markers.");
                foreach (var anchor in portal.spawnPoints)
                {
                    ExactFloor(anchor.position);
                    Require(Physics.OverlapCapsule(anchor.position + Vector3.up * .4f, anchor.position + Vector3.up * 1.5f, .27f, ~0,
                        QueryTriggerInteraction.Ignore).Length == 0, "A coach alighting marker intersects furniture."); ++report.physicalAnchorChecks;
                    Require(Complete(anchor.position, hall, closedMask), "A coach alighting marker cannot reach the free exit."); ++report.markerPathChecks; ++report.alightingMarkers;
                }
            }
        }

        static void ConfigureCoachAlighting(Transform root, StationDecisionWorld world)
        {
            foreach (var portal in world.arrivalController.portals.Where(p => p.trackNumber > 0))
            {
                var train = world.rail.trains.Single(t => t.trackNumber == portal.trackNumber);
                Require(train.coachBoardingAnchors.Length == 4, "The Daily four-car train needs four saved coach door anchors.");
                var parent = Child(root, "Voie " + portal.trackNumber + " — alighting at each coach door");
                var markers = new List<Transform>();
                for (int coach = 0; coach < train.coachBoardingAnchors.Length; ++coach)
                {
                    Vector3 door = train.coachBoardingAnchors[coach].position;
                    var candidates = new List<Vector3>();
                    for (int row = 0; row < 3; ++row)
                        foreach (float side in new[] { .65f, 1.8f }) candidates.Add(door + new Vector3(side, 0, 1.5f + row * 1.1f));
                    // A bench may be near a particular door; use only other strictly clear floor points beside that same coach.
                    foreach (float z in new[] { -.8f, -1.9f, -3f, -4.1f, 4.8f, .35f })
                        foreach (float x in new[] { .65f, 1.8f, 2.9f }) candidates.Add(door + new Vector3(x, 0, z));
                    var selected = new List<Vector3>();
                    foreach (var candidate in candidates)
                    {
                        if (selected.Count == 6) break;
                        if (!NavMesh.SamplePosition(candidate, out var hit, .18f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - door.y) > .15f ||
                            Vector2.Distance(new Vector2(candidate.x, candidate.z), new Vector2(hit.position.x, hit.position.z)) > .18f ||
                            selected.Any(p => Vector3.Distance(p, hit.position) < .95f) ||
                            Physics.OverlapCapsule(hit.position + Vector3.up * .4f, hit.position + Vector3.up * 1.5f, .27f, ~0, QueryTriggerInteraction.Ignore).Length != 0) continue;
                        selected.Add(hit.position);
                    }
                    Require(selected.Count == 6, "Coach " + (coach + 1) + " has fewer than six clear alighting markers on track " + portal.trackNumber);
                    for (int slot = 0; slot < selected.Count; ++slot)
                        markers.Add(Marker(parent, "Coach " + (coach + 1) + " alighting " + slot, selected[slot]));
                }
                foreach (var old in portal.spawnPoints) if (old) UnityEngine.Object.DestroyImmediate(old.gameObject);
                portal.spawnPointsPerCoach = 6; portal.spawnPoints = markers.ToArray();
            }
            EditorUtility.SetDirty(world.arrivalController);
        }

        static void ConfigureAreas()
        {
            var settings = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("NavMeshProjectSettings"));
            var areas = settings.FindProperty("areas"); Require(areas != null && areas.arraySize >= 8, "Unity navigation area settings are unavailable.");
            foreach (var pair in AreaNames())
            {
                var item = areas.GetArrayElementAtIndex(pair.Key); var name = item.FindPropertyRelative("name");
                Require(string.IsNullOrEmpty(name.stringValue) || name.stringValue == pair.Value, "Required navigation area is already used: " + pair.Key);
                name.stringValue = pair.Value; item.FindPropertyRelative("cost").floatValue = 1f;
            }
            settings.ApplyModifiedPropertiesWithoutUndo();
            foreach (var pair in AreaNames()) Require(NavMesh.GetAreaFromName(pair.Value) == pair.Key, "Navigation area registration failed: " + pair.Value);
        }
        static Dictionary<int, string> AreaNames() => new Dictionary<int, string> { { 3, "Daily Platform 2 Ingress" }, { 4, "Daily Free Egress" },
            { 5, "Daily Platform 4 Ingress" }, { 6, "Daily Platform 5 Ingress" }, { 7, "Daily Platform 7 Ingress" } };
        static void RestoreAreaNames(string previous)
        {
            var names = System.Text.RegularExpressions.Regex.Matches(previous, "(?m)^  - name: (.*)$");
            var settings = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("NavMeshProjectSettings"));
            var areas = settings.FindProperty("areas");
            foreach (var pair in AreaNames())
                if (pair.Key < names.Count) areas.GetArrayElementAtIndex(pair.Key).FindPropertyRelative("name").stringValue = names[pair.Key].Groups[1].Value.Trim();
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        static void BuildFence(Transform root, Material material)
        {
            foreach (var segment in new[] { new Vector2(-3.95f, -2.15f), new Vector2(-.65f, -.2f), new Vector2(2.8f, 3.95f) })
            {
                float middle = (segment.x + segment.y) * .5f, length = segment.y - segment.x;
                Box(root, "Fence top rail", new Vector3(middle, 1.1f, 0), new Vector3(length, .08f, .12f), material);
                Box(root, "Fence lower rail", new Vector3(middle, .35f, 0), new Vector3(length, .07f, .12f), material);
                for (float x = segment.x; x <= segment.y + .01f; x += .3f)
                    Box(root, "Fence upright", new Vector3(x, .6f, 0), new Vector3(.055f, 1.2f, .07f), material);
            }
            foreach (float x in new[] { -2.2f, -.6f, -.2f, 2.8f }) Box(root, "Gate pedestal", new Vector3(x, .57f, 0), new Vector3(.22f, 1.14f, .55f), material);
        }
        static Material Material(Transform root, string description, string path) => AssetDatabase.LoadAssetAtPath<Material>(path);
        static Transform Box(Transform parent, string name, Vector3 local, Vector3 scale, Material material)
        {
            var item = GameObject.CreatePrimitive(PrimitiveType.Cube); item.name = name; item.transform.SetParent(parent, false);
            item.transform.localPosition = local; item.transform.localScale = scale; if (material) item.GetComponent<Renderer>().sharedMaterial = material;
            return item.transform;
        }
        static void Label(Transform parent, string name, Vector3 local, string text)
        {
            var item = new GameObject(name, typeof(RectTransform)); item.transform.SetParent(parent, false); item.transform.localPosition = local;
            var label = item.AddComponent<TextMeshPro>(); label.text = text; label.fontSize = 1.6f; label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white; label.rectTransform.sizeDelta = new Vector2(7.5f, 1.25f); label.transform.localRotation = Quaternion.Euler(0, 180, 0);
        }
        static Transform Marker(Transform parent, string name, Vector3 worldPosition)
        { var item = Child(parent, name); item.position = worldPosition; return item; }
        static Transform Child(Transform parent, string name)
        { var item = new GameObject(name).transform; item.SetParent(parent, false); return item; }
        static NavMeshLink Link(Transform parent, string name, Transform start, Transform end, float width, int area, int agentType)
        {
            var link = Child(parent, name).gameObject.AddComponent<NavMeshLink>(); link.agentTypeID = agentType;
            link.startTransform = start; link.endTransform = end; link.width = width; link.area = area; link.bidirectional = false;
            link.autoUpdate = false; return link;
        }
        static void SnapMarkers(Transform root)
        {
            foreach (var gate in root.GetComponentsInChildren<StationDecisionPlatformGate>())
            {
                var queueCandidates = gate.queueAnchors.Select(t => t.position).ToList();
                for (int row = 0; row < 16; ++row)
                for (int column = 0; column < 3; ++column)
                    queueCandidates.Add(new Vector3(gate.transform.position.x - 1.4f - column * 1.1f, .02f, 43.1f - row * .95f));
                var safeQueue = new List<Vector3>();
                // Preserve the physical scanner head. Only tail slots may move around a column or bench.
                safeQueue.Add(ExactFloor(gate.queueAnchors[0].position));
                foreach (var point in queueCandidates.Skip(1))
                {
                    if (safeQueue.Count == gate.queueAnchors.Length) break;
                    if (!NavMesh.SamplePosition(point, out var hit, .18f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - .02f) > .15f ||
                        Vector2.Distance(new Vector2(point.x, point.z), new Vector2(hit.position.x, hit.position.z)) > .18f ||
                        safeQueue.Any(p => Vector3.Distance(p, hit.position) < .949f) ||
                        Physics.OverlapCapsule(hit.position + Vector3.up * .4f, hit.position + Vector3.up * 1.5f, .27f, ~0, QueryTriggerInteraction.Ignore).Length != 0) continue;
                    safeQueue.Add(hit.position);
                }
                Require(safeQueue.Count == gate.queueAnchors.Length, "The hall-side floor cannot fit the independent ticket queue markers.");
                for (int i = 0; i < safeQueue.Count; ++i) gate.queueAnchors[i].position = safeQueue[i];
                var candidates = gate.holdingAnchors.Select(t => t.position).ToList();
                for (int row = 0; row < 13; ++row)
                for (int column = 0; column < 9; ++column)
                    candidates.Add(new Vector3(gate.transform.position.x - 11.1f + column * 1.1f, .02f, 28.9f + row * 1.1f));
                var clear = new List<Vector3>();
                foreach (var point in candidates)
                {
                    if (clear.Count == gate.holdingAnchors.Length) break;
                    if (!NavMesh.SamplePosition(point, out var hit, .18f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - .02f) > .15f ||
                        Vector2.Distance(new Vector2(point.x, point.z), new Vector2(hit.position.x, hit.position.z)) > .18f ||
                        clear.Any(p => Vector3.Distance(p, hit.position) < .95f) || safeQueue.Any(p => Vector3.Distance(p, hit.position) < .95f) ||
                        Physics.OverlapCapsule(hit.position + Vector3.up * .4f, hit.position + Vector3.up * 1.5f, .27f, ~0, QueryTriggerInteraction.Ignore).Length != 0) continue;
                    clear.Add(hit.position);
                }
                Require(clear.Count == gate.holdingAnchors.Length, "The concourse cannot fit the independent outside-window holding markers.");
                for (int i = 0; i < clear.Count; ++i) gate.holdingAnchors[i].position = clear[i];
                foreach (var marker in gate.queueAnchors.Concat(gate.holdingAnchors).Concat(new[] { gate.hallCrossingPoint, gate.platformCrossingPoint,
                    gate.egressLink.startTransform, gate.egressLink.endTransform })) marker.position = ExactFloor(marker.position);
            }
            foreach (var link in root.GetComponentsInChildren<NavMeshLink>()) link.UpdateLink();
        }
        static Vector3 ExactFloor(Vector3 point)
        {
            Require(NavMesh.SamplePosition(point, out var hit, .18f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - .02f) < .15f &&
                Vector2.Distance(new Vector2(point.x, point.z), new Vector2(hit.position.x, hit.position.z)) < .18f,
                "Gate anchor is not on its exact safe floor: " + point); return hit.position;
        }
        static bool Complete(Vector3 from, Vector3 to, int mask)
        { var path = new NavMeshPath(); return NavMesh.CalculatePath(from, to, mask, path) && path.status == NavMeshPathStatus.PathComplete; }
        static void CheckPlatformFloorPath(Vector3 from, Vector3 to, int mask, Report report)
        {
            var path = new NavMeshPath();
            Require(NavMesh.CalculatePath(from, to, mask, path) && path.status == NavMeshPathStatus.PathComplete, "The coach platform route is incomplete.");
            for (int segment = 1; segment < path.corners.Length; ++segment)
            {
                var start = path.corners[segment - 1]; var end = path.corners[segment];
                int samples = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(start, end)));
                for (int sample = 0; sample <= samples; ++sample)
                {
                    Vector3 point = Vector3.Lerp(start, end, (float)sample / samples);
                    Require(NavMesh.SamplePosition(point, out var hit, .18f, 1) && Mathf.Abs(hit.position.y - .02f) < .15f &&
                        Vector2.Distance(new Vector2(point.x, point.z), new Vector2(hit.position.x, hit.position.z)) < .18f,
                        "A coach route leaves the protected floor while passing retained platform furniture: " + point);
                    ++report.platformFloorProbes;
                }
            }
        }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static string Hash(string path) { using var algorithm = SHA256.Create(); return BitConverter.ToString(algorithm.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
    }
}
#endif
