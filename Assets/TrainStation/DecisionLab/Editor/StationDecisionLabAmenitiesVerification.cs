#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using StationFlow.TrainStation.Social;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Independent geometry checks and bounded native-speed seating/service integration.</summary>
    [InitializeOnLoad]
    public static class StationDecisionLabAmenitiesVerification
    {
        const string Key = "StationFlow.DecisionLab.AmenitiesVerification";
        const string Output = "Reports/TrainStationDecisionLab";
        static Run run;
        static StationDecisionWorld world;
        static StationDecisionDashboard dashboard;
        static double nextPoll, nextWrite, pauseAt, captureAt, cameraHoldUntil;
        static int lastFrame;
        static bool requestStop;
        sealed class AfterAmenitiesAnimation { }
        static StationDecisionPassenger pausedActor, deadlineActor, captureActor, disabledSeatActor;
        static StationSeat pausedSeat, deadlineSeat, disabledSeat;
        static float pauseClock, releaseBy, deadlineReleaseBy, disabledReleaseBy;
        static Vector3[] pauseRoots, pauseBones;
        static string captureKind;
        static readonly Dictionary<string, Watch> people = new Dictionary<string, Watch>();
        static readonly Dictionary<StationInformationDesk, ServiceWatch> services = new Dictionary<StationInformationDesk, ServiceWatch>();
        static readonly List<StationSeat> seats = new List<StationSeat>();
        static readonly HashSet<string> kinds = new HashSet<string>();

        [Serializable] public sealed class FileEvidence { public string path, before, after; }
        [Serializable] public sealed class Gate
        {
            public string service, marker;
            public Vector3 position, across;
            public float requiredPhysicalWidth, requiredNavCenterWidth, measuredNavCenterWidth;
            public int capsules;
            public bool physicalClear, complete;
            public List<string> blockers = new List<string>();
        }
        [Serializable] public sealed class Layout
        {
            public string checkedUtc, scene, navigationAsset;
            public bool success, filesPreserved;
            public int seats, seatingPois, routeChecks, departureRoutes, seatApproachChecks;
            public List<Gate> gates = new List<Gate>();
            public List<FileEvidence> files = new List<FileEvidence>();
            public List<string> errors = new List<string>();
            public string method = "Independent complete NavMesh paths; entry/front-aisle cross-rays measure available agent-center width after bake erosion; standing-height physics capsules check physical clearance at authored cross-sections. This is not every possible path or a congestion-capacity proof.";
        }
        [Serializable] public sealed class Person
        {
            public string id, fixtureNeed, lastExecution, lastSeat;
            public int reservations, occupations, releases, completedSeatedVisits, seatedSamples, poseChecks, navigationSamples, remainingDistanceFallbackSamples, remainingDistanceUnavailableSamples;
            public float seatedSeconds, maximumNoProgressSeconds, maximumStationarySeconds, maximumSeatStep, minimumHipHeight = 99f, maximumHipHeight;
            public List<string> seatedKinds = new List<string>(), issues = new List<string>();
        }
        [Serializable] public sealed class Service
        {
            public string name, fixtureVisitor;
            public bool fixtureRequested;
            public int joined, began, ended, queuePeak, physicalArrivalChecks, servicePositionChecks;
            public float maximumWaitingWithoutService;
        }
        [Serializable] public sealed class CameraFrame
        {
            public string passenger, activity;
            public float actualDistance, requestedDistance, yaw, pitch, bodyViewportHeight;
            public Vector3 cameraPosition, focusPosition, headViewport, hipViewport;
            public bool noVolumeOverlap, clearFocusRay, visibleHeadAndHip;
            public List<string> blockers = new List<string>();
        }
        [Serializable] public sealed class Run
        {
            public string startedUtc, completedUtc, phase, archivePath, outputDirectory, oldPlayScene;
            public long deadlineTicks;
            public bool success, filesPreserved, labPreserved, oldOptionsEnabled, oldPaused, oldBackground;
            public bool completionReached, postAnimationHook, framingOnly;
            public int oldOptions, oldFps, oldVsync, initializeFrame, observations, ownershipChecks, seatedPoseChecks, overlapChecks;
            public float oldScale, startedSimulation, elapsedSimulation, pauseSeconds, maximumPausedRootDrift, maximumPausedBoneDrift;
            public bool coffeeObserved, readingObserved, restObserved, pauseChecked, delayChecked, cancellationChecked, cancellationReleaseChecked;
            public bool deadlineInjected, deadlineReleaseChecked;
            public bool seatDisableInjected, seatDisableReleaseChecked;
            public string cancelledPassenger, deadlinePassenger, coffeeScreenshot, readingScreenshot;
            public List<FileEvidence> files = new List<FileEvidence>();
            public List<Person> travellers = new List<Person>();
            public List<Service> services = new List<Service>();
            public List<CameraFrame> framing = new List<CameraFrame>();
            public List<string> errors = new List<string>(), warnings = new List<string>();
            public string fixture = "Runtime-only coverage: actual services delayed600s; default scenario events cleared; first4 needs favor Coffee, next4 Reading/Shop, next4 Rest with optionalNeedWeight100000. Initial display and full route/budget policy stay active. One ordinary background traveller requests each shop through public FIFO, including ticketing. One seated actor receives delay/cancellation while paused; another receives a closed-cutoff observation. No actor teleport, forced activity selection, saved profile edit, or temporary animation replacement. Native timeScale1;180–300sim bounded.";
            public string scope = "Integration coverage, not proof that crowds never jam. Queue waiting and travel stalls are separate. Seated root/hip geometry and authored clip are checked, not detailed hand/object contact. Default policy behaviour requires a separate unmodified run.";
        }
        sealed class Watch
        {
            public StationDecisionPassenger actor;
            public Person result;
            public StationSeat seat;
            public string state;
            public Vector3 position, goal, movementAnchor;
            public float time, lastProgress, lastMovement, bestRemaining = float.PositiveInfinity, seatedAt = -1f;
        }
        sealed class ServiceWatch { public StationInformationDesk desk; public Service result; public StationPassenger visitor; public float idleSince = -1f; }

        static StationDecisionLabAmenitiesVerification()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            Application.logMessageReceived += OnLog;
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Validate amenity circulation and seats")]
        public static void ValidateSavedLayout()
        {
            RequireEditAndClean();
            if (SceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var scene = SceneManager.GetActiveScene();
            var report = new Layout { checkedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path, files = SnapshotFiles() };
            try
            {
                var w = Components<StationDecisionWorld>(scene).Single();
                var chairs = w.pois.Where(p => p && p.seats != null).SelectMany(p => p.seats).Where(s => s).Distinct().ToArray();
                Require(chairs.Length > 0 && chairs.All(s => !string.IsNullOrEmpty(s.seatId)) &&
                    chairs.Select(s => s.seatId).Distinct().Count() == chairs.Length, "Missing or duplicate activity seats.");
                report.seats = chairs.Length;
                report.seatingPois = w.pois.Count(p => p.seatedActivity != DecisionSeatedActivity.None);
                foreach (var kind in new[] { DecisionSeatedActivity.Coffee, DecisionSeatedActivity.Reading, DecisionSeatedActivity.Rest })
                    Require(w.pois.Any(p => p.seatedActivity == kind && p.seats.Length > 0), "Missing seated activity: " + kind);
                var surface = Components<Unity.AI.Navigation.NavMeshSurface>(scene).Single();
                report.navigationAsset = AssetDatabase.GetAssetPath(surface.navMeshData);
                Require(report.navigationAsset.StartsWith("Assets/TrainStation/DecisionLab/", StringComparison.Ordinal), "Lab NavMesh is not isolated.");
                Physics.SyncTransforms();
                foreach (var seat in chairs)
                {
                    Require(seat.seatedPose && seat.approachPoint, "Missing seat root/approach: " + seat.seatId);
                    float d = Vector3.Distance(seat.seatedPose.position, seat.ApproachPosition);
                    Require(d >= .1f && d <= 1.2f, "Seat transition is not adjacent: " + seat.seatId + "/" + d);
                    Require(Snap(seat.ApproachPosition, out var approach) && Clear(approach, .25f, null), "Obstructed or off-NavMesh chair approach: " + seat.seatId);
                    ++report.seatApproachChecks;
                    foreach (var p in w.participants)
                    { Require(Route(p.transform.position, approach), "No route from " + p.passengerId + " to " + seat.seatId); ++report.routeChecks; }
                }
                for (int i = 0; i < chairs.Length; ++i) for (int j = i + 1; j < chairs.Length; ++j)
                    Require(Flat(chairs[i].seatedPose.position, chairs[j].seatedPose.position) >= .6f, "Chair root poses overlap.");
                var root = scene.GetRootGameObjects().SingleOrDefault(g => g.name == "14 Decision Lab — accessible amenities");
                Require(root, "Missing amenities circulation root.");
                var circulation = root.transform.Find("Circulation");
                Require(circulation && circulation.childCount >= 4, "Missing four service circulation marker groups.");
                foreach (Transform service in circulation)
                {
                    var entry = service.Find("Entry"); var front = service.Find("Front aisle");
                    var departure = service.Find("Departure"); var hall = service.Find("Hall approach");
                    Require(entry && front && departure && hall, "Incomplete circulation markers: " + service.name);
                    bool info = service.name == "Info";
                    CheckGate(service.name, entry, info ? 2.4f : 4f, info ? 1.2f : 2.8f, report);
                    CheckGate(service.name, front, info ? 2.4f : 3f, info ? 1.2f : 1.8f, report);
                    Require(Route(hall.position, entry.position) && Route(entry.position, front.position) &&
                        Route(front.position, departure.position) && Route(departure.position, hall.position), "Incomplete entry/exit circuit: " + service.name);
                    report.departureRoutes += 4;
                }
                foreach (var poi in w.pois.Where(p => p.service))
                {
                    Require(poi.departurePoint && poi.service.queueSlots.Length > 0, "Missing counter departure point: " + poi.poiId);
                    Require(Route(poi.service.queueSlots[0].position, poi.departurePoint.position), "Counter departure route incomplete: " + poi.poiId);
                    ++report.departureRoutes;
                    foreach (var slot in poi.service.queueSlots)
                    {
                        Require(slot && Snap(slot.position, out var location) && Clear(slot.position, .25f, null), "Obstructed/off-NavMesh queue slot: " + poi.poiId);
                        foreach (var p in w.participants)
                        { Require(Route(p.transform.position, slot.position), "Counter arrival route incomplete: " + poi.poiId); ++report.routeChecks; }
                    }
                    foreach (var seat in poi.seats ?? Array.Empty<StationSeat>())
                    { Require(Route(poi.departurePoint.position, seat.ApproachPosition), "No counter-to-chair route: " + poi.poiId); ++report.departureRoutes; }
                }
                report.success = true;
            }
            catch (Exception e) { report.errors.Add(e.ToString()); }
            report.filesPreserved = CheckFiles(report.files); report.success &= report.filesPreserved;
            Write(Output + "/amenities-layout-check.json", report);
            Require(report.success, string.Join("\n", report.errors));
        }

        static void CheckGate(string service, Transform marker, float physical, float nav, Layout report)
        {
            var gate = new Gate { service = service, marker = marker.name, position = marker.position, across = marker.right,
                requiredPhysicalWidth = physical, requiredNavCenterWidth = nav, physicalClear = true };
            report.gates.Add(gate);
            Require(Snap(marker.position, out var center), "Circulation center is off NavMesh: " + service + "/" + marker.name);
            var right = marker.right; right.y = 0; right.Normalize();
            gate.measuredNavCenterWidth = RayWidth(center, right) + RayWidth(center, -right);
            float half = physical * .5f - .25f;
            int n = Mathf.CeilToInt(half * 2f / .2f);
            for (int i = 0; i <= n; ++i)
            { gate.physicalClear &= Clear(center + right * Mathf.Lerp(-half, half, i / (float)n), .24f, gate.blockers); ++gate.capsules; }
            gate.complete = gate.physicalClear && gate.measuredNavCenterWidth + .05f >= nav;
            Require(gate.complete, "Insufficient aisle: " + service + "/" + marker.name + " nav=" + gate.measuredNavCenterWidth.ToString("0.00") + "m; blockers=" + string.Join(",", gate.blockers));
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Check amenity arrivals seating and release")]
        public static void BeginPlayCheck()
        {
            Begin(false);
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Check seated follow framing")]
        public static void BeginFramingCheck()
        {
            Begin(true);
        }

        static void Begin(bool framingOnly)
        {
            Require(string.IsNullOrEmpty(SessionState.GetString(Key, "")), "Amenities verification already active.");
            ValidateSavedLayout();
            run = new Run { startedUtc = DateTime.UtcNow.ToString("O"), phase = "entering", deadlineTicks = DateTime.UtcNow.AddMinutes(8).Ticks,
                framingOnly = framingOnly,
                archivePath = Output + (framingOnly ? "/amenities-framing-" : "/amenities-") + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json", files = SnapshotFiles(),
                oldOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled, oldOptions = (int)EditorSettings.enterPlayModeOptions,
                oldPaused = EditorApplication.isPaused, oldBackground = Application.runInBackground, oldFps = Application.targetFrameRate,
                oldVsync = QualitySettings.vSyncCount, oldScale = Time.timeScale, oldPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene) };
            if (framingOnly) run.fixture = "Runtime-only camera coverage: same needs and +600s trip delay as seating fixture; no interruption test. Follow default selection without changing orbit/zoom, wait at least0.5s, check physical camera volume, focus ray, distance and projected head/hip/body before each screenshot. Stop after both Coffee and Reading captures; native1x, maximum150sim.";
            Save(); EditorSettings.enterPlayModeOptionsEnabled = false;
            EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            EditorApplication.isPaused = false; EditorApplication.isPlaying = true;
        }

        static void Initialize()
        {
            var scene = SceneManager.GetActiveScene(); Require(scene.path == ScenePath, "Wrong fixture scene.");
            world = Components<StationDecisionWorld>(scene).Single(); dashboard = Components<StationDecisionDashboard>(scene).Single();
            Require(world.rail && world.rail.IsReady, "Rail is not initialized.");
            Time.timeScale = 1f; Application.runInBackground = true; Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0;
            run.startedSimulation = world.ElapsedSeconds; run.outputDirectory = world.OutputDirectory;
            people.Clear(); services.Clear(); seats.Clear(); kinds.Clear(); lastFrame = -1; requestStop = false;
            pausedActor = deadlineActor = captureActor = disabledSeatActor = null; pausedSeat = deadlineSeat = disabledSeat = null;
            cameraHoldUntil = 0;
            world.events.Clear();
            foreach (var id in world.participants.Select(p => p.serviceId).Distinct())
                world.ApplyEvent(new StationDecisionScenarioEvent { kind = DecisionScenarioEventKind.Delay, serviceId = id,
                    atSeconds = world.ElapsedSeconds, delaySeconds = 600f, announceImmediately = true });
            for (int i = 0; i < world.participants.Length; ++i)
            {
                var p = world.participants[i]; string need = i < 4 ? "Coffee" : i < 8 ? "Reading" : i < 12 ? "Rest" : "unchanged";
                if (i < 12)
                {
                    p.needs = new DecisionNeeds { thirst = i < 4 ? 1f : 0f, shopping = i >= 4 && i < 8 ? 1f : 0f,
                        fatigue = i >= 8 ? 1f : 0f, toilet = 0f, information = .1f };
                    p.policyParameters.optionalNeedWeight = 100000f;
                }
                var result = new Person { id = p.passengerId, fixtureNeed = need }; run.travellers.Add(result);
                people[p.passengerId] = new Watch { actor = p, result = result, position = p.transform.position, movementAnchor = p.transform.position,
                    time = world.ElapsedSeconds, lastProgress = world.ElapsedSeconds, lastMovement = world.ElapsedSeconds };
            }
            seats.AddRange(world.pois.Where(p => p.seats != null).SelectMany(p => p.seats).Where(s => s).Distinct());
            var volunteers = Components<StationPassenger>(scene).Where(p => p.gameObject.activeInHierarchy && !p.externalActivityControl &&
                p.IsAvailableForActivity && p.activity != StationPassengerActivity.Seated).ToList();
            foreach (var desk in Components<StationInformationDesk>(scene).Where(d => d.isActiveAndEnabled))
            {
                var result = new Service { name = desk.serviceName }; var watch = new ServiceWatch { desk = desk, result = result };
                if (desk.serviceKind == StationServiceKind.Shop)
                {
                    Require(volunteers.Count > 0, "Missing background service coverage volunteer.");
                    watch.visitor = volunteers[0]; volunteers.RemoveAt(0); result.fixtureVisitor = watch.visitor.name;
                }
                services.Add(desk, watch); run.services.Add(result);
                desk.queueJoined.AddListener(p => Joined(desk, p));
                desk.conversationStarted.AddListener(p => Served(desk, p, false));
                desk.conversationEnded.AddListener(p => Served(desk, p, true));
            }
            var loop = PlayerLoop.GetCurrentPlayerLoop(); RemoveHook(ref loop);
            run.postAnimationHook = InstallHook(ref loop);
            Require(run.postAnimationHook, "Could not install post-animation amenities observation.");
            PlayerLoop.SetPlayerLoop(loop);
            run.phase = "running"; nextWrite = EditorApplication.timeSinceStartup + 1d; Save();
        }

        static void Observe()
        {
            if (lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount; ++run.observations; float now = world.ElapsedSeconds;
            run.elapsedSimulation = now - run.startedSimulation;
            var claims = new HashSet<StationSeat>();
            foreach (var watch in people.Values)
            {
                var p = watch.actor; var r = watch.result; Require(p, "Retained participant missing.");
                var chair = p.ReservedSeat;
                if (chair)
                {
                    Check(claims.Add(chair), "Two participants claim " + chair.seatId);
                    if (chair != disabledSeat || chair.enabled) Check(chair.ReservedBy == p.passenger, "Seat owner mismatch: " + p.passengerId);
                    if (chair != watch.seat) ++r.reservations; ++run.ownershipChecks;
                }
                if (watch.seat && watch.seat != chair)
                {
                    ++r.releases;
                    if ((p.LastDecisionReason ?? "").StartsWith("Completed ", StringComparison.Ordinal)) ++r.completedSeatedVisits;
                    Check(watch.seat.ReservedBy != p.passenger && watch.seat.Occupant != p.passenger, "Former chair retained its owner: " + p.passengerId);
                    if (!p.Finished) Check(p.motion.agent.enabled && p.motion.agent.isOnNavMesh, "Chair released before navigation restored: " + p.passengerId);
                }
                if (p.IsSeated)
                {
                    if (watch.state != "Seated") ++r.occupations;
                    ++r.seatedSamples; if (!world.Paused) r.seatedSeconds += Mathf.Max(0, now - watch.time);
                    Check(p.ExecutionState == "Seated" && chair && (chair == disabledSeat && !chair.enabled || chair.Occupant == p.passenger), "Seated state lacks physical occupancy: " + p.passengerId);
                    Check(p.HasCheckedDisplay, "Seated fixture bypassed initial display: " + p.passengerId);
                    Check(!p.motion.agent.enabled, "Seated actor is driven by navigation: " + p.passengerId);
                    if (chair) Check(Vector3.Distance(p.transform.position, chair.seatedPose.position) <= .04f, "Seated root differs from chair pose: " + p.passengerId);
                    string kind = p.SeatedActivity.ToString(); kinds.Add(kind); if (!r.seatedKinds.Contains(kind)) r.seatedKinds.Add(kind);
                    if (watch.seatedAt < 0) watch.seatedAt = now;
                    if (now - watch.seatedAt >= 1f) { SeatedPose(p, r); ConsiderCapture(p, kind); }
                }
                else watch.seatedAt = -1f;
                if (p.Finished) Check(!chair, "Terminal journey retains a chair: " + p.passengerId);
                if (watch.state == "SittingDown" || watch.state == "StandingUp" || p.ExecutionState == "SittingDown" || p.ExecutionState == "StandingUp")
                {
                    float step = Vector3.Distance(watch.position, p.transform.position); r.maximumSeatStep = Mathf.Max(r.maximumSeatStep, step);
                    Check(step <= Mathf.Max(.08f, 2.5f * (now - watch.time) + .08f), "Local seat transition jumped: " + p.passengerId + "/" + step);
                }
                if (p.ExecutionState == "SittingDown" && watch.state == "WalkingToSeat" && chair)
                    Check(Flat(watch.position, chair.ApproachPosition) <= .25f, "Sit started away from the actual chair approach: " + p.passengerId);
                Travel(watch, now);
                if (!string.IsNullOrEmpty(p.NavigationIssue) && !r.issues.Contains(p.NavigationIssue))
                { r.issues.Add(p.NavigationIssue); run.warnings.Add(p.passengerId + ": " + p.NavigationIssue); }
                Check(!p.motion.animationPlayer || string.IsNullOrEmpty(p.motion.animationPlayer.LastError), "Animation player error: " + p.passengerId);
                watch.seat = chair; watch.state = p.ExecutionState; watch.position = p.transform.position; watch.time = now;
                r.lastExecution = p.ExecutionState; r.lastSeat = p.CurrentSeatId;
            }
            foreach (var chair in seats)
            {
                Check(!chair.Occupant || chair.Occupant == chair.ReservedBy, "Chair occupant differs from reservation owner: " + chair.seatId);
                if (chair.ReservedBy) Check(chair.ReservedBy.gameObject.activeInHierarchy, "Inactive actor retains chair: " + chair.seatId);
            }
            var occupied = seats.Where(s => s.Occupant).ToArray();
            for (int i = 0; i < occupied.Length; ++i) for (int j = i + 1; j < occupied.Length; ++j)
            { ++run.overlapChecks; Check(Flat(occupied[i].Occupant.transform.position, occupied[j].Occupant.transform.position) >= .5f, "Seated actors overlap."); }
            foreach (var watch in services.Values)
            {
                var desk = watch.desk; watch.result.queuePeak = Mathf.Max(watch.result.queuePeak, desk.QueueCount);
                if (desk.QueueCount > 0 && !desk.ActiveCustomer)
                { if (watch.idleSince < 0) watch.idleSince = now; watch.result.maximumWaitingWithoutService = Mathf.Max(watch.result.maximumWaitingWithoutService, now - watch.idleSince); }
                else watch.idleSince = -1f;
                if (watch.visitor && !watch.result.fixtureRequested && !world.Paused && watch.visitor.IsAvailableForActivity)
                    watch.result.fixtureRequested = watch.visitor.TryVisitService(desk);
            }
            run.coffeeObserved = kinds.Contains("Coffee"); run.readingObserved = kinds.Contains("Reading"); run.restObserved = kinds.Contains("Rest");
            CapturePending();
            if (!run.framingOnly) Interruptions();
            if (!world.Paused && (run.framingOnly ? run.elapsedSimulation >= 150f ||
                FramingCovered() && EditorApplication.timeSinceStartup >= cameraHoldUntil : run.elapsedSimulation >= 300f ||
                run.elapsedSimulation >= 180f && Covered())) requestStop = true;
        }

        static void SeatedPose(StationDecisionPassenger p, Person r)
        {
            var animator = p.motion.animationPlayer.TargetAnimator;
            Require(animator && animator.avatar && animator.avatar.isHuman, "Seated actor lacks Humanoid.");
            var hip = animator.GetBoneTransform(HumanBodyBones.Hips);
            float height = p.transform.InverseTransformPoint(hip.position).y;
            r.minimumHipHeight = Mathf.Min(r.minimumHipHeight, height); r.maximumHipHeight = Mathf.Max(r.maximumHipHeight, height);
            ++r.poseChecks; ++run.seatedPoseChecks;
            Check(height >= .25f && height <= .9f, "Measured hip is not seated: " + p.passengerId + "/" + height);
            Check(p.motion.CurrentMotion == p.motion.sitIdleId, "Seated dwell plays another clip: " + p.passengerId);
            Check(animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y < hip.position.y + .15f &&
                animator.GetBoneTransform(HumanBodyBones.RightFoot).position.y < hip.position.y + .15f, "Seated foot exceeds hip height: " + p.passengerId);
        }
        static void Travel(Watch w, float now)
        {
            var p = w.actor; var a = p.motion.agent;
            bool travel = p.ExecutionState == "Walking" || p.ExecutionState == "WalkingToSeat" || p.ExecutionState == "ClearingCounter" ||
                p.ExecutionState == "Service" && p.passenger.CurrentState == StationPassengerState.ApproachingQueue;
            if (world.Paused || !travel || !a || !a.enabled || !a.isOnNavMesh || a.pathPending || !a.hasPath || a.remainingDistance <= a.stoppingDistance + .4f)
            { w.lastProgress = w.lastMovement = now; w.movementAnchor = p.transform.position; w.bestRemaining = float.PositiveInfinity; return; }
            ++w.result.navigationSamples;
            bool measurable = StationDecisionNavigationProgress.TryGetRemainingDistance(p.motion, out float remaining);
            if (!measurable) ++w.result.remainingDistanceUnavailableSamples;
            else if (float.IsInfinity(a.remainingDistance) || float.IsNaN(a.remainingDistance) || a.remainingDistance < 0f)
                ++w.result.remainingDistanceFallbackSamples;
            bool changedGoal = Vector3.Distance(a.destination, w.goal) > .25f;
            if (changedGoal || measurable && remaining < w.bestRemaining - .15f)
            { w.lastProgress = now; w.bestRemaining = measurable ? remaining : float.PositiveInfinity; w.goal = a.destination; }
            if (changedGoal || Flat(w.movementAnchor, p.transform.position) >= .15f)
            { w.lastMovement = now; w.movementAnchor = p.transform.position; }
            float idle = now - w.lastProgress; w.result.maximumNoProgressSeconds = Mathf.Max(w.result.maximumNoProgressSeconds, idle);
            float stationary = now - w.lastMovement; w.result.maximumStationarySeconds = Mathf.Max(w.result.maximumStationarySeconds, stationary);
            Check(a.pathStatus == NavMeshPathStatus.PathComplete, "Incomplete live path: " + p.passengerId);
            if (measurable && idle > 25f) Check(false, "Non-queue travel stalled over25s: " + p.passengerId + "/" + p.ExecutionState);
            if (stationary > 25f) Check(false, "Non-queue actor physically stationary over25s: " + p.passengerId + "/" + p.ExecutionState);
        }
        static void Joined(StationInformationDesk desk, StationPassenger p)
        {
            if (run == null || !services.TryGetValue(desk, out var w)) return;
            ++w.result.joined; ++w.result.physicalArrivalChecks;
            Check(desk.TryGetQueueSlot(p, out var slot) && Flat(p.transform.position, slot.position) <= desk.arrivalTolerance + .08f,
                "FIFO admitted distant customer: " + desk.serviceName + "/" + p.name);
        }
        static void Served(StationInformationDesk desk, StationPassenger p, bool ended)
        {
            if (run == null || !services.TryGetValue(desk, out var w)) return;
            if (ended) ++w.result.ended;
            else { ++w.result.began; ++w.result.servicePositionChecks; Check(desk.GetQueueIndex(p) == 0 && desk.IsWithinServiceArea(p.transform.position), "Service started away from physical head: " + desk.serviceName); }
        }
        static void ConsiderCapture(StationDecisionPassenger p, string kind)
        {
            if (captureActor || world.Paused || EditorApplication.timeSinceStartup < cameraHoldUntil || kind != "Coffee" && kind != "Reading" ||
                kind == "Coffee" && !string.IsNullOrEmpty(run.coffeeScreenshot) || kind == "Reading" && !string.IsNullOrEmpty(run.readingScreenshot)) return;
            captureActor = p; captureKind = kind; captureAt = EditorApplication.timeSinceStartup + .5d;
            dashboard.SelectParticipant(Array.IndexOf(world.participants, p));
        }
        static void CapturePending()
        {
            if (!captureActor || EditorApplication.timeSinceStartup < captureAt) return;
            if (captureActor.IsSeated)
            {
                MeasureFraming(captureActor, captureKind);
                string path = Path.GetFullPath(Output + "/amenities-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-seated-" + captureKind.ToLowerInvariant() + ".png");
                Directory.CreateDirectory(Output); ScreenCapture.CaptureScreenshot(path);
                if (captureKind == "Coffee") run.coffeeScreenshot = path; else run.readingScreenshot = path;
                cameraHoldUntil = EditorApplication.timeSinceStartup + 1d;
            }
            captureActor = null;
        }
        static void MeasureFraming(StationDecisionPassenger p, string kind)
        {
            var follow = dashboard.followCamera;
            Require(follow && follow.IsFollowing && follow.FollowedPassenger == p && follow.controlledCamera,
                "Selected seated actor is not the camera target.");
            var camera = follow.controlledCamera;
            var animator = p.motion.animationPlayer.TargetAnimator;
            var points = new[] { HumanBodyBones.Head, HumanBodyBones.Hips, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot }
                .Select(b => camera.WorldToViewportPoint(animator.GetBoneTransform(b).position)).ToArray();
            var result = new CameraFrame { passenger = p.passengerId, activity = kind, actualDistance = follow.ActualDistance,
                requestedDistance = follow.RequestedDistance, yaw = follow.CurrentYaw, pitch = follow.CurrentPitch,
                cameraPosition = camera.transform.position, focusPosition = follow.FocusPosition,
                headViewport = points[0], hipViewport = points[1], bodyViewportHeight = points.Max(v => v.y) - points.Min(v => v.y),
                noVolumeOverlap = true, clearFocusRay = true };
            bool Ignore(Collider c) => !c || c.GetComponentInParent<StationActorMotion>() || c.GetComponentInParent<StationDecisionPassenger>() ||
                c.transform == camera.transform || c.transform.IsChildOf(camera.transform) ||
                follow.ignoredCollisionRoots != null && follow.ignoredCollisionRoots.Any(r => r && (c.transform == r || c.transform.IsChildOf(r)));
            foreach (var c in Physics.OverlapSphere(camera.transform.position, follow.collisionRadius - .015f, follow.collisionMask, QueryTriggerInteraction.Ignore))
                if (!Ignore(c)) { result.noVolumeOverlap = false; result.blockers.Add("volume:" + c.name); }
            Vector3 ray = camera.transform.position - follow.FocusPosition;
            foreach (var hit in Physics.RaycastAll(follow.FocusPosition, ray.normalized, Mathf.Max(0, ray.magnitude - .02f), follow.collisionMask, QueryTriggerInteraction.Ignore))
                if (!Ignore(hit.collider)) { result.clearFocusRay = false; result.blockers.Add("ray:" + hit.collider.name); }
            result.visibleHeadAndHip = points.Take(2).All(v => v.z > camera.nearClipPlane && v.x >= .08f && v.x <= .92f && v.y >= .08f && v.y <= .92f);
            run.framing.Add(result);
            Check(result.actualDistance >= 1.5f, "Seated follow collapsed below1.5m: " + p.passengerId + "/" + result.actualDistance);
            Check(result.noVolumeOverlap && result.clearFocusRay, "Seated camera intersects architecture: " + p.passengerId);
            Check(result.visibleHeadAndHip && result.bodyViewportHeight >= .10f && result.bodyViewportHeight <= .85f,
                "Seated person is not framed at a reasonable body scale: " + p.passengerId + "/" + result.bodyViewportHeight);
        }
        static bool FramingCovered() => run.framing.Any(f => f.activity == "Coffee") && run.framing.Any(f => f.activity == "Reading") &&
            File.Exists(run.coffeeScreenshot ?? "") && File.Exists(run.readingScreenshot ?? "");

        static void Interruptions()
        {
            float now = world.ElapsedSeconds;
            if (!pausedActor && !run.pauseChecked && !captureActor && EditorApplication.timeSinceStartup >= cameraHoldUntil &&
                !string.IsNullOrEmpty(run.coffeeScreenshot) && !string.IsNullOrEmpty(run.readingScreenshot))
            {
                pausedActor = world.participants.FirstOrDefault(p => p.IsSeated && p.SeatedSecondsRemaining > 3f);
                if (pausedActor)
                {
                    pausedSeat = pausedActor.ReservedSeat; run.cancelledPassenger = pausedActor.passengerId;
                    dashboard.SelectParticipant(Array.IndexOf(world.participants, pausedActor));
                    pauseRoots = world.participants.Select(p => p.transform.position).ToArray(); pauseBones = Bones(pausedActor);
                    pauseClock = now; world.TogglePause(); pauseAt = EditorApplication.timeSinceStartup;
                }
            }
            if (pausedActor && world.Paused && !run.pauseChecked)
            {
                double duration = EditorApplication.timeSinceStartup - pauseAt;
                Check(Mathf.Abs(now - pauseClock) < .001f, "Seated Pause advanced world clock.");
                for (int i = 0; i < world.participants.Length; ++i) run.maximumPausedRootDrift = Mathf.Max(run.maximumPausedRootDrift, Vector3.Distance(pauseRoots[i], world.participants[i].transform.position));
                var b = Bones(pausedActor);
                for (int i = 0; i < b.Length; ++i) run.maximumPausedBoneDrift = Mathf.Max(run.maximumPausedBoneDrift, Vector3.Distance(pauseBones[i], b[i]));
                Check(run.maximumPausedRootDrift < .001f && run.maximumPausedBoneDrift < .001f, "Paused announcement moved root/bones.");
                if (duration >= .5d && !run.delayChecked)
                { world.ApplyEvent(new StationDecisionScenarioEvent { kind = DecisionScenarioEventKind.Delay, serviceId = pausedActor.serviceId, atSeconds = now, delaySeconds = 45f, announceImmediately = true }); run.delayChecked = true; }
                if (duration >= 1d && !run.cancellationChecked)
                { world.ApplyEvent(new StationDecisionScenarioEvent { kind = DecisionScenarioEventKind.Cancellation, serviceId = pausedActor.serviceId, atSeconds = now, announceImmediately = true }); run.cancellationChecked = true; }
                Check(pausedActor.ReservedSeat == pausedSeat && pausedSeat.ReservedBy == pausedActor.passenger, "Paused interruption released chair before standing.");
                if (duration >= 2d)
                { run.pauseSeconds = (float)duration; world.TogglePause(); run.pauseChecked = true; releaseBy = now + 18f; }
            }
            if (run.pauseChecked && !run.cancellationReleaseChecked)
            {
                if (!pausedActor.ReservedSeat && pausedSeat.ReservedBy != pausedActor.passenger && pausedSeat.Occupant != pausedActor.passenger)
                { run.cancellationReleaseChecked = true; Check(pausedActor.motion.agent.enabled && pausedActor.motion.agent.isOnNavMesh, "Cancellation did not restore navigation."); }
                else if (now > releaseBy) Check(false, "Cancellation did not release chair within18s.");
            }
            if (run.pauseChecked && !run.deadlineInjected && !world.Paused)
            {
                deadlineActor = world.participants.FirstOrDefault(p => p.IsSeated && p.serviceId != pausedActor.serviceId && p.SeatedSecondsRemaining > 2f);
                if (deadlineActor)
                {
                    deadlineSeat = deadlineActor.ReservedSeat; run.deadlinePassenger = deadlineActor.passengerId;
                    var known = deadlineActor.KnownTrip; known.cutoffSeconds = now - .01f; known.departureSeconds = now + 8f; known.status = DecisionTrainStatus.Closed;
                    deadlineActor.ReceiveAnnouncement(known, "Amenities verification: observed boarding cutoff expired");
                    run.deadlineInjected = true; deadlineReleaseBy = now + 18f;
                }
            }
            if (run.deadlineInjected && !run.deadlineReleaseChecked)
            {
                if (!deadlineActor.ReservedSeat && deadlineSeat.ReservedBy != deadlineActor.passenger && deadlineSeat.Occupant != deadlineActor.passenger)
                { run.deadlineReleaseChecked = true; Check(deadlineActor.motion.agent.enabled && deadlineActor.motion.agent.isOnNavMesh, "Cutoff interruption did not restore navigation."); }
                else if (now > deadlineReleaseBy) Check(false, "Cutoff did not release chair within18s.");
            }
            if (run.deadlineInjected && !run.seatDisableInjected && !world.Paused)
            {
                disabledSeatActor = world.participants.FirstOrDefault(p => p.IsSeated && p != pausedActor && p != deadlineActor && p.SeatedSecondsRemaining > 2f);
                if (disabledSeatActor)
                {
                    disabledSeat = disabledSeatActor.ReservedSeat;
                    disabledSeat.enabled = false;
                    run.seatDisableInjected = true; disabledReleaseBy = now + 18f;
                }
            }
            if (run.seatDisableInjected && !run.seatDisableReleaseChecked)
            {
                if (!disabledSeatActor.ReservedSeat && disabledSeat.ReservedBy != disabledSeatActor.passenger && disabledSeat.Occupant != disabledSeatActor.passenger)
                {
                    run.seatDisableReleaseChecked = true;
                    Check(disabledSeatActor.motion.agent.enabled && disabledSeatActor.motion.agent.isOnNavMesh, "Seat disable did not restore navigation.");
                    disabledSeat.enabled = true;
                }
                else if (now > disabledReleaseBy) Check(false, "Disabled seat did not complete safe standing within18s.");
            }
        }
        static bool Covered() => FramingCovered() && run.coffeeObserved && run.readingObserved && run.restObserved && run.pauseChecked &&
            run.cancellationReleaseChecked && run.deadlineReleaseChecked && run.seatDisableReleaseChecked && !string.IsNullOrEmpty(run.coffeeScreenshot) &&
            !string.IsNullOrEmpty(run.readingScreenshot) && services.Values.Where(s => s.desk.serviceKind == StationServiceKind.Shop).All(s => s.result.joined > 0 && s.result.ended > 0) &&
            run.travellers.Sum(p => p.releases) >= 3 && run.travellers.Sum(p => p.completedSeatedVisits) > 0;
        static void Complete()
        {
            run.completionReached = true;
            Check(run.framingOnly ? FramingCovered() : Covered(), run.framingOnly ? "Seated camera framing coverage was not reached in150sim." :
                "Required physical shop/seat/pause/interruption coverage was not reached in300sim.");
            Check(File.Exists(run.coffeeScreenshot ?? "") && File.Exists(run.readingScreenshot ?? ""), "Actual seated Game screenshots were not written.");
            foreach (var s in services.Values.Where(s => !run.framingOnly && s.desk.serviceKind == StationServiceKind.Shop))
                Check(s.result.joined > 0 && s.result.began > 0 && s.result.ended > 0, "Shop lacked a complete physical service visit: " + s.result.name);
            if (world.Paused) world.TogglePause(); world.FlushLogs(); run.phase = "exiting"; Save(); WriteReport(); EditorApplication.isPlaying = false;
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return; nextPoll = EditorApplication.timeSinceStartup + .05d;
            Load(); if (run == null || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            try
            {
                if (run.phase == "restoring" && !EditorApplication.isPlayingOrWillChangePlaymode) { Restore(); return; }
                Require(DateTime.UtcNow.Ticks < run.deadlineTicks, "Amenities wall-clock timeout.");
                if (run.phase == "initializing" && EditorApplication.isPlaying && Time.frameCount >= run.initializeFrame) Initialize();
                else if (run.phase == "running" && EditorApplication.isPlaying)
                { Require(world, "Domain reload invalidated live fixture; partial evidence retained."); if (requestStop) Complete(); }
                if (EditorApplication.timeSinceStartup >= nextWrite) { nextWrite = EditorApplication.timeSinceStartup + 1d; Save(); WriteReport(); }
            }
            catch (Exception e)
            {
                Check(false, e.ToString()); run.phase = "exiting"; Save(); WriteReport();
                if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false; else Restore();
            }
        }
        static void ModeChanged(PlayModeStateChange change)
        {
            Load(); if (run == null) return;
            if (change == PlayModeStateChange.EnteredPlayMode) { run.phase = "initializing"; run.initializeFrame = Time.frameCount + 15; Save(); }
            else if (change == PlayModeStateChange.ExitingPlayMode)
            {
                var loop = PlayerLoop.GetCurrentPlayerLoop(); RemoveHook(ref loop); PlayerLoop.SetPlayerLoop(loop);
                if (!run.completionReached) Check(false, "Play stopped before coverage completed.");
                run.phase = "exiting"; Save();
            }
            else if (change == PlayModeStateChange.EnteredEditMode) { run.phase = "restoring"; Save(); }
        }
        static void Restore()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop(); RemoveHook(ref loop); PlayerLoop.SetPlayerLoop(loop);
            EditorSettings.enterPlayModeOptionsEnabled = run.oldOptionsEnabled; EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)run.oldOptions;
            EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(run.oldPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(run.oldPlayScene);
            EditorApplication.isPaused = run.oldPaused; Application.runInBackground = run.oldBackground; Application.targetFrameRate = run.oldFps;
            QualitySettings.vSyncCount = run.oldVsync; Time.timeScale = run.oldScale;
            run.filesPreserved = CheckFiles(run.files.Where(f => f.path != ScenePath)); run.labPreserved = CheckFiles(run.files.Where(f => f.path == ScenePath));
            Check(run.filesPreserved && run.labPreserved, "Saved scene/schedule/navigation assets changed during test.");
            Check(run.completionReached && run.observations > 0 && run.seatedPoseChecks > 0, "Fixture did not reach executed completion.");
            run.completedUtc = DateTime.UtcNow.ToString("O"); run.phase = "complete"; run.success = run.errors.Count == 0;
            WriteReport(); SessionState.EraseString(Key); run = null; world = null; dashboard = null;
        }
        static void OnLog(string message, string stack, LogType type)
        { if (run != null && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)) Check(false, message); }
        static void Check(bool condition, string message) { if (!condition && run != null && run.errors.Count < 100 && !run.errors.Contains(message)) run.errors.Add(message); }
        static void Save() { if (run != null) SessionState.SetString(Key, JsonUtility.ToJson(run)); }
        static void Load() { if (run == null) { string json = SessionState.GetString(Key, ""); if (!string.IsNullOrEmpty(json)) run = JsonUtility.FromJson<Run>(json); } }
        static void WriteReport() { if (run != null) { Write(Output + (run.framingOnly ? "/amenities-framing-check.json" : "/amenities-play-check.json"), run); Write(run.archivePath, run); } }
        static Vector3[] Bones(StationDecisionPassenger p)
        {
            var a = p.motion.animationPlayer.TargetAnimator;
            return new[] { HumanBodyBones.Hips, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
                HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot }.Select(b => a.GetBoneTransform(b).position).ToArray();
        }
        static bool Snap(Vector3 point, out Vector3 position)
        {
            bool okay = NavMesh.SamplePosition(point, out var hit, .3f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - point.y) < .2f;
            position = okay ? hit.position : point; return okay;
        }
        static bool Route(Vector3 from, Vector3 to)
        {
            if (!Snap(from, out var a) || !Snap(to, out var b)) return false;
            var path = new NavMeshPath(); return NavMesh.CalculatePath(a, b, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
        }
        static float RayWidth(Vector3 point, Vector3 direction) => NavMesh.Raycast(point, point + direction * 6f, out var hit, NavMesh.AllAreas) ? Vector3.Distance(point, hit.position) : 6f;
        static bool Clear(Vector3 point, float radius, List<string> blockers)
        {
            bool clear = true;
            foreach (var c in Physics.OverlapCapsule(point + Vector3.up * .4f, point + Vector3.up * 1.5f, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!c.enabled || !c.gameObject.activeInHierarchy || c.GetComponentInParent<StationActorMotion>() || c.bounds.max.y < point.y + .25f) continue;
                clear = false; if (blockers != null && !blockers.Contains(c.name)) blockers.Add(c.name);
            }
            return clear;
        }
        static float Flat(Vector3 a, Vector3 b) { a.y = b.y = 0; return Vector3.Distance(a, b); }
        static List<FileEvidence> SnapshotFiles()
        {
            var paths = new HashSet<string> { SourceScene, SourceSchedule, ScenePath, SchedulePath };
            foreach (string guid in AssetDatabase.FindAssets("t:NavMeshData", new[] { "Assets/TrainStation" })) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            return paths.Where(File.Exists).OrderBy(p => p, StringComparer.Ordinal).Select(p => new FileEvidence { path = p, before = Digest(p) }).ToList();
        }
        static bool CheckFiles(IEnumerable<FileEvidence> evidence)
        {
            bool okay = true;
            foreach (var f in evidence) { f.after = File.Exists(f.path) ? Digest(f.path) : "missing"; okay &= f.before == f.after; }
            return okay;
        }
        static string Digest(string path)
        { using (var sha = SHA256.Create()) using (var file = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); }
        static void AfterAnimation()
        {
            if (run == null || run.phase != "running" || requestStop) return;
            try { Observe(); }
            catch (Exception e) { Check(false, e.ToString()); requestStop = true; }
        }
        static bool InstallHook(ref PlayerLoopSystem loop)
        {
            if (loop.type == typeof(UnityEngine.PlayerLoop.PreLateUpdate))
            {
                var children = (loop.subSystemList ?? Array.Empty<PlayerLoopSystem>()).ToList();
                children.Add(new PlayerLoopSystem { type = typeof(AfterAmenitiesAnimation), updateDelegate = AfterAnimation });
                loop.subSystemList = children.ToArray(); return true;
            }
            if (loop.subSystemList == null) return false;
            for (int i = 0; i < loop.subSystemList.Length; ++i)
            { var child = loop.subSystemList[i]; if (InstallHook(ref child)) { loop.subSystemList[i] = child; return true; } }
            return false;
        }
        static void RemoveHook(ref PlayerLoopSystem loop)
        {
            if (loop.subSystemList == null) return;
            var children = new List<PlayerLoopSystem>();
            foreach (var original in loop.subSystemList)
            { if (original.type == typeof(AfterAmenitiesAnimation)) continue; var child = original; RemoveHook(ref child); children.Add(child); }
            loop.subSystemList = children.ToArray();
        }
    }
}
#endif
