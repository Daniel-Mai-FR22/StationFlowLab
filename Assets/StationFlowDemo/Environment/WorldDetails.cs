using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StationFlow.SyntheticTraffic
{
    public sealed partial class WorldBuilder
    {
        void ApplyRoughness(Material material,string asset)
        {
            Texture2D source=Resources.Load<Texture2D>("StationFlowArt/"+asset+"_roughness");
            if(source==null||SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null)return;
            RenderTexture previous=RenderTexture.active;
            var target=RenderTexture.GetTemporary(512,512,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            Graphics.Blit(source,target);RenderTexture.active=target;
            var packed=new Texture2D(512,512,TextureFormat.RGBA32,true,true){name=asset+" metallic smoothness",anisoLevel=8};
            packed.ReadPixels(new Rect(0,0,512,512),0,0);
            Color[] pixels=packed.GetPixels();
            for(int i=0;i<pixels.Length;i++)pixels[i]=new Color(0,0,0,Mathf.Clamp01(1-pixels[i].r));
            packed.SetPixels(pixels);packed.Apply(true,true);
            RenderTexture.active=previous;RenderTexture.ReleaseTemporary(target);
            material.SetTexture("_MetallicGlossMap",packed);material.SetFloat("_Smoothness",.82f);material.EnableKeyword("_METALLICSPECGLOSSMAP");
        }
        Transform Group(string name,Vector3 p,float angle=0,Transform parent=null)
        {
            var t=new GameObject(name).transform;t.SetParent(parent??Root,false);t.localPosition=p;t.localRotation=Quaternion.Euler(0,angle,0);return t;
        }
        void Rod(string name,Vector3 a,Vector3 b,float radius,Material mat,Transform parent=null)
        {
            var o=Shape(PrimitiveType.Cylinder,name,parent??Root,(a+b)*.5f,new Vector3(radius*2,Vector3.Distance(a,b)*.5f,radius*2),mat);
            o.transform.localRotation=Quaternion.FromToRotation(Vector3.up,b-a);
        }
        void MeshObject(string name,Vector3[] vertices,int[] triangles,Material mat,Transform parent)
        {
            var mesh=new Mesh{name=name,indexFormat=IndexFormat.UInt32};mesh.vertices=vertices;mesh.triangles=triangles;mesh.RecalculateNormals();mesh.RecalculateBounds();
            var go=new GameObject(name);go.transform.SetParent(parent,false);go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterial=mat;
        }
        void DetailedCurbs(int sx,int sz,Material curb)
        {
            for(float d=7;d<87;d+=1.25f)
            {
                // Lowered kerbstones at the zebra crossing, continuous joints elsewhere.
                float h=d>=8.25f&&d<=10.75f?.025f:.16f;
                Box("Jointed kerbstone N-S",new Vector3(sx*7.08f,h/2,sz*(d+.61f)),new Vector3(.2f,h,1.225f),curb);
                Box("Jointed kerbstone E-W",new Vector3(sx*(d+.61f),h/2,sz*7.08f),new Vector3(1.225f,h,.2f),curb);
            }
            var tactile=Mat("Tactile limestone",new Color(.70f,.66f,.51f),.22f);
            for(int row=0;row<3;row++)for(int col=0;col<14;col++)
            {
                Shape(PrimitiveType.Cylinder,"Tactile paving stud",Root,new Vector3(sx*(7.9f+row*.14f),.173f,sz*(9.05f+col*.145f)),new Vector3(.035f,.009f,.035f),tactile);
                Shape(PrimitiveType.Cylinder,"Tactile paving stud",Root,new Vector3(sx*(9.05f+col*.145f),.173f,sz*(7.9f+row*.14f)),new Vector3(.035f,.009f,.035f),tactile);
            }
        }

        void Building(Vector3 p,float width,float depth,float height,int front,int variant)
        {
            Transform b=Group("European townhouse address",p);
            Color[] palette={new Color(.76f,.70f,.59f),new Color(.83f,.80f,.71f),new Color(.77f,.73f,.62f),new Color(.63f,.67f,.64f),new Color(.74f,.61f,.48f),new Color(.70f,.74f,.72f)};
            int v=Math.Abs(variant)%palette.Length;
            var wall=v==4?Surface("Warm brick facade",new Color(.90f,.84f,.74f),"brick",3):Mat("Mineral render "+v,palette[v],.2f);
            var trim=Mat("Facade stone trims",new Color(.75f,.72f,.64f),.28f);
            var frame=Mat("Painted joinery "+v,v%2==0?new Color(.25f,.28f,.25f):new Color(.63f,.60f,.51f),.38f);
            var roof=Mat("Slate roof "+v%3,v%3==0?new Color(.25f,.28f,.30f):v%3==1?new Color(.31f,.33f,.34f):new Color(.32f,.23f,.19f),.32f,.12f);
            Box("Masonry",new Vector3(0,height/2,0),new Vector3(width,height,depth),wall,b);
            Box("Dressed stone shop storey",new Vector3(0,1.72f,0),new Vector3(width+.12f,3.44f,depth+.12f),Mat("Shop masonry "+v,palette[v]*.81f),b);
            Box("Foundation plinth",new Vector3(0,.19f,0),new Vector3(width+.22f,.38f,depth+.22f),trim,b);
            Box("Roof cornice lower",new Vector3(0,height-.2f,0),new Vector3(width+.28f,.16f,depth+.28f),trim,b);
            Box("Roof cornice cap",new Vector3(0,height+.02f,0),new Vector3(width+.58f,.2f,depth+.58f),trim,b);
            Roof(b,width,depth,height+.11f,roof,v);
            int adjacent=p.x>0?3:1;
            if(adjacent==front)adjacent=p.z>0?2:0;
            for(int side=0;side<4;side++)
            {
                Transform f=Group("Street elevation",Vector3.zero,side*90,b);
                float w=side%2==0?width:depth,z=(side%2==0?depth:width)/2+.08f;
                bool street=side==front||side==adjacent;
                int cols=Mathf.Max(2,Mathf.FloorToInt(w/2.9f));float step=(w-1.6f)/cols;
                var mortar=Mat("Mortar shadow",new Color(.48f,.46f,.41f));
                for(int course=0;course<7;course++)
                {
                    Box("Ground stone bed joint",new Vector3(0,.47f+course*.42f,z+.013f),new Vector3(w,.018f,.035f),mortar,f);
                    if(street)for(int joint=0;joint<cols;joint++)Box("Stone perp joint",new Vector3((joint-(cols-1)*.5f)*step+(course%2)*.45f,.66f+course*.42f,z+.016f),new Vector3(.015f,.36f,.025f),mortar,f);
                }
                int rows=Mathf.FloorToInt((height-3.65f)/3.05f);
                for(int row=0;row<rows;row++)
                {
                    float y=5.03f+row*3.05f;
                    for(int col=0;col<cols;col++)
                    {
                        float x=(col-(cols-1)*.5f)*step;
                        Window(f,new Vector3(x,y,z),v+row+col,frame,trim,row==0&&street&&col%2==0);
                        if(v%3==1&&street)
                        {
                            var shutter=Mat("Sage shutters",new Color(.28f,.36f,.31f),.23f);
                            for(int end=-1;end<=1;end+=2)
                            {
                                Box("Timber shutter",new Vector3(x+end*.96f,y,z+.08f),new Vector3(.40f,1.94f,.09f),shutter,f);
                                for(int slat=0;slat<8;slat++)Box("Shutter louvre",new Vector3(x+end*.96f,y-.78f+slat*.22f,z+.14f),new Vector3(.37f,.045f,.06f),frame,f);
                            }
                        }
                    }
                    Box("Limestone string course",new Vector3(0,y-1.34f,z+.02f),new Vector3(w+.25f,.12f,.19f),trim,f);
                }
                for(int level=0;level<Mathf.FloorToInt(height/.7f);level++)for(int end=-1;end<=1;end+=2)
                    Box("Corner quoin",new Vector3(end*(w*.5f-.19f),.42f+level*.7f,z),new Vector3(level%2==0?.52f:.34f,.31f,.12f),trim,f);
                Rod("Zinc downpipe",new Vector3(w*.5f-.46f,.3f,z+.13f),new Vector3(w*.5f-.46f,height-.15f,z+.13f),.045f,materials["Charcoal metal"],f);
                if(street)Shop(f,w,z,v);
                else for(int col=0;col<cols;col++)Window(f,new Vector3((col-(cols-1)*.5f)*step,1.8f,z),v+col,frame,trim,false);
            }
        }
        void Window(Transform f,Vector3 p,int variation,Material frame,Material trim,bool balcony)
        {
            var recess=Mat("Window recess",new Color(.075f,.09f,.095f),.05f);
            Color[] colors={new Color(.18f,.25f,.29f),new Color(.23f,.29f,.32f),new Color(.30f,.34f,.33f),new Color(.18f,.21f,.22f),new Color(.35f,.32f,.24f)};
            var glass=Mat("Reflected glazing "+Math.Abs(variation%5),colors[Math.Abs(variation%5)],.92f,.38f);
            Box("Recess shadow",p,new Vector3(1.55f,2.05f,.13f),recess,f);
            Box("Reflective window",p+new Vector3(0,0,.08f),new Vector3(1.32f,1.86f,.025f),glass,f);
            for(int side=-1;side<=1;side+=2)
            {
                Box("Stone jamb",p+new Vector3(side*.79f,0,.05f),new Vector3(.13f,2.14f,.19f),trim,f);
                Box("Window stile",p+new Vector3(side*.63f,0,.11f),new Vector3(.052f,1.86f,.06f),frame,f);
            }
            Box("Lintel",p+new Vector3(0,1.09f,.04f),new Vector3(1.74f,.14f,.2f),trim,f);
            Box("Sill",p+new Vector3(0,-1.08f,.13f),new Vector3(1.8f,.13f,.38f),trim,f);
            Box("Window mullion",p+new Vector3(0,0,.12f),new Vector3(.055f,1.88f,.06f),frame,f);
            Box("Window transom",p+new Vector3(0,.36f,.12f),new Vector3(1.33f,.055f,.06f),frame,f);
            if(variation%4==0)for(int side=-1;side<=1;side+=2)Box("Linen curtain behind glass",p+new Vector3(side*.48f,0,.105f),new Vector3(.18f,1.77f,.015f),Mat("Linen curtains",new Color(.63f,.60f,.49f),.16f),f);
            if(!balcony)return;
            var rail=materials["Charcoal metal"];
            Box("Balcony slab",p+new Vector3(0,-1.07f,.6f),new Vector3(2.02f,.13f,1.24f),trim,f);
            Box("Balcony handrail",p+new Vector3(0,-.14f,1.13f),new Vector3(2,.05f,.05f),rail,f);
            Box("Balcony lower rail",p+new Vector3(0,-.86f,1.13f),new Vector3(2,.035f,.035f),rail,f);
            for(int r=0;r<9;r++)Box("Wrought iron baluster",p+new Vector3(-.92f+r*.23f,-.52f,1.13f),new Vector3(.025f,.77f,.025f),rail,f);
            for(int side=-1;side<=1;side+=2)Box("Balcony return",p+new Vector3(side*.97f,-.14f,.61f),new Vector3(.04f,.04f,1.06f),rail,f);
            if(variation%2==0)
            {
                Box("Window flower box",p+new Vector3(0,-.57f,1.17f),new Vector3(1,.23f,.25f),Mat("Terracotta",new Color(.43f,.24f,.15f)),f);
                for(int i=0;i<6;i++)Shape(PrimitiveType.Sphere,"Balcony geranium",f,p+new Vector3(-.4f+i*.16f,-.38f,1.18f),new Vector3(.16f,.17f,.2f),Mat("Geranium flowers",new Color(.6f,.15f,.15f)));
            }
        }
        void Roof(Transform b,float w,float d,float y,Material mat,int v)
        {
            float h=v%3==2?2.1f:2.8f,inset=v%3==2?d*.46f:2.1f;
            Vector3[] vertices={new Vector3(-w/2-.1f,y,-d/2-.1f),new Vector3(w/2+.1f,y,-d/2-.1f),new Vector3(w/2+.1f,y,d/2+.1f),new Vector3(-w/2-.1f,y,d/2+.1f),new Vector3(-w/2+1.5f,y+h,-d/2+inset),new Vector3(w/2-1.5f,y+h,-d/2+inset),new Vector3(w/2-1.5f,y+h,d/2-inset),new Vector3(-w/2+1.5f,y+h,d/2-inset)};
            MeshObject("Mansard and hipped slate roof",vertices,new[]{0,4,1,1,4,5,1,5,2,2,5,6,2,6,3,3,6,7,3,7,0,0,7,4,4,7,5,5,7,6},mat,b);
            for(float x=-w/2+.8f;x<w/2;x+=1.0f)for(int side=-1;side<=1;side+=2)
                Rod("Standing roof seam",new Vector3(x,y+.025f,side*(d/2+.1f)),new Vector3(Mathf.Clamp(x,-w/2+1.5f,w/2-1.5f),y+h+.025f,side*(d/2-inset)),.012f,materials["Charcoal metal"],b);
            for(int side=-1;side<=1;side+=2)for(int c=-1;c<=1;c+=2)
            {
                var dormer=Group("Slate dormer",new Vector3(c*w*.24f,y+.75f,side*(d*.5f-.55f)),side>0?0:180,b);
                Box("Dormer cheeks",new Vector3(0,.15f,0),new Vector3(1.38f,1.5f,.9f),materials["Facade stone trims"],dormer);
                Box("Dormer glass",new Vector3(0,.15f,.47f),new Vector3(.98f,1.08f,.04f),Mat("Dormer glass",new Color(.2f,.27f,.31f),.85f,.25f),dormer);
                Box("Dormer mullion",new Vector3(0,.15f,.50f),new Vector3(.055f,1.1f,.06f),materials["Charcoal metal"],dormer);
                Box("Dormer cap",new Vector3(0,.94f,.04f),new Vector3(1.57f,.12f,1.1f),mat,dormer);
            }
            Box("Brick chimney",new Vector3(w*.28f,y+h+.47f,d*.11f),new Vector3(.82f,1.48f,.72f),materials["Facade stone trims"],b);
            Box("Chimney crown",new Vector3(w*.28f,y+h+1.23f,d*.11f),new Vector3(.98f,.13f,.89f),mat,b);
            for(int i=0;i<2;i++)Shape(PrimitiveType.Cylinder,"Terracotta chimney pot",b,new Vector3(w*.28f-.2f+i*.4f,y+h+1.45f,d*.11f),new Vector3(.22f,.23f,.22f),Mat("Clay pots",new Color(.40f,.25f,.18f)));
        }
        void Shop(Transform f,float w,float z,int v)
        {
            Color[] colors={new Color(.12f,.25f,.22f),new Color(.26f,.17f,.15f),new Color(.17f,.23f,.29f),new Color(.35f,.29f,.20f)};
            var paint=Mat("Shop paint "+v%4,colors[v%4],.36f);var glass=Mat("Shopfront glass",new Color(.15f,.20f,.20f),.91f,.3f);
            string[] names={"CAFE DES TILLEULS","BOULANGERIE","ATELIER FLORAL","LIBRAIRIE","MAISON & OBJETS","EPICERIE FINE"};float span=w-1.55f;
            Box("Timber shopfront",new Vector3(0,1.7f,z+.075f),new Vector3(span,2.8f,.13f),paint,f);
            Box("Shop glazing",new Vector3(0,1.68f,z+.15f),new Vector3(span-.28f,2.37f,.035f),glass,f);
            for(int n=-1;n<=1;n++)
            {
                float x=n*span*.31f;Box("Shop display mullion",new Vector3(x,1.64f,z+.21f),new Vector3(.10f,2.43f,.095f),paint,f);
                if(n!=0)for(int item=0;item<5;item++)Box("Window display",new Vector3(x*.75f-.6f+item*.31f,1.05f,z+.23f),new Vector3(.22f,.24f+(item%2)*.18f,.1f),Mat("Display paper "+item,new Color(.58f+.05f*item,.46f+.04f*item,.30f+.07f*item)),f);
            }
            Box("Shop door frame",new Vector3(0,1.53f,z+.22f),new Vector3(1.3f,2.48f,.11f),paint,f);
            Box("Door glass",new Vector3(0,1.66f,z+.29f),new Vector3(1.04f,1.9f,.025f),glass,f);
            Rod("Brass door pull",new Vector3(.38f,1.22f,z+.34f),new Vector3(.38f,1.64f,z+.34f),.019f,Mat("Brass hardware",new Color(.48f,.36f,.18f),.68f,.82f),f);
            Box("Store fascia",new Vector3(0,3.18f,z+.20f),new Vector3(span,.48f,.17f),paint,f);
            FittedText("Store name",names[v%names.Length],new Vector3(0,3.18f,z+.30f),f,.21f,new Color(.92f,.87f,.69f),span-.4f);
            var canvas=Mat("Canvas stripe "+v%4,colors[v%4]*1.4f,.08f);var cream=Mat("Canvas cream",new Color(.76f,.72f,.59f),.08f);
            int stripes=Mathf.CeilToInt(span/.45f);float sw=span/stripes;
            for(int i=0;i<stripes;i++)
            {
                float x=-span/2+sw*(i+.5f);var material=i%2==0?canvas:cream;
                Box("Canvas awning stripe",new Vector3(x,2.92f,z+.67f),new Vector3(sw+.006f,.06f,1.15f),material,f,new Vector3(12,0,0));
                Box("Awning valance",new Vector3(x,2.72f,z+1.24f),new Vector3(sw+.006f,.22f,.045f),material,f);
            }
            FittedText("Address number",(12+v*3).ToString(),new Vector3(w/2-.58f,2.55f,z+.10f),f,.13f,new Color(.18f,.22f,.23f),.32f);
        }
        static void FittedText(string name,string value,Vector3 p,Transform parent,float height,Color color,float maxWidth,bool forward=false)
        {
            var o=new GameObject(name);o.transform.SetParent(parent,false);o.transform.localPosition=p;o.transform.localRotation=Quaternion.Euler(0,forward?0:180,0);
            var font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");var text=o.AddComponent<TextMesh>();text.font=font;text.text=value;text.fontSize=64;text.anchor=TextAnchor.MiddleCenter;text.alignment=TextAlignment.Center;text.color=color;
            font.RequestCharactersInTexture(value,64,FontStyle.Normal);float maxAdvance=0,lineAdvance=0;
            foreach(char c in value){if(c=='\n'){maxAdvance=Mathf.Max(maxAdvance,lineAdvance);lineAdvance=0;continue;}if(font.GetCharacterInfo(c,out CharacterInfo info,64,FontStyle.Normal))lineAdvance+=info.advance;}
            maxAdvance=Mathf.Max(maxAdvance,lineAdvance);text.characterSize=Mathf.Min(height*10/64,maxAdvance>0?maxWidth*10/maxAdvance:height*10/64);
            var renderer=o.GetComponent<MeshRenderer>();renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
            WorldTextMaterial.Bind(text);
        }

        void Tree(Vector3 p,int variation)
        {
            int variant=Math.Abs(variation%3);var trunk=Mat("Tree bark",new Color(.25f,.21f,.15f),.16f);
            Rod("Lower tree trunk",p,p+new Vector3(.07f,2.6f,.03f),.15f,trunk);
            Rod("Upper tree trunk",p+new Vector3(.07f,2.2f,.03f),p+new Vector3(-.1f,4.6f,.07f),.095f,trunk);
            var greens=new[]{Mat("Leaf sun "+variant,new Color(.25f+.025f*variant,.35f+.02f*variant,.12f),.16f),Mat("Leaf mid "+variant,new Color(.18f+.025f*variant,.29f+.02f*variant,.10f),.14f),Mat("Leaf shade "+variant,new Color(.12f+.02f*variant,.23f+.02f*variant,.085f),.12f)};
            foreach(var green in greens)green.SetFloat("_Cull",0);
            for(int branch=0;branch<9;branch++)
            {
                float angle=branch*2.4f;Vector3 a=p+new Vector3(0,2.4f+branch*.17f,0),end=p+new Vector3(Mathf.Cos(angle)*1.65f,4.2f+branch*.1f,Mathf.Sin(angle)*1.65f);
                Rod("Tree branch",a,end,.052f,trunk);Rod("Branch fork",Vector3.Lerp(a,end,.62f),end+new Vector3(.38f,.55f,-.28f),.021f,trunk);
            }
            for(int layer=0;layer<3;layer++)
            {
                var vertices=new List<Vector3>();var triangles=new List<int>();
                for(int i=0;i<520;i++)
                {
                    Vector3 r=new Vector3((float)random.NextDouble()*2-1,(float)random.NextDouble()*2-1,(float)random.NextDouble()*2-1);
                    if(r.sqrMagnitude>1){i--;continue;}float ripple=1+.16f*Mathf.Sin(r.x*8+r.z*6);
                    Vector3 center=p+new Vector3(r.x*2.03f*ripple,4.95f+r.y*1.85f,r.z*1.86f*ripple);
                    Quaternion rotation=Quaternion.Euler((float)random.NextDouble()*145-72,(float)random.NextDouble()*360,(float)random.NextDouble()*145-72);
                    float length=.18f+(float)random.NextDouble()*.15f;int a=vertices.Count;
                    vertices.Add(center+rotation*new Vector3(0,0,-length));vertices.Add(center+rotation*new Vector3(-length*.52f,0,0));
                    vertices.Add(center+rotation*new Vector3(0,.05f,length));vertices.Add(center+rotation*new Vector3(length*.52f,0,0));triangles.AddRange(new[]{a,a+1,a+2,a,a+2,a+3});
                }
                MeshObject("Individual leaf crown",vertices.ToArray(),triangles.ToArray(),greens[layer],Root);
            }
            for(int i=-5;i<=5;i++)if(Mathf.Abs(i)>1)Box("Tree grate slot",p+new Vector3(i*.12f,.013f,0),new Vector3(.04f,.025f,1.3f),materials["Charcoal metal"]);
        }

        void StreetDetails()
        {
            var metal=materials["Charcoal metal"];var white=materials["Road paint"];
            for(int sx=-1;sx<=1;sx+=2)for(int sz=-1;sz<=1;sz+=2)
            {
                for(int n=0;n<3;n++)
                {
                    float d=29+n*7;ParkedCar(new Vector3(sx*5.74f,.01f,sz*d),sx>0?0:180,n+sx+sz);
                    Box("Parking bay edge",new Vector3(sx*4.6f,.016f,sz*d),new Vector3(.075f,.014f,5.7f),white);
                    Box("Parking bay end",new Vector3(sx*5.65f,.016f,sz*(d+3)),new Vector3(2.2f,.014f,.075f),white);
                }
                SpeedSign(new Vector3(sx*8.1f,.13f,sz*22),sx>0?180:0);
                BikeRack(new Vector3(sx*13.7f,.13f,sz*34.3f),sx>0?90:-90);
                Bin(new Vector3(sx*13.45f,.13f,sz*19.5f));
                for(int d=18;d<70;d+=15)
                {
                    Drain(new Vector3(sx*6.78f,.015f,sz*d),0);Drain(new Vector3(sx*d,.015f,sz*6.78f),90);
                }
                if(sx==sz)Cafe(new Vector3(sx*24,.13f,sz*13.8f),sz>0?180:0);
            }
            BusShelter(new Vector3(-13.7f,.13f,46),90);
        }
        void Drain(Vector3 p,float angle)
        {
            var t=Group("Kerbside drain",p,angle);Box("Drain recess",Vector3.zero,new Vector3(.38f,.018f,.7f),Mat("Drain shadow",new Color(.08f,.08f,.072f)),t);
            for(int i=0;i<8;i++)Box("Drain iron slat",new Vector3(0,.011f,-.30f+i*.087f),new Vector3(.36f,.015f,.035f),materials["Charcoal metal"],t);
        }
        void Bin(Vector3 p)
        {
            var t=Group("Municipal litter bin",p);var metal=materials["Charcoal metal"];
            Shape(PrimitiveType.Cylinder,"Bin interior",t,new Vector3(0,.5f,0),new Vector3(.4f,.42f,.4f),Mat("Bin interior",new Color(.08f,.09f,.08f)));
            for(int i=0;i<16;i++){float a=i*Mathf.PI/8;Rod("Bin vertical rib",new Vector3(Mathf.Cos(a)*.23f,.12f,Mathf.Sin(a)*.23f),new Vector3(Mathf.Cos(a)*.23f,.93f,Mathf.Sin(a)*.23f),.014f,metal,t);}
            Shape(PrimitiveType.Cylinder,"Bin lid",t,new Vector3(0,.96f,0),new Vector3(.53f,.035f,.53f),metal);
        }
        void BevelBody(Transform parent,string name,Vector3 p,Vector3 size,float bevel,Material mat)
        {
            float x=size.x*.5f,y=size.y*.5f,z=size.z*.5f;
            var v=new[]{new Vector3(-x,-y,-z),new Vector3(x,-y,-z),new Vector3(x,-y,z),new Vector3(-x,-y,z),new Vector3(-x+bevel,y,-z+bevel),new Vector3(x-bevel,y,-z+bevel),new Vector3(x-bevel,y,z-bevel),new Vector3(-x+bevel,y,z-bevel)};
            for(int i=0;i<v.Length;i++)v[i]+=p;MeshObject(name,v,new[]{0,4,1,1,4,5,1,5,2,2,5,6,2,6,3,3,6,7,3,7,0,0,7,4,4,7,5,5,7,6,0,1,3,1,2,3},mat,parent);
        }
        void ParkedCar(Vector3 p,float angle,int variant)
        {
            var t=Group("Unoccupied parked vehicle",p,angle);int v=Math.Abs(variant%5);
            Color[] colors={new Color(.34f,.39f,.42f),new Color(.53f,.54f,.51f),new Color(.29f,.13f,.11f),new Color(.14f,.20f,.27f),new Color(.65f,.62f,.54f)};
            var paint=Mat("Parked paint "+v,colors[v],.78f,.48f);var glass=Mat("Parked glass",new Color(.105f,.16f,.20f),.93f,.42f);var rubber=Mat("Parking rubber",new Color(.035f,.038f,.043f),.16f);
            BevelBody(t,"Parked compact body",new Vector3(0,.64f,0),new Vector3(1.78f,.70f,4.15f),.15f,paint);
            BevelBody(t,"Parked compact cabin",new Vector3(0,1.18f,-.12f),new Vector3(1.52f,.73f,2.25f),.25f,glass);
            Box("Painted roof",new Vector3(0,1.59f,-.15f),new Vector3(1.24f,.08f,1.52f),paint,t);
            Box("Bonnet",new Vector3(0,1.01f,1.34f),new Vector3(1.6f,.085f,1.22f),paint,t,new Vector3(4,0,0));
            for(int side=-1;side<=1;side+=2)
            {
                Box("Door pillar",new Vector3(side*.765f,1.21f,-.12f),new Vector3(.04f,.61f,.10f),paint,t);
                Box("Side mirror",new Vector3(side*.96f,1.17f,.65f),new Vector3(.2f,.16f,.24f),paint,t);
                for(int wheel=-1;wheel<=1;wheel+=2)
                {
                    Shape(PrimitiveType.Cylinder,"Parked tire",t,new Vector3(side*.87f,.37f,wheel*1.32f),new Vector3(.65f,.105f,.65f),rubber,new Vector3(0,0,90));
                    Shape(PrimitiveType.Cylinder,"Parked alloy wheel",t,new Vector3(side*.983f,.37f,wheel*1.32f),new Vector3(.41f,.008f,.41f),Mat("Wheel alloy",new Color(.53f,.55f,.54f),.7f,.85f),new Vector3(0,0,90));
                }
                Box("Front headlight",new Vector3(side*.61f,.76f,2.075f),new Vector3(.42f,.16f,.035f),Mat("Headlight glass",new Color(.83f,.86f,.80f),.8f),t);
                Box("Rear lamp",new Vector3(side*.65f,.79f,-2.075f),new Vector3(.28f,.18f,.035f),Mat("Red lamp lens",new Color(.48f,.035f,.02f),.8f),t);
            }
            Box("Front grille",new Vector3(0,.56f,2.085f),new Vector3(.83f,.19f,.03f),rubber,t);
            Box("EU registration",new Vector3(0,.79f,2.095f),new Vector3(.46f,.10f,.025f),Mat("Number plate",new Color(.86f,.87f,.82f)),t);
            Box("EU plate band",new Vector3(-.21f,.79f,2.115f),new Vector3(.045f,.10f,.013f),Mat("EU blue",new Color(.03f,.14f,.42f)),t);
        }
        void BikeRack(Vector3 p,float angle)
        {
            var t=Group("Cycle parking",p,angle);var iron=materials["Charcoal metal"];
            for(int i=0;i<3;i++)
            {
                float x=(i-1)*.75f;Rod("Bike stand",new Vector3(x,0,-.45f),new Vector3(x,.77f,-.45f),.024f,iron,t);
                Rod("Bike stand",new Vector3(x,.77f,-.45f),new Vector3(x,.77f,.45f),.024f,iron,t);Rod("Bike stand",new Vector3(x,.77f,.45f),new Vector3(x,0,.45f),.024f,iron,t);
                if(i!=1)Bicycle(new Vector3(x+.12f,0,0),t,i);
            }
        }
        void Bicycle(Vector3 p,Transform parent,int variant)
        {
            var t=Group("Parked city bicycle",p,0,parent);var frame=Mat("Bicycle frame "+variant,variant==0?new Color(.24f,.37f,.36f):new Color(.49f,.25f,.16f),.58f,.6f);var iron=materials["Charcoal metal"];
            Vector3 rear=new Vector3(0,.35f,-.58f),front=new Vector3(0,.35f,.58f),crank=new Vector3(0,.34f,-.12f),seat=new Vector3(0,.87f,-.25f),head=new Vector3(0,.85f,.39f);
            foreach(Vector3 c in new[]{rear,front})for(int seg=0;seg<20;seg++)
            {
                float a=seg*Mathf.PI/10,b=(seg+1)*Mathf.PI/10;
                Rod("Bicycle tire",c+new Vector3(0,Mathf.Sin(a)*.33f,Mathf.Cos(a)*.33f),c+new Vector3(0,Mathf.Sin(b)*.33f,Mathf.Cos(b)*.33f),.024f,materials["Parking rubber"],t);
                if(seg%2==0)Rod("Bicycle spoke",c,c+new Vector3(0,Mathf.Sin(a)*.30f,Mathf.Cos(a)*.30f),.005f,iron,t);
            }
            foreach(var pair in new[]{new[]{rear,seat},new[]{rear,crank},new[]{crank,seat},new[]{crank,head},new[]{seat,head},new[]{head,front}})Rod("Bicycle tube",pair[0],pair[1],.020f,frame,t);
            Rod("Handlebar stem",head,new Vector3(0,1.04f,.36f),.018f,iron,t);Rod("Handlebar",new Vector3(-.23f,1.04f,.36f),new Vector3(.23f,1.04f,.36f),.018f,iron,t);
            Box("Bicycle saddle",seat+new Vector3(0,.10f,-.02f),new Vector3(.18f,.06f,.28f),materials["Parking rubber"],t);
            Box("Rear bicycle rack",new Vector3(0,.80f,-.65f),new Vector3(.23f,.035f,.45f),iron,t);
        }
        void Cafe(Vector3 p,float angle)
        {
            var t=Group("Pavement cafe terrace outside walking routes",p,angle);var iron=materials["Charcoal metal"];var cane=Mat("Cafe woven cane",new Color(.57f,.43f,.25f),.21f);
            for(int i=-1;i<=1;i++)
            {
                var table=Group("Bistro table",new Vector3(i*2.35f,0,0),0,t);
                Shape(PrimitiveType.Cylinder,"Marble table top",table,new Vector3(0,.74f,0),new Vector3(.70f,.035f,.70f),Mat("Cafe marble",new Color(.71f,.69f,.62f),.55f));
                Rod("Bistro pedestal",new Vector3(0,.06f,0),new Vector3(0,.72f,0),.035f,iron,table);
                for(int side=-1;side<=1;side+=2)
                {
                    Rod("Table foot",new Vector3(side*.28f,.05f,0),new Vector3(0,.20f,0),.025f,iron,table);
                    var chair=Group("Woven bistro chair",new Vector3(side*.76f,0,0),side>0?90:-90,table);
                    Box("Woven seat",new Vector3(0,.46f,0),new Vector3(.40f,.045f,.39f),cane,chair);
                    Box("Woven chair back",new Vector3(0,.76f,-.18f),new Vector3(.40f,.40f,.04f),cane,chair,new Vector3(-7,0,0));
                    for(int x=-1;x<=1;x+=2)for(int z=-1;z<=1;z+=2)Rod("Chair tubular leg",new Vector3(x*.19f,.02f,z*.18f),new Vector3(x*.17f,.48f,z*.16f),.016f,iron,chair);
                }
                Shape(PrimitiveType.Cylinder,"Cafe cup",table,new Vector3(.16f,.81f,.1f),new Vector3(.07f,.055f,.07f),Mat("Porcelain",new Color(.86f,.85f,.78f),.65f));
            }
            var board=Group("Cafe menu board",new Vector3(4.1f,0,0),-12,t);
            Box("Menu timber frame",new Vector3(0,.65f,0),new Vector3(.65f,1.05f,.075f),Mat("Cafe timber",new Color(.35f,.20f,.105f)),board,new Vector3(-8,0,0));
            Box("Chalkboard",new Vector3(0,.67f,.046f),new Vector3(.55f,.86f,.015f),Mat("Chalkboard",new Color(.065f,.10f,.08f)),board,new Vector3(-8,0,0));
            FittedText("Menu lettering","CAFE\nDU JOUR\n\nESPRESSO\nCROISSANT",new Vector3(0,.76f,.14f),board,.08f,new Color(.82f,.81f,.69f),.48f);
        }
        void BusShelter(Vector3 p,float angle)
        {
            var t=Group("Glazed bus shelter",p,angle);var metal=materials["Charcoal metal"];var glass=Mat("Shelter tinted glazing",new Color(.30f,.40f,.41f),.94f,.25f);
            for(int side=-1;side<=1;side+=2)Rod("Shelter column",new Vector3(side*1.6f,0,.38f),new Vector3(side*1.6f,2.7f,.38f),.04f,metal,t);
            Box("Shelter canopy",new Vector3(0,2.69f,0),new Vector3(3.7f,.12f,1.46f),metal,t);
            Box("Shelter back glass",new Vector3(0,1.46f,.47f),new Vector3(3.23f,2.2f,.025f),glass,t);
            Box("Shelter glass safety stripe",new Vector3(0,1.35f,.49f),new Vector3(3.2f,.045f,.016f),Mat("Glass safety stripe",new Color(.74f,.77f,.72f)),t);
            Box("Shelter seat",new Vector3(0,.49f,.06f),new Vector3(2.4f,.07f,.40f),Mat("Shelter timber",new Color(.39f,.27f,.15f)),t);
            Box("Bus stop display",new Vector3(1.43f,2.34f,-.07f),new Vector3(.48f,.50f,.09f),Mat("Transit blue",new Color(.10f,.23f,.32f)),t);
            FittedText("Bus stop number","24",new Vector3(1.43f,2.34f,-.13f),t,.22f,Color.white,.36f,true);
            Box("Route map frame",new Vector3(-1.25f,1.65f,.40f),new Vector3(.45f,.70f,.08f),Mat("Timetable paper",new Color(.77f,.76f,.67f)),t);
            for(int i=0;i<7;i++)Box("Timetable route row",new Vector3(-1.25f,1.9f-i*.07f,.35f),new Vector3(.32f,.01f,.02f),metal,t);
        }
        void SpeedSign(Vector3 p,float angle)
        {
            var t=Group("30 km/h neighbourhood sign",p,angle);Rod("Sign pole",Vector3.zero,new Vector3(0,2.85f,0),.032f,materials["Charcoal metal"],t);
            Shape(PrimitiveType.Cylinder,"Red speed-sign ring",t,new Vector3(0,2.61f,0),new Vector3(.63f,.032f,.63f),Mat("Traffic sign red",new Color(.65f,.04f,.025f),.35f),new Vector3(90,0,0));
            Shape(PrimitiveType.Cylinder,"White sign face",t,new Vector3(0,2.61f,-.035f),new Vector3(.49f,.012f,.49f),Mat("Reflective sign white",new Color(.9f,.89f,.82f),.42f),new Vector3(90,0,0));
            FittedText("Speed limit","30",new Vector3(0,2.61f,-.053f),t,.24f,new Color(.07f,.075f,.075f),.38f,true);
        }
        void EnhanceLighting()
        {
            Sun.shadowBias=.035f;Sun.shadowNormalBias=.16f;
            RenderSettings.defaultReflectionMode=DefaultReflectionMode.Skybox;RenderSettings.reflectionIntensity=.8f;
            // UrbanRenderQuality owns the single HDR reflection probe and its refresh after lighting changes.
            ApplyLightingPreset(0);
        }
        /// <summary>0: bright afternoon; 1: warm low sun; 2: soft overcast. Camera settings are owned by CameraRig.</summary>
        public void ApplyLightingPreset(int preset)
        {
            if(Sun==null)return;
            RenderSettings.ambientMode=AmbientMode.Trilight;RenderSettings.fog=true;RenderSettings.fogMode=FogMode.ExponentialSquared;
            if(preset==1)
            {
                Sun.color=new Color(1,.78f,.52f);Sun.intensity=1.9f;Sun.transform.rotation=Quaternion.Euler(23,-42,0);
                RenderSettings.ambientSkyColor=new Color(.53f,.61f,.74f);RenderSettings.ambientEquatorColor=new Color(.45f,.45f,.42f);RenderSettings.ambientGroundColor=new Color(.22f,.21f,.19f);
                RenderSettings.fogColor=new Color(.71f,.65f,.57f);RenderSettings.fogDensity=.0036f;
            }
            else if(preset==2)
            {
                Sun.color=new Color(.85f,.91f,1);Sun.intensity=.95f;Sun.transform.rotation=Quaternion.Euler(54,-32,0);
                RenderSettings.ambientSkyColor=new Color(.64f,.70f,.74f);RenderSettings.ambientEquatorColor=new Color(.54f,.58f,.60f);RenderSettings.ambientGroundColor=new Color(.30f,.32f,.32f);
                RenderSettings.fogColor=new Color(.64f,.70f,.73f);RenderSettings.fogDensity=.0048f;
            }
            else
            {
                Sun.color=new Color(1,.95f,.84f);Sun.intensity=1.75f;Sun.transform.rotation=Quaternion.Euler(47,-38,0);
                RenderSettings.ambientSkyColor=new Color(.52f,.64f,.79f);RenderSettings.ambientEquatorColor=new Color(.49f,.54f,.57f);RenderSettings.ambientGroundColor=new Color(.25f,.27f,.26f);
                RenderSettings.fogColor=new Color(.66f,.75f,.82f);RenderSettings.fogDensity=.0029f;
            }
            Sun.shadowStrength=preset==2?.43f:.88f;
        }
    }
}
