using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StationFlow.SyntheticTraffic
{
    /// <summary>Metre-scale, right-hand European junction. Original geometry plus credited CC0 surfaces.</summary>
    public sealed partial class WorldBuilder
    {
        public Transform Root { get; private set; }
        readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
        static readonly Dictionary<Material,float> tileMetres = new Dictionary<Material,float>();
        readonly List<SignalVisual> signals = new List<SignalVisual>();
        System.Random random = new System.Random(73);
        public Light Sun { get; private set; }

        public Material Mat(string name, Color color, float smoothness = .25f, float metallic = 0)
        {
            if (materials.TryGetValue(name, out Material result)) return result;
            result = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            result.SetColor("_BaseColor", color); result.SetFloat("_Smoothness", smoothness); result.SetFloat("_Metallic", metallic);
            result.enableInstancing = true; materials.Add(name, result); return result;
        }

        Material Surface(string name, Color color, string asset, float repeat)
        {
            if(materials.TryGetValue(name,out Material cached))return cached;
            Material m = Mat(name, color);
            Texture2D tex = Resources.Load<Texture2D>("StationFlowArt/" + asset + "_diffuse");
            if (tex != null) { m.SetTexture("_BaseMap", tex); m.SetTextureScale("_BaseMap", Vector2.one); tileMetres[m]=asset=="asphalt"?18:asset=="pavement"?2.8f:3.3f; }
            Texture2D normal = Resources.Load<Texture2D>("StationFlowArt/" + asset + "_normal");
            if (normal != null) { m.SetTexture("_BumpMap", normal); m.SetFloat("_BumpScale", .65f); m.EnableKeyword("_NORMALMAP"); }
            ApplyRoughness(m,asset);
            return m;
        }

        public static GameObject Shape(PrimitiveType type, string name, Transform parent, Vector3 position, Vector3 scale, Material material, Vector3 rotation = default)
        {
            GameObject o = GameObject.CreatePrimitive(type); o.name = name; o.transform.SetParent(parent, false);
            o.transform.localPosition = position; o.transform.localScale = scale; o.transform.localEulerAngles = rotation;
            o.GetComponent<Renderer>().sharedMaterial = material;
            if(type==PrimitiveType.Cube&&tileMetres.TryGetValue(material,out float metres))
            {
                Mesh mesh=UnityEngine.Object.Instantiate(o.GetComponent<MeshFilter>().sharedMesh);Vector3[] vertices=mesh.vertices,normals=mesh.normals;Vector2[] uv=new Vector2[vertices.Length];
                for(int i=0;i<vertices.Length;i++)
                {
                    Vector3 v=Vector3.Scale(vertices[i],scale),n=normals[i];
                    uv[i]=Mathf.Abs(n.y)>.5f?new Vector2(v.x,v.z)/metres:Mathf.Abs(n.x)>.5f?new Vector2(v.z,v.y)/metres:new Vector2(v.x,v.y)/metres;
                }
                mesh.uv=uv;mesh.RecalculateTangents();o.GetComponent<MeshFilter>().sharedMesh=mesh;
            }
            Collider collider = o.GetComponent<Collider>(); collider.enabled = false;
            if (Application.isPlaying) UnityEngine.Object.Destroy(collider); else UnityEngine.Object.DestroyImmediate(collider);
            return o;
        }
        GameObject Box(string name, Vector3 p, Vector3 size, Material m, Transform parent = null, Vector3 rotation = default)
            => Shape(PrimitiveType.Cube, name, parent ?? Root, p, size, m, rotation);

        public void Build()
        {
            Root = new GameObject("EUROPEAN QUARTER • original procedural architecture").transform;
            var asphalt = Surface("Asphalt", new Color(.78f,.79f,.80f), "asphalt", 10);
            var pavement = Surface("Limestone pavement", new Color(.92f,.89f,.81f), "pavement", 12);
            var curb = Mat("Cut stone curb", new Color(.60f,.61f,.57f));
            var white = Mat("Road paint", new Color(.88f,.88f,.80f));
            var yellow = Mat("Parking paint", new Color(.85f,.68f,.24f));
            var metal = Mat("Charcoal metal", new Color(.075f,.095f,.11f), .55f, .65f);
            Box("Earth", new Vector3(0,-.35f,0), new Vector3(1800,.5f,1800), Mat("Earth",new Color(.28f,.33f,.28f)));
            Box("Road N-S", new Vector3(0,-.05f,0),new Vector3(14,.1f,1600),asphalt);
            Box("Road E-W", new Vector3(0,-.049f,0),new Vector3(1600,.1f,14),asphalt);
            for(int sx=-1;sx<=1;sx+=2) for(int sz=-1;sz<=1;sz+=2)
            {
                Box("Raised pavement", new Vector3(sx*403.5f,.08f,sz*403.5f), new Vector3(793,.16f,793),pavement);
                DetailedCurbs(sx,sz,curb);
                for(int b=0;b<3;b++)
                {
                    Building(new Vector3(sx*(23+b*18),.13f,sz*24), 15, 18, 13+(b%3)*3, sz<0 ? 0 : 2, b+sz+sx);
                    if(b>0) Building(new Vector3(sx*24,.13f,sz*(24+b*20)),18,17,13+b*3,sx<0 ? 1 : 3,b+2);
                }
                for(int j=0;j<4;j++)
                {
                    float n=18+j*16;
                    Tree(new Vector3(sx*13.4f,.13f,sz*(n+8)),j+sx+sz);
                    Tree(new Vector3(sx*(n+8),.13f,sz*13.4f),j+2);
                    Lamp(new Vector3(sx*8.5f,.13f,sz*(21+j*17)),metal);
                    Lamp(new Vector3(sx*(21+j*17),.13f,sz*8.5f),metal);
                }
                Bench(new Vector3(sx*13.5f,.13f,sz*22), sx>0 ? 90 : -90);
                Box("Planter",new Vector3(sx*13.5f,.45f,sz*15.8f),new Vector3(1.4f,.65f,.85f),curb);
                Box("Shrub hedge",new Vector3(sx*13.5f,.97f,sz*15.8f),new Vector3(1.3f,.7f,.75f),Mat("Hedge",new Color(.19f,.28f,.14f)));
                for(int n=0;n<4;n+=3)
                    Shape(PrimitiveType.Cylinder,"Bollard",Root,new Vector3(sx*(8.2f+n*1.15f),.55f,sz*8.1f),new Vector3(.11f,.42f,.11f),metal);
                Signal(new Vector3(sx*7.8f,.13f,sz*12.8f), sz<0?0:180, true);
                Signal(new Vector3(sx*12.8f,.13f,sz*7.8f), sx<0?90:270, false);
            }
            for(int sign=-1;sign<=1;sign+=2)
            {
                for(int i=-6;i<=6;i++)
                {
                    Box("Zebra N-S",new Vector3(i,.012f,sign*10),new Vector3(.48f,.02f,2.4f),white);
                    Box("Zebra E-W",new Vector3(sign*10,.012f,i),new Vector3(2.4f,.02f,.48f),white);
                }
                // Stop bars leave the complete pedestrian crossing clear.
                Box("Stop bar N-S",new Vector3(sign>0?-3.5f:3.5f,.013f,sign*15),new Vector3(6.4f,.025f,.3f),white);
                Box("Stop bar E-W",new Vector3(sign*15,.013f,sign>0?3.5f:-3.5f),new Vector3(.3f,.025f,6.4f),white);
                for(int d=18;d<320;d+=6)
                {
                    Box("Centre dash N-S",new Vector3(0,.012f,sign*d),new Vector3(.12f,.02f,3),white);
                    Box("Centre dash E-W",new Vector3(sign*d,.012f,0),new Vector3(3,.02f,.12f),white);
                }
                for(int d=24;d<75;d+=22)
                {
                    Arrow(new Vector3(sign>0?-3.2f:3.2f,.018f,sign*d),sign>0?180:0,white);
                    Arrow(new Vector3(sign*d,.018f,sign>0?3.2f:-3.2f),sign>0?270:90,white);
                }
                StreetSign(new Vector3(sign*8.1f,3.0f,-sign*8.1f),"RUE DES TILLEULS",metal);
            }
            StreetDetails();
            ExtendStreetFronts();
            Lighting();
            EnhanceLighting();
            // Original repeated architectural pieces are combined per material to keep draw calls bounded.
            CombineStaticGeometry();
        }

        void Arrow(Vector3 p,float heading,Material mat)
        {
            var g=new GameObject("Straight and right lane marking").transform;g.SetParent(Root);g.position=p;g.rotation=Quaternion.Euler(0,heading,0);
            Box("Stem",new Vector3(0,0,0),new Vector3(.18f,.018f,1.8f),mat,g);
            Box("Arrow left",new Vector3(-.24f,0,.63f),new Vector3(.16f,.018f,.8f),mat,g,new Vector3(0,-38,0));
            Box("Arrow right",new Vector3(.24f,0,.63f),new Vector3(.16f,.018f,.8f),mat,g,new Vector3(0,38,0));
            Box("Right-turn arrow branch",new Vector3(.45f,0,-.2f),new Vector3(.9f,.018f,.15f),mat,g);
            Box("Right-turn arrow tip upper",new Vector3(.75f,0,-.02f),new Vector3(.5f,.018f,.13f),mat,g,new Vector3(0,42,0));
            Box("Right-turn arrow tip lower",new Vector3(.75f,0,-.38f),new Vector3(.5f,.018f,.13f),mat,g,new Vector3(0,-42,0));
        }

        void LegacyBuilding(Vector3 p,float width,float depth,float height,int front,int variant)
        {
            Transform b=new GameObject("Townhouse block").transform;b.SetParent(Root);b.localPosition=p;
            Color[] palette={new Color(.74f,.65f,.51f),new Color(.72f,.74f,.70f),new Color(.62f,.43f,.32f),new Color(.76f,.72f,.61f),new Color(.62f,.65f,.63f)};
            int v=Math.Abs(variant)%palette.Length;
            var wall=v==2 ? Surface("Brick facade",Color.white,"brick",4):Mat("Facade "+v,palette[v]);
            var trim=Mat("Facade stone trims",new Color(.73f,.73f,.68f));
            var glass=Mat("Dark architectural glass",new Color(.12f,.20f,.24f),.86f,.25f);
            var frame=Mat("Window frames",new Color(.30f,.33f,.31f),.35f);
            Box("Masonry",new Vector3(0,height/2,0),new Vector3(width,height,depth),wall,b);
            Box("Foundation plinth",new Vector3(0,.33f,0),new Vector3(width+.16f,.66f,depth+.16f),trim,b);
            Box("Roof cornice",new Vector3(0,height+.08f,0),new Vector3(width+.5f,.3f,depth+.5f),trim,b);
            Box("Roof",new Vector3(0,height+.26f,0),new Vector3(width,.2f,depth),Mat("Roof zinc",new Color(.23f,.27f,.28f),.4f,.3f),b);
            for(int side=0;side<4;side++)
            {
                Transform f=new GameObject("Facade").transform;f.SetParent(b,false);f.localRotation=Quaternion.Euler(0,side*90,0);
                float w=(side%2==0)?width:depth;float z=((side%2==0)?depth:width)/2+.04f;
                int cols=Mathf.FloorToInt(w/3.1f);
                for(int row=0;row<(int)(height/3.1f);row++)
                {
                    float y=1.8f+row*3.05f;
                    for(int col=0;col<cols;col++)
                    {
                        float x=(col-(cols-1)/2f)*3.0f;
                        Box("Window surround",new Vector3(x,y,z),new Vector3(1.62f,2.04f,.14f),trim,f);
                        Box("Glazing",new Vector3(x,y,z+.08f),new Vector3(1.32f,1.73f,.06f),glass,f);
                        Box("Mullion",new Vector3(x,y,z+.13f),new Vector3(.055f,1.73f,.065f),frame,f);
                        Box("Transom",new Vector3(x,y+.1f,z+.13f),new Vector3(1.35f,.055f,.065f),frame,f);
                        Box("Sill",new Vector3(x,y-1.03f,z+.1f),new Vector3(1.84f,.12f,.35f),trim,f);
                        if(row==1 && col%2==0)
                        {
                            Box("Balcony",new Vector3(x,y-.85f,z+.65f),new Vector3(2.1f,.12f,1.3f),trim,f);
                            var rail=materials["Charcoal metal"];
                            Box("Handrail",new Vector3(x,y-.05f,z+1.2f),new Vector3(2.1f,.05f,.05f),rail,f);
                            for(int r=0;r<7;r++)Box("Baluster",new Vector3(x-.9f+r*.3f,y-.45f,z+1.2f),new Vector3(.035f,.8f,.035f),rail,f);
                        }
                    }
                    if(row>0)Box("String course",new Vector3(0,y-1.35f,z),new Vector3(w+.2f,.12f,.18f),trim,f);
                }
                if(side==front)
                {
                    var awning=Mat("Awning "+v,v%2==0?new Color(.14f,.29f,.27f):new Color(.42f,.19f,.13f));
                    Box("Shop canopy",new Vector3(0,3.1f,z+.65f),new Vector3(w-.8f,.14f,1.5f),awning,f,new Vector3(-10,0,0));
                    Box("Store sign",new Vector3(0,3.48f,z+.16f),new Vector3(w-.9f,.58f,.14f),awning,f);
                    Text("Store name",v%2==0?"ATELIER  /  CAFE":"MARCHE DU QUARTIER",new Vector3(0,3.48f,z+.25f),f,.22f,Color.white);
                }
            }
            Box("Chimney",new Vector3(width*.28f,height+.8f,depth*.22f),new Vector3(1.0f,1.4f,.9f),trim,b);
        }

        void LegacyTree(Vector3 p,int variation)
        {
            var trunk=Mat("Tree bark",new Color(.26f,.20f,.15f));var green=Mat("Foliage "+Math.Abs(variation%3),new Color(.25f+.03f*(variation%3),.36f+.035f*(variation%3),.15f));
            green.SetFloat("_Cull",0);
            Shape(PrimitiveType.Cylinder,"Tree trunk",Root,p+Vector3.up*1.9f,new Vector3(.23f,1.9f,.23f),trunk);
            // Each crown is an original cloud of two-sided bent leaf polygons, casting dappled shadows.
            var vertices=new List<Vector3>();var triangles=new List<int>();
            for(int i=0;i<900;i++)
            {
                Vector3 r=new Vector3((float)random.NextDouble()*2-1,(float)random.NextDouble()*2-1,(float)random.NextDouble()*2-1);
                if(r.sqrMagnitude>1){i--;continue;}
                Vector3 center=p+new Vector3(r.x*2.0f,4.8f+r.y*1.8f,r.z*1.85f);
                Quaternion rotation=Quaternion.Euler((float)random.NextDouble()*130-65,(float)random.NextDouble()*360,(float)random.NextDouble()*130-65);
                float length=.30f+(float)random.NextDouble()*.25f;int a=vertices.Count;
                vertices.Add(center+rotation*new Vector3(0,0,-length));vertices.Add(center+rotation*new Vector3(-length*.42f,0,0));
                vertices.Add(center+rotation*new Vector3(0,.05f,length));vertices.Add(center+rotation*new Vector3(length*.42f,0,0));
                triangles.AddRange(new[]{a,a+1,a+2,a,a+2,a+3});
            }
            var mesh=new Mesh{name="Leaf crown"};mesh.SetVertices(vertices);mesh.SetTriangles(triangles,0);mesh.RecalculateNormals();mesh.RecalculateBounds();
            var foliage=new GameObject("Leaf crown");foliage.transform.SetParent(Root,false);foliage.AddComponent<MeshFilter>().sharedMesh=mesh;foliage.AddComponent<MeshRenderer>().sharedMaterial=green;
            Box("Tree grate",p+new Vector3(0,.01f,0),new Vector3(1.55f,.02f,1.55f),materials["Charcoal metal"]);
        }
        void Lamp(Vector3 p,Material metal)
        {
            Shape(PrimitiveType.Cylinder,"Street light",Root,p+Vector3.up*3.3f,new Vector3(.11f,3.3f,.11f),metal);
            Box("Lantern",p+Vector3.up*6.65f,new Vector3(.55f,.18f,.35f),metal);
            Box("Lantern diffuser",p+Vector3.up*6.54f,new Vector3(.44f,.04f,.26f),Mat("Diffusers",new Color(.90f,.89f,.72f)));
        }
        void Bench(Vector3 p,float angle)
        {
            var b=new GameObject("Timber bench").transform;b.SetParent(Root);b.position=p;b.rotation=Quaternion.Euler(0,angle,0);
            var wood=Mat("Bench timber",new Color(.39f,.24f,.13f));var metal=materials["Charcoal metal"];
            for(int i=0;i<4;i++)Box("Seat slat",new Vector3(0,.52f,-.22f+i*.145f),new Vector3(1.9f,.08f,.11f),wood,b);
            for(int i=0;i<3;i++)Box("Back slat",new Vector3(0,.76f+i*.14f,.26f),new Vector3(1.9f,.10f,.07f),wood,b);
            for(int side=-1;side<=1;side+=2)Box("Bench leg",new Vector3(side*.72f,.3f,0),new Vector3(.08f,.6f,.48f),metal,b);
        }
        void StreetSign(Vector3 p,string name,Material metal)
        {
            Shape(PrimitiveType.Cylinder,"Street-sign post",Root,new Vector3(p.x,p.y*.5f,p.z),new Vector3(.07f,p.y*.5f,.07f),metal);
            var b=new GameObject("Street name").transform;b.SetParent(Root);b.position=p;
            Box("Sign plate",Vector3.zero,new Vector3(2.3f,.4f,.08f),Mat("Street signs",new Color(.08f,.19f,.28f)),b);
            Text("Street name",name,new Vector3(0,0,.05f),b,.115f,Color.white);
            FittedText("Street name reverse",name,new Vector3(0,0,-.05f),b,.115f,Color.white,2.1f,true);
        }
        static void Text(string name,string value,Vector3 p,Transform parent,float size,Color color)
        {
            FittedText(name,value,p,parent,size,color,name=="Street name"?2.1f:8f);
        }
        sealed class SignalVisual { public bool NS; public Renderer[] Bulbs; public Renderer Ped; }
        void Signal(Vector3 p,float rotation,bool ns)
        {
            var pole=new GameObject(ns?"N-S signal":"E-W signal").transform;pole.SetParent(Root);pole.position=p;pole.rotation=Quaternion.Euler(0,rotation,0);
            var metal=materials["Charcoal metal"];
            Shape(PrimitiveType.Cylinder,"Signal pole",pole,Vector3.up*1.9f,new Vector3(.1f,1.9f,.1f),metal);
            Box("Signal housing",new Vector3(0,3.25f,0),new Vector3(.42f,1.12f,.32f),metal,pole);
            SignalVisual s=new SignalVisual{NS=ns,Bulbs=new Renderer[3]};
            for(int i=0;i<3;i++)
                s.Bulbs[i]=Shape(PrimitiveType.Sphere,"Traffic lens",pole,new Vector3(0,3.59f-i*.34f,-.18f),new Vector3(.27f,.27f,.06f),LampMat("off",new Color(.055f,.06f,.055f),false)).GetComponent<Renderer>();
            Box("Pedestrian signal case",new Vector3(.32f,2.1f,0),new Vector3(.3f,.5f,.22f),metal,pole);
            s.Ped=Box("Pedestrian indicator",new Vector3(.32f,2.1f,-.12f),new Vector3(.16f,.28f,.04f),LampMat("pedRed",Color.red,true),pole).GetComponent<Renderer>();signals.Add(s);
        }
        Material LampMat(string key,Color color,bool emission)
        {
            var m=Mat("Signal "+key,color,.4f);if(emission){m.EnableKeyword("_EMISSION");m.SetColor("_EmissionColor",color*2.5f);}return m;
        }
        public void UpdateSignals(SignalPhase phase)
        {
            foreach(var s in signals)
            {
                bool green=s.NS?phase==SignalPhase.NorthSouthGreen:phase==SignalPhase.EastWestGreen;
                bool amber=s.NS?phase==SignalPhase.NorthSouthAmber:phase==SignalPhase.EastWestAmber;
                for(int i=0;i<3;i++)s.Bulbs[i].sharedMaterial=LampMat(i==0&&!green&&!amber?"red":i==1&&amber?"amber":i==2&&green?"green":"off",i==0&&!green&&!amber?new Color(1,.025f,.01f):i==1&&amber?new Color(1,.5f,.015f):i==2&&green?new Color(.015f,1,.25f):new Color(.055f,.06f,.055f),i==0&&!green&&!amber||i==1&&amber||i==2&&green);
                bool walk=phase==SignalPhase.PedestrianWalk;s.Ped.sharedMaterial=LampMat(walk?"pedGreen":"pedRed",walk?Color.green:Color.red,true);
            }
        }
        void Lighting()
        {
            RenderSettings.ambientMode=AmbientMode.Trilight;
            RenderSettings.ambientSkyColor=new Color(.55f,.65f,.77f);RenderSettings.ambientEquatorColor=new Color(.48f,.52f,.54f);RenderSettings.ambientGroundColor=new Color(.25f,.26f,.25f);
            RenderSettings.fog=true;RenderSettings.fogMode=FogMode.ExponentialSquared;RenderSettings.fogDensity=.0045f;RenderSettings.fogColor=new Color(.64f,.73f,.78f);
            Sun=new GameObject("Late-afternoon sun").AddComponent<Light>();Sun.type=LightType.Directional;Sun.color=new Color(1,.92f,.78f);Sun.intensity=2.5f;Sun.shadows=LightShadows.Soft;Sun.transform.rotation=Quaternion.Euler(43,-32,0);
            var skyShader=Shader.Find("Skybox/Procedural");if(skyShader!=null){var sky=new Material(skyShader);sky.SetFloat("_AtmosphereThickness",.9f);sky.SetColor("_SkyTint",new Color(.54f,.61f,.69f));sky.SetColor("_GroundColor",new Color(.43f,.44f,.39f));RenderSettings.skybox=sky;}
            RenderSettings.sun=Sun;QualitySettings.shadowDistance=105;
        }
        void CombineStaticGeometry()
        {
            CacheCameraObstacles();
            var groups=new Dictionary<Material,List<CombineInstance>>();var originals=new List<GameObject>();
            foreach(MeshFilter filter in Root.GetComponentsInChildren<MeshFilter>())
            {
                if(filter.sharedMesh==null||filter.gameObject.name.Contains("lens")||filter.gameObject.name.Contains("indicator")||filter.GetComponent<TextMesh>()!=null)continue;
                var renderer=filter.GetComponent<MeshRenderer>();if(renderer==null||renderer.sharedMaterial==null)continue;
                Material material=renderer.sharedMaterial;if(!groups.ContainsKey(material))groups[material]=new List<CombineInstance>();
                groups[material].Add(new CombineInstance{mesh=filter.sharedMesh,transform=filter.transform.localToWorldMatrix});originals.Add(filter.gameObject);
            }
            foreach(var pair in groups)
            {
                var mesh=new Mesh{name="Combined "+pair.Key.name,indexFormat=IndexFormat.UInt32};mesh.CombineMeshes(pair.Value.ToArray(),true,true);
                var go=new GameObject(mesh.name);go.transform.SetParent(Root);go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterial=pair.Key;
            }
            foreach(var go in originals){var r=go.GetComponent<MeshRenderer>();r.enabled=false;}
        }
    }
}
