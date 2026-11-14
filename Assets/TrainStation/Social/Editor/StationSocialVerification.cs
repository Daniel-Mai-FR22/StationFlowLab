#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.HumanAnimation;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Exercises the saved scene's real navigation and social state machines in Play mode.</summary>
    [InitializeOnLoad]
    public static class StationSocialVerification
    {
        const string ScenePath="Assets/TrainStation/Scenes/TrainStation.unity";
        const string StateKey="StationFlow.StationSocial.Verification";
        const string LogKey=StateKey+".Errors";
        const string ReportPath="Reports/TrainStationSocial/play-mode-check.json";
        [Serializable] sealed class SavedScene { public string path; public bool isLoaded,isActive; }
        [Serializable] sealed class ActorResult
        {
            public string name,originalSeat,finalSeat;
            public List<string> states=new List<string>();
            public int conversations;
            public float maximumLuggageTravel,maximumLuggageDistance;
            public bool walkingObserved,talkingObserved,seatedAgain;
            public float initialPathLength,walkingSpeed,requestTime=-1f,admissionTime=-1f,conversationStartTime=-1f,conversationEndTime=-1f;
            public float approachTravel,maximumApproachRemainingPath,admissionSlotDistance,queueIndexChangedAt;
            public int admissionSlotIndex=-1,previousQueueIndex=-1,queueAdvances,settledQueueSamples;
            public Vector3 previousApproachPosition,admissionPosition;
            public bool approachPositionRecorded;
            public float movingSince=-1f,stationarySince=-1f;
        }
        [Serializable] sealed class LocomotionWindow
        {
            public string name;
            public float requestedSpeed,duration,distance,animatedDistance,relativePhaseError,meanWorldSpeed,meanPlaybackSpeed,playbackIntegral;
            public int samples;
        }
        [Serializable] sealed class LocomotionResult
        {
            public string actor,phase="waiting for departure";
            public bool started,passed,accelerationObserved,decelerationObserved,stoppedIdlePassed,resumedPassed;
            public int stage;
            public float originalSpeed,referenceSpeed,clipDuration,stageStarted,previousTime,previousNormalizedTime,previousVelocity,pauseSettledDistance;
            public Vector3 previousPosition,pauseDestination;
            public bool sampleReady;
            public bool poseSampleReady;
            public int hipSamples;
            public float previousPosePhase,sampledWalkCycles,maximumHipPlanarJump;
            public Vector3 minimumLocalHip,maximumLocalHip,previousLocalHip;
            public List<LocomotionWindow> windows=new List<LocomotionWindow>();
        }
        [Serializable] sealed class CleanerResult
        {
            public string name;
            public int completedWorkCycles,settledGripSamples,geometrySamples;
            public bool patrolObserved,workObserved,toolObserved;
            public float maximumHandAngle,maximumToolTravel,maximumRightGripError,maximumLeftGripError,maximumBrushGroundError;
            public float minimumHipHeight=999f,minimumFootHeight=999f,maximumFootHeight,minimumSkinHeight=999f,maximumSkinHeight;
            public GripPose failedGripPose;
        }
        [Serializable] sealed class GripPose
        {
            public float simulationTime,humanScale;
            public Vector3 actorPosition,hipsPosition,leftShoulderPosition,rightShoulderPosition,leftHandPosition,rightHandPosition,leftGripTarget,rightGripTarget,modelLossyScale;
        }
        [Serializable] sealed class ConversationMemberResult
        {
            public string name;
            public Vector3 initialPosition;
            public bool speakerObserved,listenerObserved;
            public int settledFacingSamples;
            public float maximumTravel,maximumSettledFacingError;
        }
        [Serializable] sealed class ConversationGroupResult
        {
            public string name,initialSpeaker,previousSpeaker;
            public int initialCompletedTurns,completedTurns;
            public float speakerChangedAt;
            public List<ConversationMemberResult> members=new List<ConversationMemberResult>();
            public List<string> speakerStarts=new List<string>(),speakerEnds=new List<string>();
        }
        [Serializable] sealed class Run
        {
            public string startedUtc,completedUtc,unityVersion,phase,originalPlayScene,blockerName,conflictSeat;
            public bool success,abort,capturedSettings,originalOptionsEnabled,originalPaused,originalRunInBackground;
            public bool restoredSettings,restoredScenes,frontGatePassed,fifoPassed,seatConflictRequested,seatConflictOccupied,alternativeSeatPassed,atomicSeatPassed;
            public bool receptionImageCaptured,cleaningImageCaptured,groupImageCaptured,groupInteractionsPassed;
            public bool requestDidNotReservePassed,laterRequestArrivedFirstPassed,compactQueuePassed;
            public int originalOptions,originalTargetFrameRate,originalVSyncCount,checkAfterFrame,baselineObjects,baselineCameras,baselineLights,framesObserved,queuePeak;
            public float originalTimeScale,simulationStarted,simulatedSeconds,minimumMovingActorDistance=999f;
            public float minimumMovingToGroupDistance=999f;
            public int movingNearGroupSamples;
            public long deadlineTicks;
            public SavedScene[] scenes;
            public List<ActorResult> actors=new List<ActorResult>();
            public List<CleanerResult> cleaners=new List<CleanerResult>();
            public List<ConversationGroupResult> conversationGroups=new List<ConversationGroupResult>();
            public LocomotionResult locomotion=new LocomotionResult();
            public List<string> requested=new List<string>(),joined=new List<string>(),approached=new List<string>(),started=new List<string>(),ended=new List<string>(),errors=new List<string>();
        }
        [Serializable] sealed class SavedReport
        {
            public string unityVersion,scene;
            public bool success;
            public int passengers,seatedPassengers,standingPassengers,conversationGroups,cleaners,receptionists,seats,spareSeats,navMeshSurfaces,completeRoutes,prefabActors;
            public List<string> errors=new List<string>();
        }
        static StationSocialWorld world;
        static StationPassenger[] participants;
        static StationPassenger blocker;
        static StationSeat conflictSeat;
        static readonly Dictionary<StationCleaner,Quaternion> workingHands=new Dictionary<StationCleaner,Quaternion>();
        static readonly Dictionary<StationCleaner,Vector3> workingTools=new Dictionary<StationCleaner,Vector3>();
        static readonly Dictionary<StationCleaner,float> workingStarted=new Dictionary<StationCleaner,float>();
        static readonly HashSet<StationCleaner> checkedWorkingSkin=new HashSet<StationCleaner>();
        static double nextPoll;

        static StationSocialVerification()
        {
            EditorApplication.update+=Tick;
            EditorApplication.playModeStateChanged+=ModeChanged;
            Application.logMessageReceived+=Log;
        }

        [MenuItem("Efrei Research Lab/Train Station Social/Validate saved scene")]
        public static void ValidateSavedScene()
        {
            if (HasMixedCrowd()) { StationMixedCrowdAuthoring.Validate(); return; }
            var report=InspectSavedScene(SceneManager.GetActiveScene());
            Write("Reports/TrainStationSocial/saved-scene-check.json",report);
            if(!report.success)throw new InvalidOperationException(string.Join("\n",report.errors));
        }

        static SavedReport InspectSavedScene(Scene scene)
        {
            var report=new SavedReport{scene=scene.path,unityVersion=Application.unityVersion};
            try
            {
                Require(scene.path==ScenePath,"Open the saved TrainStation scene before checking.");
                var all=Components<Transform>(scene);
                Require(all.All(t=>GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject)==0),"Scene contains a missing script.");
                var social=Components<StationSocialWorld>(scene).Single();
                Require(social.passengers!=null&&social.passengers.Length==18&&social.passengers.All(p=>p),"Expected eighteen saved seated passengers.");
                Require(social.cleaners!=null&&social.cleaners.Length==3&&social.cleaners.All(p=>p),"Expected three saved cleaners.");
                Require(social.seats!=null&&social.seats.Length==24&&social.seats.All(s=>s),"Expected twenty-four saved seats, including six spares.");
                var groups=social.conversationGroups;
                Require(groups!=null&&groups.Length==4&&groups.All(g=>g),"Expected four saved conversation groups.");
                Require(Components<StationConversationGroup>(scene).Length==groups.Length&&groups.Distinct().Count()==groups.Length,"Conversation groups contain duplicate or unregistered scene references.");
                Require(groups.Select(g=>g.name).Distinct().Count()==groups.Length,"Conversation group names must be distinct for inspection.");
                Require(groups.All(g=>g.members!=null&&g.members.Length==3&&g.members.All(m=>m)),"Each conversation group needs three saved members.");
                var members=groups.SelectMany(g=>g.members).ToArray();
                Require(members.Distinct().Count()==12&&Components<StationConversationMember>(scene).Length==12,"Expected twelve distinct saved standing conversation members.");
                Require(social.informationDesk&&social.informationDesk.receptionist,"Saved reception desk/staff references are missing.");
                report.seatedPassengers=social.passengers.Length;report.standingPassengers=members.Length;report.passengers=report.seatedPassengers+report.standingPassengers;
                report.conversationGroups=groups.Length;report.cleaners=social.cleaners.Length;report.seats=social.seats.Length;report.spareSeats=report.seats-report.seatedPassengers;
                report.receptionists=Components<StationReceptionist>(scene).Length;
                Require(report.receptionists==1,"Expected one saved receptionist.");
                Require(social.seats.Select(s=>s.seatId).Distinct().Count()==social.seats.Length&&social.seats.All(s=>!string.IsNullOrWhiteSpace(s.seatId)),"Seat IDs must be unique and nonempty.");
                var desk=social.informationDesk;
                Require(desk.queueSlots!=null&&desk.queueSlots.Length>=3&&desk.queueSlots.All(t=>t),"At least three authored queue positions are required.");
                Require(desk.queueJoined!=null&&desk.customerApproached!=null&&desk.conversationStarted!=null&&desk.conversationEnded!=null,"Serialized reception events are missing.");
                Require(desk.IsWithinServiceArea(desk.queueSlots[0].position),"Front service point is outside the receptionist's configured range.");
                var surfaces=Components<NavMeshSurface>(scene);
                report.navMeshSurfaces=surfaces.Length;
                Require(surfaces.Length>0&&surfaces.All(s=>s.navMeshData&&AssetDatabase.Contains(s.navMeshData)),"NavMesh data must be baked and saved as an asset.");
                foreach(var actor in social.passengers.Select(p=>p.motion).Concat(social.cleaners.Select(c=>c.motion)).Concat(members.Select(m=>m.motion)).Append(desk.receptionist.motion))
                {
                    Require(actor&&actor.animationPlayer&&actor.animationPlayer.ValidateSetup(out _),"An actor has invalid saved animation references/Avatar.");
                    Require(PrefabUtility.GetCorrespondingObjectFromOriginalSource(actor.gameObject),"Actor is not a reusable saved prefab instance: "+actor.name);
                    report.prefabActors++;
                    var library=actor.animationPlayer.Library;
                    Require(library&&library.Validate().Count==0,"Invalid shared social animation library.");
                    Require(library.Find(actor.idleId)?.clip&&library.Find(actor.walkId)?.clip,"Actor locomotion clips are missing: "+actor.name);
                    Require(actor.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(s=>s.sharedMesh&&s.sharedMesh.vertexCount>0),"Saved actor skin is missing: "+actor.name);
                }
                Require(report.prefabActors==34&&Components<StationActorMotion>(scene).Length==34,"Expected thirty-four saved human prefab actors.");
                foreach(var group in groups)
                {
                    Require(group.members.Select(m=>m.name).Distinct().Count()==group.members.Length,"Member names must be distinct within a conversation group.");
                    Require(group.speakerStarted!=null&&group.speakerEnded!=null&&group.automaticConversation,"Saved conversation events or automatic turn-taking are missing: "+group.name);
                    var centre=group.members.Aggregate(Vector3.zero,(sum,m)=>sum+m.transform.position)/group.members.Length;
                    foreach(var member in group.members)
                    {
                        Require(member.group==group&&member.motion&&member.motion.agent,"Conversation member references are incomplete: "+member.name);
                        Require(member.motion.agent.enabled&&member.motion.agent.obstacleAvoidanceType!=ObstacleAvoidanceType.NoObstacleAvoidance,"Standing conversation members need active navigation avoidance.");
                        Require(member.motion.animationPlayer.Library.Find(member.motion.talkId)?.clip,"Conversation member has no talking clip.");
                        CheckRoute(member.transform.position,desk.queueSlots[0].position,"standing member "+member.name);report.completeRoutes++;
                        Require(FacingAngle(member.transform,centre)<25f,"Saved conversation member does not face its group: "+member.name);
                        foreach(var other in group.members.Where(m=>m!=member))
                            Require(FlatDistance(member.transform.position,other.transform.position)>=member.motion.agent.radius+other.motion.agent.radius+.1f,"Saved conversation members overlap: "+member.name);
                    }
                }
                foreach(var seat in social.seats)
                {
                    Require(seat.seatedPose&&seat.approachPoint&&!seat.luggagePoint,"Seat pose, approach or no-luggage setup invalid: "+seat.seatId);
                    CheckRoute(seat.ApproachPosition,desk.queueSlots[0].position,"seat "+seat.seatId);report.completeRoutes++;
                }
                for(int i=0;i<desk.queueSlots.Length;i++)
                {
                    CheckRoute(desk.queueSlots[i].position,desk.queueSlots[0].position,"queue "+i);report.completeRoutes++;
                    if(i>0)Require(FlatDistance(desk.queueSlots[i].position,desk.queueSlots[i-1].position)>=.8f,"Queue positions overlap.");
                }
                foreach(var passenger in social.passengers)
                {
                    Require(passenger.motion&&passenger.motion.agent,"Passenger has no saved NavMeshAgent: "+passenger.name);
                    Require(passenger.world==social&&passenger.informationDesk==desk&&passenger.initialSeat&&social.seats.Contains(passenger.initialSeat),"Passenger world/desk/initial-seat references are incomplete: "+passenger.name);
                    Require(!passenger.luggage,"Passenger still references removed luggage: "+passenger.name);
                    var library=passenger.motion.animationPlayer.Library;
                    foreach(var id in new[]{passenger.motion.sitIdleId,passenger.motion.sitDownId,passenger.motion.standUpId,passenger.motion.talkId})
                        Require(library.Find(id)?.clip,"A passenger transition/conversation clip is missing: "+id);
                }
                Require(social.passengers.Select(p=>p.initialSeat).Distinct().Count()==social.passengers.Length,"Two authored passengers share an initial seat.");
                foreach(var cleaner in social.cleaners)
                {
                    Require(cleaner.motion&&cleaner.motion.agent,"Cleaner has no saved NavMeshAgent: "+cleaner.name);
                    Require(cleaner.motion.animationPlayer.Library.Find(cleaner.motion.sweepId)?.clip,"Cleaner has no authored cleaning motion: "+cleaner.name);
                    Require(cleaner.cleaningTool&&cleaner.cleaningTool.GetComponentsInChildren<Renderer>(true).Any(r=>r.enabled),"Cleaner has no visible saved tool: "+cleaner.name);
                    Require(cleaner.workPoints!=null&&cleaner.workPoints.Length>=2&&cleaner.workPoints.All(t=>t),"Cleaner needs saved work points: "+cleaner.name);
                    var grip=cleaner.motion.animationPlayer.TargetAnimator.GetComponent<StationCleaningTool>();
                    Require(grip&&grip.cleaner==cleaner&&grip.toolRoot==cleaner.cleaningTool,"Saved cleaner hand/tool references are incomplete: "+cleaner.name);
                    for(int i=0;i<cleaner.workPoints.Length;i++)
                    {CheckRoute(cleaner.workPoints[i].position,cleaner.workPoints[(i+1)%cleaner.workPoints.Length].position,cleaner.name+" work point "+i);report.completeRoutes++;}
                }
                Require(!Components<MonoBehaviour>(scene).Any(m=>m&&m.GetType().FullName=="StationFlow.SyntheticTraffic.DemoController"),"Junction Lab controller leaked into TrainStation.");
                report.success=true;
            }
            catch(Exception error){report.errors.Add(error.Message);}
            return report;
        }

        static void CheckRoute(Vector3 from,Vector3 to,string label)
        {
            Require(NavMesh.SamplePosition(from,out var start,.55f,NavMesh.AllAreas),"No baked walking surface at "+label);
            Require(NavMesh.SamplePosition(to,out var end,.55f,NavMesh.AllAreas),"No baked walking surface at destination for "+label);
            var path=new NavMeshPath();
            Require(NavMesh.CalculatePath(start.position,end.position,NavMesh.AllAreas,path)&&path.status==NavMeshPathStatus.PathComplete,"Incomplete baked NavMesh route: "+label);
        }

        static float PathLength(Vector3 from,Vector3 to)
        {
            Require(NavMesh.SamplePosition(from,out var start,.55f,NavMesh.AllAreas),"Arrival regression needs an authored start on the NavMesh.");
            Require(NavMesh.SamplePosition(to,out var end,.55f,NavMesh.AllAreas),"Arrival regression needs an authored destination on the NavMesh.");
            var path=new NavMeshPath();
            Require(NavMesh.CalculatePath(start.position,end.position,NavMesh.AllAreas,path)&&path.status==NavMeshPathStatus.PathComplete,"Arrival regression needs a complete physical route.");
            float length=0f;var corners=path.corners;
            for(int i=1;i<corners.Length;i++)length+=Vector3.Distance(corners[i-1],corners[i]);
            return length;
        }

        [MenuItem("Efrei Research Lab/Train Station Social/Check actual Play mode social cycle")]
        public static void BeginPlayCheck()
        {
            if (HasMixedCrowd()) { StationMixedCrowdVerification.BeginPlayCheck(); return; }
            if(Load()!=null)throw new InvalidOperationException("A station social Play check is already running.");
            var run=new Run{startedUtc=DateTime.UtcNow.ToString("O"),unityVersion=Application.unityVersion,phase="opening",deadlineTicks=DateTime.UtcNow.AddSeconds(210).Ticks};
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode&&!EditorApplication.isCompiling&&!EditorApplication.isUpdating,"Finish Play/import before checking.");
                CleanScenes();var setup=EditorSceneManager.GetSceneManagerSetup();
                Require(setup.Length>0&&setup.All(s=>!string.IsNullOrEmpty(s.path)),"Save all open scenes before checking so their setup can be restored.");
                run.scenes=setup.Select(s=>new SavedScene{path=s.path,isLoaded=s.isLoaded,isActive=s.isActive}).ToArray();
                run.originalPlayScene=AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene);
                run.originalOptionsEnabled=EditorSettings.enterPlayModeOptionsEnabled;run.originalOptions=(int)EditorSettings.enterPlayModeOptions;
                run.originalPaused=EditorApplication.isPaused;run.originalTimeScale=Time.timeScale;
                run.originalRunInBackground=Application.runInBackground;run.originalTargetFrameRate=Application.targetFrameRate;run.originalVSyncCount=QualitySettings.vSyncCount;
                run.capturedSettings=true;SessionState.EraseString(LogKey);Save(run);
                EditorSettings.enterPlayModeOptionsEnabled=false;EditorSceneManager.playModeStartScene=null;EditorApplication.isPaused=false;
            }
            catch(Exception error){run.errors.Add(error.Message);run.abort=true;Finish(run);}
        }

        static void Tick()
        {
            if(EditorApplication.timeSinceStartup<nextPoll)return;nextPoll=EditorApplication.timeSinceStartup+.03;
            var run=Load();if(run==null)return;
            if(run.phase=="restoring"&&!EditorApplication.isPlayingOrWillChangePlaymode){Finish(run);return;}
            if(run.abort&&run.phase=="exiting")
            {if(!EditorApplication.isPlayingOrWillChangePlaymode)Finish(run);return;}
            try
            {
                Require(DateTime.UtcNow.Ticks<run.deadlineTicks,"Social Play check timed out in "+run.phase+". "+DescribeActors());
                if(EditorApplication.isCompiling||EditorApplication.isUpdating)return;
                Require(run.errors.Count==0,string.Join("\n",run.errors));
                Require(string.IsNullOrEmpty(SessionState.GetString(LogKey,"")),"Unity logged an error during the social test.");
                if(run.phase=="opening"&&!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    CleanScenes();var scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
                    var saved=InspectSavedScene(scene);Write("Reports/TrainStationSocial/saved-scene-check.json",saved);
                    Require(saved.success,string.Join("\n",saved.errors));
                    run.baselineObjects=Components<Transform>(scene).Length;run.baselineCameras=Components<Camera>(scene).Count(c=>c.isActiveAndEnabled);run.baselineLights=Components<Light>(scene).Count(l=>l.isActiveAndEnabled);
                    run.phase="entering";Save(run);EditorApplication.isPlaying=true;
                }
                else if(run.phase=="initializing"&&EditorApplication.isPlaying&&Time.frameCount>=run.checkAfterFrame)
                {InitializeScenario(run);run.phase="scenario";Save(run);}
                else if(run.phase=="scenario"&&EditorApplication.isPlaying)
                {
                    ObserveScenario(run);
                    if(ScenarioComplete(run))
                    {
                        FinalAssertions(run);run.phase="exiting";Save(run);Time.timeScale=run.originalTimeScale;EditorApplication.isPlaying=false;
                    }
                    else Save(run);
                }
                else if(run.phase=="restoring"&&!EditorApplication.isPlayingOrWillChangePlaymode)Finish(run);
            }
            catch(Exception error)
            {
                run.errors.Add(error.ToString());run.abort=true;run.phase="exiting";Save(run);Write(ReportPath,run);
                if(EditorApplication.isPlayingOrWillChangePlaymode){Time.timeScale=run.originalTimeScale;EditorApplication.isPlaying=false;}
                else Finish(run);
            }
        }

        static void InitializeScenario(Run run)
        {
            var scene=SceneManager.GetActiveScene();Require(scene.path==ScenePath,"Unexpected startup scene.");
            world=Components<StationSocialWorld>(scene).Single();
            foreach(var passenger in world.passengers){passenger.automaticRequests=false;passenger.transitionDurationScale=.25f;passenger.navigationTimeout=120f;passenger.queueWaitTimeout=300f;}
            Require(world.passengers.All(p=>p.CurrentState.ToString()=="Seated"&&p.CurrentSeat&&p.CurrentSeat.Occupant==p),"All authored passengers must initialize into distinct occupied seats.");
            var desk=world.informationDesk;var front=desk.queueSlots[0].position;
            var routes=world.passengers.Select(p=>new{passenger=p,length=PathLength(p.CurrentSeat.ApproachPosition,front)}).OrderBy(p=>p.length).ToArray();
            Require(routes.Last().length>routes[0].length+2f,"Arrival regression requires materially different saved-seat route lengths.");
            participants=new[]{routes.Last().passenger,routes[0].passenger,routes[1].passenger};conflictSeat=participants[0].CurrentSeat;
            participants[0].motion.SetTravelSpeed(Mathf.Max(.3f,participants[0].motion.RequestedTravelSpeed*.55f));
            blocker=world.passengers.Except(participants).OrderBy(p=>FlatDistance(p.transform.position,conflictSeat.ApproachPosition)).First();
            run.blockerName=blocker.name;run.conflictSeat=conflictSeat.seatId;
            run.actors=participants.Select(p=>new ActorResult{name=p.name,originalSeat=p.CurrentSeat.seatId,
                initialPathLength=PathLength(p.CurrentSeat.ApproachPosition,front),walkingSpeed=p.motion.agent.speed}).ToList();
            run.cleaners=world.cleaners.Select(c=>new CleanerResult{name=c.name}).ToList();workingHands.Clear();workingTools.Clear();workingStarted.Clear();checkedWorkingSkin.Clear();
            run.conversationGroups=world.conversationGroups.Select(g=>new ConversationGroupResult{name=g.name,
                initialSpeaker=g.ActiveSpeaker?g.ActiveSpeaker.name:"",previousSpeaker=g.ActiveSpeaker?g.ActiveSpeaker.name:"",
                initialCompletedTurns=g.CompletedTurns,speakerChangedAt=Time.time,
                members=g.members.Select(m=>new ConversationMemberResult{name=m.name,initialPosition=m.transform.position}).ToList()}).ToList();
            foreach(var group in world.conversationGroups)
            {group.speakerStarted.AddListener(OnGroupSpeakerStarted);group.speakerEnded.AddListener(OnGroupSpeakerEnded);}
            // Keep the first real conversation open long enough for all three physically arriving customers to queue.
            desk.conversationDuration=Mathf.Clamp(run.actors[0].initialPathLength/run.actors[0].walkingSpeed+10f,24f,70f);
            foreach(var cleaner in world.cleaners)cleaner.workDuration=3f;
            var direction=front-desk.ReceptionistPosition;direction.y=0f;direction.Normalize();
            Require(desk.IsWithinServiceArea(front),"Authored front point does not trigger information service.");
            Require(!desk.IsWithinServiceArea(desk.ReceptionistPosition-direction*.6f),"Nearness behind the staff incorrectly triggers service.");
            Require(!desk.IsWithinServiceArea(front+Vector3.Cross(Vector3.up,direction)*1.5f),"A customer beside the queue incorrectly triggers service.");
            run.frontGatePassed=true;
            desk.queueJoined.AddListener(OnJoined);desk.customerApproached.AddListener(OnApproached);desk.conversationStarted.AddListener(OnStarted);desk.conversationEnded.AddListener(OnEnded);
            run.simulationStarted=Time.time;
            Require(!desk.TryJoin(participants[0])&&desk.QueueCount==0,"Direct TryJoin admitted a distant seated passenger before any journey.");
            RequestVisit(run,0);
            Require(desk.QueueCount==0&&!desk.ActiveCustomer,"A distant request reserved a queue position before physical arrival.");
            run.requestDidNotReservePassed=true;
            Application.runInBackground=true;Application.targetFrameRate=60;QualitySettings.vSyncCount=0;Time.timeScale=4f;
        }

        static void RequestVisit(Run run,int index)
        {
            var passenger=participants[index];var desk=world.informationDesk;int before=desk.QueueCount;
            run.requested.Add(passenger.name);run.actors[index].requestTime=Time.time-run.simulationStarted;
            Require(passenger.TryRequestInformation(),"Could not request information: "+passenger.name);
            run.actors[index].states.Add(passenger.CurrentState.ToString());
            Require(!desk.Contains(passenger)&&desk.GetQueueIndex(passenger)==-1&&desk.QueueCount==before,"A request reserved a queue position while its passenger was still at the bench.");
        }

        static void ObserveScenario(Run run)
        {
            Require(world&&participants!=null,"Verification lost its live scene references during Play.");
            run.framesObserved++;run.simulatedSeconds=Time.time-run.simulationStarted;
            var desk=world.informationDesk;run.queuePeak=Mathf.Max(run.queuePeak,desk.QueueCount);
            ObserveLocomotion(run);
            if(run.requested.Count==1&&run.locomotion.passed&&participants[0].CurrentState.ToString()=="ApproachingQueue"&&run.actors[0].approachTravel>.5f)
            {
                Require(desk.QueueCount==0&&!desk.ActiveCustomer,"A distant traveller occupied an empty queue before arriving.");
                Require(!desk.TryJoin(participants[0])&&desk.QueueCount==0,"Direct TryJoin admitted a requesting traveller still far from the queue.");
                RequestVisit(run,1);
            }
            if(run.requested.Count==2&&run.joined.Contains(participants[1].name))RequestVisit(run,2);
            if(run.joined.Count==3)desk.conversationDuration=8f;
            ObserveQueue(run);
            if(!run.seatConflictRequested&&conflictSeat.IsAvailable)
            {
                Require(blocker.TryRequestSeat(conflictSeat),"Blocker could not begin a real transfer into the vacated seat.");
                Require(conflictSeat.ReservedBy==blocker,"Seat transfer did not reserve its target atomically.");
                Require(!conflictSeat.TryReserve(participants[0])&&!conflictSeat.TryOccupy(participants[0]),"Two passengers could claim the same reserved seat.");
                run.atomicSeatPassed=true;run.seatConflictRequested=true;
            }
            if(run.seatConflictRequested&&blocker.CurrentState.ToString()=="Seated"&&blocker.CurrentSeat==conflictSeat&&conflictSeat.Occupant==blocker)
                run.seatConflictOccupied=true;
            var allTalking=world.passengers.Where(p=>p.CurrentState.ToString()=="Talking").ToArray();
            Require(allTalking.Length<=1,"More than one customer is talking to the receptionist.");
            if(desk.ActiveCustomer)
            {
                Require(desk.GetQueueIndex(desk.ActiveCustomer)==0,"A later queue member overtook the first.");
                Require(desk.IsWithinServiceArea(desk.ActiveCustomer.transform.position),"Conversation started outside the front reception area.");
                Require(desk.receptionist.CurrentCustomer==desk.ActiveCustomer,"Receptionist and queue disagree about the customer.");
                Require(desk.receptionist.motion.CurrentMotion==desk.receptionist.motion.talkId,"Receptionist is not using the conversation motion.");
                var staffAnimator=desk.receptionist.motion.animationPlayer.TargetAnimator;
                if(!run.receptionImageCaptured&&!staffAnimator.IsInTransition(0)&&staffAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime>.15f)
                {StationSocialCapture.CaptureLiveReception();run.receptionImageCaptured=true;}
            }
            foreach(var seat in world.seats)
                Require(!seat.Occupant||seat.ReservedBy==seat.Occupant,"A seat's occupant differs from its reservation owner: "+seat.seatId);
            var occupied=world.seats.Where(s=>s.Occupant).Select(s=>s.Occupant).ToArray();
            Require(occupied.Distinct().Count()==occupied.Length,"A passenger simultaneously occupies more than one seat.");
            for(int i=0;i<participants.Length;i++)
            {
                var passenger=participants[i];var result=run.actors[i];var state=passenger.CurrentState.ToString();
                if(!result.states.Contains(state))result.states.Add(state);
                Require(state!="Disabled",passenger.name+" entered Disabled state.");
                Require(!passenger.motion.NavigationFailed,passenger.name+": "+passenger.motion.NavigationIssue);
                result.conversations=passenger.CompletedConversations;
                if(state!="ApproachingQueue"&&state!="WalkingToQueue"&&state!="ReturningToSeat")result.movingSince=result.stationarySince=-1f;
                if(state=="ApproachingQueue")
                {
                    Require(!desk.Contains(passenger)&&desk.GetQueueIndex(passenger)==-1,"An approaching traveller has a reserved queue index.");
                    if(result.approachPositionRecorded)result.approachTravel+=FlatDistance(result.previousApproachPosition,passenger.transform.position);
                    result.previousApproachPosition=passenger.transform.position;result.approachPositionRecorded=true;
                    var agent=passenger.motion.agent;
                    if(agent.enabled&&agent.isOnNavMesh&&!agent.pathPending&&agent.hasPath)
                    {
                        Require(agent.pathStatus==NavMeshPathStatus.PathComplete,"An approaching traveller has no complete live path.");
                        if(Finite(agent.remainingDistance))result.maximumApproachRemainingPath=Mathf.Max(result.maximumApproachRemainingPath,agent.remainingDistance);
                    }
                }
                if(state=="ApproachingQueue"||state=="WalkingToQueue"||state=="ReturningToSeat")
                {
                    CheckMovingPresentation(passenger,result);
                    Require(!passenger.luggage,"A suitcase reference returned while a passenger was travelling.");
                }
                if(state=="Talking")
                {result.talkingObserved=true;Require(passenger.motion.CurrentMotion==passenger.motion.talkId,"Talking customer has no conversation animation.");Require(Mathf.Abs(passenger.motion.animationPlayer.PlaybackSpeed-1f)<.01f,"Conversation inherited a locomotion playback speed.");}
                if(state=="Seated"&&passenger.CompletedConversations>0)
                {
                    Require(passenger.CurrentSeat&&passenger.CurrentSeat.Occupant==passenger,"Returned passenger has no occupied seat.");
                    result.finalSeat=passenger.CurrentSeat.seatId;result.seatedAgain=true;
                    Require(passenger.motion.CurrentMotion==passenger.motion.sitIdleId,"Returned passenger has no seated idle animation.");
                    Require(Mathf.Abs(passenger.motion.animationPlayer.PlaybackSpeed-1f)<.01f,"Seated idle inherited a locomotion playback speed.");
                }
            }
            ObserveCleaners(run);
            ObserveConversationGroups(run);
            ObserveAvoidance(run);
            Require(run.simulatedSeconds<480,"Scenario did not complete within eight simulated minutes. "+DescribeActors());
        }

        static void CheckMovingPresentation(StationPassenger passenger,ActorResult result)
        {
            var motion=passenger.motion;var agent=motion.agent;
            float speed=agent&&agent.enabled&&agent.isOnNavMesh?new Vector2(agent.velocity.x,agent.velocity.z).magnitude:0f;
            if(speed>.15f)
            {
                result.walkingObserved=true;result.stationarySince=-1f;
                if(result.movingSince<0f)result.movingSince=Time.time;
                if(Time.time-result.movingSince>.4f)Require(motion.CurrentMotion==motion.walkId,"A physically moving passenger has no walk animation.");
            }
            else if(speed<.03f)
            {
                result.movingSince=-1f;
                if(result.stationarySince<0f)result.stationarySince=Time.time;
                if(Time.time-result.stationarySince>.65f)Require(motion.CurrentMotion==motion.idleId,"A stopped passenger keeps walking in place.");
            }
            else {result.movingSince=-1f;result.stationarySince=-1f;}
        }

        static void ObserveLocomotion(Run run)
        {
            var result=run.locomotion;if(result.passed)return;
            var passenger=participants[0];var motion=passenger.motion;var agent=motion.agent;
            var animator=motion.animationPlayer.TargetAnimator;
            if(!result.started)
            {
                if(passenger.CurrentState.ToString()!="ApproachingQueue")return;
                Require(agent.enabled&&agent.isOnNavMesh&&agent.hasPath,"Locomotion regression needs a real active NavMesh path.");
                result.started=true;result.actor=passenger.name;result.originalSpeed=agent.speed;
                result.referenceSpeed=motion.EffectiveWalkReferenceSpeed;result.clipDuration=motion.Duration(motion.walkId);
                Require(Finite(result.referenceSpeed)&&result.referenceSpeed>.1f&&result.clipDuration>.1f,"Walking reference speed and clip duration must be calibrated positive values.");
                BeginLocomotionStage(result,motion,0);
            }
            Require(passenger.CurrentState.ToString()=="ApproachingQueue","The speed regression exhausted its authored approach before completing the gait checks.");
            Require(agent.hasPath&&!motion.NavigationFailed,"Locomotion regression lost its real navigation path.");
            float elapsed=Time.time-result.stageStarted;
            Require(elapsed<10f,"Locomotion window failed to collect real movement: "+result.phase);
            ObserveWalkPose(result,passenger);
            float velocity=new Vector2(agent.velocity.x,agent.velocity.z).magnitude;
            if(velocity-result.previousVelocity>.035f)result.accelerationObserved=true;
            if(result.previousVelocity-velocity>.035f)result.decelerationObserved=true;
            result.previousVelocity=velocity;
            if(result.stage==4)
            {
                Require(agent.isStopped,"A retained navigation path overrode the externally requested stop.");
                Require(FlatDistance(agent.destination,result.pauseDestination)<.02f,"Stopping unexpectedly changed the active path destination.");
                if(elapsed>.6f)
                {
                    Require(motion.CurrentMotion==motion.idleId&&!animator.IsInTransition(0),"A stopped actor did not settle into idle while retaining its path.");
                    Require(Mathf.Abs(motion.animationPlayer.PlaybackSpeed-1f)<.01f,"Stopped idle inherited the walk playback multiplier.");
                    if(result.sampleReady)result.pauseSettledDistance+=FlatDistance(result.previousPosition,passenger.transform.position);
                    result.sampleReady=true;result.previousPosition=passenger.transform.position;
                    Require(result.pauseSettledDistance<.03f,"The actor moved during its settled stopped interval.");
                }
                if(elapsed>=1.25f)
                {
                    result.stoppedIdlePassed=true;agent.isStopped=false;
                    BeginLocomotionStage(result,motion,5);
                }
                return;
            }
            var state=animator.GetCurrentAnimatorStateInfo(0);
            bool walking=motion.CurrentMotion==motion.walkId&&!animator.IsInTransition(0)&&
                state.IsName(motion.animationPlayer.Library.Find(motion.walkId).stateName);
            if(elapsed<.65f||!walking){result.sampleReady=false;return;}
            var window=result.windows[result.windows.Count-1];
            if(result.sampleReady)
            {
                float duration=Time.time-result.previousTime;float cycles=state.normalizedTime-result.previousNormalizedTime;
                Require(duration>=0f&&cycles>=-.001f,"Walking animation restarted or ran backwards during continuous navigation.");
                if(duration>0f)
                {
                    window.duration+=duration;window.distance+=FlatDistance(result.previousPosition,passenger.transform.position);
                    window.animatedDistance+=Mathf.Max(0f,cycles)*result.clipDuration*result.referenceSpeed;
                    window.playbackIntegral+=motion.animationPlayer.PlaybackSpeed*duration;window.samples++;
                }
            }
            result.sampleReady=true;result.previousTime=Time.time;result.previousPosition=passenger.transform.position;result.previousNormalizedTime=state.normalizedTime;
            if(window.duration<1.6f||window.distance<.35f)return;
            window.meanWorldSpeed=window.distance/window.duration;window.meanPlaybackSpeed=window.playbackIntegral/window.duration;
            window.relativePhaseError=Mathf.Abs(window.animatedDistance-window.distance)/window.distance;
            Require(Mathf.Abs(window.animatedDistance-window.distance)<=Mathf.Max(.06f,window.distance*.20f),
                "Animator gait phase does not match actual world displacement in "+window.name+": walked "+window.distance.ToString("F3")+" m, animated "+window.animatedDistance.ToString("F3")+" m.");
            float expectedPlayback=window.meanWorldSpeed/result.referenceSpeed;
            Require(Mathf.Abs(window.meanPlaybackSpeed-expectedPlayback)<=Mathf.Max(.035f,expectedPlayback*.20f),"Playback rate does not match measured movement in "+window.name+".");
            if(result.stage==5)
            {
                result.resumedPassed=true;
                Require(result.windows[1].meanWorldSpeed>result.windows[0].meanWorldSpeed+.15f&&result.windows[2].meanWorldSpeed>result.windows[1].meanWorldSpeed+.15f&&
                    result.windows[3].meanWorldSpeed<result.windows[2].meanWorldSpeed*.7f,"The scenario did not exercise actual acceleration and deceleration across distinct walking speeds.");
                Require(result.accelerationObserved&&result.decelerationObserved&&result.sampledWalkCycles>=2f,"Insufficient live acceleration, deceleration or complete gait cycles were observed.");
                result.passed=true;result.phase="complete";agent.speed=result.originalSpeed;
            }
            else BeginLocomotionStage(result,motion,result.stage+1);
        }

        static void BeginLocomotionStage(LocomotionResult result,StationActorMotion motion,int stage)
        {
            result.stage=stage;result.stageStarted=Time.time;result.sampleReady=false;
            if(stage==4)
            {
                result.phase="stopped with active path";result.pauseDestination=motion.agent.destination;
                motion.agent.isStopped=true;return;
            }
            float speed=stage==0?.35f:stage==1?.70f:stage==2?1.05f:stage==3?.35f:result.originalSpeed;
            string label=stage==0?"slow":stage==1?"medium":stage==2?"brisk":stage==3?"decelerated":"resumed";
            result.phase=label;motion.SetTravelSpeed(speed);
            result.windows.Add(new LocomotionWindow{name=label,requestedSpeed=speed});
        }

        static void ObserveWalkPose(LocomotionResult result,StationPassenger passenger)
        {
            var motion=passenger.motion;var animator=motion.animationPlayer.TargetAnimator;var state=animator.GetCurrentAnimatorStateInfo(0);
            if(motion.CurrentMotion!=motion.walkId||animator.IsInTransition(0)||!state.IsName(motion.animationPlayer.Library.Find(motion.walkId).stateName))
            {result.poseSampleReady=false;return;}
            var localHip=passenger.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.Hips).position);
            Require(Finite(localHip),"Walking generated a non-finite hips pose.");
            if(result.hipSamples==0)result.minimumLocalHip=result.maximumLocalHip=localHip;
            else {result.minimumLocalHip=Vector3.Min(result.minimumLocalHip,localHip);result.maximumLocalHip=Vector3.Max(result.maximumLocalHip,localHip);}
            if(result.poseSampleReady)
            {
                float cycles=state.normalizedTime-result.previousPosePhase;
                Require(cycles>=-.001f,"The walking pose restarted within a continuous gait cycle.");
                result.sampledWalkCycles+=Mathf.Max(0f,cycles);
                result.maximumHipPlanarJump=Mathf.Max(result.maximumHipPlanarJump,FlatDistance(result.previousLocalHip,localHip));
            }
            result.hipSamples++;result.poseSampleReady=true;result.previousLocalHip=localHip;result.previousPosePhase=state.normalizedTime;
            Require(FlatDistance(result.minimumLocalHip,result.maximumLocalHip)<animator.humanScale*.35f&&result.maximumHipPlanarJump<animator.humanScale*.25f,
                "Walking hips drift or snap relative to the navigation root; the clip may contain baked forward translation.");
        }

        static void ObserveQueue(Run run)
        {
            var desk=world.informationDesk;
            var admitted=run.joined.Where(name=>!run.ended.Contains(name)).ToArray();
            Require(desk.QueueCount==admitted.Length,"Queue membership includes a distant request, an unreported arrival, or a departed customer.");
            int previousHeldSlot=-1;
            for(int i=0;i<admitted.Length;i++)
            {
                var passenger=participants.Single(p=>p.name==admitted[i]);var result=run.actors.Single(a=>a.name==passenger.name);
                Require(desk.GetQueueIndex(passenger)==i,"Physical arrival order contains an empty index or was overtaken.");
                Require(desk.TryGetQueueSlot(passenger,out var slot),"Admitted customer has no physical queue target.");
                int heldSlot=Array.IndexOf(desk.queueSlots,slot);
                Require(heldSlot>=i&&heldSlot>previousHeldSlot,"Individual queue targets overtook or shared an occupied position.");
                previousHeldSlot=heldSlot;
                if(result.previousQueueIndex>i){result.queueAdvances+=result.previousQueueIndex-i;result.queueIndexChangedAt=Time.time;}
                result.previousQueueIndex=i;
                // FIFO membership compacts immediately, while physical targets advance after
                // each traveller observes their predecessor and reacts independently.
                if(passenger.CurrentState.ToString()=="WaitingInQueue"&&Time.time-desk.GetLastQueueAdvanceTime(passenger)>.2f)
                {
                    Require(FlatDistance(passenger.transform.position,slot.position)<=Mathf.Max(.35f,desk.arrivalTolerance),"A waiting passenger left a physical gap at its assigned queue position.");
                    result.settledQueueSamples++;
                }
            }
            if(run.joined.Count>0)
            {
                Require(run.joined[0]==participants[1].name,"The nearby later requester did not physically join first.");
                Require(run.actors[1].requestTime>run.actors[0].requestTime,"The near traveller was not requested after the distant traveller.");
                run.laterRequestArrivedFirstPassed=true;
            }
            if(run.started.Count>0)Require(run.started[0]==participants[1].name,"A distant early request blocked service for the physically first arrival.");
        }

        static void ObserveCleaners(Run run)
        {
            for(int i=0;i<world.cleaners.Length;i++)
            {
                var cleaner=world.cleaners[i];var result=run.cleaners[i];var state=cleaner.CurrentState.ToString();
                Require(state!="Disabled",cleaner.name+" entered Disabled state.");
                Require(!cleaner.motion.NavigationFailed,cleaner.name+": "+cleaner.motion.NavigationIssue);
                result.completedWorkCycles=cleaner.CompletedWorkCycles;
                if(state=="Patrol")result.patrolObserved=true;
                if(state!="Working")
                {
                    workingHands.Remove(cleaner);workingTools.Remove(cleaner);workingStarted.Remove(cleaner);checkedWorkingSkin.Remove(cleaner);
                }
                if(state=="Working")
                {
                    result.workObserved=true;Require(cleaner.motion.CurrentMotion==cleaner.motion.sweepId,"Working cleaner has no cleaning motion.");
                    Require(cleaner.cleaningTool&&cleaner.cleaningTool.GetComponentsInChildren<Renderer>().Any(r=>r.enabled),"Working cleaner has no visible tool.");
                    result.toolObserved=true;
                    var hand=cleaner.motion.animationPlayer.TargetAnimator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                    if(hand)
                    {
                        if(!workingHands.TryGetValue(cleaner,out var initial))workingHands[cleaner]=hand.localRotation;
                        else result.maximumHandAngle=Mathf.Max(result.maximumHandAngle,Quaternion.Angle(initial,hand.localRotation));
                    }
                    if(!workingTools.TryGetValue(cleaner,out var start))
                    {workingTools[cleaner]=cleaner.cleaningTool.position;workingStarted[cleaner]=Time.time;}
                    else
                    {
                        result.maximumToolTravel=Mathf.Max(result.maximumToolTravel,Vector3.Distance(start,cleaner.cleaningTool.position));
                        if(Time.time-workingStarted[cleaner]>.7f)
                        {
                            var grip=cleaner.motion.animationPlayer.TargetAnimator.GetComponent<StationCleaningTool>();
                            Require(grip,"Working cleaner has no hand/tool constraint.");
                            var animator=cleaner.motion.animationPlayer.TargetAnimator;
                            if(animator.IsInTransition(0)||grip.CurrentIKWeight<.995f)continue;
                            result.settledGripSamples++;
                            result.maximumRightGripError=Mathf.Max(result.maximumRightGripError,grip.RightGripError);
                            result.maximumLeftGripError=Mathf.Max(result.maximumLeftGripError,grip.LeftGripError);
                            result.maximumBrushGroundError=Mathf.Max(result.maximumBrushGroundError,Mathf.Abs(cleaner.cleaningTool.position.y-cleaner.transform.position.y-grip.groundOffset));
                            if(grip.RightGripError>=.15f||grip.LeftGripError>=.15f)
                            {
                                result.failedGripPose=new GripPose{simulationTime=Time.time-run.simulationStarted,humanScale=animator.humanScale,
                                    actorPosition=cleaner.transform.position,hipsPosition=animator.GetBoneTransform(HumanBodyBones.Hips).position,
                                    leftShoulderPosition=animator.GetBoneTransform(HumanBodyBones.LeftUpperArm).position,rightShoulderPosition=animator.GetBoneTransform(HumanBodyBones.RightUpperArm).position,
                                    leftHandPosition=animator.GetBoneTransform(HumanBodyBones.LeftHand).position,rightHandPosition=animator.GetBoneTransform(HumanBodyBones.RightHand).position,
                                    leftGripTarget=grip.toolRoot.position+grip.toolRoot.up*grip.secondaryGripHeight,rightGripTarget=grip.toolRoot.position+grip.toolRoot.up*grip.primaryGripHeight,
                                    modelLossyScale=animator.transform.lossyScale};
                                try{StationSocialCapture.CaptureCleanerFailure(cleaner);}
                                catch(Exception error){run.errors.Add("Could not capture cleaner failure: "+error.Message);}
                            }
                            Require(grip.RightGripError<.15f&&grip.LeftGripError<.15f,"Cleaner hands do not meet the tool shaft.");
                            Require(result.maximumBrushGroundError<.03f,"Cleaning brush does not follow the floor height.");
                            if(!run.cleaningImageCaptured){StationSocialCapture.CaptureLiveCleaner();run.cleaningImageCaptured=true;}
                            float ground=cleaner.transform.position.y;
                            float hipHeight=animator.GetBoneTransform(HumanBodyBones.Hips).position.y-ground;
                            float leftFoot=animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y-ground;
                            float rightFoot=animator.GetBoneTransform(HumanBodyBones.RightFoot).position.y-ground;
                            result.minimumHipHeight=Mathf.Min(result.minimumHipHeight,hipHeight);
                            result.minimumFootHeight=Mathf.Min(result.minimumFootHeight,Mathf.Min(leftFoot,rightFoot));
                            result.maximumFootHeight=Mathf.Max(result.maximumFootHeight,Mathf.Max(leftFoot,rightFoot));
                            Require(hipHeight>animator.humanScale*.55f&&hipHeight<animator.humanScale*1.2f,"Cleaning pose has an invalid standing hip height; check Humanoid body/root curves.");
                            Require(leftFoot>-.05f&&rightFoot>-.05f&&leftFoot<animator.humanScale*.20f&&rightFoot<animator.humanScale*.20f,"Cleaning feet are below the floor or floating above it.");
                            if(checkedWorkingSkin.Add(cleaner))CheckWorkingGeometry(cleaner,result,ground);
                        }
                    }
                }
            }
        }

        static void CheckWorkingGeometry(StationCleaner cleaner,CleanerResult result,float ground)
        {
            var skins=cleaner.motion.animationPlayer.TargetAnimator.GetComponentsInChildren<SkinnedMeshRenderer>();
            Require(skins.Length>0,"Cleaner has no visible skinned geometry.");
            float minimum=float.PositiveInfinity,maximum=float.NegativeInfinity;
            foreach(var skin in skins)
            {
                var mesh=new Mesh();
                try
                {
                    skin.BakeMesh(mesh,false);var vertices=mesh.vertices;
                    Require(vertices.Length>0,"Cleaning pose produced an empty skin.");
                    foreach(var vertex in vertices)
                    {
                        var worldVertex=skin.transform.TransformPoint(vertex);
                        Require(Finite(worldVertex),"Cleaning pose produced a non-finite skin vertex.");
                        minimum=Mathf.Min(minimum,worldVertex.y-ground);maximum=Mathf.Max(maximum,worldVertex.y-ground);
                    }
                }
                finally{UnityEngine.Object.DestroyImmediate(mesh);}
            }
            result.geometrySamples++;result.minimumSkinHeight=Mathf.Min(result.minimumSkinHeight,minimum);result.maximumSkinHeight=Mathf.Max(result.maximumSkinHeight,maximum);
            Require(minimum>-.08f&&minimum<=.035f&&maximum>cleaner.motion.animationPlayer.TargetAnimator.humanScale*1.4f,"Cleaning skin intersects the floor, floats above it, or lacks a standing body's height.");
        }

        static void ObserveConversationGroups(Run run)
        {
            foreach(var group in world.conversationGroups)
            {
                var result=run.conversationGroups.Single(g=>g.name==group.name);
                Require(group.isActiveAndEnabled&&group.automaticConversation,"An authored conversation group stopped running.");
                result.completedTurns=group.CompletedTurns-result.initialCompletedTurns;
                var speaker=group.ActiveSpeaker;string name=speaker?speaker.name:"";
                if(result.previousSpeaker!=name){result.previousSpeaker=name;result.speakerChangedAt=Time.time;}
                Require(group.members.Count(m=>m.IsSpeaking)==(speaker?1:0),"Conversation group has multiple speakers or an inconsistent active turn.");
                foreach(var member in group.members)
                {
                    var observed=result.members.Single(m=>m.name==member.name);var motion=member.motion;var agent=motion.agent;
                    Require(member.isActiveAndEnabled&&member.NavigationReady&&agent.enabled&&agent.isOnNavMesh&&agent.isStopped,"Standing group member is missing its stopped navigation agent: "+member.name);
                    observed.maximumTravel=Mathf.Max(observed.maximumTravel,FlatDistance(observed.initialPosition,member.transform.position));
                    Require(observed.maximumTravel<.06f&&new Vector2(agent.velocity.x,agent.velocity.z).magnitude<.05f,"A standing conversation member drifted or walked away: "+member.name);
                    bool speaking=member==speaker;
                    Require(member.IsSpeaking==speaking&&motion.CurrentMotion==(speaking?motion.talkId:motion.idleId),"Conversation role and selected animation disagree: "+member.name);
                    Require(!motion.LocomotionActive&&Mathf.Abs(motion.animationPlayer.PlaybackSpeed-1f)<.01f,"Standing conversation inherited navigation playback speed.");
                    if(speaking)observed.speakerObserved=true;
                    else if(speaker)observed.listenerObserved=true;
                    if(speaker&&Time.time-result.speakerChangedAt>.9f)
                    {
                        var target=speaking?group.members.Where(m=>m!=member).Aggregate(Vector3.zero,(sum,m)=>sum+m.transform.position)/(group.members.Length-1):speaker.transform.position;
                        float angle=FacingAngle(member.transform,target);observed.maximumSettledFacingError=Mathf.Max(observed.maximumSettledFacingError,angle);observed.settledFacingSamples++;
                        Require(angle<15f,"A settled conversation member is not facing the speaker or listeners: "+member.name);
                    }
                }
                if(!run.groupImageCaptured&&speaker&&Time.time-result.speakerChangedAt>.9f&&!speaker.motion.animationPlayer.TargetAnimator.IsInTransition(0))
                {StationSocialCapture.CaptureLiveGroup(group);run.groupImageCaptured=true;}
            }
        }

        static void ObserveAvoidance(Run run)
        {
            var agents=world.GetComponentsInChildren<NavMeshAgent>(true).Where(a=>a.isActiveAndEnabled&&a.isOnNavMesh).ToArray();
            for(int i=0;i<agents.Length;i++)for(int j=i+1;j<agents.Length;j++)
            {
                float distance=FlatDistance(agents[i].transform.position,agents[j].transform.position);
                run.minimumMovingActorDistance=Mathf.Min(run.minimumMovingActorDistance,distance);
                Require(distance>=Mathf.Max(.35f,agents[i].radius+agents[j].radius-.08f),"Walking actors overlap: "+agents[i].name+" / "+agents[j].name+" at "+distance.ToString("F3")+" m.");
            }
            foreach(var member in world.conversationGroups.SelectMany(g=>g.members))
            foreach(var moving in agents.Where(a=>!a.isStopped&&new Vector2(a.velocity.x,a.velocity.z).magnitude>.15f))
            {
                float distance=FlatDistance(member.transform.position,moving.transform.position);
                run.minimumMovingToGroupDistance=Mathf.Min(run.minimumMovingToGroupDistance,distance);
                if(distance<3f)run.movingNearGroupSamples++;
            }
        }

        static bool ScenarioComplete(Run run)=>run.actors.All(a=>a.seatedAgain)&&run.seatConflictOccupied
            &&run.cleaners.All(c=>c.completedWorkCycles>0&&c.workObserved&&c.toolObserved&&c.maximumHandAngle>.1f&&c.maximumToolTravel>.02f&&c.settledGripSamples>0&&c.geometrySamples>0)
            &&run.conversationGroups.Count==4&&run.conversationGroups.All(g=>g.completedTurns>=3&&g.members.All(m=>m.speakerObserved&&m.listenerObserved&&m.settledFacingSamples>0));

        static void FinalAssertions(Run run)
        {
            Require(run.locomotion.passed&&run.locomotion.stoppedIdlePassed&&run.locomotion.resumedPassed,"Actual locomotion speed, stop and resume regression did not complete.");
            Require(run.groupImageCaptured&&run.movingNearGroupSamples>0,"Standing groups were not captured or no real walker passed close enough to observe their avoidance.");
            foreach(var group in run.conversationGroups)
            {
                Require(group.speakerStarts.Distinct().Count()==3&&group.speakerEnds.Count>=3,"Conversation did not rotate through all three authored speakers.");
                var expectedEnds=(string.IsNullOrEmpty(group.initialSpeaker)?Enumerable.Empty<string>():new[]{group.initialSpeaker}).Concat(group.speakerStarts).ToArray();
                Require(group.speakerEnds.SequenceEqual(expectedEnds.Take(group.speakerEnds.Count)),"Conversation start/end events disagree with completed turn order.");
                Require(!group.speakerStarts.Zip(group.speakerStarts.Skip(1),(a,b)=>a==b).Any(same=>same),"Conversation repeated the same speaker without a listener turn.");
            }
            run.groupInteractionsPassed=true;
            Require(run.queuePeak>=3,"Three physically present customers never formed a queue.");
            Require(run.requested.Count==3&&run.joined.Count==3&&run.joined.Distinct().Count()==3,"Each request must produce exactly one physical queue admission.");
            Require(run.joined.SequenceEqual(run.started)&&run.joined.SequenceEqual(run.ended)&&run.joined.SequenceEqual(run.approached),"Reception did not follow physical-arrival FIFO order.");
            Require(run.requestDidNotReservePassed&&run.laterRequestArrivedFirstPassed&&run.joined[0]==participants[1].name&&run.started[0]==participants[1].name,"A later nearby request did not arrive and receive service before the distant first request.");
            Require(run.actors.All(a=>a.admissionTime>a.requestTime&&a.approachTravel>1f&&a.maximumApproachRemainingPath>1f&&a.admissionSlotIndex>=0),"Physical arrivals were not demonstrated by real navigation and recorded path travel.");
            Require(run.actors.Where(a=>a.admissionSlotIndex>0).All(a=>a.queueAdvances>=a.admissionSlotIndex),"Queued customers did not advance through the compacted line to the service position.");
            run.compactQueuePassed=true;
            run.fifoPassed=true;
            Require(run.actors.All(a=>a.walkingObserved&&a.talkingObserved&&a.conversations==1&&a.seatedAgain),"A requested passenger did not complete exactly one full conversation/return cycle.");
            Require(participants.All(p=>!p.luggage),"A passenger still references luggage after the scenario.");
            Require(participants[0].CurrentSeat!=conflictSeat&&conflictSeat.Occupant==blocker,"Passenger returned to the occupied original seat instead of choosing a spare.");
            run.alternativeSeatPassed=true;
            Require(world.informationDesk.QueueCount==0&&!world.informationDesk.ActiveCustomer,"Information queue did not empty after the three conversations.");
            var scene=SceneManager.GetActiveScene();
            Require(Components<Transform>(scene).Length==run.baselineObjects,"Social runtime created or removed scene objects.");
            Require(Components<Camera>(scene).Count(c=>c.isActiveAndEnabled)==run.baselineCameras&&Components<Light>(scene).Count(l=>l.isActiveAndEnabled)==run.baselineLights,"Social runtime changed the station's cameras or lighting.");
            Require(!Components<MonoBehaviour>(scene).Any(m=>m&&m.GetType().FullName=="StationFlow.SyntheticTraffic.DemoController"),"Junction Lab started inside TrainStation.");
        }

        static void OnGroupSpeakerStarted(StationConversationMember member)=>RecordGroupEvent(member,true);
        static void OnGroupSpeakerEnded(StationConversationMember member)=>RecordGroupEvent(member,false);
        static void RecordGroupEvent(StationConversationMember member,bool started)
        {
            var run=Load();if(run==null||run.phase!="scenario")return;
            var result=member&&member.group?run.conversationGroups.FirstOrDefault(g=>g.name==member.group.name):null;
            if(result==null||!result.members.Any(m=>m.name==member.name))run.errors.Add("Unexpected standing conversation event.");
            else (started?result.speakerStarts:result.speakerEnds).Add(member.name);
            Save(run);
        }

        static void OnJoined(StationPassenger passenger)=>RecordEvent(passenger,"joined");
        static void OnApproached(StationPassenger passenger)=>RecordEvent(passenger,"approached");
        static void OnStarted(StationPassenger passenger)=>RecordEvent(passenger,"started");
        static void OnEnded(StationPassenger passenger)=>RecordEvent(passenger,"ended");
        static void RecordEvent(StationPassenger passenger,string kind)
        {
            var run=Load();if(run==null)return;
            if(!passenger||!world||!run.requested.Contains(passenger.name))run.errors.Add("Unexpected reception event: "+kind);
            else
            {
                var desk=world.informationDesk;var result=run.actors.Single(a=>a.name==passenger.name);float time=Time.time-run.simulationStarted;
                if(kind=="joined")
                {
                    if(run.joined.Contains(passenger.name))run.errors.Add("A passenger was admitted to the same queue more than once.");
                    result.admissionTime=time;result.admissionPosition=passenger.transform.position;result.admissionSlotIndex=desk.GetQueueIndex(passenger);
                    result.previousQueueIndex=result.admissionSlotIndex;result.queueIndexChangedAt=Time.time;
                    if(desk.TryGetQueueSlot(passenger,out var slot))
                    {
                        result.admissionSlotDistance=FlatDistance(passenger.transform.position,slot.position);
                        if(result.admissionSlotDistance>desk.arrivalTolerance+.001f||Mathf.Abs(passenger.transform.position.y-slot.position.y)>1.2f)
                            run.errors.Add("A distant passenger was admitted without physically reaching the queue tail.");
                    }
                    else run.errors.Add("Queue admission did not assign a real authored position.");
                    if(result.approachPositionRecorded)result.approachTravel+=FlatDistance(result.previousApproachPosition,passenger.transform.position);
                    run.joined.Add(passenger.name);
                }
                else
                {
                    if(kind!="ended"&&!desk.IsWithinServiceArea(passenger.transform.position))run.errors.Add("Reception event fired outside the front service area.");
                    if(kind=="started")result.conversationStartTime=time;
                    if(kind=="ended")result.conversationEndTime=time;
                    (kind=="approached"?run.approached:kind=="started"?run.started:run.ended).Add(passenger.name);
                }
            }
            Save(run);
        }

        static void ModeChanged(PlayModeStateChange state)
        {
            var run=Load();if(run==null)return;
            if(state==PlayModeStateChange.EnteredPlayMode){run.phase="initializing";run.checkAfterFrame=Time.frameCount+8;}
            else if(state==PlayModeStateChange.ExitingPlayMode&&run.phase!="exiting")
            {run.abort=true;run.errors.Add("Play was stopped before the social verification completed.");run.phase="exiting";}
            else if(state==PlayModeStateChange.EnteredEditMode)run.phase="restoring";
            Save(run);
        }
        static void Log(string message,string trace,LogType type)
        {
            if(Load()==null||(type!=LogType.Error&&type!=LogType.Exception&&type!=LogType.Assert))return;
            var previous=SessionState.GetString(LogKey,"");if(previous.Length<16000)SessionState.SetString(LogKey,previous+message+"\n"+trace+"\n");
        }
        static Run Load(){var json=SessionState.GetString(StateKey,"");return string.IsNullOrEmpty(json)?null:JsonUtility.FromJson<Run>(json);}
        static void Save(Run run)=>SessionState.SetString(StateKey,JsonUtility.ToJson(run));
        static string DescribeActors()=>world?string.Join("; ",world.passengers.Select(p=>p.name+":"+p.CurrentState)):"No live world";
        static void Finish(Run run)
        {
            if(run.capturedSettings)
            {
                try
                {
                    Time.timeScale=run.originalTimeScale;Application.runInBackground=run.originalRunInBackground;Application.targetFrameRate=run.originalTargetFrameRate;QualitySettings.vSyncCount=run.originalVSyncCount;
                    EditorSceneManager.playModeStartScene=string.IsNullOrEmpty(run.originalPlayScene)?null:AssetDatabase.LoadAssetAtPath<SceneAsset>(run.originalPlayScene);
                    EditorSettings.enterPlayModeOptions=(EnterPlayModeOptions)run.originalOptions;EditorSettings.enterPlayModeOptionsEnabled=run.originalOptionsEnabled;EditorApplication.isPaused=run.originalPaused;
                    run.restoredSettings=true;CleanScenes();
                    EditorSceneManager.RestoreSceneManagerSetup(run.scenes.Select(s=>new SceneSetup{path=s.path,isLoaded=s.isLoaded,isActive=s.isActive}).ToArray());run.restoredScenes=true;
                }
                catch(Exception error){run.errors.Add("Restoration failed: "+error);}
            }
            var log=SessionState.GetString(LogKey,"");if(!string.IsNullOrEmpty(log))run.errors.Add("Unity errors:\n"+log);
            run.success=!run.abort&&run.errors.Count==0&&run.restoredSettings&&run.restoredScenes&&run.frontGatePassed&&run.fifoPassed&&run.alternativeSeatPassed&&run.atomicSeatPassed&&run.requestDidNotReservePassed&&run.laterRequestArrivedFirstPassed&&run.compactQueuePassed&&run.locomotion.passed&&run.groupInteractionsPassed;
            run.phase="complete";run.completedUtc=DateTime.UtcNow.ToString("O");SessionState.EraseString(StateKey);SessionState.EraseString(LogKey);
            Write(ReportPath,run);world=null;participants=null;blocker=null;conflictSeat=null;workingHands.Clear();workingTools.Clear();workingStarted.Clear();checkedWorkingSkin.Clear();
            Debug.Log("Station social Play verification "+(run.success?"PASSED":"FAILED")+". See "+ReportPath);
        }
        static void CleanScenes(){for(int i=0;i<SceneManager.sceneCount;i++)Require(!SceneManager.GetSceneAt(i).isDirty,"Save or revert unsaved scenes before checking. Nothing is automatically saved/discarded.");}
        static bool HasMixedCrowd() => Components<StationSocialWorld>(SceneManager.GetActiveScene()).Any(w => w.shopDesks != null && w.shopDesks.Length > 0);
        static T[] Components<T>(Scene scene)where T:Component=>scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<T>(true)).ToArray();
        static float FlatDistance(Vector3 a,Vector3 b){a.y=b.y=0;return Vector3.Distance(a,b);}
        static float FacingAngle(Transform actor,Vector3 target)
        {
            var direction=target-actor.position;direction.y=0f;var forward=actor.forward;forward.y=0f;
            return direction.sqrMagnitude<.0001f?0f:Vector3.Angle(forward,direction);
        }
        static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
        static bool Finite(Vector3 value)=>!float.IsNaN(value.x)&&!float.IsInfinity(value.x)&&!float.IsNaN(value.y)&&!float.IsInfinity(value.y)&&!float.IsNaN(value.z)&&!float.IsInfinity(value.z);
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static void Write(string path,object value){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,JsonUtility.ToJson(value,true));}
    }
}
#endif
