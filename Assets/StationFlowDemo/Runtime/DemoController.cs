using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StationFlow.SyntheticTraffic
{
    public sealed partial class DemoController : MonoBehaviour
    {
        public int Seed=42,Pedestrians=96,Vehicles=24,CaptureFps=10;
        public TrafficSimulation Simulation {get;private set;}
        public CameraRig Rig {get;private set;}
        WorldBuilder world;
        UrbanRenderQuality quality;
        readonly List<ActorVisual> actors=new List<ActorVisual>();
        readonly List<LabelTarget> labels=new List<LabelTarget>();
        Transform actorRoot;
        bool paused,showUI=true,automated,showSensors=true;
        int view,frame,framesRequested,selected=-1,lightingPreset;
        int selectedTrackId;
        float followOrbit,followZoom=1;
        float accumulator,previewClock,speed=1,orbitYaw=-45,orbitPitch=47,orbitDistance=35,smoothedFps=60;
        Vector3 orbitTarget=new Vector3(0,1,0);
        string captureFolder="",status="Explore the quarter. Select a person or vehicle to inspect.";
        const float StepSize=1f/60f;
        double captureStartTime;
        int captureStartRed,captureStartPedRed,captureStartCollisions,captureStartTrips,captureStartTurns;
        int captureSeed,capturePeople,captureVehicles;
        // JunctionLab saves this component in its scene. Starting another scene must
        // never opt it into the traffic demo or replace its cameras and lighting.
        void Start()
        {
            try
            {
                foreach(Camera c in FindObjectsByType<Camera>())c.enabled=false;
                foreach(AudioListener l in FindObjectsByType<AudioListener>())l.enabled=false;
                foreach(Light l in FindObjectsByType<Light>())l.enabled=false;
                QualitySettings.vSyncCount=0;Application.targetFrameRate=60;Application.runInBackground=true;
                world=new WorldBuilder();world.Build();
                quality=new GameObject("Urban render quality").AddComponent<UrbanRenderQuality>();quality.Initialize();
                actorRoot=new GameObject("Tracked actors").transform;
                Rig=new GameObject("Synchronized sensor rig").AddComponent<CameraRig>();
                Rig.SensorPosition=new Vector3(12.5f,8,-12.5f);Rig.Initialize();
                ResetSimulation();SetView(0);
                string[] args=Environment.GetCommandLineArgs();
                string qa=Arg(args,"-qaOutput","");
                if(qa!=""){automated=true;captureFolder=Path.GetFullPath(qa);StartCoroutine(RunQualityCheck(captureFolder));return;}
                captureFolder=Arg(args,"-stationflowCapture","");automated=captureFolder!="";
                if(automated)
                {
                    CaptureFps=int.Parse(Arg(args,"-captureFps","10"));framesRequested=int.Parse(Arg(args,"-frames","900"));
                    int warmup=int.Parse(Arg(args,"-warmup","0"));
                    for(int i=0;i<warmup*60;i++)Tick();
                    StartCoroutine(AutomatedCapture(args));
                }
            }
            catch(Exception e){Debug.LogException(e);WriteFailure(e);if(Application.isBatchMode)Application.Quit(2);}
        }
        static string Arg(string[] args,string key,string fallback)
        {int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
        void ResetSimulation()
        {
            if(Rig.IsCapturing)StopCapture();
            foreach(var a in actors){a.Root.SetActive(false);Destroy(a.Root);}
            actors.Clear();labels.Clear();selected=-1;
            Simulation=new TrafficSimulation(Seed,Pedestrians,Vehicles);accumulator=0;
            Simulation.VisibilityGuard=Rig.IsObservableAt;
            foreach(var a in Simulation.Agents)
            {
                var visual=new ActorVisual(a,world,actorRoot);actors.Add(visual);
                labels.Add(new LabelTarget{id=a.Id,category=a.Kind==AgentKind.Pedestrian?"pedestrian":"car",root=visual.Root,state=a.State});
            }
            UpdateActors(0);status="Seed "+Seed+"  /  "+Pedestrians+" people  /  "+Vehicles+" vehicles";
            if(view==4)SetView(0);
        }
        void Tick(){Simulation.Step(StepSize);UpdateActors(StepSize);}
        void Update()
        {
            if(Simulation==null||automated)return;
            float dt=Mathf.Min(Time.unscaledDeltaTime,.15f);
            smoothedFps=Mathf.Lerp(smoothedFps,1/Mathf.Max(.001f,Time.unscaledDeltaTime),.04f);
            var k=Keyboard.current;
            if(k!=null)
            {
                if(k.spaceKey.wasPressedThisFrame)paused=!paused;
                if(k.f1Key.wasPressedThisFrame)showUI=!showUI;
                if(k.digit1Key.wasPressedThisFrame)SetView(0);
                if(k.digit2Key.wasPressedThisFrame)SetView(1);
                if(k.digit3Key.wasPressedThisFrame)SetView(2);
                if(k.digit4Key.wasPressedThisFrame)SetView(3);
                if(k.tabKey.wasPressedThisFrame)NextActor();
                if(k.lKey.wasPressedThisFrame)SetLighting((lightingPreset+1)%3);
                if(k.rKey.wasPressedThisFrame){if(Rig.IsCapturing)StopCapture();else StartCapture();}
                if(k.f12Key.wasPressedThisFrame)SaveScreenshot();
                if(k.escapeKey.wasPressedThisFrame&&!Application.isEditor)Application.Quit();
            }
            if(!paused)
            {
                if(Rig.IsCapturing){for(int i=0;i<60/CaptureFps;i++)Tick();RecordFrame();}
                else
                {
                    accumulator+=dt*speed;
                    while(accumulator>=StepSize){Tick();accumulator-=StepSize;}
                }
            }
            UpdateCamera(dt);
            if(Mouse.current!=null&&Mouse.current.leftButton.wasPressedThisFrame&&!PointerOverPanel())SelectActor();
            previewClock+=dt;
            if(!Rig.IsCapturing&&(showSensors||view==2||view==3)&&previewClock>(view==2||view==3?.05f:.35f))
            {previewClock=0;Rig.RenderSensors();}
        }
        void UpdateActors(float dt)
        {
            for(int i=0;i<Simulation.Agents.Count;i++)
            {
                var a=Simulation.Agents[i];actors[i].Update(a,dt);labels[i].state=a.State;labels[i].id=a.Id;
                labels[i].velocity=a.Kind==AgentKind.Pedestrian?new Vector3(a.VelocityX,0,a.VelocityZ):Quaternion.Euler(0,a.HeadingDegrees,0)*Vector3.forward*a.Speed;
                labels[i].groupId=a.GroupId;labels[i].groupSize=a.GroupSize;labels[i].conversing=a.Conversation;labels[i].navigationIntent=a.NavigationIntent;
            }
            world.UpdateSignals(Simulation.Phase);
            if(selected>=0&&(!Simulation.Agents[selected].Active||Simulation.Agents[selected].Id!=selectedTrackId))
            {selected=-1;if(view==4)SetView(0);}
        }
        public void SetView(int index)
        {
            view=index;if(Rig==null)return;
            Rig.OverviewCamera.enabled=index==0||index==1||index==4;
            if(index==4&&selected>=0)selectedTrackId=Simulation.Agents[selected].Id;
            Rig.OverviewCamera.fieldOfView=index==0?58:65;
            if(index==1)Rig.OverviewCamera.transform.SetPositionAndRotation(new Vector3(6.3f,1.75f,-12.1f),Quaternion.LookRotation(new Vector3(-8.5f,-.15f,15)));
            UpdateCamera(1);
        }
        void UpdateCamera(float dt)
        {
            Camera c=Rig.OverviewCamera;c.aspect=(float)Screen.width/Mathf.Max(1,Screen.height);
            if(view==0)
            {
                var m=Mouse.current;var k=Keyboard.current;
                if(!automated&&m!=null&&!PointerOverPanel())
                {
                    if(m.rightButton.isPressed){Vector2 d=m.delta.ReadValue();orbitYaw+=d.x*.16f;orbitPitch=Mathf.Clamp(orbitPitch-d.y*.14f,16,78);}
                    orbitDistance=Mathf.Clamp(orbitDistance-m.scroll.ReadValue().y*.015f,12,70);
                }
                if(!automated&&k!=null)
                {
                    Vector3 d=new Vector3((k.dKey.isPressed?1:0)-(k.aKey.isPressed?1:0),0,(k.wKey.isPressed?1:0)-(k.sKey.isPressed?1:0));
                    orbitTarget+=Quaternion.Euler(0,orbitYaw,0)*d*dt*(k.leftShiftKey.isPressed?18:8);
                    orbitTarget.x=Mathf.Clamp(orbitTarget.x,-25,25);orbitTarget.z=Mathf.Clamp(orbitTarget.z,-25,25);
                }
                Quaternion r=Quaternion.Euler(orbitPitch,orbitYaw,0);c.transform.SetPositionAndRotation(orbitTarget-r*Vector3.forward*orbitDistance,r);
            }
            else if(view==4&&selected>=0&&selected<actors.Count)
            {
                var a=Simulation.Agents[selected];Transform t=actors[selected].Root.transform;
                var mouse=Mouse.current;
                if(!automated&&mouse!=null&&!PointerOverPanel())
                {
                    if(mouse.rightButton.isPressed)followOrbit+=mouse.delta.ReadValue().x*.2f;
                    followZoom=Mathf.Clamp(followZoom-mouse.scroll.ReadValue().y*.0015f,.55f,2.3f);
                }
                Vector3 target=t.position+Vector3.up*1.2f;
                Vector3 desired=target+Quaternion.Euler(0,followOrbit,0)*(-t.forward*(a.Kind==AgentKind.Pedestrian?3.7f:6.2f)+t.right*2.6f)*followZoom+Vector3.up*1.6f;
                desired=world.KeepFollowCameraClear(target,desired);
                c.transform.position=Vector3.Distance(c.transform.position,desired)>35?desired:Vector3.Lerp(c.transform.position,desired,1-Mathf.Exp(-5*dt));
                c.transform.position=world.KeepFollowCameraClear(target,c.transform.position);
                c.transform.rotation=Quaternion.Slerp(c.transform.rotation,Quaternion.LookRotation(target-c.transform.position),1-Mathf.Exp(-7*dt));
            }
            Rig.RefreshObservationEnvelope();
        }
        void Follow(AgentKind kind)
        {
            float best=float.MaxValue;int choice=-1;
            for(int i=0;i<Simulation.Agents.Count;i++)
            {
                var a=Simulation.Agents[i];if(a.Kind!=kind||!a.Active)continue;
                float score=a.X*a.X+a.Z*a.Z+(a.Speed<.2f?200:0);
                if(score<best){best=score;choice=i;}
            }
            if(choice>=0){selected=choice;SetView(4);}
        }
        void FollowWalkingStyle(bool social)
        {
            float best=float.MaxValue;int choice=-1;
            for(int i=0;i<Simulation.Agents.Count;i++)
            {
                var a=Simulation.Agents[i];
                if(a.Kind!=AgentKind.Pedestrian||!a.Active||(social?a.GroupSize<2:a.NavigationIntent!="Corner"))continue;
                float score=a.X*a.X+a.Z*a.Z+(a.Speed<.2f?400:0)+(social&&!a.Conversation?150:0);
                if(score<best){best=score;choice=i;}
            }
            if(choice<0)return;
            selected=choice;followZoom=social?1.3f:1;followOrbit=social?115:0;SetView(4);
        }
        void NextActor()
        {
            for(int i=0;i<actors.Count;i++)
            {selected=(selected+1)%actors.Count;if(Simulation.Agents[selected].Active&&Simulation.Agents[selected].X*Simulation.Agents[selected].X+Simulation.Agents[selected].Z*Simulation.Agents[selected].Z<65*65){SetView(4);return;}}
            Follow(AgentKind.Pedestrian);
        }
        void SelectActor()
        {
            if(view==2||view==3)return;
            Ray ray=Rig.OverviewCamera.ScreenPointToRay(Mouse.current.position.ReadValue());float best=float.MaxValue;int hit=-1;
            for(int i=0;i<actors.Count;i++)
            {
                if(!Simulation.Agents[i].Active)continue;
                bool person=Simulation.Agents[i].Kind==AgentKind.Pedestrian;
                Bounds b=new Bounds(actors[i].Root.transform.position+Vector3.up*(person?.9f:.8f),person?new Vector3(.8f,1.9f,.8f):new Vector3(4.5f,1.7f,4.5f));
                if(b.IntersectRay(ray,out float d)&&d<best){best=d;hit=i;}
            }
            if(hit>=0||view!=4){selected=hit;if(hit>=0)selectedTrackId=Simulation.Agents[hit].Id;}
        }
        bool PointerOverPanel()
        {
            if(!showUI||Mouse.current==null)return false;
            Vector2 p=Mouse.current.position.ReadValue();float scale=UiScale(),offset=(Screen.width-1600*scale)*.5f,x=(p.x-offset)/scale,y=(Screen.height-p.y)/scale,h=Screen.height/scale;
            if(x<0||x>1600)return false;
            return y<83||y>h-83||x<281||(showSensors&&x>1272)||(selected>=0&&x>1272&&y>h-260);
        }
        void SetLighting(int value){lightingPreset=value;world.ApplyLightingPreset(value);quality.SetLighting(value);}
        float UiScale(){return Mathf.Min(Screen.width/1600f,Screen.height/900f);}
        void SetDensity(bool busy){Pedestrians=busy?96:48;Vehicles=busy?24:12;ResetSimulation();}
        void SaveScreenshot()
        {
            string folder=Application.isEditor?Path.GetFullPath("Reports/UrbanScreenshots"):Path.Combine(Path.GetDirectoryName(Application.dataPath),"Screenshots");
            Directory.CreateDirectory(folder);ScreenCapture.CaptureScreenshot(Path.Combine(folder,"EfreiResearchLab_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".png"));status="Screenshot saved.";
        }
        void StartCapture()
        {
            string dir=Application.isEditor?Path.GetFullPath("Captures"):Path.Combine(Path.GetDirectoryName(Application.dataPath),"Captures");
            BeginAt(Path.Combine(dir,DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_seed"+Seed));
        }
        void BeginAt(string folder)
        {
            if(CaptureFps<1||60%CaptureFps!=0)throw new ArgumentException("Capture FPS must divide 60.");
            captureFolder=Path.GetFullPath(folder);Rig.BeginCapture(captureFolder,Seed,CaptureFps);frame=0;captureStartTime=Simulation.Time;paused=false;
            captureStartRed=Simulation.RedLightViolations;captureStartPedRed=Simulation.PedestrianRedLightViolations;captureStartCollisions=Simulation.Collisions;
            captureStartTrips=Simulation.CompletedCrossings;captureStartTurns=Simulation.CompletedRightTurns;
            captureSeed=Seed;capturePeople=Pedestrians;captureVehicles=Vehicles;
            TextAsset info=Resources.Load<TextAsset>("build-info");if(info!=null)File.WriteAllText(Path.Combine(captureFolder,"build-info.json"),info.text);
            status="Recording RGB and annotations.";
        }
        void RecordFrame(){Rig.RenderSensors();Rig.SaveFrame(frame,Simulation.Time,Simulation.Phase.ToString(),labels);frame++;}
        void StopCapture(){Rig.EndCapture();WriteRunReport();status="Saved "+frame+" synchronized frame pairs.";}
        IEnumerator AutomatedCapture(string[] args)
        {
            yield return null;yield return null;bool failed=false;
            try
            {
                if(Arg(args,"-validateSensors","false")=="true")Rig.ValidateProjectionRendering(Path.Combine(captureFolder,"sensor-validation"));
                BeginAt(captureFolder);RecordFrame();
            }
            catch(Exception e){Debug.LogException(e);WriteFailure(e);failed=true;}
            if(failed){Application.Quit(3);yield break;}
            while(frame<framesRequested)
            {
                try{for(int i=0;i<60/CaptureFps;i++)Tick();RecordFrame();}
                catch(Exception e){Debug.LogException(e);WriteFailure(e);failed=true;break;}
                yield return null;
            }
            Rig.EndCapture();WriteRunReport();yield return null;
            if(Application.isBatchMode||Arg(args,"-exitAfterCapture","false")=="true")Application.Quit(failed?4:0);
        }
        [Serializable]sealed class RunReport
        {
            public string schema="stationflow.run.v2",unityVersion,graphicsDevice;
            public int seed,frames,fps,people,vehicles,redLightViolations,pedestrianRedLightViolations,collisions,completedCrossings,completedRightTurns;
            public double startSimulationTime,endSimulationTime;
            public string scope="Capture interval counters. Straight and protected right turns; exclusive pedestrian phase. Amodal labels; no occlusion ground truth.";
        }
        void WriteRunReport()
        {
            if(captureFolder=="")return;Directory.CreateDirectory(captureFolder);
            var r=new RunReport{unityVersion=Application.unityVersion,graphicsDevice=SystemInfo.graphicsDeviceName,seed=captureSeed,frames=frame,fps=CaptureFps,people=capturePeople,vehicles=captureVehicles,redLightViolations=Simulation.RedLightViolations-captureStartRed,pedestrianRedLightViolations=Simulation.PedestrianRedLightViolations-captureStartPedRed,collisions=Simulation.Collisions-captureStartCollisions,completedCrossings=Simulation.CompletedCrossings-captureStartTrips,completedRightTurns=Simulation.CompletedRightTurns-captureStartTurns,startSimulationTime=captureStartTime,endSimulationTime=Simulation.Time};
            File.WriteAllText(Path.Combine(captureFolder,"run-report.json"),JsonUtility.ToJson(r,true));
        }
        void WriteFailure(Exception e)
        {try{string dir=captureFolder==""?Path.Combine(Application.persistentDataPath,"StationFlowErrors"):captureFolder;Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"failure.txt"),e.ToString());}catch{}}
        void OnApplicationQuit(){if(Rig!=null&&Rig.IsCapturing)StopCapture();}
    }
}
