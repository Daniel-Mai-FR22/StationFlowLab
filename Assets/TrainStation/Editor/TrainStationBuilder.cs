#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Editor
{
    // This is an authoring tool, never included in a player. The resulting scene is fully serialized.
    public static partial class TrainStationBuilder
    {
        public const string ScenePath = "Assets/TrainStation/Scenes/TrainStation.unity";
        const string ArtPath = "Assets/TrainStation/Materials";
        static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();

        [MenuItem("Efrei Research Lab/Train Station/Open TrainStation")]
        public static void Open()
        {
            if (!File.Exists(ScenePath)) { Create(); return; }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(ScenePath,ImportAssetOptions.ForceSynchronousImport);
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(ScenePath);
        }

        [MenuItem("Efrei Research Lab/Train Station/Create a new editable scene")]
        public static void Create()
        {
            if (File.Exists(ScenePath)) { Open(); return; } // Never overwrite hand edits.
            BuildAt(ScenePath);
        }

        [MenuItem("Efrei Research Lab/Train Station/Generate a separate fresh copy")]
        public static void CreateCopy() { BuildAt(AssetDatabase.GenerateUniqueAssetPath(ScenePath)); }

        static void EnsureResources()
        {
            Directory.CreateDirectory("Assets/TrainStation/Scenes");
            Directory.CreateDirectory(ArtPath);
            if (!File.Exists("Assets/TextMesh Pro/Resources/TMP Settings.asset"))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TMP_Text).Assembly);
                AssetDatabase.ImportPackage(package.resolvedPath + "/Package Resources/TMP Essential Resources.unitypackage", false);
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            if(TMP_Settings.LoadDefaultSettings()==null)throw new InvalidOperationException("TMP resources are importing. Wait for the import to finish, then create the scene again.");
            MakeMaterials();
            AssetDatabase.SaveAssets();
        }

        static void BuildAt(string path)
        {
            EnsureResources();
            var prior = SceneManager.GetActiveScene();
            var creationMode=string.IsNullOrEmpty(prior.path) && !prior.isDirty ? NewSceneMode.Single : NewSceneMode.Additive;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, creationMode);
            SceneManager.SetActiveScene(scene);
            try
            {
                var root = new GameObject("TrainStation — Gare de Valmont").transform;
                BuildArchitecture(root);
                BuildInteriors(root);
                BuildRail(root);
                BuildExterior(root);
                ConfigureLighting(root);
                var cameras = BuildCameras(root);
                BuildUI(root, cameras);
                var controls = root.GetComponentInChildren<TrainStationViewControls>(true);
                controls.roofGroups = root.GetComponentsInChildren<Transform>(true)
                    .Where(t => t.name.StartsWith("Roofs -", StringComparison.Ordinal)).Select(t => t.gameObject).ToArray();
                BuildMarkers(root);
                AssetDatabase.SaveAssets();
                if (!EditorSceneManager.SaveScene(scene, path)) throw new IOException("Scene could not be saved: " + path);
                var buildScenes = EditorBuildSettings.scenes.ToList();
                if (!buildScenes.Any(s => s.path == path)) buildScenes.Add(new EditorBuildSettingsScene(path, true));
                EditorBuildSettings.scenes = buildScenes.ToArray();
                // A clean previous scene can close safely; never discard unsaved user work.
                if (prior.IsValid() && prior.isLoaded && !prior.isDirty) EditorSceneManager.CloseScene(prior, true);
                Selection.activeGameObject = root.gameObject;
                if (SceneView.lastActiveSceneView != null)
                    SceneView.lastActiveSceneView.LookAt(new Vector3(0, 2, 30), Quaternion.Euler(37, -34, 0), 110);
                Validate(scene);
                Debug.Log("TrainStation saved as fully editable scene: " + path);
            }
            catch { if(prior.IsValid()&&prior.isLoaded)SceneManager.SetActiveScene(prior); throw; }
        }

        static void MakeMaterials()
        {
            materials.Clear();
            string[] keys={"stone","concrete","floor","steel","dark","glass","blue","magenta","white","yellow","wood","green","asphalt","ballast","rail","roof","light","red"};
            foreach(string key in keys)
            {
                var saved=AssetDatabase.LoadAssetAtPath<Material>(ArtPath+"/Station_"+key+".mat");
                if(saved!=null)materials[key]=saved;
            }
            if(materials.Count==keys.Length)return; // Keep material edits when authoring a separate scene copy.
            MaterialAsset("stone", new Color(.72f,.68f,.58f), 0,.3f);
            MaterialAsset("concrete", new Color(.52f,.55f,.55f), 0,.22f);
            MaterialAsset("floor", new Color(.72f,.73f,.70f), 0,.45f);
            MaterialAsset("steel", new Color(.25f,.32f,.34f), .72f,.48f);
            MaterialAsset("dark", new Color(.028f,.057f,.075f), .12f,.4f);
            MaterialAsset("glass", new Color(.46f,.72f,.77f,.20f), .02f,.55f, true);
            MaterialAsset("blue", new Color(.025f,.105f,.26f), .12f,.42f);
            MaterialAsset("magenta", new Color(.67f,.018f,.23f), .1f,.38f);
            MaterialAsset("white", new Color(.89f,.91f,.9f), 0,.4f);
            MaterialAsset("yellow", new Color(.94f,.7f,.18f), 0,.35f);
            MaterialAsset("wood", new Color(.38f,.20f,.085f), 0,.35f);
            MaterialAsset("green", new Color(.15f,.29f,.16f), 0,.12f);
            MaterialAsset("asphalt", new Color(.16f,.185f,.20f), 0,.18f);
            MaterialAsset("ballast", new Color(.28f,.27f,.24f), 0,.05f);
            MaterialAsset("rail", new Color(.39f,.42f,.43f), .86f,.64f);
            MaterialAsset("roof", new Color(.23f,.28f,.29f), .45f,.4f);
            MaterialAsset("light", new Color(.97f,.9f,.73f), 0,.5f);
            materials["light"].EnableKeyword("_EMISSION"); materials["light"].SetColor("_EmissionColor", new Color(1,.88f,.62f)*2);
            MaterialAsset("red", new Color(.72f,.04f,.025f), .05f,.45f);
            ApplyTexture("asphalt", "asphalt_diffuse.jpg", new Vector2(18,18));
            ApplyTexture("concrete", "pavement_diffuse.jpg", new Vector2(4,4));
            ApplyTexture("ballast", "asphalt_diffuse.jpg", new Vector2(2,35));
            foreach (var mat in materials.Values) EditorUtility.SetDirty(mat);
        }
        static void ApplyTexture(string key, string file, Vector2 scale)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/StationFlowDemo/Art/Resources/StationFlowArt/" + file);
            if (tex == null) return;
            materials[key].SetTexture("_BaseMap",tex); materials[key].SetTextureScale("_BaseMap",scale);
        }
        static void MaterialAsset(string key, Color color, float metallic, float smoothness, bool transparent=false)
        {
            string path=ArtPath+"/Station_"+key+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null) { material=new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(material,path); }
            material.SetColor("_BaseColor",color); material.SetFloat("_Metallic",metallic); material.SetFloat("_Smoothness",smoothness);
            material.enableInstancing=true;
            if(transparent)
            {
                material.SetFloat("_Surface",1); material.SetFloat("_Blend",0); material.SetFloat("_ZWrite",0);
                material.SetFloat("_SrcBlend",(float)BlendMode.SrcAlpha); material.SetFloat("_DstBlend",(float)BlendMode.OneMinusSrcAlpha);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.SetOverrideTag("RenderType","Transparent"); material.renderQueue=3000;
            }
            materials[key]=material;
        }
        static Material Mat(string key) => materials[key];
        static Transform Group(string name, Transform parent)
        {
            var go=new GameObject(name); go.transform.SetParent(parent,false); return go.transform;
        }
        static GameObject Box(string name,Transform parent,Vector3 pos,Vector3 size,Material mat,bool collider=true)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube); go.name=name; go.transform.SetParent(parent,false);
            go.transform.localPosition=pos; go.transform.localScale=size; go.GetComponent<Renderer>().sharedMaterial=mat;
            if(mat==Mat("glass"))go.GetComponent<Renderer>().shadowCastingMode=ShadowCastingMode.Off;
            if(!collider) UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.isStatic=true; return go;
        }
        static GameObject Cylinder(string name,Transform parent,Vector3 pos,float radius,float height,Material mat,bool collider=true)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cylinder); go.name=name; go.transform.SetParent(parent,false);
            go.transform.localPosition=pos; go.transform.localScale=new Vector3(radius*2,height*.5f,radius*2);
            go.GetComponent<Renderer>().sharedMaterial=mat;
            if(!collider) UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.isStatic=true; return go;
        }
        static GameObject Beam(string name,Transform parent,Vector3 a,Vector3 b,float thickness,Material mat)
        {
            var go=Box(name,parent,(a+b)*.5f,new Vector3(thickness,Vector3.Distance(a,b),thickness),mat,false);
            go.transform.localRotation=Quaternion.FromToRotation(Vector3.up,b-a); return go;
        }
        static TextMeshPro Label(string name,Transform parent,string value,Vector3 pos,float fontSize,Color color,float yaw=0)
        {
            var go=new GameObject(name); go.transform.SetParent(parent,false); go.transform.localPosition=pos;
            go.transform.localRotation=Quaternion.Euler(0,yaw,0);
            var t=go.AddComponent<TextMeshPro>(); t.font=TMP_Settings.defaultFontAsset; t.text=value;
            t.fontSize=fontSize*10; t.color=color; t.alignment=TextAlignmentOptions.Center;
            t.textWrappingMode=TextWrappingModes.NoWrap; t.overflowMode=TextOverflowModes.Overflow;
            t.rectTransform.sizeDelta=new Vector2(30,8); t.raycastTarget=false;
            go.isStatic=true; return t;
        }

        static void BuildArchitecture(Transform root)
        {
            var shell=Group("01 Architecture — hall et galerie",root);
            var floor=Group("01 Floors — level 0.00 m",shell);
            Box("Hall slab 72 x 56 m",floor,new Vector3(0,-.3f,0),new Vector3(72,.6f,56),Mat("floor"));
            Box("Cross concourse — step-free access to all platforms",floor,new Vector3(0,-.3f,36),new Vector3(96,.6f,16),Mat("floor"));
            // Thin inlays break up the large stone floor without blocking circulation.
            for(int x=-36;x<=36;x+=3) Box("Stone joint X "+x,floor,new Vector3(x,.006f,0),new Vector3(.018f,.009f,56),Mat("concrete"),false);
            for(int z=-28;z<=28;z+=3) Box("Stone joint Z "+z,floor,new Vector3(0,.007f,z),new Vector3(72,.009f,.018f),Mat("concrete"),false);
            for(int side=-1;side<=1;side+=2)
            {
                Box("Main circulation border",floor,new Vector3(side*19.3f,.013f,0),new Vector3(.18f,.018f,56),Mat("stone"),false);
                Box("External masonry lower wall",shell,new Vector3(side*36,2.85f,0),new Vector3(.65f,5.7f,56),Mat("stone"));
                Box("Clerestory lintel",shell,new Vector3(side*36,8.75f,0),new Vector3(.65f,.5f,56),Mat("stone"));
                for(int pier=-28;pier<=28;pier+=8)
                    Box("Clerestory stone pier",shell,new Vector3(side*36,7.1f,pier),new Vector3(.65f,2.8f,2.5f),Mat("stone"));
                Box("Cornice",shell,new Vector3(side*36,9,0),new Vector3(1,.4f,57),Mat("white"));
                for(int z=-24;z<=24;z+=8)
                {
                    Box("Side clerestory window",shell,new Vector3(side*35.64f,7.1f,z),new Vector3(.09f,2.6f,5.5f),Mat("glass"),false);
                    Box("Clerestory sill",shell,new Vector3(side*35.5f,5.7f,z),new Vector3(.4f,.15f,5.8f),Mat("white"),false);
                }
            }
            var facade=Group("02 Façade — three open entrance portals",shell);
            // Front openings at x=-24, 0, 24; all thresholds are flush.
            foreach(float x in new[]{-34f,-28f,-20f,-12f,-6f,6f,12f,20f,28f,34f})
            {
                Box("Limestone pier",facade,new Vector3(x,4.5f,-28),new Vector3(x==34||x==-34?4:1.1f,9,1),Mat("stone"));
                Box("Pier capital",facade,new Vector3(x,8.5f,-28.15f),new Vector3(1.5f,.38f,1.3f),Mat("white"));
            }
            foreach(float x in new[]{-24f,0f,24f})
            {
                float w=x==0?10:6;
                Box("Entrance transom",facade,new Vector3(x,6.9f,-28),new Vector3(w,2.4f,.18f),Mat("glass"),false);
                Box("Entrance header",facade,new Vector3(x,5.5f,-28),new Vector3(w,.25f,.35f),Mat("steel"),false);
                foreach(int sign in new[]{-1,1}) Box("Open sliding glass leaf",facade,new Vector3(x+sign*(w/2-.65f),2.6f,-28.1f),new Vector3(1.1f,5.2f,.09f),Mat("glass"));
            }
            foreach(float x in new[]{-16f,-9f,9f,16f})
            {
                Box("Front glazing",facade,new Vector3(x,3.7f,-28),new Vector3(5,7.1f,.14f),Mat("glass"));
                Box("Window mullion",facade,new Vector3(x,3.7f,-28.12f),new Vector3(.09f,7.1f,.18f),Mat("steel"),false);
            }
            Box("Entablature",facade,new Vector3(0,9.4f,-28),new Vector3(73,1.4f,1.25f),Mat("stone"));
            Box("Main nameplate",facade,new Vector3(0,8.7f,-28.7f),new Vector3(23,1.1f,.15f),Mat("blue"),false);
            Label("Gare de Valmont lettering",facade,"GARE DE VALMONT",new Vector3(0,8.7f,-28.8f),.74f,Color.white);
            Box("SNCF cartouche",facade,new Vector3(-28,10.75f,-28.2f),new Vector3(5.6f,2.2f,.5f),Mat("magenta"));
            Label("SNCF letters",facade,"SNCF",new Vector3(-28,10.75f,-28.49f),1.15f,Color.white);
            Box("Entrance canopy",facade,new Vector3(0,5.3f,-31),new Vector3(31,.14f,6),Mat("glass"),false);
            foreach(float x in new[]{-15f,-7.5f,0,7.5f,15f}) Beam("Canopy bracket",facade,new Vector3(x,7,-28),new Vector3(x,5.2f,-34),.12f,Mat("steel"));

            var structure=Group("03 Exposed iron structure",shell);
            for(int z=-24;z<=28;z+=8)
            {
                foreach(int side in new[]{-1,1})
                {
                    Cylinder("Iron column",structure,new Vector3(side*20,5,z),.2f,10,Mat("steel"));
                    Box("Column plinth",structure,new Vector3(side*20,.3f,z),new Vector3(.85f,.6f,.85f),Mat("stone"));
                    Beam("Knee brace",structure,new Vector3(side*20,7.8f,z),new Vector3(side*16,10.5f,z),.14f,Mat("steel"));
                }
                Beam("Tie rod",structure,new Vector3(-20,10,z),new Vector3(20,10,z),.12f,Mat("steel"));
                for(int i=0;i<10;i++)
                {
                    float x0=-20+i*4,x1=x0+4;
                    float y0=10+5*Mathf.Sin((x0+20)/40*Mathf.PI), y1=10+5*Mathf.Sin((x1+20)/40*Mathf.PI);
                    Beam("Arched roof rib",structure,new Vector3(x0,y0,z),new Vector3(x1,y1,z),.23f,Mat("steel"));
                    Beam("Truss web",structure,new Vector3(x0,10,z),new Vector3(x1,y1,z),.07f,Mat("steel"));
                }
            }
            var roofs=Group("Roofs - Hall vault and service wings",shell);
            for(int i=0;i<10;i++)
            {
                float x0=-20+i*4,x1=x0+4,y0=10+5*Mathf.Sin((x0+20)/40*Mathf.PI),y1=10+5*Mathf.Sin((x1+20)/40*Mathf.PI);
                var pane=Box("Vault glazing bay "+(i+1),roofs,new Vector3((x0+x1)/2,(y0+y1)/2,0),new Vector3(Mathf.Sqrt(16+(y1-y0)*(y1-y0)),.08f,57),Mat("glass"),false);
                pane.transform.localRotation=Quaternion.Euler(0,0,Mathf.Atan2(y1-y0,4)*Mathf.Rad2Deg);
                Box("Longitudinal glazing bar",roofs,new Vector3(x0,y0,0),new Vector3(.07f,.12f,57),Mat("steel"),false);
            }
            foreach(int side in new[]{-1,1}) Box("Wing roof",roofs,new Vector3(side*28,9.3f,0),new Vector3(16,.25f,57),Mat("roof"),false);
            var gables=Group("Roofs - Vault end glazing",shell);
            for(int i=0;i<10;i++)foreach(int side in new[]{-1,1})
            {
                float x0=-20+i*4,x1=x0+4,y0=10+5*Mathf.Sin((x0+20)/40*Mathf.PI),y1=10+5*Mathf.Sin((x1+20)/40*Mathf.PI);
                VaultEndPane(gables,"Gable "+side+" bay "+i,x0,x1,side<0?10:7,y0,y1,side*28.35f);
            }
            var gallery=Group("04 Cross concourse — all platforms",shell);
            foreach(int side in new[]{-1,1})
            {
                foreach(float z in new[]{30f,42f}) Box("Gallery side wall beside exit",gallery,new Vector3(side*48,3.1f,z),new Vector3(.3f,6.2f,4),Mat("glass"));
                Box("Side exit transom",gallery,new Vector3(side*48,5.7f,36),new Vector3(.3f,1,8),Mat("glass"));
                // Side extensions close the weather envelope south of the gallery.
                Box("Gallery return",gallery,new Vector3(side*42,3.1f,28),new Vector3(12,6.2f,.25f),Mat("glass"));
            }
            for(int x=-48;x<=48;x+=12)
            {
                Cylinder("Gallery post",gallery,new Vector3(x,3.5f,42),.15f,7,Mat("steel"));
                Beam("Gallery roof beam",gallery,new Vector3(x,7,28),new Vector3(x,7,44),.18f,Mat("steel"));
            }
            var galleryRoof=Group("Roofs - Cross concourse",shell);
            Box("Gallery roof glazing",galleryRoof,new Vector3(0,7.1f,36),new Vector3(97,.1f,17),Mat("glass"),false);
        }

        static void VaultEndPane(Transform parent,string name,float x0,float x1,float bottom,float y0,float y1,float z)
        {
            string folder="Assets/TrainStation/Meshes";Directory.CreateDirectory(folder);
            string path=folder+"/"+name.Replace(' ','_')+".asset";
            var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(mesh==null)
            {
                mesh=new Mesh{name=name};
                mesh.vertices=new[]{new Vector3(x0,bottom,z),new Vector3(x1,bottom,z),new Vector3(x1,y1,z),new Vector3(x0,y0,z),new Vector3(x0,bottom,z+.06f),new Vector3(x1,bottom,z+.06f),new Vector3(x1,y1,z+.06f),new Vector3(x0,y0,z+.06f)};
                mesh.triangles=new[]{0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7};
                mesh.RecalculateNormals();mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,path);
            }
            var go=Group(name,parent).gameObject;go.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=Mat("glass");renderer.shadowCastingMode=ShadowCastingMode.Off;go.isStatic=true;
            Beam("Glazing mullion",parent,new Vector3(x0,bottom,z),new Vector3(x0,y0,z),.07f,Mat("steel"));
        }

        static void ConfigureLighting(Transform root)
        {
            var group=Group("07 Lighting — daylight",root);
            var sun=new GameObject("Sun — late morning").AddComponent<Light>(); sun.transform.SetParent(group,false);
            sun.type=LightType.Directional; sun.transform.rotation=Quaternion.Euler(48,-32,0);sun.color=new Color(1,.95f,.86f);sun.intensity=2.1f;sun.shadows=LightShadows.Soft;
            RenderSettings.sun=sun;RenderSettings.ambientMode=AmbientMode.Trilight;
            RenderSettings.ambientSkyColor=new Color(.60f,.70f,.80f);RenderSettings.ambientEquatorColor=new Color(.45f,.49f,.5f);RenderSettings.ambientGroundColor=new Color(.26f,.25f,.23f);
            RenderSettings.fog=false;
            string lookPath=ArtPath+"/StationAtmosphere.asset";
            var look=AssetDatabase.LoadAssetAtPath<VolumeProfile>(lookPath);
            if(look==null)
            {
                look=ScriptableObject.CreateInstance<VolumeProfile>();AssetDatabase.CreateAsset(look,lookPath);
                var tone=look.Add<Tonemapping>(true);tone.mode.Override(TonemappingMode.ACES);AssetDatabase.AddObjectToAsset(tone,look);
                var grade=look.Add<ColorAdjustments>(true);grade.postExposure.Override(-.2f);grade.saturation.Override(-3);AssetDatabase.AddObjectToAsset(grade,look);
                EditorUtility.SetDirty(look);
            }
            var volume=Group("Station colour and exposure",group).gameObject.AddComponent<Volume>();volume.isGlobal=true;volume.sharedProfile=look;
            var sky=AssetDatabase.LoadAssetAtPath<Material>(ArtPath+"/StationSky.mat");
            if(sky==null){sky=new Material(Shader.Find("Skybox/Procedural"));AssetDatabase.CreateAsset(sky,ArtPath+"/StationSky.mat");}
            sky.SetFloat("_SunSize",.025f);sky.SetFloat("_AtmosphereThickness",.8f);sky.SetColor("_SkyTint",new Color(.5f,.6f,.68f));EditorUtility.SetDirty(sky);RenderSettings.skybox=sky;
            for(int z=-18;z<=22;z+=20) foreach(int x in new[]{-14,14})
            {
                var go=new GameObject("Hall soft fill");go.transform.SetParent(group,false);go.transform.localPosition=new Vector3(x,8,z);
                var light=go.AddComponent<Light>();light.type=LightType.Point;light.range=23;light.intensity=5;light.color=new Color(1,.91f,.76f);light.shadows=LightShadows.None;
                Box("Suspended luminaire",group,new Vector3(x,8,z),new Vector3(2.8f,.12f,.4f),Mat("light"),false);
                Beam("Pendant cable",group,new Vector3(x,8,z),new Vector3(x,12,z),.035f,Mat("dark"));
            }
        }

        static Camera[] BuildCameras(Transform root)
        {
            var group=Group("08 Cameras — six saved viewpoints",root);
            var names=new[]{"01 Hall principal","02 Parvis et façade","03 Quais et voies","04 Ensemble","05 Commerces","06 Galerie des départs"};
            var positions=new[]{new Vector3(5,4.7f,-21),new Vector3(64,29,-75),new Vector3(-33.8f,2.7f,61),new Vector3(175,185,-125),new Vector3(-5,2.5f,-8),new Vector3(44,3.2f,34)};
            var targets=new[]{new Vector3(-4,4.5f,14),new Vector3(0,5,-5),new Vector3(-36,2.6f,144),new Vector3(0,0,64),new Vector3(-27,1.9f,0),new Vector3(-20,2.8f,36)};
            var cameras=new Camera[6];
            for(int i=0;i<cameras.Length;i++)
            {
                var go=new GameObject(names[i]);go.transform.SetParent(group,false);go.transform.position=positions[i];go.transform.LookAt(targets[i]);
                var cam=go.AddComponent<Camera>();cam.nearClipPlane=.08f;cam.farClipPlane=800;cam.fieldOfView=i==3?49:65;
                cam.clearFlags=CameraClearFlags.Skybox;cam.allowHDR=true;
                var data=cam.GetUniversalAdditionalCameraData();data.renderPostProcessing=true;data.antialiasing=AntialiasingMode.FastApproximateAntialiasing;
                cam.enabled=i==0;go.AddComponent<AudioListener>().enabled=i==0;cameras[i]=cam;
            }
            cameras[0].gameObject.tag="MainCamera";return cameras;
        }

        static void BuildMarkers(Transform root)
        {
            var g=Group("10 Scenario anchors — empty, no simulation",root);
            var names=new[]{"ENTRY_Main","ENTRY_West","ENTRY_East","ZONE_Hall","ZONE_Ticketing","ZONE_Retail","ZONE_Concourse","PLATFORM_A","PLATFORM_B","PLATFORM_C","PLATFORM_D","EXIT_Parvis","CCTV_Hall","CCTV_Concourse"};
            var points=new[]{new Vector3(0,0,-30),new Vector3(-24,0,-30),new Vector3(24,0,-30),Vector3.zero,new Vector3(-25,0,-17),new Vector3(-25,0,13),new Vector3(0,0,34),new Vector3(-36,0,70),new Vector3(-12,0,70),new Vector3(12,0,70),new Vector3(36,0,70),new Vector3(0,0,-43),new Vector3(18,7,-20),new Vector3(-44,6,35)};
            for(int i=0;i<names.Length;i++){var t=Group(names[i],g);t.localPosition=points[i];}
        }

        [MenuItem("Efrei Research Lab/Train Station/Validate saved scene")]
        public static void ValidateActive() { Validate(SceneManager.GetActiveScene()); }
        static void Validate(Scene scene)
        {
            var objects=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Transform>(true)).ToArray();
            var errors=new List<string>();
            foreach(var t in objects)
            {
                if(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject)>0) errors.Add("Missing script: "+t.name);
                var r=t.GetComponent<Renderer>();
                if(r!=null && r.sharedMaterials.Any(m=>m==null||m.shader==null||m.shader.name.Contains("InternalError")))errors.Add("Missing material: "+t.name);
            }
            if(objects.Any(t=>t.GetComponent<Rigidbody>()!=null))errors.Add("Unexpected physics-driven object");
            foreach(var t in objects.Where(t=>t.GetComponent<Animator>()!=null))
            {
                var actor=t.GetComponentInParent<StationFlow.TrainStation.Social.StationActorMotion>();
                if(!actor||!actor.animationPlayer||actor.animationPlayer.TargetAnimator!=t.GetComponent<Animator>())
                    errors.Add("Animator is not bound to a saved station actor: "+t.name);
            }
            int cameras=objects.Count(t=>t.GetComponent<Camera>()!=null);
            var controls=objects.Select(t=>t.GetComponent<TrainStationViewControls>()).FirstOrDefault(c=>c!=null);
            if(controls==null||controls.viewpoints==null||controls.viewpoints.Length!=cameras||controls.viewpoints.Any(c=>!c)||controls.viewpoints.Distinct().Count()!=cameras)
                errors.Add("Saved cameras and presentation view references must match uniquely.");
            if(controls==null||controls.roofGroups.Length<3)errors.Add("Missing UI or roof bindings");
            Directory.CreateDirectory("Reports/TrainStation");
            File.WriteAllText("Reports/TrainStation/validation.json",JsonUtility.ToJson(new ValidationReport{scene=scene.path,objects=objects.Length,renderers=objects.Count(t=>t.GetComponent<Renderer>()!=null),colliders=objects.Count(t=>t.GetComponent<Collider>()!=null),cameras=cameras,errors=errors.ToArray()},true));
            if(errors.Count>0)throw new Exception(string.Join("\n",errors));
        }
        [Serializable]class ValidationReport { public string scene;public int objects,renderers,colliders,cameras;public string[] errors; }
    }
}
#endif
