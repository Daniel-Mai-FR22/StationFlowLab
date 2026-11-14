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
    /// <summary>Actual saved actors exercise social queue order and native-agent passage in Play mode.</summary>
    [InitializeOnLoad]
    public static class StationQueueVerification
    {
        const string ScenePath = "Assets/TrainStation/Scenes/TrainStation.unity";
        const string StateKey = "StationFlow.StationQueue.Verification";
        const string LogKey = StateKey + ".Errors";
        const string ReportPath = "Reports/TrainStationSocial/QueueBehavior/queue-play-check.json";
        [Serializable] sealed class SavedScene { public string path; public bool isLoaded, isActive; }
        [Serializable] sealed class Advance
        {
            public string passenger, predecessor;
            public Vector3 before, predecessorBefore;
            public float predecessorMovedAt = -1f, startedAt = -1f, targetChangedAt = -1f, finishedAt = -1f;
            public float reactionObserved, totalAdvance;
            public int startFrame = -1;
        }
        [Serializable] sealed class Wave
        {
            public string cause, departed;
            public float triggeredAt, completedAt;
            public bool passed;
            public List<Advance> advances = new List<Advance>();
        }
        [Serializable] sealed class Run
        {
            public string startedUtc, completedUtc, unityVersion, phase, originalPlayScene, fixture;
            public bool success, abort, capturedSettings, originalOptionsEnabled, originalPaused, originalRunInBackground;
            public bool restoredSettings, restoredScenes, compactSpacingPassed, headWavePassed, middleWavePassed, newcomerFifoPassed, nativePassagePassed, rearDeparturePassed;
            public bool passerClearanceAboveLegacyLimit, rearClearanceAboveLegacyLimit, severePersistentOverlapObserved;
            public int originalOptions, originalTargetFrameRate, originalVSyncCount, readyFrame, baselineObjects, finalObjects, sampledFrames, setupPlacements, baselinePassengers;
            public int nextFixtureActor, physicalAdmissions, fifoChecks, passerDetours, occupiedCenterlineCrossings;
            public int rearDepartureDetours, rearDepartureCenterlineCrossings, temporaryHomeAnchorChanges;
            public float originalTimeScale, startedAt, phaseAt, simulatedSeconds, minimumSettledSpacing = 1000f, maximumSettledSpacing;
            public float minimumMovingSpacing = 1000f, minimumPasserDistance = 1000f, passerTravel;
            public float rearDepartureStartedAt, rearDepartureSideStepAt = -1f, rearDepartureArrivalAt = -1f, rearDepartureTravel, minimumRearDepartureDistance = 1000f;
            public float maximumPasserRadiusPenetration, maximumRearRadiusPenetration, maximumPasserSevereOverlapSeconds, maximumRearSevereOverlapSeconds;
            public long deadlineTicks;
            public SavedScene[] scenes;
            public List<string> admissionOrder = new List<string>(), errors = new List<string>();
            public List<string> warnings = new List<string>();
            public List<Wave> waves = new List<Wave>();
        }
        static Run live;
        static StationSocialWorld world;
        static StationInformationDesk desk;
        static StationPassenger[] fixture;
        static StationPassenger passer;
        static StationPassenger rearDeparture;
        static readonly List<StationPassenger> expected = new List<StationPassenger>();
        static readonly List<StationPassenger> observed = new List<StationPassenger>();
        static readonly Dictionary<StationPassenger, Advance> advances = new Dictionary<StationPassenger, Advance>();
        static readonly Dictionary<StationPassenger, StationPassenger> predecessors = new Dictionary<StationPassenger, StationPassenger>();
        static readonly Dictionary<StationPassenger, Vector3> fixedBefore = new Dictionary<StationPassenger, Vector3>();
        static readonly Dictionary<StationPassenger, float> severeOverlapSince = new Dictionary<StationPassenger, float>();
        static Vector3 queueDirection, crossingStart, crossingGoal, crossingPrevious;
        static Vector3 rearStart, rearGoal, rearPrevious;
        static int previousFrame = -1, initialDetours;
        static int initialRearDetours;
        static double nextWrite;
        static Wave wave;

        static StationQueueVerification()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            Application.logMessageReceived += Log;
        }

        [MenuItem("Efrei Research Lab/Train Station Social/Verify compact reactive queues in Play")]
        public static void BeginPlayCheck()
        {
            Require(Load() == null, "A queue verification is already active.");
            var run = new Run { startedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion, phase = "opening", deadlineTicks = DateTime.UtcNow.AddSeconds(360).Ticks,
                fixture = "Seven existing saved standing passengers and one saved solo traveller. Initial placement uses NavMesh Warp only to establish a controlled fixture; subsequent motion uses native NavMeshAgent pathfinding and avoidance. A second completed customer returns to a temporarily moved existing home anchor behind the queue using the ordinary passenger FSM. FIFO, physical admission, compact spacing, cascading reaction and completion timing gates are unchanged. Native passing and rear egress must complete; axis crossings, path length, side-step timing and body overlap are reported rather than requiring a custom planner or a guaranteed detour. PASS means social flow completion, not collision-free passage. All fixture changes are Play-only. Other saved passengers remain active with automatic service requests disabled during this isolated test." };
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating, "Finish Play/import before queue verification.");
                CleanScenes(); var setup = EditorSceneManager.GetSceneManagerSetup();
                Require(setup.Length > 0 && setup.All(s => !string.IsNullOrEmpty(s.path)), "Save all open scenes before checking.");
                run.scenes = setup.Select(s => new SavedScene { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray();
                run.originalPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene);
                run.originalOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled; run.originalOptions = (int)EditorSettings.enterPlayModeOptions;
                run.originalPaused = EditorApplication.isPaused; run.originalTimeScale = Time.timeScale;
                run.originalRunInBackground = Application.runInBackground; run.originalTargetFrameRate = Application.targetFrameRate; run.originalVSyncCount = QualitySettings.vSyncCount;
                run.capturedSettings = true; SessionState.EraseString(LogKey); Save(run);
                EditorSettings.enterPlayModeOptionsEnabled = false; EditorSceneManager.playModeStartScene = null; EditorApplication.isPaused = false;
            }
            catch (Exception error) { run.errors.Add(error.ToString()); run.abort = true; Finish(run); }
        }

        static void Tick()
        {
            var run = live ?? Load(); if (run == null) return;
            if (run.phase == "restoring" && !EditorApplication.isPlayingOrWillChangePlaymode) { Finish(run); return; }
            if (run.abort && run.phase == "exiting") { if (!EditorApplication.isPlayingOrWillChangePlaymode) Finish(run); return; }
            try
            {
                Require(DateTime.UtcNow.Ticks < run.deadlineTicks, "Queue verification timed out in " + run.phase + ". " + Describe());
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                Require(string.IsNullOrEmpty(SessionState.GetString(LogKey, "")), "Unity logged an error during queue verification.");
                if (run.phase == "opening" && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    CleanScenes(); var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                    run.baselineObjects = Components<Transform>(scene).Length;
                    run.phase = "entering"; Save(run); EditorApplication.isPlaying = true;
                }
                else if (run.phase == "initializing" && EditorApplication.isPlaying && Time.frameCount >= run.readyFrame)
                { live = run; Initialize(run); Save(run); }
                else if (EditorApplication.isPlaying && run.phase != "entering" && run.phase != "initializing" && run.phase != "exiting" && previousFrame != Time.frameCount)
                {
                    previousFrame = Time.frameCount; run.sampledFrames++; run.simulatedSeconds = Time.time - run.startedAt;
                    Observe(run); AdvanceScenario(run); Save(run);
                }
            }
            catch (Exception error)
            {
                run.errors.Add(error.ToString()); run.abort = true; run.phase = "exiting"; Save(run); Write(run);
                if (EditorApplication.isPlayingOrWillChangePlaymode) { Time.timeScale = run.originalTimeScale; EditorApplication.isPlaying = false; }
                else Finish(run);
            }
        }

        static void Initialize(Run run)
        {
            world = Components<StationSocialWorld>(SceneManager.GetActiveScene()).Single(); desk = world.informationDesk;
            Require(desk && desk.queueSlots.Length >= 6, "Accueil needs six saved queue positions.");
            run.baselinePassengers = world.passengers.Length; Require(run.baselinePassengers == 300, "Expected 300 saved passengers.");
            foreach (var p in world.passengers) p.automaticRequests = false;
            foreach (var d in new[] { desk }.Concat(world.shopDesks)) d.conversationDuration = 999f;
            fixture = world.passengers.Where(p => p.activity == StationPassengerActivity.StandingGroup && p.IsAvailableForActivity)
                .OrderBy(p => FlatDistance(p.transform.position, desk.queueSlots[0].position)).Take(7).ToArray();
            Require(fixture.Length == 7, "Seven available standing passengers are required.");
            passer = world.passengers.First(p => p.activity == StationPassengerActivity.SoloWalking && p.IsAvailableForActivity);
            foreach (var p in fixture) { p.queueWaitTimeout = 300f; p.approachTimeout = 300f; p.navigationTimeout = 120f; }
            queueDirection = desk.queueSlots[1].position - desk.queueSlots[0].position; queueDirection.y = 0f; queueDirection.Normalize();
            expected.Clear(); observed.Clear(); advances.Clear(); predecessors.Clear(); fixedBefore.Clear(); wave = null;
            desk.queueJoined.AddListener(p =>
            {
                if (live == null) return;
                observed.Add(p); live.admissionOrder.Add(p.name);
                Require(desk.TryGetQueueSlot(p, out var slot) && FlatDistance(p.transform.position, slot.position) <= desk.arrivalTolerance + .05f,
                    "Admission was not a physical arrival: " + p.name);
                live.physicalAdmissions++;
            });
            desk.conversationStarted.AddListener(p => { Require(expected.Count > 0 && expected[0] == p, "A customer overtook the physical FIFO."); if (live != null) live.fifoChecks++; });
            desk.conversationEnded.AddListener(p => expected.Remove(p));
            Time.timeScale = 1f; Application.runInBackground = true; Application.targetFrameRate = 90; QualitySettings.vSyncCount = 0;
            run.startedAt = Time.time; SetPhase(run, "building"); PlaceNext(run);
        }

        static void PlaceNext(Run run)
        {
            var p = fixture[run.nextFixtureActor];
            Require(p.motion.EnableNavigationAt(desk.queueSlots[expected.Count].position), "Fixture cannot attach to saved queue floor: " + p.name);
            run.setupPlacements++; expected.Add(p);
            Require(p.TryVisitService(desk), "Fixture actor could not request accueil: " + p.name);
            run.nextFixtureActor++;
        }

        static void Observe(Run run)
        {
            foreach (var p in expected)
            {
                if (!desk.Contains(p)) continue;
                Require(desk.GetQueueIndex(p) == expected.IndexOf(p), "FIFO index changed without departure: " + p.name);
            }
            for (int i = 1; i < expected.Count; i++)
            {
                var a = expected[i - 1]; var b = expected[i];
                if (!desk.Contains(a) || !desk.Contains(b)) continue;
                float gap = FlatDistance(a.transform.position, b.transform.position);
                run.minimumMovingSpacing = Mathf.Min(run.minimumMovingSpacing, gap);
                Require(Vector3.Dot(b.transform.position - a.transform.position, queueDirection) > -.12f, "A queued traveller passed the predecessor: " + b.name);
            }
            if (wave != null && !wave.passed)
            {
                foreach (var pair in advances)
                {
                    var p = pair.Key; var a = pair.Value; var pred = predecessors[p];
                    float predMoved = FlatDistance(pred.transform.position, a.predecessorBefore);
                    if (a.predecessorMovedAt < 0f && predMoved >= .12f) a.predecessorMovedAt = Time.time;
                    float movement = Vector3.Dot(a.before - p.transform.position, queueDirection);
                    a.totalAdvance = Mathf.Max(a.totalAdvance, movement);
                    if (a.targetChangedAt < 0f && desk.GetLastQueueAdvanceTime(p) >= wave.triggeredAt)
                        a.targetChangedAt = desk.GetLastQueueAdvanceTime(p);
                    if (a.startedAt < 0f && movement >= .04f)
                    {
                        a.startedAt = Time.time; a.startFrame = Time.frameCount;
                        Require(a.predecessorMovedAt >= 0f, "Queue follower moved before observing predecessor clearance: " + p.name);
                        a.reactionObserved = a.startedAt - a.predecessorMovedAt;
                    }
                    if (a.startedAt >= 0f && IsSettled(p) && a.totalAdvance > .3f && a.finishedAt < 0f) a.finishedAt = Time.time;
                }
                foreach (var pair in fixedBefore) Require(FlatDistance(pair.Key.transform.position, pair.Value) < .20f, "An unaffected person moved after a cancellation behind them: " + pair.Key.name);
            }
        }

        static void AdvanceScenario(Run run)
        {
            Require(Time.time - run.phaseAt < (run.phase == "crossing" ? 60f : 40f), "Scenario did not finish: " + run.phase + "; " + Describe());
            switch (run.phase)
            {
                case "building":
                    if (!expected.All(p => desk.Contains(p) && IsSettled(p))) return;
                    if (run.nextFixtureActor < 6) { PlaceNext(run); return; }
                    SetPhase(run, "settled"); break;
                case "settled":
                    CheckSettledSpacing(run);
                    if (Time.time - run.phaseAt < 2f) return;
                    run.compactSpacingPassed = true;
                    StartWave(run, 0, "Front customer finishes service and walks away"); SetPhase(run, "head-wave"); break;
                case "head-wave":
                    if (!WaveFinished(run)) return;
                    run.headWavePassed = true; SetPhase(run, "head-settled"); break;
                case "head-settled":
                    CheckSettledSpacing(run);
                    if (Time.time - run.phaseAt < 1f) return;
                    StartWave(run, 2, "Middle customer cancels"); SetPhase(run, "middle-wave"); break;
                case "middle-wave":
                    if (!WaveFinished(run)) return;
                    run.middleWavePassed = true; SetPhase(run, "middle-settled"); break;
                case "middle-settled":
                    CheckSettledSpacing(run);
                    if (Time.time - run.phaseAt < 1f) return;
                    run.nextFixtureActor = 6; PlaceNext(run); SetPhase(run, "newcomer"); break;
                case "newcomer":
                    if (!expected.All(p => desk.Contains(p) && IsSettled(p))) return;
                    Require(expected.Count == 5 && observed.Count == 7 && desk.GetQueueIndex(fixture[6]) == 4, "A newly arrived customer did not join the physical tail.");
                    run.newcomerFifoPassed = true; BeginCrossing(run); SetPhase(run, "crossing"); break;
                case "crossing":
                    ObserveCrossing(run);
                    if (!passer.motion.HasArrived) return;
                    Require(FlatDistance(passer.transform.position, crossingGoal) < .5f, "Passer did not reach the opposite side.");
                    run.passerClearanceAboveLegacyLimit = run.minimumPasserDistance > .40f;
                    if (!run.passerClearanceAboveLegacyLimit) run.warnings.Add("Native passer came within 0.40m of a queued actor; passage completion is not collision-free validation.");
                    if (run.occupiedCenterlineCrossings > 0) run.warnings.Add("Native passer crossed between occupied queue members " + run.occupiedCenterlineCrossings + " times; no queue-aware custom planner is enabled.");
                    run.nativePassagePassed = true; BeginRearDeparture(run); SetPhase(run, "rear-egress"); break;
                case "rear-egress":
                    ObserveRearDeparture(run);
                    if (!wave.passed && !WaveFinished(run)) return;
                    if (rearDeparture.CurrentState != StationPassengerState.StandingWithGroup || FlatDistance(rearDeparture.transform.position, rearGoal) >= .5f) return;
                    run.rearDepartureArrivalAt = Time.time;
                    run.rearClearanceAboveLegacyLimit = run.minimumRearDepartureDistance > .50f;
                    if (!run.rearClearanceAboveLegacyLimit) run.warnings.Add("Native departing customer came within 0.50m of a queued actor; social completion does not guarantee physical clearance.");
                    if (run.rearDepartureCenterlineCrossings > 0) run.warnings.Add("Native departing customer crossed between occupied queue members " + run.rearDepartureCenterlineCrossings + " times.");
                    run.rearDeparturePassed = true; Complete(run); break;
            }
        }

        static void CheckSettledSpacing(Run run)
        {
            for (int i = 1; i < expected.Count; i++)
            {
                float gap = FlatDistance(expected[i - 1].transform.position, expected[i].transform.position);
                run.minimumSettledSpacing = Mathf.Min(run.minimumSettledSpacing, gap); run.maximumSettledSpacing = Mathf.Max(run.maximumSettledSpacing, gap);
                Require(gap >= .62f && gap <= 1.05f, "Settled queue spacing is not compact and non-overlapping: " + gap);
            }
        }

        static void StartWave(Run run, int index, string reason)
        {
            var departed = expected[index];
            wave = new Wave { cause = reason, departed = departed.name, triggeredAt = Time.time }; run.waves.Add(wave);
            advances.Clear(); predecessors.Clear(); fixedBefore.Clear();
            for (int i = 0; i < index; i++) fixedBefore.Add(expected[i], expected[i].transform.position);
            for (int i = index + 1; i < expected.Count; i++)
            {
                var p = expected[i]; var pred = expected[i - 1];
                var a = new Advance { passenger = p.name, predecessor = pred.name, before = p.transform.position, predecessorBefore = pred.transform.position };
                advances.Add(p, a); predecessors.Add(p, pred); wave.advances.Add(a);
            }
            if (index == 0)
            {
                Require(desk.ActiveCustomer == departed, "Front fixture customer must be actively conversing before service completion.");
                // The fixture held the conversation while six saved customers assembled.
                // Expire only its timer; the ordinary desk Update performs the completion,
                // event callbacks, passenger return, and gradual queue advancement.
                var deadline = typeof(StationInformationDesk).GetField("conversationEndsAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Require(deadline != null, "Could not release the fixture conversation timer.");
                deadline.SetValue(desk, Time.time);
            }
            else { expected.RemoveAt(index); desk.Cancel(departed); }
        }

        static bool WaveFinished(Run run)
        {
            if (wave.advances.Any(a => a.finishedAt < 0f)) return false;
            for (int i = 0; i < wave.advances.Count; i++)
            {
                var a = wave.advances[i];
                Require(a.reactionObserved >= .1f, "Follower did not show a perceptible reaction after predecessor movement: " + a.passenger + " delay=" + a.reactionObserved);
                if (i > 0) Require(a.startFrame > wave.advances[i - 1].startFrame && a.startedAt > wave.advances[i - 1].startedAt + .10f,
                    "The queue moved in sync instead of propagating from the front: " + a.passenger);
            }
            wave.passed = true; wave.completedAt = Time.time; advances.Clear(); predecessors.Clear(); fixedBefore.Clear();
            return true;
        }

        static void BeginCrossing(Run run)
        {
            var center = (expected[1].transform.position + expected[2].transform.position) * .5f;
            var lateral = Vector3.Cross(Vector3.up, queueDirection).normalized;
            Require(NavMesh.SamplePosition(center + lateral * 3f, out var start, .6f, NavMesh.AllAreas), "No saved floor at the crossing start.");
            Require(NavMesh.SamplePosition(center - lateral * 3f, out var goal, .6f, NavMesh.AllAreas), "No saved floor at the crossing destination.");
            crossingStart = start.position; crossingGoal = goal.position; crossingPrevious = crossingStart;
            passer.enabled = false; Require(passer.motion.EnableNavigationAt(crossingStart), "Cannot initialize crossing traveller."); run.setupPlacements++;
            initialDetours = passer.motion.QueueDetourCount;
            severeOverlapSince.Clear();
            Require(passer.motion.TryMoveTo(crossingGoal), "Crossing route could not be requested."); passer.motion.PlayWalk();
        }

        static void ObserveCrossing(Run run)
        {
            var point = passer.transform.position;
            run.passerTravel += FlatDistance(crossingPrevious, point);
            run.passerDetours = passer.motion.QueueDetourCount - initialDetours;
            foreach (var p in expected)
            {
                float distance = FlatDistance(point, p.transform.position);
                run.minimumPasserDistance = Mathf.Min(run.minimumPasserDistance, distance);
                RecordPassageOverlap(run, passer, p, distance, false);
            }
            for (int i = 1; i < expected.Count; i++)
                if (Crosses(crossingPrevious, point, expected[i - 1].transform.position, expected[i].transform.position)) run.occupiedCenterlineCrossings++;
            crossingPrevious = point;
        }

        static void BeginRearDeparture(Run run)
        {
            rearDeparture = expected[0];
            Require(rearDeparture.homePoint, "Rearward departure fixture needs an existing saved home anchor.");
            Require(NavMesh.SamplePosition(expected[expected.Count - 1].transform.position + queueDirection * 3f,
                out var goal, .6f, NavMesh.AllAreas), "No saved floor behind the queue for the home anchor.");
            rearStart = rearPrevious = rearDeparture.transform.position; rearGoal = goal.position;
            rearDeparture.homePoint.position = rearGoal; run.temporaryHomeAnchorChanges++;
            run.rearDepartureStartedAt = Time.time; initialRearDetours = rearDeparture.motion.QueueDetourCount;
            severeOverlapSince.Clear();
            StartWave(run, 0, "Served head returns to usual activity behind the occupied queue");
        }

        static void ObserveRearDeparture(Run run)
        {
            var point = rearDeparture.transform.position;
            run.rearDepartureTravel += FlatDistance(rearPrevious, point);
            run.rearDepartureDetours = rearDeparture.motion.QueueDetourCount - initialRearDetours;
            var lateral = Vector3.Cross(Vector3.up, queueDirection).normalized;
            if (run.rearDepartureSideStepAt < 0f && Mathf.Abs(Vector3.Dot(point - rearStart, lateral)) >= .82f)
                run.rearDepartureSideStepAt = Time.time;
            foreach (var p in expected)
                if (p != rearDeparture)
                {
                    float distance = FlatDistance(point, p.transform.position);
                    run.minimumRearDepartureDistance = Mathf.Min(run.minimumRearDepartureDistance, distance);
                    RecordPassageOverlap(run, rearDeparture, p, distance, true);
                }
            for (int i = 1; i < expected.Count; i++)
                if (expected[i - 1] != rearDeparture && expected[i] != rearDeparture &&
                    Crosses(rearPrevious, point, expected[i - 1].transform.position, expected[i].transform.position)) run.rearDepartureCenterlineCrossings++;
            rearPrevious = point;
        }

        static void RecordPassageOverlap(Run run, StationPassenger moving, StationPassenger queued, float distance, bool rear)
        {
            float penetration = Mathf.Max(0f, moving.motion.agent.radius + queued.motion.agent.radius - distance);
            if (rear) run.maximumRearRadiusPenetration = Mathf.Max(run.maximumRearRadiusPenetration, penetration);
            else run.maximumPasserRadiusPenetration = Mathf.Max(run.maximumPasserRadiusPenetration, penetration);
            if (distance >= .25f) { severeOverlapSince.Remove(queued); return; }
            if (!severeOverlapSince.TryGetValue(queued, out float since)) { since = Time.time; severeOverlapSince[queued] = since; }
            float duration = Time.time - since;
            if (rear) run.maximumRearSevereOverlapSeconds = Mathf.Max(run.maximumRearSevereOverlapSeconds, duration);
            else run.maximumPasserSevereOverlapSeconds = Mathf.Max(run.maximumPasserSevereOverlapSeconds, duration);
            if (duration >= 2f && !run.severePersistentOverlapObserved)
            {
                run.severePersistentOverlapObserved = true;
                run.warnings.Add("SEVERE PERSISTENT OVERLAP: native passage held actor centres below 0.25m for at least two seconds: " + moving.name + " / " + queued.name);
            }
        }

        static bool Crosses(Vector3 from, Vector3 to, Vector3 a, Vector3 b)
        {
            var r = new Vector2(to.x - from.x, to.z - from.z); var s = new Vector2(b.x - a.x, b.z - a.z);
            var q = new Vector2(a.x - from.x, a.z - from.z); float d = Cross(r, s);
            if (Mathf.Abs(d) < .000001f) return false;
            float t = Cross(q, s) / d, u = Cross(q, r) / d;
            return t > 0f && t <= 1f && u > .01f && u < .99f;
        }
        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        static bool IsSettled(StationPassenger p) => (p.CurrentState == StationPassengerState.WaitingInQueue || p.CurrentState == StationPassengerState.Talking) && p.motion.agent.velocity.sqrMagnitude < .01f;
        static void SetPhase(Run run, string phase) { run.phase = phase; run.phaseAt = Time.time; }
        static void Complete(Run run)
        {
            run.finalObjects = Components<Transform>(SceneManager.GetActiveScene()).Length;
            Require(run.finalObjects == run.baselineObjects && world.passengers.Length == run.baselinePassengers, "Queue test changed the saved object or passenger count.");
            Require(run.physicalAdmissions == 7 && run.fifoChecks >= 3, "Insufficient physical admissions or service-order observations.");
            run.phase = "exiting"; Save(run); Time.timeScale = run.originalTimeScale; EditorApplication.isPlaying = false;
        }
        static void ModeChanged(PlayModeStateChange state)
        {
            var run = live ?? Load(); if (run == null) return;
            if (state == PlayModeStateChange.EnteredPlayMode) { run.phase = "initializing"; run.readyFrame = Time.frameCount + 6; }
            else if (state == PlayModeStateChange.ExitingPlayMode && run.phase != "exiting") { run.abort = true; run.errors.Add("Play was stopped before queue verification completed."); run.phase = "exiting"; }
            else if (state == PlayModeStateChange.EnteredEditMode) run.phase = "restoring";
            Save(run);
        }
        static void Finish(Run run)
        {
            if (run.capturedSettings)
            {
                try
                {
                    Time.timeScale = run.originalTimeScale; Application.runInBackground = run.originalRunInBackground; Application.targetFrameRate = run.originalTargetFrameRate; QualitySettings.vSyncCount = run.originalVSyncCount;
                    EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(run.originalPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(run.originalPlayScene);
                    EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)run.originalOptions; EditorSettings.enterPlayModeOptionsEnabled = run.originalOptionsEnabled; EditorApplication.isPaused = run.originalPaused;
                    run.restoredSettings = true; CleanScenes(); EditorSceneManager.RestoreSceneManagerSetup(run.scenes.Select(s => new SceneSetup { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray()); run.restoredScenes = true;
                }
                catch (Exception error) { run.errors.Add("Restoration failed: " + error); }
            }
            var log = SessionState.GetString(LogKey, ""); if (!string.IsNullOrEmpty(log)) run.errors.Add("Unity errors:\n" + log);
            run.success = !run.abort && run.errors.Count == 0 && run.restoredSettings && run.restoredScenes && run.compactSpacingPassed && run.headWavePassed && run.middleWavePassed && run.newcomerFifoPassed && run.nativePassagePassed && run.rearDeparturePassed;
            run.phase = "complete"; run.completedUtc = DateTime.UtcNow.ToString("O"); SessionState.EraseString(StateKey); SessionState.EraseString(LogKey);
            Write(run); live = null; world = null; desk = null; fixture = null; passer = null; rearDeparture = null;
            expected.Clear(); observed.Clear(); advances.Clear(); predecessors.Clear(); fixedBefore.Clear();
            Debug.Log("Station reactive queue Play verification " + (run.success ? "PASSED" : "FAILED") + ". See " + ReportPath);
        }
        static void Log(string message, string trace, LogType type)
        {
            if (Load() == null || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            var previous = SessionState.GetString(LogKey, ""); if (previous.Length < 16000) SessionState.SetString(LogKey, previous + message + "\n" + trace + "\n");
        }
        static Run Load() { var json = SessionState.GetString(StateKey, ""); return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Run>(json); }
        static void Save(Run run)
        {
            SessionState.SetString(StateKey, JsonUtility.ToJson(run));
            if (EditorApplication.timeSinceStartup < nextWrite) return;
            nextWrite = EditorApplication.timeSinceStartup + 1; Write(run);
        }
        static void Write(Run run) { Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath, JsonUtility.ToJson(run, true)); }
        static string Describe() => fixture == null ? "" : string.Join("; ", fixture.Select(p => p ? p.name + ":" + p.CurrentState + "/" + p.LastIssue : "missing"));
        static void CleanScenes() { for (int i = 0; i < SceneManager.sceneCount; i++) Require(!SceneManager.GetSceneAt(i).isDirty, "Save or revert unsaved scenes before checking. The check never discards user edits."); }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        static float FlatDistance(Vector3 a, Vector3 b) { a.y = b.y = 0f; return Vector3.Distance(a, b); }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
