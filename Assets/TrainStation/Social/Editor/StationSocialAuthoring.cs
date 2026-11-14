#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.HumanAnimation;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Explicit saved-scene authoring. No actors, markers, props or navigation geometry are generated at runtime.</summary>
    public static class StationSocialAuthoring
    {
        public const string ScenePath = "Assets/TrainStation/Scenes/TrainStation.unity";
        public const string RootName = "11 Social life — authored actors, seats and navigation";
        public const string AssetRoot = "Assets/TrainStation/Social";
        public const string ReceptionistPrefabPath = AssetRoot + "/Prefabs/Station_Receptionist.prefab";
        public const string CleanerPrefabPath = AssetRoot + "/Prefabs/Station_Cleaner.prefab";
        public const string PassengerPrefabPath = AssetRoot + "/Prefabs/Station_Passenger.prefab";
        public const string LuggagePrefabPath = AssetRoot + "/Prefabs/Station_Luggage.prefab";
        public const string NavigationPath = AssetRoot + "/Navigation/Station_Hall_Concourse.asset";
        const string ModelPath = "Assets/HumanAnimation/Source/Mixamo/Models/XBot.fbx";
        const string ReportPath = "Reports/TrainStation/social-authoring.json";

        [MenuItem("Efrei Research Lab/Train Station/Social/Populate saved station")]
        public static void Build()
        {
            RequireEditAndClean();
            if (!File.Exists(ScenePath)) throw new FileNotFoundException("Create the saved TrainStation scene first.");
            var scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (scene.GetRootGameObjects().Any(g => g.name == RootName)) { Validate(); return; }
            var library = StationSocialAnimationAssets.EnsureResources();
            RequireMotions(library);
            EnsureFolders();
            string backup = "BuildRequests/Backups/TrainStation.before-social." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".unity";
            Directory.CreateDirectory(Path.GetDirectoryName(backup)); File.Copy(ScenePath, backup, false);
            var station = scene.GetRootGameObjects().First(g => g.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Point_information_central"));
            var social = new GameObject(RootName);
            SceneManager.MoveGameObjectToScene(social, scene);
            var world = social.AddComponent<StationSocialWorld>();
            social.AddComponent<NavMeshModifier>().ignoreFromBuild = true;
            var navigation = Group("01 Navigation — baked hall and concourse", social.transform);
            var seatsRoot = Group("02 Seats — actual hall bench places", social.transform);
            var information = Group("03 Accueil — staff, service zone and queue", social.transform);
            var routes = Group("04 Cleaning routes — editable work points", social.transform);
            var actors = Group("05 Actors — reusable prefab instances", social.transform);
            var props = Group("07 Maintenance equipment", social.transform);

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (!source) throw new InvalidOperationException("Saved XBot model missing.");
            var receptionPrefab = EnsureActorPrefab("Receptionist", ReceptionistPrefabPath, source, library, new Color(.045f, .22f, .47f));
            var cleanerPrefab = EnsureActorPrefab("Cleaner", CleanerPrefabPath, source, library, new Color(.13f, .48f, .30f));
            var passengerPrefab = EnsureActorPrefab("Passenger", PassengerPrefabPath, source, library, new Color(.43f, .19f, .36f));
            passengerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PassengerPrefabPath);
            // Existing floors/obstacles supply the bake; bench tops must never become stepping surfaces.
            foreach (var bench in station.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Banquette_", StringComparison.Ordinal)))
            {
                var modifier = bench.GetComponent<NavMeshModifier>() ?? bench.gameObject.AddComponent<NavMeshModifier>();
                modifier.overrideArea = true; modifier.area = 1;
            }
            var surface = navigation.gameObject.AddComponent<NavMeshSurface>();
            surface.agentTypeID = 0; surface.collectObjects = CollectObjects.Volume;
            surface.center = new Vector3(0, 1.1f, 8.0f); surface.size = new Vector3(42f, 3.2f, 67f);
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders; surface.layerMask = ~0;
            surface.ignoreNavMeshAgent = true; surface.ignoreNavMeshObstacle = true;
            surface.overrideVoxelSize = true; surface.voxelSize = .08f; surface.minRegionArea = .5f;
            BakeSurface(surface);

            var desk = information.gameObject.AddComponent<StationInformationDesk>();
            var deskGeometry = station.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Point_information_central");
            var staffObject = Instantiate(receptionPrefab, actors, "Accueil — agent information");
            staffObject.transform.SetPositionAndRotation(deskGeometry.TransformPoint(new Vector3(0, 0, .05f)), Quaternion.Euler(0, 180, 0));
            var staff = staffObject.GetComponent<StationReceptionist>();
            desk.receptionist = staff; desk.receptionRadius = 3f; desk.conversationDuration = 7f;
            StationQueueAuthoring.Configure(desk);
            var queue = Group("Queue — first position is service front", information);
            desk.queueSlots = new Transform[12];
            for (int i = 0; i < desk.queueSlots.Length; i++)
            {
                var point = Group(i == 0 ? "00 Service position" : "Queue position " + i.ToString("00"), queue);
                point.position = Snap(deskGeometry.TransformPoint(new Vector3(0, 0, -2.55f - i * StationQueueAuthoring.QueueSpacing)));
                point.rotation = Quaternion.identity; desk.queueSlots[i] = point;
            }
            var look = Group("Staff look target — customer at service front", information);
            look.position = desk.queueSlots[0].position + Vector3.up * 1.55f; staff.interactionLookTarget = look;
            world.informationDesk = desk;

            Vector3 seatedHip = PrefabSeatedHip(passengerPrefab);
            var seats = new List<StationSeat>();
            var islands = station.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Ilot_attente_", StringComparison.Ordinal)).OrderBy(t => t.position.z).ThenBy(t => t.position.x).ToArray();
            if (islands.Length != 6) throw new InvalidOperationException("Expected the six existing hall waiting islands; no replacement benches are generated.");
            for (int islandIndex = 0; islandIndex < islands.Length; islandIndex++)
            {
                var island = islands[islandIndex];
                Vector3 inward = Vector3.right * -Mathf.Sign(island.position.x);
                var bench = island.GetComponentsInChildren<Transform>().First(t => t.name == "Banquette_4_places" && Vector3.Dot(-t.forward, inward) > .8f);
                var group = Group("Bench " + (islandIndex + 1).ToString("00") + " — " + island.name, seatsRoot);
                for (int place = 0; place < 4; place++)
                {
                    float alongBench = -.85f * 1.5f + place * .85f;
                    var go = Group("Seat " + (seats.Count + 1).ToString("00"), group).gameObject;
                    var seat = go.AddComponent<StationSeat>(); seat.seatId = "Hall-" + (islandIndex + 1).ToString("00") + "-" + (place + 1);
                    Quaternion facing = bench.rotation * Quaternion.Euler(0, 180, 0);
                    Vector3 targetHip = bench.TransformPoint(new Vector3(alongBench, .65f, -.025f));
                    seat.seatedPose = Group("Seated actor root — clip calibrated", go.transform);
                    seat.seatedPose.SetPositionAndRotation(targetHip - facing * seatedHip, facing);
                    seat.approachPoint = Group("Standing approach — NavMesh floor", go.transform);
                    seat.approachPoint.SetPositionAndRotation(Snap(bench.TransformPoint(new Vector3(alongBench, 0, -1.20f))), facing);
                    seat.luggagePoint = null;
                    seats.Add(seat);
                }
            }
            world.seats = seats.ToArray();
            var passengers = new List<StationPassenger>();
            for (int i = 0; i < 12; i++)
            {
                var go = Instantiate(passengerPrefab, actors, "Passenger " + (i + 1).ToString("00") + " — seated traveller");
                var passenger = go.GetComponent<StationPassenger>();
                var seat = seats[(i / 2) * 4 + (i % 2) * 2];
                passenger.motion.agent.enabled = false; passenger.world = world; passenger.informationDesk = desk; passenger.initialSeat = seat;
                passenger.randomSeed = 1700 + i * 73; passenger.automaticRequests = true;
                passenger.requestInterval = new Vector2(45f, 110f);
                passenger.navigationTimeout = 80f; passenger.queueWaitTimeout = 150f;
                go.transform.SetPositionAndRotation(seat.seatedPose.position, seat.seatedPose.rotation);
                passenger.luggage = null;
                TintPassenger(go, i); passengers.Add(passenger);
            }
            world.passengers = passengers.ToArray();
            Vector3[][] routePoints =
            {
                new[] { new Vector3(-6,0,-17), new Vector3(-6,0,-5), new Vector3(-6,0,12), new Vector3(-6,0,23), new Vector3(-18,0,23), new Vector3(-18,0,-17) },
                new[] { new Vector3(6,0,-18), new Vector3(6,0,-6), new Vector3(6,0,11), new Vector3(6,0,23), new Vector3(18,0,23), new Vector3(18,0,-18) },
                new[] { new Vector3(-17,0,35), new Vector3(-7,0,38), new Vector3(8,0,36), new Vector3(18,0,38), new Vector3(10,0,29), new Vector3(-3,0,29) }
            };
            world.cleaners = new StationCleaner[3];
            for (int i = 0; i < 3; i++)
            {
                var route = Group("Cleaner " + (i + 1) + " — patrol and sweep route", routes);
                var nodes = routePoints[i].Select((position, n) => { var point = Group("Work point " + (n + 1).ToString("00"), route); point.position = Snap(position); return point; }).ToArray();
                var go = Instantiate(cleanerPrefab, actors, "Cleaner " + (i + 1).ToString("00") + " — entretien");
                go.transform.SetPositionAndRotation(nodes[0].position, Quaternion.LookRotation(nodes[1].position - nodes[0].position));
                var cleaner = go.GetComponent<StationCleaner>(); cleaner.world = world; cleaner.workPoints = nodes; cleaner.workDuration = 6f + i; cleaner.patrolSpeed = 1f; cleaner.yieldRadius = 1.6f;
                world.cleaners[i] = cleaner;
                MaintenanceCart(props, new Vector3(i == 0 ? -19 : 19, 0, i == 2 ? 27.8f : -22), i);
            }
            StationSocialPresentation.Ensure(world);
            foreach(var component in social.GetComponentsInChildren<Component>(true))
                if(component && PrefabUtility.IsPartOfPrefabInstance(component))PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            Validate();
            Debug.Log("TrainStation social population authored and saved. Original scene backup: " + backup);
        }

        static GameObject EnsureActorPrefab(string role, string path, GameObject source, HumanAnimationLibrary library, Color color)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path); if (existing) return existing;
            var go = new GameObject("Station_" + role);
            try
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(source); model.name = "XBot — shared Humanoid model"; model.transform.SetParent(go.transform, false);
                var animator = model.GetComponentInChildren<Animator>(); animator.runtimeAnimatorController = library.controller; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var player = go.AddComponent<HumanAnimationPlayer>(); player.Configure(animator, library);
                var agent = go.AddComponent<NavMeshAgent>(); agent.radius = .32f; agent.height = 1.8f; agent.baseOffset = 0; agent.speed = 1.15f; agent.angularSpeed = 240f;
                agent.acceleration = 5f; agent.stoppingDistance = .12f; agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance; agent.avoidancePriority = role == "Passenger" ? 40 : 80;
                var motion = go.AddComponent<StationActorMotion>(); motion.animationPlayer = player; motion.agent = agent;
                StationNativeNavigationAuthoring.ConfigureMotion(motion);
                motion.idleId = FindMotion(library, "Neutral Idle").id; motion.walkId = FindMotion(library, "walking").id;
                motion.sitIdleId = FindMotion(library, "Sitting Idle").id; motion.sitDownId = FindMotion(library, "Sit Down").id;
                motion.standUpId = FindMotion(library, "Stand Up").id; motion.talkId = FindMotion(library, "Talking").id; motion.sweepId = StationSocialAnimationAssets.SweepId;
                ColorModel(model, Material(role + "_Uniform", color));
                if (role == "Receptionist")
                {
                    go.AddComponent<StationReceptionist>().motion = motion; agent.enabled = false;
                    SamplePose(animator, FindMotion(library, "Neutral Idle").clip, .27f);
                    Badge(animator, "ACCUEIL", new Color(.89f, .10f, .37f));
                }
                else if (role == "Cleaner")
                {
                    var cleaner = go.AddComponent<StationCleaner>(); cleaner.motion = motion;
                    SamplePose(animator, library.Find(StationSocialAnimationAssets.SweepId).clip, .23f);
                    Badge(animator, "ENTRETIEN", new Color(.95f, .76f, .12f));
                    var tool = BuildBroom(go.transform); cleaner.cleaningTool = tool;
                    var grip = animator.gameObject.AddComponent<StationCleaningTool>(); grip.motion = motion; grip.cleaner = cleaner; grip.toolRoot = tool;
                }
                else
                {
                    var passenger = go.AddComponent<StationPassenger>(); passenger.motion = motion; agent.enabled = false;
                    SamplePose(animator, FindMotion(library, "Sitting Idle").clip, .30f);
                    passenger.luggage = null;
                }
                return PrefabUtility.SaveAsPrefabAsset(go, path);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        /// <summary>Intentionally persist sampled bone transforms; never leave an AnimationMode session active.</summary>
        static void SamplePose(Animator animator, AnimationClip clip, float normalizedTime)
        {
            if (AnimationMode.InAnimationMode()) throw new InvalidOperationException("Stop the existing animation preview before authoring initial poses.");
            var transforms = animator.GetComponentsInChildren<Transform>(true).Where(t => t != animator.transform).ToArray();
            var driver = ScriptableObject.CreateInstance<AnimationModeDriver>(); driver.hideFlags = HideFlags.HideAndDontSave;
            Vector3[] positions = null, scales = null; Quaternion[] rotations = null;
            try
            {
                AnimationMode.StartAnimationMode(driver); AnimationMode.BeginSampling();
                try { AnimationMode.SampleAnimationClip(animator.gameObject, clip, clip.length * normalizedTime); }
                finally { AnimationMode.EndSampling(); }
                positions = transforms.Select(t => t.localPosition).ToArray(); rotations = transforms.Select(t => t.localRotation).ToArray(); scales = transforms.Select(t => t.localScale).ToArray();
            }
            finally { if (AnimationMode.InAnimationMode(driver)) AnimationMode.StopAnimationMode(driver); UnityEngine.Object.DestroyImmediate(driver); }
            for (int i = 0; i < transforms.Length; i++)
            {
                transforms[i].localPosition = positions[i]; transforms[i].localRotation = rotations[i]; transforms[i].localScale = scales[i];
                if(PrefabUtility.IsPartOfPrefabInstance(transforms[i]))PrefabUtility.RecordPrefabInstancePropertyModifications(transforms[i]);
            }
            animator.transform.localPosition = Vector3.zero; animator.transform.localRotation = Quaternion.identity; animator.transform.localScale = Vector3.one;
        }
        static Vector3 PrefabSeatedHip(GameObject prefab)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try { var animator = instance.GetComponentInChildren<Animator>(); return instance.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.Hips).position); }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }

        static void Badge(Animator animator, string label, Color color)
        {
            var chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            var badge = Shape("Badge — " + label, PrimitiveType.Cube, chest, Vector3.zero, new Vector3(.17f, .095f, .025f), Material("Badge_" + label, color));
            badge.transform.position = animator.transform.TransformPoint(new Vector3(-.09f, 1.37f, .11f)); badge.transform.rotation = animator.transform.rotation;
        }
        static Transform BuildBroom(Transform parent)
        {
            var root = Group("Balai — brush origin, shaft along +Y", parent); root.localPosition = new Vector3(.32f, .015f, .62f);
            Shape("Manche aluminium", PrimitiveType.Cylinder, root, new Vector3(0, .76f, 0), new Vector3(.035f, .76f, .035f), Material("ToolSteel", new Color(.55f,.61f,.65f)));
            Shape("Tete du balai", PrimitiveType.Cube, root, new Vector3(0,.045f,0),new Vector3(.48f,.07f,.16f),Material("ToolGreen",new Color(.16f,.46f,.30f)));
            for (int i=0;i<8;i++) Shape("Brosse " + i,PrimitiveType.Cube,root,new Vector3(-.21f+i*.06f,.012f,0),new Vector3(.045f,.03f,.14f),Material("Bristles",new Color(.25f,.22f,.17f)));
            return root;
        }
        /// <summary>Legacy command alias now enforces the saved no-luggage scene configuration.</summary>
        public static void UpgradePassengerLuggage() => StationNoLuggageAuthoring.RemoveAll();
        static bool UpgradeCleanerFloorContactAsset()
        {
            var root=PrefabUtility.LoadPrefabContents(CleanerPrefabPath);
            try
            {
                var grip=root.GetComponentInChildren<StationCleaningTool>(true);
                // Only replace the original generated default; leave subsequent authored adjustments intact.
                if(!grip||!Mathf.Approximately(grip.groundOffset,.06f))return false;
                grip.groundOffset=.005f;PrefabUtility.RecordPrefabInstancePropertyModifications(grip);
                PrefabUtility.SaveAsPrefabAsset(root,CleanerPrefabPath);return true;
            }
            finally{PrefabUtility.UnloadPrefabContents(root);}
        }

        [Serializable] sealed class CleanerPoseReport
        {
            public bool success, previewStopped;
            public int sceneCleaners;
            public List<float> hipHeights = new List<float>();
            public List<float> headHeights = new List<float>();
        }
        /// <summary>Refresh this session's saved cleaning pose after correcting the Humanoid root-height channel.</summary>
        [MenuItem("Efrei Research Lab/Train Station/Social/Refresh saved cleaner poses")]
        public static void RefreshCleanerPose()
        {
            RequireEditAndClean();
            var scene=SceneManager.GetActiveScene();if(scene.path!=ScenePath)scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
            var social=scene.GetRootGameObjects().Single(g=>g.name==RootName);var world=social.GetComponent<StationSocialWorld>();
            var library=StationSocialAnimationAssets.EnsureResources();
            var clip=library.Find(StationSocialAnimationAssets.SweepId)?.clip;
            if(!clip)throw new InvalidOperationException("The corrected saved sweeping clip is missing.");
            string stamp=DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");Directory.CreateDirectory("BuildRequests/Backups");
            File.Copy(ScenePath,"BuildRequests/Backups/TrainStation.before-cleaner-pose."+stamp+".unity",false);
            File.Copy(CleanerPrefabPath,"BuildRequests/Backups/Station_Cleaner.before-pose."+stamp+".prefab",false);
            var report=new CleanerPoseReport();
            var prefab=PrefabUtility.LoadPrefabContents(CleanerPrefabPath);
            try
            {
                RefreshOneCleanerPose(prefab.GetComponent<StationCleaner>(),clip,report);
                PrefabUtility.SaveAsPrefabAsset(prefab,CleanerPrefabPath);
            }
            finally{PrefabUtility.UnloadPrefabContents(prefab);}
            // Explicitly sample instances too: previous authored pose overrides must not retain the old body height.
            foreach(var cleaner in world.cleaners)
            {
                if(!cleaner||PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(cleaner.gameObject)!=CleanerPrefabPath)
                    throw new InvalidOperationException("Cleaner pose refresh only supports this session's saved cleaner prefab instances.");
                RefreshOneCleanerPose(cleaner,clip,report);report.sceneCleaners++;
            }
            report.previewStopped=!AnimationMode.InAnimationMode();
            report.success=report.sceneCleaners==3&&report.previewStopped&&report.hipHeights.All(y=>y>.65f&&y<1.35f)&&report.headHeights.All(y=>y>1.25f&&y<2.1f);
            Directory.CreateDirectory("Reports/TrainStation");File.WriteAllText("Reports/TrainStation/cleaner-pose-refresh.json",JsonUtility.ToJson(report,true));
            if(!report.success)throw new InvalidOperationException("Corrected cleaner pose is not standing above the floor; see cleaner-pose-refresh.json.");
            AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);Validate();
        }
        static void RefreshOneCleanerPose(StationCleaner cleaner,AnimationClip clip,CleanerPoseReport report)
        {
            if(!cleaner||!cleaner.motion||!cleaner.motion.animationPlayer)throw new InvalidOperationException("Cleaner prefab motion references are missing.");
            var animator=cleaner.motion.animationPlayer.TargetAnimator;SamplePose(animator,clip,.23f);
            var badge=animator.GetComponentsInChildren<Transform>(true).FirstOrDefault(t=>t.name=="Badge — ENTRETIEN");
            if(badge)
            {
                badge.position=animator.transform.TransformPoint(new Vector3(-.09f,1.37f,.11f));badge.rotation=animator.transform.rotation;
                if(PrefabUtility.IsPartOfPrefabInstance(badge))PrefabUtility.RecordPrefabInstancePropertyModifications(badge);
            }
            var grip=animator.GetComponent<StationCleaningTool>();
            if(grip&&grip.toolRoot)
            {
                var position=grip.toolRoot.localPosition;position.y=grip.groundOffset;grip.toolRoot.localPosition=position;
                if(PrefabUtility.IsPartOfPrefabInstance(grip.toolRoot))PrefabUtility.RecordPrefabInstancePropertyModifications(grip.toolRoot);
            }
            report.hipHeights.Add(cleaner.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.Hips).position).y);
            report.headHeights.Add(cleaner.transform.InverseTransformPoint(animator.GetBoneTransform(HumanBodyBones.Head).position).y);
        }
        static void MaintenanceCart(Transform parent,Vector3 position,int index)
        {
            var cart=Group("Maintenance cart "+(index+1),parent);cart.position=position;
            var grey=Material("CartGrey",new Color(.27f,.33f,.36f)); var yellow=Material("SafetyYellow",new Color(.93f,.68f,.08f));
            Shape("Plateau",PrimitiveType.Cube,cart,new Vector3(0,.20f,0),new Vector3(.72f,.12f,1.0f),grey);
            Shape("Seau",PrimitiveType.Cylinder,cart,new Vector3(0,.45f,-.18f),new Vector3(.40f,.24f,.40f),yellow);
            Shape("Sac de collecte",PrimitiveType.Cube,cart,new Vector3(0,.66f,.31f),new Vector3(.62f,.75f,.33f),grey);
            for(int x=-1;x<=1;x+=2)for(int z=-1;z<=1;z+=2) Shape("Roulette",PrimitiveType.Sphere,cart,new Vector3(x*.28f,.075f,z*.38f),new Vector3(.15f,.15f,.15f),Material("Black",new Color(.025f,.035f,.04f)));
            Shape("Panneau sol humide",PrimitiveType.Cube,cart,new Vector3(.62f,.38f,0),new Vector3(.43f,.75f,.05f),yellow);
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Bake saved hall navigation")]
        public static void BakeNavigation()
        {
            RequireEditAndClean(); var scene=SceneManager.GetActiveScene();
            if(scene.path!=ScenePath)scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
            var world=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<StationSocialWorld>(true)).SingleOrDefault();
            if(world && world.shopDesks != null && world.shopDesks.Length > 0) { StationNavigationSafety.Apply(); return; }
            var surface=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<NavMeshSurface>(true)).SingleOrDefault();
            if(!surface)throw new InvalidOperationException("Populate social objects before rebaking navigation.");
            BakeSurface(surface);EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);Validate();
        }
        static void BakeSurface(NavMeshSurface surface)
        {
            Physics.SyncTransforms();surface.BuildNavMesh();
            if(!surface.navMeshData)throw new InvalidOperationException("Unity did not produce a NavMeshData asset.");
            string path=AssetDatabase.GenerateUniqueAssetPath(NavigationPath);AssetDatabase.CreateAsset(surface.navMeshData,path);AssetDatabase.SaveAssets();
            surface.RemoveData();surface.AddData();
            if(NavMesh.CalculateTriangulation().indices.Length==0)throw new InvalidOperationException("Baked navigation has no walkable triangles.");
        }
        [Serializable] sealed class AuthoringReport
        {
            public string scene,unityVersion,navMeshAsset;public bool success;public int passengers,standingPassengers,conversationGroups,totalPassengers,cleaners,receptionists,seats,emptySeats,navTriangles,completePaths,authoredSeatedPoses;
            public List<string> errors=new List<string>();
        }
        [MenuItem("Efrei Research Lab/Train Station/Social/Validate saved actors and routes")]
        public static void Validate()
        {
            if (SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<StationSocialWorld>(true)).Any(w => w.shopDesks != null && w.shopDesks.Length > 0))
            { StationMixedCrowdAuthoring.Validate(); return; }
            var report=new AuthoringReport{scene=SceneManager.GetActiveScene().path,unityVersion=Application.unityVersion};
            try
            {
                var scene=SceneManager.GetActiveScene();if(scene.path!=ScenePath)throw new InvalidOperationException("Open TrainStation before validating social authoring.");
                var root=scene.GetRootGameObjects().Single(g=>g.name==RootName);var world=root.GetComponent<StationSocialWorld>();
                report.passengers=world.passengers.Length;report.cleaners=world.cleaners.Length;report.seats=world.seats.Length;
                report.conversationGroups=world.conversationGroups==null?0:world.conversationGroups.Length;
                report.standingPassengers=world.conversationGroups==null?0:world.conversationGroups.Where(g=>g).Sum(g=>g.members.Length);
                report.totalPassengers=report.passengers+report.standingPassengers;
                report.receptionists=root.GetComponentsInChildren<StationReceptionist>(true).Length;report.emptySeats=report.seats-report.passengers;
                var surface=root.GetComponentInChildren<NavMeshSurface>();report.navMeshAsset=surface?AssetDatabase.GetAssetPath(surface.navMeshData):"";
                if(report.passengers<10||report.cleaners!=3||report.receptionists!=1||report.seats<18||report.emptySeats<6)report.errors.Add("Saved role/seat counts are incomplete.");
                if(string.IsNullOrEmpty(report.navMeshAsset))report.errors.Add("NavMeshData is not a saved asset.");
                report.navTriangles=NavMesh.CalculateTriangulation().indices.Length/3;
                var front=world.informationDesk.queueSlots[0].position;
                foreach(var seat in world.seats)
                {
                    if(!seat.seatedPose||!seat.approachPoint){report.errors.Add("Seat markers missing: "+seat.name);continue;}
                    if(!PathComplete(seat.approachPoint.position,front))report.errors.Add("Seat approach cannot reach accueil: "+seat.seatId);else report.completePaths++;
                    if(Mathf.Abs(seat.approachPoint.position.y)>.2f)report.errors.Add("Seat approach is off the ground: "+seat.seatId);
                }
                foreach(var cleaner in world.cleaners)foreach(var point in cleaner.workPoints)
                    if(!PathComplete(point.position,front))report.errors.Add("Cleaning work point cannot reach hall: "+point.name);else report.completePaths++;
                foreach(var point in world.informationDesk.queueSlots)
                    if(!PathComplete(point.position,front))report.errors.Add("Queue point is unreachable: "+point.name);else report.completePaths++;
                foreach(var passenger in world.passengers)
                {
                    var animator=passenger.motion.animationPlayer.TargetAnimator;
                    if(!animator.avatar||!animator.avatar.isValid||!animator.avatar.isHuman)report.errors.Add("Invalid Humanoid Avatar: "+passenger.name);
                    if(!passenger.initialSeat||passenger.luggage||passenger.motion.agent.enabled)report.errors.Add("Passenger initial seating/no-luggage state is incomplete: "+passenger.name);
                    var hip=animator.GetBoneTransform(HumanBodyBones.Hips);var knee=animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
                    if(Mathf.Abs(hip.position.y-.65f)<.04f && Mathf.Abs(hip.position.y-knee.position.y)<.32f)report.authoredSeatedPoses++;else report.errors.Add("Initial saved pose is not aligned to the bench: "+passenger.name);
                    if(PrefabUtility.GetCorrespondingObjectFromSource(passenger.gameObject)==null)report.errors.Add("Passenger is not a reusable prefab instance: "+passenger.name);
                }
                if(root.GetComponentsInChildren<Rigidbody>(true).Length!=0)report.errors.Add("Unexpected physics rigidbodies in social setup.");
                if(AnimationMode.InAnimationMode())report.errors.Add("An editor animation preview was left active.");
            }
            catch(Exception error){report.errors.Add(error.ToString());}
            report.success=report.errors.Count==0;Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true));
            if(!report.success)throw new InvalidOperationException("Social authoring validation failed: "+string.Join("; ",report.errors));
        }
        static bool PathComplete(Vector3 from,Vector3 to)
        {
            if(!NavMesh.SamplePosition(from,out var a,.25f,NavMesh.AllAreas)||!NavMesh.SamplePosition(to,out var b,.25f,NavMesh.AllAreas))return false;
            var path=new NavMeshPath();return NavMesh.CalculatePath(a.position,b.position,NavMesh.AllAreas,path)&&path.status==NavMeshPathStatus.PathComplete;
        }
        static Vector3 Snap(Vector3 position)
        {
            if(!NavMesh.SamplePosition(position,out var hit,.5f,NavMesh.AllAreas)||Mathf.Abs(hit.position.y)>.2f)throw new InvalidOperationException("No ground NavMesh near authored point "+position);
            return hit.position;
        }
        static HumanMotion FindMotion(HumanAnimationLibrary library,string label)=>library.motions.FirstOrDefault(m=>m!=null&&string.Equals(m.Label,label,StringComparison.OrdinalIgnoreCase)&&m.clip)
            ??throw new InvalidOperationException("Required saved animation missing: "+label);
        static void RequireMotions(HumanAnimationLibrary library)
        {
            foreach(string label in new[]{"Neutral Idle","walking","Sitting Idle","Sit Down","Stand Up","Talking"})FindMotion(library,label);
            if(library.Find(StationSocialAnimationAssets.SweepId)?.clip==null)throw new InvalidOperationException("Authored station sweeping clip is missing.");
        }
        static void RequireEditAndClean()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode||AnimationMode.InAnimationMode())throw new InvalidOperationException("Stop Play/animation preview before authoring station social objects.");
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("Save or revert scene edits before social authoring. No unsaved changes are discarded.");
        }
        static void EnsureFolders()
        {
            foreach(string folder in new[]{"Prefabs","Materials","Navigation"})Directory.CreateDirectory(AssetRoot+"/"+folder);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
        static Transform Group(string name,Transform parent){var g=new GameObject(name);g.transform.SetParent(parent,false);return g.transform;}
        static GameObject Instantiate(GameObject prefab,Transform parent,string name){var g=(GameObject)PrefabUtility.InstantiatePrefab(prefab,parent.gameObject.scene);g.transform.SetParent(parent,false);g.name=name;return g;}
        static GameObject Shape(string name,PrimitiveType type,Transform parent,Vector3 position,Vector3 scale,Material material)
        {var g=GameObject.CreatePrimitive(type);g.name=name;g.transform.SetParent(parent,false);g.transform.localPosition=position;g.transform.localScale=scale;g.GetComponent<Renderer>().sharedMaterial=material;UnityEngine.Object.DestroyImmediate(g.GetComponent<Collider>());return g;}
        static Material Material(string name,Color color)
        {string path=AssetRoot+"/Materials/"+name+".mat";var material=AssetDatabase.LoadAssetAtPath<Material>(path);if(material)return material;material=new Material(Shader.Find("Universal Render Pipeline/Lit")){name=name,color=color};material.SetColor("_BaseColor",color);material.SetFloat("_Smoothness",.25f);AssetDatabase.CreateAsset(material,path);return material;}
        static void ColorModel(GameObject model,Material surface)
        {foreach(var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))if(renderer.name.IndexOf("Surface",StringComparison.OrdinalIgnoreCase)>=0)renderer.sharedMaterials=renderer.sharedMaterials.Select(_=>surface).ToArray();}
        static void TintPassenger(GameObject actor,int index)
        {Color[] palette={new Color(.43f,.19f,.36f),new Color(.16f,.36f,.48f),new Color(.54f,.38f,.14f),new Color(.23f,.39f,.30f)};ColorModel(actor,Material("Passenger_Colour_"+(index%4),palette[index%4]));}
    }
}
#endif
