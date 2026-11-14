#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.TrainStation.RailOperations;
using StationFlow.TrainStation.Social;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    [InitializeOnLoad]
    public static class StationDecisionLabVerification
    {
        const string RunKey = "StationFlow.DecisionLab.PlayCheck";
        const string LogKey = RunKey + ".Errors";
        [Serializable] public sealed class SavedReport
        {
            public bool success, originalScenePreserved, originalSchedulePreserved, scheduleIsolated, restartSceneRegistered;
            public string checkedUtc, scene;
            public int participants, backgroundActors, routeChecks, poiCount;
            public List<string> errors = new List<string>();
        }
        [Serializable] public sealed class TravellerResult
        {
            public string id, serviceId, finalActivity, finalExecution, target, issue, decisionReason;
            public bool checkedDisplay, finished, boarded, boardingViolation;
            public int decisions, completedActivities, optionalCompleted;
            public float distanceTravelled;
            public Vector3 previousPosition;
            public List<string> activities = new List<string>();
        }
        [Serializable] public sealed class Run
        {
            public string startedUtc, completedUtc, phase, unityVersion, originalSceneHash, originalScheduleHash, originalPlayScene, outputDirectory;
            public long deadlineTicks;
            public bool success, batch, pausePassed, delayObserved, captured, oldOptionsEnabled, oldPaused, oldRunInBackground;
            public bool cancellationFixture, cancellationInjected, cancellationObserved;
            public bool nativeSpeedFixture;
            public bool originalScenePreserved, originalSchedulePreserved;
            public int oldOptions, oldTargetFrameRate, oldVsync, checkAfterFrame, frames, boarded, finished, totalDecisions, routePendingFrames, incompletePathFrames;
            public float oldTimeScale, startedAt, elapsed, pauseStartedUnscaled;
            public float verificationTimeScale = 4f;
            public float cancellationScheduledAt = -1f, cancellationInjectedAt = -1f;
            public double initialRailElapsed, pauseClock, maxClockDrift;
            public int trainArrivals, trainDepartures, trainStateChanges, serviceProfileChecks;
            public int domainReloadRecoveries;
            public long decisionLogBytes, trajectoryLogBytes, eventLogBytes;
            public List<TravellerResult> travellers = new List<TravellerResult>();
            public List<string> activities = new List<string>(), errors = new List<string>();
            public string limitation = "Native crowd avoidance remains the station baseline. Boarding is virtual at platform anchors and toilets use entrance dwell. Shop seating uses the existing sit/idle/stand clips; it does not include dedicated sipping or page-turning gestures.";
        }
        static double nextPoll;
        static StationDecisionWorld liveWorld;
        static Run liveRun;
        static StationDecisionLabVerification()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            Application.logMessageReceived += OnLog;
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Validate saved experiment")]
        public static void ValidateSavedScene()
        {
            RequireEditAndClean();
            Require(File.Exists(ScenePath), "Create the isolated experiment first.");
            // Verification reads the saved fixture; explicit authoring menus apply any presentation or gait upgrades.
            if (SceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var report = Inspect(SceneManager.GetActiveScene());
            Write(ReportDirectory + "/saved-scene-check.json", report);
            Require(report.success, string.Join("\n", report.errors));
        }
        static SavedReport Inspect(Scene scene)
        {
            var result = new SavedReport { checkedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path };
            try
            {
                Require(scene.path == ScenePath, "Validation must run only in the isolated scene.");
                var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestPath));
                result.originalScenePreserved = Hash(SourceScene) == manifest.sourceSceneSha256;
                result.originalSchedulePreserved = Hash(SourceSchedule) == manifest.sourceScheduleSha256;
                Require(result.originalScenePreserved && result.originalSchedulePreserved, "Original station scene or schedule differs from the creation baseline.");
                var world = Components<StationDecisionWorld>(scene).Single();
                Require(world.rail && world.rail.schedule, "Missing shared experiment rail controller.");
                result.scheduleIsolated = AssetDatabase.GetAssetPath(world.rail.schedule) == SchedulePath;
                Require(result.scheduleIsolated, "The experiment must use its duplicated schedule asset.");
                Require(world.rail.ValidateConfiguration().Count == 0, string.Join("; ", world.rail.ValidateConfiguration()));
                Require(Mathf.Approximately(world.rail.simulationRate, 1f), "The experiment clock must match scaled Unity time.");
                Require(world.participants != null && world.participants.Length == ParticipantCount, "Expected 24 opted-in travellers.");
                Require(world.participants.All(p => p && p.world == world && p.motion && p.passenger && p.passenger.externalActivityControl && !p.passenger.automaticRequests),
                    "A participant is missing its explicit experimental control contract.");
                Require(world.participants.Select(p => p.passengerId).Distinct().Count() == world.participants.Length, "Passenger IDs must be unique.");
                Require(world.pois != null && world.pois.All(p => p && p.interactionPoint && p.capacity > 0), "Missing saved POI anchors or capacity.");
                Require(world.pois.Select(p => p.poiId).Distinct().Count() == world.pois.Length, "POI IDs must be unique.");
                var required = new[] { DecisionActivity.Display, DecisionActivity.Cafe, DecisionActivity.Toilet, DecisionActivity.Shop,
                    DecisionActivity.Rest, DecisionActivity.Info, DecisionActivity.Platform, DecisionActivity.Exit };
                foreach (var activity in required) Require(world.pois.Any(p => p.activity == activity), "Missing activity anchor: " + activity);
                foreach (var traveller in world.participants)
                {
                    Require(traveller.gameObject.activeInHierarchy, "Experiment participant is inactive: " + traveller.passengerId);
                    var service = world.rail.schedule.services.Single(s => s.serviceId == traveller.serviceId);
                    Require(Mathf.Abs(service.departureOffsetSeconds - traveller.departureOffsetSeconds) < .001f, "Saved assigned trip does not match schedule.");
                    Require(world.pois.Any(p => p.activity == DecisionActivity.Platform && p.trackNumber == service.trackNumber), "Missing assigned platform.");
                    foreach (var poi in world.pois)
                    {
                        var path = new NavMeshPath();
                        var from = Snap(traveller.transform.position); var to = Snap(poi.interactionPoint.position);
                        Require(NavMesh.CalculatePath(from, to, traveller.motion.agent.areaMask, path) && path.status == NavMeshPathStatus.PathComplete,
                            "No saved route from " + traveller.passengerId + " to " + poi.poiId);
                        ++result.routeChecks;
                    }
                }
                result.participants = world.participants.Length; result.poiCount = world.pois.Length;
                result.backgroundActors = Components<StationPassenger>(scene).Count(p => p.gameObject.activeInHierarchy && !p.externalActivityControl);
                Require(result.backgroundActors == BackgroundCount, "Background population should preserve 60 active travellers.");
                Require(!Components<StationPassenger>(scene).Any(p => p.gameObject.activeInHierarchy && p.automaticRequests), "Automatic baseline service requests would compete with experimental policy.");
                result.restartSceneRegistered = EditorBuildSettings.scenes.Any(s => s.enabled && s.path == ScenePath);
                Require(result.restartSceneRegistered, "Experiment must be registered for the restart action.");
                result.success = true;
            }
            catch (Exception error) { result.errors.Add(error.ToString()); }
            return result;
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Check actual Play mode decisions")]
        public static void BeginPlayCheck() => Begin(false);
        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Check cancellation and replanning in Play mode")]
        public static void BeginCancellationCheck() => Begin(false, true);
        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Check cancellation at ordinary 1x speed")]
        public static void BeginNativeSpeedCancellationCheck() => Begin(false, true, 1f);
        /// <summary>Launch Unity without -quit; the verifier exits after completing Play and restoring settings.</summary>
        public static void RunBatchValidation()
        {
            try { if (!File.Exists(ScenePath)) Create(); Begin(Application.isBatchMode); }
            catch (Exception error)
            {
                Debug.LogException(error);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                else throw;
            }
        }
        static void Begin(bool batch, bool cancellationFixture = false, float verificationTimeScale = 4f)
        {
            Require(string.IsNullOrEmpty(SessionState.GetString(RunKey, "")), "A Decision Lab check is already active.");
            RequireEditAndClean();
            ValidateSavedScene();
            var run = new Run { startedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                batch = batch, cancellationFixture = cancellationFixture, phase = "entering", deadlineTicks = DateTime.UtcNow.AddMinutes(8).Ticks,
                nativeSpeedFixture = cancellationFixture && Mathf.Approximately(verificationTimeScale, 1f),
                verificationTimeScale = verificationTimeScale,
                cancellationScheduledAt = cancellationFixture ? 2f : -1f,
                originalSceneHash = Hash(SourceScene), originalScheduleHash = Hash(SourceSchedule),
                originalPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene),
                oldOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled, oldOptions = (int)EditorSettings.enterPlayModeOptions,
                oldPaused = EditorApplication.isPaused, oldTimeScale = Time.timeScale, oldRunInBackground = Application.runInBackground,
                oldTargetFrameRate = Application.targetFrameRate, oldVsync = QualitySettings.vSyncCount };
            SessionState.EraseString(LogKey); Save(run);
            EditorSettings.enterPlayModeOptionsEnabled = false;
            EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = true;
        }
        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + .05;
            var run = liveRun ?? Load(); if (run == null) return;
            try
            {
                if (run.phase == "restoring" && !EditorApplication.isPlayingOrWillChangePlaymode) { Finish(run); return; }
                Require(DateTime.UtcNow.Ticks < run.deadlineTicks, "Runtime validation timed out in phase " + run.phase);
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                Require(string.IsNullOrEmpty(SessionState.GetString(LogKey, "")), "Unity reported an error: " + SessionState.GetString(LogKey, ""));
                if (run.phase == "initializing" && EditorApplication.isPlaying && Time.frameCount >= run.checkAfterFrame) Initialize(run);
                else if (run.phase == "running" && EditorApplication.isPlaying) { EnsureLiveWorld(run); Observe(run); }
                else if (run.phase == "paused" && EditorApplication.isPlaying) { EnsureLiveWorld(run); ObservePause(run); }
                Save(run);
            }
            catch (Exception error)
            {
                run.errors.Add(error.ToString()); run.phase = "exiting"; Save(run); Write(ReportPath(run), run);
                if (EditorApplication.isPlayingOrWillChangePlaymode) { Time.timeScale = run.oldTimeScale; EditorApplication.isPlaying = false; }
                else Finish(run);
            }
        }
        static void Initialize(Run run)
        {
            Require(SceneManager.GetActiveScene().path == ScenePath, "Play loaded a scene other than Decision Lab.");
            liveRun = run;
            liveWorld = Components<StationDecisionWorld>(SceneManager.GetActiveScene()).Single();
            Require(liveWorld.rail && liveWorld.rail.IsReady, "Experiment rail initialization failed: " +
                (liveWorld.rail ? liveWorld.rail.ConfigurationIssue : "missing rail controller"));
            run.startedAt = Time.time; run.initialRailElapsed = liveWorld.rail.ElapsedSimulationSeconds;
            run.outputDirectory = liveWorld.OutputDirectory;
            run.travellers = liveWorld.participants.Select(p => new TravellerResult { id = p.passengerId,
                serviceId = p.serviceId, previousPosition = p.transform.position }).ToList();
            BindRailObservers();
            Application.runInBackground = true; Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0;
            Time.timeScale = run.verificationTimeScale;
            run.verificationTimeScale = Time.timeScale;
            run.phase = "running";
        }
        static void EnsureLiveWorld(Run run)
        {
            if (liveWorld) return;
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == ScenePath, "Play validation lost its experiment scene after an Editor reload. Reopen Decision Lab and rerun the check.");
            var worlds = Components<StationDecisionWorld>(scene);
            Require(worlds.Length == 1 && worlds[0], "Play validation could not recover the Decision Lab world after an Editor reload. The existing run was preserved; rerun from a clean Play start.");
            var recovered = worlds[0];
            Require(recovered.rail && recovered.rail.IsReady, "The recovered Decision Lab railway is unavailable or uninitialized after an Editor reload. Rerun from a clean Play start.");
            Require(recovered.participants != null && recovered.participants.Length == run.travellers.Count,
                "The participant registry changed during Play validation; the accumulated run cannot be continued.");
            for (int i = 0; i < recovered.participants.Length; ++i)
            {
                var passenger = recovered.participants[i]; var prior = run.travellers[i];
                Require(passenger && passenger.passengerId == prior.id && passenger.motion && passenger.ActivityHistory != null,
                    "Passenger references changed or disappeared during an Editor reload: " + prior.id);
                Require(passenger.DecisionCount >= prior.decisions && (!prior.checkedDisplay || passenger.HasCheckedDisplay) &&
                    (!prior.finished || passenger.Finished) && (!prior.boarded || passenger.Boarded),
                    "Passenger runtime state reset during an Editor reload: " + prior.id + ". Rerun from a clean Play start; accumulated observations were preserved.");
            }
            Require(!recovered.loggingEnabled || (!string.IsNullOrEmpty(recovered.OutputDirectory) &&
                recovered.OutputDirectory == run.outputDirectory), "The audit stream was lost or replaced during an Editor reload. Rerun from a clean Play start.");
            liveWorld = recovered;
            liveRun = run;
            ++run.domainReloadRecoveries;
            BindRailObservers();
        }
        static void BindRailObservers()
        {
            liveWorld.rail.trainArrived.RemoveListener(OnTrainArrived);
            liveWorld.rail.trainDeparted.RemoveListener(OnTrainDeparted);
            liveWorld.rail.trainStateChanged.RemoveListener(OnTrainStateChanged);
            liveWorld.rail.trainArrived.AddListener(OnTrainArrived);
            liveWorld.rail.trainDeparted.AddListener(OnTrainDeparted);
            liveWorld.rail.trainStateChanged.AddListener(OnTrainStateChanged);
        }
        static void OnTrainArrived(StationTrain train) { if (liveRun != null) ++liveRun.trainArrivals; }
        static void OnTrainDeparted(StationTrain train) { if (liveRun != null) ++liveRun.trainDepartures; }
        static void OnTrainStateChanged(StationTrain train) { if (liveRun != null) ++liveRun.trainStateChanges; }
        static void Observe(Run run)
        {
            run.elapsed = Time.time - run.startedAt;
            ++run.frames;
            run.maxClockDrift = Math.Max(run.maxClockDrift, Math.Abs((liveWorld.rail.ElapsedSimulationSeconds - run.initialRailElapsed) - run.elapsed));
            Require(run.maxClockDrift < .8, "Passenger and train clocks drifted by " + run.maxClockDrift + " seconds.");
            for (int i = 0; i < liveWorld.participants.Length; ++i)
            {
                var p = liveWorld.participants[i]; var result = run.travellers[i];
                Require(p && p.motion && p.ActivityHistory != null, "A participant lost its runtime references: " + result.id);
                Vector3 position = p.transform.position;
                Require(Finite(position), "Non-finite passenger position: " + p.passengerId);
                result.distanceTravelled += Vector3.Distance(position, result.previousPosition); result.previousPosition = position;
                result.finalActivity = p.CurrentActivity.ToString(); result.finalExecution = p.ExecutionState;
                result.target = p.CurrentTargetId; result.checkedDisplay = p.HasCheckedDisplay; result.finished = p.Finished;
                result.boarded = p.Boarded; result.decisions = p.DecisionCount; result.completedActivities = p.CompletedActivities;
                result.optionalCompleted = p.CompletedOptionalCount;
                result.issue = p.NavigationIssue; result.decisionReason = p.LastDecisionReason; result.boardingViolation = p.BoardingViolation;
                result.activities = p.ActivityHistory.ToList();
                Require(!p.BoardingViolation, "A passenger attempted invalid boarding: " + p.passengerId);
                var agent = p.motion.agent;
                if (p.ExecutionState == "Service" && p.passenger.CurrentState == StationPassengerState.ApproachingQueue && agent)
                {
                    ++run.serviceProfileChecks;
                    Require(Mathf.Abs(agent.speed - p.walkSpeed) < .001f,
                        "The FIFO executor replaced the experiment walking profile for " + p.passengerId);
                }
                if (!p.Finished && agent && agent.enabled && agent.isOnNavMesh)
                {
                    if (agent.pathPending) ++run.routePendingFrames;
                    else if (agent.hasPath && agent.pathStatus != NavMeshPathStatus.PathComplete) ++run.incompletePathFrames;
                }
            }
            if (run.elapsed > 70f)
                run.delayObserved = liveWorld.rail.schedule.services[0].departureOffsetSeconds >= 254.9f;
            if (run.cancellationFixture && !run.cancellationInjected && liveWorld.ElapsedSeconds >= run.cancellationScheduledAt)
            {
                // Cancel at 2 simulation seconds, before the affected travellers finish their first display visit.
                // This event is Play-only. It never enters the saved demonstration or its duplicated schedule.
                run.cancellationInjectedAt = liveWorld.ElapsedSeconds;
                liveWorld.ApplyEvent(new StationDecisionScenarioEvent { atSeconds = run.cancellationScheduledAt,
                    serviceId = liveWorld.rail.schedule.services[3].serviceId,
                    kind = DecisionScenarioEventKind.Cancellation, announceImmediately = true });
                run.cancellationInjected = true;
            }
            if (run.cancellationInjected)
                run.cancellationObserved = liveWorld.ObserveTrip(liveWorld.rail.schedule.services[3].serviceId).status == DecisionTrainStatus.Cancelled;
            if (!run.pausePassed && run.elapsed >= 30f)
            {
                run.pauseClock = liveWorld.rail.ElapsedSimulationSeconds; run.pauseStartedUnscaled = Time.unscaledTime;
                liveWorld.TogglePause(); run.phase = "paused"; return;
            }
            if (!run.cancellationFixture && !run.captured && run.elapsed >= 100f)
            {
                StationDecisionLabCapture.Capture(); run.captured = true;
            }
            if (run.elapsed < (run.cancellationFixture ? 300f : 600f)) return;
            run.boarded = run.travellers.Count(p => p.boarded); run.finished = run.travellers.Count(p => p.finished);
            run.totalDecisions = run.travellers.Sum(p => p.decisions);
            run.activities = run.travellers.SelectMany(p => p.activities).Distinct().OrderBy(s => s).ToList();
            string fixtureCancelledService = run.cancellationFixture ? liveWorld.rail.schedule.services[3].serviceId : null;
            Require(run.travellers.Where(p => !run.cancellationFixture || p.serviceId != fixtureCancelledService).All(p => p.checkedDisplay),
                run.cancellationFixture ? "Every unaffected passenger must observe the timetable." : "Every experimental passenger must observe the timetable.");
            Require(run.travellers.All(p => p.distanceTravelled > 1f), "A participant never travelled.");
            Require(run.totalDecisions > ParticipantCount, "No sequence of passenger decisions was observed.");
            Require(run.boarded > 0, "No traveller completed valid virtual boarding.");
            Require(run.delayObserved, "The configured +75 second service delay was not observed.");
            Require(run.pausePassed, "Shared-clock pause was not verified.");
            Require(run.trainDepartures > 0, "No actual train departures were observed.");
            Require(run.travellers.Sum(p => p.optionalCompleted) > 0, "No genuine optional activity completed.");
            Require(run.serviceProfileChecks > 0, "No physical service approach verified its experiment speed profile.");
            if (run.cancellationFixture)
            {
                Require(run.cancellationObserved, "The Play-only cancellation was not reflected in operational truth.");
                var affected = run.travellers.Where(p => p.serviceId == fixtureCancelledService).ToArray();
                Require(affected.Length == 6 && affected.All(p => p.finished && p.finalExecution == "Exited" && !p.boarded),
                    "All six travellers assigned to the early-cancelled service must finish by exiting without boarding.");
            }
            liveWorld.FlushLogs();
            run.outputDirectory = liveWorld.OutputDirectory;
            Require(!string.IsNullOrEmpty(run.outputDirectory) && Directory.Exists(run.outputDirectory), "The run did not create an audit directory.");
            run.decisionLogBytes = new FileInfo(Path.Combine(run.outputDirectory, "decisions.jsonl")).Length;
            run.trajectoryLogBytes = new FileInfo(Path.Combine(run.outputDirectory, "trajectories.jsonl")).Length;
            run.eventLogBytes = new FileInfo(Path.Combine(run.outputDirectory, "events.jsonl")).Length;
            Require(run.decisionLogBytes > 0 && run.trajectoryLogBytes > 0 && run.eventLogBytes > 0, "A required audit stream is empty.");
            run.phase = "exiting";
            Time.timeScale = run.oldTimeScale;
            Save(run); EditorApplication.isPlaying = false;
        }
        static void ObservePause(Run run)
        {
            Require(Math.Abs(liveWorld.rail.ElapsedSimulationSeconds - run.pauseClock) < .1, "Rail moved while the experiment was paused.");
            for (int i = 0; i < liveWorld.participants.Length; ++i)
                Require(Vector3.Distance(liveWorld.participants[i].transform.position, run.travellers[i].previousPosition) < .1f,
                    "A passenger moved while the experiment was paused.");
            if (Time.unscaledTime - run.pauseStartedUnscaled < .6f) return;
            liveWorld.TogglePause(); run.pausePassed = true; run.phase = "running";
        }
        static void ModeChanged(PlayModeStateChange change)
        {
            var run = liveRun ?? Load(); if (run == null) return;
            if (change == PlayModeStateChange.EnteredPlayMode) { run.phase = "initializing"; run.checkAfterFrame = Time.frameCount + 8; }
            else if (change == PlayModeStateChange.ExitingPlayMode && run.phase != "exiting") { run.errors.Add("Play was stopped before verification completed."); run.phase = "exiting"; }
            else if (change == PlayModeStateChange.EnteredEditMode) run.phase = "restoring";
            Save(run);
        }
        static void Finish(Run run)
        {
            try
            {
                Time.timeScale = run.oldTimeScale; Application.runInBackground = run.oldRunInBackground;
                Application.targetFrameRate = run.oldTargetFrameRate; QualitySettings.vSyncCount = run.oldVsync;
                EditorSettings.enterPlayModeOptionsEnabled = run.oldOptionsEnabled;
                EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)run.oldOptions;
                EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(run.originalPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(run.originalPlayScene);
                EditorApplication.isPaused = run.oldPaused;
                run.originalScenePreserved = Hash(SourceScene) == run.originalSceneHash;
                run.originalSchedulePreserved = Hash(SourceSchedule) == run.originalScheduleHash;
                if (!run.originalScenePreserved || !run.originalSchedulePreserved) run.errors.Add("Original scene or schedule changed during Play validation.");
                string logs = SessionState.GetString(LogKey, ""); if (!string.IsNullOrEmpty(logs)) run.errors.Add(logs);
            }
            catch (Exception error) { run.errors.Add(error.ToString()); }
            run.completedUtc = DateTime.UtcNow.ToString("O"); run.phase = "complete";
            run.success = run.errors.Count == 0;
            Write(ReportPath(run), run); SessionState.EraseString(RunKey); SessionState.EraseString(LogKey); liveRun = null; liveWorld = null;
            if (run.batch && Application.isBatchMode) EditorApplication.Exit(run.success ? 0 : 1);
        }
        static void OnLog(string message, string trace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (liveRun == null && string.IsNullOrEmpty(SessionState.GetString(RunKey, ""))) return;
            string prior = SessionState.GetString(LogKey, "");
            if (prior.Length < 16000) SessionState.SetString(LogKey, prior + message + "\n" + trace + "\n");
        }
        static bool Finite(Vector3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x) && !float.IsNaN(p.y) && !float.IsInfinity(p.y) && !float.IsNaN(p.z) && !float.IsInfinity(p.z);
        static Run Load() { var json = SessionState.GetString(RunKey, ""); return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Run>(json); }
        static void Save(Run run) => SessionState.SetString(RunKey, JsonUtility.ToJson(run));
        static string ReportPath(Run run) => ReportDirectory + (run.nativeSpeedFixture ? "/native-speed-cancellation-check.json" :
            run.cancellationFixture ? "/cancellation-check.json" : "/play-check.json");
    }
}
#endif
