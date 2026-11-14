#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using StationFlow.TrainStation.RailOperations;
using StationFlow.TrainStation.Social;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    public static class StationDecisionDayAuthoring
    {
        public const string ScenePath = "Assets/TrainStation/Scenes/TrainStationDaily.unity";
        public const string ScenarioPath = "Assets/TrainStation/DecisionLab/Resources/StationDecisionDayScenario.asset";
        public const string SchedulePath = "Assets/TrainStation/DecisionLab/Resources/StationDecisionDaySchedule.asset";

        [Serializable] sealed class Receipt
        {
            public string createdUtc, scene, sourceLabHash, originalSceneHash, savedSceneHash;
            public bool success, labPreserved, originalPreserved;
            public int poolSlots, entrancePortals, platformPortals, safeSpawnPoints, completeRoutes;
        }

        [Serializable] sealed class DensityReceipt
        {
            public string configuredUtc, scene, sceneHashBefore, sceneHashAfter, scheduleHashBefore, scheduleHashAfter,
                navigationHashBefore, navigationHashAfter;
            public bool success, schedulePreserved, navigationPreserved;
            public float visualActorRatioBefore, visualActorRatioAfter;
            public float toiletMeanDurationBefore, toiletMeanDurationAfter, toiletDurationSpreadBefore, toiletDurationSpreadAfter;
            public int poolSlotsBefore, poolSlotsAfter, journeysBefore, journeysAfter,
                representedBoardingBefore, representedBoardingAfter, representedAlightingBefore, representedAlightingAfter;
            public bool toiletInsideVestibuleAvailable;
            public string toiletUseLocation, toiletInsideProbeIssue;
            public int toiletQueueSlots, toiletUseCapacity, toiletPhysicalMarkerChecks, toiletPairSeparationChecks, toiletPathChecks, toiletOccupiedRouteClearanceChecks;
            public Vector3[] toiletQueuePoints, toiletUsePoints;
            public Vector3 toiletExitPoint;
        }

        [Serializable] sealed class ToiletFloorProbe
        {
            public string checkedUtc, scene, sceneHashBefore, sceneHashAfter, navigationHashBefore, navigationHashAfter;
            public bool savedScenePreserved, navigationPreserved, sceneCleanBefore, sceneCleanAfter;
            public Vector3 poiPosition;
            public System.Collections.Generic.List<ToiletFloorCandidate> candidates = new System.Collections.Generic.List<ToiletFloorCandidate>();
        }
        [Serializable] sealed class ToiletFloorCandidate
        {
            public Vector3 requested, sampledPosition, nearestDiagnosticPosition;
            public bool exactFloor, physicalClear, nativeRouteFromPoi, nativeRouteFromAllEntrances, nearestDiagnosticFound;
            public float floorSnapDistance, nearestDiagnosticDistance;
            public string issue;
        }

        public static void ProbeToiletFloor()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && SceneManager.GetActiveScene().path == ScenePath,
                "Open the saved daily scene in Edit mode before probing WC floor geometry.");
            var scene = SceneManager.GetActiveScene();
            var controller = Components<StationDecisionArrivalController>(scene).Single();
            var toilet = controller.world.pois.Single(p => p && p.activity == DecisionActivity.Toilet);
            var report = new ToiletFloorProbe { checkedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path,
                sceneHashBefore = Hash(ScenePath), navigationHashBefore = Hash(StationDecisionGateAuthoring.NavigationPath),
                sceneCleanBefore = !scene.isDirty, poiPosition = toilet.Position };
            Physics.SyncTransforms();
            int mask = StationDecisionPlatformGate.DefaultAreaMask(NavMesh.AllAreas);
            var entrancePositions = controller.portals.Where(p => p.trackNumber == 0).Select(p => p.spawnPoints[p.spawnPoints.Length / 2].position).ToArray();
            for (int xi = 0; xi <= 17; ++xi)
            for (int zi = 0; zi <= 24; ++zi)
            {
                var requested = new Vector3(16.5f + xi * .5f, 0f, -26f + zi * .5f);
                var candidate = new ToiletFloorCandidate { requested = requested };
                candidate.exactFloor = NavMesh.SamplePosition(requested, out var hit, .18f, mask) && Mathf.Abs(hit.position.y - .02f) < .15f;
                if (candidate.exactFloor)
                {
                    candidate.sampledPosition = hit.position; candidate.floorSnapDistance = Vector3.Distance(requested, hit.position);
                    candidate.physicalClear = TryToiletPoint(requested, out var snapped, out string issue);
                    candidate.issue = issue;
                    candidate.nativeRouteFromPoi = ToiletRoute(toilet.Position, hit.position);
                    candidate.nativeRouteFromAllEntrances = entrancePositions.All(p => ToiletRoute(p, hit.position));
                }
                else candidate.issue = "No exact saved floor NavMesh within 0.18 m.";
                candidate.nearestDiagnosticFound = NavMesh.SamplePosition(requested, out var nearest, 2.5f, mask);
                if (candidate.nearestDiagnosticFound)
                { candidate.nearestDiagnosticPosition = nearest.position; candidate.nearestDiagnosticDistance = Vector3.Distance(requested, nearest.position); }
                report.candidates.Add(candidate);
            }
            report.sceneHashAfter = Hash(ScenePath); report.navigationHashAfter = Hash(StationDecisionGateAuthoring.NavigationPath);
            report.savedScenePreserved = report.sceneHashBefore == report.sceneHashAfter;
            report.navigationPreserved = report.navigationHashBefore == report.navigationHashAfter; report.sceneCleanAfter = !scene.isDirty;
            Directory.CreateDirectory("Reports/TrainStationDaily/DensityAndActivities");
            File.WriteAllText("Reports/TrainStationDaily/DensityAndActivities/toilet-layout-probe.json", JsonUtility.ToJson(report, true));
            Require(report.savedScenePreserved && report.navigationPreserved && report.sceneCleanBefore == report.sceneCleanAfter,
                "Read-only WC probing unexpectedly changed a saved file or scene dirty state.");
        }

        [MenuItem("Efrei Research Lab/Train Station/Daily/Set visible passenger density to twenty percent")]
        public static void ConfigureVisibleDensity()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && SceneManager.GetActiveScene().path == ScenePath && !SceneManager.GetActiveScene().isDirty,
                "Open the clean saved daily scene in Edit mode before configuring visible density.");
            var scene = SceneManager.GetActiveScene();
            var controller = Components<StationDecisionArrivalController>(scene).Single();
            var world = Components<StationDecisionWorld>(scene).Single();
            var scenario = controller.scenario;
            Require(controller.world == world && scenario && AssetDatabase.GetAssetPath(scenario) == ScenarioPath && world.rail && world.rail.schedule,
                "The saved daily scene must own its scenario, world and schedule bindings.");
            var templates = controller.actorPool;
            Require(templates != null && (templates.Length == 64 || templates.Length == 128) && templates.Distinct().Count() == templates.Length &&
                templates.All(p => p && !p.gameObject.activeInHierarchy && p.motion && p.passenger && p.world == world) &&
                world.participants.SequenceEqual(templates), "Density expansion requires a complete inactive actor pool.");
            var poolRoot = templates[0].transform.parent;
            Require(poolRoot && templates.All(p => p.transform.parent == poolRoot), "Saved pool actors must share their own inactive slot root.");
            var services = world.rail.schedule.services.Select(s => new DecisionArrivalService { serviceId = s.serviceId, trainNumber = s.trainNumber,
                trackNumber = s.trackNumber, arrivalSeconds = s.arrivalOffsetSeconds, departureSeconds = s.departureOffsetSeconds,
                doorSeconds = s.doorDurationSeconds, expectedBoarding = s.expectedBoarding, expectedAlighting = s.expectedAlighting,
                coachSeatCapacities = s.coachSeatCapacities, throughPassengers = s.throughPassengers }).ToArray();
            var entrances = controller.portals.Where(p => p.trackNumber == 0).Select(p => p.entranceId).ToArray();
            var beforeSettings = scenario.PlannerSettings(); beforeSettings.scenarioDate = world.rail.schedule.operatingDateIso;
            beforeSettings.entranceWeights = controller.entranceWeights;
            var beforePlan = DecisionArrivalPlanner.Generate(services, entrances, beforeSettings);
            var toilets = world.pois.Where(p => p && p.activity == DecisionActivity.Toilet).ToArray();
            Require(toilets.Length == 1 && !toilets[0].service, "The saved daily WC must be a single duration-based activity.");
            var toilet = toilets[0];
            var schedulePath = AssetDatabase.GetAssetPath(world.rail.schedule);
            var report = new DensityReceipt { configuredUtc = DateTime.UtcNow.ToString("O"), scene = ScenePath,
                sceneHashBefore = Hash(ScenePath), scheduleHashBefore = Hash(schedulePath),
                navigationHashBefore = Hash(StationDecisionGateAuthoring.NavigationPath), visualActorRatioBefore = scenario.visualActorRatio,
                toiletMeanDurationBefore = toilet.meanDurationSeconds, toiletDurationSpreadBefore = toilet.durationSpreadSeconds,
                poolSlotsBefore = templates.Length, journeysBefore = beforePlan.Count,
                representedBoardingBefore = beforePlan.Where(p => p.purpose == DecisionJourneyPurpose.Boarding).Sum(p => p.representedPassengers),
                representedAlightingBefore = beforePlan.Where(p => p.purpose == DecisionJourneyPurpose.Alighting).Sum(p => p.representedPassengers) };
            ConfigureToiletQueue(scene, world, controller, toilet, report);
            var expandedPool = templates.ToList();
            for (int i = templates.Length; i < 128; ++i)
            {
                var clone = UnityEngine.Object.Instantiate(templates[i % templates.Length].gameObject, poolRoot);
                clone.name = "Actor slot " + i.ToString("000"); clone.SetActive(false);
                var actor = clone.GetComponent<StationDecisionPassenger>();
                actor.world = world; actor.enabled = true;
                actor.passenger.externalActivityControl = true; actor.passenger.automaticRequests = false;
                if (actor.intentLabel) actor.intentLabel.enabled = false;
                expandedPool.Add(actor);
            }
            controller.actorPool = expandedPool.ToArray(); world.participants = controller.actorPool;
            scenario.visualActorRatio = .2f; scenario.activeActorLimit = 128;
            toilet.meanDurationSeconds = 180f; toilet.durationSpreadSeconds = 60f;
            report.toiletMeanDurationAfter = toilet.meanDurationSeconds; report.toiletDurationSpreadAfter = toilet.durationSpreadSeconds;
            var afterSettings = scenario.PlannerSettings(); afterSettings.scenarioDate = world.rail.schedule.operatingDateIso;
            afterSettings.entranceWeights = controller.entranceWeights;
            var afterPlan = DecisionArrivalPlanner.Generate(services, entrances, afterSettings);
            report.visualActorRatioAfter = scenario.visualActorRatio; report.poolSlotsAfter = controller.actorPool.Length; report.journeysAfter = afterPlan.Count;
            report.representedBoardingAfter = afterPlan.Where(p => p.purpose == DecisionJourneyPurpose.Boarding).Sum(p => p.representedPassengers);
            report.representedAlightingAfter = afterPlan.Where(p => p.purpose == DecisionJourneyPurpose.Alighting).Sum(p => p.representedPassengers);
            Require(report.representedBoardingBefore == report.representedBoardingAfter && report.representedAlightingBefore == report.representedAlightingAfter,
                "Increasing visible density must preserve the exact real passenger demand.");
            EditorUtility.SetDirty(controller); EditorUtility.SetDirty(world); EditorUtility.SetDirty(scenario); EditorUtility.SetDirty(toilet);
            EditorSceneManager.MarkSceneDirty(scene);
            Require(EditorSceneManager.SaveScene(scene, ScenePath), "Could not save the expanded daily actor pool.");
            AssetDatabase.SaveAssets();
            report.sceneHashAfter = Hash(ScenePath); report.scheduleHashAfter = Hash(schedulePath);
            report.navigationHashAfter = Hash(StationDecisionGateAuthoring.NavigationPath);
            report.schedulePreserved = report.scheduleHashBefore == report.scheduleHashAfter;
            report.navigationPreserved = report.navigationHashBefore == report.navigationHashAfter;
            report.success = report.schedulePreserved && report.navigationPreserved;
            Directory.CreateDirectory("Reports/TrainStationDaily/DensityAndActivities");
            File.WriteAllText("Reports/TrainStationDaily/DensityAndActivities/density-authoring.json", JsonUtility.ToJson(report, true));
            Require(report.success, "A schedule or navigation asset changed during density expansion.");
        }

        static void ConfigureToiletQueue(Scene scene, StationDecisionWorld world, StationDecisionArrivalController controller,
            StationDecisionPoi toilet, DensityReceipt report)
        {
            Physics.SyncTransforms();
            var queuePoints = new Vector3[12];
            for (int i = 0; i < 6; ++i)
            {
                queuePoints[i] = SnapToiletPoint(new Vector3(19.4f, 0f, -17.8f - i * .95f), report);
                queuePoints[6 + i] = SnapToiletPoint(new Vector3(18.3f, 0f, -22.55f + i * .95f), report);
            }
            var exit = SnapToiletPoint(new Vector3(18f, 0f, -16f), report);
            var inside = new[] { new Vector3(23f, 0f, -18.7f), new Vector3(23f, 0f, -17.3f) };
            var usePoints = new Vector3[2];
            bool insideAvailable = true;
            for (int i = 0; i < inside.Length; ++i)
            {
                if (!TryToiletPoint(inside[i], out usePoints[i], out string issue) || !ToiletRoute(queuePoints[0], usePoints[i]) || !ToiletRoute(usePoints[i], exit))
                {
                    insideAvailable = false;
                    report.toiletInsideProbeIssue += "Use " + i + ": " + (string.IsNullOrEmpty(issue) ? "Incomplete saved native path." : issue) + " ";
                }
            }
            report.toiletInsideVestibuleAvailable = insideAvailable;
            if (!insideAvailable)
                for (int i = 0; i < usePoints.Length; ++i)
                    usePoints[i] = SnapToiletPoint(new Vector3(21f, 0f, i == 0 ? -18.7f : -17.3f), report);
            else report.toiletPhysicalMarkerChecks += usePoints.Length;
            report.toiletUseLocation = insideAvailable ?
                "Two timed uses inside the accessible WC entrance vestibule on the saved native floor; no individual cubicle interaction." :
                "Two timed uses at the accessible WC entrance before the door; the saved native floor did not support both inside vestibule candidates.";
            var allPoints = queuePoints.Concat(usePoints).Concat(new[] { exit }).ToArray();
            for (int i = 0; i < allPoints.Length; ++i)
            for (int j = i + 1; j < allPoints.Length; ++j)
            { Require(Vector3.Distance(allPoints[i], allPoints[j]) >= .70f, "WC marker bodies overlap: " + i + "/" + j); ++report.toiletPairSeparationChecks; }
            foreach (var from in queuePoints)
            foreach (var use in usePoints) CheckToiletRoute(from, use, report);
            foreach (var use in usePoints) CheckToiletRoute(use, exit, report);
            for (int i = 0; i < usePoints.Length; ++i)
            {
                var otherUse = usePoints.Where((p, index) => index != i).ToArray();
                CheckToiletClearancePath(queuePoints[0], usePoints[i], queuePoints.Skip(1).Concat(otherUse).ToArray(), report);
                CheckToiletClearancePath(usePoints[i], exit, queuePoints.Concat(otherUse).ToArray(), report);
            }
            for (int i = 1; i < queuePoints.Length; ++i) CheckToiletRoute(queuePoints[i], queuePoints[i - 1], report);
            foreach (var portal in controller.portals.Where(p => p.trackNumber == 0))
            foreach (var point in portal.spawnPoints) CheckToiletRoute(point.position, queuePoints[queuePoints.Length - 1], report);
            foreach (var poi in world.pois.Where(p => p && p != toilet && p.activity != DecisionActivity.Platform)) CheckToiletRoute(exit, poi.Position, report);
            foreach (var gate in world.platformGates.Where(g => g && g.queueAnchors.Length > 0)) CheckToiletRoute(exit, gate.queueAnchors[0].position, report);
            const string rootName = "17 Daily WC — two uses and physical arrival FIFO";
            var root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == rootName)?.transform;
            if (!root)
            {
                root = new GameObject(rootName).transform;
                SceneManager.MoveGameObjectToScene(root.gameObject, scene);
            }
            var queue = toilet.GetComponent<StationDecisionToiletQueue>();
            if (!queue) queue = toilet.gameObject.AddComponent<StationDecisionToiletQueue>();
            queue.world = world; queue.poi = toilet;
            queue.queueSlots = queuePoints.Select((p, i) => ToiletMarker(root, "WC arrival FIFO slot " + i.ToString("00"), p)).ToArray();
            queue.usePoints = usePoints.Select((p, i) => ToiletMarker(root, "WC timed use " + (i + 1), p)).ToArray();
            queue.exitPoint = ToiletMarker(root, "WC departure — clear of the waiting head", exit);
            toilet.toiletQueue = queue; toilet.departurePoint = queue.exitPoint; toilet.capacity = queue.usePoints.Length; toilet.spreadRadius = 0f;
            EditorUtility.SetDirty(queue);
            report.toiletQueueSlots = queue.queueSlots.Length; report.toiletUseCapacity = queue.usePoints.Length;
            report.toiletQueuePoints = queuePoints; report.toiletUsePoints = usePoints; report.toiletExitPoint = exit;
        }

        static Transform ToiletMarker(Transform parent, string name, Vector3 point)
        {
            var marker = parent.Find(name);
            if (!marker) { marker = new GameObject(name).transform; marker.SetParent(parent, false); }
            marker.position = point; EditorUtility.SetDirty(marker); return marker;
        }
        static Vector3 SnapToiletPoint(Vector3 point, DensityReceipt report)
        {
            Require(TryToiletPoint(point, out var snapped, out string issue), "WC marker is not physically usable: " + point + " / " + issue);
            ++report.toiletPhysicalMarkerChecks; return snapped;
        }
        static bool TryToiletPoint(Vector3 point, out Vector3 snapped, out string issue)
        {
            snapped = default; issue = "";
            int mask = StationDecisionPlatformGate.DefaultAreaMask(NavMesh.AllAreas);
            if (!NavMesh.SamplePosition(point, out var hit, .18f, mask) || Mathf.Abs(hit.position.y - .02f) >= .15f)
            { issue = "No exact saved floor NavMesh at the requested position."; return false; }
            var blockers = Physics.OverlapCapsule(hit.position + Vector3.up * .35f, hit.position + Vector3.up * 1.75f, .27f, ~0, QueryTriggerInteraction.Ignore)
                .Where(c => c && c.enabled && c.bounds.max.y >= hit.position.y + .25f).ToArray();
            if (blockers.Length > 0) { issue = "Physical obstacle: " + string.Join(", ", blockers.Select(c => c.name)); return false; }
            snapped = hit.position; return true;
        }
        static bool ToiletRoute(Vector3 from, Vector3 to)
        {
            var path = new NavMeshPath();
            return NavMesh.CalculatePath(from, to, StationDecisionPlatformGate.DefaultAreaMask(NavMesh.AllAreas), path) && path.status == NavMeshPathStatus.PathComplete;
        }
        static void CheckToiletRoute(Vector3 from, Vector3 to, DensityReceipt report)
        { Require(ToiletRoute(from, to), "A saved WC route is incomplete: " + from + " → " + to); ++report.toiletPathChecks; }
        static void CheckToiletClearancePath(Vector3 from, Vector3 to, Vector3[] occupiedMarkers, DensityReceipt report)
        {
            var path = new NavMeshPath();
            Require(NavMesh.CalculatePath(from, to, StationDecisionPlatformGate.DefaultAreaMask(NavMesh.AllAreas), path) && path.status == NavMeshPathStatus.PathComplete,
                "A WC admission/departure corridor is incomplete.");
            ++report.toiletPathChecks;
            foreach (var marker in occupiedMarkers)
            {
                for (int i = 1; i < path.corners.Length; ++i)
                {
                    var segment = path.corners[i] - path.corners[i - 1]; segment.y = 0f;
                    var offset = marker - path.corners[i - 1]; offset.y = 0f;
                    float fraction = segment.sqrMagnitude > .0001f ? Mathf.Clamp01(Vector3.Dot(offset, segment) / segment.sqrMagnitude) : 0f;
                    var distance = offset - fraction * segment;
                    Require(distance.magnitude >= .70f, "A WC admission/departure path crosses an occupied use/queue body at " + marker);
                }
                ++report.toiletOccupiedRouteClearanceChecks;
            }
        }

        [MenuItem("Efrei Research Lab/Train Station/Daily/Create dated timetable and arrivals scene")]
        public static void Create()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play before creating the daily scene.");
            Require(!SceneManager.GetActiveScene().isDirty, "Save the current scene before creating the daily scenario.");
            Require(!File.Exists(ScenePath), "The daily scene already exists; open and edit it without recreating saved changes.");
            Require(!File.Exists(ScenarioPath) && !File.Exists(SchedulePath), "Existing day resources require review before scene creation.");
            string previousScenePath = SceneManager.GetActiveScene().path;
            bool copied = false;
            try
            {
            var receipt = new Receipt { createdUtc = DateTime.UtcNow.ToString("O"), scene = ScenePath,
                sourceLabHash = Hash(StationDecisionLabAuthoring.ScenePath), originalSceneHash = Hash(StationDecisionLabAuthoring.SourceScene) };
            Require(AssetDatabase.CopyAsset(StationDecisionLabAuthoring.ScenePath, ScenePath), "Could not copy the saved Decision Lab.");
            copied = true;
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var world = Components<StationDecisionWorld>(scene).Single();
            var templates = world.participants.Where(p => p).ToArray();
            Require(templates.Length > 0, "The saved Lab has no repaired passenger templates.");
            foreach (var p in Components<StationPassenger>(scene)) p.gameObject.SetActive(false);
            foreach (var group in Components<StationWalkingGroup>(scene)) group.enabled = false;
            foreach (var member in Components<StationConversationMember>(scene)) member.enabled = false;
            var social = Components<StationSocialWorld>(scene).Single();
            social.passengers = Array.Empty<StationPassenger>();
            var root = new GameObject("15 Daily timetable — entrances and actor pool").transform;
            var poolRoot = new GameObject("Inactive actor slots — prepared only at scheduled entry").transform;
            poolRoot.SetParent(root, false);
            var scenario = ScriptableObject.CreateInstance<StationDecisionDayScenario>();
            scenario.scenarioDate = "2026-10-06"; scenario.seed = 20261006;
            AssetDatabase.CreateAsset(scenario, ScenarioPath);
            var schedule = StationDecisionDayRailAuthoring.CreateScheduleAsset(SchedulePath, scenario.scenarioDate, 7, 40);
            StationDecisionDayRailAuthoring.ConfigureOpenScene(schedule);
            var driver = world.gameObject.AddComponent<StationDecisionArrivalController>();
            driver.world = world; driver.scenario = scenario;
            world.arrivalController = driver; world.seed = scenario.seed;
            world.scenarioName = "Valmont — dated terminus timetable and scheduled passenger demand";
            world.outputRoot = "Captures/TrainStationDaily";
            world.events.Clear();
            driver.actorPool = new StationDecisionPassenger[scenario.activeActorLimit];
            for (int i = 0; i < driver.actorPool.Length; ++i)
            {
                var clone = UnityEngine.Object.Instantiate(templates[i % templates.Length].gameObject, poolRoot);
                clone.name = "Actor slot " + i.ToString("000"); clone.SetActive(false);
                var actor = clone.GetComponent<StationDecisionPassenger>();
                actor.world = world;
                actor.passenger.externalActivityControl = true; actor.passenger.automaticRequests = false;
                actor.passenger.initialSeat = null; actor.passenger.walkingGroup = null;
                actor.passenger.conversationMember = null; actor.passenger.homePoint = null;
                actor.passenger.activity = StationPassengerActivity.SoloWalking;
                actor.enabled = true;
                if (actor.intentLabel) actor.intentLabel.enabled = false;
                driver.actorPool[i] = actor;
            }
            world.participants = driver.actorPool;
            foreach (var poi in world.pois.Where(p => p && p.activity == DecisionActivity.Platform))
            { poi.capacity = 64; poi.spreadRadius = 3f; }
            foreach (var poi in world.pois.Where(p => p))
            {
                if (poi.activity == DecisionActivity.Toilet) { poi.meanDurationSeconds = 180f; poi.durationSpreadSeconds = 60f; }
                if (poi.seatedActivity == DecisionSeatedActivity.Coffee) { poi.seatedMeanDurationSeconds = 300f; poi.seatedDurationSpreadSeconds = 120f; }
                if (poi.seatedActivity == DecisionSeatedActivity.Reading) { poi.seatedMeanDurationSeconds = 540f; poi.seatedDurationSpreadSeconds = 180f; }
                if (poi.seatedActivity == DecisionSeatedActivity.Rest) { poi.seatedMeanDurationSeconds = 300f; poi.seatedDurationSpreadSeconds = 120f; }
                if (poi.service) poi.service.conversationDuration = poi.activity == DecisionActivity.Info ? 15f : 20f;
            }
            var portals = new System.Collections.Generic.List<StationDecisionSpawnPortal>();
            foreach (var entry in new[] { new { id = "entry-west", x = -24f }, new { id = "entry-main", x = 0f }, new { id = "entry-east", x = 24f } })
            {
                var portal = new StationDecisionSpawnPortal { entranceId = entry.id, trackNumber = 0, spawnPoints = new Transform[6] };
                var area = new GameObject(entry.id + " — safe floor inside open facade").transform; area.SetParent(root, false);
                for (int i = 0; i < 6; ++i) portal.spawnPoints[i] = Marker(area, "Entry slot " + i, new Vector3(entry.x + (i - 2.5f) * 1.1f, 0f, -25.1f));
                portals.Add(portal); ++receipt.entrancePortals;
            }
            foreach (var poi in world.pois.Where(p => p && p.activity == DecisionActivity.Platform))
            {
                var portal = new StationDecisionSpawnPortal { entranceId = "platform-" + poi.trackNumber, trackNumber = poi.trackNumber, spawnPoints = new Transform[6] };
                var area = new GameObject(portal.entranceId + " — virtual alighting safe anchors").transform; area.SetParent(root, false);
                for (int i = 0; i < 6; ++i) portal.spawnPoints[i] = Marker(area, "Alighting slot " + i,
                    poi.transform.position + new Vector3((i % 2 == 0 ? -.7f : .7f), 0f, 4f + (i / 2) * 1.1f));
                portals.Add(portal); ++receipt.platformPortals;
            }
            driver.portals = portals.ToArray();
            Physics.SyncTransforms();
            var display = world.pois.First(p => p && p.activity == DecisionActivity.Display);
            var exit = world.pois.First(p => p && p.activity == DecisionActivity.Exit);
            foreach (var portal in driver.portals)
            foreach (var marker in portal.spawnPoints)
            {
                var blockers = Physics.OverlapCapsule(marker.position + Vector3.up * .35f, marker.position + Vector3.up * 1.75f, .27f, ~0, QueryTriggerInteraction.Ignore);
                Require(blockers.Length == 0, "Spawn overlaps a physical obstacle: " + marker.name + " / " + string.Join(", ", blockers.Select(c => c.name)));
                ++receipt.safeSpawnPoints;
                CheckRoute(marker.position, portal.trackNumber == 0 ? display.Position : exit.Position); ++receipt.completeRoutes;
                if (portal.trackNumber == 0)
                    foreach (var platform in world.pois.Where(p => p && p.activity == DecisionActivity.Platform))
                    { CheckRoute(marker.position, platform.Position); ++receipt.completeRoutes; }
            }
            var dashboard = world.GetComponent<StationDecisionDashboard>();
            if (dashboard) { dashboard.selectedParticipant = 0; dashboard.visible = true; }
            EditorSceneManager.MarkSceneDirty(scene);
            Require(EditorSceneManager.SaveScene(scene, ScenePath), "Could not save daily scene.");
            AssetDatabase.SaveAssets();
            var settings = EditorBuildSettings.scenes.ToList();
            if (!settings.Any(s => s.path == ScenePath)) settings.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = settings.ToArray();
            receipt.poolSlots = driver.actorPool.Length; receipt.savedSceneHash = Hash(ScenePath);
            receipt.labPreserved = Hash(StationDecisionLabAuthoring.ScenePath) == receipt.sourceLabHash;
            receipt.originalPreserved = Hash(StationDecisionLabAuthoring.SourceScene) == receipt.originalSceneHash;
            receipt.success = receipt.labPreserved && receipt.originalPreserved;
            Directory.CreateDirectory("Reports/TrainStationDaily");
            File.WriteAllText("Reports/TrainStationDaily/authoring.json", JsonUtility.ToJson(receipt, true));
            Require(receipt.success, "A source scene changed while copying the day scenario.");
            }
            catch
            {
                // These paths were absent before this operation; roll back only this new scene and its resources.
                if (copied)
                {
                    EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
                    AssetDatabase.DeleteAsset(ScenePath);
                    AssetDatabase.DeleteAsset(ScenarioPath);
                    AssetDatabase.DeleteAsset(SchedulePath);
                }
                throw;
            }
        }

        public static void CaptureGame()
        {
            Require(EditorApplication.isPlaying && SceneManager.GetActiveScene().path == ScenePath, "Run the daily scene before capturing its Game view.");
            Directory.CreateDirectory("Reports/TrainStationDaily");
            string path = Path.GetFullPath("Reports/TrainStationDaily/game-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".png");
            ScreenCapture.CaptureScreenshot(path);
            File.WriteAllText("Reports/TrainStationDaily/game-capture-path.txt", path);
        }

        public static void SimplifyLabels()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && SceneManager.GetActiveScene().path == ScenePath && !SceneManager.GetActiveScene().isDirty,
                "Open the clean saved daily scene in Edit mode before configuring its labels.");
            var scene = SceneManager.GetActiveScene();
            var controller = Components<StationDecisionArrivalController>(scene).Single();
            foreach (var actor in controller.actorPool)
                if (actor && actor.intentLabel) { actor.intentLabel.enabled = false; EditorUtility.SetDirty(actor.intentLabel); }
            EditorSceneManager.MarkSceneDirty(scene);
            Require(EditorSceneManager.SaveScene(scene, ScenePath), "Could not save the daily presentation.");
            File.WriteAllText("Reports/TrainStationDaily/presentation-scene-hash.txt", Hash(ScenePath));
        }

        public static void WidenEntranceAnchors()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && SceneManager.GetActiveScene().path == ScenePath && !SceneManager.GetActiveScene().isDirty,
                "Open the clean saved daily scene in Edit mode before configuring its entrances.");
            var scene = SceneManager.GetActiveScene();
            var controller = Components<StationDecisionArrivalController>(scene).Single();
            try
            {
                foreach (var portal in controller.portals.Where(p => p.trackNumber == 0))
                {
                    float centre = portal.entranceId == "entry-west" ? -24f : portal.entranceId == "entry-east" ? 24f : 0f;
                    for (int i = 0; i < portal.spawnPoints.Length; ++i)
                    {
                        var point = new Vector3(centre + (i - 2.5f) * 1.1f, 0f, -25.1f);
                        Require(NavMesh.SamplePosition(point, out var hit, .18f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - .02f) < .15f,
                            "Widened entrance is outside the safe floor: " + point);
                        Require(Physics.OverlapCapsule(hit.position + Vector3.up * .35f, hit.position + Vector3.up * 1.75f, .27f, ~0, QueryTriggerInteraction.Ignore).Length == 0,
                            "Widened entrance overlaps furniture: " + point);
                        foreach (var poi in controller.world.pois.Where(p => p)) CheckRoute(hit.position, poi.Position);
                        portal.spawnPoints[i].position = hit.position;
                    }
                }
                EditorSceneManager.MarkSceneDirty(scene);
                Require(EditorSceneManager.SaveScene(scene, ScenePath), "Could not save the widened entrance anchors.");
                File.WriteAllText("Reports/TrainStationDaily/entrance-scene-hash.txt", Hash(ScenePath));
            }
            catch { EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single); throw; }
        }

        static Transform Marker(Transform parent, string name, Vector3 point)
        {
            bool nearest = NavMesh.SamplePosition(point, out var diagnosticHit, 4f, NavMesh.AllAreas);
            Require(NavMesh.SamplePosition(point, out var hit, .18f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - .02f) < .15f,
                "The entrance/alighting marker is outside the owned floor NavMesh: " + name + " / " + point + " / nearest=" + (nearest ? diagnosticHit.position.ToString() : "none"));
            var marker = new GameObject(name).transform; marker.SetParent(parent, false); marker.position = hit.position; return marker;
        }
        static void CheckRoute(Vector3 from, Vector3 to)
        { var path = new NavMeshPath(); Require(NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete, "A spawn route is incomplete."); }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static string Hash(string path) { using var algorithm = SHA256.Create(); return BitConverter.ToString(algorithm.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
    }
}
#endif
