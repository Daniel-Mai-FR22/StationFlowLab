using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StationFlow.TrainStation.DecisionLab
{
    [Serializable]
    public sealed class StationDecisionCaptureStamp
    {
        public string runId, utc, simulationDate;
        public long syncSampleId;
        public int unityFrame;
        public double simulationSeconds, stationClockSeconds, realElapsedSeconds;
        public float timeScale;
        public bool paused;
    }

    /// <summary>Independent, explicitly started evidence session. Simulation and UI remain owned by the world.</summary>
    [DefaultExecutionOrder(1000), DisallowMultipleComponent]
    public sealed class StationDecisionCaptureSession : MonoBehaviour
    {
        StationDecisionWorld world;
        StationDecisionDataWriter dataWriter;
        string dataWriterMode = "background";
        public StationDecisionDataWriter DataWriter => dataWriter;
        public string DataWriterMode => dataWriterMode;
        public void ConfigureDataWriter(string mode)
        {
            if (IsRecording || stopping) throw new InvalidOperationException("Stop capture before changing its data writer.");
            if (mode != "main" && mode != "background") throw new ArgumentException("Data writer mode must be main or background.", nameof(mode));
            dataWriterMode = mode;
        }
        Stopwatch clock;
        StationDecisionCaptureStamp stamp;
        readonly List<CameraStream> streams = new List<CameraStream>();
        readonly List<string> errors = new List<string>();
        long sampleId;
        double nextFlushAt, previousTickAt;
        bool stopping, dataCompletionStarted, framePacingApplied;
        StationDecisionCaptureStamp finalizationStamp;
        string stopReason;
        double stopRequestedRealSeconds, stopSimulationSeconds;
        int dataQueueMaxCommands = StationDecisionDataWriter.DefaultMaxPendingCommands;
        long dataQueueMaxBytes = StationDecisionDataWriter.DefaultMaxPendingBytes;
        long optionalAdmissionBudgetOverrideBytes, essentialHeadroomBytes;
        int essentialHeadroomCommands;
        long captureBatchesRequested, captureBatchesAdmitted, captureBatchesRejectedData, captureBatchesRejectedVideo;
        double nextPressureMarkerAt;
        CapturePressure pressureWindow;
        int previousTargetFrameRate, previousVSyncCount, captureTargetFrameRate;
        string runMetadataJson;
        int readbackSlots = 4;
        bool commonBatchAdmission = true;
        string renderProfileName = StationDecisionCaptureRenderProfile.DefaultProfile;
        CameraStream.Reservation[] batchReservations = Array.Empty<CameraStream.Reservation>();
        public int ReadbackSlotCount => readbackSlots;
        public bool CommonBatchAdmission => commonBatchAdmission;
        public string RenderProfile => renderProfileName;
        public void ConfigureCapturePipeline(int readbackSlots = 4, bool commonBatchAdmission = true)
        {
            if (IsRecording || stopping) throw new InvalidOperationException("Stop capture before changing its pipeline.");
            if (readbackSlots < 1 || readbackSlots > 4) throw new ArgumentOutOfRangeException(nameof(readbackSlots));
            this.readbackSlots = readbackSlots; this.commonBatchAdmission = commonBatchAdmission;
        }
        public void ConfigureRenderProfile(string profile)
        {
            if (IsRecording || stopping) throw new InvalidOperationException("Stop capture before changing its render profile.");
            renderProfileName = StationDecisionCaptureRenderProfile.Normalize(profile);
        }
        Manifest manifest;
        StationDecisionCaptureDiagnostics diagnostics;
        StationDecisionFrameTelemetry frameTelemetry;
        public bool IsRecording { get; private set; }
        public bool IsFinalizing => stopping;
        public int PendingDataCommands => dataWriter?.PendingCommands ?? 0;
        public long CaptureBatchesRequested => captureBatchesRequested;
        public long CaptureBatchesAdmitted => captureBatchesAdmitted;
        public long CaptureBatchesRejectedData => captureBatchesRejectedData;
        public long CaptureBatchesRejectedVideo => captureBatchesRejectedVideo;
        /// <summary>Explicit diagnostic ceiling for optional batches; the queue cap and essential admission remain unchanged.</summary>
        public void ConfigureOptionalAdmissionBudget(long optionalBudgetBytes)
        {
            if (IsRecording || stopping) throw new InvalidOperationException("Stop capture before changing its optional admission budget.");
            if (optionalBudgetBytes < 1 || optionalBudgetBytes > dataQueueMaxBytes) throw new ArgumentOutOfRangeException(nameof(optionalBudgetBytes));
            optionalAdmissionBudgetOverrideBytes = optionalBudgetBytes;
        }
        /// <summary>Diagnostic queue limit override. Production defaults remain unchanged.</summary>
        public void ConfigureDataQueueLimits(long maximumPendingBytes, int maximumPendingCommands = StationDecisionDataWriter.DefaultMaxPendingCommands)
        {
            if (IsRecording || stopping) throw new InvalidOperationException("Stop capture before changing queue limits.");
            if (maximumPendingBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumPendingBytes));
            if (maximumPendingCommands < 1) throw new ArgumentOutOfRangeException(nameof(maximumPendingCommands));
            if (optionalAdmissionBudgetOverrideBytes > maximumPendingBytes) throw new ArgumentOutOfRangeException(nameof(maximumPendingBytes), "The queue limit is smaller than the configured optional budget.");
            dataQueueMaxBytes = maximumPendingBytes; dataQueueMaxCommands = maximumPendingCommands;
        }
        public string OutputDirectory { get; private set; }
        public string RunId { get; private set; }
        public string LastError { get; private set; } = "";
        public StationDecisionCaptureOptions Options { get; private set; }
        public StationDecisionCaptureStamp CurrentStamp => GetStamp();
        public static string StorageRoot => StationDecisionCaptureStorage.ResolveRoot(Application.dataPath, Application.platform == RuntimePlatform.OSXPlayer);
        public static string DefaultOutputRoot => StorageRoot;
        public static string GetDefaultOutputRoot() => StorageRoot;
        public long VideoFramesWritten => streams.Sum(s => Interlocked.Read(ref s.Written));
        public long VideoFramesDropped => streams.Sum(s => Interlocked.Read(ref s.Dropped));
        public int RecordingVideoCameraCount => streams.Count;
        public double GameFpsRolling1s => frameTelemetry ? frameTelemetry.GameFpsRolling1s : 0d;
        public long GetActualVideoFramesWritten(string cameraId)
        {
            var stream = streams.FirstOrDefault(s => string.Equals(s.CameraId, cameraId, StringComparison.Ordinal));
            return stream == null ? 0L : Interlocked.Read(ref stream.Written);
        }
        public double GetActualVideoFpsRolling1s(string cameraId)
        {
            var stream = streams.FirstOrDefault(s => string.Equals(s.CameraId, cameraId, StringComparison.Ordinal));
            return stream == null || clock == null ? 0d : stream.GetActualFpsAt(clock.Elapsed.TotalSeconds);
        }
        public StationDecisionCameraFrameRate[] GetActualVideoRates()
        {
            double now = GetStamp()?.realElapsedSeconds ?? (clock == null ? 0d : clock.Elapsed.TotalSeconds);
            return streams.Select(s => s.GetRateSnapshotAt(now)).ToArray();
        }
        public bool VideoFramePacingApplied => framePacingApplied;
        public int RequestedRenderFrameRate => captureTargetFrameRate;
        public double AverageVideoFramesPerSecondPerCamera => streams.Count > 0 && clock != null && clock.Elapsed.TotalSeconds > 0d
            ? VideoFramesWritten / (streams.Count * clock.Elapsed.TotalSeconds) : 0d;
        public void Initialize(StationDecisionWorld owner)
        {
            if (IsRecording || stopping) throw new InvalidOperationException("Stop capture before changing its world.");
            world = owner;
            frameTelemetry = GetComponent<StationDecisionFrameTelemetry>();
            if (!frameTelemetry) frameTelemetry = gameObject.AddComponent<StationDecisionFrameTelemetry>();
            frameTelemetry.Initialize(this);
        }
        public void ConfigureDiagnostics(StationDecisionCaptureDiagnostics collector)
        {
            if (IsRecording || stopping) throw new InvalidOperationException("Stop capture before changing diagnostics.");
            diagnostics = collector;
        }
        public void SetRunMetadata(string json) { if (IsRecording || stopping) throw new InvalidOperationException("Launch metadata is fixed during capture."); runMetadataJson = json; }
        public StationSurveillanceCamera[] AvailableCameras => FindObjectsByType<StationSurveillanceCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(c => c && c.gameObject.scene == gameObject.scene && c.cameraComponent).OrderBy(c => c.cameraId, StringComparer.Ordinal).ToArray();

        public bool StartCapture(StationDecisionCaptureOptions requested)
        {
            if (IsRecording || stopping) { if (string.IsNullOrEmpty(LastError)) LastError = "A capture is already active or closing."; return false; }
            LastError = ""; errors.Clear(); streams.Clear(); stamp = null; manifest = null; sampleId = 0; previousTickAt = 0; captureTargetFrameRate = 0;
            dataCompletionStarted = false; finalizationStamp = null; stopReason = null; stopRequestedRealSeconds = stopSimulationSeconds = 0;
            captureBatchesRequested = captureBatchesAdmitted = captureBatchesRejectedData = captureBatchesRejectedVideo = 0;
            pressureWindow = null; nextPressureMarkerAt = 1d;
            essentialHeadroomBytes = optionalAdmissionBudgetOverrideBytes > 0 ? dataQueueMaxBytes - optionalAdmissionBudgetOverrideBytes : Math.Max(1L, dataQueueMaxBytes / 4L);
            essentialHeadroomCommands = Math.Max(1, dataQueueMaxCommands / 4);
            try
            {
                if (!world) world = GetComponent<StationDecisionWorld>();
                if (!world || !world.rail || !world.rail.IsReady) throw new InvalidOperationException("The railway clock is not ready.");
                Options = (requested ?? new StationDecisionCaptureOptions()).Copy(); Options.Validate();
                var cameras = AvailableCameras;
                var chosen = new HashSet<string>(Options.selectedCameraIds, StringComparer.Ordinal);
                if (Options.video)
                {
                    if (cameras.Length == 0) throw new InvalidOperationException("No installed surveillance camera is available.");
                    if (cameras.Any(c => string.IsNullOrWhiteSpace(c.cameraId)) || cameras.Select(c => c.cameraId).Distinct(StringComparer.Ordinal).Count() != cameras.Length)
                        throw new InvalidOperationException("Surveillance camera IDs must be unique and nonempty.");
                    if (cameras.Select(c => SafeName(c.cameraId)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != cameras.Length)
                        throw new InvalidOperationException("Surveillance camera IDs collide after conversion to safe directory names.");
                    foreach (string id in chosen) if (!cameras.Any(c => c.cameraId == id)) throw new ArgumentException("Unknown surveillance camera: " + id);
                    cameras = cameras.Where(c => chosen.Count == 0 || chosen.Contains(c.cameraId)).ToArray();
                }
                else cameras = Array.Empty<StationSurveillanceCamera>();
                RunId = "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                OutputDirectory = Path.Combine(StorageRoot, RunId); Directory.CreateDirectory(OutputDirectory);
                var dataSchema = StationDecisionDataSchemaInfo.CopyToCapture(OutputDirectory);
                dataWriter = new StationDecisionDataWriter(OutputDirectory, RunId, (record, pretty) => JsonUtility.ToJson(record, pretty), dataWriterMode == "background", diagnostics, dataQueueMaxCommands, dataQueueMaxBytes);
                dataWriter.OpenRoot("sync.jsonl"); clock = Stopwatch.StartNew();
                manifest = new Manifest { schema = "stationflow.station_capture.v4", storageLayout = "per_agent.v3",
                    recordEncoding = "references-and-state-revisions.v1", normalizationFile = "normalization.json",
                    recordDefinitionsFile = "record-definitions.jsonl", rostersFile = "rosters.jsonl", stateCheckpointsFile = "state-checkpoints.jsonl",
                    skeletonDefinitionsFile = Options.skeletons ? "skeleton-definitions.jsonl" : "",
                    skeletonPoseFileName = Options.skeletons ? "skeleton-poses.bin" : "", skeletonBindingsFileName = Options.skeletons ? "skeleton-bindings.jsonl" : "",
                    skeletonIndexFileName = Options.skeletons ? "skeleton-index.jsonl" : "", skeletonPoseFormat = Options.skeletons ? "stationflow.skeleton_pose_binary.v1" : "", runId = RunId, createdUtc = DateTime.UtcNow.ToString("O"),
                    dataSchema = dataSchema,
                    operatingDate = world.rail.schedule.operatingDateIso, scene = gameObject.scene.path, unityVersion = Application.unityVersion,
                    applicationProductName = Application.productName, applicationVersion = Application.version, applicationBuildGuid = Application.buildGUID,
                    platform = Application.platform.ToString(), captureOptions = Options.Copy(), selectedCameraIds = cameras.Select(c => c.cameraId).ToArray(),
                    localTimeZoneId = TimeZoneInfo.Local.Id, localUtcOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,
                    launchMetadataJson = runMetadataJson,
                    videoFrameAvatarSnapshots = true, captureCadence = "each_unity_frame", nominalPlaybackFps = Options.videoFps,
                    readbackSlotCount = readbackSlots, commonBatchAdmission = commonBatchAdmission, renderProfile = renderProfileName,
                    dataWriterMode = dataWriterMode, dataQueueMaxPendingCommands = dataQueueMaxCommands, dataQueueMaxRetainedBytes = dataQueueMaxBytes,
                    dataEssentialHeadroomBytes = essentialHeadroomBytes, dataEssentialHeadroomCommands = essentialHeadroomCommands,
                    optionalAdmissionBudgetBytes = dataQueueMaxBytes - essentialHeadroomBytes, optionalAdmissionBudgetOverrideBytes = optionalAdmissionBudgetOverrideBytes,
                    captureAdmissionPolicy = commonBatchAdmission
                        ? "camera-first-common-and-reserved-data.v1: all selected cameras and one conservative avatar-data batch are reserved before optional authoring; capacity rejection drops the entire optional batch; essential simulation records remain independent."
                        : "camera-first-independent-and-reserved-data.v1: available cameras retain independent admission; at least one camera and one shared conservative avatar-data batch are reserved before optional authoring; essential simulation records remain independent.",
                    actualVideoRateDefinition = "Per saved frame, captureIntervalRealSeconds and captureFpsInstantaneous use successive original render stamp.realElapsedSeconds (first frame null); captureFpsRolling1s counts saved render stamps in (frameRealSeconds-1, frameRealSeconds] divided by one real second. AVI headers and videoTimeSeconds retain nominal playback timing, not actual capture FPS.",
                    storageRoot = StorageRoot, startSimulationSeconds = world.rail.ElapsedSimulationSeconds, startClockSeconds = world.rail.ClockSeconds,
                    simulationSeed = world.seed, status = "recording", synchronization = "The manifest runId and syncSampleId resolve exact capture stamps through canonical clock records in sync.jsonl; each cameraStateId resolves its immutable camera snapshot in video/<camera>/camera-states.jsonl. Expanded readers restore full captureStamp/stamp and camera fields. AVI playback time omits dropped requests",
                    videoFormat = "AVI/MJPEG, no audio, independent 5 minute or 900 MB segments; JPEG encoding and disk writing on bounded background queues",
                    syncFile = "sync.jsonl", videoDirectory = Options.video ? "video" : "", scenarioFile = "scenario.json",
                    agentsDirectory = Options.profiles || Options.trajectories || Options.animations || Options.skeletons || Options.decisions || Options.events ? "agents" : "",
                    agentIndexFile = Options.profiles || Options.trajectories || Options.animations || Options.skeletons || Options.decisions || Options.events ? "agents/index.json" : "" };
                if (cameras.Length > 0) Directory.CreateDirectory(Path.Combine(OutputDirectory, "video"));
                if (diagnostics != null)
                {
                    diagnostics.SetMetadata("runId", RunId); diagnostics.SetMetadata("captureDirectory", OutputDirectory);
                    diagnostics.SetMetadata("applicationBuildGuid", Application.buildGUID);
                    diagnostics.SetMetadata("requestedWidth", Options.videoWidth.ToString(CultureInfo.InvariantCulture));
                    diagnostics.SetMetadata("requestedHeight", Options.videoHeight.ToString(CultureInfo.InvariantCulture));
                    diagnostics.SetMetadata("nominalPlaybackFps", Options.videoFps.ToString(CultureInfo.InvariantCulture));
                    diagnostics.SetMetadata("captureCadence", "each_unity_frame");
                    diagnostics.SetMetadata("readbackSlotCount", readbackSlots.ToString(CultureInfo.InvariantCulture));
                    diagnostics.SetMetadata("commonBatchAdmission", commonBatchAdmission.ToString());
                    diagnostics.SetMetadata("renderProfile", renderProfileName);
                    diagnostics.SetMetadata("dataWriterMode", dataWriterMode);
                    diagnostics.SetMetadata("selectedCameraIds", string.Join(",", manifest.selectedCameraIds));
                }
                foreach (var camera in cameras) streams.Add(new CameraStream(camera, OutputDirectory, Options, diagnostics, readbackSlots, renderProfileName, dataWriterMode == "background"));
                batchReservations = new CameraStream.Reservation[streams.Count];
                ApplyVideoFramePacing();
                IsRecording = true; nextFlushAt = 1;
                WriteManifest(); WriteReadme(); RecordMarker("capture_started", "Options are fixed for this session.");
                if (framePacingApplied) RecordMarker("capture_frame_pacing", "targetFrameRate=" + captureTargetFrameRate + "; vSyncCount=0; previousTargetFrameRate=" + previousTargetFrameRate + "; previousVSyncCount=" + previousVSyncCount + ". Capture attempts follow Unity frames; AVI playback is nominal " + Options.videoFps + " fps only; original frame sidecars define actual rates.");
                return true;
            }
            catch (Exception error)
            {
                LastError = error.Message; errors.Add(error.ToString());
                CleanupCaptureResources();
                if (manifest != null && !string.IsNullOrEmpty(OutputDirectory)) { manifest.status = "failed"; manifest.errors = errors.ToArray(); TryWriteManifest(); }
                UnityEngine.Debug.LogError("Station capture could not start: " + LastError, this); return false;
            }
        }

        public StationDecisionCaptureStamp GetStamp()
        {
            if (!IsRecording || dataWriter == null) return null;
            double simulation = world && world.rail ? world.rail.ElapsedSimulationSeconds : 0d;
            bool paused = world && (world.Paused || world.rail && world.rail.IsPaused) || Time.timeScale == 0f;
            if (stamp != null && stamp.unityFrame == Time.frameCount && stamp.simulationSeconds == simulation && stamp.timeScale == Time.timeScale && stamp.paused == paused) return stamp;
            stamp = new StationDecisionCaptureStamp { runId = RunId, syncSampleId = ++sampleId, unityFrame = Time.frameCount,
                simulationSeconds = simulation, stationClockSeconds = world && world.rail ? world.rail.ClockSeconds : 0d,
                simulationDate = world && world.rail && world.rail.schedule ? world.rail.schedule.operatingDateIso : "",
                realElapsedSeconds = clock.Elapsed.TotalSeconds, utc = DateTime.UtcNow.ToString("O"), timeScale = Time.timeScale, paused = paused };
            WriteSync(new SyncRecord { kind = "clock", stamp = stamp });
            return stamp;
        }

        public void RecordMarker(string kind, string detail)
        {
            if (!IsRecording || dataWriter == null) return;
            WriteSync(new SyncRecord { kind = kind, detail = detail, stamp = GetStamp() });
        }

        public void RecordMeasuredPerformance(StationDecisionMeasuredFrameRates record)
        {
            if (!IsRecording || dataWriter == null || record == null) return;
            WriteSync(new SyncRecord { kind = "measured_frame_rates", stamp = GetStamp(),
                measuredFrameRates = StationDecisionCaptureSnapshot.Copy(record, out _) });
        }

        public void RecordAvatarSnapshot(string[] activeJourneyIds, string reason)
        {
            if (!IsRecording || dataWriter == null || !(Options.trajectories || Options.animations || Options.skeletons)) return;
            WriteSync(new SyncRecord { kind = "avatar_snapshot", detail = reason,
                activeJourneyIds = (string[])(activeJourneyIds ?? Array.Empty<string>()).Clone(), stamp = GetStamp() });
        }

        void WriteSync(SyncRecord record)
        {
            // Capture stamps are created once and never changed. Each queued record owns its mutable arrays.
            long retained = 2048 + (record.activeJourneyIds?.Length ?? 0) * 256L + (record.detail?.Length ?? 0) * 2L;
            dataWriter.WriteRoot("sync.jsonl", record, retainedBytes: retained);
        }

        internal void CompleteDataInitialization(bool final = true)
        {
            long started = Stopwatch.GetTimestamp();
            try { dataWriter.CompleteInitialization(); }
            finally
            {
                double waitSeconds = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
                manifest.dataInitializationWaitRealSeconds += waitSeconds;
                diagnostics?.Record("", "data_initialization_startup_wait_wall", "ms", waitSeconds * 1000d);
            }
            if (!final) return;
            stamp = null; // Startup may span real seconds without advancing a Unity frame.
            RecordMarker("capture_data_initialized", "Startup data flushed before simulation/video admission; waitRealSeconds=" + manifest.dataInitializationWaitRealSeconds.ToString("R", CultureInfo.InvariantCulture));
            WriteManifest();
        }

        internal void RecordDataSnapshotCost(long started) => diagnostics?.Elapsed("", "data_snapshot_copy_cpu_wall", started);

        public void ReportError(string detail)
        {
            LastError = detail ?? "Unspecified data-collection error.";
            errors.Add(LastError);
            RecordMarker("capture_error", LastError);
        }

        void LateUpdate()
        {
            if (stopping) { AdvanceFinalization(); return; }
            if (!IsRecording) return;
            long tickStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
            try
            {
                dataWriter.ThrowIfFailed();
                var now = GetStamp();
                double real = clock.Elapsed.TotalSeconds;
                if (previousTickAt > 0 && real - previousTickAt > 1d)
                    RecordMarker("runtime_gap", "No capture update for " + (real - previousTickAt).ToString("F6", CultureInfo.InvariantCulture) + " real seconds; no synthetic frames were inserted.");
                previousTickAt = real;
                foreach (var stream in streams)
                {
                    stream.PumpReadyPackets();
                    while (stream.Messages.TryDequeue(out var message)) RecordMarker(message.kind, stream.CameraId + ": " + message.detail);
                    if (stream.Failure != null) throw new IOException(stream.CameraId + ": " + stream.Failure);
                }
                if (Options.video) CaptureVideoBatch(now, real);
                FlushPressureWindow(real);

                if (real >= nextFlushAt) { dataWriter.Flush(); nextFlushAt = real + 1d; }
            }
            catch (Exception error) { LastError = error.Message; errors.Add(error.ToString()); StopCapture("capture_error"); UnityEngine.Debug.LogError("Station capture stopped: " + error.Message, this); }
            finally { diagnostics?.Elapsed("", "main_capture_tick_wall", tickStarted); }
        }

        void CaptureVideoBatch(StationDecisionCaptureStamp now, double real)
        {
            ++captureBatchesRequested; diagnostics?.Increment("", "capture_batches_requested");
            if (!TryReserveVideoBatch(now, real))
            {
                ++captureBatchesRejectedVideo; diagnostics?.Increment("", "capture_batches_rejected");
                diagnostics?.Increment("", "capture_batches_rejected_video");
                NoteCapacityRejection(false, 0, now, real); return;
            }
            try
            {
                long estimatedBytes = world.EstimateVideoSnapshotBytes();
                if (!dataWriter.TryBeginReservedBatch(estimatedBytes, essentialHeadroomBytes, out var batch, essentialHeadroomCommands))
                {
                    ++captureBatchesRejectedData; diagnostics?.Increment("", "capture_batches_rejected");
                    diagnostics?.Increment("", "capture_batches_rejected_data");
                    for (int i = 0; i < streams.Count; ++i)
                        if (batchReservations[i].IsReserved) streams[i].RejectForDataPressure(batchReservations[i]);
                    NoteCapacityRejection(true, estimatedBytes, now, real); return;
                }
                // Reservation happens before any roster/pose publication or same-frame snapshot cache advances.
                // Commit is guaranteed inside the declared budget; real I/O failures and underestimates remain fatal.
                using (batch)
                {
                    long snapshotStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                    try { world.CaptureDataForVideoFrame(); batch.Commit(); }
                    finally { diagnostics?.Elapsed("", "avatar_snapshot_cpu_wall", snapshotStarted); }
                }
                ++captureBatchesAdmitted; diagnostics?.Increment("", "capture_batches_admitted");
                long canvasStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                var canvases = FindObjectsByType<Canvas>(FindObjectsSortMode.None).Where(c => c && c.gameObject.scene == gameObject.scene && c.enabled).ToArray();
                diagnostics?.Elapsed("", "canvas_lookup_cpu_wall", canvasStarted);
                try
                {
                    foreach (var canvas in canvases) canvas.enabled = false;
                    var renderStamp = GetStamp();
                    for (int i = 0; i < streams.Count; ++i)
                        if (batchReservations[i].IsReserved) streams[i].CaptureReserved(batchReservations[i], renderStamp);
                }
                finally { foreach (var canvas in canvases) if (canvas) canvas.enabled = true; }
            }
            finally
            {
                for (int i = 0; i < streams.Count; ++i) streams[i].CancelUnusedReservation(batchReservations[i]);
            }
        }

        bool TryReserveVideoBatch(StationDecisionCaptureStamp now, double real)
        {
            bool allReady = true, anyReady = false; int deniedCameras = 0;
            for (int i = 0; i < streams.Count; ++i)
                if (streams[i].TryReserve(out batchReservations[i])) anyReady = true;
                else { allReady = false; ++deniedCameras; }
            if (commonBatchAdmission && !allReady)
            {
                for (int i = 0; i < streams.Count; ++i) streams[i].RejectReservation(batchReservations[i], false);
                return false;
            }
            if (!commonBatchAdmission)
            {
                for (int i = 0; i < streams.Count; ++i)
                    if (!batchReservations[i].IsReserved) streams[i].RejectReservation(batchReservations[i], false);
                if (anyReady && deniedCameras > 0)
                {
                    var window = PressureWindowAt(now, real);
                    ++window.partialVideoBatches; window.partialCameraDeniedRequests += deniedCameras;
                }
            }
            return anyReady;
        }

        CapturePressure PressureWindowAt(StationDecisionCaptureStamp now, double real)
        {
            if (pressureWindow == null) pressureWindow = new CapturePressure {
                firstRejectedUnityFrame = now.unityFrame, firstRejectedRealElapsedSeconds = real };
            pressureWindow.lastRejectedUnityFrame = now.unityFrame; pressureWindow.lastRejectedRealElapsedSeconds = real;
            pressureWindow.maxPendingDataBytes = Math.Max(pressureWindow.maxPendingDataBytes, dataWriter.PendingBytes);
            pressureWindow.maxPendingDataCommands = Math.Max(pressureWindow.maxPendingDataCommands, dataWriter.PendingCommands);
            return pressureWindow;
        }

        void NoteCapacityRejection(bool dataPressure, long estimatedBytes, StationDecisionCaptureStamp now, double real)
        {
            var window = PressureWindowAt(now, real);
            if (dataPressure) ++window.dataDeniedBatches; else ++window.videoDeniedBatches;
            window.maxEstimatedBatchBytes = Math.Max(window.maxEstimatedBatchBytes, estimatedBytes);
        }

        void FlushPressureWindow(double real, bool final = false)
        {
            if (!final && real < nextPressureMarkerAt) return;
            nextPressureMarkerAt = real + 1d;
            if (pressureWindow == null) return;
            pressureWindow.reason = pressureWindow.dataDeniedBatches > 0
                ? pressureWindow.videoDeniedBatches > 0 || pressureWindow.partialVideoBatches > 0 ? "data_and_camera_capacity" : "data_queue_capacity" : "camera_capacity";
            var boundary = final ? finalizationStamp : GetStamp();
            if (boundary == null) return;
            // Ownership transfers to the writer. Replace the window instead of mutating queued metadata.
            WriteSync(new SyncRecord { kind = "capture_capacity_pressure", stamp = boundary, capturePressure = pressureWindow });
            pressureWindow = null;
        }

        /// <summary>Close live capture admission immediately; subsequent frames finish owned work without blocking the UI.</summary>
        public void StopCapture(string reason = "user_stopped")
        {
            if (!IsRecording || stopping) return;
            stopping = true;
            stopReason = reason;
            stopRequestedRealSeconds = clock?.Elapsed.TotalSeconds ?? 0d;
            stopSimulationSeconds = world && world.rail ? world.rail.ElapsedSimulationSeconds : 0d;
            try { finalizationStamp = GetStamp(); RecordMarker("capture_stopping", reason); }
            catch (Exception error) { CaptureCleanupError(error); finalizationStamp = stamp; }
            IsRecording = false;
            // Readback targets remain owned until their callbacks and video workers have finished.
            foreach (var stream in streams) stream.BeginStop();
            try { RestoreVideoFramePacing(); } catch (Exception error) { CaptureCleanupError(error); }
            diagnostics?.SetMetadata("captureStopRequestedRealSeconds", stopRequestedRealSeconds.ToString("R", CultureInfo.InvariantCulture));
        }

        void AdvanceFinalization()
        {
            if (!stopping) return;
            long started = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
            try
            {
                bool videosFinished = true;
                foreach (var stream in streams)
                    try { if (!stream.TryCompleteStop()) videosFinished = false; }
                    catch (Exception error) { CaptureCleanupError(error); videosFinished = false; }
                if (!videosFinished) return;
                BeginDataFinalization();
                // Completion is published after all accepted commands and file closure, including failed drains.
                if (dataWriter != null && !dataWriter.IsCompletionFinished) return;
                try { dataWriter?.TryComplete(); } catch (Exception error) { CaptureCleanupError(error); }
                FinishFinalization(false);
            }
            finally { diagnostics?.Elapsed("", "main_capture_finalization_poll_wall", started); }
        }

        void BeginDataFinalization()
        {
            if (dataCompletionStarted) return;
            foreach (var stream in streams)
            {
                while (stream.Messages.TryDequeue(out var message))
                    try { RecordFinalizationMarker(message.kind, stream.CameraId + ": " + message.detail); }
                    catch (Exception error) { CaptureCleanupError(error); }
                if (stream.Failure != null) CaptureCleanupError(new IOException(stream.CameraId + ": " + stream.Failure));
            }
            try { FlushPressureWindow(stopRequestedRealSeconds, true); } catch (Exception error) { CaptureCleanupError(error); }
            try { RecordFinalizationMarker("capture_stopped", stopReason); } catch (Exception error) { CaptureCleanupError(error); }
            dataCompletionStarted = true;
            dataWriter?.BeginComplete();
        }

        void RecordFinalizationMarker(string kind, string detail)
        {
            if (dataWriter == null || finalizationStamp == null) return;
            // Observation may have resumed already. Terminal markers refer to the capture boundary,
            // never to a new simulation state that has no corresponding captured avatar data.
            WriteSync(new SyncRecord { kind = kind, detail = detail, stamp = finalizationStamp });
        }

        /// <summary>Explicit terminal barrier for shutdown or offline tooling; never used by a live Stop button/error.</summary>
        public void CompletePendingStopForShutdown()
        {
            if (!stopping) return;
            foreach (var stream in streams)
                try { stream.CompleteStopForShutdown(); } catch (Exception error) { CaptureCleanupError(error); }
            try { BeginDataFinalization(); } catch (Exception error) { CaptureCleanupError(error); }
            try { dataWriter?.Complete(); } catch (Exception error) { CaptureCleanupError(error); }
            FinishFinalization(true);
        }

        void FinishFinalization(bool terminal)
        {
            try
            {
                if (dataWriter != null)
                {
                    manifest.dataCommandsAccepted = dataWriter.CommandsAccepted; manifest.dataCommandsCompleted = dataWriter.CommandsCompleted;
                    manifest.dataRecordsAccepted = dataWriter.RecordsAccepted; manifest.dataRecordsCompleted = dataWriter.RecordsCompleted;
                    manifest.dataQueueHighWaterCommands = dataWriter.HighWaterCommands; manifest.dataQueueHighWaterRetainedBytes = dataWriter.HighWaterBytes;
                }
                manifest.stopReason = stopReason; manifest.endSimulationSeconds = stopSimulationSeconds;
                manifest.stoppedUtc = DateTime.UtcNow.ToString("O");
                manifest.realDurationSeconds = clock?.Elapsed.TotalSeconds ?? stopRequestedRealSeconds;
                manifest.stopRequestedRealElapsedSeconds = stopRequestedRealSeconds;
                manifest.finalizationRealSeconds = Math.Max(0d, manifest.realDurationSeconds - stopRequestedRealSeconds);
                manifest.captureBatchesRequested = captureBatchesRequested; manifest.captureBatchesAdmitted = captureBatchesAdmitted;
                manifest.captureBatchesRejectedData = captureBatchesRejectedData; manifest.captureBatchesRejectedVideo = captureBatchesRejectedVideo;
                manifest.syncSamples = sampleId;
            }
            catch (Exception error) { CaptureCleanupError(error); }
            finally
            {
                // Normal completion has already closed every file and finished every worker; no Join is required.
                CleanupCaptureResources(terminal);
                if (manifest != null)
                {
                    manifest.status = errors.Count == 0 ? "complete" : "failed";
                    manifest.cameraResults = streams.Select(stream => stream.Result()).ToArray(); manifest.errors = errors.ToArray();
                    try { WriteManifest(); }
                    catch (Exception error) { CaptureCleanupError(error); manifest.status = "failed"; manifest.errors = errors.ToArray(); TryWriteManifest(); }
                    if (diagnostics != null)
                    {
                        diagnostics.SetMetadata("status", manifest.status);
                        diagnostics.SetMetadata("manifestRealDurationSeconds", manifest.realDurationSeconds.ToString("R", CultureInfo.InvariantCulture));
                        diagnostics.SetMetadata("captureFinalizationRealSeconds", Math.Max(0d, manifest.realDurationSeconds - stopRequestedRealSeconds).ToString("R", CultureInfo.InvariantCulture));
                        diagnostics.SetMetadata("finalizedFramesWritten", VideoFramesWritten.ToString(CultureInfo.InvariantCulture));
                        diagnostics.SetMetadata("finalizedRequestsDropped", VideoFramesDropped.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        void ApplyVideoFramePacing()
        {
            if (!Options.video || streams.Count == 0) return;
            previousTargetFrameRate = Application.targetFrameRate; previousVSyncCount = QualitySettings.vSyncCount;
            captureTargetFrameRate = Math.Max(60, previousTargetFrameRate);
            framePacingApplied = true;
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = captureTargetFrameRate;
        }
        void RestoreVideoFramePacing()
        {
            if (!framePacingApplied) return;
            Application.targetFrameRate = previousTargetFrameRate; QualitySettings.vSyncCount = previousVSyncCount;
            framePacingApplied = false;
        }

        void OnApplicationFocus(bool focus) => RecordMarker(focus ? "application_focus_gained" : "application_focus_lost", "Application focus changed; the runtime clock remains authoritative.");
        void OnApplicationPause(bool held) => RecordMarker(held ? "application_suspended" : "application_resumed", "Platform application pause notification.");
        void OnApplicationQuit() { StopCapture("application_quit"); CompletePendingStopForShutdown(); }
        void OnDisable() { StopCapture("component_disabled"); CompletePendingStopForShutdown(); }
        void OnDestroy() { StopCapture("component_destroyed"); CompletePendingStopForShutdown(); }
        void CaptureCleanupError(Exception error)
        {
            if (string.IsNullOrEmpty(LastError)) LastError = error.Message;
            errors.Add(error.ToString());
        }
        void CleanupStreams()
        { foreach (var stream in streams) try { stream.Dispose(); } catch (Exception error) { CaptureCleanupError(error); } }
        void CleanupCaptureResources(bool terminal = true)
        {
            IsRecording = false;
            try
            {
                try { RestoreVideoFramePacing(); } catch (Exception error) { CaptureCleanupError(error); }
                try { if (terminal) dataWriter?.Dispose(); } catch (Exception error) { CaptureCleanupError(error); } finally { dataWriter = null; }
                CleanupStreams();
            }
            finally { clock?.Stop(); stopping = false; framePacingApplied = false; }
        }
        void WriteManifest() => File.WriteAllText(Path.Combine(OutputDirectory, "manifest.json"), JsonUtility.ToJson(manifest, true), new UTF8Encoding(false));
        void WriteReadme() => File.WriteAllText(Path.Combine(OutputDirectory, "README.txt"),
            "COLLECTE DE DONNEES DE LA GARE\n" +
            "manifest.json contient les options et l'etat final de la collecte. scenario.json contient les metadonnees du scenario choisies. Le contrat detaille en francais accompagne cette session dans TrainStationData/.\n" +
            "applicationProductName, applicationVersion et applicationBuildGuid dans le manifeste identifient le produit et la compilation qui ont genere ces donnees. applicationBuildGuid permet de retrouver le build-info.json livre avec cette compilation.\n" +
            "Le manifeste stationflow.station_capture.v4 declare storageLayout=per_agent.v3 et recordEncoding=references-and-state-revisions.v1. Les donnees des voyageurs sont rangees dans agents/<repertoireSur>/ ; agents/index.json associe chaque agentId logique au chemin relatif depuis la racine de cette collecte.\n" +
            "L’index stationflow.agents_index.v1 contient tous les IDs de voyages planifies. Un agentId identifie un voyage unique : ne pas fusionner les donnees selon actorSlotId, car le pool visuel reutilise ses emplacements. Les noms de dossiers utilisent un prefixe lisible ASCII limite a 56 caracteres et les 16 premiers caracteres hexadecimaux du SHA-256 de l’ID original ; lire l’ID exact dans l’index.\n" +
            "Selon les options, chaque dossier contient profile.json (un seul profile et un seul journey), trajectories.jsonl, animations.jsonl, decisions.jsonl et events.jsonl. L’option squelettes ajoute skeleton-bindings.jsonl, skeleton-poses.bin et skeleton-index.jsonl. Les dossiers sont crees au premier besoin ; l’option profiles cree un profil pour chaque voyage planifie. Une entree d’index peut donc ne pas encore avoir de dossier. Si toutes les options de donnees voyageurs sont decochees, agents/ et son index sont absents et les chemins correspondants du manifeste sont vides.\n" +
            "Les fichiers communs restent a la racine : sync.jsonl, scenario.json, timetable.json, train-states.jsonl et video/. events.jsonl a la racine ne contient que les evenements de station dont agentId est vide ; les evenements individuels sont dans le dossier du voyageur. scenario.json conserve les metadonnees partagees ; ses listes profiles et scheduledJourneys sont absentes ou vides pour ne pas dupliquer les donnees individuelles.\n" +
            "Les fichiers normalization.json, record-definitions.jsonl, rosters.jsonl et state-checkpoints.jsonl decrivent les references, les champs invariants et les etats reutilises. Lire les fichiers avec TrainStationData/read_capture.py : CaptureReader(racine).iter_records(cheminRelatif) et read_json(cheminRelatif) reconstruisent les objets logiques complets de ce format. Le lecteur traite uniquement ce nouveau format de collecte.\n" +
            "skeleton-definitions.jsonl definit une fois les rigs : noms et ordre des articulations Humanoid, liens parentaux, chemins, coordonnees monde Unity et metres. skeleton-bindings.jsonl relie chaque pose au rig/avatar/slot effectif sans confondre les agents logiques. skeleton-poses.bin conserve les XYZ float32 et les bits de validite des 55 articulations, avec syncSampleId et sampleId. Aucune rotation d’os, keypoint 2D ou visibilite image n’est calculee. Le lecteur fourni decode ce flux ; skeleton-index.jsonl contient un point d’acces toutes les 256 poses.\n" +
            "sync.jsonl est toujours present. Chaque ligne clock conserve une fois l'horodatage complet original, UTC comprise. Les autres observations utilisent syncSampleId ; la cle complete est runId du manifeste + syncSampleId. Le lecteur restitue captureStamp ou stamp a partir de cette horloge sans interpolation.\n" +
            "simulationSeconds mesure le temps simule ecoule; stationClockSeconds compte les secondes depuis minuit a la date simulationDate. realElapsedSeconds est le temps reel monotone depuis le debut de cette collecte, pauses comprises. utc utilise l'heure UTC. unityFrame, paused et timeScale decrivent l'etat du moteur.\n" +
            "La capture tente une image a chaque frame Unity. captureOptions.videoFps et les en-tetes AVI indiquent uniquement une cadence nominale de lecture ; ils ne pilotent pas la capture et ne mesurent pas son debit.\n" +
            "Chaque image sauvegardee conserve captureIntervalRealSeconds et captureFpsInstantaneous depuis les horodatages de rendu originaux (null pour la premiere image). captureFpsRolling1s compte les images rendues dans (instant-1s, instant], divise par une seconde reelle. La premiere seconde peut donc etre partielle. Les lignes measured_frame_rates de sync.jsonl enregistrent environ chaque seconde les FPS reels du jeu et des videos, dans la meme horloge.\n" +
            "Le temps de lecture AVI ne correspond pas directement au temps simule. Chaque ligne video/<camera>/frames.jsonl relie segment + videoFrame (numerotation a partir de zero) a syncSampleId et cameraStateId. Le fichier camera-states.jsonl du meme dossier conserve les matrices completes une fois par etat exact, avec effectiveFromSyncSampleId ; cet etat est ecrit avant la premiere image qui le reference. Une camera deplacee ou une projection modifiee cree une nouvelle revision, meme si elle revient ensuite a une pose anterieure. La cadence nominale du fichier est constante; les images abandonnees ne sont pas inserees. Une pause peut produire des poses identiques a plusieurs instants reels.\n" +
            "Le segment N est segment-NNNN.avi (MJPEG, sans audio). Un nouveau segment commence apres 5 minutes d'images encodees ou avant 900 Mo. Toutes les cameras partagent la meme horloge. Les marqueurs runtime_gap et video_frame_dropped signalent les observations manquantes; aucune image non observee n'est inventee.\n" +
            "Les lots refusés faute de capacité ne sont pas rejoués. capture_capacity_pressure regroupe leurs nombres et bornes temporelles ; les compteurs du manifeste distinguent les refus liés aux données et aux caméras. stopRequestedRealElapsedSeconds marque la demande d’arrêt ; finalizationRealSeconds mesure la fermeture restante, incluse dans realDurationSeconds.\n" +
            "calibration.json et les matrices des etats de camera utilisent les positions Unity en metres: +Y vers le haut, +Z vers l'avant. Les matrices 4x4 sont stockees par colonnes. L'origine des images est en haut a gauche. Les captures excluent les Canvas et la couche UI.\n" +
            "Les videos calibrees utilisent un modele stenope ideal. Le post-traitement est desactive uniquement pendant le rendu de capture, puis son reglage initial est retabli. K est une matrice 3x3 stockee par lignes; les matrices extrinseques et de projection 4x4 sont stockees par colonnes. Le centre du pixel superieur gauche est (0,0): u=(NDC.x+1)*width/2-0.5, v=(1-NDC.y)*height/2-0.5. Utiliser la projection CPU pour la geometrie; la projection GPU inclut les conventions de la cible de rendu.\n" +
            "worldToCvCameraMatrix transforme directement les positions Unity en coordonnees camera CV (+X a droite, +Y vers le bas, +Z vers l'avant). Le monde Unity est gauche; la vue Unity utilise -Z vers l'avant. Pour un monde droit, inverser d'abord le Z du monde et multiplier l'extrinseque exportee par diag(1,1,-1,1).\n" +
            "Lorsque les trajectoires, animations ou squelettes sont selectionnes, chaque image video possede un marqueur avatar_snapshot avec la liste exacte des voyageurs actifs et les donnees correspondantes au meme instant, y compris pendant une pause.\n" +
            "calibrationVersion nomme le modele geometrique et ne remplace pas cameraStateId, qui identifie un etat immuable dans cette camera et cette collecte. Les valeurs FPS variables restent presentes dans chaque observation video. Un fichier individuel absent peut correspondre a une option desactivee ou a un flux sans observation encore ecrite ; consulter les options du manifeste et l’index. L'etat complete du manifeste confirme la fermeture de la collecte, et non la fin de toute la journee simulee. Apres un arret brutal, un manifeste recording ou un fichier .index.tmp signale une session non finalisee: la conserver pour recuperation et ne pas la traiter comme complete.\n",
            new UTF8Encoding(false));
        void TryWriteManifest() { try { WriteManifest(); } catch { /* Preserve the original I/O failure. */ } }
        static StreamWriter OpenText(string path) => new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 32768);
        static string SafeName(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        static float[] Elements(Matrix4x4 value) { var result = new float[16]; for (int i = 0; i < 16; ++i) result[i] = value[i]; return result; }
        static float[] Intrinsics(Matrix4x4 projection, int width, int height) => new[]
        {
            width * projection.m00 * .5f, -width * projection.m01 * .5f, width * (1f - projection.m02) * .5f - .5f,
            -height * projection.m10 * .5f, height * projection.m11 * .5f, height * (1f + projection.m12) * .5f - .5f,
            0f, 0f, 1f
        };
        static float[] InverseIntrinsics(float[] k)
        {
            float det = k[0] * k[4] - k[1] * k[3];
            if (Mathf.Abs(det) < .000001f) throw new InvalidOperationException("The capture projection has singular pixel intrinsics.");
            return new[] { k[4] / det, -k[1] / det, (k[1] * k[5] - k[4] * k[2]) / det,
                -k[3] / det, k[0] / det, (k[3] * k[2] - k[0] * k[5]) / det, 0f, 0f, 1f };
        }
        static float ProjectionFov(float scale, float offset) => (Mathf.Atan((1f + offset) / scale) - Mathf.Atan((-1f + offset) / scale)) * Mathf.Rad2Deg;
        static Matrix4x4 ViewToCv => Matrix4x4.Scale(new Vector3(1f, -1f, -1f));

        [Serializable] sealed class SyncRecord { public string kind, detail; public string[] activeJourneyIds; public StationDecisionCaptureStamp stamp; public StationDecisionMeasuredFrameRates measuredFrameRates; public CapturePressure capturePressure; }
        [Serializable] sealed class CapturePressure
        {
            public string reason;
            public int firstRejectedUnityFrame, lastRejectedUnityFrame, maxPendingDataCommands;
            public double firstRejectedRealElapsedSeconds, lastRejectedRealElapsedSeconds;
            public long dataDeniedBatches, videoDeniedBatches, maxEstimatedBatchBytes, maxPendingDataBytes;
            public long partialCameraDeniedRequests, partialVideoBatches;
        }
        [Serializable] sealed class Manifest
        {
            public int readbackSlotCount;
            public bool commonBatchAdmission;
            public string renderProfile, dataWriterMode;
            public double dataInitializationWaitRealSeconds;
            public int dataQueueMaxPendingCommands, dataQueueHighWaterCommands;
            public long dataQueueMaxRetainedBytes, dataQueueHighWaterRetainedBytes, dataCommandsAccepted, dataCommandsCompleted;
            public long dataRecordsAccepted, dataRecordsCompleted;
            public string captureAdmissionPolicy;
            public int dataEssentialHeadroomCommands;
            public long dataEssentialHeadroomBytes, optionalAdmissionBudgetBytes, optionalAdmissionBudgetOverrideBytes;
            public long captureBatchesRequested, captureBatchesAdmitted, captureBatchesRejectedData, captureBatchesRejectedVideo;
            public double stopRequestedRealElapsedSeconds, finalizationRealSeconds;
            public string schema, runId, createdUtc, stoppedUtc, operatingDate, scene, unityVersion, platform, storageRoot, status, stopReason,
                synchronization, videoFormat, syncFile, scenarioFile, videoDirectory, localTimeZoneId, launchMetadataJson,
                applicationProductName, applicationVersion, applicationBuildGuid, captureCadence, actualVideoRateDefinition,
                storageLayout, agentsDirectory, agentIndexFile, recordEncoding, normalizationFile,
                recordDefinitionsFile, rostersFile, stateCheckpointsFile, skeletonDefinitionsFile, skeletonPoseFileName, skeletonBindingsFileName, skeletonIndexFileName, skeletonPoseFormat;
            public int simulationSeed, localUtcOffsetMinutes, nominalPlaybackFps;
            public bool videoFrameAvatarSnapshots;
            public long syncSamples;
            public double startSimulationSeconds, endSimulationSeconds, startClockSeconds, realDurationSeconds;
            public StationDecisionCaptureOptions captureOptions;
            public StationDecisionDataSchemaMetadata dataSchema;
            public string[] selectedCameraIds, errors;
            public CameraResult[] cameraResults;
        }
        [Serializable] sealed class Calibration
        {
            public string calibrationVersion = "stationflow.pinhole.v1", cameraId, displayName, purpose,
                matrixOrder = "column-major Unity Matrix4x4 indexer; index=column*4+row", intrinsicMatrixOrder = "row-major 3x3; index=row*3+column",
                coordinateSystem = "Unity world: left-handed, metres, +X right, +Y up, +Z forward; Unity view: right-handed, +X right, +Y up, -Z forward; CV camera: +X right, +Y down, +Z forward",
                cvExtrinsicConvention = "worldToCvCameraMatrix = diag(1,-1,-1,1) * worldToCameraMatrix maps original Unity world avatar coordinates directly; to use a right-handed world first flip world Z with S=diag(1,1,-1,1), then extrinsicRH=worldToCvCameraMatrix*S",
                pixelMapping = "JPEG upright; origin top-left pixel centre=(0,0), pixel centres range [0,width-1]x[0,height-1]; u=(NDC.x+1)*width/2-0.5, v=(1-NDC.y)*height/2-0.5; CPU projectionMatrix is authoritative for geometry; gpuProjectionMatrix includes graphics API render-target conventions",
                cameraModel = "ideal-pinhole", distortionModel = "none; post processing disabled only for this capture render request",
                readbackRowNormalization, graphicsApi;
            public int width, height, fps;
            public Vector3 position, rotationEuler;
            public Quaternion rotationQuaternion;
            public float fieldOfView, verticalFieldOfViewDegrees, horizontalFieldOfViewDegrees, aspect, nearClip, farClip;
            public bool orthographic, originalPostProcessing, capturedPostProcessing, readbackRowsFlipped;
            public float[] worldToCameraMatrix, cameraToWorldMatrix, projectionMatrix, inverseProjectionMatrix, gpuProjectionMatrix,
                intrinsicMatrix, inverseIntrinsicMatrix, worldToCvCameraMatrix, cvCameraToWorldMatrix, unityViewToCvCameraMatrix,
                distortionCoefficients = new float[5];
        }
        [Serializable] sealed class CameraResult { public string cameraId, directory, error, cameraStateFile; public long framesWritten, requestsDropped, cameraStatesWritten; public int segments; }
        sealed class CameraMessage { public string kind, detail; }
        sealed class FramePacket
        {
            public byte[] pixels; public string matricesJson;
            public StationDecisionCaptureStamp stamp; public FrameMatrices matrices;
            public long requestId, diagnosticEnqueuedAt, diagnosticReadyAt;
            public double renderRealElapsedSeconds; public bool flipRows;
            public StationDecisionCaptureRing<FramePacket>.Ticket lease;
        }
        [Serializable] sealed class FrameMatrices
        {
            public float[] worldToCamera, cameraToWorld, projection, inverseProjection, gpuProjection, intrinsicMatrix, inverseIntrinsicMatrix, worldToCvCamera, cvCameraToWorld;
            public Vector3 position, rotationEuler;
            public Quaternion rotation;
            public float verticalFieldOfViewDegrees, horizontalFieldOfViewDegrees, nearClip, farClip;
            public bool capturedPostProcessing;
        }

        sealed class CameraStream : IDisposable
        {
            readonly Camera camera;
            readonly UniversalAdditionalCameraData cameraData;
            readonly StationDecisionCaptureRenderProfile renderProfile;
            readonly StationDecisionCaptureOptions options;
            readonly StationDecisionCaptureDiagnostics diagnostics;
            readonly string directory;
            readonly ReadbackSlot[] slots;
            readonly StationDecisionCaptureRing<FramePacket> ring;
            readonly BlockingCollection<FramePacket> packets = new BlockingCollection<FramePacket>(2);
            readonly object pumpGate = new object(), rateSnapshotGate = new object();
            readonly StationDecisionVideoRateTracker actualRate = new StationDecisionVideoRateTracker();
            readonly StationDecisionCameraStateWriter cameraStates = new StationDecisionCameraStateWriter();
            readonly Thread worker;
            readonly bool asyncReadback, backgroundDataSerialization;
            bool flipRows, orientationKnown, stopped, disposed, accepting = true;
            byte[] orientationReference;
            Calibration calibration;
            Texture2D fallback;
            long requestId;
            int segments;
            public readonly ConcurrentQueue<CameraMessage> Messages = new ConcurrentQueue<CameraMessage>();
            public readonly string CameraId;
            public long Written, Dropped;
            public volatile string Failure;
            sealed class ReadbackSlot
            {
                public RenderTexture target;
                public byte[] pixels;
                public AsyncGPUReadbackRequest pending;
                public bool readbackPending;
            }
            public struct Reservation
            {
                internal StationDecisionCaptureRing<FramePacket>.Ticket lease;
                internal long requestId;
                internal string reason, diagnosticReason;
                public bool IsReserved => lease.IsValid;
            }
            public double GetActualFpsAt(double realNow) { lock (rateSnapshotGate) return actualRate.GetFpsAt(realNow); }
            public StationDecisionCameraFrameRate GetRateSnapshotAt(double realNow)
            {
                lock (rateSnapshotGate)
                    return new StationDecisionCameraFrameRate { cameraId = CameraId,
                        videoFpsRolling1s = actualRate.GetFpsAt(realNow), framesWritten = Interlocked.Read(ref Written),
                        requestsDropped = Interlocked.Read(ref Dropped) };
            }
            public CameraStream(StationSurveillanceCamera source, string root, StationDecisionCaptureOptions settings,
                StationDecisionCaptureDiagnostics diagnostics, int slotCount, string profileName, bool backgroundDataSerialization)
            {
                this.diagnostics = diagnostics; this.backgroundDataSerialization = backgroundDataSerialization;
                camera = source.cameraComponent; CameraId = source.cameraId; options = settings.Copy();
                if (camera.orthographic) throw new NotSupportedException("Calibrated CCTV recording requires a perspective camera: " + CameraId);
                ring = new StationDecisionCaptureRing<FramePacket>(slotCount); slots = new ReadbackSlot[slotCount];
                renderProfile = new StationDecisionCaptureRenderProfile(camera, profileName);
                cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
                directory = Path.Combine(root, "video", SafeName(CameraId));
                asyncReadback = SystemInfo.supportsAsyncGPUReadback;
                try
                {
                    Directory.CreateDirectory(directory);
                    for (int i = 0; i < slots.Length; ++i)
                    {
                        var targetForSlot = new RenderTexture(options.videoWidth, options.videoHeight, 24, RenderTextureFormat.ARGB32)
                            { name = "Capture " + CameraId + " slot " + i, hideFlags = HideFlags.HideAndDontSave };
                        slots[i] = new ReadbackSlot { target = targetForSlot };
                        slots[i].pixels = new byte[checked(options.videoWidth * options.videoHeight * 3)];
                        if (!targetForSlot.Create()) throw new IOException("Could not allocate capture target for " + CameraId);
                    }
                    var target = slots[0].target;
                    diagnostics?.SetMetadata(CameraId + ".asyncReadback", asyncReadback.ToString());
                    diagnostics?.SetMetadata(CameraId + ".queueCapacity", packets.BoundedCapacity.ToString(CultureInfo.InvariantCulture));
                    diagnostics?.SetMetadata(CameraId + ".ownedSlotCount", slots.Length.ToString(CultureInfo.InvariantCulture));
                    var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
                    if (!RenderPipeline.SupportsRenderRequest(camera, request)) throw new NotSupportedException("The current render pipeline cannot render installed camera " + CameraId + ".");
                float aspect = camera.aspect; camera.aspect = options.videoWidth / (float)options.videoHeight;
                try
                {
                    var projection = camera.projectionMatrix; var view = camera.worldToCameraMatrix; var cv = ViewToCv * view;
                    var intrinsic = Intrinsics(projection, options.videoWidth, options.videoHeight);
                    calibration = new Calibration
                    { cameraId = CameraId, displayName = source.displayName, purpose = source.purpose, width = options.videoWidth, height = options.videoHeight,
                        fps = options.videoFps, position = camera.transform.position, rotationEuler = camera.transform.eulerAngles,
                        rotationQuaternion = camera.transform.rotation, graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                        fieldOfView = camera.fieldOfView, aspect = camera.aspect, nearClip = camera.nearClipPlane, farClip = camera.farClipPlane,
                        verticalFieldOfViewDegrees = ProjectionFov(projection.m11, projection.m12), horizontalFieldOfViewDegrees = ProjectionFov(projection.m00, projection.m02),
                        originalPostProcessing = cameraData && cameraData.renderPostProcessing, capturedPostProcessing = false,
                        orthographic = camera.orthographic, worldToCameraMatrix = Elements(camera.worldToCameraMatrix), cameraToWorldMatrix = Elements(camera.cameraToWorldMatrix),
                        inverseProjectionMatrix = Elements(projection.inverse), intrinsicMatrix = intrinsic, inverseIntrinsicMatrix = InverseIntrinsics(intrinsic),
                        worldToCvCameraMatrix = Elements(cv), cvCameraToWorldMatrix = Elements(cv.inverse), unityViewToCvCameraMatrix = Elements(ViewToCv),
                        projectionMatrix = Elements(camera.projectionMatrix), gpuProjectionMatrix = Elements(GL.GetGPUProjectionMatrix(camera.projectionMatrix, true)),
                        readbackRowNormalization = asyncReadback ? "pending first-frame comparison to Texture2D.ReadPixels" : "synchronous Texture2D.ReadPixels convention" };
                    WriteCalibration();
                }
                finally { camera.aspect = aspect; }
                if (!asyncReadback) fallback = new Texture2D(options.videoWidth, options.videoHeight, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };

                    worker = new Thread(WriteFrames) { IsBackground = true, Name = "Station video " + CameraId }; worker.Start();
                }
                catch (Exception original)
                {
                    var failures = new List<Exception> { original };
                    foreach (var slot in slots) if (slot != null && slot.target)
                        try { slot.target.Release(); UnityEngine.Object.Destroy(slot.target); } catch (Exception error) { failures.Add(error); }
                    try { if (fallback) UnityEngine.Object.Destroy(fallback); } catch (Exception error) { failures.Add(error); }
                    try { renderProfile.Dispose(); } catch (Exception error) { failures.Add(error); }
                    try { packets.Dispose(); } catch (Exception error) { failures.Add(error); }
                    if (failures.Count > 1) throw new AggregateException("Capture allocation failed; cleanup also reported errors.", failures);
                    throw;
                }
            }
            public bool TryReserve(out Reservation reservation)
            {
                PumpReadyPackets();
                reservation = new Reservation { requestId = requestId++ };
                diagnostics?.Increment(CameraId, "requests_attempted");
                diagnostics?.Record(CameraId, "queue_depth_at_request", "packets", packets.Count);
                diagnostics?.Record(CameraId, "owned_slots_at_request", "slots", ring.OwnedCount);
                diagnostics?.Record(CameraId, "readbacks_in_flight_at_request", "requests", ring.PendingCount);
                if (!accepting || stopped || Failure != null)
                { reservation.reason = "capture_not_accepting"; return false; }
                if (!camera || !camera.gameObject.activeInHierarchy)
                { reservation.reason = "camera_unavailable"; return false; }
                // The first orientation reference must match its own pixels. Admit only that first request until calibrated.
                if (asyncReadback && !orientationKnown && ring.OwnedCount > 0)
                {
                    reservation.reason = "readback_orientation_pending"; reservation.diagnosticReason = "readback_in_flight";
                    diagnostics?.Increment(CameraId, "gate_readback_in_flight_observed"); return false;
                }
                if (ring.TryReserve(out reservation.lease)) return true;
                bool pendingReadback = ring.PendingCount > 0;
                reservation.reason = "readback_or_encoding_slots_busy";
                reservation.diagnosticReason = pendingReadback ? "readback_in_flight" : "owned_slots_busy";
                diagnostics?.Increment(CameraId, pendingReadback ? "gate_readback_in_flight_observed" : "gate_owned_slots_busy_observed");
                return false;
            }
            public void RejectReservation(Reservation reservation, bool emitMessage = true)
            {
                ring.CancelReservation(reservation.lease);
                Drop(reservation.requestId, reservation.reason ?? "common_batch_peer_busy", reservation.diagnosticReason ?? reservation.reason ?? "common_batch_peer_busy", emitMessage);
            }
            public void RejectForDataPressure(Reservation reservation)
            {
                if (ring.CancelReservation(reservation.lease))
                    Drop(reservation.requestId, "data_queue_capacity", "data_queue_capacity", false);
            }
            public void CancelUnusedReservation(Reservation reservation)
            { if (ring.CancelReservation(reservation.lease)) Drop(reservation.requestId, "capture_batch_aborted"); }
            public void RequestFrame(StationDecisionCaptureStamp renderStamp)
            {
                if (TryReserve(out var reservation)) CaptureReserved(reservation, renderStamp);
                else RejectReservation(reservation);
            }
            public void CaptureReserved(Reservation reservation, StationDecisionCaptureStamp renderStamp)
            {
                var slot = slots[reservation.lease.index];
                var target = slot.target;
                var packet = new FramePacket { lease = reservation.lease, stamp = renderStamp, requestId = reservation.requestId,
                    renderRealElapsedSeconds = renderStamp.realElapsedSeconds, pixels = slot.pixels };
                bool submitted = false;
                long preparationStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                float aspect = camera.aspect; Rect rect = camera.rect; int mask = camera.cullingMask;
                bool originalPostProcessing = cameraData && cameraData.renderPostProcessing;
                try
                {
                    ring.Submit(reservation.lease); submitted = true;
                    camera.aspect = options.videoWidth / (float)options.videoHeight; camera.rect = new Rect(0, 0, 1, 1);
                    int uiLayer = LayerMask.NameToLayer("UI"); if (uiLayer >= 0) camera.cullingMask &= ~(1 << uiLayer);
                    var projection = camera.projectionMatrix; var view = camera.worldToCameraMatrix; var cv = ViewToCv * view;
                    var intrinsic = Intrinsics(projection, options.videoWidth, options.videoHeight);
                    packet.matrices = new FrameMatrices { worldToCamera = Elements(view), cameraToWorld = Elements(view.inverse), projection = Elements(projection),
                        inverseProjection = Elements(projection.inverse), intrinsicMatrix = intrinsic, inverseIntrinsicMatrix = InverseIntrinsics(intrinsic), worldToCvCamera = Elements(cv), cvCameraToWorld = Elements(cv.inverse),
                        verticalFieldOfViewDegrees = ProjectionFov(projection.m11, projection.m12), horizontalFieldOfViewDegrees = ProjectionFov(projection.m00, projection.m02), nearClip = camera.nearClipPlane, farClip = camera.farClipPlane,
                        capturedPostProcessing = false, rotationEuler = camera.transform.eulerAngles,
                        gpuProjection = Elements(GL.GetGPUProjectionMatrix(projection, true)), position = camera.transform.position, rotation = camera.transform.rotation };
                    diagnostics?.Elapsed(CameraId, "camera_matrices_snapshot_cpu_wall", preparationStarted);
                    if (!backgroundDataSerialization)
                    {
                        long jsonStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                        packet.matricesJson = JsonUtility.ToJson(packet.matrices);
                        diagnostics?.Elapsed(CameraId, "camera_matrices_json_main_wall", jsonStarted);
                    }
                    try
                    {
                        if (cameraData) cameraData.renderPostProcessing = false;
                        long renderStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                        try { using (renderProfile.Apply()) RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target }); }
                        finally { diagnostics?.Elapsed(CameraId, "render_submit_cpu_wall", renderStarted); }
                        diagnostics?.Increment(CameraId, "renders_submitted");
                    }
                    finally { if (cameraData) cameraData.renderPostProcessing = originalPostProcessing; }
                    if (asyncReadback)
                    {
                        if (!orientationKnown)
                        {
                            long referenceStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                            try { orientationReference = ReadReferencePixels(target); }
                            finally { diagnostics?.Elapsed(CameraId, "first_frame_reference_readpixels_wall", referenceStarted); }
                        }
                        long readbackStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                        int readbackUnityFrame = diagnostics != null ? Time.frameCount : 0;
                        slot.readbackPending = true;
                        slot.pending = AsyncGPUReadback.Request(target, 0, TextureFormat.RGB24,
                            request => ProcessReadback(slot, packet, request, readbackStarted, readbackUnityFrame));
                    }
                    else
                    {
                        var active = RenderTexture.active;
                        long fallbackStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                        try
                        {
                            RenderTexture.active = target; fallback.ReadPixels(new Rect(0, 0, options.videoWidth, options.videoHeight), 0, 0, false);
                            fallback.GetRawTextureData<byte>().CopyTo(slot.pixels); packet.flipRows = false;
                            CompletePacket(packet); PumpReadyPackets();
                        }
                        finally { RenderTexture.active = active; diagnostics?.Elapsed(CameraId, "synchronous_fallback_readpixels_wall", fallbackStarted); }
                    }
                }
                catch (Exception error)
                {
                    if (Failure == null) Failure = error.ToString(); slot.readbackPending = false;
                    Drop(reservation.requestId, "render_error: " + error.Message);
                    if (submitted) ring.Complete(reservation.lease, null); else ring.CancelReservation(reservation.lease);
                    PumpReadyPackets();
                }
                finally { if (camera) { camera.aspect = aspect; camera.rect = rect; camera.cullingMask = mask; } }
            }
            void ProcessReadback(ReadbackSlot slot, FramePacket packet, AsyncGPUReadbackRequest request, long readbackStarted, int readbackUnityFrame)
            {
                long callbackStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                bool completed = false;
                if (diagnostics != null)
                {
                    diagnostics.Elapsed(CameraId, "readback_request_to_callback_wall", readbackStarted);
                    diagnostics.Record(CameraId, "readback_callback_unity_frame_gap", "frames", Time.frameCount - readbackUnityFrame);
                    diagnostics.Increment(CameraId, "readback_callbacks");
                }
                try
                {
                    if (request.hasError) throw new IOException("GPU readback failed.");
                    if (Failure != null) { Drop(packet.requestId, "capture_failed_before_readback"); ring.Complete(packet.lease, null); completed = true; return; }
                    long copyStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                    request.GetData<byte>().CopyTo(slot.pixels);
                    diagnostics?.Elapsed(CameraId, "readback_callback_pixel_copy_wall", copyStarted);
                    if (!orientationKnown && orientationReference != null)
                    {
                        long orientationStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                        long direct = RowDifference(packet.pixels, orientationReference, false), reversed = RowDifference(packet.pixels, orientationReference, true);
                        flipRows = reversed < direct; orientationKnown = true; orientationReference = null;
                        calibration.readbackRowsFlipped = flipRows;
                        calibration.readbackRowNormalization = (flipRows ? "rows reversed" : "unchanged") + "; measured against first-frame Texture2D.ReadPixels; directError=" + direct + "; reversedError=" + reversed;
                        WriteCalibration(); diagnostics?.Elapsed(CameraId, "first_frame_orientation_and_calibration_wall", orientationStarted);
                    }
                    packet.flipRows = flipRows;
                    CompletePacket(packet); completed = true;
                }
                catch (Exception error)
                {
                    if (Failure == null) Failure = error.ToString(); Drop(packet.requestId, request.hasError ? "gpu_readback_error" : "readback_processing_error: " + error.Message);
                    if (!completed) ring.Complete(packet.lease, null);
                }
                finally
                {
                    slot.readbackPending = false; PumpReadyPackets();
                    diagnostics?.Elapsed(CameraId, "readback_callback_total_wall", callbackStarted);
                }
            }
            void CompletePacket(FramePacket packet)
            {
                if (diagnostics != null) packet.diagnosticReadyAt = Stopwatch.GetTimestamp();
                ring.Complete(packet.lease, packet);
            }
            public void PumpReadyPackets()
            {
                lock (pumpGate)
                {
                    while ((Failure != null || packets.Count < packets.BoundedCapacity) && ring.TryTakeReady(out var lease, out var packet))
                    {
                        if (Failure != null || packets.IsAddingCompleted)
                        { Drop(packet.requestId, "capture_failed_before_write"); ring.Release(lease); continue; }
                        if (diagnostics != null)
                        {
                            diagnostics.Elapsed(CameraId, "ordered_ready_wait_wall", packet.diagnosticReadyAt);
                            packet.diagnosticEnqueuedAt = Stopwatch.GetTimestamp();
                        }
                        // This is the only producer; workers only remove. Capacity cannot shrink between Count and TryAdd.
                        if (!packets.TryAdd(packet))
                        { ring.Release(lease); Drop(packet.requestId, "encoding_queue_full", "enqueue_full"); }
                        else diagnostics?.Increment(CameraId, "packets_enqueued");
                        diagnostics?.Record(CameraId, "queue_depth_after_enqueue_attempt", "packets", packets.Count);
                    }
                }
            }
            void Drop(long id, string why, string diagnosticReason = null, bool emitMessage = true)
            {
                Interlocked.Increment(ref Dropped);
                if (diagnostics != null)
                {
                    diagnostics.Increment(CameraId, "dropped_total");
                    diagnostics.Increment(CameraId, "drop_" + (diagnosticReason ?? why.Split(':')[0]));
                }
                if (emitMessage) Messages.Enqueue(new CameraMessage { kind = "video_frame_dropped", detail = "requestId=" + id + "; " + why });
            }
            void WriteFrames()
            {
                StationMjpegAviWriter avi = null;
                try
                {
                    using (var timestamps = OpenText(Path.Combine(directory, "frames.jsonl")))
                    using (var states = OpenText(Path.Combine(directory, StationDecisionCameraStateWriter.FileName)))
                    {
                        foreach (var packet in packets.GetConsumingEnumerable())
                        {
                            try
                            {
                            if (diagnostics != null)
                            {
                                diagnostics.Elapsed(CameraId, "packet_queue_wait_wall", packet.diagnosticEnqueuedAt);
                                diagnostics.Record(CameraId, "queue_depth_at_worker_dequeue", "packets", packets.Count);
                            }
                            if (packet.flipRows)
                            {
                                long flipStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                                FlipRows(packet.pixels, options.videoWidth, options.videoHeight);
                                diagnostics?.Elapsed(CameraId, "pixel_row_flip_worker_wall", flipStarted);
                            }
                            if (backgroundDataSerialization)
                            {
                                // Only immutable render-time DTOs cross this boundary; camera/Transform reads stay on the main thread.
                                long jsonStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                                packet.matricesJson = JsonUtility.ToJson(packet.matrices);
                                diagnostics?.Elapsed(CameraId, "video_metadata_json_worker_wall", jsonStarted);
                            }
                            // Compare exact exported state, not an epsilon or a camera-name assumption. The bounded
                            // cache retains only its last value; revisiting an earlier pose creates a new revision.
                            long stateId = cameraStates.Reference(states, packet.stamp.syncSampleId, packet.matricesJson);
                            long encodeStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                            byte[] jpeg = ImageConversion.EncodeArrayToJPG(packet.pixels, GraphicsFormat.R8G8B8_UNorm, (uint)options.videoWidth, (uint)options.videoHeight, 0, options.jpegQuality);
                            diagnostics?.Elapsed(CameraId, "jpeg_encode_worker_wall", encodeStarted);
                            diagnostics?.Record(CameraId, "encoded_jpeg_size", "bytes", jpeg == null ? 0 : jpeg.Length);
                            if (jpeg == null || jpeg.Length == 0) throw new IOException("The JPEG encoder returned no image data.");
                            if (avi == null || avi.FrameCount >= options.videoFps * 300 || avi.Length + jpeg.Length > 900000000L)
                            {
                                long segmentStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                                avi?.Dispose(); avi = new StationMjpegAviWriter(Path.Combine(directory, "segment-" + (++segments).ToString("0000") + ".avi"), options.videoWidth, options.videoHeight, options.videoFps);
                                diagnostics?.Elapsed(CameraId, "avi_segment_rotation_io_wall", segmentStarted);
                            }
                            long ioStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                            long appendStarted = ioStarted;
                            int frame = avi.AppendJpeg(jpeg);
                            diagnostics?.Elapsed(CameraId, "avi_append_io_wall", appendStarted);
                            // A telemetry sample sees a complete saved prefix: rate history, sidecar and frame count agree.
                            lock (rateSnapshotGate)
                            {
                                var measuredRate = actualRate.Add(packet.renderRealElapsedSeconds);
                                long sidecarStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                                timestamps.WriteLine(StationDecisionCameraStateWriter.FrameLine(segments, frame, frame / (double)options.videoFps,
                                    packet.requestId, packet.stamp.syncSampleId, stateId, measuredRate));
                                diagnostics?.Elapsed(CameraId, "sidecar_write_io_wall", sidecarStarted);
                                long flushStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                                timestamps.Flush();
                                diagnostics?.Elapsed(CameraId, "sidecar_flush_io_wall", flushStarted);
                                diagnostics?.Elapsed(CameraId, "avi_and_sidecar_total_io_wall", ioStarted);
                                diagnostics?.Increment(CameraId, "frames_written");
                                Interlocked.Increment(ref Written);
                            }
                            }
                            catch { Drop(packet.requestId, "video_encoder_failed"); throw; }
                            finally { ring.Release(packet.lease); PumpReadyPackets(); }
                        }
                    }
                }
                catch (Exception error) { Failure = error.ToString(); Messages.Enqueue(new CameraMessage { kind = "video_encoder_failed", detail = error.Message }); }
                finally
                {
                    long finalizeStarted = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
                    try { avi?.Dispose(); } catch (Exception error) { Failure = error.ToString(); }
                    finally { diagnostics?.Elapsed(CameraId, "avi_finalization_io_wall", finalizeStarted); }
                    while (packets.TryTake(out var abandoned))
                    { Drop(abandoned.requestId, "encoder_failed_before_write"); ring.Release(abandoned.lease); }
                    PumpReadyPackets();
                }
            }

            static void FlipRows(byte[] pixels, int width, int height)
            {
                int stride = width * 3;
                for (int y = 0; y < height / 2; ++y)
                    for (int x = 0; x < stride; ++x) { int a = y * stride + x, b = (height - 1 - y) * stride + x; byte value = pixels[a]; pixels[a] = pixels[b]; pixels[b] = value; }
            }
            byte[] ReadReferencePixels(RenderTexture target)
            {
                var texture = new Texture2D(options.videoWidth, options.videoHeight, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
                var active = RenderTexture.active;
                try
                {
                    RenderTexture.active = target;
                    texture.ReadPixels(new Rect(0, 0, options.videoWidth, options.videoHeight), 0, 0, false);
                    return texture.GetRawTextureData<byte>().ToArray();
                }
                finally { RenderTexture.active = active; UnityEngine.Object.Destroy(texture); }
            }
            long RowDifference(byte[] pixels, byte[] reference, bool reverse)
            {
                long result = 0; int stride = options.videoWidth * 3;
                if (pixels.Length != reference.Length) throw new IOException("GPU pixel count differs from the capture dimensions.");
                for (int y = 0; y < options.videoHeight; y += Math.Max(1, options.videoHeight / 20))
                    for (int x = 0; x < options.videoWidth; x += Math.Max(1, options.videoWidth / 20))
                        for (int c = 0; c < 3; ++c)
                            result += Math.Abs(pixels[(reverse ? options.videoHeight - 1 - y : y) * stride + x * 3 + c] - reference[y * stride + x * 3 + c]);
                return result;
            }
            void WriteCalibration() => File.WriteAllText(Path.Combine(directory, "calibration.json"), JsonUtility.ToJson(calibration, true), new UTF8Encoding(false));

            public void BeginStop()
            {
                accepting = false; ring.CloseAdmission();
            }
            public bool TryCompleteStop()
            {
                if (stopped) return true;
                BeginStop();
                if (!worker.IsAlive && Failure == null && ring.HasUnqueued)
                    Failure = "Video worker exited before draining owned frames.";
                PumpReadyPackets();
                // Unity delivers readback callbacks on subsequent frames, even while simulation time is paused.
                if (slots.Any(slot => slot.readbackPending) || ring.HasUnqueued) return false;
                lock (pumpGate) if (!packets.IsAddingCompleted) packets.CompleteAdding();
                if (worker.IsAlive) return false;
                stopped = true; return true;
            }
            void CompleteReadbackForShutdown()
            {
                BeginStop();
                List<Exception> failures = null;
                foreach (var slot in slots) if (slot.readbackPending)
                    try { slot.pending.WaitForCompletion(); }
                    catch (Exception error) { if (failures == null) failures = new List<Exception>(); failures.Add(error); }
                PumpReadyPackets();
                if (failures != null) throw new AggregateException("Could not complete every owned GPU readback.", failures);
            }
            public void CompleteStopForShutdown()
            {
                if (stopped) return;
                CompleteReadbackForShutdown();
                while (!TryCompleteStop()) Thread.Sleep(1);
                worker.Join();
            }
            public CameraResult Result() => new CameraResult { cameraId = CameraId, directory = "video/" + SafeName(CameraId), error = Failure,
                framesWritten = Interlocked.Read(ref Written), requestsDropped = Interlocked.Read(ref Dropped), segments = segments,
                cameraStateFile = StationDecisionCameraStateWriter.FileName, cameraStatesWritten = cameraStates.StateCount };
            public void Dispose()
            {
                if (disposed) return; if (!stopped) CompleteStopForShutdown(); disposed = true;
                List<Exception> failures = null;
                foreach (var slot in slots)
                    try { slot.target.Release(); UnityEngine.Object.Destroy(slot.target); }
                    catch (Exception error) { if (failures == null) failures = new List<Exception>(); failures.Add(error); }
                try { if (fallback) UnityEngine.Object.Destroy(fallback); }
                catch (Exception error) { if (failures == null) failures = new List<Exception>(); failures.Add(error); }
                try { renderProfile.Dispose(); }
                catch (Exception error) { if (failures == null) failures = new List<Exception>(); failures.Add(error); }
                try { packets.Dispose(); }
                catch (Exception error) { if (failures == null) failures = new List<Exception>(); failures.Add(error); }
                if (failures != null) throw new AggregateException("Capture resource cleanup reported errors.", failures);
            }
        }
    }
}
