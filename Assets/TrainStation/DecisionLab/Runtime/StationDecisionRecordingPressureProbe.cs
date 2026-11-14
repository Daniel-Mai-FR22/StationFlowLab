#pragma warning disable CS0649 // Evidence DTOs are populated by JsonUtility.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace StationFlow.TrainStation.DecisionLab.Diagnostics
{
    /// <summary>Opt-in Player-only pressure/paused-recording experiment. Normal launches never install it.</summary>
    [DefaultExecutionOrder(2500)]
    public sealed class StationDecisionRecordingPressureProbe : MonoBehaviour
    {
        const string Flag = "--station-recording-pressure-probe";
        readonly Stopwatch clock = new Stopwatch();
        readonly List<Sample> samples = new List<Sample>();
        readonly List<string> failures = new List<string>(), observedErrors = new List<string>();
        StationDecisionWorld world;
        Report report;
        double phaseAt, previousFrameAt, nextSampleAt, finalizingAt, recoveryAt, recoverySimulation;
        int phaseFrame, finalizingFrame;
        string phase = "waiting";
        bool finished;
        [Serializable] sealed class Configuration
        {
            public string label = "pressure", output, date = "2026-10-28", cameras = "4";
            public int seed = 20261028, queueMiB = 64, optionalBudgetKiB;
            public double movingSeconds = 12, pausedSeconds = 30, resumedSeconds = 5;
        }
        [Serializable] sealed class Report
        {
            public string schema = "stationflow.recording_pressure_probe.v1", startedUtc, completedUtc, buildGuid, outputDirectory, captureDirectory, runId, manifestStatus;
            public string[] cameraIds, failures, observedErrors;
            public Configuration configuration;
            public bool success, captureComplete, closed, allAcceptedDataCompleted, exactVideoClockJoins, observationResumed, resumeStable, timeout;
            public int activePassengersAtStart, maxActivePassengers, finalizingUnityFrames, recoveryUnityFrames;
            public long queueHighWaterBytes, commandsAccepted, commandsCompleted, videoFrames, joinedVideoFrames, missingVideoClockFrames;
            public int queueHighWaterCommands;
            public long captureBatchesRequested, captureBatchesAdmitted, captureBatchesRejectedData, captureBatchesRejectedVideo;
            public long pressureMarkerDataDenials, pressureMarkerVideoDenials;
            public double captureSetupSeconds, stopCallMilliseconds, finalizationSeconds, finalizingMaxFrameMilliseconds, recordingMaxFrameMilliseconds, movingGameFps, pausedGameFps, resumedGameFps;
            public List<Phase> phases = new List<Phase>();
            public Sample[] samples;
            public string limitation = "Ordinary hidden/unfocused Player, 1280x720 main view, target60. Startup/closure excluded from phase FPS. Queue byte accounting estimates retained objects; it is not measured total process RAM. Natural overload and diagnostic queue limits are separate from hardware throughput claims.";
        }
        [Serializable] sealed class Phase { public string name; public double realStart, realEnd, duration, gameFps, simulationStart, simulationEnd; public int unityFrameStart, unityFrameEnd; }
        [Serializable] sealed class Sample { public string phase; public double realSeconds, simulationSeconds; public int unityFrame, activePassengers, queueCommands; public long queueBytes, acceptedCommands, completedCommands; public bool paused, recording, finalizing; }
        [Serializable] sealed class Manifest { public string status; public long dataCommandsAccepted, dataCommandsCompleted, dataQueueHighWaterRetainedBytes; public int dataQueueHighWaterCommands; public long captureBatchesRequested, captureBatchesAdmitted, captureBatchesRejectedData, captureBatchesRejectedVideo; public CameraResult[] cameraResults; }
        [Serializable] sealed class CameraResult { public string cameraId, directory; public long framesWritten; }
        [Serializable] sealed class Sync { public string kind; public StationDecisionCaptureStamp stamp; public Pressure capturePressure; }
        [Serializable] sealed class Pressure { public long dataDeniedBatches, videoDeniedBatches; }
        [Serializable] sealed class VideoFrame { public StationDecisionCaptureStamp stamp; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            string[] args = Environment.GetCommandLineArgs();
            if(Application.isEditor || !args.Contains(Flag)) return;
            var config = new Configuration { output = Path.Combine(StationDecisionCaptureSession.StorageRoot, "PressureProbe") };
            try
            {
                Require(!args.Contains("--station-capture-benchmark") && !args.Contains("--station-collection-smoke"), "Run only one verification hook at a time.");
                for(int i=0;i<args.Length;++i)
                {
                    if(!args[i].StartsWith("--station-pressure-", StringComparison.Ordinal)) continue;
                    string key=args[i]; Require(i+1<args.Length, "Missing value for "+key); string value=args[++i];
                    switch(key)
                    {
                        case "--station-pressure-output": config.output=Path.GetFullPath(value); break;
                        case "--station-pressure-label": config.label=value; break;
                        case "--station-pressure-seed": config.seed=int.Parse(value,CultureInfo.InvariantCulture); break;
                        case "--station-pressure-date": config.date=value; break;
                        case "--station-pressure-cameras": config.cameras=value; break;
                        case "--station-pressure-queue-mib": config.queueMiB=int.Parse(value,CultureInfo.InvariantCulture); break;
                        case "--station-pressure-optional-budget-kib": config.optionalBudgetKiB=int.Parse(value,CultureInfo.InvariantCulture); break;
                        case "--station-pressure-moving-seconds": config.movingSeconds=double.Parse(value,CultureInfo.InvariantCulture); break;
                        case "--station-pressure-paused-seconds": config.pausedSeconds=double.Parse(value,CultureInfo.InvariantCulture); break;
                        case "--station-pressure-resumed-seconds": config.resumedSeconds=double.Parse(value,CultureInfo.InvariantCulture); break;
                        default: throw new ArgumentException("Unknown pressure option: "+key);
                    }
                }
                Require(config.cameras=="4" || config.cameras=="all", "Camera selection must be4 orall.");
                Require(config.queueMiB>=4 && config.queueMiB<=64, "Diagnostic queue budget must be4–64MiB.");
                Require(config.optionalBudgetKiB>=0 && config.optionalBudgetKiB<=config.queueMiB*1024, "Optional admission ceiling must fit within the configured data queue.");
                Require(config.movingSeconds>=2 && config.pausedSeconds>=2 && config.resumedSeconds>=2 && config.movingSeconds+config.pausedSeconds+config.resumedSeconds<=120, "Invalid probe phase durations.");
                DateTime.ParseExact(config.date,"yyyy-MM-dd",CultureInfo.InvariantCulture);
                var scenario=Resources.Load<StationDecisionDayScenario>("StationDecisionDayScenario"); Require(scenario,"Daily scenario missing.");
                scenario.randomizeOnLaunch=true;scenario.useFixedRuntimeSeed=true;scenario.fixedRuntimeSeed=config.seed;scenario.replayDateIso=config.date;scenario.defaultTimeScale=1;
                Application.runInBackground=true;Application.targetFrameRate=60;QualitySettings.vSyncCount=0;
                var host=new GameObject("Explicit recording pressure probe");DontDestroyOnLoad(host);host.AddComponent<StationDecisionRecordingPressureProbe>().Initialize(config);
            }
            catch(Exception error)
            {
                Directory.CreateDirectory(config.output);File.WriteAllText(Path.Combine(config.output,"pressure.json"),JsonUtility.ToJson(new Report{configuration=config,failures=new[]{error.ToString()}},true));Application.Quit(2);
            }
        }
        void Initialize(Configuration config)
        {
            report=new Report{configuration=config,startedUtc=DateTime.UtcNow.ToString("O"),buildGuid=Application.buildGUID,outputDirectory=config.output};
            Directory.CreateDirectory(config.output);clock.Start();Application.logMessageReceived+=OnLog;
        }
        void LateUpdate()
        {
            if(finished || report==null)return;
            try
            {
                double now=clock.Elapsed.TotalSeconds;
                Require(now<report.configuration.movingSeconds+report.configuration.pausedSeconds+report.configuration.resumedSeconds+180,"Probe exceeded its bounded wall-clock timeout.");
                if(phase=="waiting"){TryStart();return;}
                double frameMs=(now-previousFrameAt)*1000;previousFrameAt=now;
                if(phase=="finalizing")report.finalizingMaxFrameMilliseconds=Math.Max(report.finalizingMaxFrameMilliseconds,frameMs);
                else if(phase!="recovery")report.recordingMaxFrameMilliseconds=Math.Max(report.recordingMaxFrameMilliseconds,frameMs);
                Observe(now);
                if(phase=="finalizing")
                {
                    report.finalizingUnityFrames=Time.frameCount-finalizingFrame;
                    Require(!world.Paused,"Observation was paused again while an earlier capture error was being finalized.");
                    if(world.CaptureSession.IsFinalizing)return;
                    report.finalizationSeconds=now-finalizingAt;report.closed=!world.IsCollecting;
                    phase="recovery";recoveryAt=now;recoverySimulation=world.ElapsedSeconds;phaseFrame=Time.frameCount;return;
                }
                if(phase=="recovery")
                {
                    Require(!world.Paused && !world.rail.IsPaused,"Observation was paused again after capture finalization.");
                    if(now-recoveryAt<2)return;
                    report.recoveryUnityFrames=Time.frameCount-phaseFrame;report.resumeStable=world.ElapsedSeconds>recoverySimulation && report.recoveryUnityFrames>1;
                    Require(report.resumeStable,"Observation failed to advance after Stop.");ValidateFinalized();Finish();return;
                }
                if(!world.IsCollecting){BeginFinalization(false);return;}
                Require(world.SimulationSpeed==1f,"Recording speed must remain x1.");
                if(phase=="moving" && now-phaseAt>=report.configuration.movingSeconds)
                {EndPhase();world.TogglePause();Require(world.Paused,"Pause did not hold the simulation.");BeginPhase("paused");}
                else if(phase=="paused" && now-phaseAt>=report.configuration.pausedSeconds)
                {EndPhase();world.TogglePause();Require(!world.Paused,"Resume after a prolonged recorded Pause failed.");BeginPhase("resumed");}
                else if(phase=="resumed" && now-phaseAt>=report.configuration.resumedSeconds)BeginFinalization(true);
            }
            catch(Exception error)
            {
                failures.Add(error.ToString());
                if(error is TimeoutException || clock.Elapsed.TotalSeconds>170)report.timeout=true;
                if(phase=="finalizing" || phase=="recovery" || phase=="waiting")Finish();
                else BeginFinalization(false);
            }
        }
        void TryStart()
        {
            if(!world)world=FindObjectsByType<StationDecisionWorld>(FindObjectsSortMode.None).FirstOrDefault(w=>w.IsDaily);
            if(!world || !world.rail || !world.rail.IsReady || !world.arrivalController || !world.arrivalController.IsReady || !world.CaptureSession)return;
            Require(world.Paused && Math.Abs(world.ElapsedSeconds)<.001,"Probe must begin from the paused launch state.");
            foreach(var panel in FindObjectsByType<StationDecisionCollectionPanel>())if(panel.gameObject.scene==world.gameObject.scene)panel.Hide();
            var views=FindObjectsByType<TrainStationViewControls>().FirstOrDefault(v=>v.gameObject.scene==world.gameObject.scene);if(views)views.SetHall();
            report.cameraIds=world.CaptureSession.AvailableCameras.Where(c=>report.configuration.cameras=="all" || c.cameraId.StartsWith("HALL-",StringComparison.Ordinal)).Select(c=>c.cameraId).OrderBy(id=>id,StringComparer.Ordinal).ToArray();
            Require(report.cameraIds.Length==(report.configuration.cameras=="all"?16:4),"Selected camera inventory differs from the expected scene inventory.");
            world.CaptureSession.ConfigureDataQueueLimits(report.configuration.queueMiB*1024L*1024);
            if(report.configuration.optionalBudgetKiB>0)world.CaptureSession.ConfigureOptionalAdmissionBudget(report.configuration.optionalBudgetKiB*1024L);
            var options=new StationDecisionCaptureOptions{video=true,timetable=true,profiles=true,trajectories=true,animations=true,skeletons=true,decisions=true,events=true,videoWidth=854,videoHeight=480,videoFps=30,jpegQuality=75,selectedCameraIds=report.cameraIds};
            double setupAt=clock.Elapsed.TotalSeconds;bool began=world.BeginDataCollection(options);report.captureSetupSeconds=clock.Elapsed.TotalSeconds-setupAt;
            report.captureDirectory=world.CaptureSession.OutputDirectory;report.runId=world.CaptureSession.RunId;
            Require(began,"Probe could not start capture: "+world.CollectionError);report.activePassengersAtStart=world.ActivePassengerCount;BeginPhase("moving");
        }
        void BeginPhase(string name)
        {
            phase=name;phaseAt=previousFrameAt=clock.Elapsed.TotalSeconds;phaseFrame=Time.frameCount;nextSampleAt=phaseAt;
            report.phases.Add(new Phase{name=name,realStart=phaseAt,unityFrameStart=phaseFrame,simulationStart=world.ElapsedSeconds});
        }
        void EndPhase()
        {
            var item=report.phases.Last();if(item.realEnd>0)return;
            item.realEnd=clock.Elapsed.TotalSeconds;item.unityFrameEnd=Time.frameCount;item.simulationEnd=world.ElapsedSeconds;item.duration=item.realEnd-item.realStart;
            item.gameFps=(item.unityFrameEnd-item.unityFrameStart)/Math.Max(.000001,item.duration);
            if(item.name=="moving")report.movingGameFps=item.gameFps;else if(item.name=="paused")report.pausedGameFps=item.gameFps;else report.resumedGameFps=item.gameFps;
        }
        void BeginFinalization(bool requestedStop)
        {
            if(report.phases.Count>0)EndPhase();finalizingAt=previousFrameAt=clock.Elapsed.TotalSeconds;finalizingFrame=Time.frameCount;phase="finalizing";
            double stopAt=clock.Elapsed.TotalSeconds;
            if(world.IsCollecting)world.StopDataCollection(requestedStop?"pressure_probe_complete":"pressure_probe_failed");
            report.stopCallMilliseconds=(clock.Elapsed.TotalSeconds-stopAt)*1000;
            world.SetSimulationSpeed(1);world.BeginObservation();report.observationResumed=!world.Paused;
            Require(report.observationResumed,"Observation could not resume while the recording closed.");
        }
        void Observe(double now)
        {
            report.maxActivePassengers=Math.Max(report.maxActivePassengers,world.ActivePassengerCount);
            if(now<nextSampleAt)return;nextSampleAt=now+.5;var writer=world.CaptureSession.DataWriter;
            samples.Add(new Sample{phase=phase,realSeconds=now,simulationSeconds=world.ElapsedSeconds,unityFrame=Time.frameCount,activePassengers=world.ActivePassengerCount,paused=world.Paused,recording=world.IsCollecting,finalizing=world.CaptureSession.IsFinalizing,queueBytes=writer?.PendingBytes??0,queueCommands=writer?.PendingCommands??0,acceptedCommands=writer?.CommandsAccepted??0,completedCommands=writer?.CommandsCompleted??0});
        }
        void ValidateFinalized()
        {
            var manifest=JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(report.captureDirectory,"manifest.json")));
            report.manifestStatus=manifest.status;report.captureComplete=manifest.status=="complete";
            report.queueHighWaterBytes=manifest.dataQueueHighWaterRetainedBytes;report.queueHighWaterCommands=manifest.dataQueueHighWaterCommands;
            report.commandsAccepted=manifest.dataCommandsAccepted;report.commandsCompleted=manifest.dataCommandsCompleted;
            report.captureBatchesRequested=manifest.captureBatchesRequested;report.captureBatchesAdmitted=manifest.captureBatchesAdmitted;report.captureBatchesRejectedData=manifest.captureBatchesRejectedData;report.captureBatchesRejectedVideo=manifest.captureBatchesRejectedVideo;
            Require(report.captureBatchesRequested==report.captureBatchesAdmitted+report.captureBatchesRejectedData+report.captureBatchesRejectedVideo,"Common capture admission counters do not reconcile.");
            report.allAcceptedDataCompleted=report.commandsAccepted==report.commandsCompleted;Require(report.allAcceptedDataCompleted,"Accepted data commands did not finish.");
            var reader=new StationDecisionCaptureReader(report.captureDirectory);var stamps=new Dictionary<long,StationDecisionCaptureStamp>();
            foreach(string line in reader.ReadLines("sync.jsonl"))
            {if(string.IsNullOrWhiteSpace(line))continue;var record=JsonUtility.FromJson<Sync>(line);if(record.kind=="clock" && record.stamp!=null)stamps[record.stamp.syncSampleId]=record.stamp;
                if(record.kind=="capture_capacity_pressure" && record.capturePressure!=null){report.pressureMarkerDataDenials+=record.capturePressure.dataDeniedBatches;report.pressureMarkerVideoDenials+=record.capturePressure.videoDeniedBatches;}}
            Require(report.pressureMarkerDataDenials==report.captureBatchesRejectedData && report.pressureMarkerVideoDenials==report.captureBatchesRejectedVideo,"Aggregated pressure marker counts differ from manifest denials.");
            foreach(var camera in manifest.cameraResults??Array.Empty<CameraResult>())
            {
                long frames=0;
                foreach(string line in reader.ReadLines(camera.directory+"/frames.jsonl"))
                {
                    if(string.IsNullOrWhiteSpace(line))continue;++frames;var frame=JsonUtility.FromJson<VideoFrame>(line);var stamp=frame.stamp;
                    if(stamp!=null && stamps.TryGetValue(stamp.syncSampleId,out var canonical) && canonical.runId==stamp.runId && canonical.unityFrame==stamp.unityFrame && Math.Abs(canonical.realElapsedSeconds-stamp.realElapsedSeconds)<.0001 && Math.Abs(canonical.simulationSeconds-stamp.simulationSeconds)<.0001)++report.joinedVideoFrames;
                    else ++report.missingVideoClockFrames;
                }
                Require(frames==camera.framesWritten,"Encoded/sidecar counts disagree for "+camera.cameraId);report.videoFrames+=frames;
            }
            report.exactVideoClockJoins=report.videoFrames>0 && report.missingVideoClockFrames==0;
            if(report.captureComplete)Require(report.exactVideoClockJoins,"A complete capture contains video frames without exact clock joins.");
        }
        void Finish()
        {
            if(finished)return;finished=true;report.completedUtc=DateTime.UtcNow.ToString("O");report.samples=samples.ToArray();report.failures=failures.ToArray();report.observedErrors=observedErrors.ToArray();
            report.success=failures.Count==0 && report.closed && report.resumeStable && report.allAcceptedDataCompleted;
            File.WriteAllText(Path.Combine(report.outputDirectory,"pressure.json"),JsonUtility.ToJson(report,true),new UTF8Encoding(false));Application.logMessageReceived-=OnLog;Application.Quit(report.success?0:2);
        }
        void OnLog(string message,string stack,LogType type){if((type==LogType.Error || type==LogType.Exception || type==LogType.Assert) && observedErrors.Count<20)observedErrors.Add(message+"\n"+stack);}
        void OnDestroy(){Application.logMessageReceived-=OnLog;}
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    }
}
