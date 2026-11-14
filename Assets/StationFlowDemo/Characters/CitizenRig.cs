using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace StationFlow.SyntheticTraffic
{
    /// <summary>CC0 skinned citizens with planted-foot IK, speed-aware steps and independent social poses.</summary>
    public sealed class CitizenRig
    {
        [Serializable]class BoneData { public string name;public int parent;public Vector3 position; }
        [Serializable]class RigData { public BoneData[] bones; }
        static RigData data;
        static readonly Dictionary<string,Mesh> meshes=new Dictionary<string,Mesh>();
        readonly Transform[] bones;
        readonly Vector3 pelvisRest;
        readonly int variant;
        readonly Transform root;
        float phaseOffset;
        float motion,time,cyclePhase,stride=.6f,headYaw,headPitch,talking,gesture,phoneBlend,turnLean;
        float idleTime,pelvisHeightDrop,settleClock,settleDuration,settlePause;
        bool settling;
        int settleFoot=-1,lastSettledFoot;
        Vector3 settleFrom,settleTo;
        Quaternion settleFacingFrom,settleFacingTo;
        readonly FootContact[] contacts={new FootContact(),new FootContact()};
        sealed class FootContact
        {
            public bool initialized,swinging;
            public Vector3 planted,swingStart,lastPosition;
            public Quaternion facing;
            public float swingPhaseStart;
        }
        Transform phone,bag;
        readonly WorldBuilder world;

        public CitizenRig(Transform parent,WorldBuilder world,int variant)
        {
            this.world=world;this.variant=Math.Abs(variant);root=parent;phaseOffset=variant*2.173f;
            if(data==null)data=JsonUtility.FromJson<RigData>(Resources.Load<TextAsset>("Citizens/skeleton").text);
            bones=new Transform[data.bones.Length];
            for(int i=0;i<bones.Length;i++)
            {
                var b=data.bones[i];bones[i]=new GameObject(b.name).transform;
                bones[i].SetParent(b.parent<0?parent:bones[b.parent],false);
                bones[i].localPosition=b.position-(b.parent<0?Vector3.zero:data.bones[b.parent].position);
            }
            pelvisRest=bones[0].localPosition;
            string[] outfits={"male_casualsuit01","male_casualsuit02","male_casualsuit05","female_casualsuit01"};
            string outfit=outfits[this.variant%4];bool female=this.variant%4==3;
            Color[] tints={Color.white,new Color(.72f,.82f,.96f),new Color(.96f,.78f,.65f),new Color(.8f,.92f,.79f)};
            Skin(parent,"body_"+outfit,Textured("Citizen skin "+this.variant%4,"skin"+(female?2:this.variant%4==1?1:this.variant%4==2?3:0),Color.white,.32f));
            Skin(parent,outfit,Textured("Wardrobe "+this.variant,outfit+"_diffuse",tints[this.variant/4%4],.18f,outfit+"_normal",true));
            Skin(parent,"shoes01",Textured("Leather walking shoes","shoes01_diffuse",new Color(.55f,.55f,.55f),.44f,"shoes01_normal"));
            string hair=female?"bob02":"short02";
            Skin(parent,hair,Textured("Citizen hair "+hair+" "+this.variant%3,hair+"_diffuse",this.variant%3==1?new Color(.62f,.49f,.31f):new Color(.38f,.28f,.22f),.23f,hair+"_normal",true));
            Skin(parent,"low-poly",Textured("Citizen eye brown","eyes",Color.white,.72f));
            if(this.variant%3==0)
            {
                bag=new GameObject("Canvas backpack").transform;bag.SetParent(bones[2],false);bag.localPosition=new Vector3(0,-.07f,-.155f);
                var canvas=world.Mat("Canvas backpack "+this.variant%2,this.variant%2==0?new Color(.12f,.21f,.19f):new Color(.43f,.27f,.15f),.14f);
                WorldBuilder.Shape(PrimitiveType.Sphere,"Padded pack",bag,Vector3.zero,new Vector3(.29f,.36f,.15f),canvas);
                WorldBuilder.Shape(PrimitiveType.Cube,"Front pocket",bag,new Vector3(0,-.04f,-.066f),new Vector3(.21f,.16f,.038f),canvas);
                for(int side=-1;side<=1;side+=2)WorldBuilder.Shape(PrimitiveType.Cube,"Woven shoulder strap",bones[2],new Vector3(side*.126f,.001f,.071f),new Vector3(.027f,.31f,.018f),canvas);
            }
            if(this.variant%4==1)
            {
                phone=new GameObject("Phone").transform;phone.SetParent(bones[10],false);phone.localPosition=new Vector3(0,-.027f,.032f);
                WorldBuilder.Shape(PrimitiveType.Cube,"Phone case",phone,Vector3.zero,new Vector3(.059f,.108f,.01f),world.Mat("Phone graphite",new Color(.022f,.027f,.029f),.6f));
                WorldBuilder.Shape(PrimitiveType.Cube,"Phone display",phone,new Vector3(0,0,.006f),new Vector3(.05f,.094f,.002f),world.Mat("Phone display",new Color(.17f,.28f,.34f),.65f));
            }
        }

        Material Textured(string name,string texture,Color tint,float smooth,string normal=null,bool cutout=false)
        {
            Material m=world.Mat(name,tint,smooth);
            m.SetTexture("_BaseMap",Resources.Load<Texture2D>("Citizens/"+texture));
            if(normal!=null){m.SetTexture("_BumpMap",Resources.Load<Texture2D>("Citizens/"+normal));m.SetFloat("_BumpScale",.45f);m.EnableKeyword("_NORMALMAP");}
            if(cutout){m.SetFloat("_AlphaClip",1);m.SetFloat("_Cutoff",.3f);m.EnableKeyword("_ALPHATEST_ON");m.SetFloat("_Cull",0);m.renderQueue=(int)RenderQueue.AlphaTest;}
            return m;
        }
        void Skin(Transform parent,string name,Material material)
        {
            if(!meshes.TryGetValue(name,out Mesh mesh))
            {
                using(var stream=new MemoryStream(Resources.Load<TextAsset>("Citizens/"+name).bytes))
                using(var r=new BinaryReader(stream))
                {
                    int n=r.ReadInt32(),nt=r.ReadInt32();var v=new Vector3[n];var normals=new Vector3[n];var uv=new Vector2[n];var weights=new BoneWeight[n];
                    for(int i=0;i<n;i++)
                    {
                        v[i]=new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());normals[i]=new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());uv[i]=new Vector2(r.ReadSingle(),r.ReadSingle());
                        weights[i]=new BoneWeight{boneIndex0=r.ReadInt32(),boneIndex1=r.ReadInt32(),boneIndex2=r.ReadInt32(),boneIndex3=r.ReadInt32(),weight0=r.ReadSingle(),weight1=r.ReadSingle(),weight2=r.ReadSingle(),weight3=r.ReadSingle()};
                    }
                    var tri=new int[nt];for(int i=0;i<nt;i++)tri[i]=r.ReadInt32();
                    var bind=new Matrix4x4[bones.Length];for(int i=0;i<bind.Length;i++)bind[i]=Matrix4x4.Translate(-data.bones[i].position);
                    mesh=new Mesh{name="CC0 "+name,indexFormat=IndexFormat.UInt32};mesh.vertices=v;mesh.normals=normals;mesh.uv=uv;mesh.triangles=tri;mesh.boneWeights=weights;mesh.bindposes=bind;mesh.RecalculateTangents();mesh.RecalculateBounds();meshes.Add(name,mesh);
                }
            }
            var go=new GameObject(name);go.transform.SetParent(parent,false);var renderer=go.AddComponent<SkinnedMeshRenderer>();renderer.sharedMesh=mesh;renderer.sharedMaterial=material;renderer.bones=bones;renderer.rootBone=bones[0];renderer.localBounds=new Bounds(new Vector3(0,.9f,0),new Vector3(1.7f,2.1f,1.7f));renderer.quality=SkinQuality.Bone4;renderer.updateWhenOffscreen=false;
        }

        public void ResetIdentity(int trackId)
        {
            // A recycled pool entity must not inherit the previous person's feet, gaze or conversation beat.
            uint seed=unchecked((uint)(trackId*747796405+variant*2891336453L));
            phaseOffset=(seed%10007)*.006271f;time=0;
            cyclePhase=Mathf.Repeat(phaseOffset*.15915494f,1);
            motion=talking=gesture=phoneBlend=headYaw=headPitch=turnLean=0;
            idleTime=pelvisHeightDrop=settleClock=settlePause=0;settling=false;settleFoot=-1;lastSettledFoot=variant%2;
            stride=.6f;
            for(int i=0;i<bones.Length;i++)bones[i].localRotation=Quaternion.identity;
            bones[0].localPosition=pelvisRest;
            foreach(var foot in contacts)foot.initialized=false;
        }

        public void Animate(SimAgent agent,float distance,float dt,float visualTurnRate)
        {
            time+=dt;
            float height=Mathf.Max(.8f,root.lossyScale.y),speed=dt>.0001f?distance/dt:agent.Speed;
            idleTime=speed<.14f?idleTime+dt:0;
            if(!settling&&idleTime>.16f){settling=true;settleFoot=-1;settlePause=0;}
            if(settling)
            {
                AdvanceSettling(dt,speed);
                if(speed>.22f&&settleFoot<0)
                {
                    settling=false;idleTime=0;
                    // Resume from mid-stance. The opposite foot starts its shortened first swing from
                    // its current contact, rather than snapping into an arbitrary point of a walk cycle.
                    cyclePhase=lastSettledFoot==0?.3f:.8f;
                    foreach(var foot in contacts){foot.planted=foot.lastPosition;foot.swinging=false;}
                }
            }
            float target=Mathf.SmoothStep(0,1,Mathf.Clamp01(speed/.7f));
            motion=dt==0?target:Damp(motion,target,7,dt);
            // A foot is in contact for 60% of the cycle. This distance is also used to advance phase,
            // so changing walking speed changes cadence and reach together, without treadmill sliding.
            float normalizedSpeed=speed/height;
            float desiredStride=Mathf.Clamp(Mathf.Min(.34f+normalizedSpeed*.24f,Mathf.Max(.14f,normalizedSpeed*.70f)),.14f,.68f);
            stride=dt==0?desiredStride:Damp(stride,desiredStride,3,dt);
            float cycleDistance=stride*height/.6f;
            if(!settling)cyclePhase=Mathf.Repeat(cyclePhase+distance/Mathf.Max(.2f,cycleDistance),1);
            float walk=cyclePhase*Mathf.PI*2,idle=time+phaseOffset,waiting=1-motion;
            float leftReach=RestReach(cyclePhase),rightReach=RestReach(Mathf.Repeat(cyclePhase+.5f,1));
            float reach=Mathf.Max(Mathf.Abs(leftReach),Mathf.Abs(rightReach))*stride;
            float legLength=Vector3.Distance(data.bones[11].position,data.bones[12].position)+Vector3.Distance(data.bones[12].position,data.bones[13].position);
            float strideDrop=legLength-Mathf.Sqrt(Mathf.Max(.1f,legLength*legLength-reach*reach))+.007f;
            float restingDrop=FootStandingDrop(legLength);
            float pelvisDrop=settling?restingDrop:Mathf.Max(Mathf.Lerp(restingDrop,strideDrop,motion),restingDrop-.012f);
            pelvisHeightDrop=dt==0?pelvisDrop:Mathf.MoveTowards(pelvisHeightDrop,pelvisDrop,dt*(pelvisDrop>pelvisHeightDrop?1.2f:.45f));
            turnLean=Damp(turnLean,Mathf.Clamp(-visualTurnRate*speed*.012f,-3.8f,3.8f),4,dt);
            float idleShift=Mathf.Sin(idle*.37f)*.006f+Mathf.Sin(idle*.71f)*.002f;
            bones[0].localPosition=pelvisRest+new Vector3(Mathf.Sin(walk)*.006f*motion+idleShift*waiting,-pelvisHeightDrop,0);
            bones[0].localRotation=Quaternion.Euler(0,Mathf.Sin(walk)*2f*motion,Mathf.Sin(idle*.37f)*.8f*waiting);
            bones[1].localRotation=Quaternion.Euler(1.3f*motion,Mathf.Sin(walk)*-2.6f*motion,turnLean+Mathf.Cos(walk)*.7f*motion);
            bones[2].localRotation=Quaternion.Euler(Mathf.Sin(idle*1.12f)*.5f,0,0);

            talking=Damp(talking,agent.Conversation?1:0,3,dt);
            gesture=Damp(gesture,Mathf.Clamp01(agent.GestureBlend)*talking,5,dt);
            float swing=Mathf.Sin(walk)*Mathf.Lerp(11,22,Mathf.Clamp01(speed/1.7f))*motion;
            bones[5].localRotation=Quaternion.Euler(-swing,0,3);
            bones[8].localRotation=Quaternion.Euler(swing,0,-3);
            bones[6].localRotation=Quaternion.Euler(-10-Mathf.Max(0,swing)*.30f,0,0);
            bones[9].localRotation=Quaternion.Euler(-10-Mathf.Max(0,-swing)*.30f,0,0);
            bones[7].localRotation=Quaternion.identity;bones[10].localRotation=Quaternion.identity;

            // Short, person-specific phrases rather than matching cyclic arm movement across a group.
            float phrase=Mathf.Sin(idle*2.15f+Mathf.Sin(idle*.47f)*1.7f);
            float accent=Mathf.Max(0,Mathf.Sin(idle*3.17f+phaseOffset));
            bool useLeft=variant%2==0||phone!=null;
            int upper=useLeft?5:8,lower=upper+1,wrist=upper+2;float side=useLeft?-1:1;
            BlendBone(upper,new Vector3(-18-phrase*5,side*9,side*(5+accent*5)),gesture);
            BlendBone(lower,new Vector3(-44-accent*14,side*(9+phrase*5),side*4),gesture);
            BlendBone(wrist,new Vector3(4+phrase*7,side*(12+phrase*7),side*5),gesture);
            bones[2].localRotation*=Quaternion.Euler(0,Mathf.Clamp(agent.ConversationLookYaw,-55,55)*.075f*talking,0);

            float phoneCheck=phone!=null&&!agent.Conversation&&!agent.Looking?waiting*Pulse(idle+phaseOffset,14.7f,5.6f,1.6f):0;
            phoneBlend=Damp(phoneBlend,phoneCheck,3.2f,dt);
            if(phone!=null)
            {
                BlendBone(8,new Vector3(-25,2,9),phoneBlend);
                BlendBone(9,new Vector3(-83,-5,0),phoneBlend);
                BlendBone(10,new Vector3(3,-4,2),phoneBlend);
            }
            float conversationalGlance=Pulse(idle*.93f,7.9f,4.8f,.8f)*talking;
            float desiredYaw=agent.Looking?agent.LookYaw:Mathf.Lerp(Mathf.Sin(idle*.43f)*5,agent.ConversationLookYaw,conversationalGlance);
            float desiredPitch=phoneBlend*20+Mathf.Sin(idle*.81f)*.8f+gesture*Mathf.Sin(idle*2.15f)*1.6f;
            headYaw=Mathf.MoveTowardsAngle(headYaw,Mathf.Clamp(desiredYaw,-67,67),dt*105);
            headPitch=Damp(headPitch,desiredPitch,5,dt);
            bones[3].localRotation=Quaternion.Euler(headPitch*.18f,headYaw*.28f,0);
            bones[4].localRotation=Quaternion.Euler(headPitch*.82f,headYaw*.72f,Mathf.Sin(idle*.39f)*1.1f*waiting+gesture*.7f);
            if(bag!=null)bag.localRotation=Quaternion.Euler(Mathf.Sin(walk*2)*1.1f*motion,0,Mathf.Sin(walk)*1.5f*motion);

            if(settling)
            {
                SolveLeg(11,contacts[0].lastPosition,contacts[0].facing);
                SolveLeg(14,contacts[1].lastPosition,contacts[1].facing);
            }
            else
            {
                Foot(0,11,cyclePhase,agent,speed,cycleDistance,dt);
                Foot(1,14,Mathf.Repeat(cyclePhase+.5f,1),agent,speed,cycleDistance,dt);
            }
        }

        static float Damp(float value,float target,float rate,float dt)=>Mathf.Lerp(value,target,1-Mathf.Exp(-dt*rate));
        static float RestReach(float phase)=>phase<=.6f?.5f-phase/.6f:Mathf.Lerp(-.5f,.5f,Mathf.SmoothStep(0,1,(phase-.6f)/.4f));
        static float Pulse(float t,float period,float duration,float ease)
        {
            float p=Mathf.Repeat(t,period);
            return Mathf.SmoothStep(0,1,Mathf.Clamp01(p/ease))*Mathf.SmoothStep(0,1,Mathf.Clamp01((duration-p)/ease));
        }
        void BlendBone(int index,Vector3 pose,float weight)=>bones[index].localRotation=Quaternion.Slerp(bones[index].localRotation,Quaternion.Euler(pose),weight);

        Vector3 RelaxedFootPosition(int index)
        {
            Vector3 rest=data.bones[index==0?13:16].position;
            // A small fore-aft difference and slight toe-out make the rest pose less rigid.
            rest.z+=index==0?.018f:-.022f;
            return root.TransformPoint(rest);
        }
        Quaternion RelaxedFootFacing(int index)=>root.rotation*Quaternion.Euler(0,index==0?5:-5,0);
        void AdvanceSettling(float dt,float speed)
        {
            for(int i=0;i<contacts.Length;i++)if(!contacts[i].initialized)
            {
                contacts[i].planted=contacts[i].lastPosition=RelaxedFootPosition(i);
                contacts[i].facing=RelaxedFootFacing(i);contacts[i].initialized=true;contacts[i].swinging=false;
            }
            if(settleFoot<0)
            {
                settlePause=Mathf.Max(0,settlePause-dt);
                if(speed>.22f||settlePause>0)return;
                int choice=-1;float best=0;
                for(int i=0;i<contacts.Length;i++)
                {
                    var foot=contacts[i];float distance=Vector3.Distance(foot.lastPosition,RelaxedFootPosition(i));
                    float angle=Quaternion.Angle(foot.facing,RelaxedFootFacing(i));
                    if(distance<.045f&&angle<24&&!foot.swinging)continue;
                    float score=distance+(foot.swinging?1:0)+(angle>24?angle*.002f:0)+(i!=lastSettledFoot?.012f:0);
                    if(score>best){best=score;choice=i;}
                }
                if(choice<0)return;
                settleFoot=choice;settleClock=0;
                var moving=contacts[choice];settleFrom=moving.lastPosition;settleTo=RelaxedFootPosition(choice);
                settleFacingFrom=moving.facing;settleFacingTo=RelaxedFootFacing(choice);
                settleDuration=Mathf.Clamp(.20f+Vector3.Distance(settleFrom,settleTo)*.30f,.22f,.36f);
                moving.swinging=false;
            }
            var active=contacts[settleFoot];settleClock+=dt;
            float p=Mathf.Clamp01(settleClock/settleDuration),smooth=Mathf.SmoothStep(0,1,p);
            // Only the airborne foot is repositioned. The support foot retains its world-space contact.
            settleTo=RelaxedFootPosition(settleFoot);settleFacingTo=RelaxedFootFacing(settleFoot);
            active.lastPosition=Vector3.Lerp(settleFrom,settleTo,smooth)+Vector3.up*(Mathf.Sin(p*Mathf.PI)*.035f*root.lossyScale.y);
            active.facing=Quaternion.Slerp(settleFacingFrom,settleFacingTo,smooth);
            if(p>=1)
            {
                active.planted=active.lastPosition;active.swinging=false;lastSettledFoot=settleFoot;
                settleFoot=-1;settlePause=.065f;
            }
        }

        float FootStandingDrop(float length)
        {
            // Contacts constrain the pelvis during a step. Once both feet settle, this approaches upright.
            float reach=0;
            for(int i=0;i<contacts.Length;i++)if(contacts[i].initialized)
            {
                Vector3 p=root.InverseTransformPoint(contacts[i].lastPosition)-data.bones[i==0?13:16].position;
                reach=Mathf.Max(reach,Mathf.Min(.43f,new Vector2(p.x,p.z).magnitude));
            }
            return length-Mathf.Sqrt(Mathf.Max(.1f,length*length-reach*reach))+.002f;
        }

        void Foot(int index,int upper,float phase,SimAgent agent,float speed,float cycleDistance,float dt)
        {
            FootContact foot=contacts[index];bool swing=phase>.6f;
            Vector3 rest=data.bones[upper+2].position;
            Vector3 flat=root.TransformPoint(rest);
            if(!foot.initialized)
            {
                foot.planted=root.TransformPoint(rest+Vector3.forward*(RestReach(phase)*stride*motion));
                foot.swingStart=root.TransformPoint(rest-Vector3.forward*stride*.5f);
                foot.facing=root.rotation;foot.lastPosition=foot.planted;foot.swinging=swing;foot.swingPhaseStart=0;foot.initialized=true;
            }
            if(swing&&!foot.swinging){foot.swingStart=foot.lastPosition;foot.swingPhaseStart=(phase-.6f)/.4f;}
            if(!swing&&foot.swinging){foot.planted=foot.lastPosition;foot.planted.y=flat.y;foot.facing=root.rotation;}
            Vector3 target=foot.planted;Quaternion facing=foot.facing;float toe=0;
            if(swing)
            {
                float raw=(phase-.6f)/.4f,p=Mathf.InverseLerp(foot.swingPhaseStart,1,raw);
                float remaining=(1-phase)*cycleDistance/Mathf.Max(.12f,speed);
                Vector3 velocity=new Vector3(agent.VelocityX,0,agent.VelocityZ);
                Vector3 landing=flat+root.forward*(stride*.5f*root.lossyScale.y)+velocity*Mathf.Min(.7f,remaining);
                if(speed<.06f)landing=flat+root.forward*(stride*.22f*root.lossyScale.y);
                target=Vector3.Lerp(foot.swingStart,landing,Mathf.SmoothStep(0,1,p));
                target.y=Mathf.Lerp(foot.swingStart.y,flat.y,p)+Mathf.Sin(p*Mathf.PI)*(.045f+stride*.035f)*root.lossyScale.y;
                facing=Quaternion.Slerp(foot.facing,root.rotation,Mathf.SmoothStep(0,1,p));
                toe=Mathf.Sin(p*Mathf.PI)*6;
            }
            else
            {
                // Curved paths can rotate sharply around a planted foot. Limit only an unreachable contact;
                // ordinary stance remains fixed in world space even while the body steers around another person.
                Vector3 local=root.InverseTransformPoint(target)-rest;
                float reach=new Vector2(local.x,local.z).magnitude;
                if(reach>.45f)
                {
                    float correction=Mathf.Clamp01(dt*7);
                    target=Vector3.Lerp(target,flat+root.TransformVector(new Vector3(local.x,0,local.z)*(.43f/reach)),correction);
                    foot.planted=target;
                }
            }
            SolveLeg(upper,target,facing*Quaternion.Euler(toe,0,0));
            foot.lastPosition=target;foot.swinging=swing;
        }

        void SolveLeg(int upper,Vector3 target,Quaternion footRotation)
        {
            // Solve the knee in a forward bend plane, including sideways avoidance and curved trajectories.
            Transform hip=bones[upper],knee=bones[upper+1],ankle=bones[upper+2];
            hip.localRotation=Quaternion.identity;knee.localRotation=Quaternion.identity;ankle.localRotation=Quaternion.identity;
            Vector3 start=hip.position;float a=Vector3.Distance(start,knee.position),b=Vector3.Distance(knee.position,ankle.position);
            Vector3 delta=target-start;float d=Mathf.Clamp(delta.magnitude,.1f,a+b-.0005f),cos=Mathf.Clamp((a*a+d*d-b*b)/(2*a*d),-1,1);
            Vector3 direction=delta.sqrMagnitude>.000001f?delta.normalized:Vector3.down;
            Vector3 reachable=start+direction*d;
            Vector3 bend=Vector3.ProjectOnPlane(root.forward,direction).normalized;
            if(bend.sqrMagnitude<.5f)bend=root.forward;
            Vector3 kneeTarget=start+direction*(a*cos)+bend*(a*Mathf.Sqrt(Mathf.Max(0,1-cos*cos)));
            hip.rotation=Quaternion.FromToRotation(knee.position-start,kneeTarget-start)*hip.rotation;
            knee.rotation=Quaternion.FromToRotation(ankle.position-knee.position,reachable-knee.position)*knee.rotation;
            ankle.rotation=footRotation;
        }
    }
}
