using UnityEngine;

namespace StationFlow.SyntheticTraffic
{
    public sealed partial class DemoController
    {
        GUIStyle title,heading,body,small,button,big,tagStyle;
        bool stylesReady;
        Texture2D researchLogo;
        readonly Color teal=new Color(.44f,.9f,.78f),muted=new Color(.62f,.69f,.73f),panel=new Color(.025f,.045f,.057f,.94f);
        void Styles()
        {
            if(stylesReady)return;stylesReady=true;
            researchLogo=Resources.Load<Texture2D>("Branding/EfreiResearchLab");
            title=new GUIStyle(GUI.skin.label){fontSize=29,fontStyle=FontStyle.Bold,normal={textColor=Color.white}};
            heading=new GUIStyle(title){fontSize=15};
            body=new GUIStyle(GUI.skin.label){fontSize=13,wordWrap=true,normal={textColor=new Color(.87f,.9f,.91f)}};
            small=new GUIStyle(body){fontSize=11,normal={textColor=muted}};
            big=new GUIStyle(title){fontSize=34};
            tagStyle=new GUIStyle(small){fontStyle=FontStyle.Bold,normal={textColor=teal}};
            button=new GUIStyle(GUI.skin.button){fontSize=12,fontStyle=FontStyle.Bold,alignment=TextAnchor.MiddleCenter};
            button.normal.textColor=Color.white;button.normal.background=null;button.hover.background=null;button.active.background=null;
        }
        void Fill(Rect r,Color c){Color old=GUI.color;GUI.color=c;GUI.DrawTexture(r,Texture2D.whiteTexture);GUI.color=old;}
        void Label(float x,float y,float w,float h,string s,GUIStyle style){GUI.Label(new Rect(x,y,w,h),s,style);}
        bool Button(float x,float y,float w,float h,string s,bool active=false)
        {Rect r=new Rect(x,y,w,h);Fill(r,active?new Color(.13f,.34f,.3f):new Color(.12f,.17f,.2f));return GUI.Button(r,s,button);}
        static string PhaseName(SignalPhase p)
        {
            switch(p)
            {
                case SignalPhase.NorthSouthGreen:return "NORTH / SOUTH";
                case SignalPhase.EastWestGreen:return "EAST / WEST";
                case SignalPhase.PedestrianWalk:return "PEDESTRIANS WALK";
                case SignalPhase.PedestrianClearance:return "PEDESTRIANS CLEAR";
                case SignalPhase.NorthSouthAmber:case SignalPhase.EastWestAmber:return "AMBER / CLEARING";
                default:return "ALL RED / CLEARING";
            }
        }
        static string BehaviourName(string state)
        {
            switch(state)
            {
                case "WalkingTogether":return "Walking together";
                case "Conversing":return "Walking & talking";
                case "Regrouping":return "Waiting for friends";
                case "Yielding":return "Making room";
                default:return state.Replace('_',' ');
            }
        }
        void OnGUI()
        {
            if(Simulation==null||Rig==null)return;Styles();
            if(view==2||view==3)
            {
                Fill(new Rect(0,0,Screen.width,Screen.height),Color.black);
                GUI.DrawTexture(new Rect(0,0,Screen.width,Screen.height),view==2?Rig.PinholeTexture:Rig.FisheyeTexture,ScaleMode.ScaleToFit);
            }
            if(!showUI)return;
            float f=UiScale();Matrix4x4 old=GUI.matrix;GUI.matrix=Matrix4x4.TRS(new Vector3((Screen.width-1600*f)*.5f,0,0),Quaternion.identity,new Vector3(f,f,1));float h=Screen.height/f;
            Fill(new Rect(0,0,1600,80),panel);
            if(researchLogo!=null)
            {
                Fill(new Rect(18,7,152,66),Color.white);
                // Display the official square asset's central wordmark, preserving its aspect ratio.
                GUI.DrawTextureWithTexCoords(new Rect(28,7,132,66),researchLogo,new Rect(70f/660,240f/660,480f/660,240f/660));
            }
            Label(190,12,350,16,"SYNTHETIC VISION",tagStyle);Label(190,32,360,38,"Efrei Research Lab",title);
            Label(565,28,450,23,"LIVING STREETS",heading);Label(565,49,600,20,"SOLO WALKS   /   SOCIAL GROUPS   /   CONTINUOUS JOURNEYS",small);
            Label(1300,24,280,20,Rig.IsCapturing?"RECORDING  /  "+frame+" PAIRS":paused?"SIMULATION PAUSED":"LIVE  /  SEED "+Seed,tagStyle);
            Label(1300,46,280,20,Simulation.Time.ToString("000.0")+" s    ·    "+smoothedFps.ToString("0")+" fps",small);
            float leftBottom=Mathf.Min(h-100,735);
            Fill(new Rect(18,98,252,leftBottom-98),panel);
            Label(34,113,222,21,"SIGNAL CONTROL",tagStyle);
            Label(34,145,225,23,PhaseName(Simulation.Phase),heading);
            Label(34,173,150,46,Simulation.PhaseRemaining.ToString("0.0")+" s",big);
            Label(34,223,220,32,"Protected turns and pedestrian crossing.",small);
            Fill(new Rect(34,265,220,1),new Color(.2f,.27f,.29f));
            Label(34,278,220,20,"STREET ACTIVITY",tagStyle);
            int localPeople=0,localCars=0,solo=0,social=0;
            foreach(var a in Simulation.Agents)
            {
                if(!a.Active||a.X*a.X+a.Z*a.Z>60*60)continue;
                if(a.Kind==AgentKind.Vehicle){localCars++;continue;}
                localPeople++;if(a.GroupSize>1)social++;else solo++;
            }
            Label(34,306,220,24,localPeople+" people    ·    "+localCars+" cars nearby",body);
            Label(34,333,220,22,solo+" solo    ·    "+social+" walking in groups",body);
            Label(34,359,220,22,"Completed crossings   "+Simulation.CompletedCrossings,body);
            int incidents=Simulation.RedLightViolations+Simulation.PedestrianRedLightViolations+Simulation.Collisions;
            Label(34,392,220,22,incidents==0?"●  Traffic checks clear":"Traffic incidents  "+incidents,incidents==0?tagStyle:heading);
            Label(34,432,220,20,"LIGHT & ATMOSPHERE",tagStyle);
            if(Button(34,462,69,31,"DAY",lightingPreset==0))SetLighting(0);
            if(Button(108,462,70,31,"GOLDEN",lightingPreset==1))SetLighting(1);
            if(Button(183,462,71,31,"CLOUD",lightingPreset==2))SetLighting(2);
            Label(34,511,220,20,"POPULATION",tagStyle);
            if(Button(34,540,105,31,"QUIET",Pedestrians==48))SetDensity(false);
            if(Button(149,540,105,31,"BUSY",Pedestrians==96))SetDensity(true);
            if(Button(34,591,105,33,"REPLAY"))ResetSimulation();
            if(Button(149,591,105,33,"NEW SEED")){Seed++;ResetSimulation();}
            if(Button(34,637,220,31,quality.HighQuality?"QUALITY  /  HIGH":"QUALITY  /  BALANCED"))quality.ToggleQuality();
            if(Button(34,681,220,31,showSensors?"HIDE SENSOR PREVIEWS":"SHOW SENSOR PREVIEWS"))showSensors=!showSensors;
            if(showSensors)
            {
                float x=1288;
                Fill(new Rect(x,98,294,226),panel);Label(x+14,110,265,20,"01  /  PINHOLE CAMERA",tagStyle);
                Label(x+14,134,265,20,"1280 × 720    ·    70° VERTICAL",small);
                GUI.DrawTexture(new Rect(x+10,163,274,154),Rig.PinholeTexture,ScaleMode.ScaleToFit);
                Fill(new Rect(x,338,294,326),panel);Label(x+14,350,265,20,"02  /  FISHEYE CAMERA",tagStyle);
                Label(x+14,374,265,20,"768 × 768    ·    180° EQUIDISTANT",small);
                GUI.DrawTexture(new Rect(x+25,408,244,244),Rig.FisheyeTexture,ScaleMode.ScaleToFit);
            }
            if(selected>=0&&selected<Simulation.Agents.Count&&Simulation.Agents[selected].Active)
            {
                var a=Simulation.Agents[selected];float y=h-245;
                Fill(new Rect(1288,y,294,153),panel);Label(1302,y+12,262,22,(a.Kind==AgentKind.Pedestrian?"CITIZEN":"VEHICLE")+"  /  "+a.Id,tagStyle);
                Label(1302,y+40,260,25,BehaviourName(a.State)+"   ·   "+(a.Speed*3.6f).ToString("0.0")+" km/h",body);
                Label(1302,y+68,260,28,a.Kind==AgentKind.Vehicle?(a.TurnIntent==1?"Route: right turn":"Route: straight"):(a.GroupSize>1?"Group of "+a.GroupSize+(a.Conversation?"  ·  Chatting":"  ·  Walking together"):a.NavigationIntent=="Corner"?"Solo  ·  Turning the street corner":"Solo  ·  Crossing to another street"),small);
                if(Button(1302,y+104,126,31,"FOLLOW",view==4))SetView(4);
                if(Button(1438,y+104,126,31,"NEXT"))NextActor();
            }
            Label(292,100,750,22,view==0?"ORBIT VIEW":view==1?"STREET LEVEL":view==2?"PINHOLE SENSOR":view==3?"FISHEYE SENSOR":"FOLLOWING ACTOR",tagStyle);
            Fill(new Rect(0,h-83,1600,83),panel);
            if(Button(20,h-68,98,32,paused?"RESUME":"PAUSE",paused))paused=!paused;
            if(Button(126,h-68,66,32,speed.ToString("0")+"×",speed>1))speed=speed==1?2:speed==2?4:1;
            if(Button(210,h-68,139,32,"1  OVERVIEW",view==0))SetView(0);
            if(Button(357,h-68,125,32,"2  STREET",view==1))SetView(1);
            if(Button(490,h-68,125,32,"3  PINHOLE",view==2))SetView(2);
            if(Button(623,h-68,125,32,"4  FISHEYE",view==3))SetView(3);
            if(Button(772,h-68,135,32,"FOLLOW PERSON"))Follow(AgentKind.Pedestrian);
            if(Button(915,h-68,125,32,"FOLLOW CAR"))Follow(AgentKind.Vehicle);
            if(Button(1048,h-68,132,32,"FOLLOW GROUP"))FollowWalkingStyle(true);
            if(Button(1188,h-68,132,32,"CORNER WALK"))FollowWalkingStyle(false);
            if(Button(1397,h-68,183,32,Rig.IsCapturing?"STOP RECORDING":"RECORD DATA",Rig.IsCapturing)){if(Rig.IsCapturing)StopCapture();else StartCapture();}
            Label(20,h-28,1340,23,"Right mouse: orbit   ·   Wheel: zoom   ·   WASD: pan   ·   Click: select   ·   Tab: follow next   ·   L: light   ·   F1: clean view   ·   Esc: quit",small);
            GUI.matrix=old;
        }
    }
}
