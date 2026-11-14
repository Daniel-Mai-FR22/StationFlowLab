#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.TrainStation.Social;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Saved, physical hall roundabout; native navigation chooses the bypass.</summary>
    public static class StationDecisionHallFountainAuthoring
    {
        public const string RootName = "18 Daily hall fountain and circulation";
        public const string ReportDirectory = "Reports/TrainStationDaily/HallFountain";
        public const float Radius = 5f;
        public static readonly Vector3 Center = Vector3.zero;
        const string AssetRoot = "Assets/TrainStation/DecisionLab/HallFountain";
        [Serializable] sealed class Receipt
        {
            public string configuredUtc, scene, backupDirectory, previousNavigationHash, navigationHash,
                previousSceneHash, sceneHash, openSceneBackup, error;
            public bool success, protectedFilesPreserved, openSceneWasDirty;
            public Vector3 fountainCenter, accueilCenter, displayCenter;
            public float fountainRadius, rimHeight;
            public int nativeNavigationActors;
            public Vector3[] displayStandingPoints, accueilQueue;
            public List<FileEvidence> protectedFiles = new List<FileEvidence>();
        }
        [Serializable] sealed class FileEvidence { public string path, before, after; }
        [Serializable] sealed class CaptureReceipt
        {
            public string capturedUtc, scene, mode;
            public float elapsedSimulationSeconds;
            public Vector3 fountainCenter;
            public float fountainRadius;
            public CameraView[] views;
        }
        [Serializable] sealed class CameraView
        {
            public string image;
            public Vector3 position, eulerAngles;
            public float verticalFieldOfView;
            public int width, height;
            public Matrix4x4 worldToCamera, projection;
        }

        [MenuItem("Efrei Research Lab/Train Station/Daily/Install central hall fountain")]
        public static void Apply()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play before editing the hall layout.");
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == StationDecisionDayAuthoring.ScenePath, "Open Daily before changing circulation.");
            var existingRoot = scene.GetRootGameObjects().SingleOrDefault(g => g.name == RootName);
            var surface = Components<NavMeshSurface>(scene).Single();
            var world = Components<StationDecisionWorld>(scene).Single();
            var social = Components<StationSocialWorld>(scene).Single();
            string navigation = AssetDatabase.GetAssetPath(surface.navMeshData);
            Require(navigation == StationDecisionGateAuthoring.NavigationPath, "The hall must use the Daily-owned controlled-platform NavMesh.");
            var report = new Receipt { configuredUtc = DateTime.UtcNow.ToString("O"), scene = scene.path,
                previousNavigationHash = Hash(navigation), previousSceneHash = Hash(scene.path),
                fountainCenter = Center, fountainRadius = Radius, rimHeight = .60f };
            Directory.CreateDirectory(ReportDirectory);
            report.backupDirectory = ReportDirectory + "/Before-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
            Directory.CreateDirectory(report.backupDirectory);
            File.Copy(scene.path, report.backupDirectory + "/TrainStationDaily.unity");
            File.Copy(navigation, report.backupDirectory + "/Daily_Controlled_Platforms.asset");
            report.openSceneWasDirty = scene.isDirty;
            if (scene.isDirty)
            {
                report.openSceneBackup = report.backupDirectory + "/TrainStationDaily.open-scene.unity";
                Require(EditorSceneManager.SaveScene(scene, report.openSceneBackup, true), "Could not preserve the open scene's existing changes before authoring.");
            }
            foreach (string path in new[] { ScenePath, SourceScene, StationDecisionDayAuthoring.SchedulePath,
                StationDecisionDayAuthoring.ScenarioPath, SchedulePath, StationDecisionLabAmenitiesAuthoring.NavigationPath, "ProjectSettings/NavMeshAreas.asset" })
                if (File.Exists(path)) report.protectedFiles.Add(new FileEvidence { path = path, before = Hash(path) });
            bool saved = false;
            try
            {
                var root = existingRoot ? existingRoot.transform : new GameObject(RootName).transform;
                if (!existingRoot)
                {
                    SceneManager.MoveGameObjectToScene(root.gameObject, scene);
                    var fountain = Child(root, "Fontaine centrale — bassin et obstacle circulaire");
                    fountain.position = Center;
                    BuildFountain(fountain);
                }
                var savedFountain = root.Cast<Transform>().Single(t => t.name.StartsWith("Fontaine centrale", StringComparison.Ordinal));
                savedFountain.position = Center; EditorUtility.SetDirty(savedFountain);
                ConfigureDisplay(scene, world, root, report);
                ConfigureAccueil(scene, world, social, report);
                ConfigureCrowdMotion(world, report);
                RerouteTactile(scene, root);
                StationDecisionHallRedesignAuthoring.ConfigureHallCameras(scene);
                Physics.SyncTransforms();
                var owned = surface.navMeshData;
                surface.BuildNavMesh();
                var baked = surface.navMeshData;
                Require(baked && baked != owned && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(baked)), "The hall bake must produce temporary data, not alter another scene's navigation.");
                surface.RemoveData(); surface.navMeshData = owned;
                EditorUtility.CopySerialized(baked, owned); owned.name=System.IO.Path.GetFileNameWithoutExtension(navigation); EditorUtility.SetDirty(owned);
                UnityEngine.Object.DestroyImmediate(baked); surface.AddData();
                foreach (var link in Components<NavMeshLink>(scene)) link.UpdateLink();
                SnapMovedMarkers(world, social.informationDesk);
                StationDecisionHallFountainVerification.CheckLayout();
                StationDecisionHallRedesignAuthoring.Validate();
                foreach (var item in report.protectedFiles)
                { item.after = Hash(item.path); Require(item.after == item.before, "Hall authoring changed a protected resource: " + item.path); }
                report.protectedFilesPreserved = true;
                EditorUtility.SetDirty(surface); EditorUtility.SetDirty(world);
                EditorSceneManager.MarkSceneDirty(scene); AssetDatabase.SaveAssets();
                Require(EditorSceneManager.SaveScene(scene), "Could not save the hall fountain layout."); saved = true;
                report.navigationHash = Hash(navigation); report.sceneHash = Hash(scene.path); report.success = true;
            }
            catch (Exception error)
            {
                report.error = error.ToString();
                if (!saved)
                {
                    File.Copy(report.backupDirectory + "/Daily_Controlled_Platforms.asset", navigation, true);
                    AssetDatabase.ImportAsset(navigation, ImportAssetOptions.ForceUpdate);
                    if (report.openSceneWasDirty)
                    {
                        File.Copy(report.openSceneBackup, StationDecisionDayAuthoring.ScenePath, true);
                        AssetDatabase.ImportAsset(StationDecisionDayAuthoring.ScenePath, ImportAssetOptions.ForceUpdate);
                    }
                    EditorSceneManager.OpenScene(StationDecisionDayAuthoring.ScenePath, OpenSceneMode.Single);
                }
                throw;
            }
            finally { File.WriteAllText(ReportDirectory + "/fountain-authoring.json", JsonUtility.ToJson(report, true)); }
            Capture();
        }

        static void BuildFountain(Transform parent)
        {
            var stone = Material("Fountain pale stone", new Color(.68f,.72f,.72f), .20f, .05f);
            var lip = Material("Fountain light rim", new Color(.86f,.88f,.84f), .32f, .05f);
            var water = Material("Fountain turquoise water", new Color(.055f,.43f,.55f), .90f, .12f);
            var spray = Material("Fountain water spray", new Color(.70f,.91f,.97f), .65f, .02f);
            var baseMesh = MeshAsset("FountainSolidBasin", Cylinder(Radius,.40f));
            var basin = MeshObject(parent, "Bassin solide — aucun passage ni marche sur l'eau", baseMesh, stone);
            basin.gameObject.AddComponent<MeshCollider>().sharedMesh = baseMesh;
            var exclusion = basin.gameObject.AddComponent<NavMeshModifier>();
            exclusion.overrideArea = true; exclusion.area = 1; exclusion.applyToChildren = false;
            var rim = MeshObject(parent, "Margelle basse", MeshAsset("FountainRing", Ring(4.50f,Radius,.40f,.60f)), lip);
            rim.gameObject.AddComponent<MeshCollider>().sharedMesh = rim.GetComponent<MeshFilter>().sharedMesh;
            Disc(parent, "Surface d'eau", new Vector3(0,.455f,0),4.50f,.018f,water);
            Disc(parent, "Socle central", new Vector3(0,.59f,0),.72f,.26f,lip);
            Disc(parent, "Vasque centrale", new Vector3(0,.94f,0),1.10f,.18f,lip);
            Disc(parent, "Eau de la vasque", new Vector3(0,1.035f,0),.98f,.015f,water);
            Disc(parent, "Buse centrale", new Vector3(0,1.14f,0),.12f,.20f,stone);
            // Chord bands exclude the whole interior at floor height, not just the raised rim.
            var cuts = Child(parent, "Exclusion NavMesh circulaire — bassin entier");
            const int bands = 40; float step = Radius * 2f / bands;
            for (int i=0;i<bands;++i)
            {
                float z = -Radius + step*(i+.5f);
                float near = Mathf.Max(0,Mathf.Abs(z)-step*.5f);
                var cut = Child(cuts,"Bande " + i.ToString("00"));
                var volume = cut.gameObject.AddComponent<NavMeshModifierVolume>(); volume.area=1;
                volume.center = new Vector3(0,1,z);
                volume.size = new Vector3(2f*Mathf.Sqrt(Radius*Radius-near*near)+.02f,3,step+.01f);
            }
            var nozzle = Child(parent,"Jet central"); nozzle.localPosition = new Vector3(0,1.24f,0);
            Jet(nozzle,spray,4.8f,.82f,100f,.045f,5f);
            for (int i=0;i<8;++i)
            {
                float a=i*Mathf.PI/4;
                var jet=Child(parent,"Jet de bassin " + (i+1));
                jet.localPosition=new Vector3(Mathf.Cos(a)*2.9f,.48f,Mathf.Sin(a)*2.9f);
                jet.localRotation=Quaternion.Euler(0,-a*Mathf.Rad2Deg,0);
                Jet(jet,spray,3.2f,.64f,35f,.035f,7f);
            }
            rim.GetComponent<Renderer>().receiveShadows=true;
        }

        static void Jet(Transform transform, Material material,float speed,float life,float rate,float size,float angle)
        {
            // A Unity particle cone points local +Z; orient it vertically without adding navigation geometry.
            transform.localRotation = Quaternion.Euler(-90,0,0);
            var system=transform.gameObject.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main=system.main; main.loop=true; main.playOnAwake=true; main.duration=2f; main.startLifetime=life;
            main.startSpeed=speed; main.startSize=size; main.startColor=new Color(.75f,.95f,1f,.9f);
            main.gravityModifier=.8f; main.maxParticles=256; main.simulationSpace=ParticleSystemSimulationSpace.World;
            var emission=system.emission; emission.rateOverTime=rate;
            var shape=system.shape; shape.shapeType=ParticleSystemShapeType.Cone; shape.angle=angle; shape.radius=.06f;
            system.GetComponent<ParticleSystemRenderer>().sharedMaterial=material;
        }

        static void ConfigureDisplay(Scene scene, StationDecisionWorld world, Transform root, Receipt report)
        {
            StationDecisionHallRedesignAuthoring.ConfigureDisplayLayout(scene,world,root);
            var poi=world.pois.Single(p=>p.poiId=="display-hall");
            report.displayCenter=poi.Position;
            report.displayStandingPoints=poi.standingPoints.Select(p=>p.position).ToArray();
        }

        static void ConfigureAccueil(Scene scene, StationDecisionWorld world, StationSocialWorld social, Receipt report)
        {
            StationDecisionHallRedesignAuthoring.ConfigureAccueilLayout(scene,world,social);
            report.accueilCenter=StationDecisionHallRedesignAuthoring.AccueilCenter;
            report.accueilQueue=social.informationDesk.queueSlots.Select(p=>p.position).ToArray();
        }

        static void SnapMovedMarkers(StationDecisionWorld world,StationInformationDesk desk)
        {
            var display=world.pois.Single(p=>p.poiId=="display-hall");
            var info=world.pois.Single(p=>p.poiId=="info-accueil");
            foreach(var marker in StationDecisionHallRedesignAuthoring.MovedServiceMarkers(world,desk))
            {
                Require(NavMesh.SamplePosition(marker.position,out var hit,.18f,StationDecisionPlatformGate.DefaultAreaMask(NavMesh.AllAreas)) &&
                    Mathf.Abs(hit.position.y-.02f)<.15f, "Moved hall marker is off the saved floor: "+marker.name);
                marker.position=hit.position; EditorUtility.SetDirty(marker);
            }
        }

        static void ConfigureCrowdMotion(StationDecisionWorld world, Receipt report)
        {
            foreach (var passenger in world.participants.Where(p => p))
            {
                var motion=passenger.motion ? passenger.motion : passenger.GetComponent<StationActorMotion>();
                Require(motion && motion.agent, "Daily actor is missing its navigation component.");
                // People remain native agents even when stopped. Carved crowd footprints
                // disconnect busy queues and gateway approaches from the saved floor.
                foreach (var obstacle in motion.GetComponents<NavMeshObstacle>())
                    UnityEngine.Object.DestroyImmediate(obstacle);
                var planningRoot=motion.transform.Find("Stationary planned footprint");
                if (planningRoot) UnityEngine.Object.DestroyImmediate(planningRoot.gameObject);
                motion.agent.enabled=true;
                motion.agent.avoidancePriority=50;
                motion.agent.obstacleAvoidanceType=ObstacleAvoidanceType.HighQualityObstacleAvoidance;
                motion.agent.autoRepath=true;
                motion.agent.updatePosition=true;
                motion.agent.updateRotation=true;
                EditorUtility.SetDirty(motion); EditorUtility.SetDirty(motion.agent);
                ++report.nativeNavigationActors;
            }
        }

        static void RerouteTactile(Scene scene,Transform root)
        {
            var existing=Components<Transform>(scene).Single(t=>t.name=="Bandes_guidage_tactile_continues");
            foreach(string name in new[]{"Entree_vers_hall","Contournement_accueil_sud","Contournement_accueil_ouest","Retour_axe_hall","Hall_vers_concourse"})
            { var strip=existing.Find(name); Require(strip,"Missing saved tactile strip "+name); strip.gameObject.SetActive(false); EditorUtility.SetDirty(strip.gameObject); }
            var material=AssetDatabase.LoadAssetAtPath<Material>("Assets/TrainStation/Materials/yellow.mat");
            if(!material) material=existing.GetComponentsInChildren<MeshRenderer>(true).First().sharedMaterial;
            const string guideName="Guidage tactile — contournement ouest sans traverser le bassin";
            var previousGuide=root.Find(guideName);
            if (previousGuide) UnityEngine.Object.DestroyImmediate(previousGuide.gameObject);
            var guide=Child(root,guideName);
            var points=new[]{new Vector3(0,.025f,-28),new Vector3(0,.025f,-20),new Vector3(-7,.025f,-20),
                new Vector3(-7,.025f,10),new Vector3(-7,.025f,16),new Vector3(0,.025f,16),new Vector3(0,.025f,36)};
            for(int i=1;i<points.Length;++i)
            for(int rib=-1;rib<=1;++rib)
            {
                var direction=points[i]-points[i-1]; var right=Vector3.Cross(Vector3.up,direction.normalized);
                var bar=GameObject.CreatePrimitive(PrimitiveType.Cube); bar.name="Nervure "+i+" "+rib;
                bar.transform.SetParent(guide,false); bar.transform.position=(points[i]+points[i-1])*.5f+right*(rib*.13f);
                bar.transform.rotation=Quaternion.LookRotation(direction);
                bar.transform.localScale=new Vector3(.075f,.025f,direction.magnitude+.07f);
                UnityEngine.Object.DestroyImmediate(bar.GetComponent<Collider>()); bar.GetComponent<Renderer>().sharedMaterial=material;
            }
        }

        static void ReaimAccueilCamera(Scene scene,StationInformationDesk desk,Transform departure)
        {
            var meta=Components<StationSurveillanceCamera>(scene).Single(m=>m.cameraId=="HALL-01");
            var camera=meta.cameraComponent; var lens=camera.transform.position;
            var points=new[]{desk.receptionist.transform.position+Vector3.up*1.55f,desk.queueSlots[0].position+Vector3.up*1.55f,
                desk.queueSlots.Last().position+Vector3.up*1.55f,departure.position+Vector3.up*1.55f};
            var direction=points.Select(p=>(p-lens).normalized).Aggregate(Vector3.zero,(sum,d)=>sum+d).normalized;
            var head=camera.transform.parent; head.rotation=Quaternion.LookRotation(direction,Vector3.up);
            head.position+=lens-camera.transform.position;
            meta.aimTarget.position=lens+direction*points.Average(p=>Vector3.Distance(p,lens));
            for(int i=0;i<meta.coveragePoints.Length;++i) { meta.coveragePoints[i].position=points[Mathf.Min(i,3)]; EditorUtility.SetDirty(meta.coveragePoints[i]); }
            EditorUtility.SetDirty(head); EditorUtility.SetDirty(meta.aimTarget); EditorUtility.SetDirty(meta);
        }

        static Material Material(string name,Color color,float smoothness,float metallic)
        {
            Directory.CreateDirectory(AssetRoot); AssetDatabase.Refresh();
            string path=AssetRoot+"/"+name.Replace(' ','_')+".mat";
            var result=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(!result)
            {
                result=new Material(Shader.Find("Universal Render Pipeline/Lit")){name=name};
                result.SetColor("_BaseColor",color); result.SetFloat("_Smoothness",smoothness); result.SetFloat("_Metallic",metallic);
                AssetDatabase.CreateAsset(result,path);
            }
            return result;
        }
        static Mesh MeshAsset(string name,Mesh mesh)
        {
            string path=AssetRoot+"/"+name+".asset"; var existing=AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if(existing){UnityEngine.Object.DestroyImmediate(mesh);return existing;}
            mesh.name=name; AssetDatabase.CreateAsset(mesh,path); return mesh;
        }
        static Transform MeshObject(Transform parent,string name,Mesh mesh,Material material)
        {
            var item=Child(parent,name); item.gameObject.AddComponent<MeshFilter>().sharedMesh=mesh;
            item.gameObject.AddComponent<MeshRenderer>().sharedMaterial=material; return item;
        }
        static Transform Child(Transform parent,string name)
        {
            var child = new GameObject(name).transform; child.SetParent(parent,false); return child;
        }
        static void Disc(Transform parent,string name,Vector3 position,float radius,float height,Material material)
        {
            var item=GameObject.CreatePrimitive(PrimitiveType.Cylinder); item.name=name; item.transform.SetParent(parent,false);
            item.transform.localPosition=position; item.transform.localScale=new Vector3(radius*2,height*.5f,radius*2);
            UnityEngine.Object.DestroyImmediate(item.GetComponent<Collider>()); item.GetComponent<Renderer>().sharedMaterial=material;
        }
        static Mesh Cylinder(float radius,float height)
        {
            const int n=96; var v=new List<Vector3>(); var t=new List<int>();
            for(int i=0;i<n;++i){float a=i*2*Mathf.PI/n;v.Add(new Vector3(radius*Mathf.Cos(a),0,radius*Mathf.Sin(a)));v.Add(new Vector3(radius*Mathf.Cos(a),height,radius*Mathf.Sin(a)));}
            v.Add(Vector3.zero);v.Add(Vector3.up*height);
            for(int i=0;i<n;++i)
            {int a=i*2,b=((i+1)%n)*2;t.AddRange(new[]{a,a+1,b,b,a+1,b+1,2*n,a,b,2*n+1,b+1,a+1});}
            var mesh=new Mesh();mesh.SetVertices(v);mesh.SetTriangles(t,0);mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
        }
        static Mesh Ring(float inner,float outer,float bottom,float top)
        {
            const int n=96;var v=new List<Vector3>();var t=new List<int>();
            for(int i=0;i<n;++i){float a=i*2*Mathf.PI/n;var d=new Vector3(Mathf.Cos(a),0,Mathf.Sin(a));v.Add(d*inner+Vector3.up*bottom);v.Add(d*outer+Vector3.up*bottom);v.Add(d*inner+Vector3.up*top);v.Add(d*outer+Vector3.up*top);}
            for(int i=0;i<n;++i){int a=i*4,b=((i+1)%n)*4;t.AddRange(new[]{a+2,b+2,a+3,b+2,b+3,a+3,a+1,a+3,b+1,a+3,b+3,b+1,a,b,a+2,b,b+2,a+2});}
            var mesh=new Mesh();mesh.SetVertices(v);mesh.SetTriangles(t,0);mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
        }

        public static void Capture()
        {
            var scene=SceneManager.GetActiveScene(); Require(scene.path==StationDecisionDayAuthoring.ScenePath,"Capture requires Daily.");
            var camera=Components<Camera>(scene).First(c=>c.isActiveAndEnabled && !c.targetTexture);
            var position=camera.transform.position;var rotation=camera.transform.rotation;var fov=camera.fieldOfView;var orthographic=camera.orthographic;
            var texture=camera.targetTexture;var rect=camera.rect;var active=RenderTexture.active;
            var canvases=Components<Canvas>(scene);var enabled=canvases.Select(c=>c.enabled).ToArray();
            bool dirty=scene.isDirty; RenderTexture target=null;
            try
            {
                Directory.CreateDirectory(ReportDirectory);foreach(var canvas in canvases)canvas.enabled=false;
                target=new RenderTexture(1600,1000,24,RenderTextureFormat.ARGB32);target.Create();
                camera.targetTexture=target;camera.rect=new Rect(0,0,1,1);camera.orthographic=false;camera.fieldOfView=62;
                string suffix=Application.isPlaying?"live":"saved";
                var overview=Render(camera,target,new Vector3(15,8,-17),Center+Vector3.up*.8f,Vector3.up,ReportDirectory+"/fountain-"+suffix+"-overview.png");
                // Both cameras stay below the hall vault, avoiding glass/roof occlusion in analysis images.
                camera.fieldOfView=90;
                var top=Render(camera,target,Center+Vector3.up*12,Center,Vector3.forward,ReportDirectory+"/fountain-"+suffix+"-top.png");
                var world=Components<StationDecisionWorld>(scene).Single();
                File.WriteAllText(ReportDirectory+"/capture-"+suffix+".json",JsonUtility.ToJson(new CaptureReceipt{
                    capturedUtc=DateTime.UtcNow.ToString("O"),scene=scene.path,mode=suffix,elapsedSimulationSeconds=Application.isPlaying?world.ElapsedSeconds:0,
                    fountainCenter=Center,fountainRadius=Radius,views=new[]{overview,top}},true));
            }
            finally
            {
                for(int i=0;i<canvases.Length;++i)if(canvases[i])canvases[i].enabled=enabled[i];
                camera.transform.SetPositionAndRotation(position,rotation);camera.fieldOfView=fov;camera.orthographic=orthographic;
                camera.targetTexture=texture;camera.rect=rect;RenderTexture.active=active;
                if(target){target.Release();UnityEngine.Object.DestroyImmediate(target);}
            }
            Require(scene.isDirty==dirty,"Temporary capture changed the saved dirty state.");
        }
        static CameraView Render(Camera camera,RenderTexture target,Vector3 lens,Vector3 aim,Vector3 up,string path)
        {
            camera.transform.SetPositionAndRotation(lens,Quaternion.LookRotation(aim-lens,up));
            RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=target});
            RenderTexture.active=target;var image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
            try{image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());}
            finally{UnityEngine.Object.DestroyImmediate(image);}
            return new CameraView{image=path,position=camera.transform.position,eulerAngles=camera.transform.eulerAngles,
                verticalFieldOfView=camera.fieldOfView,width=target.width,height=target.height,worldToCamera=camera.worldToCameraMatrix,projection=camera.projectionMatrix};
        }
    }
}
#endif
