using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using StationFlow.TrainStation.RailOperations;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab
{
    public enum DecisionScenarioEventKind { Delay, Cancellation }

    [Serializable]
    public sealed class StationDecisionScenarioEvent
    {
        public float atSeconds = 55f;
        public string serviceId;
        public DecisionScenarioEventKind kind;
        public float delaySeconds = 75f;
        public bool announceImmediately = true;
    }

    public struct DecisionTripObservation
    {
        public string serviceId;
        public int trackNumber;
        public float departureSeconds, cutoffSeconds;
        public DecisionTrainStatus status;
        public StationTrain train;
    }

    /// <summary>Scene-local experiment: one clock, immutable trip identity, contextual decisions and audit data.</summary>
    [DefaultExecutionOrder(150)]
    public sealed class StationDecisionWorld : MonoBehaviour
    {
        public StationRailOperations rail;
        public StationDecisionArrivalController arrivalController;
        public StationDecisionPlatformGate[] platformGates = Array.Empty<StationDecisionPlatformGate>();
        public StationDecisionCoachQueue[] coachQueues = Array.Empty<StationDecisionCoachQueue>();
        public StationDecisionPassenger[] participants = Array.Empty<StationDecisionPassenger>();
        public StationDecisionPoi[] pois = Array.Empty<StationDecisionPoi>();
        public string scenarioName = "Conditional activity experiment";
        public int seed = 20261005;
        public bool loggingEnabled = true;
        [Tooltip("The daily scene starts paused so recording options can be chosen before any passengers enter.")]
        public bool collectionSetupOnStart = true;
        [Range(1f, 30f)] public float trajectorySampleRate = 5f;
        public List<StationDecisionScenarioEvent> events = new List<StationDecisionScenarioEvent>();
        public string outputRoot = "Captures/TrainStationDecisionLab";
        public float congestionAllowance = 1.15f;
        StationRailSchedule runtimeSchedule;
        StationTimetableDisplay[] displays;
        readonly HashSet<string> cancelled = new HashSet<string>();
        readonly Dictionary<string, float> delays = new Dictionary<string, float>();
        readonly HashSet<int> firedEvents = new HashSet<int>();
        readonly HashSet<PlayableGraph> heldAnimationGraphs = new HashSet<PlayableGraph>();
        readonly Dictionary<string, int> admittedPassengers = new Dictionary<string, int>();
        readonly HashSet<string> admittedJourneys = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> admittedSeats = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, float> enteredAt = new Dictionary<string, float>();
        int sampleId;
        int lastAvatarSnapshotFrame = -1;
        long lastAvatarSnapshotSyncId = -1;
        string[] lastAvatarSnapshotIds = Array.Empty<string>();
        int archivedDecisionCount;
        Animator[] pausedAnimators = Array.Empty<Animator>();
        StreamWriter decisionsWriter, trajectoriesWriter, animationsWriter, eventsWriter, trainStatesWriter;
        StationDecisionDataWriter dataWriter;
        StationDecisionCaptureOptions logOptions;
        StationDecisionSkeletonCollector skeletonCollector;
        bool TrajectoriesEnabled => trajectoriesWriter != null || dataWriter != null && logOptions.trajectories;
        bool SkeletonsEnabled => dataWriter != null && logOptions.skeletons;
        bool AnimationsEnabled => animationsWriter != null || dataWriter != null && logOptions.animations;
        bool DecisionsEnabled => decisionsWriter != null || dataWriter != null && logOptions.decisions;
        float nextLogFlush;
        float nextSample, resumeScale = 1f;
        string handledCollectionFailureRunId;
        bool paused;
        bool ownsAvoidancePrediction;
        float previousAvoidancePrediction;
        bool resumeAnimationGraphsPending;
        int resumeAnimationGraphsFrame;

        public float ElapsedSeconds => rail ? (float)rail.ElapsedSimulationSeconds : 0f;
        public bool Paused => paused;
        public float SimulationSpeed => paused ? resumeScale : Time.timeScale;
        public bool IsDaily => rail && rail.schedule && rail.schedule.finiteOperatingDay;
        public int BoardingCount => IsDaily && arrivalController ? arrivalController.BoardedCount : participants.Count(p => p && p.Boarded);
        public int FailedCount => IsDaily && arrivalController ? arrivalController.ExitedCount + arrivalController.UnservedCount : participants.Count(p => p && p.Finished && !p.Boarded);
        public int ActivePassengerCount => participants.Count(p => p && p.gameObject.activeInHierarchy && !p.Finished && (!IsDaily || p.HasEntered));
        public int DecisionCount => IsDaily ? archivedDecisionCount + participants.Where(p => p && p.HasEntered && !p.Finished).Sum(p => p.DecisionCount) : participants.Where(p => p).Sum(p => p.DecisionCount);
        public string LastEvent { get; private set; } = "DÃ©part prÃ©vu; consultez le tableau";
        public string OutputDirectory { get; private set; }
        public StationDecisionRunSession RunSession { get; private set; }
        public StationDecisionCaptureSession CaptureSession { get; private set; }
        public bool IsCollecting => CaptureSession && CaptureSession.IsRecording;
        public bool SimulationSpeedLocked => IsCollecting;
        public string CollectionError { get; private set; } = "";
        public bool CollectionSetupRequired { get; private set; }

        void Awake()
        {
            if (!arrivalController) arrivalController = GetComponent<StationDecisionArrivalController>();
            if (!rail || !rail.schedule) return;
            // Never mutate the saved lab schedule or the source station's ScriptableObject in Play mode.
            if (rail.schedule.finiteOperatingDay && arrivalController && arrivalController.scenario && arrivalController.scenario.randomizeOnLaunch)
            {
                var template = arrivalController.scenario;
                RunSession = StationDecisionRunBootstrap.Create(rail, template, template.useFixedRuntimeSeed,
                    template.fixedRuntimeSeed, template.replayDateIso);
                runtimeSchedule = RunSession.schedule;
                arrivalController.scenario = RunSession.scenario;
                seed = RunSession.seed;
            }
            else
            {
                runtimeSchedule = Instantiate(rail.schedule);
                runtimeSchedule.name = rail.schedule.name + " (runtime only)";
            }
            rail.schedule = runtimeSchedule;
            rail.simulationRate = 1f;
            rail.playOnStart = true;
            displays = FindObjectsByType<StationTimetableDisplay>(FindObjectsSortMode.None)
                .Where(d => d.gameObject.scene == gameObject.scene).ToArray();
            resumeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
        }

        void OnEnable()
        {
            if (!IsDaily || !arrivalController || !arrivalController.scenario || ownsAvoidancePrediction) return;
            previousAvoidancePrediction = NavMesh.avoidancePredictionTime;
            float requested = arrivalController.scenario.avoidancePredictionSeconds;
            NavMesh.avoidancePredictionTime = float.IsNaN(requested) || float.IsInfinity(requested)
                ? 2f : Mathf.Clamp(requested, .5f, 5f);
            ownsAvoidancePrediction = true;
        }

        void RestoreAvoidancePrediction()
        {
            if (!ownsAvoidancePrediction) return;
            NavMesh.avoidancePredictionTime = previousAvoidancePrediction;
            ownsAvoidancePrediction = false;
        }

        void Start()
        {
            if (!rail || !rail.IsReady) { Debug.LogError("DecisionLab requires a valid railway clock.", this); return; }
            if (IsDaily)
            {
                CaptureSession = GetComponent<StationDecisionCaptureSession>();
                if (!CaptureSession) CaptureSession = gameObject.AddComponent<StationDecisionCaptureSession>();
                CaptureSession.Initialize(this);
                if (RunSession != null) CaptureSession.SetRunMetadata(JsonUtility.ToJson(RunSession));
                if (arrivalController && arrivalController.scenario) SetSimulationSpeed(arrivalController.scenario.defaultTimeScale);
                CollectionSetupRequired = collectionSetupOnStart;
                if (CollectionSetupRequired && !paused) TogglePause();
                var collectionPanel = GetComponent<StationDecisionCollectionPanel>();
                if (!collectionPanel) collectionPanel = gameObject.AddComponent<StationDecisionCollectionPanel>();
                collectionPanel.Initialize(this);
            }
            else if (loggingEnabled) OpenLogs();
        }

        void Update()
        {
            if (paused)
            {
                // A motion request can restart or replace an Animator graph while the clock is held.
                // Stop it before Unity's animation phase; keep the Animator enabled for valid requests.
                HoldAnimationGraphs();
                return;
            }
            if (resumeAnimationGraphsPending)
            {
                // timeScale changes only affect a subsequent frame's deltaTime. Playing a
                // Humanoid graph in the resume frame can evaluate foot IK at zero time and
                // permanently fold its legs. Keep the evaluated pose until time advances.
                if (Time.frameCount > resumeAnimationGraphsFrame && Time.deltaTime > 0f && Time.timeScale > 0f)
                    ResumeAnimationGraphs();
                else HoldAnimationGraphs();
            }
            if (!rail || !rail.IsReady) return;
            if (IsDaily && rail.DayComplete)
            {
                LastEvent = arrivalController ? "JournÃ©e terminÃ©e Â· " + arrivalController.ActiveCount + " en gare Â· " + arrivalController.ScheduledCount + " non entrÃ©s" : "JournÃ©e terminÃ©e";
                if (IsCollecting) StopDataCollection("operating_day_finished");
                if (!paused) TogglePause();
                FlushLogs(); return;
            }
            for (int i = 0; i < events.Count; ++i)
            {
                var change = events[i];
                if (change == null || firedEvents.Contains(i) || ElapsedSeconds < change.atSeconds) continue;
                firedEvents.Add(i);
                ApplyEvent(change);
            }
        }

        void LateUpdate()
        {
            try
            {
                // A stopped session owns its asynchronous drain. Detach producers before
                // inspecting its error so Resume cannot enqueue into a closing writer.
                if (dataWriter != null && CaptureSession && !CaptureSession.IsRecording) CloseLogs();
                if (CaptureSession && !CaptureSession.IsRecording && !string.IsNullOrEmpty(CaptureSession.LastError))
                {
                    CollectionError = CaptureSession.LastError;
                    if (handledCollectionFailureRunId != CaptureSession.RunId)
                    {
                        handledCollectionFailureRunId = CaptureSession.RunId;
                        CloseLogs();
                        if (!paused) TogglePause();
                    }
                }
                dataWriter?.ThrowIfFailed();
                if (IsCollecting && Time.realtimeSinceStartup >= nextLogFlush)
                { FlushLogs(); nextLogFlush = Time.realtimeSinceStartup + 1f; }
                if (!paused && (TrajectoriesEnabled || AnimationsEnabled || SkeletonsEnabled && !logOptions.video) && ElapsedSeconds >= nextSample)
                {
                    // Video samples supplement this cadence; they must never postpone the regular ground truth.
                    nextSample = ElapsedSeconds + 1f / Mathf.Max(1f, trajectorySampleRate);
                    WriteAvatarSnapshot("simulation_sample");
                }
            }
            catch (Exception error)
            {
                handledCollectionFailureRunId = CaptureSession ? CaptureSession.RunId : null;
                ReportCollectionWriteError(error);
                if (IsCollecting) StopDataCollection("data_writer_failed"); else CloseLogs();
                if (!paused) TogglePause();
                Debug.LogError("Station data collection stopped: " + error.Message, this);
            }
            if (displays == null) return;
            // Presentation overrides are limited to this copied scene and reflect the same operational data.
            foreach (var display in displays)
            {
                if (!display) continue;
                for (int i = 0; i < display.rows.Length; ++i)
                {
                    var row = display.rows[i];
                    if (row == null || !row.status) continue;
                    var value = display.GetRowValues(i);
                    if (cancelled.Contains(value.serviceId ?? ""))
                    { row.status.text = "ANNULÃ‰"; row.status.color = new Color(1f, .42f, .35f); }
                    else if (delays.TryGetValue(value.serviceId ?? "", out var seconds) &&
                        value.state != StationTrainState.Departing && value.state != StationTrainState.Offstage)
                    { row.status.text = "RETARD +" + Mathf.CeilToInt(seconds) + " s"; row.status.color = new Color(1f, .78f, .18f); }
                }
            }
        }

        /// <summary>Conservative retained-byte budget, before any optional snapshot cache is advanced.</summary>
        public long EstimateVideoSnapshotBytes()
        {
            // Includes clock/roster/event fixed DTOs, command overhead and a margin for
            // the detached event payload. Motion records retain their writer estimates.
            long bytes = StationDecisionDataWriter.BatchOverheadBytes + 16384;
            if (!(TrajectoriesEnabled || AnimationsEnabled || SkeletonsEnabled)) return bytes;
            foreach (var traveller in participants)
            {
                if (!traveller || IsDaily && (!traveller.HasEntered || !traveller.gameObject.activeInHierarchy || traveller.Finished)) continue;
                bytes += 256; // SyncRecord.activeJourneyIds retained-byte accounting.
                if (logOptions != null && logOptions.events)
                    bytes += 24 + (traveller.passengerId?.Length ?? 0) * 2L;
                if (TrajectoriesEnabled) bytes += 4096 + StationDecisionDataWriter.BatchRecordOverheadBytes;
                if (AnimationsEnabled) bytes += 2048 + StationDecisionDataWriter.BatchRecordOverheadBytes;
            }
            if (SkeletonsEnabled) bytes += skeletonCollector.EstimateSnapshotBytes(participants, IsDaily);
            // Same-frame motion dedup can make the actual batch smaller. Reserving the
            // full case avoids changing published rig/binding/clock caches just to plan.
            return bytes;
        }

        /// <summary>Frame-exact ground truth, immediately before the selected CCTV cameras render.</summary>
        public void CaptureDataForVideoFrame()
        {
            if (TrajectoriesEnabled || AnimationsEnabled || SkeletonsEnabled) WriteAvatarSnapshot("video_frame");
        }

        void WriteAvatarSnapshot(string reason)
        {
            // One FIFO command owns this sample's clock, roster and per-agent records.
            // An outer reserved batch can own the whole camera admission transaction.
            if (dataWriter != null && !dataWriter.HasActiveBatch)
            {
                using (var batch = dataWriter.BeginBatch())
                { WriteAvatarSnapshotCore(reason); batch.Commit(); }
            }
            else WriteAvatarSnapshotCore(reason);
        }

        void WriteAvatarSnapshotCore(string reason)
        {
            var stamp = CaptureSession ? CaptureSession.GetStamp() : null;
            long id = stamp != null ? stamp.syncSampleId : 0;
            bool takeSkeleton = SkeletonsEnabled && (!logOptions.video || reason == "video_frame");
            if (lastAvatarSnapshotFrame == Time.frameCount && lastAvatarSnapshotSyncId == id)
            {
                if (CaptureSession && CaptureSession.IsRecording) CaptureSession.RecordAvatarSnapshot(lastAvatarSnapshotIds, reason);
                if (takeSkeleton) WriteSkeletonSnapshot(stamp);
                return;
            }
            ++sampleId;
            var sampled = participants.Where(p => p && (!IsDaily || (p.HasEntered && p.gameObject.activeInHierarchy && !p.Finished))).ToArray();
            lastAvatarSnapshotIds = sampled.Select(p => p.passengerId).ToArray();
            if (CaptureSession && CaptureSession.IsRecording) CaptureSession.RecordAvatarSnapshot(lastAvatarSnapshotIds, reason);
            if (IsDaily) WriteEvent(new EventRecord { time = ElapsedSeconds, kind = "trajectory_sample", sampleId = sampleId,
                activeJourneyIds = lastAvatarSnapshotIds });
            foreach (var traveller in sampled)
            {
                if (TrajectoriesEnabled) WriteTrajectory(traveller);
                if (AnimationsEnabled) WriteAnimation(traveller);
            }
            if (takeSkeleton) WriteSkeletonSnapshot(stamp);
            lastAvatarSnapshotFrame = Time.frameCount; lastAvatarSnapshotSyncId = id;
        }

        void WriteSkeletonSnapshot(StationDecisionCaptureStamp stamp)
        {
            for (int i = 0; i < participants.Length; ++i)
            {
                var traveller = participants[i];
                if (traveller && (!IsDaily || traveller.HasEntered && traveller.gameObject.activeInHierarchy && !traveller.Finished))
                    skeletonCollector.Capture(traveller, i, sampleId, stamp, ElapsedSeconds);
            }
        }

        public StationDecisionPoi GetPlatform(int track) => pois.FirstOrDefault(p => p && p.activity == DecisionActivity.Platform && p.trackNumber == track);

        public bool TryGetCoachAnchor(int track, int coach, out Transform anchor)
        {
            anchor = null;
            var train = rail ? rail.trains.FirstOrDefault(t => t && t.trackNumber == track) : null;
            return train && train.TryGetCoachBoardingAnchor(coach, out anchor);
        }

        public DecisionTripObservation ObserveTrip(string serviceId)
        {
            var observation = new DecisionTripObservation { serviceId = serviceId, status = DecisionTrainStatus.Departed };
            if (!rail || !rail.schedule) return observation;
            int index = Array.FindIndex(rail.schedule.services, s => s != null && s.serviceId == serviceId);
            if (index < 0) return observation;
            var snapshot = rail.GetServiceSnapshot(index);
            var service = snapshot.service;
            observation.trackNumber = service.trackNumber;
            // This experiment binds the initial occurrence. Never roll a missed journey into the next cycle.
            observation.departureSeconds = service.departureOffsetSeconds;
            observation.cutoffSeconds = service.departureOffsetSeconds - Mathf.Max(service.doorDurationSeconds, service.boardingCutoffLeadSeconds);
            observation.train = snapshot.train;
            if (cancelled.Contains(serviceId)) observation.status = DecisionTrainStatus.Cancelled;
            else if (ElapsedSeconds >= observation.departureSeconds) observation.status = DecisionTrainStatus.Departed;
            else if (ElapsedSeconds >= observation.cutoffSeconds) observation.status = DecisionTrainStatus.Closed;
            else if (snapshot.state == StationTrainState.Boarding) observation.status = DecisionTrainStatus.Boarding;
            else observation.status = delays.ContainsKey(serviceId) ? DecisionTrainStatus.Delayed : DecisionTrainStatus.Scheduled;
            return observation;
        }

        public int AdmittedPassengerCount(string serviceId) => admittedPassengers.TryGetValue(serviceId ?? "", out int count) ? count : 0;

        public bool TryReserveBoarding(StationDecisionPassenger traveller, out string reason)
        {
            reason = "";
            if (!traveller || !rail || !rail.schedule) { reason = "Missing boarding references."; return false; }
            if (traveller.IsAlighter || admittedJourneys.Contains(traveller.JourneyId)) { reason = "Journey is not eligible for a new boarding admission."; return false; }
            var actual = ObserveTrip(traveller.serviceId);
            var service = rail.schedule.services.FirstOrDefault(s => s != null && s.serviceId == traveller.serviceId);
            var platform = GetPlatform(actual.trackNumber);
            if (service == null || actual.status != DecisionTrainStatus.Boarding || !actual.train ||
                actual.train.Service == null || actual.train.Service.serviceId != traveller.serviceId ||
                actual.train.DoorOpenFraction <= .95f || ElapsedSeconds >= actual.cutoffSeconds || !platform ||
                traveller.CurrentActivity != DecisionActivity.Platform || traveller.CurrentTargetId != platform.poiId ||
                (traveller.ArrivalPlan == null ? Vector3.Distance(traveller.transform.position, platform.Position) > platform.spreadRadius + 1f :
                    !TryGetCoachAnchor(actual.trackNumber, traveller.ArrivalPlan.coachNumber, out var coachAnchor) ||
                    Vector3.Distance(traveller.transform.position, coachAnchor.position) > 1.5f || !traveller.HasPlatformAccess))
            { reason = "Assigned trip is not open for boarding."; return false; }
            if (traveller.CoachQueue && !traveller.CoachQueue.CanBoard(traveller))
            { reason = "Assigned coach boarding queue has not admitted this journey."; return false; }
            int weight = Mathf.Max(1, traveller.RepresentedPassengers);
            var plan = traveller.ArrivalPlan;
            if (plan != null)
            {
                if (plan.scenarioDate != rail.schedule.operatingDateIso || string.IsNullOrEmpty(plan.ticketId) || plan.coachNumber < 1 ||
                    plan.coachNumber > service.CoachCount || plan.seatNumbers == null || plan.seatNumbers.Length != weight ||
                    plan.seatNumbers.Distinct().Count() != weight || plan.seatNumbers.Any(n => n < 1 || n > service.coachSeatCapacities[plan.coachNumber-1] ||
                        admittedSeats.Contains(service.serviceId + "/" + plan.coachNumber + "/" + n)))
                { reason = "Invalid or already admitted reserved seats."; return false; }
            }
            int used = AdmittedPassengerCount(traveller.serviceId);
            if (used + service.throughPassengers + weight > service.seatedCapacity)
            { reason = "Train seated capacity exhausted."; LogEvent("capacity_denied", traveller.serviceId, reason, traveller.passengerId); return false; }
            admittedPassengers[traveller.serviceId] = used + weight;
            admittedJourneys.Add(traveller.JourneyId);
            if (plan != null) foreach (int seat in plan.seatNumbers) admittedSeats.Add(service.serviceId + "/" + plan.coachNumber + "/" + seat);
            return true;
        }

        public void SetSimulationSpeed(float scale)
        {
            resumeScale = SimulationSpeedLocked ? 1f : Mathf.Clamp(scale, 1f, 8f);
            if (!paused) Time.timeScale = resumeScale;
        }

        public bool BeginDataCollection(StationDecisionCaptureOptions options)
        {
            CollectionError = "";
            if (!CaptureSession || !rail || !rail.IsReady || IsCollecting || CaptureSession.IsFinalizing)
            { CollectionError = "La collecte n'est pas prête, est déjà active ou termine ses fichiers."; return false; }
            if (IsDaily && rail.DayComplete)
            { CollectionError = "La journée est terminée. Recommencez la simulation pour créer une nouvelle journée."; return false; }
            float previousSpeed = SimulationSpeed;
            SetSimulationSpeed(1f);
            if (!CaptureSession.StartCapture(options))
            { SetSimulationSpeed(previousSpeed); CollectionError = CaptureSession.LastError; return false; }
            try
            {
                OpenLogsAt(CaptureSession.OutputDirectory, CaptureSession.Options);
                handledCollectionFailureRunId = null;
                CollectionSetupRequired = false;
                if (paused) TogglePause();
                CaptureSession.RecordMarker("simulation_collection_started", "Shared simulation clock resumed at x1; acceleration is locked until collection stops.");
                return true;
            }
            catch (Exception error)
            {
                CollectionError = error.Message;
                handledCollectionFailureRunId = CaptureSession.RunId;
                try { CloseLogs(); } catch (Exception closeError) { ReportCollectionWriteError(closeError); }
                ReportCollectionWriteError(error); CaptureSession.StopCapture("data_writer_setup_failed");
                if (!paused) TogglePause();
                SetSimulationSpeed(previousSpeed);
                Debug.LogError("Station data collection failed: " + error.Message, this);
                return false;
            }
        }

        public void StopDataCollection(string reason = "user_stopped")
        {
            if (!IsCollecting) { CloseLogs(); return; }
            if (!paused) TogglePause();
            try { LogEvent("run_ended", null, reason); }
            catch (Exception error) { ReportCollectionWriteError(error); }
            try { CloseLogs(); }
            catch (Exception error) { ReportCollectionWriteError(error); }
            finally { CaptureSession.StopCapture(reason); }
            if (!string.IsNullOrEmpty(CaptureSession.LastError))
            { CollectionError = CaptureSession.LastError; handledCollectionFailureRunId = CaptureSession.RunId; }
        }

        void ReportCollectionWriteError(Exception error)
        {
            CollectionError = error.Message;
            // Even if the synchronization file is unavailable, ReportError records failure before attempting its marker.
            try { if (CaptureSession) CaptureSession.ReportError(error.ToString()); } catch (Exception) { }
        }

        void AcknowledgeStoppedCollection(bool resumeRequested)
        {
            if (!CaptureSession || CaptureSession.IsRecording) return;
            if (dataWriter != null) CloseLogs();
            // An explicit Resume can occur in the same frame as Session.LateUpdate failure,
            // before this world's next LateUpdate has consumed it. Do not re-pause that request.
            if (resumeRequested && !string.IsNullOrEmpty(CaptureSession.LastError))
            { CollectionError = CaptureSession.LastError; handledCollectionFailureRunId = CaptureSession.RunId; }
        }

        public void BeginObservation()
        {
            AcknowledgeStoppedCollection(true);
            CollectionSetupRequired = false;
            if (paused) TogglePause();
        }

        public void TogglePause()
        {
            AcknowledgeStoppedCollection(paused);
            paused = !paused;
            if (paused)
            {
                resumeAnimationGraphsPending = false;
                resumeScale = SimulationSpeedLocked ? 1f : Mathf.Max(.01f, Time.timeScale);
                pausedAnimators = FindObjectsByType<Animator>(FindObjectsInactive.Include)
                    .Where(a => a.gameObject.scene == gameObject.scene && a.avatar && a.avatar.isHuman).ToArray();
                // A zero-time Humanoid IK evaluation can corrupt the retained leg pose.
                // Hold graph evaluation before changing the shared clock, preserving the evaluated pose.
                HoldAnimationGraphs();
                Time.timeScale = 0f;
                if (rail) rail.Pause();
            }
            else
            {
                if (SimulationSpeedLocked) resumeScale = 1f;
                Time.timeScale = resumeScale;
                if (rail) rail.Resume();
                resumeAnimationGraphsPending = true;
                resumeAnimationGraphsFrame = Time.frameCount;
            }
            LogEvent("pause", null, paused ? "paused" : "resumed");
        }

        void HoldAnimationGraphs()
        {
            foreach (var animator in pausedAnimators)
            {
                if (!animator) continue;
                var graph = animator.playableGraph;
                if (!graph.IsValid() || !graph.IsPlaying()) continue;
                heldAnimationGraphs.Add(graph);
                graph.Stop();
            }
        }

        void ResumeAnimationGraphs()
        {
            resumeAnimationGraphsPending = false;
            foreach (var graph in heldAnimationGraphs)
                if (graph.IsValid() && !graph.IsPlaying()) graph.Play();
            heldAnimationGraphs.Clear();
            pausedAnimators = Array.Empty<Animator>();
        }

        public void RestartScene()
        {
            if (IsCollecting) StopDataCollection("scene_restarted");
            FlushLogs();
            ResumeAnimationGraphs();
            Time.timeScale = paused ? resumeScale : Mathf.Max(.01f, Time.timeScale);
            SceneManager.LoadScene(gameObject.scene.name);
        }

        public void InjectDelay()
        {
            var service = rail && rail.schedule ? rail.schedule.services.FirstOrDefault(s => s != null &&
                !cancelled.Contains(s.serviceId) && ElapsedSeconds < s.departureOffsetSeconds - s.doorDurationSeconds) : null;
            if (service != null) ApplyEvent(new StationDecisionScenarioEvent { serviceId = service.serviceId, kind = DecisionScenarioEventKind.Delay, delaySeconds = 75f });
        }

        public void InjectCancellation()
        {
            var service = rail && rail.schedule ? rail.schedule.services.FirstOrDefault(s => s != null &&
                !cancelled.Contains(s.serviceId) && ElapsedSeconds < s.departureOffsetSeconds - s.doorDurationSeconds &&
                participants.Any(p => p && !p.Finished && p.serviceId == s.serviceId)) : null;
            if (service != null) ApplyEvent(new StationDecisionScenarioEvent { serviceId = service.serviceId, kind = DecisionScenarioEventKind.Cancellation });
        }

        public void ApplyEvent(StationDecisionScenarioEvent change)
        {
            if (!rail || !runtimeSchedule || change == null) return;
            var service = runtimeSchedule.services.FirstOrDefault(s => s != null && s.serviceId == change.serviceId);
            if (service == null) return;
            string[] affected = { service.serviceId };
            if (change.kind == DecisionScenarioEventKind.Delay)
            {
                if (cancelled.Contains(service.serviceId) || ElapsedSeconds >= service.departureOffsetSeconds - service.doorDurationSeconds) return;
                float delay = Mathf.Clamp(change.delaySeconds, 1f, 900f);
                if (IsDaily)
                {
                    var previous = runtimeSchedule.services.ToDictionary(s => s.serviceId, s => s.departureOffsetSeconds);
                    if (!rail.TryDelayService(service.serviceId, delay, out affected, out string error))
                    { LastEvent = "Retard refusÃ© : " + error; LogEvent("dispatch_change_rejected", service.serviceId, error); return; }
                    foreach (string id in affected)
                    {
                        var changed = runtimeSchedule.services.First(s => s.serviceId == id);
                        delays.TryGetValue(id, out float prior);
                        delays[id] = prior + changed.departureOffsetSeconds - previous[id];
                    }
                }
                else
                {
                    service.departureOffsetSeconds += delay;
                    delays.TryGetValue(service.serviceId, out float previous);
                    delays[service.serviceId] = previous + delay;
                }
                LastEvent = service.trainNumber + " : retard +" + delay.ToString("0") + " s";
            }
            else
            {
                if (ElapsedSeconds >= service.departureOffsetSeconds - service.doorDurationSeconds) return;
                if (!cancelled.Add(service.serviceId)) return;
                // The cancelled train leaves empty; passengers can no longer board its fixed trip occurrence.
                if (!IsDaily) service.departureOffsetSeconds = Mathf.Max(ElapsedSeconds + service.doorDurationSeconds + 20f,
                    service.arrivalOffsetSeconds + 2f * service.doorDurationSeconds);
                LastEvent = service.trainNumber + " : train annulÃ©, accueil / sortie";
            }
            rail.SetSimulationTime(rail.ElapsedSimulationSeconds);
            LogEvent(change.kind.ToString(), change.serviceId, LastEvent);
            if (change.announceImmediately)
                foreach (var traveller in participants) if (traveller && !traveller.Finished && (!IsDaily || traveller.HasEntered) && affected.Contains(traveller.serviceId))
                    traveller.ReceiveAnnouncement(ObserveTrip(traveller.serviceId), change.kind.ToString());
            if (paused) HoldAnimationGraphs();
        }

        void OpenLogs()
        {
            try
            {
                string run = "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                OpenLogsAt(Path.GetFullPath(Path.Combine(outputRoot, run)), null);
            }
            catch (Exception error) { CloseLogs(); Debug.LogError("DecisionLab logging failed: " + error.Message, this); }
        }

        void OpenLogsAt(string directory, StationDecisionCaptureOptions options)
        {
            OutputDirectory = directory;
            Directory.CreateDirectory(OutputDirectory);
            logOptions = options == null ? null : options.Copy();
            dataWriter = options == null ? null : CaptureSession.DataWriter;
            // Original non-daily experiments retain their historical layout.
            decisionsWriter = options == null ? OpenWriter("decisions.jsonl", true) : null;
            trajectoriesWriter = options == null ? OpenWriter("trajectories.jsonl") : null;
            animationsWriter = null;
            eventsWriter = options == null ? OpenWriter("events.jsonl", true) : null;
            var description = CreateManifest();
            if (options == null) File.WriteAllText(Path.Combine(OutputDirectory, "manifest.json"), JsonUtility.ToJson(description, true), new UTF8Encoding(false));
            else
            {
                // Runtime services/needs/settings remain mutable. Freeze the startup graph once, not on every video frame.
                description = Detached(description, out long descriptionBytes);
                if (options.profiles || options.trajectories || options.animations || options.skeletons || options.decisions || options.events)
                {
                    dataWriter.PrepareAgents(Mathf.Clamp(participants.Length * 4, 32, 512),
                        (description.profiles ?? Array.Empty<Profile>()).Select(profile => profile.agentId).ToArray());
                    if (options.profiles)
                    {
                        var journeys = (description.scheduledJourneys ?? Array.Empty<DecisionArrivalPlan>()).ToDictionary(j => j.journeyId, StringComparer.Ordinal);
                        int profilesQueued = 0;
                        foreach (var profile in description.profiles ?? Array.Empty<Profile>())
                        {
                            journeys.TryGetValue(profile.agentId, out var journey);
                            // These profiles/plans belong to the detached startup graph and are never mutated again.
                            dataWriter.WriteAgent(profile.agentId, "profile.json", new AgentProfileFile {
                                captureStamp = CaptureSession.GetStamp(), profile = profile, journey = journey
                            }, true, 4096 + (profile.agentId?.Length ?? 0) * 2L + (profile.seatNumbers?.Length ?? 0) * 8L);
                            // Bound startup admission as well: no full-day profile burst is retained behind slow file creation.
                            if (++profilesQueued % 256 == 0) CaptureSession.CompleteDataInitialization(false);
                        }
                    }
                }
                if (options.skeletons) { dataWriter.OpenRoot("skeleton-definitions.jsonl"); skeletonCollector = new StationDecisionSkeletonCollector(dataWriter, participants); }
                description.schema = "stationflow.station_decision_day.v6";
                if (!options.animations) description.animationCatalog = null;
                description.profiles = null; description.scheduledJourneys = null;
                if (!options.timetable) description.services = null;
                // Account only for the shared scenario that will actually be queued, after individual profiles/plans were removed.
                description = Detached(description, out descriptionBytes);
                dataWriter.WriteRoot("scenario.json", description, true, descriptionBytes);
                if (options.events) dataWriter.OpenRoot("events.jsonl");
                if (options.timetable)
                {
                    var timetable = Detached(new TimetableFile
                    { captureStamp = CaptureSession.GetStamp(), operatingDate = runtimeSchedule.operatingDateIso,
                        startClockSeconds = runtimeSchedule.StartClockSeconds, minimumTrackClearanceSeconds = runtimeSchedule.minimumTrackClearanceSeconds,
                        services = description.services }, out long timetableBytes);
                    dataWriter.WriteRoot("timetable.json", timetable, true, timetableBytes);
                    dataWriter.OpenRoot("train-states.jsonl");
                    rail.trainStateChanged.AddListener(RecordTrainState);
                    foreach (var train in rail.trains) if (train) RecordTrainState(train);
                }
            }
            nextSample = ElapsedSeconds; nextLogFlush = Time.realtimeSinceStartup + 1f;
            sampleId = 0; lastAvatarSnapshotFrame = -1; lastAvatarSnapshotSyncId = -1;
            LogEvent("run_started", null, scenarioName);
            // Thousands of profile files can take seconds to create. Finish this existing Start preparation
            // before simulation/video admission, rather than accumulating live snapshots behind startup I/O.
            if (dataWriter != null) CaptureSession.CompleteDataInitialization();
        }

        T Detached<T>(T record, out long retainedBytes)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return StationDecisionCaptureSnapshot.Copy(record, out retainedBytes); }
            finally { CaptureSession?.RecordDataSnapshotCost(started); }
        }

        void WriteAgentLine(string agentId, string stream, object record, StreamWriter legacy, bool detach = false, long retainedBytes = 4096)
        {
            if (dataWriter != null)
            {
                if (CaptureSession && !CaptureSession.IsRecording) return;
                if (detach) record = Detached(record, out retainedBytes);
                dataWriter.WriteAgent(agentId, stream, record, retainedBytes: retainedBytes);
            }
            else legacy?.WriteLine(JsonUtility.ToJson(record));
        }

        bool TrainStatesEnabled => trainStatesWriter != null || dataWriter != null && logOptions.timetable;
        void WriteRootLine(string name, object record, StreamWriter legacy, bool detach = false, long retainedBytes = 4096)
        {
            if (dataWriter != null)
            {
                if (CaptureSession && !CaptureSession.IsRecording) return;
                if (detach) record = Detached(record, out retainedBytes);
                dataWriter.WriteRoot(name, record, retainedBytes: retainedBytes);
            }
            else legacy?.WriteLine(JsonUtility.ToJson(record));
        }

        StreamWriter OpenWriter(string name, bool autoFlush = false) => new StreamWriter(Path.Combine(OutputDirectory, name), false, new UTF8Encoding(false)) { AutoFlush = autoFlush };

        Manifest CreateManifest() => new Manifest
                { launch = RunSession, mobilityModelVersion = DecisionMobilityProfiles.ModelVersion,
                    animationCatalog = participants.Where(p => p && p.motion && p.motion.animationPlayer && p.motion.animationPlayer.Library)
                        .Select(p => p.motion.animationPlayer.Library).Distinct().SelectMany(l => l.motions.Where(m => m != null).Select(m => new AnimationMotionProfile
                        { library = l.name, motionId = m.id, statePath = m.StatePath, stateHash = Animator.StringToHash(m.StatePath),
                            clipName = m.clip ? m.clip.name : "", clipLengthSeconds = m.clip ? m.clip.length : 0f,
                            loop = m.loop, blendSeconds = m.blendIn, source = m.source.ToString(),
                            blendParameter = m.blendParameter,
                            blendChildren = m.blendChildren == null ? null : m.blendChildren.Select(c => new AnimationBlendChildProfile
                            { clipName = c.clip ? c.clip.name : "", clipLengthSeconds = c.clip ? c.clip.length : 0f, threshold = c.threshold }).ToArray() })).ToArray(),
                    schema = IsDaily ? "stationflow.station_decision_day.v2" : "stationflow.station_decision_lab.v1", scenario = scenarioName, scene = gameObject.scene.path,
                    seed = seed, createdUtc = DateTime.UtcNow.ToString("o"), unityVersion = Application.unityVersion,
                    participantCount = IsDaily && arrivalController ? arrivalController.GeneratedPlan.Count : participants.Length,
                    actorSlotCount = participants.Length, operatingDate = runtimeSchedule.operatingDateIso,
                    startClockSeconds = runtimeSchedule.StartClockSeconds, finiteOperatingDay = IsDaily,
                    minimumTrackClearanceSeconds = runtimeSchedule.minimumTrackClearanceSeconds,
                    arrivalSettings = ArrivalSettingsForManifest(),
                    scheduledJourneys = IsDaily && arrivalController ? arrivalController.GeneratedPlan.ToArray() : null,
                    sampleRate = trajectorySampleRate, railStateCaptureMode = "initial-and-state-changes",
                    policy = "conditional-semi-markov-v2", boarding = IsDaily ? "ticket-gate-and-assigned-coach-anchor" : "virtual-safe-platform-anchor",
                    controlledPlatformAccess = IsDaily && platformGates.Any(g => g),
                    physicalArrivalQueues = IsDaily && platformGates.Any(g => g),
                    nativeGateLinkTelemetry = IsDaily && platformGates.Any(g => g),
                    navigationOwnership = "Unity NavMeshAgent pathfinding, autoRepath and obstacle avoidance",
                    avoidancePredictionSeconds = NavMesh.avoidancePredictionTime,
                    entrancePortals = IsDaily && arrivalController ? arrivalController.portals.Where(p => p.trackNumber == 0).Select(p => new EntranceProfile
                    { entranceId = p.entranceId, spawnPoints = p.spawnPoints.Where(t => t).Select(t => t.position).ToArray(),
                        weight = arrivalController.entranceWeights != null && arrivalController.entranceWeights.Length > 0 ?
                            arrivalController.entranceWeights.FirstOrDefault(e => e != null && e.entranceId == p.entranceId)?.weight ?? 0d : 1d }).ToArray() : null,
                    platformControls = platformGates.Where(g => g).Select(g => new GateProfile
                    { track = g.trackNumber, ingressArea = g.ingressArea, scanSecondsPerActor = g.scanSecondsPerActor,
                        arrivalTolerance = g.ArrivalTolerance, minimumQueueSeparation = g.MinimumQueueSeparation,
                        queueAnchors = g.queueAnchors.Select(a => a.position).ToArray(),
                        holdingAnchors = g.holdingAnchors.Select(a => a.position).ToArray(),
                        center = g.transform.position, hall = g.hallCrossingPoint.position, platform = g.platformCrossingPoint.position,
                        ingressStart = g.ingressLink.startTransform.position, ingressEnd = g.ingressLink.endTransform.position,
                        egressStart = g.egressLink.startTransform.position, egressEnd = g.egressLink.endTransform.position,
                        ingressWidth = g.ingressLink.width, egressWidth = g.egressLink.width,
                        queueCapacity = g.queueAnchors.Length }).ToArray(),
                    coachAnchors = IsDaily ? rail.trains.SelectMany(t => t.coachBoardingAnchors.Select((a, i) => new CoachProfile
                    { track = t.trackNumber, coachNumber = i + 1, position = a.position })).ToArray() : null,
                    coachControls = coachQueues.Where(q => q).Select(q => new CoachControlProfile
                    { track = q.trackNumber, coachNumber = q.coachNumber, boardingSecondsPerPassenger = q.boardingSecondsPerPassenger,
                        head = q.queueAnchors[0].position, queueCapacity = q.queueAnchors.Length,
                        arrivalTolerance = q.ArrivalTolerance, minimumQueueSeparation = q.MinimumQueueSeparation,
                        queueAnchors = q.queueAnchors.Select(a => a.position).ToArray() }).ToArray(),
                    toiletControls = pois.Where(p => p && p.toiletQueue).Select(p => p.toiletQueue).Distinct().Select(q => new ToiletControlProfile
                    { poiId = q.poi.poiId, capacity = q.Capacity, queueCapacity = q.queueSlots.Length,
                        arrivalTolerance = q.ArrivalTolerance, minimumQueueSeparation = q.MinimumQueueSeparation,
                        minimumUseSeconds = 120f, maximumUseSeconds = 240f,
                        queueSlots = q.queueSlots.Select(a => a.position).ToArray(),
                        usePoints = q.usePoints.Select(a => a.position).ToArray(), exitPoint = q.exitPoint.position }).ToArray(),
                    congestionAllowance = congestionAllowance, scenarioEvents = events.ToArray(),
                    services = runtimeSchedule.services, pois = pois.Where(p => p).Select(p => new PoiProfile
                    { poiId = p.poiId, activity = p.activity.ToString(), position = p.Position, track = p.trackNumber,
                        capacity = p.capacity, mean = p.meanDurationSeconds, spread = p.durationSpreadSeconds,
                        serviceDuration = p.service ? p.service.conversationDuration : 0f, spreadRadius = p.spreadRadius,
                        standingPoints = (p.standingPoints ?? Array.Empty<Transform>()).Where(t => t).Select(t => t.position).ToArray(),
                        facingTarget = p.facingTarget ? p.facingTarget.position : p.Position,
                        seatedActivity = p.seatedActivity.ToString(), seatedMean = p.seatedMeanDurationSeconds,
                        seatedSpread = p.seatedDurationSpreadSeconds,
                        departurePosition = p.departurePoint ? p.departurePoint.position : p.Position,
                        seats = (p.seats ?? Array.Empty<StationFlow.TrainStation.Social.StationSeat>()).Where(s => s).Select(s => new SeatProfile
                        { seatId = s.seatId, approach = s.ApproachPosition,
                            position = s.seatedPose ? s.seatedPose.position : s.transform.position,
                            rotation = s.seatedPose ? s.seatedPose.eulerAngles : s.transform.eulerAngles }).ToArray() }).ToArray(),
                    profiles = IsDaily && arrivalController ? arrivalController.GeneratedPlan.Select(ProfileForPlan).ToArray() : participants.Where(p => p).Select(p => new Profile
                    { agentId = p.passengerId, serviceId = p.serviceId, occurrence = 0, departure = p.departureOffsetSeconds,
                        familiarity = p.familiarity, needs = p.needs, safetyMargin = p.safetyMarginSeconds,
                        walkSpeed = p.walkSpeed, fastWalkSpeed = p.fastWalkSpeed, startDelay = p.startDelaySeconds,
                        platformKnownInitially = p.platformKnownInitially, position = p.transform.position,
                        policyParameters = p.policyParameters }).ToArray() };

        Profile ProfileForPlan(DecisionArrivalPlan plan)
        {
            var portal = arrivalController.portals.FirstOrDefault(p => p != null && p.entranceId == plan.entranceId);
            int marker = portal != null && plan.purpose == DecisionJourneyPurpose.Alighting ? (plan.coachNumber - 1) * portal.spawnPointsPerCoach : 0;
            Vector3 point = portal != null && portal.spawnPoints != null && marker >= 0 && marker < portal.spawnPoints.Length && portal.spawnPoints[marker] ? portal.spawnPoints[marker].position : Vector3.zero;
            return new Profile { agentId = plan.journeyId, serviceId = plan.serviceId, occurrence = 0,
                departure = plan.publishedDepartureSeconds, familiarity = plan.familiarity, needs = plan.needs,
                safetyMargin = plan.safetyMarginSeconds, walkSpeed = plan.walkSpeed, fastWalkSpeed = plan.fastWalkSpeed,
                startDelay = plan.plannedEntrySeconds, position = point, groupId = plan.groupId, entranceId = plan.entranceId,
                purpose = plan.purpose.ToString(), arrivalProfile = plan.arrivalProfile.ToString(), representedPassengers = plan.representedPassengers,
                plannedEntrySeconds = plan.plannedEntrySeconds, publishedDepartureSeconds = plan.publishedDepartureSeconds,
                trackNumber = plan.trackNumber, ticketId = plan.ticketId, coachNumber = plan.coachNumber, seatNumbers = plan.seatNumbers,
                leadSeconds = plan.leadSeconds, groupSize = plan.groupSize,
                groupMemberIndex = plan.groupMemberIndex, clippedAtSimulationStart = plan.clippedAtSimulationStart,
                alightingServiceVisitor = plan.alightingServiceVisitor, alightingServiceVisitLimit = plan.alightingServiceVisitLimit,
                alightingStopoverSeconds = plan.alightingStopoverSeconds, mobilityProfile = plan.mobilityProfile };
        }

        DecisionArrivalSettings ArrivalSettingsForManifest()
        {
            if (!IsDaily || !arrivalController || !arrivalController.scenario) return null;
            var settings = arrivalController.scenario.PlannerSettings();
            settings.scenarioDate = runtimeSchedule.operatingDateIso;
            settings.entranceWeights = arrivalController.entranceWeights;
            return settings;
        }

        public void RecordDecision(StationDecisionPassenger traveller, DecisionContext context, List<DecisionEvaluation> evaluations, DecisionEvaluation chosen, string trigger)
        {
            if (!DecisionsEnabled) return;
            var known = traveller.KnownTrip;
            WriteAgentLine(traveller.passengerId, "decisions.jsonl", new DecisionRecord
            { captureStamp = CaptureSession ? CaptureSession.GetStamp() : null, time = ElapsedSeconds, agentId = traveller.passengerId, decisionIndex = traveller.DecisionCount,
                actorSlotId = IsDaily ? "slot-" + Array.IndexOf(participants, traveller).ToString("000") : "",
                trigger = trigger, selectedActivity = chosen == null ? "None" : chosen.option.activity.ToString(),
                selectedPoi = chosen == null ? "" : chosen.option.poiId, reason = chosen == null ? "no feasible action" : chosen.reason,
                observedCutoff = (float)context.boardingCutoffSeconds, observedStatus = context.trainStatus.ToString(),
                previousActivity = context.previousActivity.ToString(), context = context, platformKnown = traveller.PlatformKnown,
                inputOptions = evaluations.Select(e => e.option).ToArray(),
                needs = traveller.needs, options = evaluations.Select(e => new OptionRecord
                { activity = e.option.activity.ToString(), poiId = e.option.poiId, probability = e.probability,
                    slack = double.IsInfinity(e.slackSeconds) || double.IsNaN(e.slackSeconds) ? -99999 : e.slackSeconds,
                    travel = e.option.travelSeconds, queue = e.option.queueSeconds, dwell = e.option.durationSeconds,
                    seatTravel = e.option.seatTravelSeconds, seatingTransition = e.option.seatingTransitionSeconds,
                    seatedDwell = e.option.seatedDurationSeconds,
                    toPlatform = e.option.toPlatformSeconds, toExit = e.option.toExitSeconds, reason = e.reason }).ToArray() }, decisionsWriter, detach: true);
        }

        public void LogEvent(string kind, string serviceId, string detail, string agentId = null)
        {
            var actual = serviceId == null ? default : ObserveTrip(serviceId);
            var traveller = IsDaily && agentId != null ? participants.FirstOrDefault(p => p && p.passengerId == agentId && p.HasEntered) : null;
            var plan = IsDaily && arrivalController && agentId != null ? arrivalController.GeneratedPlan.FirstOrDefault(p => p.journeyId == agentId) : null;
            if (kind == "journey_entered" && agentId != null) enteredAt[agentId] = ElapsedSeconds;
            if (kind == "journey_finished" && traveller) archivedDecisionCount += traveller.DecisionCount;
            var service = serviceId == null ? null : runtimeSchedule.services.FirstOrDefault(s => s != null && s.serviceId == serviceId);
            var joinedGate = kind == "platform_queue_joined" && traveller ? traveller.PlatformGate : null;
            var joinedCoach = kind == "coach_queue_joined" && traveller ? traveller.CoachQueue : null;
            var joinedToilet = kind == "toilet_queue_joined" && traveller ? traveller.ToiletQueue : null;
            var toilet = traveller && traveller.CurrentActivity == DecisionActivity.Toilet ? traveller.ToiletQueue : null;
            var holdingGate = traveller ? traveller.PlatformGate : null;
            int holdingIndex = holdingGate ? holdingGate.HoldingSlotIndex(agentId) : -1;
            WriteEvent(new EventRecord
            { time = ElapsedSeconds, kind = kind, serviceId = serviceId, detail = detail, agentId = agentId,
                actualCutoff = serviceId == null ? -1f : actual.cutoffSeconds,
                actualDeparture = serviceId == null ? -1f : actual.departureSeconds,
                actualStatus = serviceId == null ? "" : actual.status.ToString(),
                actorSlotId = traveller ? "slot-" + Array.IndexOf(participants, traveller).ToString("000") : "",
                representedPassengers = plan != null ? plan.representedPassengers : 0,
                groupId = plan != null ? plan.groupId : "", entranceId = plan != null ? plan.entranceId : "",
                purpose = plan != null ? plan.purpose.ToString() : "",
                profile = plan != null && (!CaptureSession || !CaptureSession.IsRecording || CaptureSession.Options.profiles) ? ProfileForPlan(plan) : null,
                position = traveller ? traveller.transform.position : Vector3.zero,
                queueArrivalPosition = joinedGate ? joinedGate.QueuePosition(traveller) : joinedCoach ? joinedCoach.PositionFor(traveller) : joinedToilet ? joinedToilet.PositionFor(traveller) : Vector3.zero,
                queueSlotIndex = joinedGate ? joinedGate.QueueSlotIndex(agentId) : joinedCoach ? joinedCoach.QueueSlotIndex(agentId) : joinedToilet ? joinedToilet.QueueSlotIndex(agentId) : -1,
                queuePredecessorJourneyId = joinedGate ? joinedGate.PredecessorJourneyId(agentId) : joinedCoach ? joinedCoach.PredecessorJourneyId(agentId) : joinedToilet ? joinedToilet.PredecessorJourneyId(agentId) : "",
                queuePredecessorPosition = joinedGate ? joinedGate.PredecessorPosition(agentId) : joinedCoach ? joinedCoach.PredecessorPosition(agentId) : joinedToilet ? joinedToilet.PredecessorPosition(agentId) : Vector3.zero,
                toiletCapacity = toilet ? toilet.Capacity : 0, toiletUseCount = toilet ? toilet.ActiveUseCount : 0,
                toiletQueueCount = toilet ? toilet.Count : 0, toiletUseSlotIndex = toilet ? toilet.UseSlotIndex(agentId) : -1,
                toiletUseDurationSeconds = toilet ? toilet.UseDurationSeconds(agentId) : 0f,
                toiletUseStartedAtSeconds = toilet ? toilet.UseStartedAtSeconds(agentId) : -1f,
                holdingTrack = holdingGate ? holdingGate.trackNumber : 0, holdingSlotIndex = holdingIndex,
                holdingLeaseCount = holdingGate ? holdingGate.HoldingLeaseCount : 0,
                holdingPosition = holdingGate && holdingIndex >= 0 && holdingIndex < holdingGate.holdingAnchors.Length ?
                    holdingGate.holdingAnchors[holdingIndex].position : Vector3.zero,
                boarded = traveller && traveller.Boarded, finished = traveller && traveller.Finished,
                outcome = traveller ? traveller.ExecutionState : "", capacity = service != null ? service.seatedCapacity : 0,
                occupancyAfter = service == null ? 0 : service.throughPassengers + AdmittedPassengerCount(serviceId),
                ticketId = plan != null ? plan.ticketId : "", coachNumber = plan != null ? plan.coachNumber : 0,
                seatNumbers = plan != null ? plan.seatNumbers : null,
                platformAccessGranted = traveller && traveller.PlatformGate && traveller.PlatformGate.CanProceed(traveller),
                platformAccessPassed = traveller && traveller.PlatformGate && traveller.PlatformGate.HasPassed(agentId),
                coachQueueValidated = traveller && traveller.BoardingQueueValidated,
                trainPresent = actual.train && actual.train.serviceId == serviceId && actual.train.berthPoint &&
                    Vector3.Distance(actual.train.transform.position, actual.train.berthPoint.position) < .1f &&
                    (actual.train.State == StationTrainState.DoorsOpening || actual.train.State == StationTrainState.Boarding),
                platformOpensAt = service != null ? actual.departureSeconds - service.platformAccessLeadSeconds : -1f });
        }

        void WriteEvent(EventRecord record)
        {
            record.captureStamp = CaptureSession ? CaptureSession.GetStamp() : null;
            if (dataWriter != null && logOptions.events && !string.IsNullOrEmpty(record.agentId))
                WriteAgentLine(record.agentId, "events.jsonl", record, null, detach: true);
            else if (eventsWriter != null || dataWriter != null && logOptions.events) WriteRootLine("events.jsonl", record, eventsWriter, detach: true);
            if (TrainStatesEnabled && (record.kind == "Delay" || record.kind == "Cancellation"))
            {
                var service = runtimeSchedule.services.FirstOrDefault(s => s != null && s.serviceId == record.serviceId);
                WriteRootLine("train-states.jsonl", new TimetableChangeRecord
                { captureStamp = record.captureStamp, time = ElapsedSeconds, kind = record.kind, serviceId = record.serviceId,
                    cancelled = record.kind == "Cancellation", arrivalSeconds = service != null ? service.arrivalOffsetSeconds : 0f,
                    departureSeconds = service != null ? service.departureOffsetSeconds : 0f, detail = record.detail }, trainStatesWriter);
            }
        }

        void WriteTrajectory(StationDecisionPassenger traveller)
        {
            var agent = traveller.motion ? traveller.motion.agent : null;
            bool onLink = agent && agent.enabled && agent.isOnNavMesh && agent.isOnOffMeshLink;
            var nativeLink = onLink ? agent.currentOffMeshLinkData : default;
            var ownedGate = onLink ? platformGates.FirstOrDefault(g => g &&
                (nativeLink.owner == g.ingressLink || nativeLink.owner == g.egressLink)) : null;
            WriteAgentLine(traveller.passengerId, "trajectories.jsonl", new TrajectoryRecord
            { time = ElapsedSeconds, agentId = traveller.passengerId, serviceId = traveller.serviceId, occurrence = 0,
                sampleId = sampleId, actorSlotId = IsDaily ? "slot-" + Array.IndexOf(participants, traveller).ToString("000") : "",
                representedPassengers = IsDaily ? traveller.RepresentedPassengers : 1,
                groupId = IsDaily ? traveller.GroupId : "", purpose = IsDaily ? (traveller.IsAlighter ? "Alighting" : "Boarding") : "",
                captureStamp = CaptureSession ? CaptureSession.GetStamp() : null,
                position = traveller.transform.position, rotation = traveller.transform.rotation,
                velocity = agent && agent.enabled && agent.isOnNavMesh ? agent.velocity : Vector3.zero,
                hasNavigationTarget = agent && agent.enabled && agent.isOnNavMesh && agent.hasPath,
                navigationTarget = agent && agent.enabled && agent.isOnNavMesh && agent.hasPath ? agent.destination : Vector3.zero,
                targetPoiPosition = pois.FirstOrDefault(p => p && p.poiId == traveller.CurrentTargetId)?.Position ?? Vector3.zero,
                effectiveWalkSpeed = traveller.walkSpeed, effectiveFastWalkSpeed = traveller.fastWalkSpeed,
                usingGroupPace = traveller.UsingGroupPace,
                alightingStopoverDeadlineSeconds = traveller.AlightingStopoverDeadlineSeconds,
                alightingServiceVisitsCompleted = traveller.AlightingServiceVisitsCompleted,
                activity = traveller.CurrentActivity.ToString(), execution = traveller.ExecutionState, targetPoi = traveller.CurrentTargetId,
                budget = traveller.BudgetSeconds, knownStatus = traveller.KnownTrip.status.ToString(),
                seatId = traveller.CurrentSeatId, seatedActivity = traveller.SeatedActivity.ToString(),
                isSeated = traveller.IsSeated, seatedSecondsRemaining = traveller.SeatedSecondsRemaining,
                finished = traveller.Finished, boarded = traveller.Boarded, visibleActor = traveller.gameObject.activeInHierarchy,
                coachNumber = traveller.ArrivalPlan != null ? traveller.ArrivalPlan.coachNumber : 0,
                platformAccessGranted = traveller.PlatformGate && traveller.PlatformGate.CanProceed(traveller),
                platformAccessPassed = traveller.PlatformGate && traveller.PlatformGate.HasPassed(traveller.JourneyId),
                isOnNativeLink = ownedGate, nativeLinkTrack = ownedGate ? ownedGate.trackNumber : 0,
                nativeLinkDirection = ownedGate ? nativeLink.owner == ownedGate.ingressLink ? "Ingress" : "Egress" : "",
                nativeLinkStart = ownedGate ? nativeLink.startPos : Vector3.zero,
                nativeLinkEnd = ownedGate ? nativeLink.endPos : Vector3.zero,
                requestedNavigationSpeed = agent ? agent.speed : 0f, navigationAreaMask = agent ? agent.areaMask : 0 }, trajectoriesWriter);
        }

        void WriteAnimation(StationDecisionPassenger traveller)
        {
            var motion = traveller.motion;
            var player = motion ? motion.animationPlayer : null;
            var animator = player ? player.TargetAnimator : null;
            bool valid = animator && animator.isInitialized && animator.runtimeAnimatorController && animator.layerCount > 0;
            var current = valid ? animator.GetCurrentAnimatorStateInfo(0) : default;
            bool transition = valid && animator.IsInTransition(0);
            var next = transition ? animator.GetNextAnimatorStateInfo(0) : default;
            WriteAgentLine(traveller.JourneyId, "animations.jsonl", new AnimationRecord
            { captureStamp = CaptureSession ? CaptureSession.GetStamp() : null, time = ElapsedSeconds,
                sampleId = sampleId, agentId = traveller.JourneyId,
                actorSlotId = "slot-" + Array.IndexOf(participants, traveller).ToString("000"),
                motionId = player ? player.CurrentMotionId : "", hasAnimatorState = valid,
                animationLibrary = player && player.Library ? player.Library.name : "",
                avatarName = animator && animator.avatar ? animator.avatar.name : "",
                avatarHumanScale = animator && animator.isHuman ? animator.humanScale : 1f,
                animationPaused = player && player.IsPaused || paused,
                playbackSpeed = player ? player.PlaybackSpeed : 0f, animatorSpeed = animator ? animator.speed : 0f,
                nativePlanarSpeed = motion ? motion.ActualPlanarSpeed : 0f,
                locomotionStrideWeight = motion ? motion.AnimatorStrideWeight : 0f,
                adaptiveLocomotion = motion && motion.EnhancedLocomotionPresentation,
                walkReferenceSpeed = motion ? motion.EffectiveWalkReferenceSpeed : 0f,
                walkPose = motion && motion.IsWalkPose, locomotionActive = motion && motion.LocomotionActive,
                currentStateHash = current.fullPathHash, normalizedTime = valid ? current.normalizedTime : 0f,
                stateLengthSeconds = valid ? current.length : 0f, inTransition = transition,
                nextStateHash = next.fullPathHash, nextNormalizedTime = transition ? next.normalizedTime : 0f,
                transitionProgress = transition ? animator.GetAnimatorTransitionInfo(0).normalizedTime : 0f }, animationsWriter, retainedBytes: 2048);
        }

        void RecordTrainState(StationTrain train)
        {
            if (!TrainStatesEnabled || !train) return;
            var service = train.Service;
            WriteRootLine("train-states.jsonl", new TrainStateRecord
            { captureStamp = CaptureSession ? CaptureSession.GetStamp() : null, time = ElapsedSeconds,
                kind = "train_state", serviceId = service != null ? service.serviceId : "", track = train.trackNumber,
                state = train.State.ToString(), doorOpenFraction = train.DoorOpenFraction,
                position = train.transform.position, rotation = train.transform.rotation,
                arrivalSeconds = service != null ? service.arrivalOffsetSeconds : 0f,
                departureSeconds = service != null ? service.departureOffsetSeconds : 0f }, trainStatesWriter);
        }

        public void FlushLogs()
        {
            if (dataWriter != null && (!CaptureSession || !CaptureSession.IsRecording)) { CloseLogs(); return; }
            dataWriter?.Flush(); decisionsWriter?.Flush(); trajectoriesWriter?.Flush(); animationsWriter?.Flush(); eventsWriter?.Flush(); trainStatesWriter?.Flush();
        }
        void CloseLogs()
        {
            if (rail) rail.trainStateChanged.RemoveListener(RecordTrainState);
            skeletonCollector?.Dispose(); skeletonCollector = null;
            var owned = new IDisposable[] { decisionsWriter, trajectoriesWriter, animationsWriter, eventsWriter, trainStatesWriter };
            dataWriter = null; logOptions = null;
            decisionsWriter = trajectoriesWriter = animationsWriter = eventsWriter = trainStatesWriter = null;
            List<Exception> failures = null;
            foreach (var resource in owned)
            {
                try { resource?.Dispose(); }
                catch (Exception error) { if (failures == null) failures = new List<Exception>(); failures.Add(error); }
            }
            if (failures != null) throw new AggregateException("Unable to close all station data writers.", failures);
        }

        void OnApplicationQuit()
        {
            if (IsCollecting) StopDataCollection("application_quit");
            else if (dataWriter != null) CloseLogs(); // The session may already have drained its worker in its own quit callback.
            else { LogEvent("run_ended", null, "application quit"); CloseLogs(); }
        }
        void OnDisable()
        {
            try { if (IsCollecting) StopDataCollection("world_disabled"); else CloseLogs(); if (resumeAnimationGraphsPending) ResumeAnimationGraphs(); }
            finally { RestoreAvoidancePrediction(); }
        }
        void OnDestroy()
        {
            RestoreAvoidancePrediction();
            CloseLogs();
            ResumeAnimationGraphs();
            if (paused) Time.timeScale = resumeScale;
            if (runtimeSchedule) Destroy(runtimeSchedule);
            if (RunSession != null && RunSession.scenario) Destroy(RunSession.scenario);
        }

        [Serializable] sealed class TimetableFile
        { public StationDecisionCaptureStamp captureStamp; public string operatingDate; public double startClockSeconds; public float minimumTrackClearanceSeconds; public StationRailService[] services; }
        [Serializable] sealed class AgentProfileFile
        { public StationDecisionCaptureStamp captureStamp; public Profile profile; public DecisionArrivalPlan journey; }
        [Serializable] sealed class ProfilesFile
        { public StationDecisionCaptureStamp captureStamp; public Profile[] profiles; public DecisionArrivalPlan[] journeys; }
        [Serializable] sealed class TrainStateRecord
        { public StationDecisionCaptureStamp captureStamp; public float time, doorOpenFraction, arrivalSeconds, departureSeconds; public string kind, serviceId, state; public int track; public Vector3 position; public Quaternion rotation; }
        [Serializable] sealed class TimetableChangeRecord
        { public StationDecisionCaptureStamp captureStamp; public float time, arrivalSeconds, departureSeconds; public string kind, serviceId, detail; public bool cancelled; }
        [Serializable] sealed class AnimationRecord
        { public StationDecisionCaptureStamp captureStamp; public string agentId, actorSlotId, motionId, animationLibrary, avatarName; public float time, playbackSpeed, animatorSpeed, nativePlanarSpeed, locomotionStrideWeight, walkReferenceSpeed, normalizedTime, stateLengthSeconds, nextNormalizedTime, transitionProgress, avatarHumanScale; public int sampleId, currentStateHash, nextStateHash; public bool hasAnimatorState, animationPaused, walkPose, locomotionActive, inTransition, adaptiveLocomotion; }
        [Serializable] sealed class AnimationMotionProfile
        { public string library, motionId, statePath, clipName, source, blendParameter; public int stateHash; public bool loop; public float clipLengthSeconds, blendSeconds; public AnimationBlendChildProfile[] blendChildren; }
        [Serializable] sealed class AnimationBlendChildProfile
        { public string clipName; public float clipLengthSeconds, threshold; }

        [Serializable] sealed class Manifest
        { public AnimationMotionProfile[] animationCatalog; public StationDecisionRunSession launch; public string mobilityModelVersion;  public string schema, scenario, scene, createdUtc, unityVersion, policy, boarding, operatingDate, navigationOwnership, railStateCaptureMode; public int seed, participantCount, actorSlotCount; public double startClockSeconds; public bool finiteOperatingDay, controlledPlatformAccess, physicalArrivalQueues, nativeGateLinkTelemetry; public DecisionArrivalSettings arrivalSettings; public DecisionArrivalPlan[] scheduledJourneys; public float sampleRate, congestionAllowance, minimumTrackClearanceSeconds, avoidancePredictionSeconds; public Profile[] profiles; public PoiProfile[] pois; public StationRailService[] services; public StationDecisionScenarioEvent[] scenarioEvents; public EntranceProfile[] entrancePortals; public GateProfile[] platformControls; public CoachProfile[] coachAnchors; public CoachControlProfile[] coachControls; public ToiletControlProfile[] toiletControls; }
        [Serializable] sealed class EntranceProfile
        { public string entranceId; public double weight; public Vector3[] spawnPoints; }
        [Serializable] sealed class GateProfile
        { public int track, queueCapacity, ingressArea; public float scanSecondsPerActor, arrivalTolerance, minimumQueueSeparation, ingressWidth, egressWidth; public Vector3 center, hall, platform, ingressStart, ingressEnd, egressStart, egressEnd; public Vector3[] queueAnchors, holdingAnchors; }
        [Serializable] sealed class CoachProfile
        { public int track, coachNumber; public Vector3 position; }
        [Serializable] sealed class CoachControlProfile
        { public int track, coachNumber, queueCapacity; public float boardingSecondsPerPassenger, arrivalTolerance, minimumQueueSeparation; public Vector3 head; public Vector3[] queueAnchors; }
        [Serializable] sealed class ToiletControlProfile
        { public string poiId; public int capacity, queueCapacity; public float arrivalTolerance, minimumQueueSeparation, minimumUseSeconds, maximumUseSeconds; public Vector3[] queueSlots, usePoints; public Vector3 exitPoint; }
        [Serializable] sealed class Profile
        { public DecisionMobilityProfile mobilityProfile;  public string agentId, serviceId, groupId, entranceId, purpose, arrivalProfile, ticketId; public int occurrence, representedPassengers, trackNumber, groupSize, groupMemberIndex, coachNumber; public int[] seatNumbers; public float departure, familiarity, safetyMargin, walkSpeed, fastWalkSpeed, startDelay, plannedEntrySeconds, publishedDepartureSeconds, leadSeconds; public DecisionNeeds needs; public bool platformKnownInitially, clippedAtSimulationStart, alightingServiceVisitor; public int alightingServiceVisitLimit; public float alightingStopoverSeconds; public Vector3 position; public DecisionPolicyParameters policyParameters; }
        [Serializable] sealed class PoiProfile
        { public string poiId, activity, seatedActivity; public Vector3 position, departurePosition, facingTarget; public Vector3[] standingPoints; public int track, capacity; public float mean, spread, serviceDuration, spreadRadius, seatedMean, seatedSpread; public SeatProfile[] seats; }
        [Serializable] sealed class SeatProfile
        { public string seatId; public Vector3 approach, position, rotation; }
        [Serializable] sealed class DecisionRecord
        { public StationDecisionCaptureStamp captureStamp;  public float time, observedCutoff; public string agentId, actorSlotId, trigger, selectedActivity, selectedPoi, reason, observedStatus, previousActivity; public int decisionIndex; public bool platformKnown; public DecisionNeeds needs; public DecisionContext context; public DecisionOption[] inputOptions; public OptionRecord[] options; }
        [Serializable] sealed class OptionRecord
        { public string activity, poiId, reason; public double probability, slack, travel, queue, dwell, toPlatform, toExit, seatTravel, seatingTransition, seatedDwell; }
        [Serializable] sealed class EventRecord
        { public StationDecisionCaptureStamp captureStamp;  public float time, actualCutoff, actualDeparture, platformOpensAt, toiletUseDurationSeconds, toiletUseStartedAtSeconds; public string kind, serviceId, detail, agentId, actualStatus, actorSlotId, groupId, entranceId, purpose, outcome, ticketId, queuePredecessorJourneyId; public int sampleId, representedPassengers, capacity, occupancyAfter, coachNumber, queueSlotIndex, toiletCapacity, toiletUseCount, toiletQueueCount, toiletUseSlotIndex, holdingTrack, holdingSlotIndex, holdingLeaseCount; public bool finished, boarded, platformAccessGranted, platformAccessPassed, trainPresent, coachQueueValidated; public Vector3 position, queueArrivalPosition, queuePredecessorPosition, holdingPosition; public Profile profile; public string[] activeJourneyIds; public int[] seatNumbers; }
        [Serializable] sealed class TrajectoryRecord
        { public StationDecisionCaptureStamp captureStamp; public Quaternion rotation; public Vector3 navigationTarget, targetPoiPosition; public bool hasNavigationTarget, usingGroupPace; public float effectiveWalkSpeed, effectiveFastWalkSpeed;  public float time, budget, seatedSecondsRemaining, requestedNavigationSpeed, alightingStopoverDeadlineSeconds; public int alightingServiceVisitsCompleted; public string agentId, serviceId, activity, execution, targetPoi, knownStatus, seatId, seatedActivity, actorSlotId, groupId, purpose, nativeLinkDirection; public int occurrence, sampleId, representedPassengers, coachNumber, nativeLinkTrack, navigationAreaMask; public Vector3 position, velocity, nativeLinkStart, nativeLinkEnd; public bool finished, boarded, visibleActor, isSeated, platformAccessGranted, platformAccessPassed, isOnNativeLink; }
    }
}
