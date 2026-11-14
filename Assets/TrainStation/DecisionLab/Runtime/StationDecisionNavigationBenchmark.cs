#pragma warning disable CS0649
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using StationFlow.TrainStation.Social;
using UnityEngine;
using UnityEngine.AI;
namespace StationFlow.TrainStation.DecisionLab.Diagnostics
{
    /// <summary>Explicit Player experiment; observation never steers production travellers.</summary>
    [DefaultExecutionOrder(2500)]
    public sealed class StationDecisionNavigationBenchmark : MonoBehaviour
    {
        const string Flag="--station-navigation-benchmark";
        readonly Stopwatch clock=new Stopwatch();
        readonly List<string> errors=new List<string>();
        readonly Dictionary<string,Watch> watches=new Dictionary<string,Watch>(StringComparer.Ordinal);
        readonly List<Snapshot> snapshots=new List<Snapshot>();
        readonly List<Incident> incidents=new List<Incident>();
        readonly List<Fixture> fixtures=new List<Fixture>();
        readonly List<Visible> visible=new List<Visible>(160);
        readonly List<double> frameMs=new List<double>();
        readonly List<DecisionPathPoint> fallbackCorners=new List<DecisionPathPoint>(32);
        Config config; Report report; StationDecisionWorld world;
        double beganWall,previousWall;
        float beganSim,previousSample,nextSample,nextSnapshot,nextWaveAt;
        int beganFrame,wave;
        bool started,finished;
        [Serializable] public sealed class Config { public string mode="daily",gait="enhanced",date="2026-10-28",output; public int seed=20261028; public float horizon=2f,seconds=120f; }
        [Serializable] sealed class Report
        {
            public string schema="stationflow.navigation_benchmark.v2",startedUtc,completedUtc,scene,applicationVersion,buildGuid,unityVersion,timetableSha256,arrivalPlanSha256,fixtureFirstWaveLayoutSha256,tracePath,processor,gpu;
            public Config request;
            public bool success,fixedSeedVerified,initiallyPaused;
            public float initialNativeHorizon,observedHorizon,horizonBeforeWorldDisable,horizonAfterWorldDisable,startSimulationSeconds,endSimulationSeconds; public bool horizonRestoreVerified; public int enhancedMovementSamples,legacyMovementSamples;
            public double realSeconds,gameFps,frameMsMedian,frameMsP95,frameMsMax,groupHoldActorSeconds,otherExcludedNativeTravelActorSeconds;
            public int unityFrames,width,height,targetFps=60,focusedFrames,unfocusedFrames,generatedJourneys,scheduledServices,enteredJourneys,boardedJourneys,exitedJourneys,failedJourneys;
            public int fixtureCreated,fixtureArrived,fixtureUnfinished,wavesStarted,samples,knownStartupNavMeshErrors,navigationIssueChanges,noProgress25sIssueChanges,largeSampleGaps; public float maxSampleGapSeconds; public int nonfiniteNativeDistances,distanceFallbackResolved,distanceFallbackRejected;
            public Metric ordinaryTravel=new Metric(),queueTravel=new Metric(),fountainTravel=new Metric(),closeNeighborTravel=new Metric(),fixtureTravel=new Metric();
            public ActorResult[] actors; public Incident[] incidents; public FixtureResult[] fixtures; public string[] errors;
            public string[] definitions={
                "Samples approximately10Hz simulation time. Durations are actor-seconds. Trace1Hz is serialized after measurement. Gaps>0.25s reported; interval is attributed to current sample and continuous-stall precision is bounded by observed gaps.",
                "success=true means the observation completed without harness errors; it does not mean jam-free movement. Inspect unfinished fixture agents, stalls and25s watchdog incidents.",
                "closeNeighborTravel/fountainTravel are duration-only subsets: episode/max-duration counters in these subsets are unsupported and remainzero. Episode metrics belong to ordinaryTravel,queueTravel,fixtureTravel and per-actor movement.",
                "Movement: active native agent, onNavMesh, !pathPending, hasPath, completepath, !isStopped, !isOnOffMeshLink, remainingDistance>max(0.5,stoppingDistance+0.25). Daily also requires LocomotionActive and a walking/queue-approach state.",
                "Queue travel is measured separately. Waiting, seated/using/service-held and group-cohesion holds excluded from ordinary travel.",
                "v2: nonfinite native remainingDistance on a valid complete path is resolved read-only through StationDecisionNavigationProgress/DecisionPathDistance and the current native corridor corners. No new route or straight-line substitute is calculated; attempt/success/rejection counts are disclosed.",
                "Slow speed<50% agent.speed. Near-zero stall speed<0.08m/s continuously with movement intent. Episodes counted crossing2s/5s; recovery requires renewed velocity before losing intent. Interruption is not recovery.",
                "Low-displacement20s: continuous movement intent with neither0.5m planar displacement from progress anchor nor0.5m remaining-path reduction. Goal change>1m or loss of intent ends window.",
                "Close neighbor:center distance<1.2m,height difference<1m. Overlap:center distance<radii sum-0.05m. NavMesh disk geometry is not proof of physical collision/pushing.",
                "Fountain ordinary travel at radius5..10m around(0,0,0). Known fountain radius5m.",
                "NavigationIssue transitions inspected every rendered frame to expose25s watchdog goal changes. Repeated identical issue without an intervening change can undercount.",
                "Crossing:48native agents, up to2waves, cardinal opposite endpoints around saved hall fountain, intended radialdistance8..9.8m. All48routes preflighted before spawning. Setup deterministically picks separated legal endpoints within3.25m of grid slots; actualpositions saved. No runtime intermediate waypoints or steering/speed clamps. Arrivals disabled; wave2 starts2s after wave1 fully clears. Unfinished agents retained.",
                "Fixed seed/date and timetable/arrival hashes prove identical generated inputs, not deterministic realtime native trajectories. One run per cell is exploratory.",
                "No video/data collection. FPS includes normal main camera/UI and diagnostic sampling, excludes result serialization. Hidden/unfocused Player differs from foreground play.",
                "Daily cadence/stride samples describe animation. Primitive crossing agents isolate avoidance and do not establish animation naturalness."
            };
        }
        [Serializable] sealed class Metric
        {
            public int samples,stallEpisodes2s,stallEpisodes5s,recoveredStalls2s,interruptedStalls2s,lowDisplacementEpisodes20s;
            public double actorSeconds,slowActorSeconds,nearZeroActorSeconds,overlapActorSeconds,speedRatioIntegral,speedIntegral,slowWalkActorSeconds,slowWalkPlaybackIntegral,slowWalkStrideIntegral,maxNearZeroSeconds,maxLowDisplacementSeconds,distanceMetres;
            public double slowPercent,nearZeroPercent,meanSpeedRatio,meanSpeed,meanSlowWalkPlayback,meanSlowWalkStride;
            public void Ratios(){if(actorSeconds>0){slowPercent=100*slowActorSeconds/actorSeconds;nearZeroPercent=100*nearZeroActorSeconds/actorSeconds;meanSpeedRatio=speedRatioIntegral/actorSeconds;meanSpeed=speedIntegral/actorSeconds;}if(slowWalkActorSeconds>0){meanSlowWalkPlayback=slowWalkPlaybackIntegral/slowWalkActorSeconds;meanSlowWalkStride=slowWalkStrideIntegral/slowWalkActorSeconds;}}
        }
        [Serializable] sealed class ActorResult { public string id,lastState,lastIssue; public Metric movement=new Metric(); public float firstSeen,lastSeen,distanceMetres; public int decisionCount,completedActivities; public bool endedDuringStall,endedDuringLowDisplacement; }
        [Serializable] sealed class Incident { public string id,kind,state,detail; public float simulationSeconds,duration; public Vector3 position,destination; }
        [Serializable] sealed class Snapshot { public string id,state,metricGroup; public float time,speed,requestedSpeed,remaining,nearest,cadence,stride; public Vector3 position,destination; public bool intent,walkPose,nativeRemainingFinite; }
        [Serializable] sealed class Trace { public string schema="stationflow.navigation_trace.v2"; public Snapshot[] samples; }
        [Serializable] sealed class FixtureResult { public string id; public Vector3 requestedStart,start,goal; public float speed,radius,initialPathLength,began,arrived=-1f; public bool complete; }
        [Serializable] sealed class TimetableFingerprint { public string date; public double startClockSeconds; public RailOperations.StationRailService[] services; }
        [Serializable] sealed class ArrivalFingerprint { public DecisionArrivalPlan[] journeys; }
        [Serializable] sealed class FixtureLayout { public FixtureGeometry[] agents; }
        [Serializable] sealed class FixtureGeometry { public string id; public Vector3 requestedStart,start,goal; public float speed,radius,initialPathLength; }
        sealed class Fixture { public NavMeshAgent agent; public FixtureResult result; }
        sealed class Watch { public ActorResult result; public bool lastIntent,stall2,stall5,low20; public float nearZero,lowProgress,progressRemaining; public Vector3 lastPosition,destination,progressPosition; public string group,issue=""; }
        struct Visible { public string id,state,group; public NavMeshAgent agent; public StationActorMotion motion; public bool intent; public float remaining; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            var args=Environment.GetCommandLineArgs();if(Application.isEditor||!args.Contains(Flag))return;
            Config settings=null;
            try
            {
                Require(!args.Contains("--station-capture-benchmark")&&!args.Contains("--station-collection-smoke"),"Do not combine diagnostic launch hooks.");
                settings=Parse(args);
                var scenario=Resources.Load<StationDecisionDayScenario>("StationDecisionDayScenario");Require(scenario,"Missing Daily scenario.");
                scenario.randomizeOnLaunch=true;scenario.useFixedRuntimeSeed=true;scenario.fixedRuntimeSeed=settings.seed;scenario.replayDateIso=settings.date;scenario.defaultTimeScale=1f;
                Application.runInBackground=true;Application.targetFrameRate=60;QualitySettings.vSyncCount=0;
                var host=new GameObject("Explicit native-navigation benchmark");DontDestroyOnLoad(host);
                host.AddComponent<StationDecisionNavigationBenchmark>().Initialize(settings);
            }
            catch(Exception error){UnityEngine.Debug.LogException(error);Application.Quit(2);}
        }
        static Config Parse(string[] args)
        {
            var c=new Config();
            string Read(string key,string fallback){int i=Array.IndexOf(args,key);return i<0?fallback:i+1<args.Length?args[i+1]:throw new ArgumentException("Missing value:"+key);}
            c.mode=Read("--nav-mode",c.mode);c.gait=Read("--nav-gait",c.gait);c.date=Read("--nav-date",c.date);
            c.seed=int.Parse(Read("--nav-seed","20261028"),CultureInfo.InvariantCulture);
            c.horizon=float.Parse(Read("--nav-horizon","2"),CultureInfo.InvariantCulture);c.seconds=float.Parse(Read("--nav-seconds","120"),CultureInfo.InvariantCulture);
            c.output=Path.GetFullPath(Read("--nav-output",Path.Combine(StationDecisionCaptureSession.StorageRoot,"NavigationBenchmarks","navigation-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")+".json")));
            Require(c.mode=="daily"||c.mode=="crossing","Mode must be daily or crossing.");Require(c.gait=="enhanced"||c.gait=="legacy","Invalid gait.");
            Require(c.horizon==2||c.horizon==3||c.horizon==4,"Horizon must be2,3or4seconds.");Require(c.seconds>=30&&c.seconds<=600,"Duration must be30..600seconds.");
            Require(DateTime.TryParseExact(c.date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _),"Invalid date.");return c;
        }
        void Initialize(Config settings)
        {
            config=settings;clock.Start();
            report=new Report{request=config,startedUtc=DateTime.UtcNow.ToString("O"),applicationVersion=Application.version,buildGuid=Application.buildGUID,unityVersion=Application.unityVersion,
                processor=SystemInfo.processorType,gpu=SystemInfo.graphicsDeviceName,width=Screen.width,height=Screen.height,initialNativeHorizon=NavMesh.avoidancePredictionTime};
            Application.logMessageReceived+=OnLog;
        }
        void LateUpdate()
        {
            if(config==null||finished)return;
            try
            {
                Require(clock.Elapsed.TotalSeconds<config.seconds*3d+90,"Navigation benchmark wall-time limit.");
                if(!started){TryStart();return;}
                Require(world&&!world.Paused&&!world.rail.IsPaused&&Mathf.Abs(Time.timeScale-1)<.001f,"Unexpected pause/speed change.");
                Require(Mathf.Abs(NavMesh.avoidancePredictionTime-config.horizon)<.001f,"Prediction horizon changed.");
                double wall=clock.Elapsed.TotalSeconds;frameMs.Add((wall-previousWall)*1000);previousWall=wall;
                if(Application.isFocused)++report.focusedFrames;else ++report.unfocusedFrames;
                float now=world.ElapsedSeconds;
                if(config.mode=="crossing")UpdateFixture(now);else InspectIssues(now);
                if(now>=nextSample){Sample(now,Mathf.Max(0,now-previousSample));previousSample=now;nextSample=now+.1f;}
                if(now-beganSim>=config.seconds)Finish(true);
            }catch(Exception error){errors.Add(error.ToString());Finish(false);}
        }
        void TryStart()
        {
            if(!world)world=FindObjectsByType<StationDecisionWorld>(FindObjectsSortMode.None).FirstOrDefault(w=>w.IsDaily);
            if(!world||!world.rail||!world.rail.IsReady||!world.arrivalController||!world.arrivalController.IsReady||!world.CaptureSession)return;
            report.fixedSeedVerified=world.RunSession!=null&&world.RunSession.fixedSeedReplay&&world.RunSession.seed==config.seed&&world.RunSession.operatingDateIso==config.date;
            Require(report.fixedSeedVerified,"Seed/date override did not bind.");
            report.initiallyPaused=world.Paused&&world.rail.IsPaused&&Mathf.Abs(world.ElapsedSeconds)<.001f;Require(report.initiallyPaused,"World advanced before initialization.");
            report.scene=world.gameObject.scene.path;
            report.timetableSha256=Hash(JsonUtility.ToJson(new TimetableFingerprint{date=config.date,startClockSeconds=world.rail.schedule.StartClockSeconds,services=world.rail.schedule.services}));
            report.arrivalPlanSha256=Hash(JsonUtility.ToJson(new ArrivalFingerprint{journeys=world.arrivalController.GeneratedPlan.ToArray()}));
            report.generatedJourneys=world.arrivalController.GeneratedPlan.Count;report.scheduledServices=world.rail.schedule.services.Length;
            foreach(var p in world.participants)if(p&&p.motion)p.motion.ConfigureLocomotionPresentation(config.gait=="enhanced");
            NavMesh.avoidancePredictionTime=config.horizon;report.observedHorizon=NavMesh.avoidancePredictionTime;
            var panel=world.GetComponent<StationDecisionCollectionPanel>();if(panel)panel.Hide();
            var views=FindObjectsByType<TrainStationViewControls>(FindObjectsSortMode.None).FirstOrDefault(v=>v.gameObject.scene==world.gameObject.scene);if(views)views.SetHall();
            if(config.mode=="crossing"){world.arrivalController.enabled=false;foreach(var p in world.participants)if(p)p.gameObject.SetActive(false);}
            world.SetSimulationSpeed(1);world.BeginObservation();Application.targetFrameRate=60;QualitySettings.vSyncCount=0;
            beganSim=previousSample=nextSample=nextSnapshot=world.ElapsedSeconds;beganWall=previousWall=clock.Elapsed.TotalSeconds;beganFrame=Time.frameCount;
            report.startSimulationSeconds=beganSim;if(config.mode=="crossing")CreateWave(beganSim);started=true;
        }
        void InspectIssues(float now)
        {
            foreach(var p in world.participants)
            {
                if(!p||!p.HasEntered||!p.gameObject.activeInHierarchy)continue;
                var w=GetWatch(p.JourneyId,p.transform.position,now);string issue=p.NavigationIssue??"";
                if(issue!=w.issue){w.issue=issue;w.result.lastIssue=issue;if(issue.Length>0){++report.navigationIssueChanges;if(issue.IndexOf("no progress",StringComparison.OrdinalIgnoreCase)>=0)++report.noProgress25sIssueChanges;
                    incidents.Add(new Incident{id=p.JourneyId,kind="navigation_issue",state=p.ExecutionState,detail=issue,simulationSeconds=now,position=p.transform.position});}}
                w.result.decisionCount=p.DecisionCount;w.result.completedActivities=p.CompletedActivities;
            }
        }
        void Sample(float now,float dt)
        {
            ++report.samples;report.maxSampleGapSeconds=Mathf.Max(report.maxSampleGapSeconds,dt);if(dt>.25f)++report.largeSampleGaps;visible.Clear();
            if(config.mode=="crossing")
            {
                foreach(var f in fixtures)if(f.agent&&f.agent.enabled&&f.agent.isOnNavMesh){bool eligible=Eligible(f.agent,out float remaining);visible.Add(new Visible{id=f.result.id,state="NativeCrossing",group="fixture",agent=f.agent,intent=eligible,remaining=remaining});}
            }
            else foreach(var p in world.participants)
            {
                if(!p||!p.HasEntered||p.Finished||!p.gameObject.activeInHierarchy||!p.motion)continue;
                var a=p.motion.agent;if(!a||!a.enabled||!a.isOnNavMesh)continue;
                string state=p.ExecutionState??"";bool native=Eligible(a,out float remaining,p.motion);
                bool queue=state=="Service"||state.StartsWith("Gate",StringComparison.Ordinal)||state.StartsWith("Coach",StringComparison.Ordinal)||(state.StartsWith("WC",StringComparison.Ordinal)&&state!="WCClearing");
                bool ordinary=state=="Walking"||state=="ClearingCounter"||state=="WalkingToSeat"||state=="WCClearing"||state=="LeavingSeat";
                bool intent=native&&p.motion.LocomotionActive&&(queue||ordinary);
                if(state=="WaitingForGroup"||state=="Regrouping")report.groupHoldActorSeconds+=dt;
                if(native&&!intent)report.otherExcludedNativeTravelActorSeconds+=dt;
                visible.Add(new Visible{id=p.JourneyId,state=state,group=queue?"queue":"ordinary",agent=a,motion=p.motion,intent=intent,remaining=remaining});
            }
            bool trace=now>=nextSnapshot;if(trace)nextSnapshot=now+1;
            var present=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in visible)
            {
                present.Add(item.id);var a=item.agent;var pos=a.transform.position;var w=GetWatch(item.id,pos,now);
                w.result.lastSeen=now;w.result.lastState=item.state;
                float travelled=Planar(pos-w.lastPosition).magnitude;if(travelled<3)w.result.distanceMetres+=travelled;
                bool newGoal=w.lastIntent&&(a.destination-w.destination).sqrMagnitude>1;
                if(!item.intent||newGoal||w.group!=item.group)EndEpisode(w,now,item.state,"intent_or_goal_changed");
                float speed=Planar(a.velocity).magnitude,nearest=float.PositiveInfinity;bool overlap=false;
                foreach(var other in visible)
                {
                    if(other.id==item.id||Mathf.Abs(other.agent.transform.position.y-pos.y)>1)continue;
                    float distance=Planar(other.agent.transform.position-pos).magnitude;nearest=Mathf.Min(nearest,distance);
                    if(distance<a.radius+other.agent.radius-.05f)overlap=true;
                }
                if(item.intent)
                {
                    if(item.motion){bool enhanced=item.motion.EnhancedLocomotionPresentation;Require(enhanced==(config.gait=="enhanced"),"Requested gait is not actually available on moving avatar "+item.id);if(enhanced)++report.enhancedMovementSamples;else ++report.legacyMovementSamples;}
                    var metric=MetricFor(item.group);float ratio=a.speed>.001f?speed/a.speed:0;
                    float cadence=item.motion?item.motion.AppliedWalkPlaybackSpeed:0,stride=item.motion?item.motion.LocomotionStrideWeight:0;
                    bool slowWalk=item.motion&&item.motion.IsWalkPose&&ratio<.5f&&speed>=.08f;
                    Add(metric,dt,ratio,speed,cadence,stride,overlap,slowWalk,travelled);Add(w.result.movement,dt,ratio,speed,cadence,stride,overlap,slowWalk,travelled);
                    if(nearest<1.2f)Add(report.closeNeighborTravel,dt,ratio,speed,cadence,stride,overlap,slowWalk,travelled);
                    float radius=Planar(pos).magnitude;if(item.group=="ordinary"&&radius>=5&&radius<=10)Add(report.fountainTravel,dt,ratio,speed,cadence,stride,overlap,slowWalk,travelled);
                    if(!w.lastIntent||newGoal){w.progressPosition=pos;w.progressRemaining=item.remaining;w.nearZero=w.lowProgress=0;}
                    if(speed<.08f)
                    {
                        w.nearZero+=dt;metric.maxNearZeroSeconds=Math.Max(metric.maxNearZeroSeconds,w.nearZero);w.result.movement.maxNearZeroSeconds=Math.Max(w.result.movement.maxNearZeroSeconds,w.nearZero);
                        if(w.nearZero>=2&&!w.stall2){w.stall2=true;++metric.stallEpisodes2s;++w.result.movement.stallEpisodes2s;AddIncident(item,"stall_2s",now,w.nearZero);}
                        if(w.nearZero>=5&&!w.stall5){w.stall5=true;++metric.stallEpisodes5s;++w.result.movement.stallEpisodes5s;AddIncident(item,"stall_5s",now,w.nearZero);}
                    }
                    else
                    {
                        if(w.stall2){++metric.recoveredStalls2s;++w.result.movement.recoveredStalls2s;AddIncident(item,"stall_recovered",now,w.nearZero);}
                        w.nearZero=0;w.stall2=w.stall5=false;
                    }
                    if(Planar(pos-w.progressPosition).magnitude>=.5f||w.progressRemaining-item.remaining>=.5f){w.progressPosition=pos;w.progressRemaining=item.remaining;w.lowProgress=0;w.low20=false;}
                    else
                    {
                        w.lowProgress+=dt;metric.maxLowDisplacementSeconds=Math.Max(metric.maxLowDisplacementSeconds,w.lowProgress);w.result.movement.maxLowDisplacementSeconds=Math.Max(w.result.movement.maxLowDisplacementSeconds,w.lowProgress);
                        if(w.lowProgress>=20&&!w.low20){w.low20=true;++metric.lowDisplacementEpisodes20s;++w.result.movement.lowDisplacementEpisodes20s;AddIncident(item,"low_displacement_20s",now,w.lowProgress);}
                    }
                    w.destination=a.destination;
                }
                if(trace)snapshots.Add(new Snapshot{id=item.id,time=now,state=item.state,metricGroup=item.group,position=pos,destination=a.destination,speed=speed,requestedSpeed=a.speed,remaining=Finite(item.remaining)?item.remaining:-1,
                    nearest=Finite(nearest)?nearest:-1,intent=item.intent,nativeRemainingFinite=Finite(a.remainingDistance),walkPose=item.motion&&item.motion.IsWalkPose,cadence=item.motion?item.motion.AppliedWalkPlaybackSpeed:0,stride=item.motion?item.motion.LocomotionStrideWeight:0});
                w.lastIntent=item.intent;w.group=item.group;w.lastPosition=pos;
            }
            foreach(var pair in watches)if(!present.Contains(pair.Key)&&pair.Value.lastIntent)EndEpisode(pair.Value,now,pair.Value.result.lastState,"agent_left_observation");
        }
        Metric MetricFor(string group)=>group=="fixture"?report.fixtureTravel:group=="queue"?report.queueTravel:report.ordinaryTravel;
        bool Eligible(NavMeshAgent a,out float remaining,StationActorMotion motion=null)
        {
            remaining=0;if(!a||!a.enabled||!a.isOnNavMesh||a.isStopped||a.isOnOffMeshLink||a.pathPending||!a.hasPath||a.pathStatus!=NavMeshPathStatus.PathComplete)return false;
            remaining=a.remainingDistance;
            if(!Finite(remaining))
            {
                ++report.nonfiniteNativeDistances;
                bool resolved;
                if(motion)resolved=StationDecisionNavigationProgress.TryGetRemainingDistance(motion,out remaining);
                else
                {
                    fallbackCorners.Clear();
                    foreach(var corner in a.path.corners)fallbackCorners.Add(PathPoint(corner));
                    resolved=DecisionPathDistance.TryResolve(remaining,true,false,a.isPathStale,PathPoint(a.transform.position),PathPoint(a.nextPosition),PathPoint(a.destination),fallbackCorners,out double length)&&length<=float.MaxValue;
                    if(resolved)remaining=(float)length;
                }
                if(resolved&&Finite(remaining))++report.distanceFallbackResolved;
                else{++report.distanceFallbackRejected;return false;}
            }
            return remaining>Mathf.Max(.5f,a.stoppingDistance+.25f);
        }
        static DecisionPathPoint PathPoint(Vector3 p)=>new DecisionPathPoint(p.x,p.y,p.z);
        static void Add(Metric m,float dt,float ratio,float speed,float cadence,float stride,bool overlap,bool slowWalk,float distance)
        {
            ++m.samples;m.actorSeconds+=dt;if(ratio<.5f)m.slowActorSeconds+=dt;if(speed<.08f)m.nearZeroActorSeconds+=dt;if(overlap)m.overlapActorSeconds+=dt;
            m.speedRatioIntegral+=ratio*dt;m.speedIntegral+=speed*dt;m.distanceMetres+=distance;
            if(slowWalk){m.slowWalkActorSeconds+=dt;m.slowWalkPlaybackIntegral+=cadence*dt;m.slowWalkStrideIntegral+=stride*dt;}
        }
        Watch GetWatch(string id,Vector3 position,float now)
        {
            if(!watches.TryGetValue(id,out var w)){w=new Watch{result=new ActorResult{id=id,firstSeen=now,lastSeen=now},lastPosition=position};watches.Add(id,w);}return w;
        }
        void EndEpisode(Watch w,float now,string state,string reason)
        {
            if(w.stall2){++MetricFor(w.group).interruptedStalls2s;++w.result.movement.interruptedStalls2s;incidents.Add(new Incident{id=w.result.id,kind="stall_interrupted",detail=reason,simulationSeconds=now,duration=w.nearZero,state=state,position=w.lastPosition,destination=w.destination});}
            w.lastIntent=w.stall2=w.stall5=w.low20=false;w.nearZero=w.lowProgress=0;
        }
        void AddIncident(Visible item,string kind,float now,float duration)=>incidents.Add(new Incident{id=item.id,kind=kind,state=item.state,simulationSeconds=now,duration=duration,position=item.agent.transform.position,destination=item.agent.destination});
        /// <summary>Read-only saved-NavMesh preflight, callable by the existing Editor command bridge.</summary>
        public static string ValidateCrossingFixtureLayout(StationDecisionWorld target)
        {
            Require(target,"Fixture preflight requires the Daily world.");
            var prototype=target.participants.First(p=>p&&p.motion&&p.motion.agent).motion.agent;
            var filter=new NavMeshQueryFilter{agentTypeID=prototype.agentTypeID,areaMask=prototype.areaMask};
            return JsonUtility.ToJson(new FixtureLayout{agents=BuildFixtureLayout(filter)},true);
        }
        static FixtureGeometry[] BuildFixtureLayout(NavMeshQueryFilter filter)
        {
            var layout=new List<FixtureGeometry>(48);
            for(int direction=0;direction<4;++direction)
            for(int index=0;index<12;++index)
            {
                var axis=Quaternion.Euler(0,direction*90f,0)*Vector3.right;var across=Quaternion.Euler(0,90,0)*axis;
                float lane=(index%4-1.5f)*.90f,depth=index/4*.90f;
                Vector3 requested=axis*(8f+depth)+across*lane+Vector3.up*.02f,desired=-axis*(8f+depth)+across*lane+Vector3.up*.02f;
                Vector3 start=ChooseFixturePoint(requested,filter,false,layout),goal=ChooseFixturePoint(desired,filter,true,layout);
                var path=new NavMeshPath();Require(NavMesh.CalculatePath(start,goal,filter,path)&&path.status==NavMeshPathStatus.PathComplete,"Fixture route incomplete.");
                float length=0;for(int p=1;p<path.corners.Length;++p)length+=Vector3.Distance(path.corners[p-1],path.corners[p]);
                layout.Add(new FixtureGeometry{id="direction-"+direction+"/agent-"+index,requestedStart=requested,start=start,goal=goal,speed=1.25f+(index%3)*.1f,radius=.32f,initialPathLength=length});
            }
            Require(layout.Count==48,"Expected48 validated native fixture routes.");return layout.ToArray();
        }
        static Vector3 ChooseFixturePoint(Vector3 requested,NavMeshQueryFilter filter,bool destination,List<FixtureGeometry> layout)
        {
            // Setup only: deterministic candidates around the intended slot, never runtime steering.
            for(int ring=0;ring<=10;++ring)for(int direction=0;direction<(ring==0?1:16);++direction)
            {
                float angle=direction*Mathf.PI/8f;
                var candidate=requested+new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle))*(ring*.3f);
                if(!NavMesh.SamplePosition(candidate,out var hit,.25f,filter)||Planar(hit.position).magnitude<6.5f)continue;
                bool separated=true;
                foreach(var existing in layout)if(Planar((destination?existing.goal:existing.start)-hit.position).magnitude<.75f){separated=false;break;}
                if(separated)return hit.position;
            }
            throw new InvalidOperationException("No separated native NavMesh fixture slot near "+requested);
        }
        void CreateWave(float now)
        {
            ++wave;report.wavesStarted=wave;
            var prototype=world.participants.First(p=>p&&p.motion&&p.motion.agent).motion.agent;
            var filter=new NavMeshQueryFilter{agentTypeID=prototype.agentTypeID,areaMask=prototype.areaMask};
            // Validate the entire layout before instantiating a single runtime agent.
            var layout=BuildFixtureLayout(filter);
            foreach(var point in layout)
            {
                var go=new GameObject("Native crossing "+wave+"/"+point.id);go.SetActive(false);go.transform.position=point.start;
                var shape=GameObject.CreatePrimitive(PrimitiveType.Capsule);shape.transform.SetParent(go.transform,false);shape.transform.localPosition=Vector3.up*.9f;shape.transform.localScale=new Vector3(.64f,.9f,.64f);
                var collider=shape.GetComponent<Collider>();collider.enabled=false;Destroy(collider);
                var a=go.AddComponent<NavMeshAgent>();a.agentTypeID=prototype.agentTypeID;a.areaMask=prototype.areaMask;a.radius=point.radius;a.height=1.8f;a.baseOffset=0;
                a.speed=point.speed;a.acceleration=5;a.angularSpeed=240;a.stoppingDistance=.12f;a.avoidancePriority=50;a.obstacleAvoidanceType=ObstacleAvoidanceType.HighQualityObstacleAvoidance;a.autoBraking=true;a.autoRepath=true;
                go.SetActive(true);Require(a.isOnNavMesh,"Fixture agent did not bind.");Require(a.SetDestination(point.goal),"Fixture destination rejected.");
                fixtures.Add(new Fixture{agent=a,result=new FixtureResult{id="wave-"+wave+"/"+point.id,requestedStart=point.requestedStart,start=point.start,goal=point.goal,speed=a.speed,radius=a.radius,initialPathLength=point.initialPathLength,began=now}});
                ++report.fixtureCreated;
            }
            nextWaveAt=float.PositiveInfinity;
        }
        void UpdateFixture(float now)
        {
            foreach(var f in fixtures)
            {
                if(f.result.complete||!f.agent||!f.agent.enabled)continue;var a=f.agent;
                if(!a.pathPending&&((a.hasPath&&a.pathStatus==NavMeshPathStatus.PathComplete&&Finite(a.remainingDistance)&&a.remainingDistance<=.22f)||(!a.hasPath&&Planar(a.transform.position-f.result.goal).magnitude<=.25f)))
                {f.result.complete=true;f.result.arrived=now;++report.fixtureArrived;a.enabled=false;a.gameObject.SetActive(false);}
            }
            if(wave<2&&fixtures.Count>0&&fixtures.All(f=>f.result.complete)){if(float.IsPositiveInfinity(nextWaveAt))nextWaveAt=now+2;if(now>=nextWaveAt)CreateWave(now);}
        }
        void Finish(bool success)
        {
            if(finished)return;finished=true;
            try
            {
                report.completedUtc=DateTime.UtcNow.ToString("O");report.endSimulationSeconds=world?world.ElapsedSeconds:0;
                report.realSeconds=started?clock.Elapsed.TotalSeconds-beganWall:0;report.unityFrames=started?Time.frameCount-beganFrame:0;
                report.gameFps=report.realSeconds>0?report.unityFrames/report.realSeconds:0;frameMs.Sort();
                if(frameMs.Count>0){report.frameMsMedian=Percentile(frameMs,.5);report.frameMsP95=Percentile(frameMs,.95);report.frameMsMax=frameMs[frameMs.Count-1];}
                if(world&&world.arrivalController){report.enteredJourneys=world.arrivalController.EnteredCount;report.boardedJourneys=world.arrivalController.BoardedCount;report.exitedJourneys=world.arrivalController.ExitedCount;report.failedJourneys=world.arrivalController.UnservedCount;}
                report.fixtureUnfinished=report.fixtureCreated-report.fixtureArrived;
                report.fixtureFirstWaveLayoutSha256=Hash(JsonUtility.ToJson(new FixtureLayout{agents=fixtures.Where(f=>f.result.id.StartsWith("wave-1/",StringComparison.Ordinal)).Select(f=>new FixtureGeometry{id=f.result.id,requestedStart=f.result.requestedStart,start=f.result.start,goal=f.result.goal,speed=f.result.speed,radius=f.result.radius,initialPathLength=f.result.initialPathLength}).ToArray()}));
                foreach(var w in watches.Values){w.result.movement.Ratios();w.result.endedDuringStall=w.stall2;w.result.endedDuringLowDisplacement=w.low20;}
                foreach(var m in new[]{report.ordinaryTravel,report.queueTravel,report.fountainTravel,report.closeNeighborTravel,report.fixtureTravel})m.Ratios();
                report.actors=watches.Values.Select(w=>w.result).ToArray();report.fixtures=fixtures.Select(f=>f.result).ToArray();report.incidents=incidents.ToArray();
                if(world){report.horizonBeforeWorldDisable=NavMesh.avoidancePredictionTime;world.enabled=false;report.horizonAfterWorldDisable=NavMesh.avoidancePredictionTime;report.horizonRestoreVerified=Mathf.Abs(report.horizonAfterWorldDisable-report.initialNativeHorizon)<.001f;if(!report.horizonRestoreVerified)errors.Add("World did not restore original native prediction horizon.");}
                report.errors=errors.ToArray();report.success=success&&errors.Count==0;
                Directory.CreateDirectory(Path.GetDirectoryName(config.output));report.tracePath=Path.ChangeExtension(config.output,".trace.json");
                File.WriteAllText(report.tracePath,JsonUtility.ToJson(new Trace{samples=snapshots.ToArray()}),new UTF8Encoding(false));
                File.WriteAllText(config.output,JsonUtility.ToJson(report,true),new UTF8Encoding(false));
            }catch(Exception error){UnityEngine.Debug.LogException(error);success=false;}
            Application.Quit(success&&report.success?0:2);
        }
        void OnLog(string message,string stack,LogType type)
        {
            if(type!=LogType.Error&&type!=LogType.Exception&&type!=LogType.Assert)return;
            if(!started&&message.StartsWith("Failed to create agent because there is no valid NavMesh",StringComparison.Ordinal)){++report.knownStartupNavMeshErrors;return;}
            if(errors.Count<50)errors.Add(message+"\n"+stack);
        }
        void OnDestroy(){Application.logMessageReceived-=OnLog;}
        static Vector3 Planar(Vector3 v){v.y=0;return v;}
        static bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        static double Percentile(List<double> sorted,double p)=>sorted[Mathf.Clamp((int)Math.Ceiling((sorted.Count-1)*p),0,sorted.Count-1)];
        static string Hash(string value){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant();}
    }
}
