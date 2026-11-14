#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Real Play evidence for the normalized format; only runtime lens changes and labelled fixture events.</summary>
    [InitializeOnLoad]
    public static class StationDecisionNormalizationVerification
    {
        const string Key="StationFlow.TrainStation.NormalizationVerification", ScenePath="Assets/TrainStation/Scenes/TrainStationDaily.unity";
        public const string ReportPath="Reports/TrainStationDaily/RuntimeInitialization/normalization-play-check.json";
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static Run run; static StationDecisionWorld world; static Camera camera; static double nextPoll,nextSave;
        [Serializable] public sealed class FileEvidence { public string path,before,after; }
        [Serializable] public sealed class SavedScene { public string path; public bool loaded,active; }
        [Serializable] public sealed class CaptureEvidence
        {
            public string name,directory,runId,status,testAgentId;
            public bool closed,optionFilesVerified,clocksJoined,x1ResetAndLocked,logicalReaderVerified,exactCameraRestoration;
            public int clockRecords,videoFrames,cameraStates,trajectoryRecords,animationRecords,eventRecords,profileFiles,profileSamples,registeredAgents,activeAgentsAtStart;
            public long videoDrops,commandsAccepted,commandsCompleted; public double realSeconds,simulationSeconds;
        }
        [Serializable] public sealed class Run
        {
            public bool success,initialPaused,filesPreserved,settingsRestored,sceneSetupRestored;
            public string phase,startedUtc,completedUtc,archivePath,oldPlayScene;
            public long deadlineTicks; public int initializeFrame,caseIndex,oldOptions,oldFps,oldVsync;
            public bool oldOptionsEnabled,oldPaused,oldBackground,lensChanged,lensRestored;
            public float oldScale,originalFov; public double phaseStarted,caseSimulationStart;
            public string scope="One actual Daily Play lifecycle; video-only with tiny runtime FOV change/restore, trajectories+animations without profiles, events-only and profiles-only. New format only; no simulation-time skip. Fixture events labelled normalization_fixture_*. Saved settings/assets restored.";
            public List<FileEvidence> files=new List<FileEvidence>(); public List<SavedScene> scenes=new List<SavedScene>();
            public List<CaptureEvidence> captures=new List<CaptureEvidence>(); public List<string> errors=new List<string>();
        }
        [Serializable] sealed class Manifest
        {
            public string schema,storageLayout,recordEncoding,normalizationFile,recordDefinitionsFile,rostersFile,stateCheckpointsFile,runId,status,agentIndexFile,agentsDirectory,captureCadence;
            public string[] errors,selectedCameraIds; public long dataCommandsAccepted,dataCommandsCompleted; public CameraResult[] cameraResults;
        }
        [Serializable] sealed class CameraResult { public string cameraId,cameraStateFile; public long framesWritten,requestsDropped,cameraStatesWritten; }
        [Serializable] sealed class ClockRecord { public string kind; public StationDecisionCaptureStamp stamp; }
        [Serializable] sealed class RawFrame { public int segment,videoFrame; public long syncSampleId,cameraStateId,requestId; public double videoTimeSeconds; }
        [Serializable] sealed class Frame { public StationDecisionCaptureStamp stamp; public CameraGeometry camera; public int segment,videoFrame; public long requestId; public double videoTimeSeconds; }
        [Serializable] sealed class CameraState { public long cameraStateId,effectiveFromSyncSampleId; public CameraGeometry camera; }
        [Serializable] sealed class CameraGeometry { public float[] projection,intrinsicMatrix; public float verticalFieldOfViewDegrees; public bool capturedPostProcessing; }
        [Serializable] sealed class Observation { public StationDecisionCaptureStamp captureStamp; public string agentId,kind; public ProfileIdentity profile; }
        [Serializable] sealed class ProfileIdentity { public string agentId; }
        [Serializable] sealed class JourneyIdentity { public string journeyId; }
        [Serializable] sealed class ProfileFile { public StationDecisionCaptureStamp captureStamp; public ProfileIdentity profile; public JourneyIdentity journey; }
        static StationDecisionNormalizationVerification() { EditorApplication.update+=Tick; EditorApplication.playModeStateChanged+=ModeChanged; Application.logMessageReceived+=OnLog; }
        public static void Begin()
        {
            Require(string.IsNullOrEmpty(SessionState.GetString(Key,"")),"Normalization fixture already pending.");
            Require(!EditorApplication.isPlayingOrWillChangePlaymode&&!EditorApplication.isCompiling,"Stop Play before verification.");
            for(int i=0;i<SceneManager.sceneCount;++i)Require(!SceneManager.GetSceneAt(i).isDirty,"Save dirty scenes before verification.");
            run=new Run { startedUtc=DateTime.UtcNow.ToString("O"),phase="entering",deadlineTicks=DateTime.UtcNow.AddMinutes(8).Ticks,
                archivePath="Reports/TrainStationDaily/RuntimeInitialization/normalization-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")+".json",
                oldPlayScene=AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene),oldOptionsEnabled=EditorSettings.enterPlayModeOptionsEnabled,
                oldOptions=(int)EditorSettings.enterPlayModeOptions,oldPaused=EditorApplication.isPaused,oldBackground=Application.runInBackground,
                oldFps=Application.targetFrameRate,oldVsync=QualitySettings.vSyncCount,oldScale=Time.timeScale,files=SnapshotFiles(),
                scenes=EditorSceneManager.GetSceneManagerSetup().Select(s=>new SavedScene{path=s.path,loaded=s.isLoaded,active=s.isActive}).ToList() };
            Save();WriteReport();
            try { if(SceneManager.GetActiveScene().path!=ScenePath)EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
                EditorSettings.enterPlayModeOptionsEnabled=false;EditorSceneManager.playModeStartScene=AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
                EditorApplication.isPaused=false;EditorApplication.isPlaying=true; }
            catch(Exception e){Error(e.ToString());run.phase="restore";Save();WriteReport();}
        }
        static void Initialize()
        {
            Require(SceneManager.GetActiveScene().path==ScenePath,"Wrong Daily scene.");world=Components<StationDecisionWorld>().Single();
            Require(world.rail&&world.rail.IsReady&&world.CaptureSession&&world.RunSession!=null,"Daily services not initialized.");
            Require(world.Paused&&world.rail.IsPaused&&Math.Abs(world.ElapsedSeconds)<.001&&!world.CaptureSession.IsRecording,"Daily did not enter paused.");
            run.initialPaused=true;camera=world.CaptureSession.AvailableCameras.Single(c=>c.cameraId=="HALL-01").cameraComponent;run.originalFov=camera.fieldOfView;
            Application.runInBackground=true;Application.targetFrameRate=30;QualitySettings.vSyncCount=0;StartCase(0);
        }
        static void StartCase(int index)
        {
            Require(world&&!world.CaptureSession.IsRecording&&world.Paused,"Previous capture did not stop and pause.");run.caseIndex=index;
            var options=new StationDecisionCaptureOptions{video=index==0,timetable=false,profiles=index==3,trajectories=index==1,animations=index==1,skeletons=false,decisions=false,events=index==2,
                selectedCameraIds=index==0?new[]{"HALL-01"}:Array.Empty<string>(),videoWidth=854,videoHeight=480,videoFps=30};
            world.SetSimulationSpeed(8f);Require(world.SimulationSpeed==8f&&Time.timeScale==0f,"Stopped capture did not unlock speed.");
            Require(world.BeginDataCollection(options),"Start failed: "+world.CollectionError+" "+world.CaptureSession.LastError);
            Require(world.SimulationSpeedLocked&&world.SimulationSpeed==1f&&Time.timeScale==1f,"Start failed to reset/lock x1.");
            world.SetSimulationSpeed(4f);world.SetSimulationSpeed(8f);Require(world.SimulationSpeed==1f&&Time.timeScale==1f,"Capture accepted acceleration.");
            var item=new CaptureEvidence{name=new[]{"video-only-three-camera-states","trajectories-animations-no-profiles","events-only","profiles-only"}[index],directory=world.OutputDirectory,runId=world.CaptureSession.RunId,x1ResetAndLocked=true,activeAgentsAtStart=world.ActivePassengerCount};
            Require(run.captures.All(c=>c.directory!=item.directory&&c.runId!=item.runId),"Capture identity reused.");run.captures.Add(item);
            run.phaseStarted=EditorApplication.timeSinceStartup;run.caseSimulationStart=world.ElapsedSeconds;run.phase="recording";
            if(index==2){world.LogEvent("normalization_fixture_station",null,"Explicit station event for normalized events-only verification.");
                var p=Components<StationDecisionPassenger>().FirstOrDefault(x=>x&&x.HasEntered&&!x.Finished);Require(p,"Events-only needs an entered agent.");item.testAgentId=p.JourneyId;
                world.LogEvent("normalization_fixture_agent",p.serviceId,"Explicit agent event; profiles disabled.",p.JourneyId);}
            Save();WriteReport();
        }
        static void Tick()
        {
            if(EditorApplication.timeSinceStartup<nextPoll)return;nextPoll=EditorApplication.timeSinceStartup+.05;Load();
            if(run==null||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            try
            {
                if(run.phase=="restore"&&!EditorApplication.isPlayingOrWillChangePlaymode){Restore();return;}
                Require(DateTime.UtcNow.Ticks<run.deadlineTicks,"Normalization fixture timeout.");
                if(run.phase=="initialize"&&EditorApplication.isPlaying&&Time.frameCount>=run.initializeFrame)Initialize();
                else if(run.phase=="recording"&&EditorApplication.isPlaying)
                {
                    Require(world&&world.CaptureSession.IsRecording,"Capture stopped unexpectedly.");
                    Require(world.SimulationSpeedLocked&&world.SimulationSpeed==1f&&Time.timeScale==1f,"Speed lock drifted.");
                    double duration=EditorApplication.timeSinceStartup-run.phaseStarted;
                    if(run.caseIndex==0){if(duration>=2&&!run.lensChanged){camera.fieldOfView=run.originalFov+.03125f;run.lensChanged=true;Save();}
                        if(duration>=3&&!run.lensRestored){camera.fieldOfView=run.originalFov;run.lensRestored=true;Save();}}
                    double needed=run.caseIndex==0?5d:run.caseIndex==1?3d:2d;
                    if(duration>=needed&&world.ElapsedSeconds-run.caseSimulationStart>=(run.caseIndex==0?0:needed*.9))
                    {
                        var item=run.captures[run.caseIndex];item.realSeconds=duration;item.simulationSeconds=world.ElapsedSeconds-run.caseSimulationStart;
                        world.StopDataCollection("normalization_fixture_"+run.caseIndex+"_complete");world.CaptureSession.CompletePendingStopForShutdown();Require(world.Paused&&!world.SimulationSpeedLocked,"Stop did not pause/unlock.");
                        VerifyCapture(item,run.caseIndex);if(run.caseIndex<3)StartCase(run.caseIndex+1);
                        else{run.phase="exiting";Save();WriteReport();EditorApplication.isPlaying=false;}
                    }
                }
                if(EditorApplication.timeSinceStartup>=nextSave){nextSave=EditorApplication.timeSinceStartup+1;Save();WriteReport();}
            }
            catch(Exception e)
            {
                Error(e.ToString());try{if(camera)camera.fieldOfView=run.originalFov;if(world)world.StopDataCollection("normalization_fixture_failed");}catch(Exception close){Error(close.ToString());}
                run.phase=EditorApplication.isPlayingOrWillChangePlaymode?"exiting":"restore";Save();WriteReport();if(EditorApplication.isPlayingOrWillChangePlaymode)EditorApplication.isPlaying=false;
            }
        }
        static void VerifyCapture(CaptureEvidence item,int index)
        {
            var manifest=JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(item.directory,"manifest.json"),Utf8));
            item.status=manifest.status;item.commandsAccepted=manifest.dataCommandsAccepted;item.commandsCompleted=manifest.dataCommandsCompleted;
            Require(manifest.schema=="stationflow.station_capture.v4"&&manifest.storageLayout=="per_agent.v3"&&manifest.recordEncoding=="references-and-state-revisions.v1","Wrong normalized format.");
            Require(manifest.status=="complete"&&(manifest.errors==null||manifest.errors.Length==0)&&manifest.dataCommandsAccepted==manifest.dataCommandsCompleted,"Capture failed to drain/close.");
            Require(manifest.runId==item.runId&&manifest.captureCadence=="each_unity_frame","Identity or cadence changed.");
            foreach(string f in new[]{manifest.normalizationFile,manifest.recordDefinitionsFile,manifest.rostersFile,manifest.stateCheckpointsFile})Require(!string.IsNullOrEmpty(f)&&File.Exists(Path.Combine(item.directory,f)),"Missing normalization definition: "+f);
            var reader=new StationDecisionCaptureReader(item.directory);var clocks=new Dictionary<long,StationDecisionCaptureStamp>();
            foreach(string line in reader.ReadLines("sync.jsonl")){var r=JsonUtility.FromJson<ClockRecord>(line);if(r.kind!="clock")continue;
                Require(r.stamp!=null&&r.stamp.runId==item.runId&&r.stamp.syncSampleId>0&&!string.IsNullOrEmpty(r.stamp.utc),"Clock failed expansion.");
                Require(r.stamp.timeScale==(r.stamp.paused?0f:1f),"Acceleration in saved clock.");clocks.Add(r.stamp.syncSampleId,r.stamp);}
            item.clockRecords=clocks.Count;Require(clocks.Count>0,"No canonical clocks.");
            Require(!File.Exists(Path.Combine(item.directory,"timetable.json"))&&!File.Exists(Path.Combine(item.directory,"train-states.jsonl")),"Unchecked timetable written.");
            string agents=Path.Combine(item.directory,"agents");var files=Directory.Exists(agents)?Directory.GetFiles(agents,"*",SearchOption.AllDirectories):Array.Empty<string>();var names=files.Select(Path.GetFileName).ToArray();
            Require(!names.Contains("decisions.jsonl"),"Unchecked decisions written.");
            if(index!=3)Require(!names.Contains("profile.json"),"Unchecked profiles written.");
            if(index!=1)Require(!names.Contains("trajectories.jsonl")&&!names.Contains("animations.jsonl"),"Unchecked movement/animation written.");
            if(index!=2)Require(!names.Contains("events.jsonl")&&!File.Exists(Path.Combine(item.directory,"events.jsonl")),"Unchecked events written.");
            if(index==0){Require(!Directory.Exists(agents)&&string.IsNullOrEmpty(manifest.agentIndexFile)&&string.IsNullOrEmpty(manifest.agentsDirectory),"Video-only advertised avatar data.");VerifyVideo(item,manifest,reader,clocks);}
            else
            {
                Require(!Directory.Exists(Path.Combine(item.directory,"video"))&&(manifest.selectedCameraIds?.Length??0)==0,"Data-only rendered video.");
                var roster=JsonUtility.FromJson<StationDecisionAgentsIndex>(File.ReadAllText(Path.Combine(item.directory,"agents/index.json"),Utf8));Require(roster.runId==item.runId&&roster.agents.Length>0,"Agents index missing.");item.registeredAgents=roster.agents.Length;
                if(index==1)
                {
                    foreach(var agent in roster.agents)foreach(string stream in new[]{"trajectories.jsonl","animations.jsonl"})
                    {
                        string path=agent.directory+"/"+stream;if(!File.Exists(Path.Combine(item.directory,path)))continue;
                        foreach(string line in reader.ReadLines(path)){var record=JsonUtility.FromJson<Observation>(line);VerifyStamp(record.captureStamp,item.runId,clocks);Require(record.agentId==agent.agentId,"Agent identity failed expansion.");if(stream=="trajectories.jsonl")++item.trajectoryRecords;else++item.animationRecords;}
                    }
                    Require(item.trajectoryRecords>0&&item.animationRecords>0,"No actual movement and animation observations.");
                }
                else if(index==2)
                {
                    bool station=false,individual=false;
                    foreach(string line in reader.ReadLines("events.jsonl")){var r=JsonUtility.FromJson<Observation>(line);VerifyStamp(r.captureStamp,item.runId,clocks);++item.eventRecords;if(r.kind=="normalization_fixture_station")station=true;}
                    var selected=roster.agents.Single(a=>a.agentId==item.testAgentId);
                    foreach(string line in reader.ReadLines(selected.directory+"/events.jsonl")){var r=JsonUtility.FromJson<Observation>(line);VerifyStamp(r.captureStamp,item.runId,clocks);++item.eventRecords;if(r.kind=="normalization_fixture_agent"){Require(r.agentId==item.testAgentId&&(r.profile==null||string.IsNullOrEmpty(r.profile.agentId)),"Profiles-off event leaked profile or lost identity.");individual=true;}}
                    Require(station&&individual,"Station/individual test events not expanded.");
                }
                else
                {
                    item.profileFiles=names.Count(f=>f=="profile.json");Require(item.profileFiles==roster.agents.Length,"Expected one profile per planned logical agent.");
                    foreach(var agent in new[]{roster.agents.First(),roster.agents.Last()}.GroupBy(a=>a.agentId).Select(g=>g.First()))
                    {var profile=JsonUtility.FromJson<ProfileFile>(reader.ReadJson(agent.directory+"/profile.json"));Require(profile.profile!=null&&profile.profile.agentId==agent.agentId&&profile.journey!=null&&profile.journey.journeyId==agent.agentId,"Profile/journey expansion wrong.");VerifyStamp(profile.captureStamp,item.runId,clocks);++item.profileSamples;}
                }
            }
            item.clocksJoined=item.logicalReaderVerified=item.optionFilesVerified=item.closed=true;
        }
        static void VerifyVideo(CaptureEvidence item,Manifest manifest,StationDecisionCaptureReader reader,Dictionary<long,StationDecisionCaptureStamp> clocks)
        {
            Require(manifest.selectedCameraIds.SequenceEqual(new[]{"HALL-01"})&&manifest.cameraResults.Length==1,"Unexpected selected cameras.");
            string relative="video/HALL-01/frames.jsonl";
            var states=File.ReadLines(Path.Combine(item.directory,"video/HALL-01/"+StationDecisionCameraStateWriter.FileName),Utf8).Where(l=>!string.IsNullOrWhiteSpace(l)).Select(l=>JsonUtility.FromJson<CameraState>(l)).ToArray();
            Require(states.Length==3&&states.Select(s=>s.cameraStateId).SequenceEqual(new long[]{1,2,3}),"Lens change/restore did not create exactly three revisions.");
            Require(states[0].camera.projection.SequenceEqual(states[2].camera.projection)&&!states[0].camera.projection.SequenceEqual(states[1].camera.projection),"Original projection not restored or tiny change not recorded.");
            Require(states[0].camera.verticalFieldOfViewDegrees==states[2].camera.verticalFieldOfViewDegrees&&states[0].camera.verticalFieldOfViewDegrees!=states[1].camera.verticalFieldOfViewDegrees,"FOV revisions wrong.");
            var raw=File.ReadLines(Path.Combine(item.directory,relative),Utf8).Where(l=>!string.IsNullOrWhiteSpace(l)).ToArray();var expanded=reader.ReadLines(relative).ToArray();
            Require(raw.Length>0&&raw.Length==expanded.Length,"Reader lost a saved video frame.");var referenced=new HashSet<long>();
            for(int i=0;i<raw.Length;++i)
            {
                Require(!raw[i].Contains("\"stamp\":")&&!raw[i].Contains("\"camera\":"),"Repeated inline clock or camera remains.");
                var frame=JsonUtility.FromJson<RawFrame>(raw[i]);var logical=JsonUtility.FromJson<Frame>(expanded[i]);VerifyStamp(logical.stamp,item.runId,clocks);
                Require(frame.syncSampleId==logical.stamp.syncSampleId&&frame.segment==logical.segment&&frame.videoFrame==logical.videoFrame&&frame.requestId==logical.requestId&&frame.videoTimeSeconds==logical.videoTimeSeconds,"Saved pixel identity or clock changed.");
                var state=states.Single(s=>s.cameraStateId==frame.cameraStateId);Require(state.effectiveFromSyncSampleId<=frame.syncSampleId&&clocks.ContainsKey(state.effectiveFromSyncSampleId),"State reference not yet effective.");
                Require(logical.camera!=null&&logical.camera.projection.SequenceEqual(state.camera.projection)&&logical.camera.intrinsicMatrix.SequenceEqual(state.camera.intrinsicMatrix)&&!logical.camera.capturedPostProcessing,"Geometry expansion wrong.");
                foreach(string field in new[]{"captureIntervalRealSeconds","captureFpsInstantaneous","captureFpsRolling1s"})Require(NumberToken(raw[i],field)==NumberToken(expanded[i],field),"Variable FPS lost: "+field);
                referenced.Add(frame.cameraStateId);
            }
            Require(referenced.Count==3,"No actual saved observation in each lens state.");var result=manifest.cameraResults.Single();
            Require(result.cameraStateFile==StationDecisionCameraStateWriter.FileName&&result.cameraStatesWritten==3&&result.framesWritten==raw.Length,"Manifest state/frame totals disagree.");
            item.videoFrames=raw.Length;item.cameraStates=states.Length;item.videoDrops=result.requestsDropped;item.exactCameraRestoration=true;
        }
        static double? NumberToken(string line,string field)
        {
            var match=Regex.Match(line,"\""+field+"\"\\s*:\\s*(null|[-+0-9.eE]+)");Require(match.Success,"Missing numeric field: "+field);
            return match.Groups[1].Value=="null"?(double?)null:double.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
        }
        static void VerifyStamp(StationDecisionCaptureStamp stamp,string runId,Dictionary<long,StationDecisionCaptureStamp> clocks)
        {Require(stamp!=null&&stamp.runId==runId&&clocks.TryGetValue(stamp.syncSampleId,out var canonical)&&JsonUtility.ToJson(stamp)==JsonUtility.ToJson(canonical),"Original exact clock failed expansion.");}
        static void ModeChanged(PlayModeStateChange state)
        {
            Load();if(run==null)return;
            if(state==PlayModeStateChange.EnteredPlayMode){run.phase="initialize";run.initializeFrame=Time.frameCount+8;Save();}
            else if(state==PlayModeStateChange.ExitingPlayMode){if(run.phase!="exiting")Error("Play stopped before fixture completed.");run.phase="exiting";Save();}
            else if(state==PlayModeStateChange.EnteredEditMode){run.phase="restore";world=null;camera=null;Save();}
        }
        static void Restore()
        {
            EditorSettings.enterPlayModeOptionsEnabled=run.oldOptionsEnabled;EditorSettings.enterPlayModeOptions=(EnterPlayModeOptions)run.oldOptions;
            EditorSceneManager.playModeStartScene=string.IsNullOrEmpty(run.oldPlayScene)?null:AssetDatabase.LoadAssetAtPath<SceneAsset>(run.oldPlayScene);
            EditorApplication.isPaused=run.oldPaused;Application.runInBackground=run.oldBackground;Application.targetFrameRate=run.oldFps;QualitySettings.vSyncCount=run.oldVsync;Time.timeScale=run.oldScale;
            run.settingsRestored=EditorSettings.enterPlayModeOptionsEnabled==run.oldOptionsEnabled&&(int)EditorSettings.enterPlayModeOptions==run.oldOptions&&EditorApplication.isPaused==run.oldPaused&&Application.targetFrameRate==run.oldFps&&QualitySettings.vSyncCount==run.oldVsync&&Time.timeScale==run.oldScale&&Application.runInBackground==run.oldBackground&&AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene)==run.oldPlayScene;
            try{EditorSceneManager.RestoreSceneManagerSetup(run.scenes.Select(s=>new SceneSetup{path=s.path,isLoaded=s.loaded,isActive=s.active}).ToArray());
                var restored=EditorSceneManager.GetSceneManagerSetup();run.sceneSetupRestored=restored.Length==run.scenes.Count&&restored.Select((s,i)=>s.path==run.scenes[i].path&&s.isLoaded==run.scenes[i].loaded&&s.isActive==run.scenes[i].active).All(v=>v);
            }catch(Exception e){Error("Scene setup restore: "+e);}
            run.filesPreserved=true;foreach(var f in run.files){f.after=File.Exists(f.path)?Digest(f.path):"missing";if(f.after!=f.before){run.filesPreserved=false;Error("Saved asset changed: "+f.path);}}
            if(!run.initialPaused||run.captures.Count!=4||run.captures.Any(c=>!c.closed||!c.x1ResetAndLocked||!c.logicalReaderVerified))Error("Not all four capture option cases completed.");
            if(!run.settingsRestored||!run.sceneSetupRestored)Error("Editor settings/scene setup not restored.");
            run.phase="complete";run.completedUtc=DateTime.UtcNow.ToString("O");run.success=run.errors.Count==0;WriteReport();SessionState.EraseString(Key);run=null;world=null;camera=null;
        }
        static List<FileEvidence> SnapshotFiles()
        {
            var paths=new HashSet<string>{ScenePath,"Assets/TrainStation/Scenes/TrainStation.unity","Assets/TrainStation/Scenes/TrainStationDecisionLab.unity",
                "Assets/TrainStation/DecisionLab/Resources/StationDecisionDaySchedule.asset","Assets/TrainStation/DecisionLab/Resources/StationDecisionDayScenario.asset",
                "ProjectSettings/NavMeshAreas.asset","ProjectSettings/QualitySettings.asset","ProjectSettings/GraphicsSettings.asset","Assets/Settings/PC_RPAsset.asset"};
            foreach(string guid in AssetDatabase.FindAssets("t:NavMeshData",new[]{"Assets/TrainStation"}))paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            foreach(string guid in AssetDatabase.FindAssets("t:StationRailSchedule",new[]{"Assets/TrainStation"}))paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            return paths.Where(File.Exists).OrderBy(p=>p,StringComparer.Ordinal).Select(p=>new FileEvidence{path=p,before=Digest(p)}).ToList();
        }
        static string Digest(string path){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant();}
        static T[] Components<T>()where T:Component=>SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<T>(true)).ToArray();
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        static void Error(string message){if(run!=null&&!run.errors.Contains(message)&&run.errors.Count<100)run.errors.Add(message);}
        static void OnLog(string message,string stack,LogType type){if(run!=null&&(type==LogType.Error||type==LogType.Exception||type==LogType.Assert))Error(message);}
        static void Save(){if(run!=null)SessionState.SetString(Key,JsonUtility.ToJson(run));}
        static void Load(){if(run==null){string value=SessionState.GetString(Key,"");if(!string.IsNullOrEmpty(value))run=JsonUtility.FromJson<Run>(value);}}
        static void WriteReport(){if(run==null)return;Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));string json=JsonUtility.ToJson(run,true);File.WriteAllText(ReportPath,json,Utf8);File.WriteAllText(run.archivePath,json,Utf8);}
    }
}
#endif
