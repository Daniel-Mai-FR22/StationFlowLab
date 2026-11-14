#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using StationFlow.TrainStation.RailOperations;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Two real Play launches and two explicitly selected capture sessions. Does not edit the saved scene or skip the runtime clock.</summary>
    [InitializeOnLoad]
    public static class StationDecisionCollectionVerification
    {
        const string Key = "StationFlow.TrainStation.CollectionVerification";
        const string ScenePath = "Assets/TrainStation/Scenes/TrainStationDaily.unity";
        static readonly string[] HallCameraIds = {"HALL-01","HALL-02","HALL-03","HALL-04"};
        public const string ReportPath = "Reports/TrainStationDaily/RuntimeInitialization/collection-play-check.json";
        static Run run;
        static StationDecisionWorld world;
        static double nextPoll, nextSave;

        [Serializable] public sealed class FileEvidence { public string path, before, after; }
        [Serializable] public sealed class CaptureEvidence
        {
            public string directory, captureRunId, manifestStatus;
            public bool closed, videoHeaderValid, frameSyncJoined, allOptionalFilesPresent, allOptionalFilesAbsent;
            public int syncRecords, frameRecords, pausedVideoFrames, aviFiles, measuredPerformanceSamples, pausedPerformanceSamples;
            public long videoFrames, videoDrops;
            public int sharedVideoSyncSamples, registeredAgents, agentProfileFiles, materializedAgentDirectories;
            public bool distinctHallCameraPoses;
            public string[] selectedCameraIds;
            public List<CameraEvidence> cameras = new List<CameraEvidence>();
        }
        [Serializable] public sealed class CameraEvidence { public string cameraId,calibrationPath; public bool calibrationValid; public int frames,pausedFrames; }
        [Serializable] public sealed class Run
        {
            public bool success, initialPaused, localDateChecked, runtimeClonesChecked, pauseChecked, secondLaunchChecked, filesPreserved;
            public bool speedResetAtStart, speedLockedWhileRunning, speedLockedWhilePaused, speedUnlockedAfterStop, speedResetFromRunning8;
            public string startedUtc, completedUtc, phase, archivePath, oldPlayScene;
            public string firstLaunchId, secondLaunchId, firstScheduleFingerprint, secondScheduleFingerprint, operatingDate;
            public int firstSeed, secondSeed, initialServices, secondServices, initializeFrame;
            public long deadlineTicks;
            public double phaseStarted, pausedSimulationSeconds, pauseFirstRealSeconds;
            public long pauseFirstSyncId;
            public double pausedEndSimulationSeconds, pauseLastRealSeconds;
            public bool oldOptionsEnabled, oldPaused, oldBackground;
            public int oldOptions, oldFps, oldVsync;
            public float oldScale;
            public CaptureEvidence fullCapture = new CaptureEvidence(), minimalCapture = new CaptureEvidence();
            public List<FileEvidence> files = new List<FileEvidence>();
            public List<string> errors = new List<string>();
            public string scope = "Saved daily scene, fresh generated current local date/seed on two actual Play launches, four fixed main-hall cameras HALL-01 through HALL-04 at854x480 with automatic per-Unity-frame capture and separately labelled nominal AVI playback30fps, per-camera calibration and shared runtime frame stamps,10real seconds capture,1real second shared-clock pause,5real seconds resumed, then a second minimal capture with all optional streams disabled. Checks per-agent index/profile routing without flat duplicates. Checks x4 and running x8 reset to x1, acceleration rejection while running/paused, resume at x1, unlock after Stop. No simulation time skipping.";
        }
        [Serializable] sealed class CaptureManifest { public string status,captureCadence,storageLayout,agentIndexFile,agentsDirectory; public int nominalPlaybackFps; public string[] errors,selectedCameraIds; public StationDecisionCaptureOptions captureOptions; public CameraResult[] cameraResults; }
        [Serializable] sealed class AgentProfileSnapshot { public StationDecisionCaptureStamp captureStamp; public AgentIdentity profile; public JourneyIdentity journey; }
        [Serializable] sealed class AgentIdentity { public string agentId; }
        [Serializable] sealed class JourneyIdentity { public string journeyId; }
        [Serializable] sealed class CameraResult { public string cameraId; public long framesWritten, requestsDropped; }
        [Serializable] sealed class SyncRecord { public string kind; public StationDecisionCaptureStamp stamp; public StationDecisionMeasuredFrameRates measuredFrameRates; }
        [Serializable] sealed class VideoFrame { public string kind; public StationDecisionCaptureStamp stamp; public FrameCamera camera; }
        [Serializable] sealed class FrameCamera
        { public bool capturedPostProcessing; public float[] worldToCamera,cameraToWorld,projection,inverseProjection,gpuProjection,intrinsicMatrix,inverseIntrinsicMatrix,worldToCvCamera,cvCameraToWorld; }
        [Serializable] sealed class CameraCalibration
        {
            public string calibrationVersion,cameraModel,cameraId; public int width,height,fps; public bool orthographic,capturedPostProcessing;
            public float[] worldToCameraMatrix,cameraToWorldMatrix,projectionMatrix,inverseProjectionMatrix,gpuProjectionMatrix,intrinsicMatrix,inverseIntrinsicMatrix,
                worldToCvCameraMatrix,cvCameraToWorldMatrix,unityViewToCvCameraMatrix,distortionCoefficients;
        }
        [Serializable] sealed class ServiceFingerprint { public StationRailService[] services; }

        static StationDecisionCollectionVerification()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            Application.logMessageReceived += OnLog;
        }

        public static void BeginPlayCheck()
        {
            Require(string.IsNullOrEmpty(SessionState.GetString(Key, "")), "A collection integration check is already pending.");
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling, "Stop Play before collection verification.");
            for (int i = 0; i < SceneManager.sceneCount; ++i) Require(!SceneManager.GetSceneAt(i).isDirty, "Save dirty scenes before collection verification.");
            if (SceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
            run = new Run { startedUtc = DateTime.UtcNow.ToString("O"), phase = "entering-first", deadlineTicks = DateTime.UtcNow.AddMinutes(8).Ticks,
                archivePath = "Reports/TrainStationDaily/RuntimeInitialization/collection-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json",
                oldPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene), oldOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled,
                oldOptions = (int)EditorSettings.enterPlayModeOptions, oldPaused = EditorApplication.isPaused, oldBackground = Application.runInBackground,
                oldFps = Application.targetFrameRate, oldVsync = QualitySettings.vSyncCount, oldScale = Time.timeScale, files = SnapshotFiles() };
            Save(); WriteReport(); EditorSettings.enterPlayModeOptionsEnabled = false;
            EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            EditorApplication.isPaused = false; EditorApplication.isPlaying = true;
        }

        static void Initialize(bool second)
        {
            Require(SceneManager.GetActiveScene().path == ScenePath, "Play opened the wrong daily scene.");
            world = Components<StationDecisionWorld>().Single();
            var arrivals = Components<StationDecisionArrivalController>().Single();
            Require(world.rail && world.rail.IsReady && arrivals.IsReady, "Generated railway or arrival plan did not initialize.");
            Require(world.RunSession != null && world.CaptureSession, "Runtime launch session or capture component is missing.");
            Require(world.Paused && world.rail.IsPaused && Math.Abs(world.ElapsedSeconds) < .001, "The daily simulation advanced before data-collection Start.");
            Require(!world.CaptureSession.IsRecording, "A capture started before explicit selection.");
            Require(world.rail.schedule.Validate().Count == 0 && world.rail.ValidateConfiguration().Count == 0, "Generated railway is invalid.");
            string today = DateTimeOffset.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            Require(world.RunSession.operatingDateIso == today && world.rail.schedule.operatingDateIso == today && arrivals.scenario.scenarioDate == today,
                "Launch date does not match the running computer's local date.");
            Require(!EditorUtility.IsPersistent(world.rail.schedule) && !EditorUtility.IsPersistent(arrivals.scenario), "A persistent scenario or schedule is in use during launch randomization.");
            Application.runInBackground = true; Application.targetFrameRate = 30; QualitySettings.vSyncCount = 0;
            if (second)
            {
                run.secondLaunchId = world.RunSession.runId; run.secondSeed = world.RunSession.seed;
                run.secondScheduleFingerprint = ScheduleFingerprint(world); run.secondServices = world.rail.schedule.services.Length;
                Require(run.secondLaunchId != run.firstLaunchId && run.secondSeed != run.firstSeed && run.secondScheduleFingerprint != run.firstScheduleFingerprint,
                    "A fresh Play launch reused its seed, UUID or complete timetable.");
                run.secondLaunchChecked = true; run.phase = "exiting-final"; Save(); WriteReport(); EditorApplication.isPlaying = false; return;
            }
            run.initialPaused = run.localDateChecked = run.runtimeClonesChecked = true;
            run.operatingDate = today; run.firstLaunchId = world.RunSession.runId; run.firstSeed = world.RunSession.seed;
            run.firstScheduleFingerprint = ScheduleFingerprint(world); run.initialServices = world.rail.schedule.services.Length;
            var cameras = world.CaptureSession.AvailableCameras;
            Require(HallCameraIds.All(id=>cameras.Count(c=>c.cameraId==id)==1), "All four authored fixed main-hall cameras HALL-01 through HALL-04 are required for the video fixture.");
            var options = new StationDecisionCaptureOptions { video = true, timetable = true, profiles = true, trajectories = true,
                animations = true, decisions = true, events = true, selectedCameraIds = (string[])HallCameraIds.Clone(), videoWidth = 854, videoHeight = 480, videoFps = 30 };
            world.SetSimulationSpeed(4f);
            Require(!world.SimulationSpeedLocked && world.SimulationSpeed==4f && Time.timeScale==0f,"Observation speed must be selectable before collection starts.");
            Require(world.BeginDataCollection(options), "Full capture could not start: " + world.CaptureSession.LastError);
            Require(world.SimulationSpeedLocked && world.SimulationSpeed==1f && Time.timeScale==1f,"Start did not reset paused x4 simulation to x1.");
            run.speedResetAtStart=true;
            foreach(float requested in new[]{4f,8f})
            {
                world.SetSimulationSpeed(requested);
                Require(world.SimulationSpeed==1f && Time.timeScale==1f,"Acceleration was accepted during collection.");
            }
            run.speedLockedWhileRunning=true;
            run.fullCapture.directory = world.OutputDirectory; run.fullCapture.captureRunId = world.CaptureSession.RunId;
            Require(world.CaptureSession.Options.animations && world.CaptureSession.Options.video && world.CaptureSession.Options.selectedCameraIds.SequenceEqual(HallCameraIds),
                "The requested capture options were not retained.");
            if (world.Paused) world.TogglePause();
            run.phaseStarted = EditorApplication.timeSinceStartup; run.phase = "recording-first"; Save();
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return; nextPoll = EditorApplication.timeSinceStartup + .05;
            Load(); if (run == null || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            try
            {
                Require(DateTime.UtcNow.Ticks < run.deadlineTicks, "Collection integration check wall-clock timeout.");
                if (run.phase == "restore" && !EditorApplication.isPlayingOrWillChangePlaymode) { Restore(); return; }
                if (run.phase == "enter-second" && !EditorApplication.isPlayingOrWillChangePlaymode)
                { run.phase = "entering-second"; Save(); EditorApplication.isPlaying = true; return; }
                if ((run.phase == "initialize-first" || run.phase == "initialize-second") && EditorApplication.isPlaying && Time.frameCount >= run.initializeFrame)
                    Initialize(run.phase == "initialize-second");
                else if (EditorApplication.isPlaying && (run.phase == "recording-first" || run.phase == "recording-paused" || run.phase == "recording-resumed" || run.phase == "recording-minimal"))
                {
                    Require(world && world.CaptureSession, "Runtime objects disappeared during recording.");
                    Require(world.CaptureSession.IsRecording, "Capture stopped unexpectedly: " + world.CaptureSession.LastError);
                    Require(world.SimulationSpeedLocked && world.SimulationSpeed==1f && Time.timeScale==(world.Paused?0f:1f),"Collection speed lock drifted during a runtime frame.");
                    double duration = EditorApplication.timeSinceStartup - run.phaseStarted;
                    if (run.phase == "recording-first" && duration >= 10)
                    {
                        Require(world.ElapsedSeconds > 0 && world.CaptureSession.VideoFramesWritten > 0, "The simulation or selected camera produced no runtime evidence.");
                        world.TogglePause();
                        foreach(float requested in new[]{4f,8f}) world.SetSimulationSpeed(requested);
                        Require(world.Paused && world.SimulationSpeedLocked && world.SimulationSpeed==1f && Time.timeScale==0f,"Paused collection accepted acceleration or unpaused.");
                        run.speedLockedWhilePaused=true;
                        var stamp = world.CaptureSession.GetStamp();
                        Require(stamp != null && stamp.paused, "Paused runtime stamp was not written.");
                        run.pausedSimulationSeconds = world.ElapsedSeconds; run.pauseFirstRealSeconds = stamp.realElapsedSeconds; run.pauseFirstSyncId = stamp.syncSampleId;
                        run.phase = "recording-paused"; run.phaseStarted = EditorApplication.timeSinceStartup; Save();
                    }
                    else if (run.phase == "recording-paused" && duration >= 1)
                    {
                        var stamp = world.CaptureSession.GetStamp(); run.pausedEndSimulationSeconds = world.ElapsedSeconds; run.pauseLastRealSeconds = stamp.realElapsedSeconds;
                        Require(world.Paused && world.rail.IsPaused && Math.Abs(world.ElapsedSeconds - run.pausedSimulationSeconds) < .001,
                            "Shared simulation clock advanced during Pause.");
                        Require(stamp.paused && stamp.syncSampleId > run.pauseFirstSyncId && stamp.realElapsedSeconds - run.pauseFirstRealSeconds >= .9,
                            "Pause did not retain real-time/simulation-time synchronization samples.");
                        run.pauseChecked = true; world.TogglePause();
                        Require(world.SimulationSpeed==1f && Time.timeScale==1f,"Resume did not preserve x1 recording speed.");
                        run.phase = "recording-resumed"; run.phaseStarted = EditorApplication.timeSinceStartup; Save();
                    }
                    else if (run.phase == "recording-resumed" && duration >= 5)
                    {
                        run.fullCapture.videoFrames = world.CaptureSession.VideoFramesWritten; run.fullCapture.videoDrops = world.CaptureSession.VideoFramesDropped;
                        world.StopDataCollection("integration_full_capture_complete"); world.CaptureSession.CompletePendingStopForShutdown(); VerifyCapture(run.fullCapture,true);
                        Require(!world.SimulationSpeedLocked && world.Paused,"Stop did not unlock speed while retaining Pause.");
                        foreach(float requested in new[]{4f,8f})
                        {
                            world.SetSimulationSpeed(requested);
                            Require(world.SimulationSpeed==requested && Time.timeScale==0f,"Stopped collection did not unlock acceleration.");
                        }
                        run.speedUnlockedAfterStop=true;
                        world.BeginObservation();
                        Require(world.SimulationSpeed==8f && Time.timeScale==8f,"Observation could not resume at x8 after Stop.");
                        var options = new StationDecisionCaptureOptions {video = false,timetable = false,profiles = false,trajectories = false,animations = false,skeletons = false,decisions = false,events = false};
                        Require(world.BeginDataCollection(options), "Minimal capture could not start: " + world.CaptureSession.LastError);
                        Require(world.SimulationSpeedLocked && world.SimulationSpeed==1f && Time.timeScale==1f,"Start from an already running x8 observation did not reset to x1.");
                        run.speedResetFromRunning8=true;
                        run.minimalCapture.directory = world.OutputDirectory; run.minimalCapture.captureRunId = world.CaptureSession.RunId;
                        Require(run.minimalCapture.captureRunId != run.fullCapture.captureRunId && run.minimalCapture.directory != run.fullCapture.directory, "Sequential captures overwrote an existing data session.");
                        if (world.Paused) world.TogglePause();
                        run.phase = "recording-minimal"; run.phaseStarted = EditorApplication.timeSinceStartup; Save();
                    }
                    else if (run.phase == "recording-minimal" && duration >= 2)
                    {
                        world.CaptureSession.GetStamp(); world.StopDataCollection("integration_minimal_capture_complete"); world.CaptureSession.CompletePendingStopForShutdown(); VerifyCapture(run.minimalCapture,false);
                        run.phase = "exiting-first"; Save(); WriteReport(); EditorApplication.isPlaying = false;
                    }
                }
                if (EditorApplication.timeSinceStartup >= nextSave) { nextSave = EditorApplication.timeSinceStartup + 1; Save(); WriteReport(); }
            }
            catch (Exception error)
            {
                Error(error.ToString());
                try { if (world) world.StopDataCollection("integration_failed"); } catch (Exception closeError) { Error(closeError.ToString()); }
                run.phase = EditorApplication.isPlayingOrWillChangePlaymode ? "exiting-final" : "restore"; Save(); WriteReport();
                if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false;
            }
        }

        static void VerifyCapture(CaptureEvidence evidence, bool optional)
        {
            Require(!string.IsNullOrEmpty(evidence.directory) && Directory.Exists(evidence.directory), "Capture output directory is missing.");
            string manifestPath = Path.Combine(evidence.directory,"manifest.json"), syncPath = Path.Combine(evidence.directory,"sync.jsonl");
            Require(File.Exists(manifestPath) && File.Exists(syncPath) && File.Exists(Path.Combine(evidence.directory,"scenario.json")), "Capture identity, scenario or synchronization data is missing.");
            var manifest = JsonUtility.FromJson<CaptureManifest>(File.ReadAllText(manifestPath)); evidence.manifestStatus = manifest.status;
            var captureReader = new StationDecisionCaptureReader(evidence.directory);
            evidence.videoFrames = (manifest.cameraResults ?? Array.Empty<CameraResult>()).Sum(c => c.framesWritten);
            evidence.videoDrops = (manifest.cameraResults ?? Array.Empty<CameraResult>()).Sum(c => c.requestsDropped);
            Require(manifest.status == "complete" && (manifest.errors == null || manifest.errors.Length == 0), "Capture did not close cleanly: " + manifest.status);
            Require(manifest.storageLayout=="per_agent.v3", "New capture did not select the per-agent layout.");
            Require(manifest.captureCadence=="each_unity_frame", "Capture cadence must follow actual Unity frames.");
            var clockStamps = new Dictionary<long,StationDecisionCaptureStamp>();
            foreach (string line in captureReader.ReadLines("sync.jsonl"))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var value = JsonUtility.FromJson<SyncRecord>(line); ++evidence.syncRecords;
                if (value.kind == "measured_frame_rates")
                {
                    Require(value.measuredFrameRates!=null && value.measuredFrameRates.windowSeconds==1d && value.measuredFrameRates.gameFpsRolling1s>0d,"Observed game FPS telemetry is missing or invalid.");
                    Require(value.measuredFrameRates.cameras!=null && value.measuredFrameRates.cameras.Length==(optional?4:0),"Measured video FPS does not match selected cameras.");
                    ++evidence.measuredPerformanceSamples;
                    if(value.stamp!=null && value.stamp.paused)++evidence.pausedPerformanceSamples;
                }
                if (value.kind == "clock" && value.stamp != null) clockStamps[value.stamp.syncSampleId] = value.stamp;
            }
            Require(clockStamps.Values.All(s=>s.timeScale==(s.paused?0f:1f)),"Recorded runtime timestamps contain acceleration during collection.");
            Require(evidence.measuredPerformanceSamples>0,"Observed FPS telemetry was not saved.");
            if(optional)Require(evidence.pausedPerformanceSamples>0,"Real-time FPS telemetry stopped during simulation pause.");
            Require(clockStamps.Count > 0 && clockStamps.Values.All(s => s.runId == evidence.captureRunId), "Synchronization stamps have missing or mixed session identities.");
            string[] optionalFiles = {"timetable.json","train-states.jsonl","profiles.json","trajectories.jsonl","animations.jsonl","decisions.jsonl","events.jsonl"};
            if (optional)
            {
                evidence.selectedCameraIds=manifest.selectedCameraIds;
                Require(manifest.selectedCameraIds!=null && manifest.selectedCameraIds.OrderBy(id=>id,StringComparer.Ordinal).SequenceEqual(HallCameraIds),
                    "The full capture did not retain exactly the four selected main-hall cameras.");
                string[] sharedFiles={"timetable.json","train-states.jsonl","events.jsonl"};
                Require(manifest.agentsDirectory=="agents" && manifest.agentIndexFile=="agents/index.json", "Agent index metadata is missing.");
                var agentIndex=JsonUtility.FromJson<StationDecisionAgentsIndex>(File.ReadAllText(Path.Combine(evidence.directory,manifest.agentIndexFile)));
                Require(agentIndex.runId==evidence.captureRunId && agentIndex.agents!=null && agentIndex.agents.Length==world.arrivalController.GeneratedPlan.Count,"Agent registry does not contain every logical journey.");
                evidence.registeredAgents=agentIndex.agents.Length;
                foreach(var entry in agentIndex.agents)
                {
                    string profilePath=Path.Combine(evidence.directory,entry.directory,"profile.json");
                    Require(File.Exists(profilePath), "Selected agent profile is missing: "+entry.agentId);
                    var personal=JsonUtility.FromJson<AgentProfileSnapshot>(captureReader.ReadJson(entry.directory + "/profile.json"));
                    Require(personal.profile!=null && personal.profile.agentId==entry.agentId && personal.journey!=null && personal.journey.journeyId==entry.agentId && personal.captureStamp!=null && personal.captureStamp.runId==evidence.captureRunId,"Personal profile/plan identity is mixed.");
                    ++evidence.agentProfileFiles;
                }
                evidence.materializedAgentDirectories=Directory.GetDirectories(Path.Combine(evidence.directory,"agents")).Length;
                Require(evidence.materializedAgentDirectories==evidence.registeredAgents,"Materialized profile folders differ from the logical registry.");
                Require(new[]{"profiles.json","trajectories.jsonl","animations.jsonl","decisions.jsonl"}.All(f=>!File.Exists(Path.Combine(evidence.directory,f))),"Flat avatar data duplicates remain.");
                evidence.allOptionalFilesPresent = sharedFiles.All(f => File.Exists(Path.Combine(evidence.directory,f))) && evidence.agentProfileFiles==evidence.registeredAgents;
                Require(evidence.allOptionalFilesPresent, "An explicitly selected optional data file is missing.");
                var avi = Directory.GetFiles(evidence.directory,"*.avi",SearchOption.AllDirectories); evidence.aviFiles = avi.Length;
                Require(avi.Length > 0, "Selected surveillance video did not produce an AVI.");
                foreach (string file in avi)
                {
                    using (var stream = new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
                    using (var reader = new BinaryReader(stream))
                    {
                        Require(stream.Length > 256, "Captured AVI is empty.");
                        Require(Encoding.ASCII.GetString(reader.ReadBytes(4)) == "RIFF", "Video lacks its RIFF header.");
                        uint length = reader.ReadUInt32(); Require(length + 8L == stream.Length && Encoding.ASCII.GetString(reader.ReadBytes(4)) == "AVI ", "AVI header was not finalized on Stop.");
                    }
                }
                evidence.videoHeaderValid = true;
                var cameraSyncSets=new List<HashSet<long>>();
                var cameraPoses=new List<float[]>();
                foreach(string cameraId in HallCameraIds)
                {
                    string directory=Path.Combine(evidence.directory,"video",cameraId),sidecar=Path.Combine(directory,"frames.jsonl"),calibrationPath=Path.Combine(directory,"calibration.json");
                    Require(File.Exists(sidecar) && File.Exists(calibrationPath),"A selected hall camera is missing its frame sidecar or calibration: "+cameraId);
                    var camera=new CameraEvidence{cameraId=cameraId,calibrationPath=calibrationPath};evidence.cameras.Add(camera);
                    var calibration=JsonUtility.FromJson<CameraCalibration>(File.ReadAllText(calibrationPath));
                    Require(calibration.cameraId==cameraId && calibration.width==854 && calibration.height==480 && calibration.fps==30 && CalibrationValid(calibration),
                        "A selected hall camera has invalid or incomplete metric calibration: "+cameraId);
                    camera.calibrationValid=true;cameraPoses.Add(calibration.cameraToWorldMatrix);
                    var cameraSync=new HashSet<long>();cameraSyncSets.Add(cameraSync);
                    foreach (string line in captureReader.ReadLines("video/" + cameraId + "/frames.jsonl"))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var frame = JsonUtility.FromJson<VideoFrame>(line);
                        Require(frame.stamp!=null,"A recorded camera frame is missing its exact runtime stamp: "+cameraId);
                        Require(FrameCameraValid(frame.camera),"A camera frame lacks its pinhole intrinsic/extrinsic matrices or retained postprocessing: "+cameraId);
                        Require(clockStamps.TryGetValue(frame.stamp.syncSampleId,out var stamp) && stamp.runId == frame.stamp.runId &&
                            stamp.unityFrame == frame.stamp.unityFrame && Math.Abs(stamp.simulationSeconds - frame.stamp.simulationSeconds) < .0001 &&
                            Math.Abs(stamp.realElapsedSeconds-frame.stamp.realElapsedSeconds)<.0001,
                            "A video frame cannot be joined to its runtime synchronization record: "+cameraId);
                        cameraSync.Add(frame.stamp.syncSampleId);++camera.frames;
                        if (frame.stamp.paused)
                        {
                            Require(Math.Abs(frame.stamp.simulationSeconds - run.pausedSimulationSeconds) < .001,
                                "A paused video frame advanced its simulation timestamp.");
                            ++evidence.pausedVideoFrames;++camera.pausedFrames;
                        }
                        ++evidence.frameRecords;
                    }
                    Require(camera.frames>0 && camera.pausedFrames>0,"A selected hall camera produced no actual synchronized frames or paused frames: "+cameraId);
                }
                foreach(var set in cameraSyncSets.Skip(1)) cameraSyncSets[0].IntersectWith(set);evidence.sharedVideoSyncSamples=cameraSyncSets[0].Count;
                Require(evidence.sharedVideoSyncSamples>0,"The four hall cameras have no video frames with a shared exact runtime stamp.");
                evidence.distinctHallCameraPoses=Enumerable.Range(0,cameraPoses.Count).All(a=>Enumerable.Range(a+1,cameraPoses.Count-a-1).All(b=>Enumerable.Range(12,3).Any(i=>Math.Abs(cameraPoses[a][i]-cameraPoses[b][i])>.1f)));
                Require(evidence.distinctHallCameraPoses,"Two hall calibrations unexpectedly describe the same camera position.");
                Require(evidence.frameRecords > 0 && evidence.videoFrames == evidence.frameRecords, "Finalized video counts do not match their synchronized frame sidecars."); evidence.frameSyncJoined = true;
                Require(evidence.pausedVideoFrames > 0, "The selected camera produced no real-time video frame while the simulation clock was paused.");
            }
            else
            {
                evidence.allOptionalFilesAbsent = optionalFiles.All(f => !File.Exists(Path.Combine(evidence.directory,f))) && !Directory.Exists(Path.Combine(evidence.directory,"video")) && !Directory.Exists(Path.Combine(evidence.directory,"agents"));
                Require(string.IsNullOrEmpty(manifest.agentIndexFile) && string.IsNullOrEmpty(manifest.agentsDirectory),"Minimal capture advertises unchecked avatar data.");
                Require(evidence.allOptionalFilesAbsent, "An unchecked optional stream was stored.");
                Require(!manifest.captureOptions.video && !manifest.captureOptions.timetable && !manifest.captureOptions.profiles && !manifest.captureOptions.trajectories &&
                    !manifest.captureOptions.animations && !manifest.captureOptions.skeletons && !manifest.captureOptions.decisions && !manifest.captureOptions.events, "Minimal capture manifest does not retain the unchecked choices.");
            }
            evidence.closed = true;
        }

        static void ModeChanged(PlayModeStateChange state)
        {
            Load(); if (run == null) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            { run.phase = run.phase == "entering-second" ? "initialize-second" : "initialize-first"; run.initializeFrame = Time.frameCount + 8; Save(); }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                if (run.phase != "exiting-first" && run.phase != "exiting-final") { Error("Play stopped before the collection fixture completed."); run.phase = "exiting-final"; }
                Save();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            { run.phase = run.phase == "exiting-first" && run.errors.Count == 0 ? "enter-second" : "restore"; world = null; Save(); }
        }
        static void Restore()
        {
            EditorSettings.enterPlayModeOptionsEnabled = run.oldOptionsEnabled; EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)run.oldOptions;
            EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(run.oldPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(run.oldPlayScene);
            EditorApplication.isPaused = run.oldPaused; Application.runInBackground = run.oldBackground;
            Application.targetFrameRate = run.oldFps; QualitySettings.vSyncCount = run.oldVsync; Time.timeScale = run.oldScale;
            run.filesPreserved = true;
            foreach (var file in run.files) { file.after = File.Exists(file.path) ? Digest(file.path) : "missing"; if (file.after != file.before) { run.filesPreserved = false; Error("Saved asset changed: " + file.path); } }
            if (!run.initialPaused || !run.pauseChecked || !run.secondLaunchChecked || !run.fullCapture.closed || !run.minimalCapture.closed) Error("The complete launch/capture/pause/restart fixture was not observed.");
            if(!run.speedResetAtStart || !run.speedLockedWhileRunning || !run.speedLockedWhilePaused || !run.speedUnlockedAfterStop || !run.speedResetFromRunning8) Error("Collection speed-lock lifecycle was not verified.");
            run.phase = "complete"; run.completedUtc = DateTime.UtcNow.ToString("O"); run.success = run.errors.Count == 0;
            WriteReport(); SessionState.EraseString(Key); run = null; world = null;
        }
        static List<FileEvidence> SnapshotFiles()
        {
            var paths = new HashSet<string> {ScenePath,"Assets/TrainStation/Scenes/TrainStation.unity","Assets/TrainStation/Scenes/TrainStationDecisionLab.unity",
                "Assets/TrainStation/DecisionLab/Resources/StationDecisionDaySchedule.asset","Assets/TrainStation/DecisionLab/Resources/StationDecisionDayScenario.asset",
                "ProjectSettings/NavMeshAreas.asset"};
            foreach (string guid in AssetDatabase.FindAssets("t:NavMeshData",new[]{"Assets/TrainStation"})) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            foreach (string guid in AssetDatabase.FindAssets("t:StationRailSchedule",new[]{"Assets/TrainStation"})) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            return paths.Where(File.Exists).OrderBy(p => p,StringComparer.Ordinal).Select(p => new FileEvidence{path=p,before=Digest(p)}).ToList();
        }
        static string ScheduleFingerprint(StationDecisionWorld value) => DigestBytes(Encoding.UTF8.GetBytes(JsonUtility.ToJson(new ServiceFingerprint {services=value.rail.schedule.services})));
        static string Digest(string path) => DigestBytes(File.ReadAllBytes(path));
        static string DigestBytes(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        static bool MatrixValid(float[] values)=>values!=null && values.Length==16 && values.All(value=>!float.IsNaN(value) && !float.IsInfinity(value));
        static bool IntrinsicsValid(float[] values)=>values!=null && values.Length==9 && values.All(value=>!float.IsNaN(value) && !float.IsInfinity(value)) &&
            values[0]>0 && values[4]>0 && Math.Abs(values[6])<.00001 && Math.Abs(values[7])<.00001 && Math.Abs(values[8]-1)<.00001;
        static bool CalibrationValid(CameraCalibration c)=>c!=null && c.calibrationVersion=="stationflow.pinhole.v1" && c.cameraModel=="ideal-pinhole" && !c.orthographic && !c.capturedPostProcessing &&
            MatrixValid(c.worldToCameraMatrix) && MatrixValid(c.cameraToWorldMatrix) && MatrixValid(c.projectionMatrix) && MatrixValid(c.inverseProjectionMatrix) && MatrixValid(c.gpuProjectionMatrix) &&
            MatrixValid(c.worldToCvCameraMatrix) && MatrixValid(c.cvCameraToWorldMatrix) && MatrixValid(c.unityViewToCvCameraMatrix) && IntrinsicsValid(c.intrinsicMatrix) && IntrinsicsValid(c.inverseIntrinsicMatrix) &&
            c.distortionCoefficients!=null && c.distortionCoefficients.Length==5 && c.distortionCoefficients.All(v=>v==0);
        static bool FrameCameraValid(FrameCamera c)=>c!=null && !c.capturedPostProcessing && MatrixValid(c.worldToCamera) && MatrixValid(c.cameraToWorld) && MatrixValid(c.projection) &&
            MatrixValid(c.inverseProjection) && MatrixValid(c.gpuProjection) && MatrixValid(c.worldToCvCamera) && MatrixValid(c.cvCameraToWorld) && IntrinsicsValid(c.intrinsicMatrix) && IntrinsicsValid(c.inverseIntrinsicMatrix);
        static T[] Components<T>() where T : Component => SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>(true)).ToArray();
        static void OnLog(string message,string stack,LogType type) { if (run != null && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)) Error(message); }
        static void Error(string message) { if (run != null && !run.errors.Contains(message) && run.errors.Count < 100) run.errors.Add(message); }
        static void Require(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Save() { if (run != null) SessionState.SetString(Key,JsonUtility.ToJson(run)); }
        static void Load() { if (run == null) { string text = SessionState.GetString(Key,""); if (!string.IsNullOrEmpty(text)) run = JsonUtility.FromJson<Run>(text); } }
        static void WriteReport()
        {
            if (run == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); string json = JsonUtility.ToJson(run,true);
            File.WriteAllText(ReportPath,json); File.WriteAllText(run.archivePath,json);
        }
    }
}
#endif
