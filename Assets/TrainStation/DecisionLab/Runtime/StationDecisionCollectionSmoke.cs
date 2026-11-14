using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Explicit command-line verification hook; no component or behavior is created during a normal player launch.</summary>
    public sealed class StationDecisionCollectionSmoke : MonoBehaviour
    {
        const string Flag="--station-collection-smoke";
        const double CaptureSeconds=8;
        static readonly string[] HallCameraIds={"HALL-01","HALL-02","HALL-03","HALL-04"};
        StationDecisionWorld world;
        int captureWidth=854,captureHeight=480,captureFps=30;
        readonly Stopwatch clock=new Stopwatch();
        readonly List<string> errors=new List<string>();
        Report report;
        double captureBegan,closingAt;
        bool requestedSuccess,reportWritten;
        bool started,finishing;
        [Serializable] sealed class Report
        {
            public bool success,initialPaused,currentLocalDate,portableStorageBesidePlayer,closed,speedResetOnStart,accelerationBlocked,speedUnlockedAfterStop;
            public string startedUtc,completedUtc,executableDirectory,workingDirectory,storageRoot,reportPath,scene,launchId,captureRunId,captureDirectory,operatingDate,selectedCamera;
            public string captureCadence;
            public string[] selectedCameras;
            public int seed,services,unityFrames,videoWidth,videoHeight,nominalPlaybackFps,measuredPerformanceSamples;
            public int sharedVideoSyncSamples,registeredAgents,agentProfiles;
            public long videoFrames,videoDropped;
            public double realCaptureSeconds,simulationSeconds;
            public string[] errors;
            public List<CameraEvidence> cameras=new List<CameraEvidence>();
        }
        [Serializable] sealed class CameraEvidence {public string cameraId,calibrationPath;public long framesWritten;public int exactStampFrames;public bool calibrationValid;}
        [Serializable] sealed class CaptureManifest {public string status,captureCadence,storageLayout,agentIndexFile,agentsDirectory; public int nominalPlaybackFps;public string[] errors,selectedCameraIds;public CameraResult[] cameraResults;}
        [Serializable] sealed class AgentProfileSnapshot {public StationDecisionCaptureStamp captureStamp;public AgentIdentity profile;public JourneyIdentity journey;}
        [Serializable] sealed class AgentIdentity {public string agentId;}
        [Serializable] sealed class JourneyIdentity {public string journeyId;}
        [Serializable] sealed class CameraResult {public string cameraId;public long framesWritten,requestsDropped;}
        [Serializable] sealed class SyncRecord {public string kind;public StationDecisionCaptureStamp stamp;public StationDecisionMeasuredFrameRates measuredFrameRates;}
        [Serializable] sealed class VideoFrame {public StationDecisionCaptureStamp stamp;public FrameCamera camera;}
        [Serializable] sealed class FrameCamera
        {public bool capturedPostProcessing;public float[] worldToCamera,cameraToWorld,projection,inverseProjection,gpuProjection,intrinsicMatrix,inverseIntrinsicMatrix,worldToCvCamera,cvCameraToWorld;}
        [Serializable] sealed class CameraCalibration
        {
            public string calibrationVersion,cameraModel,cameraId;public int width,height,fps;public bool orthographic,capturedPostProcessing;
            public float[] worldToCameraMatrix,cameraToWorldMatrix,projectionMatrix,inverseProjectionMatrix,gpuProjectionMatrix,intrinsicMatrix,inverseIntrinsicMatrix,
                worldToCvCameraMatrix,cvCameraToWorldMatrix,unityViewToCvCameraMatrix,distortionCoefficients;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void InstallWhenExplicitlyRequested()
        {
            if (Application.isEditor || !Environment.GetCommandLineArgs().Any(a=>string.Equals(a,Flag,StringComparison.Ordinal)))return;
            var target=new GameObject("Explicit station collection smoke verification");
            DontDestroyOnLoad(target);target.AddComponent<StationDecisionCollectionSmoke>();
        }

        void Awake()
        {
            clock.Start();Application.runInBackground=true;Application.targetFrameRate=30;
            Application.logMessageReceived+=OnLog;
            report=new Report {startedUtc=DateTime.UtcNow.ToString("O"),executableDirectory=Path.GetFullPath(Path.GetDirectoryName(Application.dataPath)??"."),
                workingDirectory=Path.GetFullPath(Environment.CurrentDirectory),storageRoot=StationDecisionCaptureSession.StorageRoot};
            report.reportPath=Path.Combine(report.storageRoot,"portable-smoke-report.json");
        }

        void Update()
        {
            if(finishing){PollFinalization();return;}
            try
            {
                if(clock.Elapsed.TotalSeconds>120)throw new TimeoutException("Portable collection smoke timed out before it could close its runtime capture.");
                if(!started)
                {
                    if(!world)world=FindObjectsByType<StationDecisionWorld>(FindObjectsSortMode.None).FirstOrDefault(w=>w.IsDaily);
                    if(!world || !world.rail || !world.rail.IsReady || !world.arrivalController || !world.arrivalController.IsReady || !world.CaptureSession)return;
                    ConfigureVideoRequest(); report.videoWidth=captureWidth;report.videoHeight=captureHeight;report.nominalPlaybackFps=captureFps;
                    report.scene=world.gameObject.scene.path;report.initialPaused=world.Paused && world.rail.IsPaused && Math.Abs(world.ElapsedSeconds)<.001;
                    Require(report.initialPaused,"Player advanced before explicit collection Start.");
                    Require(world.RunSession!=null,"Portable daily launch did not generate a run session.");
                    report.operatingDate=world.RunSession.operatingDateIso;report.currentLocalDate=report.operatingDate==DateTimeOffset.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
                    Require(report.currentLocalDate,"Portable simulation did not initialize on the current local date.");
                    report.launchId=world.RunSession.runId;report.seed=world.RunSession.seed;report.services=world.rail.schedule.services.Length;
                    Require(world.rail.ValidateConfiguration().Count==0,"Portable railway configuration is invalid.");
                    string expected=Path.GetFullPath(Path.Combine(report.executableDirectory,"Captures","TrainStationDaily"));
                    report.portableStorageBesidePlayer=string.Equals(expected,report.storageRoot,StringComparison.OrdinalIgnoreCase);
                    Require(report.portableStorageBesidePlayer,"Storage does not resolve beside the running player.");
                    var cameras=world.CaptureSession.AvailableCameras;
                    Require(HallCameraIds.All(id=>cameras.Count(c=>c.cameraId==id)==1),"All four fixed main-hall cameras HALL-01 through HALL-04 are required in the player.");
                    report.selectedCamera=HallCameraIds[0];report.selectedCameras=(string[])HallCameraIds.Clone();
                    var options=new StationDecisionCaptureOptions {video=true,timetable=true,profiles=true,trajectories=true,animations=true,decisions=true,events=true,
                        selectedCameraIds=(string[])HallCameraIds.Clone(),videoWidth=captureWidth,videoHeight=captureHeight,videoFps=captureFps};
                    world.SetSimulationSpeed(8f);
                    Require(world.SimulationSpeed==8f && !world.SimulationSpeedLocked,"Observation speed controls are locked before Start.");
                    Require(world.BeginDataCollection(options),"Portable collection Start failed: "+world.CaptureSession.LastError);
                    report.speedResetOnStart=world.SimulationSpeedLocked && world.SimulationSpeed==1f && Time.timeScale==1f;
                    Require(report.speedResetOnStart,"Portable collection did not reset to x1.");
                    foreach(float requested in new[]{4f,8f}) world.SetSimulationSpeed(requested);
                    report.accelerationBlocked=world.SimulationSpeed==1f && Time.timeScale==1f;
                    Require(report.accelerationBlocked,"Portable collection accepted acceleration.");
                    report.captureDirectory=world.OutputDirectory;report.captureRunId=world.CaptureSession.RunId;
                    Require(Path.GetFullPath(report.captureDirectory).StartsWith(report.storageRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Capture escaped the executable-local storage root.");
                    captureBegan=clock.Elapsed.TotalSeconds;started=true;
                }
                else
                {
                    Require(world && world.CaptureSession && world.IsCollecting,"Portable capture stopped unexpectedly.");
                    Require(world.SimulationSpeedLocked && world.SimulationSpeed==1f && Time.timeScale==1f,"Portable recording speed drifted.");
                    if(clock.Elapsed.TotalSeconds-captureBegan<CaptureSeconds)return;
                    report.realCaptureSeconds=clock.Elapsed.TotalSeconds-captureBegan;
                    report.videoFrames=world.CaptureSession.VideoFramesWritten;report.videoDropped=world.CaptureSession.VideoFramesDropped;
                    report.simulationSeconds=world.ElapsedSeconds;report.unityFrames=Time.frameCount;
                    Require(report.videoFrames>0 && report.simulationSeconds>0,"Portable player produced no video or movement time.");
                    Finish(errors.Count==0);
                }
            }
            catch(Exception error){errors.Add(error.ToString());Finish(false);}
        }

        void ConfigureVideoRequest()
        {
            string[] arguments=Environment.GetCommandLineArgs();
            for(int i=0;i<arguments.Length;++i)
            {
                if(arguments[i]=="--station-smoke-resolution")
                {
                    Require(i+1<arguments.Length,"Missing smoke resolution.");string value=arguments[++i].ToLowerInvariant();
                    if(value=="480"){captureWidth=854;captureHeight=480;}
                    else if(value=="720"){captureWidth=1280;captureHeight=720;}
                    else if(value=="hd" || value=="1080"){captureWidth=1920;captureHeight=1080;}
                    else throw new ArgumentException("Smoke resolution must be480,720 orHD.");
                }

            }
        }

        void VerifyClosedCameras()
        {
            var manifest=JsonUtility.FromJson<CaptureManifest>(File.ReadAllText(Path.Combine(report.captureDirectory,"manifest.json")));
            var captureReader=new StationDecisionCaptureReader(report.captureDirectory);
            report.videoFrames=(manifest.cameraResults??Array.Empty<CameraResult>()).Sum(c=>c.framesWritten);report.videoDropped=(manifest.cameraResults??Array.Empty<CameraResult>()).Sum(c=>c.requestsDropped);
            report.captureCadence=manifest.captureCadence;
            Require(manifest.captureCadence=="each_unity_frame" && manifest.nominalPlaybackFps==captureFps,"Capture must observe Unity frames with separately labelled nominal AVI playback timing.");
            Require(manifest.status=="complete" && (manifest.errors==null || manifest.errors.Length==0),"Portable capture did not close successfully.");
            Require(manifest.selectedCameraIds!=null && manifest.selectedCameraIds.OrderBy(id=>id,StringComparer.Ordinal).SequenceEqual(HallCameraIds),"Portable capture did not retain exactly the four selected hall cameras.");
            Require(manifest.storageLayout=="per_agent.v3" && manifest.agentsDirectory=="agents" && manifest.agentIndexFile=="agents/index.json","Portable player lacks per-agent layout metadata.");
            var agentIndex=JsonUtility.FromJson<StationDecisionAgentsIndex>(File.ReadAllText(Path.Combine(report.captureDirectory,manifest.agentIndexFile)));
            Require(agentIndex.runId==report.captureRunId && agentIndex.agents!=null && agentIndex.agents.Length==world.arrivalController.GeneratedPlan.Count,"Portable player registry misses logical journeys.");
            report.registeredAgents=agentIndex.agents.Length;
            foreach(var entry in agentIndex.agents)
            {
                string profilePath=Path.Combine(report.captureDirectory,entry.directory,"profile.json");
                Require(File.Exists(profilePath),"Portable personal profile missing: "+entry.agentId);
                var personal=JsonUtility.FromJson<AgentProfileSnapshot>(captureReader.ReadJson(entry.directory + "/profile.json"));
                Require(personal.profile!=null && personal.profile.agentId==entry.agentId && personal.journey!=null && personal.journey.journeyId==entry.agentId && personal.captureStamp!=null && personal.captureStamp.runId==report.captureRunId,"Portable player mixed profile/plan identities.");
                ++report.agentProfiles;
            }
            Require(new[]{"profiles.json","trajectories.jsonl","animations.jsonl","decisions.jsonl"}.All(f=>!File.Exists(Path.Combine(report.captureDirectory,f))),"Portable player retains flat avatar duplicates.");
            var stamps=new Dictionary<long,StationDecisionCaptureStamp>();
            foreach(string line in captureReader.ReadLines("sync.jsonl"))
            {
                if(string.IsNullOrWhiteSpace(line))continue;
                var record=JsonUtility.FromJson<SyncRecord>(line);
                if(record.kind=="measured_frame_rates")
                {
                    var rates=record.measuredFrameRates;
                    Require(rates!=null && rates.windowSeconds==1d && rates.gameFpsRolling1s>0d && rates.cameras!=null && rates.cameras.Length==4,"Measured frame-rate telemetry is incomplete.");
                    Require(rates.cameras.All(c=>HallCameraIds.Contains(c.cameraId) && c.videoFpsRolling1s>=0d && !double.IsInfinity(c.videoFpsRolling1s) && !double.IsNaN(c.videoFpsRolling1s) && c.framesWritten>=0 && c.requestsDropped>=0),"Invalid measured camera frame rates.");
                    ++report.measuredPerformanceSamples;
                }
                if(record.kind=="clock" && record.stamp!=null)
                {
                    Require(record.stamp.timeScale==(record.stamp.paused?0f:1f),"Recorded runtime stamp contains acceleration.");
                    stamps[record.stamp.syncSampleId]=record.stamp;
                }
            }
            Require(report.measuredPerformanceSamples>=5,"No periodic observed FPS telemetry was saved.");
            var cameraSyncSets=new List<HashSet<long>>();
            foreach(string cameraId in HallCameraIds)
            {
                var result=manifest.cameraResults==null?null:manifest.cameraResults.SingleOrDefault(c=>c.cameraId==cameraId);
                Require(result!=null && result.framesWritten>0,"A selected hall camera produced no encoded video in the player: "+cameraId);
                string directory=Path.Combine(report.captureDirectory,"video",cameraId),calibrationPath=Path.Combine(directory,"calibration.json"),sidecar=Path.Combine(directory,"frames.jsonl");
                Require(File.Exists(calibrationPath) && File.Exists(sidecar),"A selected hall camera is missing calibration or frame stamps: "+cameraId);
                var calibration=JsonUtility.FromJson<CameraCalibration>(File.ReadAllText(calibrationPath));
                Require(calibration.cameraId==cameraId && calibration.width==captureWidth && calibration.height==captureHeight && calibration.fps==captureFps && CalibrationValid(calibration),
                    "A selected hall camera has incomplete metric calibration: "+cameraId);
                var evidence=new CameraEvidence{cameraId=cameraId,calibrationPath=calibrationPath,framesWritten=result.framesWritten,calibrationValid=true};report.cameras.Add(evidence);
                var cameraSync=new HashSet<long>();cameraSyncSets.Add(cameraSync);
                foreach(string line in captureReader.ReadLines("video/" + cameraId + "/frames.jsonl"))
                {
                    if(string.IsNullOrWhiteSpace(line))continue;
                    Require(line.Contains("\"captureIntervalRealSeconds\"") && line.Contains("\"captureFpsInstantaneous\"") && line.Contains("\"captureFpsRolling1s\""),"Saved frame lacks observed timing fields.");
                    var frame=JsonUtility.FromJson<VideoFrame>(line);
                    Require(FrameCameraValid(frame.camera),"A player video frame lacks its pinhole intrinsic/extrinsic matrices or retained postprocessing: "+cameraId);
                    Require(frame.stamp!=null && stamps.TryGetValue(frame.stamp.syncSampleId,out var stamp) && stamp.runId==report.captureRunId && frame.stamp.runId==stamp.runId &&
                        stamp.unityFrame==frame.stamp.unityFrame && Math.Abs(stamp.simulationSeconds-frame.stamp.simulationSeconds)<.0001 && Math.Abs(stamp.realElapsedSeconds-frame.stamp.realElapsedSeconds)<.0001,
                        "A camera video frame cannot be joined to its exact runtime sync stamp: "+cameraId);
                    cameraSync.Add(frame.stamp.syncSampleId);++evidence.exactStampFrames;
                }
                Require(evidence.exactStampFrames==evidence.framesWritten,"Encoded video frame count differs from the synchronized sidecar: "+cameraId);
            }
            foreach(var set in cameraSyncSets.Skip(1)) cameraSyncSets[0].IntersectWith(set);report.sharedVideoSyncSamples=cameraSyncSets[0].Count;
            Require(report.sharedVideoSyncSamples>0,"The four hall cameras have no frames with a shared exact runtime synchronization stamp.");
        }
        static bool MatrixValid(float[] values)=>values!=null && values.Length==16 && values.All(value=>!float.IsNaN(value) && !float.IsInfinity(value));
        static bool IntrinsicsValid(float[] values)=>values!=null && values.Length==9 && values.All(value=>!float.IsNaN(value) && !float.IsInfinity(value)) &&
            values[0]>0 && values[4]>0 && Math.Abs(values[6])<.00001 && Math.Abs(values[7])<.00001 && Math.Abs(values[8]-1)<.00001;
        static bool CalibrationValid(CameraCalibration c)=>c!=null && c.calibrationVersion=="stationflow.pinhole.v1" && c.cameraModel=="ideal-pinhole" && !c.orthographic && !c.capturedPostProcessing &&
            MatrixValid(c.worldToCameraMatrix) && MatrixValid(c.cameraToWorldMatrix) && MatrixValid(c.projectionMatrix) && MatrixValid(c.inverseProjectionMatrix) && MatrixValid(c.gpuProjectionMatrix) &&
            MatrixValid(c.worldToCvCameraMatrix) && MatrixValid(c.cvCameraToWorldMatrix) && MatrixValid(c.unityViewToCvCameraMatrix) && IntrinsicsValid(c.intrinsicMatrix) && IntrinsicsValid(c.inverseIntrinsicMatrix) &&
            c.distortionCoefficients!=null && c.distortionCoefficients.Length==5 && c.distortionCoefficients.All(v=>v==0);
        static bool FrameCameraValid(FrameCamera c)=>c!=null && !c.capturedPostProcessing && MatrixValid(c.worldToCamera) && MatrixValid(c.cameraToWorld) && MatrixValid(c.projection) &&
            MatrixValid(c.inverseProjection) && MatrixValid(c.gpuProjection) && MatrixValid(c.worldToCvCamera) && MatrixValid(c.cvCameraToWorld) && IntrinsicsValid(c.intrinsicMatrix) && IntrinsicsValid(c.inverseIntrinsicMatrix);

        void Finish(bool success)
        {
            if(finishing)return;finishing=true;requestedSuccess=success;closingAt=clock.Elapsed.TotalSeconds;
            try {if(world && world.IsCollecting)world.StopDataCollection(success?"portable_smoke_complete":"portable_smoke_failed");}
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
                Require(!pending,"Portable capture finalization exceeded 120 seconds.");
                if(requestedSuccess)
                {
                    Require(string.IsNullOrEmpty(world.CaptureSession.LastError),"Portable capture close failed: "+world.CaptureSession.LastError);
                    report.closed=!world.IsCollecting && !world.CaptureSession.IsFinalizing && File.Exists(Path.Combine(report.captureDirectory,"manifest.json"));
                    Require(report.closed,"Portable capture manifest was not closed.");VerifyClosedCameras();world.SetSimulationSpeed(4f);
                    report.speedUnlockedAfterStop=!world.SimulationSpeedLocked && world.SimulationSpeed==4f && world.Paused && Time.timeScale==0f;
                    Require(report.speedUnlockedAfterStop,"Portable Stop did not unlock acceleration.");
                }
                report.success=requestedSuccess && errors.Count==0;
            }
            catch(Exception error){errors.Add(error.ToString());report.success=false;}
            finally
            {
                report.completedUtc=DateTime.UtcNow.ToString("O");report.errors=errors.ToArray();Directory.CreateDirectory(report.storageRoot);
                File.WriteAllText(report.reportPath,JsonUtility.ToJson(report,true));Application.logMessageReceived-=OnLog;Application.Quit(report.success?0:2);
            }
        }
        void OnLog(string message,string stack,LogType type){if(type==LogType.Error || type==LogType.Exception || type==LogType.Assert)errors.Add(message+"\n"+stack);}
        void OnDestroy(){Application.logMessageReceived-=OnLog;}
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
