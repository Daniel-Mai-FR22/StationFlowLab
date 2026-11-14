#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using StationFlow.TrainStation.Social;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.RailOperations.Editor
{
    /// <summary>Checks authored rail geometry and exercises the real shared-clock simulation without creating actors.</summary>
    [InitializeOnLoad]
    public static class StationRailVerification
    {
        const string ScenePath="Assets/TrainStation/Scenes/TrainStation.unity";
        const string StateKey="StationFlow.StationRail.Verification";
        const string LogKey=StateKey+".Errors";
        const string ReportPath="Reports/TrainStationRail/play-mode-check.json";
        [Serializable] sealed class SavedScene { public string path; public bool isLoaded,isActive; }
        [Serializable] sealed class GeometryResult
        {
            public string train,serviceId;
            public int track,wheels,sweptParts,broadPhaseCandidates;
            public float width,length,maximumWheelRailError,minimumPlatformClearance,bufferClearance;
        }
        [Serializable] sealed class SavedReport
        {
            public string scene,unityVersion;
            public bool success;
            public int trains,services,displays,platformDisplays,seatedPassengers,standingPassengers,conversationGroups,actors;
            public List<GeometryResult> geometry=new List<GeometryResult>();
            public List<string> errors=new List<string>();
        }
        [Serializable] sealed class TrainResult
        {
            public string name,serviceId,previousState;
            public List<string> states=new List<string>();
            public int arrivals,departures,stateEvents,arrivalSamples,departureSamples,berthSamples,doorSamples;
            public long arrivalCount,departureCount;
            public float maximumLateralError,maximumBerthError,maximumDoorError,maximumStepSpeed,maximumDoorTravel,minimumTerminalSpeed=999f;
            public float previousProgress,previousDoorFraction;
            public bool previousVisible,hadSample;
            public Vector3 previousPosition,pausePosition;
        }
        [Serializable] sealed class Run
        {
            public string startedUtc,completedUtc,unityVersion,phase,originalPlayScene;
            public bool success,abort,capturedSettings,originalOptionsEnabled,originalPaused,originalRunInBackground;
            public bool restoredSettings,restoredScenes,pausePassed,resumePassed,resetPassed,repeatedCyclePassed,capturedImages;
            public int originalOptions,originalTargetFrameRate,originalVSyncCount,checkAfterFrame,baselineObjects,baselineActors,baselineCameras,baselineLights,framesObserved,displayChecks;
            public float originalTimeScale;
            public double pauseClock,previousClock,endAt,pauseWallTime,startedWallTime,simulationSeconds,clockRate;
            public long deadlineTicks;
            public SavedScene[] scenes;
            public List<TrainResult> trains=new List<TrainResult>();
            public List<string> arrivalEvents=new List<string>(),departureEvents=new List<string>(),errors=new List<string>();
        }
        static StationRailOperations operations;
        static StationTimetableDisplay[] displays;
        static double nextPoll;

        static StationRailVerification()
        {
            EditorApplication.update+=Tick;
            EditorApplication.playModeStateChanged+=ModeChanged;
            Application.logMessageReceived+=Log;
        }

        [MenuItem("Efrei Research Lab/Train Station/Rail/Validate saved scene")]
        public static void ValidateSavedScene()
        {
            var report=InspectSavedScene(SceneManager.GetActiveScene());
            Write("Reports/TrainStationRail/saved-scene-check.json",report);
            Require(report.success,string.Join("\n",report.errors));
        }

        static SavedReport InspectSavedScene(Scene scene)
        {
            var report=new SavedReport{scene=scene.path,unityVersion=Application.unityVersion};
            try
            {
                Require(scene.path==ScenePath,"Open the saved TrainStation scene before checking rail operations.");
                Require(Components<Transform>(scene).All(t=>GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject)==0),"Scene contains missing scripts.");
                var ops=Components<StationRailOperations>(scene).Single();
                Require(ops.schedule&&AssetDatabase.Contains(ops.schedule),"Rail schedule must be a saved reusable asset.");
                Require(ops.schedule.Validate().Count==0,string.Join("\n",ops.schedule.Validate()));
                Require(ops.trains!=null&&ops.trains.Length==4&&ops.trains.All(t=>t),"Expected four authored trainsets.");
                Require(ops.schedule.services.Length==4&&ops.trains.Distinct().Count()==4&&Components<StationTrain>(scene).Length==4,"Train and service registrations must be complete and unique.");
                Require(ops.trainArrived!=null&&ops.trainDeparted!=null&&ops.trainStateChanged!=null,"Saved rail event hooks are missing.");
                report.trains=ops.trains.Length;report.services=ops.schedule.services.Length;
                Physics.SyncTransforms();
                foreach(var train in ops.trains)
                {
                    Require(train.ValidateSetup().Count==0,string.Join("\n",train.ValidateSetup()));
                    Require(PrefabUtility.GetCorrespondingObjectFromOriginalSource(train.gameObject),"Trainset is not a reusable prefab: "+train.name);
                    var service=ops.schedule.services.Single(s=>s.serviceId==train.serviceId);
                    Require(service.trackNumber==train.trackNumber,"Train's track does not match its timetable service.");
                    Require(train.visualRoot.gameObject.activeSelf,"All saved trains must remain inspectable in Edit mode.");
                    Require(train.doorPanels.Length>0&&train.doorPanels.Distinct().Count()==train.doorPanels.Length,"Train needs distinct authored platform door panels.");
                    Require(train.openDoorLocalOffsets.All(v=>v.magnitude>.2f),"Train door panels have no meaningful opening travel.");
                    report.geometry.Add(InspectGeometry(train,ops,scene));
                }
                var boards=Components<StationTimetableDisplay>(scene);
                Require(boards.Length==23&&boards.All(d=>d.operations==ops),"All twenty-three timetable displays must share the same rail operations clock.");
                Require(boards.Count(d=>d.mode==StationTimetableMode.Departures)==1&&boards.Count(d=>d.mode==StationTimetableMode.Arrivals)==1&&
                    boards.Count(d=>d.mode==StationTimetableMode.Traffic)==1&&boards.Count(d=>d.mode==StationTimetableMode.Platform)==20,"Saved hall, platform and HUD timetable displays are incomplete.");
                foreach(var board in boards)
                {
                    Require(board.rows!=null&&board.rows.Length==(board.mode==StationTimetableMode.Platform?2:board.mode==StationTimetableMode.Traffic?4:5),"Unexpected saved timetable row count.");
                    Require(board.rows.All(r=>r!=null&&r.time&&r.location&&r.train&&r.track&&r.status),"Timetable row text references are incomplete.");
                    Require(board.clockLabel,"Every timetable needs a shared-clock label.");
                    if(board.mode==StationTimetableMode.Platform)Require(board.trackFilter!=null&&board.trackFilter.Length==2,"Platform display must filter its two adjacent tracks.");
                }
                Require(boards.Count(d=>d.hourHand&&d.minuteHand)==1,"The station analog clock must be wired to the shared simulation.");
                report.displays=boards.Length;report.platformDisplays=boards.Count(d=>d.mode==StationTimetableMode.Platform);
                CheckCrowd(scene);
                var social=Components<StationSocialWorld>(scene).Single();
                report.seatedPassengers=social.passengers.Count(p=>p.activity==StationPassengerActivity.Seated);
                report.standingPassengers=social.StandingPassengerCount;report.conversationGroups=social.ConversationGroupCount;
                report.actors=Components<StationActorMotion>(scene).Length;
                report.success=true;
            }
            catch(Exception error){report.errors.Add(error.Message);}
            return report;
        }

        static GeometryResult InspectGeometry(StationTrain train,StationRailOperations ops,Scene scene)
        {
            var renderers=train.visualRoot.GetComponentsInChildren<Renderer>(true).Where(r=>r is MeshRenderer&&!r.GetComponent<TMPro.TMP_Text>()&&r.bounds.size.sqrMagnitude>.001f).ToArray();
            Require(renderers.Length>0,"Train has no saved visible geometry.");
            var bounds=renderers[0].bounds;foreach(var renderer in renderers.Skip(1))bounds.Encapsulate(renderer.bounds);
            Require(Finite(bounds.min)&&Finite(bounds.max),"Train geometry has non-finite bounds.");
            var result=new GeometryResult{train=train.name,serviceId=train.serviceId,track=train.trackNumber,width=bounds.size.x,length=bounds.size.z,minimumPlatformClearance=999f};
            Require(result.width>=2f&&result.width<=3.05f&&result.length>=70f&&result.length<=90f,"Trainset dimensions do not fit the authored four-car rail envelope: "+train.name);
            var wheels=renderers.Where(r=>r.name.StartsWith("Wheel tread ",StringComparison.Ordinal)).ToArray();
            Require(wheels.Length==32,"Trainset needs thirty-two inspectable wheel treads.");
            result.wheels=wheels.Length;
            foreach(var wheel in wheels)result.maximumWheelRailError=Mathf.Max(result.maximumWheelRailError,Mathf.Abs(wheel.bounds.min.y-train.berthPoint.position.y));
            Require(result.maximumWheelRailError<.055f,"Train wheels do not sit on the authored rail head: "+train.name);
            foreach(var platform in Components<Collider>(scene).Where(c=>c.name=="Platform structural slab"))
            {
                float gap=Mathf.Max(platform.bounds.min.x-bounds.max.x,bounds.min.x-platform.bounds.max.x);
                result.minimumPlatformClearance=Mathf.Min(result.minimumPlatformClearance,gap);
            }
            Require(result.minimumPlatformClearance>=.08f&&result.minimumPlatformClearance<1f,"Train/platform lateral clearance is invalid: "+train.name);
            float noseAtBerth=train.berthPoint.position.z+bounds.min.z-train.transform.position.z;
            result.bufferClearance=noseAtBerth-50.8f;
            Require(result.bufferClearance>1f,"Berthed train intersects the terminus buffer zone.");
            var minimum=bounds.min;var maximum=bounds.max;
            minimum.y=Mathf.Max(minimum.y,train.berthPoint.position.y+.06f);
            minimum.z=noseAtBerth;
            maximum.z=Mathf.Max(train.approachPoint.position.z,train.exitPoint.position.z)+bounds.max.z-train.transform.position.z;
            var candidates=Physics.OverlapBox((minimum+maximum)*.5f,(maximum-minimum)*.5f,Quaternion.identity,~0,QueryTriggerInteraction.Ignore)
                .Where(c=>c.enabled&&c.gameObject.activeInHierarchy&&c.gameObject.scene==scene&&!ops.trains.Any(t=>c.transform.IsChildOf(t.transform))).ToArray();
            result.broadPhaseCandidates=candidates.Length;
            // A full-height train envelope would incorrectly fill the empty space beside the narrow pantograph.
            // Sweep every actual renderer independently, retaining conservative bounds for that physical part.
            foreach(var renderer in renderers)
            {
                var part=renderer.bounds;var partMinimum=part.min;var partMaximum=part.max;
                Require(Finite(partMinimum)&&Finite(partMaximum),"A train part has non-finite bounds: "+renderer.name);
                partMinimum.y=Mathf.Max(partMinimum.y,train.berthPoint.position.y+.06f);
                if(partMaximum.y<=partMinimum.y)continue; // The separate wheel check covers intentional contact with the rails.
                partMinimum.z=train.berthPoint.position.z+part.min.z-train.transform.position.z;
                partMaximum.z=Mathf.Max(train.approachPoint.position.z,train.exitPoint.position.z)+part.max.z-train.transform.position.z;
                var sweep=new Bounds((partMinimum+partMaximum)*.5f,partMaximum-partMinimum);result.sweptParts++;
                if(!candidates.Any(c=>c.bounds.Intersects(sweep)))continue;
                var blockers=Physics.OverlapBox(sweep.center,sweep.extents,Quaternion.identity,~0,QueryTriggerInteraction.Ignore)
                    .Where(c=>candidates.Contains(c)).ToArray();
                Require(blockers.Length==0,"Train part '"+renderer.name+"' intersects saved geometry along its route: "+string.Join(", ",blockers.Take(6).Select(c=>c.name)));
            }
            return result;
        }

        static void CheckCrowd(Scene scene)
        {
            var world=Components<StationSocialWorld>(scene).Single();
            Require(world.passengers.All(p=>p)&&world.passengers.Count(p=>p.activity==StationPassengerActivity.Seated)==18&&world.conversationGroups.Length==4&&world.conversationGroups.All(g=>g&&g.members.Length==3),"Existing seated passengers and standing groups were not preserved.");
            int legacyMembers=Components<StationConversationMember>(scene).Count(m=>!m.GetComponent<StationPassenger>());
            int staff=world.shopDesks.Count(d=>d&&d.receptionist)+1;
            Require(Components<StationActorMotion>(scene).Length==world.passengers.Length+legacyMembers+staff+world.cleaners.Length&&Components<StationConversationMember>(scene).Length==12&&world.cleaners.Length==3,"The registered station population does not match its authored human actors.");
        }

        [MenuItem("Efrei Research Lab/Train Station/Rail/Check actual Play mode cycle")]
        public static void BeginPlayCheck()
        {
            Require(Load()==null,"A rail Play check is already running.");
            var run=new Run{startedUtc=DateTime.UtcNow.ToString("O"),unityVersion=Application.unityVersion,phase="opening",deadlineTicks=DateTime.UtcNow.AddSeconds(210).Ticks};
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode&&!EditorApplication.isCompiling&&!EditorApplication.isUpdating,"Finish Play/import before the rail check.");
                CleanScenes();var setup=EditorSceneManager.GetSceneManagerSetup();
                Require(setup.Length>0&&setup.All(s=>!string.IsNullOrEmpty(s.path)),"Save open scenes so their Editor setup can be restored.");
                run.scenes=setup.Select(s=>new SavedScene{path=s.path,isLoaded=s.isLoaded,isActive=s.isActive}).ToArray();
                run.originalPlayScene=AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene);
                run.originalOptionsEnabled=EditorSettings.enterPlayModeOptionsEnabled;run.originalOptions=(int)EditorSettings.enterPlayModeOptions;
                run.originalPaused=EditorApplication.isPaused;run.originalTimeScale=Time.timeScale;
                run.originalRunInBackground=Application.runInBackground;run.originalTargetFrameRate=Application.targetFrameRate;run.originalVSyncCount=QualitySettings.vSyncCount;
                run.capturedSettings=true;SessionState.EraseString(LogKey);Save(run);
                EditorSettings.enterPlayModeOptionsEnabled=false;EditorSceneManager.playModeStartScene=null;EditorApplication.isPaused=false;
            }
            catch(Exception error){run.abort=true;run.errors.Add(error.Message);Finish(run);}
        }

        static void Tick()
        {
            if(EditorApplication.timeSinceStartup<nextPoll)return;nextPoll=EditorApplication.timeSinceStartup+.03;
            var run=Load();if(run==null)return;
            if(run.phase=="restoring"&&!EditorApplication.isPlayingOrWillChangePlaymode){Finish(run);return;}
            if(run.abort&&run.phase=="exiting"){if(!EditorApplication.isPlayingOrWillChangePlaymode)Finish(run);return;}
            try
            {
                Require(DateTime.UtcNow.Ticks<run.deadlineTicks,"Rail Play verification timed out in "+run.phase+".");
                if(EditorApplication.isCompiling||EditorApplication.isUpdating)return;
                Require(run.errors.Count==0,string.Join("\n",run.errors));
                Require(string.IsNullOrEmpty(SessionState.GetString(LogKey,"")),"Unity logged an error during the rail check.");
                if(run.phase=="opening"&&!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    CleanScenes();var scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
                    var saved=InspectSavedScene(scene);Write("Reports/TrainStationRail/saved-scene-check.json",saved);
                    Require(saved.success,string.Join("\n",saved.errors));
                    run.baselineObjects=Components<Transform>(scene).Length;run.baselineActors=Components<StationActorMotion>(scene).Length;
                    run.baselineCameras=Components<Camera>(scene).Count(c=>c.isActiveAndEnabled);run.baselineLights=Components<Light>(scene).Count(l=>l.isActiveAndEnabled);
                    run.phase="entering";Save(run);EditorApplication.isPlaying=true;
                }
                else if(run.phase=="initializing"&&EditorApplication.isPlaying&&Time.frameCount>=run.checkAfterFrame)
                {Initialize(run);run.phase="running";Save(run);}
                else if(EditorApplication.isPlaying&&(run.phase=="running"||run.phase=="paused"||run.phase=="resetting"))
                {Observe(run);Save(run);}
            }
            catch(Exception error)
            {
                run.errors.Add(error.ToString());run.abort=true;run.phase="exiting";Save(run);Write(ReportPath,run);
                if(EditorApplication.isPlayingOrWillChangePlaymode)EditorApplication.isPlaying=false;else Finish(run);
            }
        }

        static void Initialize(Run run)
        {
            var scene=SceneManager.GetActiveScene();Require(scene.path==ScenePath,"Unexpected rail startup scene.");
            operations=Components<StationRailOperations>(scene).Single();displays=Components<StationTimetableDisplay>(scene);
            Require(string.IsNullOrEmpty(operations.ConfigurationIssue),operations.ConfigurationIssue);
            operations.ResetSimulation();operations.simulationRate=12f;operations.Resume();
            run.clockRate=operations.simulationRate;run.startedWallTime=EditorApplication.timeSinceStartup;
            run.endAt=operations.schedule.cycleDurationSeconds+operations.schedule.services.Where(s=>s.departureOffsetSeconds>=0f).Min(s=>s.departureOffsetSeconds)+10d;
            run.previousClock=operations.ElapsedSimulationSeconds;
            run.trains=operations.trains.Select(t=>new TrainResult{name=t.name,serviceId=t.serviceId}).ToList();
            operations.trainArrived.AddListener(OnArrived);operations.trainDeparted.AddListener(OnDeparted);operations.trainStateChanged.AddListener(OnStateChanged);
            Application.runInBackground=true;Application.targetFrameRate=60;QualitySettings.vSyncCount=0;
        }

        static void Observe(Run run)
        {
            Require(operations&&displays!=null,"Rail verification lost its live scene references.");
            Require(Mathf.Abs(Time.timeScale-run.originalTimeScale)<.0001f,"Rail clock acceleration changed the global simulation time scale.");
            Require(string.IsNullOrEmpty(operations.ConfigurationIssue),operations.ConfigurationIssue);
            run.framesObserved++;run.simulationSeconds=Math.Max(run.simulationSeconds,operations.ElapsedSimulationSeconds);
            CheckDisplays(run);
            if(run.phase=="paused")
            {
                Require(operations.IsPaused&&Math.Abs(operations.ElapsedSimulationSeconds-run.pauseClock)<.00001,"Paused rail clock continued advancing.");
                for(int i=0;i<operations.trains.Length;i++)
                {
                    Require(Vector3.Distance(operations.trains[i].transform.position,run.trains[i].pausePosition)<.0001f,"A paused train continued moving.");
                    Require(Mathf.Abs(operations.trains[i].DoorOpenFraction-run.trains[i].previousDoorFraction)<.0001f,"Paused train doors continued moving.");
                }
                if(EditorApplication.timeSinceStartup-run.pauseWallTime>.45)
                {run.pausePassed=true;operations.Resume();run.phase="running";run.previousClock=operations.ElapsedSimulationSeconds;}
                return;
            }
            if(run.phase=="resetting")
            {
                Require(operations.IsPaused&&Math.Abs(operations.ElapsedSimulationSeconds)<.00001,"Reset did not restore the initial shared clock.");
                if(EditorApplication.timeSinceStartup-run.pauseWallTime>.3)
                {
                    CheckInitialPoses();run.resetPassed=true;FinalAssertions(run);run.phase="exiting";Save(run);EditorApplication.isPlaying=false;
                }
                return;
            }
            double now=operations.ElapsedSimulationSeconds;double delta=now-run.previousClock;
            Require(delta>=0d&&delta<20d,"Rail clock jumped unexpectedly during continuous Play.");
            for(int i=0;i<operations.trains.Length;i++)ObserveTrain(operations.trains[i],run.trains[i],delta);
            if(run.pausePassed&&now>run.pauseClock+.5d)run.resumePassed=true;
            if(!run.pausePassed&&operations.trains.Any(t=>(t.State==StationTrainState.Arriving||t.State==StationTrainState.Departing)&&t.MotionProgress>.2f&&t.MotionProgress<.8f))
            {
                operations.Pause();run.pauseClock=operations.ElapsedSimulationSeconds;run.pauseWallTime=EditorApplication.timeSinceStartup;
                foreach(var train in operations.trains)run.trains.Single(t=>t.serviceId==train.serviceId).pausePosition=train.transform.position;
                run.phase="paused";
            }
            if(!run.capturedImages&&run.trains.All(t=>t.arrivalSamples>0||t.berthSamples>0)&&operations.trains.Any(t=>t.State==StationTrainState.Boarding))
            {StationRailCapture.CaptureLive();run.capturedImages=true;}
            run.previousClock=now;
            if(now>=run.endAt)
            {
                run.repeatedCyclePassed=now>operations.schedule.cycleDurationSeconds&&operations.trains.Any(t=>t.DepartureCount>=2);
                Require(run.repeatedCyclePassed,"The next timetable cycle did not produce a repeated departure.");
                operations.ResetSimulation();operations.Pause();run.pauseWallTime=EditorApplication.timeSinceStartup;run.phase="resetting";
            }
        }

        static void ObserveTrain(StationTrain train,TrainResult result,double delta)
        {
            string state=train.State.ToString();if(!result.states.Contains(state))result.states.Add(state);
            var position=train.transform.position;Require(Finite(position),"A train has a non-finite position.");
            result.maximumLateralError=Mathf.Max(result.maximumLateralError,Mathf.Abs(position.x-train.berthPoint.position.x),Mathf.Abs(position.y-train.berthPoint.position.y));
            Require(result.maximumLateralError<.001f&&Quaternion.Angle(train.transform.rotation,train.berthPoint.rotation)<.01f,"Train left its authored straight track.");
            bool visible=train.visualRoot.gameObject.activeSelf;
            Require(visible==(train.State!=StationTrainState.Offstage||!train.hideOffstage),"Train visibility disagrees with its offstage state.");
            if(result.hadSample&&result.previousVisible&&visible&&delta>0d)
            {
                float speed=Vector3.Distance(position,result.previousPosition)/(float)delta;
                float limit=2f*Mathf.Max(Vector3.Distance(train.approachPoint.position,train.berthPoint.position)/train.Service.approachDurationSeconds,
                    Vector3.Distance(train.exitPoint.position,train.berthPoint.position)/train.Service.departureDurationSeconds);
                result.maximumStepSpeed=Mathf.Max(result.maximumStepSpeed,speed);
                Require(speed<=limit+.25f,"Visible train snapped or moved discontinuously between timetable phases.");
            }
            if(train.State==StationTrainState.Arriving||train.State==StationTrainState.Departing)
            {
                if(train.State==StationTrainState.Arriving)result.arrivalSamples++;else result.departureSamples++;
                Require(train.DoorOpenFraction<.0001f,"A moving train has open doors.");
                Require(train.MotionProgress>=0f&&train.MotionProgress<=1f,"Train movement progress escaped its route.");
                if(result.hadSample&&result.previousState==state)
                {
                    Require(train.MotionProgress+.0001f>=result.previousProgress,"Train movement ran backwards within one journey.");
                    Require(train.State==StationTrainState.Arriving?position.z<=result.previousPosition.z+.001f:position.z>=result.previousPosition.z-.001f,"Train travelled in the wrong direction.");
                }
                if((train.State==StationTrainState.Arriving&&train.MotionProgress>.97f)||(train.State==StationTrainState.Departing&&train.MotionProgress<.03f))
                    result.minimumTerminalSpeed=Mathf.Min(result.minimumTerminalSpeed,train.CurrentSpeedMetresPerSecond);
            }
            else if(train.State!=StationTrainState.Offstage)
            {
                result.berthSamples++;result.maximumBerthError=Mathf.Max(result.maximumBerthError,Vector3.Distance(position,train.berthPoint.position));
                Require(result.maximumBerthError<.001f&&train.CurrentSpeedMetresPerSecond<.001f,"Stopped train did not settle exactly at its saved berth.");
                if(train.State==StationTrainState.Boarding)Require(train.DoorOpenFraction>.999f,"Boarding train doors are not fully open.");
            }
            Require(train.DoorOpenFraction>=0f&&train.DoorOpenFraction<=1f,"Door opening fraction is invalid.");
            for(int door=0;door<train.doorPanels.Length;door++)
            {
                var actual=train.doorPanels[door].localPosition;var expected=train.closedDoorLocalPositions[door]+train.openDoorLocalOffsets[door]*train.DoorOpenFraction;
                result.maximumDoorError=Mathf.Max(result.maximumDoorError,Vector3.Distance(actual,expected));
                result.maximumDoorTravel=Mathf.Max(result.maximumDoorTravel,Vector3.Distance(actual,train.closedDoorLocalPositions[door]));
            }
            result.doorSamples++;Require(result.maximumDoorError<.001f,"Door panel geometry does not follow the shared train state.");
            result.arrivalCount=train.ArrivalCount;result.departureCount=train.DepartureCount;
            result.previousPosition=position;result.previousProgress=train.MotionProgress;result.previousDoorFraction=train.DoorOpenFraction;
            result.previousState=state;result.previousVisible=visible;result.hadSample=true;
        }

        static void CheckDisplays(Run run)
        {
            string clock=ClockText(operations.ClockSeconds,true);
            foreach(var display in displays)
            {
                Require(display.clockLabel.text==clock,"A station display clock is out of sync: "+display.name);
                if(display.pauseLabel)Require(display.pauseLabel.text==(operations.IsPaused?"REPRENDRE":"PAUSE"),"Pause control label is out of sync.");
                for(int i=0;i<display.rows.Length;i++)
                {
                    var expected=display.GetRowValues(i);var row=display.rows[i];
                    Require(row.time.text==(expected.time??"")&&row.location.text==(expected.location??"")&&row.train.text==(expected.train??"")&&
                        row.track.text==(expected.track??"")&&row.status.text==(expected.status??""),"Rendered timetable row is stale: "+display.name+" / "+i);
                    if(string.IsNullOrEmpty(expected.serviceId))continue;
                    var train=operations.trains.Single(t=>t.serviceId==expected.serviceId);var service=train.Service;
                    Require(expected.state==train.State&&expected.train==service.trainNumber&&expected.track==service.trackNumber.ToString(),"Timetable train identity/state disagrees with the physical train.");
                    string time=display.mode==StationTimetableMode.Traffic?ClockText(train.ScheduledArrivalSeconds,false)+" / "+ClockText(train.ScheduledDepartureSeconds,false):
                        ClockText(display.mode==StationTimetableMode.Arrivals?train.ScheduledArrivalSeconds:train.ScheduledDepartureSeconds,false);
                    Require(row.time.text==time&&expected.location==(display.mode==StationTimetableMode.Arrivals?service.origin:service.destination),"Timetable route/time disagrees with its scheduled physical occurrence.");
                    if(display.trackFilter!=null&&display.trackFilter.Length>0)Require(display.trackFilter.Contains(train.trackNumber),"Platform sign advertises an unrelated track.");
                }
                if(display.hourHand)Require(Mathf.Abs(Mathf.DeltaAngle(display.hourHand.localEulerAngles.z,(float)(-operations.ClockSeconds/120d%360d)))<.05f,"Analog hour hand is out of sync.");
                if(display.minuteHand)Require(Mathf.Abs(Mathf.DeltaAngle(display.minuteHand.localEulerAngles.z,(float)(-operations.ClockSeconds/10d%360d)))<.05f,"Analog minute hand is out of sync.");
            }
            run.displayChecks++;
        }

        static void CheckInitialPoses()
        {
            foreach(var train in operations.trains)
            {
                var service=train.Service;bool initiallyBerthed=service.arrivalOffsetSeconds<=0f&&service.departureOffsetSeconds>0f;
                Require(Vector3.Distance(train.transform.position,initiallyBerthed?train.berthPoint.position:train.approachPoint.position)<.001f,"Reset did not restore the saved initial train location.");
                Require(train.ArrivalCount==0&&train.DepartureCount==0,"Reset did not clear journey counters.");
            }
        }

        static void FinalAssertions(Run run)
        {
            Require(run.pausePassed&&run.resumePassed&&run.resetPassed&&run.repeatedCyclePassed&&run.capturedImages,"Rail pause/resume/reset/repeat integration did not finish.");
            foreach(var train in run.trains)
            {
                Require(Enum.GetNames(typeof(StationTrainState)).All(s=>train.states.Contains(s)),"Not all movement, dwell and door phases were observed for "+train.name);
                Require(train.arrivals>=1&&train.departures>=1&&train.arrivalCount>=1&&train.departureCount>=1,"Train arrival/departure events and counters are incomplete.");
                Require(train.arrivals==train.arrivalCount&&train.departures==train.departureCount,"Train events disagree with physical journey counters.");
                Require(train.arrivalSamples>5&&train.departureSamples>5&&train.berthSamples>5&&train.minimumTerminalSpeed<.35f&&train.maximumDoorTravel>.2f,"Insufficient smooth arrival/departure, stopped berth or real door movement observations.");
            }
            var scene=SceneManager.GetActiveScene();CheckCrowd(scene);
            Require(Components<Transform>(scene).Length==run.baselineObjects&&Components<StationActorMotion>(scene).Length==run.baselineActors,"Rail runtime instantiated or removed scene actors/objects.");
            Require(Components<Camera>(scene).Count(c=>c.isActiveAndEnabled)==run.baselineCameras&&Components<Light>(scene).Count(l=>l.isActiveAndEnabled)==run.baselineLights,"Rail runtime changed saved cameras or lighting.");
        }

        static void OnArrived(StationTrain train)=>RecordEvent(train,0);
        static void OnDeparted(StationTrain train)=>RecordEvent(train,1);
        static void OnStateChanged(StationTrain train)=>RecordEvent(train,2);
        static void RecordEvent(StationTrain train,int kind)
        {
            var run=Load();if(run==null||run.phase!="running")return;
            var result=train?run.trains.FirstOrDefault(t=>t.serviceId==train.serviceId):null;
            if(result==null)run.errors.Add("Unexpected rail event.");
            else if(kind==0){result.arrivals++;run.arrivalEvents.Add(train.serviceId);}
            else if(kind==1){result.departures++;run.departureEvents.Add(train.serviceId);}
            else result.stateEvents++;
            Save(run);
        }
        static void ModeChanged(PlayModeStateChange state)
        {
            var run=Load();if(run==null)return;
            if(state==PlayModeStateChange.EnteredPlayMode){run.phase="initializing";run.checkAfterFrame=Time.frameCount+8;}
            else if(state==PlayModeStateChange.ExitingPlayMode&&run.phase!="exiting"){run.abort=true;run.errors.Add("Play was stopped before the rail check completed.");run.phase="exiting";}
            else if(state==PlayModeStateChange.EnteredEditMode)run.phase="restoring";
            Save(run);
        }
        static void Log(string message,string trace,LogType type)
        {
            if(Load()==null||(type!=LogType.Error&&type!=LogType.Exception&&type!=LogType.Assert))return;
            var prior=SessionState.GetString(LogKey,"");if(prior.Length<16000)SessionState.SetString(LogKey,prior+message+"\n"+trace+"\n");
        }
        static void Finish(Run run)
        {
            if(run.capturedSettings)
            {
                try
                {
                    Time.timeScale=run.originalTimeScale;Application.runInBackground=run.originalRunInBackground;
                    Application.targetFrameRate=run.originalTargetFrameRate;QualitySettings.vSyncCount=run.originalVSyncCount;
                    EditorSceneManager.playModeStartScene=string.IsNullOrEmpty(run.originalPlayScene)?null:AssetDatabase.LoadAssetAtPath<SceneAsset>(run.originalPlayScene);
                    EditorSettings.enterPlayModeOptionsEnabled=run.originalOptionsEnabled;EditorSettings.enterPlayModeOptions=(EnterPlayModeOptions)run.originalOptions;
                    EditorApplication.isPaused=run.originalPaused;run.restoredSettings=true;CleanScenes();
                    if(run.scenes!=null)EditorSceneManager.RestoreSceneManagerSetup(run.scenes.Select(s=>new SceneSetup{path=s.path,isLoaded=s.isLoaded,isActive=s.isActive}).ToArray());
                    run.restoredScenes=true;
                }
                catch(Exception error){run.errors.Add("Restoration failed: "+error);}
            }
            var log=SessionState.GetString(LogKey,"");if(!string.IsNullOrEmpty(log))run.errors.Add("Unity errors:\n"+log);
            run.success=!run.abort&&run.errors.Count==0&&run.restoredSettings&&run.restoredScenes&&run.pausePassed&&run.resumePassed&&run.resetPassed&&run.repeatedCyclePassed;
            run.phase="complete";run.completedUtc=DateTime.UtcNow.ToString("O");SessionState.EraseString(StateKey);SessionState.EraseString(LogKey);
            Write(ReportPath,run);operations=null;displays=null;
            Debug.Log("Station rail Play verification "+(run.success?"PASSED":"FAILED")+". See "+ReportPath);
        }
        static string ClockText(double seconds,bool full)=>TimeSpan.FromSeconds(Math.Floor(Math.Max(0d,seconds))).ToString(full?@"hh\:mm\:ss":@"hh\:mm",CultureInfo.InvariantCulture);
        static Run Load(){var json=SessionState.GetString(StateKey,"");return string.IsNullOrEmpty(json)?null:JsonUtility.FromJson<Run>(json);}
        static void Save(Run run)=>SessionState.SetString(StateKey,JsonUtility.ToJson(run));
        static void CleanScenes(){for(int i=0;i<SceneManager.sceneCount;i++)Require(!SceneManager.GetSceneAt(i).isDirty,"Save or revert open scene edits first; verification never discards them.");}
        static T[] Components<T>(Scene scene)where T:Component=>scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<T>(true)).ToArray();
        static bool Finite(Vector3 v)=>!float.IsNaN(v.x)&&!float.IsInfinity(v.x)&&!float.IsNaN(v.y)&&!float.IsInfinity(v.y)&&!float.IsNaN(v.z)&&!float.IsInfinity(v.z);
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static void Write(string path,object report){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,JsonUtility.ToJson(report,true));}
    }
}
#endif
