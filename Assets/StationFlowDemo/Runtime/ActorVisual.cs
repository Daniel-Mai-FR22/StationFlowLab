using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StationFlow.SyntheticTraffic
{
    /// <summary>Textured skinned citizens and smooth European passenger-car geometry.</summary>
    public sealed class ActorVisual
    {
        public GameObject Root { get; private set; }
        public Renderer[] Renderers { get; private set; }
        readonly WorldBuilder world;
        readonly AgentKind kind;
        CitizenRig citizen;
        Transform body;
        Transform[] wheelSpin,wheelSteer;
        readonly List<Renderer> brakeLamps=new List<Renderer>(),indicators=new List<Renderer>();
        readonly MaterialPropertyBlock lampBlock=new MaterialPropertyBlock();
        static readonly Dictionary<string,Mesh> combinedMeshes=new Dictionary<string,Mesh>();
        int vehicleStyle;
        float lastX,lastZ,lastSpeed,lastHeading,clock,steering,height,headingVelocity;
        int trackId=-1;
        bool hasPosition;
        GameObject S(PrimitiveType t,string n,Transform p,Vector3 x,Vector3 size,Material m,Vector3 r=default)=>WorldBuilder.Shape(t,n,p,x,size,m,r);
        Transform Joint(string name,Transform parent,Vector3 p){var t=new GameObject(name).transform;t.SetParent(parent,false);t.localPosition=p;return t;}
        public ActorVisual(SimAgent agent,WorldBuilder world,Transform parent)
        {
            this.world=world;kind=agent.Kind;Root=new GameObject((kind==AgentKind.Pedestrian?"Citizen ":"Vehicle ")+agent.Id);Root.transform.SetParent(parent,false);
            if(kind==AgentKind.Pedestrian){int v=Mathf.Abs(agent.Variant);height=.98f+(v%5)*.027f;Root.transform.localScale=new Vector3(height*(.96f+v%3*.035f),height,height);citizen=new CitizenRig(Root.transform,world,v);}
            else Vehicle(agent.Variant);
            Renderers=Root.GetComponentsInChildren<Renderer>();Update(agent,0);
        }
        void Vehicle(int variant)
        {
            int v=Mathf.Abs(variant),style=v%3;
            vehicleStyle=style;
            Color[] paints={new Color(.83f,.82f,.77f),new Color(.10f,.25f,.34f),new Color(.49f,.075f,.05f),new Color(.19f,.205f,.22f),new Color(.53f,.55f,.52f),new Color(.075f,.20f,.15f)};
            var paint=world.Mat("Pearlescent paint "+v%6,paints[v%6],.89f,.63f);
            var glass=world.Mat("Smoked reflective automotive glass",new Color(.10f,.155f,.18f),.96f,.48f);
            var rubber=world.Mat("Automotive rubber",new Color(.019f,.024f,.027f),.2f);
            var trim=world.Mat("Piano black trim",new Color(.025f,.032f,.038f),.76f,.22f);
            var chrome=world.Mat("Polished aluminium",new Color(.6f,.64f,.68f),.84f,.88f);
            var lamps=Emissive("LED running lights",new Color(.78f,.86f,.98f),1.2f);
            var brake=Emissive("Brake lamp red",new Color(.35f,.012f,.007f),.16f);
            var amber=Emissive("Indicator amber",new Color(.92f,.25f,.012f),.05f);
            body=Joint("Suspended body",Root.transform,Vector3.zero);
            float rear=style==0?-1.3f:style==1?-1.48f:-1.65f;
            float roof=style==0?1.42f:style==1?1.49f:1.57f;
            Loft("Sculpted monocoque",new[]{-2.13f,-1.96f,-1.65f,-.7f,.65f,1.48f,1.9f,2.12f},new[]{.70f,.84f,.90f,.91f,.91f,.87f,.79f,.65f},new[]{.57f,.73f,.83f,.87f,.84f,.76f,.65f,.55f},.32f,paint,body);
            Loft("Glazed cabin",new[]{rear,-.80f,.48f,1.09f},new[]{.73f,.66f,.66f,.76f},new[]{.86f,roof,roof-.025f,.84f},.80f,glass,body);
            Loft("Contoured roof",new[]{-.90f,-.76f,.40f,.53f},new[]{.59f,.65f,.65f,.60f},new[]{roof+.005f,roof+.026f,roof+.025f,roof-.015f},roof-.035f,paint,body);
            for(int side=-1;side<=1;side+=2)
            {
                Beam("A pillar",new Vector3(side*.745f,.85f,1.02f),new Vector3(side*.654f,roof-.015f,.48f),.034f,paint,body);
                Beam("C pillar",new Vector3(side*.73f,.86f,rear),new Vector3(side*.65f,roof-.012f,-.78f),.055f,paint,body);
                Beam("B pillar",new Vector3(side*.756f,.84f,-.19f),new Vector3(side*.67f,roof-.03f,-.19f),.039f,trim,body);
                Beam("Beltline chrome",new Vector3(side*.85f,.84f,rear),new Vector3(side*.83f,.80f,1.16f),.012f,chrome,body);
                Beam("Rocker sill",new Vector3(side*.91f,.34f,-1.25f),new Vector3(side*.91f,.34f,1.22f),.045f,trim,body);
                Beam("Door shut line",new Vector3(side*.917f,.40f,-.20f),new Vector3(side*.92f,.80f,-.20f),.005f,trim,body);
                S(PrimitiveType.Sphere,"Aerodynamic door mirror",body,new Vector3(side*.91f,1.0f,.81f),new Vector3(.24f,.145f,.28f),paint);
                S(PrimitiveType.Sphere,"Mirror reflective surface",body,new Vector3(side*.93f,1.0f,.70f),new Vector3(.17f,.10f,.022f),chrome);
                for(int door=-1;door<=1;door+=2)S(PrimitiveType.Cube,"Flush door handle",body,new Vector3(side*.911f,.76f,door*.56f),new Vector3(.018f,.025f,.17f),chrome);
                S(PrimitiveType.Sphere,"Clear headlamp lens",body,new Vector3(side*.56f,.61f,2.01f),new Vector3(.42f,.16f,.10f),glass);
                S(PrimitiveType.Cube,"LED running signature",body,new Vector3(side*.56f,.655f,2.055f),new Vector3(.36f,.026f,.027f),lamps);
                S(PrimitiveType.Sphere,"Projector headlamp",body,new Vector3(side*.57f,.594f,2.055f),new Vector3(.11f,.075f,.035f),lamps);
                brakeLamps.Add(S(PrimitiveType.Cube,"Rear brake lamp",body,new Vector3(side*.61f,.675f,-2.041f),new Vector3(.32f,.10f,.045f),brake).GetComponent<Renderer>());
                if(side>0)
                {
                    indicators.Add(S(PrimitiveType.Cube,"Front right turn signal",body,new Vector3(.78f,.6f,2.00f),new Vector3(.06f,.075f,.045f),amber).GetComponent<Renderer>());
                    indicators.Add(S(PrimitiveType.Cube,"Rear right turn signal",body,new Vector3(.80f,.675f,-2.018f),new Vector3(.065f,.08f,.04f),amber).GetComponent<Renderer>());
                    indicators.Add(S(PrimitiveType.Cube,"Right mirror turn signal",body,new Vector3(1.01f,1.0f,.8f),new Vector3(.035f,.022f,.1f),amber).GetComponent<Renderer>());
                }
            }
            brakeLamps.Add(S(PrimitiveType.Cube,"Centre high brake light",body,new Vector3(0,roof-.07f,-.90f),new Vector3(.34f,.025f,.028f),brake).GetComponent<Renderer>());
            S(PrimitiveType.Cube,"Front grille surround",body,new Vector3(0,.475f,2.115f),new Vector3(.77f,.19f,.05f),trim);
            for(int i=-5;i<=5;i++)S(PrimitiveType.Cube,"Grille vertical vane",body,new Vector3(i*.065f,.475f,2.144f),new Vector3(.017f,.155f,.011f),chrome);
            S(PrimitiveType.Cube,"Front lower intake",body,new Vector3(0,.345f,2.089f),new Vector3(1.48f,.083f,.05f),rubber);
            S(PrimitiveType.Cube,"Rear valance",body,new Vector3(0,.345f,-2.09f),new Vector3(1.35f,.085f,.04f),trim);
            for(int end=-1;end<=1;end+=2)
            {
                S(PrimitiveType.Cube,"European registration plate",body,new Vector3(0,.56f,end*2.147f),new Vector3(.43f,.095f,.016f),world.Mat("Plate enamel",new Color(.82f,.86f,.87f),.3f));
                S(PrimitiveType.Cube,"EU plate blue strip",body,new Vector3(-.18f,.56f,end*2.16f),new Vector3(.042f,.086f,.014f),world.Mat("EU registration blue",new Color(.015f,.045f,.31f),.3f));
                for(int k=0;k<6;k++)S(PrimitiveType.Cube,"Embossed plate glyph",body,new Vector3(-.11f+k*.046f,.56f,end*2.16f),new Vector3(.015f,.05f,.012f),trim);
            }
            wheelSpin=new Transform[4];wheelSteer=new Transform[4];int wi=0;
            for(int side=-1;side<=1;side+=2)for(int end=-1;end<=1;end+=2)
            {
                var steer=Joint(end>0?"Steering knuckle":"Rear axle",Root.transform,new Vector3(side*.855f,.326f,end*1.31f));wheelSteer[wi]=steer;
                var spin=Joint("Wheel rotation",steer,Vector3.zero);wheelSpin[wi++]=spin;
                Tyre(spin,rubber);
                S(PrimitiveType.Cylinder,"Brake disc",spin,new Vector3(side*.088f,0,0),new Vector3(.41f,.010f,.41f),world.Mat("Ventilated brake steel",new Color(.23f,.24f,.25f),.62f,.75f),new Vector3(0,0,90));
                S(PrimitiveType.Cylinder,"Wheel alloy rim",spin,new Vector3(side*.116f,0,0),new Vector3(.44f,.012f,.44f),chrome,new Vector3(0,0,90));
                S(PrimitiveType.Cylinder,"Rim inner dark",spin,new Vector3(side*.132f,0,0),new Vector3(.365f,.009f,.365f),trim,new Vector3(0,0,90));
                for(int k=0;k<5;k++)
                {
                    float angle=k*72f*Mathf.Deg2Rad;
                    S(PrimitiveType.Cube,"Alloy spoke",spin,new Vector3(side*.15f,Mathf.Cos(angle)*.105f,Mathf.Sin(angle)*.105f),new Vector3(.025f,.21f,.035f),chrome,new Vector3(k*72,0,0));
                }
                S(PrimitiveType.Cylinder,"Wheel centre",spin,new Vector3(side*.155f,0,0),new Vector3(.105f,.01f,.105f),chrome,new Vector3(0,0,90));
                CombineStaticParts(spin,false);
            }
            CombineStaticParts(body,true);
        }
        void CombineStaticParts(Transform parent,bool preserveLamps)
        {
            // These parts move as one rigid assembly. Keep signal renderers separate for property-block animation.
            var groups=new Dictionary<Material,List<CombineInstance>>();
            var originals=new List<GameObject>();
            Matrix4x4 toLocal=parent.worldToLocalMatrix;
            foreach(var filter in parent.GetComponentsInChildren<MeshFilter>())
            {
                var renderer=filter.GetComponent<MeshRenderer>();
                if(renderer==null||filter.sharedMesh==null||renderer.sharedMaterial==null)continue;
                if(preserveLamps&&(brakeLamps.Contains(renderer)||indicators.Contains(renderer)))continue;
                if(!groups.TryGetValue(renderer.sharedMaterial,out var group)){group=new List<CombineInstance>();groups.Add(renderer.sharedMaterial,group);}
                group.Add(new CombineInstance{mesh=filter.sharedMesh,transform=toLocal*filter.transform.localToWorldMatrix});
                originals.Add(filter.gameObject);
            }
            foreach(var entry in groups)
            {
                string key=(preserveLamps?"Body "+vehicleStyle:parent.parent.localPosition.x<0?"Wheel left":"Wheel right")+" "+entry.Key.name;
                if(!combinedMeshes.TryGetValue(key,out var mesh))
                {
                    mesh=new Mesh{name=key,indexFormat=IndexFormat.UInt32};
                    mesh.CombineMeshes(entry.Value.ToArray(),true,true,false);mesh.RecalculateBounds();combinedMeshes.Add(key,mesh);
                }
                var combined=new GameObject("Combined "+entry.Key.name);combined.transform.SetParent(parent,false);
                combined.AddComponent<MeshFilter>().sharedMesh=mesh;combined.AddComponent<MeshRenderer>().sharedMaterial=entry.Key;
            }
            foreach(var original in originals)
            {
                original.SetActive(false);
                if(Application.isPlaying)Object.Destroy(original);else Object.DestroyImmediate(original);
            }
        }
        Material Emissive(string name,Color color,float intensity){var m=world.Mat(name,color,.8f);m.EnableKeyword("_EMISSION");m.SetColor("_EmissionColor",color*intensity);return m;}
        void Beam(string name,Vector3 a,Vector3 b,float radius,Material mat,Transform parent)
        {
            var g=S(PrimitiveType.Cylinder,name,parent,(a+b)*.5f,new Vector3(radius*2,Vector3.Distance(a,b)*.5f,radius*2),mat);g.transform.localRotation=Quaternion.FromToRotation(Vector3.up,b-a);
        }
        static float Smooth(float a,float b,float c,float d,float t)=>.5f*((2*b)+(-a+c)*t+(2*a-5*b+4*c-d)*t*t+(-a+3*b-3*c+d)*t*t*t);
        void Loft(string name,float[] zs,float[] widths,float[] tops,float bottom,Material mat,Transform parent)
        {
            const int ring=16,sub=4;int count=(zs.Length-1)*sub+1;var vertices=new Vector3[count*ring];var triangles=new List<int>();
            for(int i=0;i<count;i++)
            {
                int k=Mathf.Min(zs.Length-2,i/sub);float t=(i-k*sub)/(float)sub;
                float z=Mathf.Lerp(zs[k],zs[k+1],t),w=Smooth(widths[Mathf.Max(0,k-1)],widths[k],widths[k+1],widths[Mathf.Min(zs.Length-1,k+2)],t),h=Smooth(tops[Mathf.Max(0,k-1)],tops[k],tops[k+1],tops[Mathf.Min(zs.Length-1,k+2)],t);
                for(int j=0;j<ring;j++)
                {
                    float angle=(j/(float)ring)*Mathf.PI*2;float x=Mathf.Sign(Mathf.Cos(angle))*Mathf.Pow(Mathf.Abs(Mathf.Cos(angle)),.40f)*w;
                    float y=(h+bottom)*.5f+Mathf.Sign(Mathf.Sin(angle))*Mathf.Pow(Mathf.Abs(Mathf.Sin(angle)),.52f)*(h-bottom)*.5f;
                    vertices[i*ring+j]=new Vector3(x,y,z);
                    if(i>0){int a=(i-1)*ring+j,b=(i-1)*ring+(j+1)%ring,c=i*ring+j,d=i*ring+(j+1)%ring;triangles.Add(a);triangles.Add(b);triangles.Add(c);triangles.Add(b);triangles.Add(d);triangles.Add(c);}
                }
            }
            for(int j=1;j<ring-1;j++){triangles.Add(0);triangles.Add(j+1);triangles.Add(j);int a=(count-1)*ring;triangles.Add(a);triangles.Add(a+j);triangles.Add(a+j+1);}
            MeshObject(name,vertices,triangles.ToArray(),mat,parent);
        }
        void Tyre(Transform parent,Material mat)
        {
            const int n=40,rings=8;var v=new Vector3[n*rings];var tr=new List<int>();
            float[] xs={-.125f,-.115f,-.09f,-.065f,.065f,.09f,.115f,.125f};float[] radius={.235f,.285f,.316f,.326f,.326f,.316f,.285f,.235f};
            for(int j=0;j<rings;j++)for(int i=0;i<n;i++)
            {
                float a=i*Mathf.PI*2/n;v[j*n+i]=new Vector3(xs[j],Mathf.Cos(a)*radius[j],Mathf.Sin(a)*radius[j]);
                if(j>0){int p=(j-1)*n+i,q=(j-1)*n+(i+1)%n,r=j*n+i,s=j*n+(i+1)%n;tr.Add(p);tr.Add(q);tr.Add(r);tr.Add(q);tr.Add(s);tr.Add(r);}
            }
            MeshObject("Radial rounded tyre",v,tr.ToArray(),mat,parent);
        }
        void MeshObject(string name,Vector3[] v,int[] tr,Material mat,Transform parent)
        {
            Mesh mesh=new Mesh{name=name};mesh.vertices=v;mesh.triangles=tr;mesh.RecalculateNormals();mesh.RecalculateBounds();var g=new GameObject(name);g.transform.SetParent(parent,false);g.AddComponent<MeshFilter>().sharedMesh=mesh;g.AddComponent<MeshRenderer>().sharedMaterial=mat;
        }
        public void Update(SimAgent agent,float dt)
        {
            Root.SetActive(agent.Active);if(!agent.Active){hasPosition=false;return;}
            bool fresh=trackId!=agent.Id||!hasPosition;
            if(fresh)
            {
                trackId=agent.Id;hasPosition=false;lastSpeed=agent.Speed;lastHeading=agent.HeadingDegrees;
                headingVelocity=steering=0;clock=(agent.Id%131)*.037f;
                Root.name=(kind==AgentKind.Pedestrian?"Citizen ":"Vehicle ")+agent.Id;
                if(citizen!=null)citizen.ResetIdentity(agent.Id);
            }
            float distance=hasPosition?Mathf.Sqrt((agent.X-lastX)*(agent.X-lastX)+(agent.Z-lastZ)*(agent.Z-lastZ)):0;
            bool teleport=distance>5;
            if(teleport){distance=0;headingVelocity=steering=0;if(citizen!=null)citizen.ResetIdentity(agent.Id);}
            lastX=agent.X;lastZ=agent.Z;clock+=dt;
            // Skin packs retain shoe volume: anchor the lowest sole, not the skeleton origin, to the surface.
            float ground=.01f;
            if(kind==AgentKind.Pedestrian)
            {
                float curb=Mathf.SmoothStep(0,1,Mathf.InverseLerp(6.95f,7.6f,Mathf.Min(Mathf.Abs(agent.X),Mathf.Abs(agent.Z))));
                ground=Mathf.Lerp(.012f,.16f,curb)-.0239446f*height;
            }
            Root.transform.position=new Vector3(agent.X,ground,agent.Z);
            Quaternion rotation=Quaternion.Euler(0,agent.HeadingDegrees,0);
            float priorVisualYaw=Root.transform.eulerAngles.y;
            if(kind==AgentKind.Vehicle||!hasPosition||teleport)Root.transform.rotation=rotation;
            else if(dt>0)
            {
                float heading=Mathf.SmoothDampAngle(priorVisualYaw,agent.HeadingDegrees,ref headingVelocity,.13f,agent.Speed>.08f?280:105,dt);
                Root.transform.rotation=Quaternion.Euler(0,heading,0);
            }
            if(kind==AgentKind.Pedestrian)
            {
                float turnRate=dt>.0001f&&hasPosition&&!teleport?Mathf.DeltaAngle(priorVisualYaw,Root.transform.eulerAngles.y)/dt:0;
                citizen.Animate(agent,distance,dt,turnRate);hasPosition=true;lastHeading=agent.HeadingDegrees;return;
            }
            float yaw=hasPosition&&!teleport?Mathf.DeltaAngle(lastHeading,agent.HeadingDegrees):0;
            float desired=dt>.001f&&agent.Speed>.25f?Mathf.Clamp(Mathf.Atan(yaw*Mathf.Deg2Rad/dt*2.62f/agent.Speed)*Mathf.Rad2Deg,-32,32):0;
            steering=Mathf.Lerp(steering,desired,1-Mathf.Exp(-dt*12));
            for(int i=0;i<wheelSpin.Length;i++){wheelSpin[i].Rotate(distance/.326f*Mathf.Rad2Deg,0,0,Space.Self);if(i%2==1)wheelSteer[i].localRotation=Quaternion.Euler(0,steering,0);}
            float acceleration=dt>.001f?(agent.Speed-lastSpeed)/dt:0;body.localRotation=Quaternion.Slerp(body.localRotation,Quaternion.Euler(Mathf.Clamp(acceleration*.20f,-.8f,.8f),0,-steering*.028f),1-Mathf.Exp(-dt*6));
            foreach(var lamp in brakeLamps){lampBlock.SetColor("_BaseColor",new Color(agent.BrakeLights?.85f:.28f,.012f,.006f));lampBlock.SetColor("_EmissionColor",new Color(agent.BrakeLights?2.8f:.20f,.012f,.006f));lamp.SetPropertyBlock(lampBlock);}
            bool blink=agent.IndicatorRight&&Mathf.Repeat(clock,.85f)<.43f;
            foreach(var lamp in indicators){lampBlock.SetColor("_BaseColor",new Color(blink?1:.24f,blink?.38f:.09f,.005f));lampBlock.SetColor("_EmissionColor",blink?new Color(3.8f,1.05f,.012f):Color.black);lamp.SetPropertyBlock(lampBlock);}
            lastSpeed=agent.Speed;lastHeading=agent.HeadingDegrees;hasPosition=true;
        }
    }
}
