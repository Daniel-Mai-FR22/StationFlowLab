#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.TrainStation.Social;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Read-only saved circulation checks and a bounded observer of the actual Daily motion.</summary>
    [InitializeOnLoad]
    public static class StationDecisionHallFountainVerification
    {
        const string ScenePath = "Assets/TrainStation/Scenes/TrainStationDaily.unity";
        const float NumericTolerance = .005f;
        static Vector3 Center => StationDecisionHallFountainAuthoring.Center;
        static float Radius => StationDecisionHallFountainAuthoring.Radius;
        static string Output => StationDecisionHallFountainAuthoring.ReportDirectory;

        [Serializable] public sealed class RouteEvidence
        {
            public string id;
            public int areaMask, corners;
            public float length, straight, minimumSegmentClearance;
            public bool detour;
        }
        [Serializable] public sealed class LayoutReport
        {
            public string checkedUtc, scene, navigationAsset;
            public bool success;
            public Vector3 center;
            public float basinRadius, minimumPathClearance = float.MaxValue;
            public int insideNavMeshChecks, routeChecks, physicalClearanceChecks, displayStops, detourRoutes;
            public int portalRoutes, queueRoutes, seatRoutes, toiletRoutes, gateRoutes, coachRoutes;
            public int nativeNavigationActors;
            public List<RouteEvidence> principalRoutes = new List<RouteEvidence>();
            public List<string> errors = new List<string>();
            public string scope = "Saved native NavMesh paths and collider clearance. Both bypass arcs are individually reachable; this is not a forecast of crowd throughput or a claim that equal numbers choose each side.";
        }
        [Serializable] public sealed class NativeReport
        {
            public string startedUtc, completedUtc, scene, outputDirectory;
            public bool success, complete;
            public Vector3 center;
            public float basinRadius, elapsed, minimumObservedRadius = float.MaxValue, minimumObservedPathClearance = float.MaxValue;
            public int observedFrames, positionChecks, pathProbes, completePathProbes, identityChecks;
            public int eastAnnulusSamples, westAnnulusSamples, eastJourneys, westJourneys, uniqueJourneys;
            public int counterJoins, counterServiceStarts, counterServiceCompletions, counterFifoChecks;
            public int displayUsingSamples, displayUsingJourneys, displayClaimPairChecks, displayPhysicalPairChecks;
            public int nativeAgentChecks, movingAvoidanceChecks, stoppedAgentSamples, actorObstacleChecks;
            public float maximumDisplayStandingOffset, minimumDisplayPhysicalSeparation = float.MaxValue;
            public List<string> errors = new List<string>();
            public string scope = "Actual saved Daily simulation; read-only Editor observations once per observed game frame, native path probes at most10Hz real time near the fountain, physical service callbacks. No demand/policy edit, forced destination, teleport or clock skip. Annulus counts are observed occupancy samples/unique journeys, not completed arc traversals. Unobserved frames and motion after this bounded run are not certified.";
        }

        static NativeReport native;
        static StationDecisionWorld world;
        static StationDecisionArrivalController arrivals;
        static int lastFrame = -1;
        static bool ending;
        static double nextPathProbe, nextWrite;
        static readonly HashSet<string> allJourneys = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> east = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> west = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> displayJourneys = new HashSet<string>(StringComparer.Ordinal);
        static readonly Dictionary<StationInformationDesk, DeskWatch> desks = new Dictionary<StationInformationDesk, DeskWatch>();
        sealed class DeskWatch
        {
            public readonly List<StationPassenger> joined = new List<StationPassenger>();
            public readonly HashSet<StationPassenger> seen = new HashSet<StationPassenger>();
            public UnityEngine.Events.UnityAction<StationPassenger> join, start, end;
        }

        static StationDecisionHallFountainVerification()
        {
            EditorApplication.update += Observe;
            EditorApplication.playModeStateChanged += ModeChanged;
        }

        [MenuItem("Efrei Research Lab/Train Station/Daily/Validate hall fountain circulation")]
        public static void CheckLayout()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("Fountain layout check requires stopped Edit mode.");
            var scene = SceneManager.GetActiveScene();
            var report = new LayoutReport { checkedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path,
                center = Center, basinRadius = Radius };
            try
            {
                Require(scene.path == ScenePath, "Fountain circulation fixture requires the Daily scene.");
                var root = scene.GetRootGameObjects().SingleOrDefault(g => g.name == StationDecisionHallFountainAuthoring.RootName);
                Require(root, "The Daily hall fountain root is missing.");
                var colliders = root.GetComponentsInChildren<Collider>(true).Where(c => c.enabled && !c.isTrigger).ToArray();
                Require(colliders.Length > 0, "Fountain lacks a solid collider.");
                Require(root.GetComponentsInChildren<NavMeshModifier>(true).Any(m => m.enabled && m.overrideArea && m.area == 1),
                    "Fountain lacks its saved Not Walkable navigation modifier.");
                var surface = Components<NavMeshSurface>(scene).Single();
                Require(surface.navMeshData, "The Daily saved NavMesh is missing.");
                report.navigationAsset = AssetDatabase.GetAssetPath(surface.navMeshData);
                var w = Components<StationDecisionWorld>(scene).Single();
                var controller = Components<StationDecisionArrivalController>(scene).Single();
                foreach (var p in w.participants.Where(p => p))
                {
                    var motion=p.motion;
                    Require(motion && motion.agent && motion.agent.enabled, "Daily actor lacks its enabled native agent: " + p.name);
                    Require(motion.GetComponentsInChildren<NavMeshObstacle>(true).Length == 0,
                        "Daily people must not carve obstacles into the walkable floor: " + p.name);
                    Require(motion.agent.avoidancePriority == 50 && motion.agent.autoRepath && motion.agent.updatePosition &&
                        motion.agent.obstacleAvoidanceType == ObstacleAvoidanceType.HighQualityObstacleAvoidance,
                        "Daily moving actors need mutual high-quality native avoidance.");
                    ++report.nativeNavigationActors;
                }
                int closedMask = StationDecisionPlatformGate.DefaultAreaMask(NavMesh.AllAreas);
                Physics.SyncTransforms();
                CheckInterior(report);
                var display = w.pois.Single(p => p && p.poiId == "display-hall");
                Require(display.standingPoints != null && display.standingPoints.Length == 24 && display.standingPoints.All(t => t),
                    "Display requires24 saved standing points.");
                Require(display.facingTarget, "Display lacks its screen-facing target.");
                foreach (var point in display.standingPoints)
                {
                    Exact(point.position, closedMask, "Display stop " + point.name);
                    Clear(point.position, report, "Display stop " + point.name);
                    Require(Flat(point.position, Center) >= Radius + .27f, "Display stop overlaps the fountain footprint.");
                    ++report.displayStops;
                }
                for (int i = 0; i < display.standingPoints.Length; ++i)
                    for (int j = i + 1; j < display.standingPoints.Length; ++j)
                        Require(Flat(display.standingPoints[i].position, display.standingPoints[j].position) >= .9f,
                            "Display standing points overlap personal space.");

                foreach (var portal in controller.portals)
                    foreach (var point in portal.spawnPoints)
                    {
                        Require(point, "Saved entrance has a missing spawn point.");
                        Exact(point.position, closedMask, portal.entranceId);
                        var destinations = portal.trackNumber == 0 ? w.pois : w.pois.Where(p => p && p.activity == DecisionActivity.Exit).ToArray();
                        foreach (var poi in destinations.Where(p => p))
                        {
                            var gate = poi.activity == DecisionActivity.Platform ? w.platformGates.FirstOrDefault(g => g && g.trackNumber == poi.trackNumber) : null;
                            int mask = gate ? gate.PlanningMask(closedMask) : closedMask;
                            Path(point.position, poi.Position, mask, portal.entranceId + " → " + poi.poiId, report);
                            ++report.portalRoutes;
                        }
                    }
                var main = controller.portals.Single(p => p.trackNumber == 0 && p.entranceId == "entry-main");
                foreach (var entry in main.spawnPoints)
                    foreach (var stop in display.standingPoints)
                    {
                        var proof = Path(entry.position, stop.position, closedMask, "Main entrance → " + stop.name, report);
                        if (SegmentClearance(entry.position, stop.position) < Radius - .1f)
                        {
                            Require(proof.length > proof.straight + .01f, "Main-to-screen path failed to detour around the basin.");
                            proof.detour = true; ++report.detourRoutes;
                        }
                        if (report.principalRoutes.Count < 8) report.principalRoutes.Add(proof);
                    }
                Require(report.detourRoutes > 0, "No main entrance → screen route crosses the basin chord and demonstrates a detour.");
                Vector3 mainCenter = main.spawnPoints.Aggregate(Vector3.zero, (sum, point) => sum + point.position) / main.spawnPoints.Length;
                var centralDisplayStop = display.standingPoints.OrderBy(p => Mathf.Abs(p.position.x - Center.x)).First().position;
                Require(SegmentClearance(mainCenter, centralDisplayStop) < Radius * .5f,
                    "The central entrance-to-screen chord does not pass meaningfully through the basin.");
                var centralDetour = Path(mainCenter, centralDisplayStop, closedMask, "Central main entrance → central saved display stop", report);
                Require(centralDetour.length > centralDetour.straight + .5f,
                    "Central entrance-to-screen route lacks a meaningful fountain detour.");
                centralDetour.detour = true; ++report.detourRoutes; report.principalRoutes.Add(centralDetour);
                // Explicit side waypoints prove each bypass independently without changing passenger destinations.
                var south = Center + new Vector3(0, .02f, -Radius - 2f);
                var north = Center + new Vector3(0, .02f, Radius + 2f);
                foreach (int side in new[] { -1, 1 })
                {
                    var middle = Center + new Vector3(side * (Radius + 2f), .02f, 0);
                    report.principalRoutes.Add(Path(south, middle, closedMask, side < 0 ? "West arc entry" : "East arc entry", report));
                    report.principalRoutes.Add(Path(middle, north, closedMask, side < 0 ? "West arc exit" : "East arc exit", report));
                    report.principalRoutes.Add(Path(north, middle, closedMask, side < 0 ? "West reverse entry" : "East reverse entry", report));
                    report.principalRoutes.Add(Path(middle, south, closedMask, side < 0 ? "West reverse exit" : "East reverse exit", report));
                }
                Vector3 origin = main.spawnPoints[0].position;
                foreach (var poi in w.pois.Where(p => p && p.service))
                {
                    Require(poi.departurePoint && poi.service.queueSlots.Length > 0, "Missing counter queue/departure: " + poi.poiId);
                    foreach (var slot in poi.service.queueSlots)
                    {
                        Require(slot, "Missing counter slot: " + poi.poiId);
                        Clear(slot.position, report, poi.poiId + "/" + slot.name);
                        Path(origin, slot.position, closedMask, "Entrance → " + poi.poiId + "/" + slot.name, report); ++report.queueRoutes;
                    }
                    Clear(poi.departurePoint.position, report, poi.poiId + " departure");
                    Path(poi.service.queueSlots[0].position, poi.departurePoint.position, closedMask, poi.poiId + " head → departure", report);
                    Path(poi.departurePoint.position, display.Position, closedMask, poi.poiId + " departure → hall", report);
                    report.queueRoutes += 2;
                }
                foreach (var seat in w.pois.Where(p => p).SelectMany(p => p.seats ?? Array.Empty<StationSeat>()).Where(s => s).Distinct())
                {
                    Clear(seat.ApproachPosition, report, "Seat " + seat.seatId);
                    Path(origin, seat.ApproachPosition, closedMask, "Entrance → seat " + seat.seatId, report); ++report.seatRoutes;
                }
                foreach (var toilet in Components<StationDecisionToiletQueue>(scene).Where(q => q.world == w))
                    foreach (var point in toilet.queueSlots.Concat(toilet.usePoints).Concat(new[] { toilet.exitPoint }))
                    {
                        Require(point, "Missing WC physical marker.");
                        Clear(point.position, report, "WC " + point.name);
                        Path(origin, point.position, closedMask, "Entrance → WC " + point.name, report); ++report.toiletRoutes;
                    }
                var exit = w.pois.First(p => p && p.activity == DecisionActivity.Exit);
                foreach (var gate in w.platformGates.Where(g => g))
                {
                    foreach (var marker in gate.queueAnchors.Concat(gate.holdingAnchors).Concat(new[] { gate.hallCrossingPoint }))
                    {
                        Require(marker, "Missing gate hall marker.");
                        Path(origin, marker.position, closedMask, "Entrance → gate " + gate.trackNumber + "/" + marker.name, report); ++report.gateRoutes;
                    }
                    var platform = w.GetPlatform(gate.trackNumber); Require(platform, "Missing platform POI.");
                    Require(!Complete(gate.queueAnchors[0].position, platform.Position, closedMask), "Unscanned access bypasses gate " + gate.trackNumber);
                    Path(gate.queueAnchors[0].position, gate.platformCrossingPoint.position, gate.PlanningMask(closedMask), "Permitted ingress " + gate.trackNumber, report);
                    Path(platform.Position, exit.Position, closedMask, "Free alighter egress " + gate.trackNumber, report);
                    Require(!Complete(gate.egressLink.endTransform.position, gate.egressLink.startTransform.position,
                        1 | (1 << StationDecisionPlatformGate.EgressArea)), "Free egress reverses into quai " + gate.trackNumber);
                    report.gateRoutes += 2;
                }
                foreach (var coach in w.coachQueues.Where(c => c))
                {
                    var gate = w.platformGates.First(g => g && g.trackNumber == coach.trackNumber);
                    foreach (var marker in coach.queueAnchors.Concat(new[] { coach.doorAnchor }))
                    {
                        Require(marker, "Missing coach boarding marker.");
                        Path(origin, marker.position, gate.PlanningMask(closedMask), "Entrance → coach " + coach.trackNumber + "/" + coach.coachNumber + "/" + marker.name, report);
                        ++report.coachRoutes;
                    }
                }
            }
            catch (Exception error) { report.errors.Add(error.ToString()); }
            if (float.IsInfinity(report.minimumPathClearance) || report.minimumPathClearance == float.MaxValue) report.minimumPathClearance = -1;
            report.success = report.errors.Count == 0; Write("saved-layout-check.json", report);
            Require(report.success, string.Join("\n", report.errors));
        }

        static void CheckInterior(LayoutReport report)
        {
            for (int ring = 0; ring <= 5; ++ring)
                for (int angle = 0; angle < (ring == 0 ? 1 : 96); ++angle)
                {
                    float r = ring * (Radius - .30f) / 5f;
                    float a = angle * Mathf.PI * 2f / 96f;
                    var point = Center + new Vector3(Mathf.Cos(a) * r, .02f, Mathf.Sin(a) * r);
                    ++report.insideNavMeshChecks;
                    Require(!NavMesh.SamplePosition(point, out _, .10f, NavMesh.AllAreas), "Floor navigation exists inside the fountain basin.");
                }
        }
        static void Exact(Vector3 point, int mask, string id)
        { Require(Snap(point, mask, out _), "Off-NavMesh saved point: " + id); }
        static bool Snap(Vector3 point, int mask, out Vector3 position)
        {
            bool okay = NavMesh.SamplePosition(point, out var hit, .2f, mask) && Mathf.Abs(hit.position.y - point.y) <= .15f;
            position = okay ? hit.position : point; return okay;
        }
        static RouteEvidence Path(Vector3 from, Vector3 to, int mask, string id, LayoutReport report)
        {
            Require(Snap(from, mask, out var a), "Path start is off NavMesh: " + id);
            Require(Snap(to, mask, out var b), "Path destination is off NavMesh: " + id);
            var path = new NavMeshPath();
            Require(NavMesh.CalculatePath(a, b, mask, path) && path.status == NavMeshPathStatus.PathComplete, "Incomplete native path: " + id);
            var proof = new RouteEvidence { id = id, areaMask = mask, corners = path.corners.Length, straight = Vector3.Distance(a, b),
                minimumSegmentClearance = float.MaxValue };
            foreach (var corner in path.corners) proof.minimumSegmentClearance = Mathf.Min(proof.minimumSegmentClearance, Flat(corner, Center));
            for (int i = 1; i < path.corners.Length; ++i)
            { proof.length += Vector3.Distance(path.corners[i - 1], path.corners[i]); proof.minimumSegmentClearance = Mathf.Min(proof.minimumSegmentClearance, SegmentClearance(path.corners[i - 1], path.corners[i])); }
            Require(proof.minimumSegmentClearance >= Radius - NumericTolerance, "Native path intersects the fountain footprint: " + id);
            report.minimumPathClearance = Mathf.Min(report.minimumPathClearance, proof.minimumSegmentClearance); ++report.routeChecks;
            return proof;
        }
        static bool Complete(Vector3 from, Vector3 to, int mask)
        { if (!Snap(from, mask, out var a) || !Snap(to, mask, out var b)) return false; var path = new NavMeshPath(); return NavMesh.CalculatePath(a, b, mask, path) && path.status == NavMeshPathStatus.PathComplete; }
        static void Clear(Vector3 point, LayoutReport report, string id)
        {
            ++report.physicalClearanceChecks;
            var hits = Physics.OverlapCapsule(point + Vector3.up * .4f, point + Vector3.up * 1.5f, .27f, ~0, QueryTriggerInteraction.Ignore);
            Require(hits.Length == 0, "Standing point intersects physical furniture: " + id + "; " + string.Join(", ", hits.Select(h => h.name)));
        }
        static float Flat(Vector3 a, Vector3 b) { a.y = b.y = 0; return Vector3.Distance(a, b); }
        static float SegmentClearance(Vector3 a, Vector3 b)
        {
            var aa = new Vector2(a.x, a.z); var bb = new Vector2(b.x, b.z); var c = new Vector2(Center.x, Center.z);
            var delta = bb - aa; float t = delta.sqrMagnitude <= .000001f ? 0 : Mathf.Clamp01(Vector2.Dot(c - aa, delta) / delta.sqrMagnitude);
            return Vector2.Distance(c, aa + delta * t);
        }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>(true)).ToArray();
        static void Require(bool okay, string text) { if (!okay) throw new InvalidOperationException(text); }
        static void Write(string filename, object value) { Directory.CreateDirectory(Output); File.WriteAllText(System.IO.Path.Combine(Output, filename), JsonUtility.ToJson(value, true)); }

        static void ModeChanged(PlayModeStateChange mode)
        {
            if (mode == PlayModeStateChange.EnteredPlayMode) { ending = false; native = null; world = null; arrivals = null; lastFrame = -1; }
            if (mode == PlayModeStateChange.ExitingPlayMode) { ending = true; FinishNative(); }
        }
        static void BeginNative()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath || !scene.GetRootGameObjects().Any(g => g.name == StationDecisionHallFountainAuthoring.RootName)) return;
            world = Components<StationDecisionWorld>(scene).SingleOrDefault();
            arrivals = Components<StationDecisionArrivalController>(scene).SingleOrDefault();
            if (!world || !arrivals || !arrivals.IsReady || string.IsNullOrEmpty(world.OutputDirectory)) return;
            native = new NativeReport { startedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path, outputDirectory = world.OutputDirectory,
                center = Center, basinRadius = Radius };
            allJourneys.Clear(); east.Clear(); west.Clear(); displayJourneys.Clear(); desks.Clear(); nextPathProbe = nextWrite = 0;
            foreach (var desk in Components<StationInformationDesk>(scene).Where(d => d.isActiveAndEnabled))
            {
                var watch = new DeskWatch(); desks.Add(desk, watch);
                watch.join = p => CounterJoined(desk, p); watch.start = p => CounterStarted(desk, p);
                watch.end = p => { if (native != null) { ++native.counterServiceCompletions; if (desks.TryGetValue(desk, out var state)) state.joined.Remove(p); } };
                desk.queueJoined.AddListener(watch.join); desk.conversationStarted.AddListener(watch.start); desk.conversationEnded.AddListener(watch.end);
                foreach (var existing in desk.QueuePassengers) { watch.joined.Add(existing); watch.seen.Add(existing); }
            }
        }
        static void Observe()
        {
            if (ending || !EditorApplication.isPlaying || EditorApplication.isCompiling) return;
            if (native == null) BeginNative();
            if (native == null || !world || !arrivals || Time.frameCount == lastFrame) return;
            lastFrame = Time.frameCount; ++native.observedFrames; native.elapsed = world.ElapsedSeconds;
            bool paths = EditorApplication.timeSinceStartup >= nextPathProbe;
            if (paths) nextPathProbe = EditorApplication.timeSinceStartup + .1;
            var display = world.pois.FirstOrDefault(p => p && p.poiId == "display-hall");
            var displayUsing = new List<StationDecisionPassenger>();
            foreach (var record in arrivals.ActiveJourneys)
            {
                var p = record.livePassenger; ++native.identityChecks;
                NativeCheck(p && p.HasEntered && p.JourneyId == record.plan.journeyId, "Active actor lost its immutable journey binding.");
                if (!p) continue;
                var actorMotion=p.motion;
                if (actorMotion)
                {
                    ++native.actorObstacleChecks;
                    NativeCheck(actorMotion.GetComponentsInChildren<NavMeshObstacle>(true).Length == 0,
                        "An actor recreated a crowd obstacle: " + p.JourneyId);
                    var liveAgent=actorMotion.agent;
                    if (liveAgent && liveAgent.enabled && liveAgent.isOnNavMesh)
                    {
                        ++native.nativeAgentChecks;
                        NativeCheck(liveAgent.updatePosition && liveAgent.avoidancePriority == 50 && liveAgent.autoRepath &&
                            liveAgent.obstacleAvoidanceType == ObstacleAvoidanceType.HighQualityObstacleAvoidance,
                            "Actor lost automatic native navigation or avoidance: " + p.JourneyId);
                        if (liveAgent.isStopped) ++native.stoppedAgentSamples;
                        else if (liveAgent.hasPath) ++native.movingAvoidanceChecks;
                    }
                    else NativeCheck(p.ReservedSeat || p.Finished,
                        "A standing actor lost its native agent: " + p.JourneyId);
                }
                allJourneys.Add(p.JourneyId); float r = Flat(p.transform.position, Center); ++native.positionChecks;
                native.minimumObservedRadius = Mathf.Min(native.minimumObservedRadius, r);
                NativeCheck(r >= Radius - NumericTolerance, "Observed actor enters fountain basin: " + p.JourneyId);
                if (display && p.CurrentActivity == DecisionActivity.Display && p.ExecutionState == "Using" && p.CurrentTargetId == display.poiId)
                {
                    ++native.displayUsingSamples; displayJourneys.Add(p.JourneyId); displayUsing.Add(p);
                    native.maximumDisplayStandingOffset = Mathf.Max(native.maximumDisplayStandingOffset,
                        Flat(p.transform.position, display.DestinationFor(p)));
                }
                if (r <= Radius + 3f && Mathf.Abs(p.transform.position.z - Center.z) <= Radius)
                {
                    if (p.transform.position.x >= Center.x) { ++native.eastAnnulusSamples; east.Add(p.JourneyId); }
                    else { ++native.westAnnulusSamples; west.Add(p.JourneyId); }
                }
                var agent = p.motion ? p.motion.agent : null;
                if (!paths || r > Radius + 15f || !agent || !agent.enabled || !agent.isOnNavMesh || agent.pathPending || !agent.hasPath || agent.isOnOffMeshLink) continue;
                ++native.pathProbes;
                bool complete = agent.pathStatus == NavMeshPathStatus.PathComplete;
                if (complete) ++native.completePathProbes;
                NativeCheck(complete, "Observed partial native path near fountain: " + p.JourneyId);
                var corners = agent.path.corners;
                for (int i = 1; i < corners.Length; ++i)
                {
                    float clearance = SegmentClearance(corners[i - 1], corners[i]);
                    native.minimumObservedPathClearance = Mathf.Min(native.minimumObservedPathClearance, clearance);
                    NativeCheck(clearance >= Radius - NumericTolerance, "Observed native path crosses basin: " + p.JourneyId);
                }
            }
            for (int i = 0; i < displayUsing.Count; ++i)
                for (int j = i + 1; j < displayUsing.Count; ++j)
                {
                    ++native.displayClaimPairChecks;
                    NativeCheck(Flat(display.DestinationFor(displayUsing[i]), display.DestinationFor(displayUsing[j])) >= .9f,
                        "Simultaneous Display users share/overlap a claimed standing point.");
                    float separation = Flat(displayUsing[i].transform.position, displayUsing[j].transform.position);
                    ++native.displayPhysicalPairChecks;
                    native.minimumDisplayPhysicalSeparation = Mathf.Min(native.minimumDisplayPhysicalSeparation, separation);
                    NativeCheck(separation >= .5f, "Simultaneous stationary Display users physically overlap.");
                }
            native.displayUsingJourneys = displayJourneys.Count;
            native.uniqueJourneys = allJourneys.Count; native.eastJourneys = east.Count; native.westJourneys = west.Count;
            if (EditorApplication.timeSinceStartup >= nextWrite) { nextWrite = EditorApplication.timeSinceStartup + 1; WriteNative(false); }
        }
        static void CounterJoined(StationInformationDesk desk, StationPassenger p)
        {
            if (native == null || !desks.TryGetValue(desk, out var watch)) return;
            ++native.counterJoins; ++native.counterFifoChecks;
            NativeCheck(p && desk.TryGetQueueSlot(p, out var slot) && Flat(p.transform.position, slot.position) <= desk.arrivalTolerance + .01f,
                "Counter admitted a customer away from physical tail: " + desk.serviceName);
            watch.joined.RemoveAll(a => !a || !desk.Contains(a));
            NativeCheck(!watch.joined.Contains(p), "Counter duplicate physical admission: " + desk.serviceName);
            watch.joined.Add(p); watch.seen.Add(p);
        }
        static void CounterStarted(StationInformationDesk desk, StationPassenger p)
        {
            if (native == null || !desks.TryGetValue(desk, out var watch)) return;
            ++native.counterServiceStarts; ++native.counterFifoChecks;
            watch.joined.RemoveAll(a => !a || !desk.Contains(a));
            NativeCheck(watch.joined.Count > 0 && watch.joined[0] == p && desk.GetQueueIndex(p) == 0 && desk.IsWithinServiceArea(p.transform.position),
                "Counter service bypassed physical FIFO head: " + desk.serviceName);
        }
        static void NativeCheck(bool okay, string text)
        { if (!okay && native != null && native.errors.Count < 100 && !native.errors.Contains(text)) native.errors.Add(text); }
        static void WriteNative(bool complete)
        {
            if (native == null) return;
            native.complete = complete; native.success = native.errors.Count == 0;
            Write("native-fountain-observations.json", native);
        }
        static void FinishNative()
        {
            if (native == null) return;
            native.completedUtc = DateTime.UtcNow.ToString("O");
            NativeCheck(native.positionChecks > 0 && native.pathProbes > 0, "Native fountain observation has no physical/path evidence.");
            foreach (var pair in desks)
                if (pair.Key) { pair.Key.queueJoined.RemoveListener(pair.Value.join); pair.Key.conversationStarted.RemoveListener(pair.Value.start); pair.Key.conversationEnded.RemoveListener(pair.Value.end); }
            if (native.minimumObservedRadius == float.MaxValue) native.minimumObservedRadius = -1;
            if (native.minimumObservedPathClearance == float.MaxValue) native.minimumObservedPathClearance = -1;
            if (native.minimumDisplayPhysicalSeparation == float.MaxValue) native.minimumDisplayPhysicalSeparation = -1;
            WriteNative(true);
            Write("native-" + System.IO.Path.GetFileName(native.outputDirectory) + ".json", native);
            desks.Clear(); native = null; world = null; arrivals = null;
        }
    }
}
#endif
