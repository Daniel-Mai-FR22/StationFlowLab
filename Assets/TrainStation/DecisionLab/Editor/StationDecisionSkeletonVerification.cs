#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using J = StationFlow.TrainStation.DecisionLab.StationDecisionJsonObject;
namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Actual persisted float32 worldXYZ versus bones observed at CCTV rendering; no2D computation.</summary>
    [InitializeOnLoad]
    public static class StationDecisionSkeletonVerification
    {
        const string Key="StationFlow.TrainStation.SkeletonVerification", ScenePath="Assets/TrainStation/Scenes/TrainStationDaily.unity";
        public const string ReportPath="Reports/TrainStationDaily/RuntimeInitialization/skeleton-play-check.json";
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static readonly string[] Hall={"HALL-01","HALL-02","HALL-03","HALL-04"};
        static Run run; static StationDecisionWorld world; static Camera referenceCamera; static double nextPoll,nextSave;
        [Serializable] public sealed class FileEvidence { public string path,before,after; }
        [Serializable] public sealed class SavedScene { public string path;public bool loaded,active; }
        [Serializable] public sealed class ExpectedPose { public string agentId,avatarName,actorSlotId,tag;public long syncSampleId;public float[] xyz;public bool[] valid; }
        [Serializable] public sealed class CaptureEvidence
        {
            public string name,directory,runId,status;
            public bool closed,optionFilesVerified,clocksJoined,x1ResetAndLocked,readerVerified,pausedPoseVerified,allCameraFramesHavePoses;
            public int clockRecords,registeredAgents,observedAgents,poses,bindings,definitions,checkpoints,maxAgentPoses,videoFrames,pausedPoses,expectedPosesMatched,expectedValidJointsMatched,reusedActorSlots;
            public long commandsAccepted,commandsCompleted,videoDrops;public double realSeconds,simulationSeconds;
        }
        [Serializable] public sealed class Run
        {
            public bool success,initialPaused,filesPreserved,settingsRestored,sceneSetupRestored;
            public string phase,startedUtc,completedUtc,archivePath,oldPlayScene;
            public long deadlineTicks;public int initializeFrame,caseIndex,oldOptions,oldFps,oldVsync;
            public bool oldOptionsEnabled,oldPaused,oldBackground;public float oldScale;
            public double phaseStarted,pauseStarted,caseSimulationStart,pausedSimulationSeconds;
            public string scope="Actual Daily Play; skeleton-only5Hz, four Hall videos+skeletons with actual1s pause and live beginCameraRendering bone oracle, trajectories with skeletons disabled, all-off. World XYZ only; no2D, quaternion, muscle or time skip. Saved scenes/assets/settings restored.";
            public List<FileEvidence> files=new List<FileEvidence>();public List<SavedScene> scenes=new List<SavedScene>();
            public List<CaptureEvidence> captures=new List<CaptureEvidence>();public List<ExpectedPose> expected=new List<ExpectedPose>();public List<string> errors=new List<string>();
        }
        [Serializable] sealed class Manifest
        {
            public string schema,storageLayout,recordEncoding,runId,status,agentIndexFile,agentsDirectory,skeletonDefinitionsFile,skeletonPoseFileName,skeletonBindingsFileName,skeletonIndexFileName,skeletonPoseFormat;
            public string[] errors,selectedCameraIds;public long dataCommandsAccepted,dataCommandsCompleted;public StationDecisionCaptureOptions captureOptions;public CameraResult[] cameraResults;
        }
        [Serializable] sealed class CameraResult { public string cameraId;public long framesWritten,requestsDropped; }
        [Serializable] sealed class ClockRecord { public string kind;public StationDecisionCaptureStamp stamp; }
        [Serializable] sealed class Frame { public StationDecisionCaptureStamp stamp; }
        [Serializable] sealed class Rig { public string schema,rigId,coordinateSystem,units,positionEncoding;public string[] jointNames,transformPaths;public int[] parentIndices; }
        [Serializable] sealed class Binding { public int bindingId;public string rigId,actorSlotId,avatarName;public long effectiveFromSyncSampleId; }
        [Serializable] sealed class IndexRow { public int observationIndex,bindingId;public long syncSampleId,byteOffset; }
        sealed class Pose { public long sid,offset;public int sample,binding,joints,length;public float time;public float[] xyz;public bool[] valid; }
        static StationDecisionSkeletonVerification(){EditorApplication.update+=Tick;EditorApplication.playModeStateChanged+=ModeChanged;Application.logMessageReceived+=OnLog;RenderPipelineManager.beginCameraRendering+=BeforeCamera;}
        public static void Begin()
        {
            Require(string.IsNullOrEmpty(SessionState.GetString(Key,"")),"Skeleton fixture already pending.");
            Require(!EditorApplication.isPlayingOrWillChangePlaymode&&!EditorApplication.isCompiling,"Stop Play before skeleton verification.");
            for(int i=0;i<SceneManager.sceneCount;++i)Require(!SceneManager.GetSceneAt(i).isDirty,"Save dirty scenes before skeleton verification.");
            run=new Run{startedUtc=DateTime.UtcNow.ToString("O"),phase="entering",deadlineTicks=DateTime.UtcNow.AddMinutes(8).Ticks,
                archivePath="Reports/TrainStationDaily/RuntimeInitialization/skeleton-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")+".json",
                oldPlayScene=AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene),oldOptionsEnabled=EditorSettings.enterPlayModeOptionsEnabled,oldOptions=(int)EditorSettings.enterPlayModeOptions,
                oldPaused=EditorApplication.isPaused,oldBackground=Application.runInBackground,oldFps=Application.targetFrameRate,oldVsync=QualitySettings.vSyncCount,oldScale=Time.timeScale,
                files=SnapshotFiles(),scenes=EditorSceneManager.GetSceneManagerSetup().Select(s=>new SavedScene{path=s.path,loaded=s.isLoaded,active=s.isActive}).ToList()};
            Save();WriteReport();try{if(SceneManager.GetActiveScene().path!=ScenePath)EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
                EditorSettings.enterPlayModeOptionsEnabled=false;EditorSceneManager.playModeStartScene=AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);EditorApplication.isPaused=false;EditorApplication.isPlaying=true;
            }catch(Exception e){Error(e.ToString());run.phase="restore";Save();WriteReport();}
        }
        static void Initialize()
        {
            Require(SceneManager.GetActiveScene().path==ScenePath,"Wrong Daily scene.");world=Components<StationDecisionWorld>().Single();
            Require(world.rail&&world.rail.IsReady&&world.CaptureSession&&world.RunSession!=null,"Daily services not initialized.");
            Require(world.Paused&&world.rail.IsPaused&&Math.Abs(world.ElapsedSeconds)<.001&&!world.CaptureSession.IsRecording,"Daily did not start paused.");
            run.initialPaused=true;referenceCamera=world.CaptureSession.AvailableCameras.Single(c=>c.cameraId==Hall[0]).cameraComponent;
            Application.runInBackground=true;Application.targetFrameRate=60;QualitySettings.vSyncCount=0;StartCase(0);
        }
        static void StartCase(int index)
        {
            Require(world&&!world.IsCollecting&&world.Paused,"Previous capture did not stop/pause.");run.caseIndex=index;
            var options=new StationDecisionCaptureOptions{video=index==1,timetable=false,profiles=false,trajectories=index==2,animations=false,skeletons=index<2,decisions=false,events=false,
                selectedCameraIds=index==1?(string[])Hall.Clone():Array.Empty<string>(),videoWidth=854,videoHeight=480,videoFps=30};
            var panel=world.GetComponent<StationDecisionCollectionPanel>();Require(panel,"Collection panel absent.");panel.SetOptions(options);var selected=panel.SelectedOptions;
            Require(selected.skeletons==options.skeletons&&selected.video==options.video&&!selected.animations&&!selected.profiles,"Independent skeleton choices lost by UI binding.");
            world.SetSimulationSpeed(8f);Require(world.BeginDataCollection(options),"Start failed: "+world.CollectionError+" "+world.CaptureSession.LastError);
            Require(world.SimulationSpeedLocked&&world.SimulationSpeed==1f&&Time.timeScale==1f,"Capture did not reset/lock x1.");world.SetSimulationSpeed(4f);world.SetSimulationSpeed(8f);Require(world.SimulationSpeed==1f,"Capture accepted acceleration.");
            var item=new CaptureEvidence{name=new[]{"skeleton-only","video-skeleton-only","trajectories-with-skeletons-disabled","all-off"}[index],directory=world.OutputDirectory,runId=world.CaptureSession.RunId,x1ResetAndLocked=true};
            Require(run.captures.All(c=>c.directory!=item.directory&&c.runId!=item.runId),"Capture identity reused.");run.captures.Add(item);run.phaseStarted=EditorApplication.timeSinceStartup;run.caseSimulationStart=world.ElapsedSeconds;run.phase="recording";Save();WriteReport();
        }
        static void Tick()
        {
            if(EditorApplication.timeSinceStartup<nextPoll)return;nextPoll=EditorApplication.timeSinceStartup+.05;Load();if(run==null||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            try
            {
                if(run.phase=="restore"&&!EditorApplication.isPlayingOrWillChangePlaymode){Restore();return;}
                Require(DateTime.UtcNow.Ticks<run.deadlineTicks,"Skeleton fixture timeout.");
                if(run.phase=="initialize"&&EditorApplication.isPlaying&&Time.frameCount>=run.initializeFrame)Initialize();
                else if((run.phase=="recording"||run.phase=="paused")&&EditorApplication.isPlaying)
                {
                    Require(world&&world.IsCollecting,"Capture stopped unexpectedly.");Require(run.errors.Count==0,"Skeleton capture oracle recorded an error.");
                    Require(world.SimulationSpeedLocked&&world.SimulationSpeed==1f&&Time.timeScale==(world.Paused?0f:1f),"Recording speed drifted.");
                    double duration=EditorApplication.timeSinceStartup-run.phaseStarted;
                    if(run.caseIndex==1&&run.phase=="recording"&&duration>=12)
                    {world.TogglePause();Require(world.Paused,"Pause failed.");run.pausedSimulationSeconds=world.ElapsedSeconds;run.pauseStarted=EditorApplication.timeSinceStartup;run.phase="paused";Save();}
                    else if(run.caseIndex==1&&run.phase=="paused")
                    {
                        Require(Math.Abs(world.ElapsedSeconds-run.pausedSimulationSeconds)<.001,"Simulation advanced in pause.");
                        if(EditorApplication.timeSinceStartup-run.pauseStarted>=1&&run.expected.Any(x=>x.tag=="video-paused-1"))FinishCase(duration);
                    }
                    else if(run.caseIndex!=1&&duration>=(run.caseIndex==0?3:run.caseIndex==2?2:1))FinishCase(duration);
                }
                if(EditorApplication.timeSinceStartup>=nextSave){nextSave=EditorApplication.timeSinceStartup+1;Save();WriteReport();}
            }
            catch(Exception e)
            {
                Error(e.ToString());try{if(world)world.StopDataCollection("skeleton_fixture_failed");}catch(Exception close){Error(close.ToString());}
                run.phase=EditorApplication.isPlayingOrWillChangePlaymode?"exiting":"restore";Save();WriteReport();if(EditorApplication.isPlayingOrWillChangePlaymode)EditorApplication.isPlaying=false;
            }
        }
        static void BeforeCamera(ScriptableRenderContext context,Camera camera)
        {
            if(run==null||run.caseIndex!=1||(run.phase!="recording"&&run.phase!="paused")||camera!=referenceCamera||!world||!world.IsCollecting)return;
            try
            {
                string tag=null;double duration=EditorApplication.timeSinceStartup-run.phaseStarted;
                if(world.Paused){if(!run.expected.Any(x=>x.tag=="video-paused-0"))tag="video-paused-0";
                    else if(EditorApplication.timeSinceStartup-run.pauseStarted>=.25&&!run.expected.Any(x=>x.tag=="video-paused-1"))tag="video-paused-1";}
                else if(!run.expected.Any(x=>x.tag=="video-running-0"))tag="video-running-0";
                else if(duration>=3&&!run.expected.Any(x=>x.tag=="video-running-1"))tag="video-running-1";
                if(tag==null)return;var stamp=world.CaptureSession.GetStamp();Require(stamp!=null,"Render oracle lacks capture clock.");
                foreach(var p in world.participants.Where(p=>p&&p.HasEntered&&!p.Finished&&p.gameObject.activeInHierarchy))
                {
                    var animator=p.motion&&p.motion.animationPlayer?p.motion.animationPlayer.TargetAnimator:null;Require(animator&&animator.isHuman&&animator.avatar&&animator.avatar.isValid,"Render oracle requires a valid Humanoid.");
                    int joints=(int)HumanBodyBones.LastBone;var expected=new ExpectedPose{agentId=p.JourneyId,avatarName=animator.avatar.name,actorSlotId="slot-"+Array.IndexOf(world.participants,p).ToString("000"),tag=tag,syncSampleId=stamp.syncSampleId,xyz=new float[joints*3],valid=new bool[joints]};
                    for(int i=0;i<joints;++i){var bone=animator.GetBoneTransform((HumanBodyBones)i);if(!bone)continue;var v=bone.position;if(!Finite(v.x)||!Finite(v.y)||!Finite(v.z))continue;expected.valid[i]=true;expected.xyz[i*3]=v.x;expected.xyz[i*3+1]=v.y;expected.xyz[i*3+2]=v.z;}
                    run.expected.Add(expected);
                }
                Require(run.expected.Any(x=>x.tag==tag),"No entered agent in render oracle batch.");
            }
            catch(Exception e){Error("Live render bone oracle: "+e);}
        }
        static void FinishCase(double duration)
        {
            var item=run.captures[run.caseIndex];item.realSeconds=duration;item.simulationSeconds=world.ElapsedSeconds-run.caseSimulationStart;
            world.StopDataCollection("skeleton_fixture_"+run.caseIndex+"_complete");world.CaptureSession.CompletePendingStopForShutdown();Require(world.Paused&&!world.SimulationSpeedLocked,"Stop did not pause/unlock.");Verify(item,run.caseIndex);
            if(run.caseIndex<3)StartCase(run.caseIndex+1);else{run.phase="exiting";Save();WriteReport();EditorApplication.isPlaying=false;}
        }
        static void Verify(CaptureEvidence item,int index)
        {
            var manifest=JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(item.directory,"manifest.json"),Utf8));item.status=manifest.status;item.commandsAccepted=manifest.dataCommandsAccepted;item.commandsCompleted=manifest.dataCommandsCompleted;
            Require(manifest.schema=="stationflow.station_capture.v4"&&manifest.storageLayout=="per_agent.v3"&&manifest.recordEncoding=="references-and-state-revisions.v1","Wrong current format.");
            Require(manifest.runId==item.runId&&manifest.status=="complete"&&(manifest.errors==null||manifest.errors.Length==0)&&manifest.dataCommandsAccepted==manifest.dataCommandsCompleted,"Capture failed to drain/finalize.");
            bool skeleton=index<2;Require(manifest.captureOptions.skeletons==skeleton&&!manifest.captureOptions.profiles&&!manifest.captureOptions.animations&&!manifest.captureOptions.events&&!manifest.captureOptions.timetable&&!manifest.captureOptions.decisions,"Independent options changed.");
            var reader=new StationDecisionCaptureReader(item.directory);var clocks=new Dictionary<long,StationDecisionCaptureStamp>();
            foreach(string line in reader.ReadLines("sync.jsonl")){var c=JsonUtility.FromJson<ClockRecord>(line);if(c.kind=="clock"){Require(c.stamp!=null&&c.stamp.runId==item.runId&&c.stamp.timeScale==(c.stamp.paused?0f:1f),"Invalid canonical clock.");clocks.Add(c.stamp.syncSampleId,c.stamp);}}
            item.clockRecords=clocks.Count;Require(item.clockRecords>0,"No canonical clocks.");
            var files=Directory.GetFiles(item.directory,"*",SearchOption.AllDirectories);var names=files.Select(Path.GetFileName).ToArray();
            Require(!names.Contains("profile.json")&&!names.Contains("animations.jsonl")&&!names.Contains("events.jsonl")&&!names.Contains("timetable.json")&&!names.Contains("train-states.jsonl"),"Disabled data stream leaked.");
            if(!skeleton)
            {
                Require(string.IsNullOrEmpty(manifest.skeletonDefinitionsFile)&&string.IsNullOrEmpty(manifest.skeletonPoseFileName)&&string.IsNullOrEmpty(manifest.skeletonBindingsFileName)&&string.IsNullOrEmpty(manifest.skeletonIndexFileName)&&string.IsNullOrEmpty(manifest.skeletonPoseFormat),"Disabled skeleton advertised metadata.");
                Require(!names.Any(x=>x.StartsWith("skeleton-",StringComparison.Ordinal)),"Disabled skeleton stored a file.");
                if(index==3)Require(!Directory.Exists(Path.Combine(item.directory,"agents"))&&!Directory.Exists(Path.Combine(item.directory,"video"))&&string.IsNullOrEmpty(manifest.agentIndexFile),"All-off retained optional storage.");
                else Require(names.Contains("trajectories.jsonl")&&!Directory.Exists(Path.Combine(item.directory,"video")),"Trajectories-only control did not record movement.");
                item.closed=item.optionFilesVerified=item.clocksJoined=item.readerVerified=true;return;
            }
            Require(manifest.skeletonDefinitionsFile=="skeleton-definitions.jsonl"&&manifest.skeletonPoseFileName=="skeleton-poses.bin"&&manifest.skeletonBindingsFileName=="skeleton-bindings.jsonl"&&manifest.skeletonIndexFileName=="skeleton-index.jsonl"&&manifest.skeletonPoseFormat=="stationflow.skeleton_pose_binary.v1","Wrong skeleton manifest paths/format.");
            Require(!names.Contains("trajectories.jsonl"),"Skeleton-only mode leaked trajectories.");
            var rigs=File.ReadLines(Path.Combine(item.directory,manifest.skeletonDefinitionsFile),Utf8).Where(l=>!string.IsNullOrWhiteSpace(l)).Select(l=>JsonUtility.FromJson<Rig>(l)).ToDictionary(x=>x.rigId,StringComparer.Ordinal);item.definitions=rigs.Count;
            int expectedJoints=(int)HumanBodyBones.LastBone;Require(rigs.Count>0,"No rig definitions.");
            foreach(var rig in rigs.Values)
            {
                Require(rig.schema=="stationflow.skeleton_definition.v1"&&rig.coordinateSystem=="UnityWorld"&&rig.units=="metres"&&rig.positionEncoding=="float32","Rig units/space/format wrong.");
                Require(rig.jointNames.Length==expectedJoints&&rig.parentIndices.Length==expectedJoints&&rig.transformPaths.Length==expectedJoints,"Rig has wrong fixed joint width.");
                for(int i=0;i<expectedJoints;++i)Require(rig.jointNames[i]==((HumanBodyBones)i).ToString()&&rig.parentIndices[i]>=-1&&rig.parentIndices[i]<expectedJoints&&rig.parentIndices[i]!=i,"Rig name order/parent invalid.");
            }
            var registry=JsonUtility.FromJson<StationDecisionAgentsIndex>(File.ReadAllText(Path.Combine(item.directory,"agents/index.json"),Utf8));Require(registry.runId==item.runId&&registry.agents.Length>0,"No logical agent registry.");item.registeredAgents=registry.agents.Length;
            var samples=new Dictionary<string,Dictionary<long,Pose>>(StringComparer.Ordinal);var allBindings=new List<Binding>();
            foreach(var agent in registry.agents)
            {
                string path=Path.Combine(item.directory,agent.directory,"skeleton-poses.bin");if(!File.Exists(path))continue;++item.observedAgents;
                var bindings=File.ReadLines(Path.Combine(item.directory,agent.directory,"skeleton-bindings.jsonl"),Utf8).Where(l=>!string.IsNullOrWhiteSpace(l)).Select(l=>JsonUtility.FromJson<Binding>(l)).ToDictionary(b=>b.bindingId);allBindings.AddRange(bindings.Values);item.bindings+=bindings.Count;
                var raw=ReadPoses(path);var byId=raw.ToDictionary(p=>p.sid);samples.Add(agent.agentId,byId);item.poses+=raw.Count;item.maxAgentPoses=Math.Max(item.maxAgentPoses,raw.Count);
                Require(raw.Count>0&&bindings.Count>0,"Empty pose or binding stream.");long last=0;int lastSample=0;
                foreach(var pose in raw)
                {
                    Require(pose.sid>last&&pose.sample>lastSample,"Pose order moved backwards.");last=pose.sid;lastSample=pose.sample;
                    Require(clocks.TryGetValue(pose.sid,out var clock)&&pose.time==(float)clock.simulationSeconds,"Pose not joined to exact capture clock.");
                    Require(bindings.TryGetValue(pose.binding,out var binding)&&rigs.ContainsKey(binding.rigId)&&binding.effectiveFromSyncSampleId<=pose.sid,"Pose binding/rig absent or future.");
                    Require(pose.joints==rigs[binding.rigId].jointNames.Length,"Pose width differs from rig.");if(clock.paused)++item.pausedPoses;
                }
                var checkpoints=File.ReadLines(Path.Combine(item.directory,agent.directory,"skeleton-index.jsonl"),Utf8).Where(l=>!string.IsNullOrWhiteSpace(l)).Select(l=>JsonUtility.FromJson<IndexRow>(l)).ToArray();item.checkpoints+=checkpoints.Length;
                Require(checkpoints.Length==(raw.Count+255)/256,"Skeleton index checkpoint cadence wrong.");
                for(int i=0;i<checkpoints.Length;++i){int observation=1+i*256;var point=checkpoints[i];var pose=raw[observation-1];Require(point.observationIndex==observation&&point.syncSampleId==pose.sid&&point.byteOffset==pose.offset&&point.bindingId==pose.binding,"Skeleton checkpoint offset/identity mismatch.");}
                var logical=reader.ReadLines(agent.directory+"/skeleton-poses.bin").ToArray();Require(logical.Length==raw.Count,"Reader dropped or added poses.");for(int i=0;i<logical.Length;++i)VerifyExpanded(logical[i],raw[i],agent.agentId,bindings[raw[i].binding],item.runId);
            }
            Require(item.poses>0&&item.observedAgents>0,"No actual skeleton observations.");item.reusedActorSlots=allBindings.GroupBy(x=>x.actorSlotId).Count(g=>g.Count()>1);
            if(index==0)
            {
                Require(!Directory.Exists(Path.Combine(item.directory,"video"))&&(manifest.selectedCameraIds?.Length??0)==0,"Skeleton-only created video.");
                Require(item.maxAgentPoses>=5&&item.maxAgentPoses<=Math.Ceiling(item.simulationSeconds*5)+3,"Skeleton-only cadence not independent5Hz.");
            }
            else
            {
                Require(manifest.selectedCameraIds.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(Hall),"Wrong selected video cameras.");Require(item.maxAgentPoses>=257,"Video skeleton run did not cross a checkpoint boundary.");
                var roster=new Dictionary<long,string[]>();
                foreach(string line in reader.ReadLines("sync.jsonl")){var row=J.Parse(line);if(row.Text("kind")!="avatar_snapshot")continue;long sid=J.Parse(row["stamp"]).Long("syncSampleId");roster[sid]=J.Array(row["activeJourneyIds"]).Select(J.Unquote).ToArray();}
                foreach(var camera in manifest.cameraResults)
                {
                    item.videoDrops+=camera.requestsDropped;int count=0;
                    foreach(string line in reader.ReadLines("video/"+camera.cameraId+"/frames.jsonl"))
                    {
                        var frame=JsonUtility.FromJson<Frame>(line);string[] ids=null;Require(frame.stamp!=null&&roster.TryGetValue(frame.stamp.syncSampleId,out ids),"Video frame lacks skeleton roster.");
                        foreach(string id in ids)Require(samples.TryGetValue(id,out var poses)&&poses.ContainsKey(frame.stamp.syncSampleId),"Video frame lacks an active agent pose.");++count;
                    }
                    Require(count==camera.framesWritten&&count>0,"Video counts differ from sidecar.");item.videoFrames+=count;
                }
                item.allCameraFramesHavePoses=true;
                foreach(var expected in run.expected)
                {
                    Pose pose=null;Require(samples.TryGetValue(expected.agentId,out var poses)&&poses.TryGetValue(expected.syncSampleId,out pose),"Live render bone oracle pose missing.");
                    for(int j=0;j<pose.joints;++j)
                    {
                        Require(pose.valid[j]==expected.valid[j],"Live render validity differs from persisted pose.");
                        for(int axis=0;axis<3;++axis)Require(FloatBits(pose.xyz[j*3+axis])==FloatBits(expected.xyz[j*3+axis]),"Persisted worldXYZ differs from actual render bone.");
                        if(pose.valid[j])++item.expectedValidJointsMatched;
                    }
                    ++item.expectedPosesMatched;
                }
                Require(new[]{"video-running-0","video-running-1","video-paused-0","video-paused-1"}.All(tag=>run.expected.Any(x=>x.tag==tag)),"Not all live render oracle phases observed.");
                var pauseA=run.expected.Where(x=>x.tag=="video-paused-0").ToDictionary(x=>x.agentId);var pauseB=run.expected.Where(x=>x.tag=="video-paused-1").ToArray();
                Require(pauseB.Length>0&&pauseA.Count==pauseB.Length,"Pause actor roster changed.");
                foreach(var b in pauseB){Require(pauseA.TryGetValue(b.agentId,out var a)&&a.syncSampleId!=b.syncSampleId&&a.valid.SequenceEqual(b.valid)&&a.xyz.Select(FloatBits).SequenceEqual(b.xyz.Select(FloatBits)),"Paused pose changed or reused a timestamp.");}
                Require(item.pausedPoses>0,"No actual paused pose rows.");item.pausedPoseVerified=true;
            }
            item.closed=item.optionFilesVerified=item.clocksJoined=item.readerVerified=true;
        }
        static List<Pose> ReadPoses(string path)
        {
            var result=new List<Pose>();using(var stream=File.OpenRead(path))using(var reader=new BinaryReader(stream,Utf8))
            {
                Require(Encoding.ASCII.GetString(reader.ReadBytes(8))=="SFLPOSE1"&&reader.ReadUInt32()==1&&reader.ReadUInt32()==16,"Skeleton binary header invalid.");
                while(stream.Position<stream.Length)
                {
                    var p=new Pose{offset=stream.Position};p.length=checked((int)reader.ReadUInt32());p.sid=reader.ReadInt64();p.sample=reader.ReadInt32();p.binding=reader.ReadInt32();p.joints=reader.ReadInt32();int maskLength=reader.ReadInt32();p.time=reader.ReadSingle();
                    Require(p.joints==(int)HumanBodyBones.LastBone&&maskLength==(p.joints+7)/8&&p.length==32+maskLength+12*p.joints,"Skeleton binary record framing invalid.");
                    var mask=reader.ReadBytes(maskLength);Require(mask.Length==maskLength,"Truncated skeleton validity.");p.valid=new bool[p.joints];p.xyz=new float[p.joints*3];
                    for(int j=0;j<p.joints;++j){p.valid[j]=(mask[j/8]&(1<<(j%8)))!=0;for(int a=0;a<3;++a){float f=reader.ReadSingle();Require(Finite(f)&&(p.valid[j]||FloatBits(f)==0),"Invalid/missing skeleton coordinate not canonical.");p.xyz[j*3+a]=f;}}
                    for(int bit=p.joints;bit<maskLength*8;++bit)Require((mask[bit/8]&(1<<(bit%8)))==0,"Validity has bits outside rig.");
                    Require(stream.Position==p.offset+p.length,"Skeleton record length mismatch.");result.Add(p);
                }
            }return result;
        }
        static void VerifyExpanded(string json,Pose pose,string agent,Binding binding,string runId)
        {
            var row=J.Parse(json);var stamp=JsonUtility.FromJson<StationDecisionCaptureStamp>(row["captureStamp"]);
            Require(stamp.runId==runId&&stamp.syncSampleId==pose.sid&&row.Text("agentId")==agent&&row.Long("bindingId")==pose.binding&&row.Long("sampleId")==pose.sample&&row.Text("rigId")==binding.rigId,"Expanded skeleton identity differs.");
            var joints=J.Array(row["jointPositionsWorld"]);var valid=J.Array(row["valid"]);Require(joints.Count==pose.joints&&valid.Count==pose.joints,"Expanded skeleton width differs.");
            for(int j=0;j<pose.joints;++j){Require((valid[j]=="true")==pose.valid[j],"Expanded validity differs.");var xyz=J.Array(joints[j]);Require(xyz.Count==3,"Expanded joint is notXYZ.");for(int a=0;a<3;++a)Require(float.Parse(xyz[a],CultureInfo.InvariantCulture)==pose.xyz[j*3+a],"Reader changed skeleton coordinate.");}
        }
        static int FloatBits(float value)=>BitConverter.ToInt32(BitConverter.GetBytes(value),0);
        static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
        static void ModeChanged(PlayModeStateChange state)
        {
            Load();if(run==null)return;
            if(state==PlayModeStateChange.EnteredPlayMode){run.phase="initialize";run.initializeFrame=Time.frameCount+8;Save();}
            else if(state==PlayModeStateChange.ExitingPlayMode){if(run.phase!="exiting")Error("Play stopped before skeleton fixture completed.");run.phase="exiting";Save();}
            else if(state==PlayModeStateChange.EnteredEditMode){run.phase="restore";world=null;referenceCamera=null;Save();}
        }
        static void Restore()
        {
            EditorSettings.enterPlayModeOptionsEnabled=run.oldOptionsEnabled;EditorSettings.enterPlayModeOptions=(EnterPlayModeOptions)run.oldOptions;
            EditorSceneManager.playModeStartScene=string.IsNullOrEmpty(run.oldPlayScene)?null:AssetDatabase.LoadAssetAtPath<SceneAsset>(run.oldPlayScene);
            EditorApplication.isPaused=run.oldPaused;Application.runInBackground=run.oldBackground;Application.targetFrameRate=run.oldFps;QualitySettings.vSyncCount=run.oldVsync;Time.timeScale=run.oldScale;
            run.settingsRestored=EditorSettings.enterPlayModeOptionsEnabled==run.oldOptionsEnabled&&(int)EditorSettings.enterPlayModeOptions==run.oldOptions&&EditorApplication.isPaused==run.oldPaused&&Application.targetFrameRate==run.oldFps&&QualitySettings.vSyncCount==run.oldVsync&&Time.timeScale==run.oldScale&&Application.runInBackground==run.oldBackground&&AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene)==run.oldPlayScene;
            try{EditorSceneManager.RestoreSceneManagerSetup(run.scenes.Select(s=>new SceneSetup{path=s.path,isLoaded=s.loaded,isActive=s.active}).ToArray());var saved=EditorSceneManager.GetSceneManagerSetup();run.sceneSetupRestored=saved.Length==run.scenes.Count&&saved.Select((s,i)=>s.path==run.scenes[i].path&&s.isLoaded==run.scenes[i].loaded&&s.isActive==run.scenes[i].active).All(x=>x);}catch(Exception e){Error("Scene restore: "+e);}
            run.filesPreserved=true;foreach(var f in run.files){f.after=File.Exists(f.path)?Digest(f.path):"missing";if(f.before!=f.after){run.filesPreserved=false;Error("Saved asset changed: "+f.path);}}
            if(!run.initialPaused||run.captures.Count!=4||run.captures.Any(c=>!c.closed||!c.readerVerified||!c.x1ResetAndLocked))Error("Not all skeleton option cases completed.");
            if(!run.settingsRestored||!run.sceneSetupRestored)Error("Editor settings or scenes not restored.");
            run.phase="complete";run.completedUtc=DateTime.UtcNow.ToString("O");run.success=run.errors.Count==0;WriteReport();SessionState.EraseString(Key);run=null;world=null;referenceCamera=null;
        }
        static List<FileEvidence> SnapshotFiles()
        {
            var paths=new HashSet<string>{ScenePath,"Assets/TrainStation/Scenes/TrainStation.unity","Assets/TrainStation/Scenes/TrainStationDecisionLab.unity","Assets/TrainStation/DecisionLab/Resources/StationDecisionDaySchedule.asset","Assets/TrainStation/DecisionLab/Resources/StationDecisionDayScenario.asset","ProjectSettings/NavMeshAreas.asset","ProjectSettings/QualitySettings.asset","ProjectSettings/GraphicsSettings.asset","Assets/Settings/PC_RPAsset.asset"};
            foreach(string guid in AssetDatabase.FindAssets("t:NavMeshData",new[]{"Assets/TrainStation"}))paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            return paths.Where(File.Exists).OrderBy(x=>x,StringComparer.Ordinal).Select(p=>new FileEvidence{path=p,before=Digest(p)}).ToList();
        }
        static string Digest(string path){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant();}
        static T[] Components<T>()where T:Component=>SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<T>(true)).ToArray();
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        static void Error(string message){if(run!=null&&!run.errors.Contains(message)&&run.errors.Count<100)run.errors.Add(message);}
        static void OnLog(string message,string stack,LogType type){if(run!=null&&(type==LogType.Error||type==LogType.Exception||type==LogType.Assert))Error(message);}
        static void Save(){if(run!=null)SessionState.SetString(Key,JsonUtility.ToJson(run));}
        static void Load(){if(run==null){string saved=SessionState.GetString(Key,"");if(!string.IsNullOrEmpty(saved))run=JsonUtility.FromJson<Run>(saved);}}
        static void WriteReport(){if(run==null)return;Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));string json=JsonUtility.ToJson(run,true);File.WriteAllText(ReportPath,json,Utf8);File.WriteAllText(run.archivePath,json,Utf8);}
    }
}
#endif
