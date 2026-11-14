#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.TrainStation.Social;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Editor
{
    /// <summary>Authors inspectable cameras once; no environment or camera is constructed by a player.</summary>
    public static class StationCameraAuthoring
    {
        public const string AssetRoot = "Assets/TrainStation/Cameras";
        public const string ScenePath = "Assets/TrainStation/Scenes/TrainStation.unity";
        public const string MountPrefabPath = AssetRoot + "/Prefabs/Station_CCTV_Mount.prefab";
        public const string OrbitPrefabPath = AssetRoot + "/Prefabs/Station_Hall_GameCamera.prefab";
        public const string ReportPath = "Reports/TrainStationCameras/authoring-validation.json";
        public const string RootName = "08 Cameras — hall orbit and fixed surveillance";

        [Serializable] public sealed class CoverageRecord
        {
            public string name, obstruction;
            public Vector3 worldPosition, viewport;
            public bool insideFrame, unobstructed;
        }
        [Serializable] public sealed class CameraRecord
        {
            public string id, name, purpose, mount;
            public Vector3 lens, attachment, target;
            public float height, fieldOfView, attachmentGap;
            public bool savedPrefab, activeHousing, primaryTargetClear;
            public List<CoverageRecord> coverage = new List<CoverageRecord>();
        }
        [Serializable] public sealed class CameraReport
        {
            public bool success, alreadyPopulated;
            public string scene = ScenePath, backup, completedUtc, error;
            public int cameras, fixedCameras, orbitCameras, coveragePoints, clearCoveragePoints;
            public List<CameraRecord> views = new List<CameraRecord>();
            public List<string> warnings = new List<string>();
        }
        sealed class Site
        {
            public string id, title, purpose;
            public Transform anchor;
            public Vector3 attachment, lens, target;
            public float fov;
            public Vector3[] samples;
        }

        [MenuItem("Efrei Research Lab/Train Station/Cameras/Author game camera and fixed CCTV")]
        public static void Populate()
        {
            RequireCleanEditor();
            var scene = SceneManager.GetSceneByPath(ScenePath);
            if (!scene.IsValid() || !scene.isLoaded) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var report = new CameraReport();
            try
            {
                var controls = Components<TrainStationViewControls>(scene).Single();
                if (Components<StationSurveillanceCamera>(scene).Any())
                {
                    report.alreadyPopulated = true; ValidateCore(scene, report); report.success = true; return;
                }
                if (controls.viewpoints == null || controls.viewpoints.Length != 6 || controls.viewpoints.Any(c => !c))
                    throw new InvalidOperationException("The six original saved viewpoints must exist before the camera upgrade.");
                report.backup = "BuildRequests/Backups/TrainStation.before-cameras." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".unity";
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(report.backup)); File.Copy(ScenePath, report.backup, false);
                EnsureFolder(AssetRoot + "/Prefabs");
                var group = controls.viewpoints[0].transform.parent;
                group.name = RootName;
                var originals = controls.viewpoints.ToArray();
                var sites = Sites(scene);
                var cameras = new Camera[15]; cameras[0] = originals[0];
                var names = new string[15]; names[0] = "01 / HALL - CAMERA LIBRE";
                MakeOrbit(scene, group, cameras[0]);
                var mountPrefab = EnsureMountPrefab();
                for (int i = 1; i < cameras.Length; i++)
                {
                    var site = sites[i - 1];
                    cameras[i] = MakeFixedCamera(group, mountPrefab, site, i < originals.Length ? originals[i] : null, i + 1);
                    names[i] = (i + 1).ToString("00") + " / " + site.title;
                }
                controls.viewpoints = cameras; controls.viewNames = names;
                controls.SelectView(0);
                StationCameraPresentation.Ensure(controls);
                StationCameraObstacles.Ensure(scene);
                EditorUtility.SetDirty(controls);
                Physics.SyncTransforms();
                ValidateCore(scene, report);
                AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save the authored station cameras.");
                report.success = true;
            }
            catch (Exception error)
            {
                report.error = error.ToString();
                if (!string.IsNullOrEmpty(report.backup)) EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                throw;
            }
            finally { Write(report); }
        }

        [MenuItem("Efrei Research Lab/Train Station/Cameras/Validate saved mountings and coverage")]
        public static void Validate()
        {
            var report = new CameraReport();
            try
            {
                var scene = SceneManager.GetSceneByPath(ScenePath);
                if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Open TrainStation first.");
                Physics.SyncTransforms(); ValidateCore(scene, report); report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally { Write(report); }
        }

        [MenuItem("Efrei Research Lab/Train Station/Cameras/Refine saved CCTV sightlines")]
        public static void RefineCoverage()
        {
            RequireCleanEditor();
            var report = new CameraReport { alreadyPopulated = true };
            var scene = SceneManager.GetSceneByPath(ScenePath);
            try
            {
                if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Open TrainStation first.");
                var saved = Components<StationSurveillanceCamera>(scene);
                if (saved.Length != 14) throw new InvalidOperationException("Author the complete camera catalogue before refining coverage.");
                report.backup = "BuildRequests/Backups/TrainStation.before-camera-refinement." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".unity";
                File.Copy(ScenePath,report.backup,false);
                foreach (var site in Sites(scene))
                {
                    var metadata = saved.Single(c => c.cameraId == site.id);
                    metadata.transform.SetParent(null,true);
                    metadata.transform.SetPositionAndRotation(site.attachment,Quaternion.identity); metadata.transform.localScale = Vector3.one;
                    ConfigurePlacement(metadata,site);
                    metadata.transform.SetParent(site.anchor,true);
                    foreach (var component in metadata.GetComponentsInChildren<Component>(true)) if (component) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }
                StationCameraPresentation.Ensure(Components<TrainStationViewControls>(scene).Single());
                StationCameraObstacles.Ensure(scene);
                Physics.SyncTransforms(); ValidateCore(scene,report);
                AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save refined camera sightlines.");
                report.success = true;
            }
            catch (Exception error) { report.error=error.ToString(); if (!string.IsNullOrEmpty(report.backup)) EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single); throw; }
            finally { Write(report); }
        }

        static void MakeOrbit(Scene scene, Transform group, Camera camera)
        {
            var rig = Group("01 Hall game camera — mouse orbit, pan and zoom", group);
            var focus = Group("Hall observation focus — move this to adjust the orbit centre", rig);
            focus.position = new Vector3(0, 2.1f, 0);
            camera.gameObject.SetActive(true); camera.transform.SetParent(rig, true);
            camera.name = "Game Camera — central hall"; Configure(camera, 67);
            var orbit = camera.gameObject.AddComponent<TrainStationOrbitCamera>();
            orbit.controlledCamera = camera; orbit.focusPoint = focus;
            orbit.defaultYaw = 15; orbit.defaultPitch = 24; orbit.defaultDistance = 19;
            orbit.minimumDistance = 5; orbit.maximumDistance = 33;
            orbit.minimumPitch = 8; orbit.maximumPitch = 72;
            orbit.panBoundsMin = new Vector3(-16, 0, -18); orbit.panBoundsMax = new Vector3(16, 0, 22);
            orbit.ignoredCollisionRoots = scene.GetRootGameObjects()
                .Where(g => g.name.StartsWith("11 Social life", StringComparison.Ordinal) || g.name.StartsWith("12 Rail operations", StringComparison.Ordinal))
                .Select(g => g.transform).Concat(new[] { group }).ToArray();
            orbit.ResetView();
            PrefabUtility.SaveAsPrefabAssetAndConnect(rig.gameObject, OrbitPrefabPath, InteractionMode.AutomatedAction);
            // Scene-only ignored roots are overrides; reusable prefab never depends on objects outside its hierarchy.
            orbit.ignoredCollisionRoots = scene.GetRootGameObjects()
                .Where(g => g.name.StartsWith("11 Social life", StringComparison.Ordinal) || g.name.StartsWith("12 Rail operations", StringComparison.Ordinal))
                .Select(g => g.transform).Concat(new[] { group }).ToArray();
            PrefabUtility.RecordPrefabInstancePropertyModifications(orbit);
        }

        static GameObject EnsureMountPrefab()
        {
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(MountPrefabPath);
            if (saved) return saved;
            var root = new GameObject("Station_CCTV_Mount");
            try
            {
                var metadata = root.AddComponent<StationSurveillanceCamera>();
                Box("Mount plate — attached to structural wall or column", root.transform, Vector3.zero, new Vector3(.22f, .24f, .055f), "steel");
                Box("Support arm", root.transform, new Vector3(0,0,.18f), new Vector3(.045f,.045f,.36f), "steel");
                var head = Group("Camera head — fixed surveyed orientation", root.transform); head.localPosition = new Vector3(0,0,.43f);
                Box("Weatherproof camera housing", head, new Vector3(0,0,-.13f), new Vector3(.20f,.17f,.30f), "white");
                Box("Dark optical face", head, new Vector3(0,0,.028f), new Vector3(.155f,.13f,.018f), "dark");
                Box("Rain hood", head, new Vector3(0,.098f,-.07f), new Vector3(.24f,.025f,.39f), "white");
                var lens = GameObject.CreatePrimitive(PrimitiveType.Cylinder); lens.name = "Lens ring"; lens.transform.SetParent(head,false);
                lens.transform.localPosition = new Vector3(0,0,.048f); lens.transform.localRotation = Quaternion.Euler(90,0,0);
                lens.transform.localScale = new Vector3(.075f,.011f,.075f); lens.GetComponent<Renderer>().sharedMaterial = Material("steel");
                UnityEngine.Object.DestroyImmediate(lens.GetComponent<Collider>());
                var camera = Group("CCTV optical camera",head).gameObject.AddComponent<Camera>();
                camera.transform.localPosition = new Vector3(0,0,.07f); Configure(camera,65); camera.enabled = false;
                camera.gameObject.AddComponent<AudioListener>().enabled = false; metadata.cameraComponent = camera;
                var target = Group("Purpose target — editable",root.transform); target.localPosition = new Vector3(0,-2,12); metadata.aimTarget = target;
                var samples = Group("Coverage samples — head height",root.transform);
                metadata.coveragePoints = new[] { Group("Primary activity",samples) }; metadata.coveragePoints[0].position = target.position;
                metadata.cameraId = "CCTV"; metadata.displayName = "Fixed station camera"; metadata.purpose = "Observe the assigned public activity zone.";
                return PrefabUtility.SaveAsPrefabAsset(root,MountPrefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        static Camera MakeFixedCamera(Transform parent, GameObject prefab, Site site, Camera original, int number)
        {
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab,parent);
            rig.name = number.ToString("00") + " CCTV — " + site.title;
            rig.transform.position = site.attachment;
            rig.transform.rotation = Quaternion.identity;
            var metadata = rig.GetComponent<StationSurveillanceCamera>();
            var head = rig.transform.Find("Camera head — fixed surveyed orientation");
            head.SetPositionAndRotation(site.lens,Quaternion.LookRotation(site.target - site.lens,Vector3.up));
            var prefabCamera = metadata.cameraComponent;
            if (original)
            {
                UnityEngine.Object.DestroyImmediate(prefabCamera.gameObject);
                original.gameObject.SetActive(true); original.transform.SetParent(head,false);
                original.transform.localPosition = new Vector3(0,0,.07f); original.transform.localRotation = Quaternion.identity;
                metadata.cameraComponent = original;
            }
            metadata.cameraComponent.name = "Optics — " + site.id;
            Configure(metadata.cameraComponent,site.fov); metadata.cameraComponent.enabled = false;
            var listener = metadata.cameraComponent.GetComponent<AudioListener>(); if (listener) listener.enabled = false;
            metadata.cameraId = site.id; metadata.displayName = site.title; metadata.purpose = site.purpose; metadata.mountAnchor = site.anchor;
            var pointsRoot = rig.transform.Find("Coverage samples — head height");
            foreach (Transform child in pointsRoot.Cast<Transform>().ToArray()) UnityEngine.Object.DestroyImmediate(child.gameObject);
            metadata.coveragePoints = new Transform[site.samples.Length];
            for (int i = 0; i < site.samples.Length; i++)
            {
                var point = Group("Activity coverage " + (i + 1).ToString("00"),pointsRoot); point.position = site.samples[i]; metadata.coveragePoints[i] = point;
            }
            ConfigurePlacement(metadata,site);
            // Existing primitive anchors have nonuniform scale. A world-axis-aligned rig root preserves
            // unit optical geometry when parented to their orthogonal wall/post axes; aim the head below it.
            rig.transform.SetParent(site.anchor,true);
            foreach (var component in rig.GetComponentsInChildren<Component>(true)) if (component) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            return metadata.cameraComponent;
        }

        static void ConfigurePlacement(StationSurveillanceCamera metadata,Site site)
        {
            var rig=metadata.transform;
            Vector3 plateNormal = site.anchor.name == "Canopy crossbeam 5" ? Vector3.down :
                site.anchor.name == "Cloison_gauche" ? site.anchor.right :
                site.anchor.name == "Cloison_droite" ? -site.anchor.right :
                site.anchor.name == "Limestone pier" ? Vector3.back :
                Vector3.right * Mathf.Sign(site.attachment.x - site.anchor.position.x);
            rig.Find("Mount plate — attached to structural wall or column").rotation = Quaternion.LookRotation(plateNormal,Mathf.Abs(Vector3.Dot(plateNormal,Vector3.up)) > .9f ? Vector3.forward : Vector3.up);
            var head=rig.Find("Camera head — fixed surveyed orientation");
            head.SetPositionAndRotation(site.lens,Quaternion.LookRotation(site.target-site.lens,Vector3.up));
            Configure(metadata.cameraComponent,site.fov);
            var arm=rig.Find("Support arm"); var armEnd=head.position-head.forward*.12f;
            arm.position=(site.attachment+armEnd)*.5f; arm.rotation=Quaternion.LookRotation(armEnd-site.attachment,Vector3.up);
            arm.localScale=new Vector3(.045f,.045f,Vector3.Distance(site.attachment,armEnd));
            metadata.mountAnchor=site.anchor; metadata.aimTarget.position=site.target;
            if(metadata.coveragePoints.Length != site.samples.Length)
            {
                var pointsRoot=rig.Find("Coverage samples — head height");
                var points=metadata.coveragePoints.ToList();
                while(points.Count < site.samples.Length) points.Add(Group("Activity coverage "+(points.Count+1).ToString("00"),pointsRoot));
                while(points.Count > site.samples.Length) { UnityEngine.Object.DestroyImmediate(points[points.Count-1].gameObject); points.RemoveAt(points.Count-1); }
                metadata.coveragePoints=points.ToArray();
            }
            for(int i=0;i<site.samples.Length;i++) metadata.coveragePoints[i].position=site.samples[i];
        }

        static Site[] Sites(Scene scene)
        {
            Transform Near(string name, Vector3 point) => Components<Transform>(scene).Where(t => t.name == name).OrderBy(t => Vector3.Distance(t.position,point)).First();
            Transform Shop(string name) => Components<Transform>(scene).Single(t => t.name == name);
            var cafe = Shop("02_Cafe_et_boulangerie"); var relay = Shop("03_Relay_presse_et_livres"); var tickets = Shop("01_Accueil_et_billetterie");
            var hall = Near("Iron column", new Vector3(-20,5,0));
            var parvis = Near("Limestone pier",new Vector3(12,4.5f,-28));
            var gallery = Near("Gallery post",new Vector3(48,3.5f,42));
            var sites = new List<Site>
            {
                new Site { id="EXT-01", title="PARVIS / ENTREES",purpose="Surveiller les portes, le parvis et les flux d'entree depuis la facade, sous l'auvent.", anchor=parvis,
                    attachment=new Vector3(12,4.85f,-28.53f),lens=new Vector3(12,4.73f,-29.12f),target=new Vector3(0,1.5f,-43),fov=74,
                    samples=new[]{new Vector3(0,1.6f,-35),new Vector3(-12,1.6f,-44),new Vector3(12,1.6f,-44),new Vector3(0,1.6f,-51)} },
                Platform(scene,-36,1,false),
                new Site { id="HALL-01",title="ACCUEIL / FILE",purpose="Observer l'agent d'accueil, le client servi et la file physique dans le hall.",anchor=hall,
                    attachment=new Vector3(-19.79f,6.2f,0),lens=new Vector3(-19.18f,6.05f,0),target=new Vector3(0,1.4f,-.8f),fov=66,
                    samples=new[]{new Vector3(0,1.6f,5.05f),new Vector3(0,1.6f,2.45f),new Vector3(0,1.6f,-4.63f),new Vector3(0,1.6f,-10.53f)} },
                ShopSite(cafe,"SHOP-01","CAFE / COMPTOIR","Observer le personnel, la commande, la file et l'entree du cafe.",new Vector3(7.47f,3.10f,2f),new Vector3(6.92f,3.21f,2f),new Vector3(-.9f,1.2f,.5f),65,
                    new[]{new Vector3(-.9f,1.6f,4.35f),new Vector3(-.9f,1.6f,1.1f),new Vector3(.5f,1.6f,-4.7f),new Vector3(3.8f,1.6f,-.7f),new Vector3(-.9f,1.2f,4.35f),new Vector3(-.9f,1.2f,1.1f)}),
                new Site { id="HALL-02",title="CONCOURSE / CORRESPONDANCES",purpose="Observer les cheminements transversaux et les acces aux quais depuis le poteau de galerie.",anchor=gallery,
                    attachment=new Vector3(47.84f,5.3f,42),lens=new Vector3(47.3f,5.15f,41.9f),target=new Vector3(0,1.5f,35.5f),fov=64,
                    samples=new[]{new Vector3(36,1.6f,36),new Vector3(12,1.6f,36),new Vector3(-12,1.6f,36),new Vector3(-36,1.6f,36)} },
                ShopSite(relay,"SHOP-02","RELAY / CAISSE","Observer la caisse, la file d'achat et l'allee d'entree du commerce de presse.",new Vector3(-7.47f,3.22f,-.2f),new Vector3(-6.92f,3.17f,-.2f),new Vector3(-1.8f,1.4f,-2.9f),80,
                    new[]{new Vector3(-4.9f,1.6f,-2.15f),new Vector3(-4.9f,1.6f,-4.95f),new Vector3(-.1f,1.6f,-4.95f),new Vector3(1.4f,1.6f,-2.4f)}),
                ShopSite(tickets,"SHOP-03","BILLETTERIE / GUICHETS","Observer les guichets, l'agent et la file de vente des billets.",new Vector3(-7.47f,3.22f,-2.7f),new Vector3(-6.92f,3.17f,-2.7f),new Vector3(0,1.4f,-.1f),80,
                    new[]{new Vector3(0,1.6f,3.35f),new Vector3(0,1.6f,.75f),new Vector3(0,1.6f,-5.25f),new Vector3(4.8f,1.6f,2.1f)}),
                Platform(scene,-36,1,true), Platform(scene,-12,2,false), Platform(scene,-12,2,true),
                Platform(scene,12,3,false), Platform(scene,12,3,true), Platform(scene,36,4,false), Platform(scene,36,4,true)
            };
            return sites.ToArray();
        }

        static Site ShopSite(Transform bay,string id,string title,string purpose,Vector3 attachment,Vector3 lens,Vector3 target,float fov,Vector3[] points)
        {
            return new Site { id=id,title=title,purpose=purpose,anchor=bay.Find(attachment.x > 0 ? "Cloison_droite" : "Cloison_gauche"),
                attachment=bay.TransformPoint(attachment),lens=bay.TransformPoint(lens),target=bay.TransformPoint(target),fov=fov,samples=points.Select(bay.TransformPoint).ToArray() };
        }

        static Site Platform(Scene scene,float x,int platform,bool far)
        {
            var root = Components<Transform>(scene).Single(t => t.name == "Quai " + platform + " - Voies " + (2 * platform - 1) + " et " + (2 * platform));
            var anchor = far ? root.Find("Canopy steel structure/Canopy crossbeam 5") : Components<Transform>(scene)
                .Where(t => t.name == "Gallery post").OrderBy(t => Vector3.Distance(t.position,new Vector3(x,3.5f,42))).First();
            return new Site
            {
                id="QUAI-"+platform+(far?"-B":"-A"),title="QUAI "+platform+(far?" / RETOUR":" / ACCES"),
                purpose=far?"Observer les zones d'attente et d'embarquement depuis la traverse de marquise, avec recouvrement de la camera d'acces.":"Observer l'arrivee des voyageurs, l'embarquement et le debut du quai depuis le poteau de galerie.",
                anchor=anchor,attachment=far?new Vector3(x-2.55f,5.23f,193):new Vector3(x+.16f,4.75f,42),
                lens=far?new Vector3(x-2.55f,4.9f,192.62f):new Vector3(x+.65f,4.63f,42.32f),
                target=far?new Vector3(x,1.5f,128):new Vector3(x+2,1.5f,101), fov=58,
                samples=far?new[]{new Vector3(x-2.35f,1.6f,180),new Vector3(x+2.35f,1.6f,163),new Vector3(x-2.35f,1.6f,143),new Vector3(x+2.35f,1.6f,122)}:
                    new[]{new Vector3(x+2.35f,1.6f,55),new Vector3(x-2.35f,1.6f,73),new Vector3(x+2.35f,1.6f,102),new Vector3(x-2.35f,1.6f,131)}
            };
        }

        static void ValidateCore(Scene scene, CameraReport report)
        {
            var controls = Components<TrainStationViewControls>(scene).Single();
            report.cameras = Components<Camera>(scene).Length;
            var fixedCameras = Components<StationSurveillanceCamera>(scene); report.fixedCameras = fixedCameras.Length;
            var orbitCameras = Components<TrainStationOrbitCamera>(scene); report.orbitCameras = orbitCameras.Length;
            if (report.cameras != 15 || report.fixedCameras != 14 || report.orbitCameras != 1) throw new InvalidOperationException("Expected one game camera and fourteen fixed CCTV cameras.");
            if (controls.viewpoints.Length != report.cameras || controls.viewNames.Length != report.cameras || controls.viewpoints.Distinct().Count() != report.cameras)
                throw new InvalidOperationException("Saved camera selection references are incomplete or duplicated.");
            if (controls.viewpoints[0] != orbitCameras[0].controlledCamera || !orbitCameras[0].focusPoint) throw new InvalidOperationException("The first view must be the authored central-hall orbit camera.");
            foreach (var fixedCamera in fixedCameras)
            {
                var camera = fixedCamera.cameraComponent;
                if (!camera || !fixedCamera.mountAnchor || !fixedCamera.aimTarget || fixedCamera.coveragePoints == null || fixedCamera.coveragePoints.Length < 3)
                    throw new InvalidOperationException("Missing physical mounting or coverage references on " + fixedCamera.name);
                var mountingRenderer = fixedCamera.mountAnchor.GetComponent<Renderer>();
                if (!mountingRenderer || fixedCamera.mountAnchor.IsChildOf(fixedCamera.transform)) throw new InvalidOperationException("CCTV must be attached to existing structural geometry: " + fixedCamera.name);
                if (fixedCamera.transform.parent != fixedCamera.mountAnchor) throw new InvalidOperationException("CCTV must move together with its physical mounting anchor.");
                if (Vector3.Distance(camera.transform.lossyScale,Vector3.one) > .01f) throw new InvalidOperationException("Structural anchor scale distorted the camera optics: " + fixedCamera.name + " scale=" + camera.transform.lossyScale);
                float gap = Vector3.Distance(mountingRenderer.bounds.ClosestPoint(fixedCamera.transform.position),fixedCamera.transform.position);
                if (gap > .20f) throw new InvalidOperationException("CCTV mount floats away from its structural anchor: " + fixedCamera.name + " gap=" + gap);
                var record = new CameraRecord { id=fixedCamera.cameraId,name=fixedCamera.displayName,purpose=fixedCamera.purpose,mount=Path(fixedCamera.mountAnchor),
                    lens=camera.transform.position,attachment=fixedCamera.transform.position,target=fixedCamera.aimTarget.position,height=camera.transform.position.y,
                    fieldOfView=camera.fieldOfView,attachmentGap=gap,savedPrefab=PrefabUtility.IsPartOfPrefabInstance(fixedCamera),activeHousing=fixedCamera.gameObject.activeInHierarchy };
                if (!record.savedPrefab || !record.activeHousing) throw new InvalidOperationException("Every CCTV needs an active saved prefab housing.");
                float aspect = camera.aspect; camera.aspect = 16f / 9f;
                try
                {
                    record.primaryTargetClear = Unobstructed(scene,camera.transform.position,fixedCamera.aimTarget.position,out string primaryBlock);
                    if (!record.primaryTargetClear) report.warnings.Add(fixedCamera.cameraId + " primary sightline blocked by " + primaryBlock);
                    foreach (var point in fixedCamera.coveragePoints)
                    {
                        Vector3 viewport = camera.WorldToViewportPoint(point.position);
                        bool inFrame = viewport.z > camera.nearClipPlane && viewport.z < camera.farClipPlane && viewport.x >= .02f && viewport.x <= .98f && viewport.y >= .02f && viewport.y <= .98f;
                        bool clear = Unobstructed(scene,camera.transform.position,point.position,out string blocker);
                        record.coverage.Add(new CoverageRecord { name=point.name,worldPosition=point.position,viewport=viewport,insideFrame=inFrame,unobstructed=clear,obstruction=blocker });
                        report.coveragePoints++; if (inFrame && clear) report.clearCoveragePoints++;
                        if (!inFrame) throw new InvalidOperationException(fixedCamera.cameraId + " misses its authored activity point: " + point.name + " " + viewport);
                        if (!clear) report.warnings.Add(fixedCamera.cameraId + " " + point.name + " blocked by " + blocker);
                    }
                }
                finally { camera.aspect = aspect; }
                report.views.Add(record);
            }
            if (fixedCameras.Select(c => c.cameraId).Distinct().Count() != 14) throw new InvalidOperationException("CCTV IDs must be unique.");
        }

        public static bool Unobstructed(Scene scene,Vector3 from,Vector3 to,out string blocker)
        {
            Vector3 delta = to - from;
            foreach (var hit in Physics.RaycastAll(from,delta.normalized,delta.magnitude-.08f,~0,QueryTriggerInteraction.Ignore).OrderBy(h=>h.distance))
            {
                var root = hit.transform.root;
                if (root.gameObject.scene != scene || root.name.StartsWith("11 Social life",StringComparison.Ordinal) || root.name.StartsWith("12 Rail operations",StringComparison.Ordinal)) continue;
                if (hit.collider.GetComponentInParent<StationSurveillanceCamera>()) continue;
                blocker = Path(hit.transform); return false;
            }
            blocker = ""; return true;
        }
        static string Path(Transform t) { string value=t.name; while(t.parent) { t=t.parent; value=t.name+"/"+value; } return value; }
        static void Configure(Camera camera,float fov)
        {
            camera.nearClipPlane=.06f; camera.farClipPlane=600; camera.fieldOfView=fov; camera.clearFlags=CameraClearFlags.Skybox; camera.allowHDR=true;
            var data=camera.GetUniversalAdditionalCameraData(); data.renderPostProcessing=true; data.antialiasing=AntialiasingMode.FastApproximateAntialiasing;
        }
        static Transform Group(string name,Transform parent) { var go=new GameObject(name); go.transform.SetParent(parent,false); return go.transform; }
        static void Box(string name,Transform parent,Vector3 position,Vector3 scale,string material)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube); go.name=name; go.transform.SetParent(parent,false); go.transform.localPosition=position; go.transform.localScale=scale;
            go.GetComponent<Renderer>().sharedMaterial=Material(material); UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        }
        static Material Material(string name) => AssetDatabase.LoadAssetAtPath<Material>("Assets/TrainStation/Materials/Station_"+name+".mat");
        static T[] Components<T>(Scene scene) where T:Component => scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<T>(true)).ToArray();
        static void EnsureFolder(string path) { if(AssetDatabase.IsValidFolder(path)) return; EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\','/')); AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(path).Replace('\\','/'),System.IO.Path.GetFileName(path)); }
        static void RequireCleanEditor()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play mode before authoring station cameras.");
            for(int i=0;i<SceneManager.sceneCount;i++) if(SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save current scene changes before authoring cameras.");
        }
        static void Write(CameraReport report) { report.completedUtc=DateTime.UtcNow.ToString("O"); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true)); }
    }
}
#endif
