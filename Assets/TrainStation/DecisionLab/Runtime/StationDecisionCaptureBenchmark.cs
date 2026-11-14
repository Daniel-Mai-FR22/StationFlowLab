#pragma warning disable CS0649 // JsonUtility populates the read-only serialized evidence DTOs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace StationFlow.TrainStation.DecisionLab.Diagnostics
{
    /// <summary>Explicit Player experiment. Normal launches never install this component.</summary>
    [DefaultExecutionOrder(2000)]
    public sealed class StationDecisionCaptureBenchmark : MonoBehaviour
    {
        const string Flag="--station-capture-benchmark";
        static readonly string[] HallIds={"HALL-01","HALL-02","HALL-03","HALL-04"};
        readonly Stopwatch clock=new Stopwatch();
        readonly List<string> errors=new List<string>();
        readonly List<double> frames=new List<double>(),main=new List<double>(),render=new List<double>(),gpu=new List<double>(),nativeGpu=new List<double>();
        readonly List<ActorSample> actors=new List<ActorSample>();
        readonly FrameTiming[] timing=new FrameTiming[1];
        StationDecisionBenchmarkConfiguration config; Report report; StationDecisionWorld world; StationDecisionCaptureDiagnostics diagnostics;
        ProfilerRecorder mainRecorder,renderRecorder,gpuRecorder;
        double began,measureAt,previousFrame,nextActorAt,cpuAt,closingAt;
        bool requestedSuccess,reportWritten; int closingFrame;
        int startFrame; ulong lastTiming; bool started,measuring,finishing,disposed;

        [Serializable] sealed class Report
        {
            public string schema="stationflow.capture_benchmark.v2",startedUtc,completedUtc,scene,operatingDate,launchId,captureRunId,captureDirectory,diagnosticReport,reportPath,timetableSha256,arrivalPlanSha256;
            public string executableDirectory,workingDirectory,applicationVersion,applicationBuildGuid,unityVersion,launchMode,mainRecorderStatus,renderRecorderStatus,gpuRecorderStatus;
            public string resolvedAdmission,resolvedRenderProfile,videoWindowDefinition="startSyncSampleId < original frame syncSampleId <= endSyncSampleId; nominal AVI FPS is playback metadata only";
            public int resolvedReadbackSlots;
            public string resolvedDataWriterMode = "not-applicable", resolvedCameraSelection = "none";
            public int installedCameraCount,resolvedCameraCount,finalizedCameraCount;
            public string[] installedCameraIds,resolvedCameraIds,finalizedCameraIds;
            public CameraInventory[] installedCameraInventory;
            public bool finalizedCameraSetVerified;
            public bool success,closed,fixedSeedVerified,initiallyPaused,batchMode,runInBackground,frameTimingEnabled,gpuTimingAvailable;
            public int seed,targetGameFps,vSyncCount,measuredUnityFrames,startUnityFrame,endUnityFrame,focusedFrames,unfocusedFrames,generatedJourneys,scheduledServices;
            public StationDecisionBenchmarkConfiguration request; public Hardware hardware; public StationDecisionCaptureOptions captureOptions;
            public double startSimulationSeconds,endSimulationSeconds,startStationClockSeconds,endStationClockSeconds,warmupRealSeconds,measuredRealSeconds;
            public double sessionWindowStartRealSeconds,sessionWindowEndRealSeconds,gameFps,processCpuSeconds,processCpuPercentOneCore,processCpuPercentAllCores,closeSeconds;
            public double captureSetupRealSeconds,stopCallMilliseconds; public int finalizingUnityFrames;
            public long sessionWindowStartSyncSampleId,sessionWindowEndSyncSampleId;
            public int sessionWindowStartUnityFrame,sessionWindowEndUnityFrame,anchoredUnityFrameDelta;
            public double anchoredRealSeconds,anchoredGameFps,cameraGameFrameRatioMin,cameraGameFrameRatioMax,cameraGameFrameRatioMean;
            public long sessionVideoFramesWritten,sessionVideoRequestsDropped;
            public Statistic gameFrameWallMs,profilerMainThreadMs,profilerRenderThreadMs,profilerGpuMs,nativeGpuMs;
            public ActorSample[] actors; public CameraResult[] cameras; public string[] errors;
            public string[] limitations={
                "Game FPS is Unity frame-count delta / Stopwatch wall seconds. Video FPS counts original finalized sidecars by explicit sync-ID boundaries, avoiding decimal JSON boundary rounding.",
                "Stage diagnostics cover warmup plus measurement. Driver frame/CPU/video summaries exclude warmup and closing. The fps request field is nominal AVI playback metadata only; there is no fixed capture target.",
                "Pipeline controls are benchmark-only overrides. Same-build baseline mode is not automatically equivalent to an older build's readback and ownership policy.",
                "Primary camera completeness metric is measured accepted frames / capture-clock Unity frame delta over the identical sync-ID window; game FPS remains a separate throughput tradeoff.",
                "Seed/date/scene/speed and generated timetable/arrival-plan hashes match across cases. Realtime stepping and native avoidance can still vary; actor and simulation-clock samples disclose workload differences.",
                Application.isBatchMode
                    ? "This run uses -batchmode with rendering, launched by the harness as a hidden Windows process. Its presentation context differs from an ordinary foreground Player. renderingThreadingMode is observed separately; -batchmode alone is not proof of a particular threading mode. Missing or zero GPU timings are unavailable, never proof of an idle GPU."
                    : "This run uses an ordinary Player without -batchmode; the harness requests a hidden Windows process. Hidden or unfocused execution can still differ from ordinary foreground use. renderingThreadingMode is observed separately. Missing or zero GPU timings are unavailable, never proof of an idle GPU.",
                "Profiler Main Thread/Render Thread include waits. Process CPU seconds include all process threads. GPU values require positive native timing samples; saved project timing settings are not changed.",
                "All-off opens the minimal capture clock/manifest. Observation creates no capture session. Normal scene UI and the main game camera stay enabled for every case."};
        }
        [Serializable] sealed class Hardware
        {
            public string operatingSystem,deviceModel,processorType,graphicsDeviceName,graphicsVendor,graphicsVersion,graphicsApi,renderPipeline,quality,renderingThreadingMode;
            public int processorCount,processorFrequencyMHz,systemMemoryMB,graphicsMemoryMB,screenWidth,screenHeight;
            public bool supportsAsyncGpuReadback;
        }
        [Serializable] sealed class CameraInventory
        { public string cameraId,displayName,purpose,gameObjectName; public bool gameObjectActive,cameraEnabled; public Vector3 position,rotationEuler; }
        [Serializable] sealed class Statistic { public int count; public double min,max,mean,median,p95,p99; }
        [Serializable] sealed class ActorSample { public double realSeconds,simulationSeconds; public int activePassengers,activePoolObjects,enteredJourneys,representedActivePassengers; }
        [Serializable] sealed class CameraResult
        {
            public string cameraId,sidecarPath; public long sessionFramesWritten,sessionRequestsDropped,sidecarFrames,measuredAcceptedFrames;
            public double firstAcceptedRealSeconds,lastAcceptedRealSeconds,acceptedFps,acceptedIntervalFps,acceptedToGameFrameRatio,sessionEncodedPlaybackSeconds,bytesWritten; public Statistic acceptedIntervalMs;
        }
        [Serializable] sealed class Manifest { public string status; public string[] errors,selectedCameraIds; public ManifestCamera[] cameraResults; }
        [Serializable] sealed class ManifestCamera { public string cameraId,directory; public long framesWritten,requestsDropped; }
        [Serializable] sealed class VideoFrame { public StationDecisionCaptureStamp stamp; }
        [Serializable] sealed class TimetableFingerprint { public string date; public double startClockSeconds; public RailOperations.StationRailService[] services; }
        [Serializable] sealed class ArrivalFingerprint { public DecisionArrivalPlan[] journeys; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            var args=Environment.GetCommandLineArgs(); if(Application.isEditor || !args.Contains(Flag))return;
            StationDecisionBenchmarkConfiguration settings=null;
            try
            {
                Require(!args.Contains("--station-collection-smoke"),"Do not combine benchmark and smoke hooks."); settings=StationDecisionBenchmarkConfiguration.Parse(args,Path.Combine(StationDecisionCaptureSession.StorageRoot,"Benchmarks"));
                // Player memory only, before the scene world's Awake clones this shared resource.
                var scenario=Resources.Load<StationDecisionDayScenario>("StationDecisionDayScenario"); Require(scenario,"Daily scenario resource missing.");
                scenario.randomizeOnLaunch=true;scenario.useFixedRuntimeSeed=true;scenario.fixedRuntimeSeed=settings.seed;scenario.replayDateIso=settings.date;scenario.defaultTimeScale=settings.simulationSpeed;
                Application.runInBackground=true;Application.targetFrameRate=60;QualitySettings.vSyncCount=0;
                var host=new GameObject("Explicit capture benchmark");DontDestroyOnLoad(host);host.AddComponent<StationDecisionCaptureBenchmark>().Initialize(settings);
            }
            catch(Exception error)
            {
                UnityEngine.Debug.LogError("Benchmark bootstrap failed: "+error);
                string output=settings?.output??Path.Combine(StationDecisionCaptureSession.StorageRoot,"Benchmarks","bootstrap-error");Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output,"benchmark.json"),JsonUtility.ToJson(new Report{errors=new[]{error.ToString()}},true));Application.Quit(2);
            }
        }
        void Initialize(StationDecisionBenchmarkConfiguration settings)
        {
            config=settings;clock.Start();Application.logMessageReceived+=OnLog;Directory.CreateDirectory(config.output);
            report=new Report{request=config,startedUtc=DateTime.UtcNow.ToString("O"),seed=config.seed,operatingDate=config.date,
                executableDirectory=Path.GetFullPath(Path.GetDirectoryName(Application.dataPath)??"."),workingDirectory=Path.GetFullPath(Environment.CurrentDirectory),
                applicationVersion=Application.version,applicationBuildGuid=Application.buildGUID,unityVersion=Application.unityVersion,reportPath=Path.Combine(config.output,"benchmark.json"),diagnosticReport=Path.Combine(config.output,"capture-diagnostics.json"),
                targetGameFps=60,vSyncCount=0,batchMode=Application.isBatchMode,runInBackground=Application.runInBackground,launchMode=(Application.isBatchMode?"Explicit CLI; -batchmode with rendering":"Explicit CLI; ordinary Player without -batchmode")+"; harness requests a hidden Windows process; main game camera enabled at "+Screen.width+"x"+Screen.height+"; runInBackground="+Application.runInBackground+".",hardware=ReadHardware()};
            mainRecorder=TryRecorder(ProfilerCategory.Internal,"Main Thread");renderRecorder=TryRecorder(ProfilerCategory.Internal,"Render Thread");gpuRecorder=TryRecorder(ProfilerCategory.Render,"GPU Frame Time");
            report.mainRecorderStatus=RecorderStatus(mainRecorder);report.renderRecorderStatus=RecorderStatus(renderRecorder);report.gpuRecorderStatus=RecorderStatus(gpuRecorder);report.frameTimingEnabled=FrameTimingManager.IsFeatureEnabled();
        }
        void LateUpdate()
        {
            if(config==null)return;
            if(finishing){PollFinalization();return;}
            try
            {
                Require(clock.Elapsed.TotalSeconds<config.warmup+config.seconds+120,"Benchmark timed out.");
                if(!started){TryStart();return;}
                Require(world && !world.Paused && !world.rail.IsPaused,"Benchmark simulation stopped unexpectedly.");
                if(config.variant!="observation")Require(world.IsCollecting,"Capture stopped unexpectedly: "+world.CaptureSession.LastError);
                double now=clock.Elapsed.TotalSeconds;
                if(!measuring){if(now-began>=config.warmup)BeginMeasurement(now);return;}
                frames.Add((now-previousFrame)*1000);previousFrame=now;
                if(Application.isFocused)++report.focusedFrames;else ++report.unfocusedFrames;
                Collect(mainRecorder,main);Collect(renderRecorder,render);Collect(gpuRecorder,gpu);CollectNativeGpu();
                if(now>=nextActorAt){actors.Add(ReadActors(now-measureAt));nextActorAt=now+.5;}
                if(now-measureAt>=config.seconds)Complete(now);
            }
            catch(Exception error){errors.Add(error.ToString());Finish(false);}
        }
        void TryStart()
        {
            if(!world)world=FindObjectsByType<StationDecisionWorld>().FirstOrDefault(w=>w.IsDaily);
            if(!world || !world.rail.IsReady || !world.arrivalController || !world.arrivalController.IsReady || !world.CaptureSession)return;
            Require(world.RunSession!=null,"Daily run metadata missing.");
            report.fixedSeedVerified=world.RunSession.fixedSeedReplay && world.RunSession.seed==config.seed && world.RunSession.operatingDateIso==config.date;
            Require(report.fixedSeedVerified,"Before-Awake seed/date override did not bind to this world.");
            report.initiallyPaused=world.Paused && world.rail.IsPaused && Math.Abs(world.ElapsedSeconds)<.001;Require(report.initiallyPaused,"World advanced before explicit benchmark Start.");
            report.scene=world.gameObject.scene.path;report.launchId=world.RunSession.runId;report.hardware=ReadHardware();
            report.timetableSha256=Hash(JsonUtility.ToJson(new TimetableFingerprint{date=config.date,startClockSeconds=world.rail.schedule.StartClockSeconds,services=world.rail.schedule.services}));
            report.arrivalPlanSha256=Hash(JsonUtility.ToJson(new ArrivalFingerprint{journeys=world.arrivalController.GeneratedPlan.ToArray()}));
            report.generatedJourneys=world.arrivalController.GeneratedPlan.Count;report.scheduledServices=world.rail.schedule.services.Length;
            var panel=world.GetComponent<StationDecisionCollectionPanel>();if(panel)panel.Hide();
            var views=FindObjectsByType<TrainStationViewControls>().FirstOrDefault(v=>v.gameObject.scene==world.gameObject.scene);if(views)views.SetHall();
            world.SetSimulationSpeed(config.simulationSpeed);Application.targetFrameRate=60;QualitySettings.vSyncCount=0;
            bool data=config.variant=="data-only" || config.variant=="all-data-video",video=config.variant=="video-only" || config.variant=="all-data-video";
            var installed=world.CaptureSession.AvailableCameras;
            report.installedCameraIds=installed.Select(c=>c.cameraId).ToArray();report.installedCameraCount=installed.Length;
            report.installedCameraInventory=installed.Select(c=>new CameraInventory{cameraId=c.cameraId,displayName=c.displayName,purpose=c.purpose,gameObjectName=c.gameObject.name,
                gameObjectActive=c.gameObject.activeInHierarchy,cameraEnabled=c.cameraComponent.enabled,position=c.cameraComponent.transform.position,rotationEuler=c.cameraComponent.transform.eulerAngles}).ToArray();
            report.resolvedCameraIds=config.ResolveCameraIds(report.installedCameraIds,HallIds);report.resolvedCameraCount=report.resolvedCameraIds.Length;
            report.resolvedCameraSelection=video?config.cameraSelection:"none";
            report.captureOptions=new StationDecisionCaptureOptions{video=video,timetable=data,profiles=data,trajectories=data,animations=data,skeletons=config.ResolveSkeletons(),decisions=data,events=data,videoWidth=config.width,videoHeight=config.height,videoFps=config.fps,jpegQuality=75,selectedCameraIds=(string[])report.resolvedCameraIds.Clone()};
            if(config.variant=="observation")world.BeginObservation();
            else
            {
                if(config.dataWriter!="default")world.CaptureSession.ConfigureDataWriter(config.dataWriter);
                report.resolvedDataWriterMode=world.CaptureSession.DataWriterMode;
                foreach(string id in report.resolvedCameraIds)Require(installed.Count(c=>c.cameraId==id)==1,"Missing or duplicate installed camera: "+id);
                if(video)
                {
                    var session=world.CaptureSession;
                    if(config.readbackSlots!=0 || config.admission!="default")
                        session.ConfigureCapturePipeline(config.readbackSlots==0?session.ReadbackSlotCount:config.readbackSlots,config.admission=="default"?session.CommonBatchAdmission:config.admission=="common");
                    if(config.renderProfile!="default")session.ConfigureRenderProfile(config.renderProfile);
                    report.resolvedReadbackSlots=session.ReadbackSlotCount;report.resolvedAdmission=session.CommonBatchAdmission?"common":"baseline";report.resolvedRenderProfile=session.RenderProfile;
                }
                else {report.resolvedAdmission=report.resolvedRenderProfile="not-applicable";}
                diagnostics=new StationDecisionCaptureDiagnostics(config.label);diagnostics.SetMetadata("benchmarkVariant",config.variant);diagnostics.SetMetadata("timingWindow","Whole capture includes warmup; benchmark.json reports measured-only frame/CPU/video rates.");
                diagnostics.SetMetadata("benchmarkResolvedAdmission",report.resolvedAdmission);diagnostics.SetMetadata("benchmarkResolvedReadbackSlots",report.resolvedReadbackSlots.ToString(CultureInfo.InvariantCulture));diagnostics.SetMetadata("benchmarkResolvedRenderProfile",report.resolvedRenderProfile);
                diagnostics.SetMetadata("benchmarkResolvedDataWriterMode",report.resolvedDataWriterMode);diagnostics.SetMetadata("renderingThreadingMode",SystemInfo.renderingThreadingMode.ToString());
                diagnostics.SetMetadata("benchmarkCameraSelection",report.resolvedCameraSelection);diagnostics.SetMetadata("benchmarkResolvedCameraCount",report.resolvedCameraCount.ToString(CultureInfo.InvariantCulture));
                diagnostics.SetMetadata("benchmarkInstalledCameraIds",string.Join(",",report.installedCameraIds));diagnostics.SetMetadata("benchmarkResolvedCameraIds",string.Join(",",report.resolvedCameraIds));
                world.CaptureSession.ConfigureDiagnostics(diagnostics);
                long setupStarted=Stopwatch.GetTimestamp();bool captureStarted;
                try {captureStarted=world.BeginDataCollection(report.captureOptions);}
                finally
                {
                    report.captureSetupRealSeconds=(Stopwatch.GetTimestamp()-setupStarted)/(double)Stopwatch.Frequency;
                    diagnostics.Record("","capture_start_setup_wall","ms",report.captureSetupRealSeconds*1000d);
                    diagnostics.SetMetadata("captureSetupRealSeconds",Number(report.captureSetupRealSeconds));
                }
                Require(captureStarted,"Capture Start failed: "+world.CollectionError);
                report.captureDirectory=world.CaptureSession.OutputDirectory;report.captureRunId=world.CaptureSession.RunId;
            }
            Require(Mathf.Abs(world.SimulationSpeed-1f)<.001f && Mathf.Abs(Time.timeScale-1f)<.001f,"Benchmark must run at actual speed1 after observation/capture Start.");
            began=clock.Elapsed.TotalSeconds;started=true;
        }
        void BeginMeasurement(double now)
        {
            report.warmupRealSeconds=now-began;measureAt=previousFrame=now;startFrame=Time.frameCount;cpuAt=Cpu();report.startUnityFrame=startFrame;
            report.startSimulationSeconds=world.rail.ElapsedSimulationSeconds;report.startStationClockSeconds=world.rail.ClockSeconds;
            var stamp=world.IsCollecting?world.CaptureSession.GetStamp():null;report.sessionWindowStartRealSeconds=stamp!=null?stamp.realElapsedSeconds:now-began;report.sessionWindowStartSyncSampleId=stamp!=null?stamp.syncSampleId:0;report.sessionWindowStartUnityFrame=stamp!=null?stamp.unityFrame:0;
            if(world.IsCollecting)world.CaptureSession.RecordMarker("benchmark_measurement_started","Warmup excluded from driver measured summaries.");
            diagnostics?.SetMetadata("measuredWindowStartRealSeconds",Number(report.sessionWindowStartRealSeconds));actors.Add(ReadActors(0));nextActorAt=now+.5;measuring=true;
        }
        void Complete(double now)
        {
            report.measuredRealSeconds=now-measureAt;report.measuredUnityFrames=Time.frameCount-startFrame;report.endUnityFrame=Time.frameCount;report.gameFps=report.measuredUnityFrames/report.measuredRealSeconds;
            report.endSimulationSeconds=world.rail.ElapsedSimulationSeconds;report.endStationClockSeconds=world.rail.ClockSeconds;var stamp=world.IsCollecting?world.CaptureSession.GetStamp():null;report.sessionWindowEndRealSeconds=stamp!=null?stamp.realElapsedSeconds:now-began;report.sessionWindowEndSyncSampleId=stamp!=null?stamp.syncSampleId:0;report.sessionWindowEndUnityFrame=stamp!=null?stamp.unityFrame:0;
            if(stamp!=null){report.anchoredUnityFrameDelta=report.sessionWindowEndUnityFrame-report.sessionWindowStartUnityFrame;report.anchoredRealSeconds=report.sessionWindowEndRealSeconds-report.sessionWindowStartRealSeconds;Require(report.anchoredRealSeconds>0 && report.anchoredUnityFrameDelta>0,"Invalid capture-clock measurement window.");report.anchoredGameFps=report.anchoredUnityFrameDelta/report.anchoredRealSeconds;}
            report.processCpuSeconds=Math.Max(0,Cpu()-cpuAt);report.processCpuPercentOneCore=100*report.processCpuSeconds/report.measuredRealSeconds;report.processCpuPercentAllCores=report.processCpuPercentOneCore/Math.Max(1,SystemInfo.processorCount);
            actors.Add(ReadActors(report.measuredRealSeconds));report.gameFrameWallMs=Stats(frames);report.profilerMainThreadMs=Stats(main);report.profilerRenderThreadMs=Stats(render);report.profilerGpuMs=Stats(gpu);report.nativeGpuMs=Stats(nativeGpu);report.gpuTimingAvailable=gpu.Count+nativeGpu.Count>0;
            diagnostics?.SetMetadata("measuredWindowEndRealSeconds",Number(report.sessionWindowEndRealSeconds));
            if(world.IsCollecting)world.CaptureSession.RecordMarker("benchmark_measurement_stopped","Closing and queue drain excluded from measured window.");Finish(true);
        }
        void Finish(bool success)
        {
            if(finishing)return;finishing=true;requestedSuccess=success;closingAt=clock.Elapsed.TotalSeconds;closingFrame=Time.frameCount;
            // Preserve measurements on early failure; queue draining is a separate window.
            if(measuring && report.measuredRealSeconds==0)
            {
                report.measuredRealSeconds=closingAt-measureAt;report.measuredUnityFrames=Time.frameCount-startFrame;
                report.endUnityFrame=Time.frameCount;report.gameFps=report.measuredUnityFrames/Math.Max(.000001,report.measuredRealSeconds);
                report.gameFrameWallMs=Stats(frames);report.profilerMainThreadMs=Stats(main);report.profilerRenderThreadMs=Stats(render);
                report.profilerGpuMs=Stats(gpu);report.nativeGpuMs=Stats(nativeGpu);
            }
            try
            {
                double callAt=clock.Elapsed.TotalSeconds;
                if(world && world.IsCollecting)world.StopDataCollection(success?"capture_benchmark_complete":"capture_benchmark_failed");
                else if(world && !world.Paused)world.TogglePause();
                report.stopCallMilliseconds=(clock.Elapsed.TotalSeconds-callAt)*1000;
            }
            catch(Exception error){errors.Add(error.ToString());requestedSuccess=false;}
            PollFinalization();
        }
        void PollFinalization()
        {
            if(reportWritten)return;
            bool pending=world && world.CaptureSession && world.CaptureSession.IsFinalizing;
            if(pending && clock.Elapsed.TotalSeconds-closingAt<120)return;
            reportWritten=true;
            try
            {
                Require(!pending,"Capture finalization exceeded 120 seconds.");
                report.closeSeconds=clock.Elapsed.TotalSeconds-closingAt;report.finalizingUnityFrames=Time.frameCount-closingFrame;
                report.closed=!world || (!world.IsCollecting && (!world.CaptureSession || !world.CaptureSession.IsFinalizing));
                if(world && world.CaptureSession && !string.IsNullOrEmpty(world.CaptureSession.LastError))errors.Add(world.CaptureSession.LastError);
                diagnostics?.SetMetadata("measuredGameFps",Number(report.gameFps));diagnostics?.FlushSnapshot(report.diagnosticReport);
                if(requestedSuccess && !string.IsNullOrEmpty(report.captureDirectory))ReadFinalizedVideos();
                report.success=requestedSuccess && errors.Count==0;
            }
            catch(Exception error){errors.Add(error.ToString());report.success=false;}
            finally
            {
                report.actors=actors.ToArray();report.errors=errors.ToArray();report.completedUtc=DateTime.UtcNow.ToString("O");
                File.WriteAllText(report.reportPath,JsonUtility.ToJson(report,true),new UTF8Encoding(false));
                DisposeRecorders();Application.logMessageReceived-=OnLog;Application.Quit(report.success?0:2);
            }
        }
        void ReadFinalizedVideos()
        {
            var captureReader=new StationDecisionCaptureReader(report.captureDirectory);
            var manifest=JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(report.captureDirectory,"manifest.json")));
            Require(manifest.status=="complete" && (manifest.errors==null || manifest.errors.Length==0),"Capture close failed.");var results=new List<CameraResult>();
            Require(report.sessionWindowStartSyncSampleId>0 && report.sessionWindowEndSyncSampleId>report.sessionWindowStartSyncSampleId,"Measured capture clock boundaries are missing.");
            StationDecisionBenchmarkConfiguration.RequireExactCameraSet(report.resolvedCameraIds,manifest.selectedCameraIds,"manifest selected cameras");
            report.finalizedCameraIds=(manifest.cameraResults??Array.Empty<ManifestCamera>()).Select(c=>c.cameraId).OrderBy(id=>id,StringComparer.Ordinal).ToArray();
            report.finalizedCameraCount=report.finalizedCameraIds.Length;
            StationDecisionBenchmarkConfiguration.RequireExactCameraSet(report.resolvedCameraIds,report.finalizedCameraIds,"finalized cameras");
            foreach(var camera in manifest.cameraResults??Array.Empty<ManifestCamera>())
            {
                report.sessionVideoFramesWritten+=camera.framesWritten;report.sessionVideoRequestsDropped+=camera.requestsDropped;
                Require(!string.IsNullOrWhiteSpace(camera.directory),"Finalized camera directory is missing: "+camera.cameraId);
                string directory=Path.GetFullPath(Path.Combine(report.captureDirectory,camera.directory.Replace('/',Path.DirectorySeparatorChar))),path=Path.Combine(directory,"frames.jsonl");
                Require(directory.StartsWith(Path.GetFullPath(report.captureDirectory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Finalized camera directory escaped capture root.");
                var times=new List<double>();long total=0;
                foreach(string line in captureReader.ReadLines(camera.directory.TrimEnd('/', '\\') + "/frames.jsonl"))
                {
                    if(string.IsNullOrWhiteSpace(line))continue;var frame=JsonUtility.FromJson<VideoFrame>(line);Require(frame.stamp!=null,"Accepted frame has no clock stamp.");++total;
                    if(frame.stamp.syncSampleId>report.sessionWindowStartSyncSampleId && frame.stamp.syncSampleId<=report.sessionWindowEndSyncSampleId)times.Add(frame.stamp.realElapsedSeconds);
                }
                Require(total==camera.framesWritten,"Sidecar/encoded frame counts differ: "+camera.cameraId);Require(times.Count>0,"No accepted frames in measured window: "+camera.cameraId);
                var intervals=new List<double>();for(int i=1;i<times.Count;++i){Require(times[i]>times[i-1],"Accepted stamps must increase.");intervals.Add((times[i]-times[i-1])*1000);}
                Require(times.Count<=report.anchoredUnityFrameDelta,"Accepted camera frames exceed measured game frames: "+camera.cameraId);
                results.Add(new CameraResult{cameraId=camera.cameraId,sidecarPath=path,sessionFramesWritten=camera.framesWritten,sessionRequestsDropped=camera.requestsDropped,sidecarFrames=total,measuredAcceptedFrames=times.Count,firstAcceptedRealSeconds=times.First(),lastAcceptedRealSeconds=times.Last(),acceptedFps=times.Count/report.anchoredRealSeconds,acceptedIntervalFps=times.Count>1?(times.Count-1)/(times.Last()-times.First()):0,acceptedToGameFrameRatio=times.Count/(double)report.anchoredUnityFrameDelta,sessionEncodedPlaybackSeconds=camera.framesWritten/(double)config.fps,bytesWritten=Directory.GetFiles(directory,"*",SearchOption.AllDirectories).Sum(p=>(double)new FileInfo(p).Length),acceptedIntervalMs=Stats(intervals)});
            }
            Require(results.Count==report.resolvedCameraCount,"Finalized camera count differs from resolved selection.");report.finalizedCameraSetVerified=true;report.cameras=results.ToArray();
            if(results.Count>0){report.cameraGameFrameRatioMin=results.Min(c=>c.acceptedToGameFrameRatio);report.cameraGameFrameRatioMax=results.Max(c=>c.acceptedToGameFrameRatio);report.cameraGameFrameRatioMean=results.Average(c=>c.acceptedToGameFrameRatio);}
        }
        ActorSample ReadActors(double real)=>new ActorSample{realSeconds=real,simulationSeconds=world.rail.ElapsedSimulationSeconds,activePassengers=world.ActivePassengerCount,activePoolObjects=world.participants.Count(p=>p && p.gameObject.activeInHierarchy),enteredJourneys=world.arrivalController.EnteredCount,representedActivePassengers=world.arrivalController.RepresentedActiveCount};
        void CollectNativeGpu()
        {
            if(!report.frameTimingEnabled)return;FrameTimingManager.CaptureFrameTimings();
            if(FrameTimingManager.GetLatestTimings(1,timing)==0 || timing[0].frameStartTimestamp==lastTiming)return;lastTiming=timing[0].frameStartTimestamp;
            if(timing[0].gpuFrameTime>0 && !double.IsNaN(timing[0].gpuFrameTime))nativeGpu.Add(timing[0].gpuFrameTime);
        }
        static void Collect(ProfilerRecorder recorder,List<double> values){if(recorder.Valid && recorder.Count>0 && recorder.LastValue>0 && recorder.UnitType.ToString()=="TimeNanoseconds")values.Add(recorder.LastValue/1000000d);}
        static ProfilerRecorder TryRecorder(ProfilerCategory category,string name){try{return ProfilerRecorder.StartNew(category,name,1);}catch{return default;}}
        static string RecorderStatus(ProfilerRecorder recorder)=>recorder.Valid?"Available; unit="+recorder.UnitType:"Unavailable in this Player; no zero-cost inference.";
        void DisposeRecorders(){if(disposed)return;disposed=true;if(mainRecorder.Valid)mainRecorder.Dispose();if(renderRecorder.Valid)renderRecorder.Dispose();if(gpuRecorder.Valid)gpuRecorder.Dispose();}
        void OnDestroy(){Application.logMessageReceived-=OnLog;DisposeRecorders();}
        void OnLog(string message,string stack,LogType type){if((type==LogType.Error || type==LogType.Exception || type==LogType.Assert) && errors.Count<50)errors.Add(message+"\n"+stack);}
        static double Cpu(){using(var process=Process.GetCurrentProcess())return process.TotalProcessorTime.TotalSeconds;}
        static string Hash(string text){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","").ToLowerInvariant();}
        static string Number(double value)=>value.ToString("R",CultureInfo.InvariantCulture);
        static Statistic Stats(List<double> values){if(values.Count==0)return new Statistic();var s=values.OrderBy(v=>v).ToArray();int n=s.Length;return new Statistic{count=n,min=s[0],max=s[n-1],mean=s.Average(),median=n%2==0?(s[n/2-1]+s[n/2])*.5:s[n/2],p95=s[Math.Max(0,(int)Math.Ceiling(n*.95)-1)],p99=s[Math.Max(0,(int)Math.Ceiling(n*.99)-1)]};}
        static Hardware ReadHardware()=>new Hardware{operatingSystem=SystemInfo.operatingSystem,deviceModel=SystemInfo.deviceModel,processorType=SystemInfo.processorType,processorCount=SystemInfo.processorCount,processorFrequencyMHz=SystemInfo.processorFrequency,systemMemoryMB=SystemInfo.systemMemorySize,graphicsDeviceName=SystemInfo.graphicsDeviceName,graphicsVendor=SystemInfo.graphicsDeviceVendor,graphicsVersion=SystemInfo.graphicsDeviceVersion,graphicsApi=SystemInfo.graphicsDeviceType.ToString(),renderingThreadingMode=SystemInfo.renderingThreadingMode.ToString(),graphicsMemoryMB=SystemInfo.graphicsMemorySize,screenWidth=Screen.width,screenHeight=Screen.height,supportsAsyncGpuReadback=SystemInfo.supportsAsyncGPUReadback,renderPipeline=GraphicsSettings.currentRenderPipeline?GraphicsSettings.currentRenderPipeline.name:"Built-in",quality=QualitySettings.names[QualitySettings.GetQualityLevel()]};
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
