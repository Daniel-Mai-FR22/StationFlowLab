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
    /// <summary>Observes real saved actors under automatic activity and physically travelled queue congestion.</summary>
    [InitializeOnLoad]
    public static class StationCrowdStressVerification
    {
        const string ScenePath = "Assets/TrainStation/Scenes/TrainStation.unity";
        const string StateKey = "StationFlow.StationCrowd.StressVerification";
        const string LogKey = StateKey + ".Errors";
        const string ReportPath = "Reports/TrainStationSocial/CollisionGait/crowd-stress-check.json";
        const float AutomaticDuration = 120f, TotalDuration = 300f;
        [Serializable] public sealed class SavedScene { public string path; public bool isLoaded, isActive; }
        [Serializable] public sealed class ActorResult
        {
            public string actor;
            public int samples, movingSamples, transitions, starts, stops;
            public float travelled, minimumRootUp = 1f, maximumFootAboveHip = -10f, maximumKneeAboveHip = -10f;
            public float maximumRootFloorHeight, minimumKneeAngle = 180f, maximumPlaybackSpeed;
        }
        [Serializable] public sealed class ServiceResult
        {
            public string service;
            public int automaticRequests, manualRequests, automaticCompletions, manualCompletions, admissions, conversations, queuePeak, physicalAdmissionChecks, fifoChecks;
            public float lastConversationStartedAt = -1f, maximumNoServiceDuration;
            public ServiceSnapshot latestSnapshot, longestIdleQueueSnapshot;
            public List<string> admissionOrder = new List<string>(), serviceOrder = new List<string>();
        }
        [Serializable] public sealed class QueueActorSnapshot
        {
            public string actor, state, currentService, assignedSlot, navigationIssue, lastIssue, pathStatus, motion;
            public int queueIndex = -1, assignedSlotIndex = -1;
            public bool agentEnabled, onNavMesh, hasPath, pathPending, isStopped, isAvoidingQueue;
            public float remainingDistance = -1f, stoppingDistance, distanceToSlot = -1f, distanceToPredecessor = -1f, lastAdvanceAt = -1f;
            public Vector3 rootPosition, assignedSlotPosition, velocity, desiredVelocity, destination;
        }
        [Serializable] public sealed class ServiceSnapshot
        {
            public float sampledAt, idleQueueDuration;
            public int queueCount;
            public string activeCustomer;
            public bool canAccept, hasPhysicalTailCapacity;
            public QueueActorSnapshot lastDeparted;
            public List<QueueActorSnapshot> queued = new List<QueueActorSnapshot>(), approaching = new List<QueueActorSnapshot>();
        }
        [Serializable] public sealed class ManualVisit
        {
            public string actor, finalState;
            public float requestedAt, admittedAt = -1, completedAt = -1, cancelledAt = -1, returnedAt = -1;
            public bool admitted, completed, cancelled, returned, stopped, resumed;
            public bool forcedStopped, challengeFinished;
            public int forcedStopCycles, forcedResumeCycles;
            public float pauseStartedAt, pauseRootTravel, resumedDistance;
            public Vector3 pausePosition, currentStopPosition;
        }
        [Serializable] public sealed class PoseRecord
        {
            public string actor, passengerState, motion, reason;
            public float time, speed, animatorSpeed, currentPhase, nextPhase, transitionPhase, rootFloorHeight;
            public bool transition, stabilizeFeet, agentEnabled, onNavMesh;
            public int currentStateHash, nextStateHash;
            public Vector3 root, rootUp, navPosition, hips, head, leftKnee, rightKnee, leftFoot, rightFoot;
        }
        [Serializable] public sealed class Run
        {
            public string phase, startedUtc, completedUtc, originalPlayScene, unityVersion, scenario;
            public bool success, aborted, capturedSettings, restoredSettings, restoredScenes, originalOptionsEnabled, originalPaused, originalRunInBackground;
            public bool automaticPhasePassed, congestionPassed, hallCaptured, anomalyCaptured;
            public int originalOptions, originalTargetFrameRate, originalVSyncCount, readyFrame;
            public float originalTimeScale, startedAt, simulatedSeconds, firstAnomalyAt = -1;
            public long deadlineTicks;
            public int baselineObjects, finalObjects, baselineCameras, finalCameras, baselineActiveCameras, baselineLights;
            public int sampledFrames, automaticRequests, automaticCompletions, automaticRequestsAt120, automaticCompletionsAt120;
            public int closePairSamples, distinctClosePairs, persistentOverlapPairs, manualTarget = 18;
            public float maximumOverlapDuration, minimumPairDistance = 1000f;
            public SavedScene[] scenes;
            public List<ActorResult> actors = new List<ActorResult>();
            public List<ServiceResult> services = new List<ServiceResult>();
            public List<ManualVisit> manual = new List<ManualVisit>();
            public List<PoseRecord> anomalies = new List<PoseRecord>();
            public List<string> errors = new List<string>(), overlapExamples = new List<string>();
        }
        sealed class Rig
        {
            public StationActorMotion motion; public StationPassenger passenger; public Animator animator;
            public Transform hips, head, leftKnee, rightKnee, leftFoot, rightFoot;
            public ActorResult result; public Vector3 previous; public bool moving;
            public PoseRecord previousPose;
        }
        sealed class ServiceWatch
        {
            public StationInformationDesk desk; public ServiceResult result;
            public StationPassenger lastDeparted;
            public float idleQueueSince = -1f, nextDiagnosticAt;
            public readonly List<StationPassenger> fifo = new List<StationPassenger>();
        }
        static Run live;
        static StationSocialWorld world;
        static Rig[] rigs;
        static readonly Dictionary<StationInformationDesk, ServiceWatch> services = new Dictionary<StationInformationDesk, ServiceWatch>();
        static readonly Dictionary<StationPassenger, ManualVisit> manual = new Dictionary<StationPassenger, ManualVisit>();
        static readonly Dictionary<StationPassenger, bool> currentTripManual = new Dictionary<StationPassenger, bool>();
        static readonly HashSet<int> encounters = new HashSet<int>(), persistentPairs = new HashSet<int>();
        static readonly Dictionary<int, float> overlaps = new Dictionary<int, float>();
        static readonly HashSet<string> anomalousActors = new HashSet<string>();
        static bool dispatchingManual;
        static double nextPoll, nextWrite;
        static float nextPairs, nextGround, nextDispatch;

        static StationCrowdStressVerification()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            Application.logMessageReceived += Log;
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Stress check 300 saved passengers")]
        public static void BeginPlayCheck()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating, "Finish Play and asset import before crowd stress verification.");
            Require(Load() == null, "A crowd stress check is already pending.");
            var run = new Run { phase = "opening", startedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion, deadlineTicks = DateTime.UtcNow.AddSeconds(600).Ticks,
                scenario = "120 simulated seconds of saved automatic activity, then 180 seconds with an 18-request accueil burst and repeated stop/start challenges. A new manual request may follow twelve existing customers: fifteen services at approximately 8.5 seconds including observed queue reaction need about 128 seconds, plus physical approach. The congestion window allows this sequential service while preserving every arrival/completion threshold. Any nonempty queue with no active conversation for 45 seconds still fails independently." };
            try
            {
                CleanScenes(); var setup = EditorSceneManager.GetSceneManagerSetup();
                Require(setup.Length > 0 && setup.All(s => !string.IsNullOrEmpty(s.path)), "Save all open scenes before the stress check.");
                run.scenes = setup.Select(s => new SavedScene { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray();
                run.originalPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene);
                run.originalOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled; run.originalOptions = (int)EditorSettings.enterPlayModeOptions;
                run.originalPaused = EditorApplication.isPaused; run.originalTimeScale = Time.timeScale;
                run.originalRunInBackground = Application.runInBackground; run.originalTargetFrameRate = Application.targetFrameRate; run.originalVSyncCount = QualitySettings.vSyncCount;
                run.capturedSettings = true; SessionState.EraseString(LogKey); live = run; Save(true);
                EditorSettings.enterPlayModeOptionsEnabled = false; EditorSceneManager.playModeStartScene = null; EditorApplication.isPaused = false;
            }
            catch (Exception error) { run.errors.Add(error.ToString()); run.aborted = true; live = run; Finish(); }
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + .04;
            live = live ?? Load(); if (live == null) return;
            if (live.phase == "restoring" && !EditorApplication.isPlayingOrWillChangePlaymode) { Finish(); return; }
            if (live.phase == "exiting") { if (!EditorApplication.isPlayingOrWillChangePlaymode) Finish(); return; }
            try
            {
                Require(DateTime.UtcNow.Ticks < live.deadlineTicks, "Crowd stress exceeded its 600-second wall-clock limit in " + live.phase);
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                if (live.phase == "opening")
                {
                    CleanScenes(); var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                    var savedWorld = Components<StationSocialWorld>(scene).Single();
                    Require(savedWorld.passengers.Length == 300 && savedWorld.passengers.All(p => p), "Author 300 registered saved passengers before stress verification.");
                    Require(Components<StationActorMotion>(scene).Length == 307, "Expected 300 passengers, four staff and three cleaners.");
                    Require(savedWorld.extraApproachingCustomersPerService == 4, "Saved automatic approach budget must be four per service.");
                    Require(savedWorld.passengers.All(p => p.automaticRequests && Mathf.Abs(p.requestInterval.x - 12f) < .01f && Mathf.Abs(p.requestInterval.y - 35f) < .01f && !p.luggage), "Saved passengers must have automatic 12–35 second requests and no luggage.");
                    live.baselineObjects = Components<Transform>(scene).Length; live.baselineCameras = Components<Camera>(scene).Length;
                    live.baselineActiveCameras = Components<Camera>(scene).Count(c => c.isActiveAndEnabled); live.baselineLights = Components<Light>(scene).Count(c => c.isActiveAndEnabled);
                    live.phase = "entering"; Save(true); EditorApplication.isPlaying = true;
                }
                else if (live.phase == "initializing" && Application.isPlaying && Time.frameCount >= live.readyFrame) Initialize();
                else if (Application.isPlaying && (live.phase == "automatic activity" || live.phase == "accueil congestion"))
                {
                    Require(world && rigs != null, "Stress runtime references were lost during Play; a mid-test script reload cannot be resumed safely.");
                    live.simulatedSeconds = Time.time - live.startedAt;
                    ObserveActors(); ObserveQueues(); ObserveManual();
                    if (Time.time >= nextPairs) { nextPairs = Time.time + .25f; ObservePairs(); }
                    if (!live.hallCaptured && live.simulatedSeconds > 25f)
                    { StationSocialCapture.CaptureLiveHall(); live.hallCaptured = true; }
                    if (live.phase == "automatic activity" && live.simulatedSeconds >= AutomaticDuration)
                    {
                        live.automaticRequestsAt120 = live.automaticRequests; live.automaticCompletionsAt120 = live.automaticCompletions;
                        live.automaticPhasePassed = live.automaticRequestsAt120 >= 30 && live.automaticCompletionsAt120 >= 15 && services.Values.All(s => s.result.automaticRequests > 0 && s.result.automaticCompletions > 0);
                        live.phase = "accueil congestion"; Save(true);
                    }
                    if (live.phase == "accueil congestion") ExerciseCongestion();
                    if (live.firstAnomalyAt >= 0 && Time.time - live.firstAnomalyAt > 3f) { Exit(false); return; }
                    if (live.simulatedSeconds >= TotalDuration) { FinalAssertions(); Exit(true); return; }
                    if (!string.IsNullOrEmpty(SessionState.GetString(LogKey, ""))) throw new InvalidOperationException("Unity logged an error during crowd stress; see the collected log.");
                    Save(false);
                }
            }
            catch (Exception error) { live.errors.Add(error.ToString()); Exit(false); }
        }

        static void Initialize()
        {
            var scene = SceneManager.GetActiveScene(); world = Components<StationSocialWorld>(scene).Single();
            services.Clear(); manual.Clear(); currentTripManual.Clear(); encounters.Clear(); overlaps.Clear(); persistentPairs.Clear(); anomalousActors.Clear();
            live.startedAt = Time.time; nextPairs = nextGround = nextDispatch = Time.time;
            foreach (var desk in new[] { world.informationDesk }.Concat(world.shopDesks))
            {
                Require(desk && desk.CanAcceptRequests, "A saved service cannot accept requests.");
                var watch = new ServiceWatch { desk = desk, result = new ServiceResult { service = desk.name } }; services.Add(desk, watch); live.services.Add(watch.result);
                desk.queueJoined.AddListener(p => Admitted(watch, p));
                desk.conversationStarted.AddListener(p => Started(watch, p));
                desk.conversationEnded.AddListener(p => Completed(watch, p));
            }
            Require(services.Count == 4, "Expected accueil and three shop services.");
            foreach (var passenger in world.passengers)
            {
                var captured = passenger; passenger.requestStarted.AddListener(() => Requested(captured));
                passenger.returnedToSeat.AddListener(() => Returned(captured)); passenger.returnedToActivity.AddListener(() => Returned(captured));
            }
            rigs = Components<StationActorMotion>(scene).Select(m =>
            {
                var animator = m.animationPlayer ? m.animationPlayer.TargetAnimator : null;
                Require(animator && animator.isHuman && animator.avatar && animator.avatar.isValid, "Actor lacks a valid Humanoid Animator: " + m.name);
                var result = new ActorResult { actor = m.name }; live.actors.Add(result);
                var rig = new Rig { motion = m, animator = animator, passenger = m.GetComponent<StationPassenger>(), result = result, previous = m.transform.position,
                    hips = animator.GetBoneTransform(HumanBodyBones.Hips), head = animator.GetBoneTransform(HumanBodyBones.Head),
                    leftKnee = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg), rightKnee = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg),
                    leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot), rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot) };
                Require(rig.hips && rig.head && rig.leftKnee && rig.rightKnee && rig.leftFoot && rig.rightFoot, "Actor has missing leg joints: " + m.name); return rig;
            }).ToArray();
            Require(rigs.Length == 307, "Runtime actor count changed before stress observation.");
            Application.runInBackground = true; Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0; Time.timeScale = 2f;
            live.phase = "automatic activity"; Save(true);
        }

        static void Requested(StationPassenger passenger)
        {
            if (live == null || !passenger.CurrentService || !services.TryGetValue(passenger.CurrentService, out var watch)) return;
            currentTripManual[passenger] = dispatchingManual;
            if (dispatchingManual) watch.result.manualRequests++;
            else { live.automaticRequests++; watch.result.automaticRequests++; }
        }
        static void Admitted(ServiceWatch watch, StationPassenger passenger)
        {
            if (live == null) return;
            watch.fifo.RemoveAll(p => !p || !watch.desk.Contains(p)); watch.fifo.Add(passenger);
            watch.result.admissions++; watch.result.admissionOrder.Add(passenger.name);
            int index = watch.desk.GetQueueIndex(passenger);
            if (index < 0 || index >= watch.desk.queueSlots.Length || !watch.desk.TryGetQueueSlot(passenger, out var physicalSlot) ||
                FlatDistance(passenger.transform.position, physicalSlot.position) > watch.desk.arrivalTolerance + .025f)
                RecordError("Queue admission occurred away from its physical tail: " + passenger.name);
            else watch.result.physicalAdmissionChecks++;
            if (manual.TryGetValue(passenger, out var visit) && visit.admittedAt < 0) { visit.admitted = true; visit.admittedAt = Time.time - live.startedAt; }
        }
        static void Started(ServiceWatch watch, StationPassenger passenger)
        {
            if (live == null) return;
            watch.fifo.RemoveAll(p => !p || !watch.desk.Contains(p));
            if (watch.fifo.Count == 0 || watch.fifo[0] != passenger || watch.desk.GetQueueIndex(passenger) != 0)
                RecordError("Service overtook physical arrival FIFO: " + passenger.name);
            else watch.result.fifoChecks++;
            watch.result.conversations++; watch.result.serviceOrder.Add(passenger.name);
            watch.result.lastConversationStartedAt = Time.time - live.startedAt;
        }
        static void Completed(ServiceWatch watch, StationPassenger passenger)
        {
            if (live == null) return;
            bool isManual = currentTripManual.TryGetValue(passenger, out var requestedManually) && requestedManually;
            if (isManual)
            {
                watch.result.manualCompletions++;
                if (manual.TryGetValue(passenger, out var visit)) { visit.completed = true; visit.completedAt = Time.time - live.startedAt; }
            }
            else { live.automaticCompletions++; watch.result.automaticCompletions++; }
            watch.lastDeparted = passenger;
            watch.fifo.Remove(passenger); currentTripManual.Remove(passenger);
        }
        static void Returned(StationPassenger passenger)
        {
            if (live != null && manual.TryGetValue(passenger, out var visit)) { visit.returned = true; visit.returnedAt = Time.time - live.startedAt; }
        }
        static void ObserveQueues()
        {
            foreach (var watch in services.Values)
            {
                watch.fifo.RemoveAll(p => !p || !watch.desk.Contains(p));
                watch.result.queuePeak = Mathf.Max(watch.result.queuePeak, watch.desk.QueueCount);
                if (watch.fifo.Count != watch.desk.QueueCount) RecordError("Observed FIFO membership differs from the physical queue: " + watch.desk.name);
                for (int i = 0; i < watch.fifo.Count; i++) if (watch.desk.GetQueueIndex(watch.fifo[i]) != i) RecordError("Queue indexes did not compact after service/cancellation: " + watch.desk.name);
                bool pendingWithoutService = watch.desk.QueueCount > 0 && !watch.desk.ActiveCustomer;
                if (!pendingWithoutService) watch.idleQueueSince = -1f;
                else if (watch.idleQueueSince < 0f) watch.idleQueueSince = Time.time;
                if (Time.time >= watch.nextDiagnosticAt)
                {
                    watch.nextDiagnosticAt = Time.time + 1f;
                    var snapshot = CaptureService(watch);
                    watch.result.latestSnapshot = snapshot;
                    if (snapshot.idleQueueDuration > watch.result.maximumNoServiceDuration)
                    {
                        watch.result.maximumNoServiceDuration = snapshot.idleQueueDuration;
                        watch.result.longestIdleQueueSnapshot = snapshot;
                    }
                }
            }
        }

        static ServiceSnapshot CaptureService(ServiceWatch watch)
        {
            var desk = watch.desk;
            var snapshot = new ServiceSnapshot
            {
                sampledAt = Time.time - live.startedAt,
                idleQueueDuration = watch.idleQueueSince < 0f ? 0f : Time.time - watch.idleQueueSince,
                queueCount = desk.QueueCount, activeCustomer = desk.ActiveCustomer ? desk.ActiveCustomer.name : "",
                canAccept = desk.CanAcceptRequests, hasPhysicalTailCapacity = desk.HasQueueCapacity,
                lastDeparted = watch.lastDeparted ? CaptureQueueActor(desk, watch.lastDeparted) : null
            };
            foreach (var p in desk.QueuePassengers) if (p) snapshot.queued.Add(CaptureQueueActor(desk, p));
            foreach (var p in world.passengers)
                if (p && p.CurrentService == desk && p.CurrentState == StationPassengerState.ApproachingQueue)
                    snapshot.approaching.Add(CaptureQueueActor(desk, p));
            return snapshot;
        }

        static QueueActorSnapshot CaptureQueueActor(StationInformationDesk desk, StationPassenger p)
        {
            var motion = p.motion; var agent = motion ? motion.agent : null;
            var state = new QueueActorSnapshot
            {
                actor = p.name, state = p.CurrentState.ToString(), currentService = p.CurrentService ? p.CurrentService.name : "",
                queueIndex = desk.GetQueueIndex(p), rootPosition = p.transform.position,
                navigationIssue = motion ? motion.NavigationIssue : "Missing motion", lastIssue = p.LastIssue,
                motion = motion ? motion.CurrentMotion : "", isAvoidingQueue = motion && motion.IsAvoidingQueue,
                agentEnabled = agent && agent.enabled, onNavMesh = agent && agent.enabled && agent.isOnNavMesh
            };
            float advancedAt = desk.GetLastQueueAdvanceTime(p);
            state.lastAdvanceAt = advancedAt < 0f ? -1f : advancedAt - live.startedAt;
            if (desk.TryGetQueueSlot(p, out var slot))
            {
                state.assignedSlot = slot.name; state.assignedSlotIndex = Array.IndexOf(desk.queueSlots, slot);
                state.assignedSlotPosition = slot.position; state.distanceToSlot = FlatDistance(p.transform.position, slot.position);
            }
            if (state.queueIndex > 0)
                state.distanceToPredecessor = FlatDistance(p.transform.position, desk.QueuePassengers[state.queueIndex - 1].transform.position);
            if (state.onNavMesh)
            {
                state.hasPath = agent.hasPath; state.pathPending = agent.pathPending; state.isStopped = agent.isStopped;
                state.stoppingDistance = agent.stoppingDistance; state.velocity = agent.velocity; state.desiredVelocity = agent.desiredVelocity;
                state.pathStatus = agent.pathStatus.ToString(); state.destination = agent.destination;
                if (!float.IsInfinity(agent.remainingDistance) && !float.IsNaN(agent.remainingDistance)) state.remainingDistance = agent.remainingDistance;
            }
            return state;
        }

        static void ExerciseCongestion()
        {
            if (Time.time >= nextDispatch && live.simulatedSeconds < 180f && manual.Count < live.manualTarget)
            {
                nextDispatch = Time.time + 1f;
                var available = world.passengers.Where(p => p && p.IsAvailableForActivity && !manual.ContainsKey(p) &&
                    (p.activity == StationPassengerActivity.SoloWalking || p.activity == StationPassengerActivity.WalkingGroup))
                    .OrderBy(p => FlatDistance(p.transform.position, world.informationDesk.queueSlots[0].position)).ToArray();
                // Three solo routes need enough real travel left to survive twelve seconds of stop/start pressure.
                var challengeCandidates = manual.Count == 0 ? available.Where(p => p.activity == StationPassengerActivity.SoloWalking &&
                    FlatDistance(p.transform.position, world.informationDesk.queueSlots[0].position) > 14f).Take(3) : Enumerable.Empty<StationPassenger>();
                foreach (var passenger in challengeCandidates.Concat(available).Distinct().Take(live.manualTarget - manual.Count))
                {
                    var visit = new ManualVisit { actor = passenger.name, requestedAt = live.simulatedSeconds };
                    manual.Add(passenger, visit); dispatchingManual = true;
                    bool accepted;
                    try { accepted = passenger.TryVisitService(world.informationDesk); }
                    finally { dispatchingManual = false; }
                    if (!accepted) { manual.Remove(passenger); continue; }
                    passenger.automaticRequests = false; live.manual.Add(visit);
                }
            }
            if (live.simulatedSeconds > 140f && live.manual.Count(v => v.cancelled) < 2)
            {
                var cancel = manual.FirstOrDefault(pair => !pair.Value.cancelled && !pair.Value.completed &&
                    (!pair.Value.stopped || pair.Value.challengeFinished) && world.informationDesk.GetQueueIndex(pair.Key) > 0);
                if (cancel.Key)
                {
                    cancel.Value.cancelled = true; cancel.Value.cancelledAt = live.simulatedSeconds;
                    world.informationDesk.Cancel(cancel.Key);
                }
            }
        }
        static void ObserveManual()
        {
            foreach (var pair in manual)
            {
                var passenger = pair.Key; var visit = pair.Value; var motion = passenger.motion; var agent = motion.agent;
                visit.finalState = passenger.CurrentState.ToString();
                if ((visit.cancelled || visit.completed) && passenger.IsAvailableForActivity && !passenger.CurrentService && !visit.returned) Returned(passenger);
                if (!visit.stopped && live.manual.Count(v => v.stopped) < 3 && passenger.activity == StationPassengerActivity.SoloWalking &&
                    motion.IsWalkPose && motion.ActualPlanarSpeed > .4f && agent && agent.enabled && agent.isOnNavMesh && !agent.pathPending && agent.remainingDistance > 12f)
                {
                    visit.stopped = true; visit.pauseStartedAt = Time.time; visit.pausePosition = passenger.transform.position;
                }
                if (visit.stopped && !visit.resumed && agent && agent.enabled && agent.isOnNavMesh)
                {
                    float elapsed = Time.time - visit.pauseStartedAt;
                    if (elapsed < 12f && passenger.IsWalking && agent.hasPath)
                    {
                        bool stop = elapsed % 1.3f < .5f;
                        if (stop && !visit.forcedStopped) { visit.forcedStopCycles++; visit.currentStopPosition = passenger.transform.position; }
                        if (!stop && visit.forcedStopped) visit.forcedResumeCycles++;
                        visit.forcedStopped = stop; agent.isStopped = stop;
                        if (stop) visit.pauseRootTravel = Mathf.Max(visit.pauseRootTravel, FlatDistance(passenger.transform.position, visit.currentStopPosition));
                    }
                    else
                    {
                        if (visit.forcedStopped) visit.forcedResumeCycles++;
                        visit.forcedStopped = false; visit.challengeFinished = elapsed >= 12f;
                        if (passenger.IsWalking && agent.hasPath) agent.isStopped = false;
                        visit.resumedDistance = FlatDistance(passenger.transform.position, visit.pausePosition);
                        if (visit.challengeFinished && motion.IsWalkPose && motion.ActualPlanarSpeed > .2f && visit.resumedDistance > .2f) visit.resumed = true;
                    }
                }
            }
        }

        static void ObserveActors()
        {
            live.sampledFrames++; bool groundSample = Time.time >= nextGround; if (groundSample) nextGround = Time.time + .5f;
            foreach (var rig in rigs)
            {
                var motion = rig.motion; var animator = rig.animator; var result = rig.result; var agent = motion.agent;
                var state = animator.GetCurrentAnimatorStateInfo(0); var next = animator.GetNextAnimatorStateInfo(0);
                bool attached = agent && agent.enabled && agent.isOnNavMesh;
                var pose = new PoseRecord { actor = motion.name, passengerState = rig.passenger ? rig.passenger.CurrentState.ToString() : motion.GetComponent<StationCleaner>() ? "Cleaner" : "Staff",
                    motion = motion.CurrentMotion, time = live.simulatedSeconds, speed = motion.ActualPlanarSpeed, animatorSpeed = animator.speed,
                    currentStateHash = state.fullPathHash, nextStateHash = next.fullPathHash, currentPhase = state.normalizedTime, nextPhase = next.normalizedTime,
                    transition = animator.IsInTransition(0), transitionPhase = animator.IsInTransition(0) ? animator.GetAnimatorTransitionInfo(0).normalizedTime : 0,
                    stabilizeFeet = animator.stabilizeFeet, agentEnabled = agent && agent.enabled, onNavMesh = attached,
                    root = motion.transform.position, rootUp = motion.transform.up, navPosition = attached ? agent.nextPosition : motion.transform.position,
                    hips = rig.hips.position, head = rig.head.position, leftKnee = rig.leftKnee.position, rightKnee = rig.rightKnee.position, leftFoot = rig.leftFoot.position, rightFoot = rig.rightFoot.position };
                result.samples++; result.travelled += FlatDistance(pose.root, rig.previous); rig.previous = pose.root;
                bool moving = attached && pose.speed > .1f;
                if (moving) result.movingSamples++; if (moving && !rig.moving) result.starts++; if (!moving && rig.moving) result.stops++; rig.moving = moving;
                if (pose.transition) result.transitions++;
                result.maximumPlaybackSpeed = Mathf.Max(result.maximumPlaybackSpeed, pose.animatorSpeed);
                float up = Vector3.Dot(pose.rootUp, Vector3.up); if (attached) result.minimumRootUp = Mathf.Min(result.minimumRootUp, up);
                float footAbove = Mathf.Max(pose.leftFoot.y, pose.rightFoot.y) - pose.hips.y;
                float kneeAbove = Mathf.Max(pose.leftKnee.y, pose.rightKnee.y) - pose.hips.y;
                float kneeAngle = Mathf.Min(Vector3.Angle(pose.hips - pose.leftKnee, pose.leftFoot - pose.leftKnee), Vector3.Angle(pose.hips - pose.rightKnee, pose.rightFoot - pose.rightKnee));
                bool uprightMotion = motion.CurrentMotion == motion.walkId || motion.CurrentMotion == motion.idleId || motion.CurrentMotion == motion.talkId;
                bool seatTransition = rig.passenger && (rig.passenger.CurrentState == StationPassengerState.Seated || rig.passenger.CurrentState == StationPassengerState.SittingDown || rig.passenger.CurrentState == StationPassengerState.StandingUp);
                bool standing = attached && uprightMotion && !seatTransition;
                if (standing)
                { result.maximumFootAboveHip = Mathf.Max(result.maximumFootAboveHip, footAbove); result.maximumKneeAboveHip = Mathf.Max(result.maximumKneeAboveHip, kneeAbove); result.minimumKneeAngle = Mathf.Min(result.minimumKneeAngle, kneeAngle); }
                string reason = null;
                if (!Finite(pose.root) || !Finite(pose.rootUp) || !Finite(pose.hips) || !Finite(pose.head) || !Finite(pose.leftKnee) || !Finite(pose.rightKnee) || !Finite(pose.leftFoot) || !Finite(pose.rightFoot)) reason = "Non-finite actor or leg joint.";
                else if (attached && up < .98f) reason = "Navigating actor tilted away from world up.";
                else if (standing && (Mathf.Max(pose.leftFoot.y, pose.rightFoot.y) > pose.head.y - .05f || footAbove > .3f * animator.humanScale)) reason = "Catastrophic foot above head or standing hips.";
                else if (standing && kneeAbove > .25f * animator.humanScale && kneeAngle < 10f) reason = "Catastrophic folded leg: elevated knee and near-zero knee angle.";
                if (attached && motion.IsWalkPose && groundSample)
                {
                    var floor = Physics.RaycastAll(pose.root + Vector3.up * .25f, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore)
                        .Where(h => StationNavigationSafety.IsStationFloor(h.collider)).OrderBy(h => h.distance).ToArray();
                    if (floor.Length > 0)
                    {
                        pose.rootFloorHeight = pose.root.y - floor[0].point.y; result.maximumRootFloorHeight = Mathf.Max(result.maximumRootFloorHeight, pose.rootFloorHeight);
                        if (pose.rootFloorHeight > .15f || pose.rootFloorHeight < -.12f) reason = "Walking root left the authored floor height (possible phantom NavMesh climb).";
                    }
                    else reason = "No authored station floor beneath a navigating walker.";
                }
                if (reason != null) Anomaly(rig, pose, reason);
                rig.previousPose = pose;
            }
        }
        static void Anomaly(Rig rig, PoseRecord pose, string reason)
        {
            if (!anomalousActors.Add(rig.motion.name)) return;
            pose.reason = reason;
            if (live.firstAnomalyAt < 0) live.firstAnomalyAt = Time.time;
            if (live.anomalies.Count < 30)
            {
                if (rig.previousPose != null) { rig.previousPose.reason = "Previous observation before anomaly"; live.anomalies.Add(rig.previousPose); }
                live.anomalies.Add(pose);
            }
            RecordError(reason + " Actor: " + pose.actor + ", motion: " + pose.motion + ", phase: " + pose.currentPhase);
            if (!live.anomalyCaptured)
            {
                live.anomalyCaptured = true;
                try { StationSocialCapture.CaptureLiveHall(); File.Copy("Reports/TrainStationSocial/mixed-hall-live.png", "Reports/TrainStationSocial/CollisionGait/first-anomaly-hall.png", true); }
                catch (Exception error) { RecordError("Anomaly capture failed: " + error.Message); }
            }
            Save(true);
        }

        static void ObservePairs()
        {
            var present = new HashSet<int>();
            for (int i = 0; i < rigs.Length; i++) for (int j = i + 1; j < rigs.Length; j++)
            {
                var a = rigs[i].motion; var b = rigs[j].motion;
                if (!a.agent || !b.agent || !a.agent.enabled || !b.agent.enabled || !a.agent.isOnNavMesh || !b.agent.isOnNavMesh) continue;
                float distance = FlatDistance(a.transform.position, b.transform.position);
                if (distance >= .85f) continue;
                int key = i * rigs.Length + j; encounters.Add(key); live.closePairSamples++; live.minimumPairDistance = Mathf.Min(live.minimumPairDistance, distance);
                if (distance >= .25f) continue;
                present.Add(key); if (!overlaps.ContainsKey(key)) overlaps[key] = Time.time;
                float duration = Time.time - overlaps[key]; live.maximumOverlapDuration = Mathf.Max(live.maximumOverlapDuration, duration);
                if (duration > 2f && persistentPairs.Add(key) && live.overlapExamples.Count < 12) live.overlapExamples.Add(a.name + " / " + b.name + " at " + live.simulatedSeconds.ToString("F2") + " s");
            }
            foreach (int key in overlaps.Keys.Where(k => !present.Contains(k)).ToArray()) overlaps.Remove(key);
            live.distinctClosePairs = encounters.Count; live.persistentOverlapPairs = persistentPairs.Count;
        }

        static void FinalAssertions()
        {
            Require(live.automaticPhasePassed, "First 120 s did not deliver at least 30 automatic requests, 15 completions and activity at all four services.");
            Require(live.services.All(s => s.maximumNoServiceDuration < 45f),
                "A nonempty queue left its receptionist idle for 45 seconds or more: " +
                string.Join("; ", live.services.Where(s => s.maximumNoServiceDuration >= 45f)
                    .Select(s => s.service + " " + s.maximumNoServiceDuration.ToString("F2") + " s. See longestIdleQueueSnapshot.")));
            Require(live.manual.Count >= 12 && live.manual.Count(v => v.admitted) >= 6 && live.manual.Count(v => v.completed) >= 3, "Manual congestion did not exercise enough real arrivals and conversations.");
            Require(live.manual.Count(v => v.cancelled && v.returned) >= 2, "Two cancelled queue customers did not return to their usual activity.");
            Require(live.manual.Count(v => v.stopped && v.resumed && v.challengeFinished && v.forcedStopCycles >= 6 && v.forcedResumeCycles >= 6) >= 3,
                "Three solo travellers did not complete repeated 0.5-second stops / 0.8-second resumes over twelve seconds.");
            foreach (var visit in live.manual.Where(v => v.stopped))
            {
                var actor = live.actors.First(a => a.actor == visit.actor);
                Require(actor.starts >= 5 && actor.stops >= 5 && actor.transitions >= 5, "Repeated stop/start did not exercise actual gait transitions: " + visit.actor);
            }
            Require(live.distinctClosePairs > 0 && live.actors.Sum(a => a.stops) > 0 && live.actors.Sum(a => a.travelled) > 100f, "The crowd did not create close encounters and stop/start movement.");
            Require(live.services.All(s => s.physicalAdmissionChecks == s.admissions && s.fifoChecks == s.conversations), "Physical admission or FIFO checks were incomplete.");
            Require(live.actors.Count == 307 && live.actors.All(a => a.samples > 100), "Not every authored actor received sustained pose observation.");
            Require(live.anomalies.Count == 0 && live.errors.Count == 0, "Crowd pose or service anomalies were observed.");
            var scene = SceneManager.GetActiveScene(); live.finalObjects = Components<Transform>(scene).Length; live.finalCameras = Components<Camera>(scene).Length;
            Require(live.finalObjects == live.baselineObjects && live.finalCameras == live.baselineCameras && Components<StationActorMotion>(scene).Length == 307, "Runtime created/removed authored objects, cameras or actors.");
            Require(Components<Camera>(scene).Count(c => c.isActiveAndEnabled) == live.baselineActiveCameras && Components<Light>(scene).Count(c => c.isActiveAndEnabled) == live.baselineLights, "Runtime changed camera/light activation.");
            Require(world.passengers.All(p => !p.luggage), "Removed passenger luggage reappeared.");
            live.congestionPassed = true;
        }
        static void RecordError(string error) { if (live != null && live.errors.Count < 50 && !live.errors.Contains(error)) live.errors.Add(error); }
        static void Exit(bool completed)
        {
            foreach (var pair in manual)
            {
                var passenger = pair.Key; var agent = passenger && passenger.motion ? passenger.motion.agent : null;
                if (pair.Value.forcedStopped && agent && agent.enabled && agent.isOnNavMesh && passenger.IsWalking && agent.hasPath) agent.isStopped = false;
                pair.Value.forcedStopped = false;
            }
            if (!completed) live.aborted = true;
            live.phase = "exiting"; Save(true);
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Time.timeScale = live.originalTimeScale; EditorApplication.isPlaying = false; }
            else Finish();
        }
        static void ModeChanged(PlayModeStateChange state)
        {
            live = live ?? Load(); if (live == null) return;
            if (state == PlayModeStateChange.EnteredPlayMode) { live.phase = "initializing"; live.readyFrame = Time.frameCount + 20; }
            else if (state == PlayModeStateChange.ExitingPlayMode && live.phase != "exiting") { live.aborted = true; RecordError("Play stopped before stress verification completed."); live.phase = "exiting"; }
            else if (state == PlayModeStateChange.EnteredEditMode) live.phase = "restoring";
            Save(true);
        }
        static void Finish()
        {
            try
            {
                if (live.capturedSettings)
                {
                    Time.timeScale = live.originalTimeScale; Application.runInBackground = live.originalRunInBackground; Application.targetFrameRate = live.originalTargetFrameRate; QualitySettings.vSyncCount = live.originalVSyncCount;
                    EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(live.originalPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(live.originalPlayScene);
                    EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)live.originalOptions; EditorSettings.enterPlayModeOptionsEnabled = live.originalOptionsEnabled; EditorApplication.isPaused = live.originalPaused; live.restoredSettings = true;
                    CleanScenes(); EditorSceneManager.RestoreSceneManagerSetup(live.scenes.Select(s => new SceneSetup { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray()); live.restoredScenes = true;
                }
            }
            catch (Exception error) { RecordError("Stress restoration failed: " + error); }
            string log = SessionState.GetString(LogKey, ""); if (!string.IsNullOrEmpty(log)) RecordError("Unity errors:\n" + log);
            live.success = !live.aborted && live.errors.Count == 0 && live.automaticPhasePassed && live.congestionPassed && live.restoredSettings && live.restoredScenes;
            live.phase = "complete"; live.completedUtc = DateTime.UtcNow.ToString("O"); Write();
            bool passed = live.success; SessionState.EraseString(StateKey); SessionState.EraseString(LogKey); live = null; world = null; rigs = null;
            services.Clear(); manual.Clear(); currentTripManual.Clear();
            Debug.Log("Station crowd stress " + (passed ? "PASSED" : "FAILED") + ". See " + ReportPath);
        }
        static void Log(string message, string trace, LogType type)
        {
            if ((live == null && Load() == null) || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            string previous = SessionState.GetString(LogKey, ""); if (previous.Length < 16000) SessionState.SetString(LogKey, previous + message + "\n" + trace + "\n");
        }
        static Run Load() { string json = SessionState.GetString(StateKey, ""); return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Run>(json); }
        static void Save(bool force)
        {
            if (!force && EditorApplication.timeSinceStartup < nextWrite) return;
            nextWrite = EditorApplication.timeSinceStartup + 2; SessionState.SetString(StateKey, JsonUtility.ToJson(live)); Write();
        }
        static void Write() { Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath, JsonUtility.ToJson(live, true)); }
        static void CleanScenes() { for (int i = 0; i < SceneManager.sceneCount; i++) Require(!SceneManager.GetSceneAt(i).isDirty, "Save/revert unsaved scene edits before checking; the stress test never discards user edits."); }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        static float FlatDistance(Vector3 a, Vector3 b) { a.y = b.y = 0; return Vector3.Distance(a, b); }
        static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    }
}
#endif
