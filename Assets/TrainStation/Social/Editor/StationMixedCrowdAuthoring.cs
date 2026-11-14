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
    /// <summary>Saved shop services, mixed passenger activities and safe hall/shop/platform navigation.</summary>
    public static class StationMixedCrowdAuthoring
    {
        public const string RootName = "09 Mixed passenger life — shops, walking groups and solo travellers";
        public const string ClerkPrefabPath = StationSocialAuthoring.AssetRoot + "/Prefabs/Station_ShopClerk.prefab";
        public const string ServicePrefabPath = StationSocialAuthoring.AssetRoot + "/Prefabs/Station_ShopServicePoint.prefab";
        public const string SoloPrefabPath = StationSocialAuthoring.AssetRoot + "/Prefabs/Station_SoloTraveller.prefab";
        public const string WalkingMemberPrefabPath = StationSocialAuthoring.AssetRoot + "/Prefabs/Station_WalkingGroupMember.prefab";
        public const string WalkingGroupPrefabPath = StationSocialAuthoring.AssetRoot + "/Prefabs/Station_WalkingGroup.prefab";
        public const string NavigationPath = StationSocialAuthoring.AssetRoot + "/Navigation/Station_Hall_Shops_Platforms.asset";
        public const string ReportPath = "Reports/TrainStationSocial/mixed-crowd-authoring.json";
        const string CounterLightName = "Counter light — saved warm shop lighting";
        static readonly Vector3[] Formation = { Vector3.zero, new Vector3(-.72f,0,-1.25f), new Vector3(.72f,0,-1.25f) };
        static readonly float[] Tracks = { -41.7f,-30.3f,-17.7f,-6.3f,6.3f,17.7f,30.3f,41.7f };
        static readonly float[] Platforms = { -36,-12,12,36 };

        [Serializable] public sealed class ServiceRecord
        {
            public string name;
            public Vector3 staff;
            public Vector3[] queue;
        }
        [Serializable] public sealed class MixedReport
        {
            public bool success, alreadyPopulated;
            public string scene, backup, navigationAsset, error;
            public int passengers, seated, standing, walkingInGroups, solo, walkingGroups, shops, staff, humans, withLuggage, withoutLuggage, freeSeats, completeRoutes, navTriangles, checkedTrackExclusions;
            public List<ServiceRecord> services = new List<ServiceRecord>();
            public List<string> prefabs = new List<string>();
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Populate shops and mixed saved passengers")]
        public static void Populate()
        {
            RequireCleanEditor();
            var report = new MixedReport { scene = StationSocialAuthoring.ScenePath };
            var scene = SceneManager.GetSceneByPath(StationSocialAuthoring.ScenePath);
            if (!scene.IsValid() || !scene.isLoaded) scene = EditorSceneManager.OpenScene(StationSocialAuthoring.ScenePath, OpenSceneMode.Single);
            var world = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<StationSocialWorld>(true)).Single();
            try
            {
                if (world.transform.Find(RootName))
                {
                    report.alreadyPopulated = true; ValidateCore(world, report); report.success = true; return;
                }
                if (world.passengers.Length != 18 || world.conversationGroups.Length != 4)
                    throw new InvalidOperationException("Expected the saved 18 seated and four standing groups before this non-destructive population upgrade.");
                report.backup = "BuildRequests/Backups/TrainStation.before-mixed-crowd." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".unity";
                Directory.CreateDirectory(Path.GetDirectoryName(report.backup)); File.Copy(StationSocialAuthoring.ScenePath, report.backup, false);
                var library = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(StationSocialAnimationAssets.LibraryPath);
                if (!library || library.Validate(true).Count != 0) throw new InvalidOperationException("Shared saved animation resources must be valid before population.");
                UpgradeConversationMemberPrefab();
                var clerkPrefab = EnsureClerkPrefab(library);
                var servicePrefab = EnsureServicePrefab(clerkPrefab);
                var soloPrefab = EnsureTravellerPrefab(SoloPrefabPath, "Station_SoloTraveller", StationPassengerActivity.SoloWalking, library);
                var memberPrefab = EnsureTravellerPrefab(WalkingMemberPrefabPath, "Station_WalkingGroupMember", StationPassengerActivity.WalkingGroup, library);
                var groupPrefab = EnsureWalkingGroupPrefab(memberPrefab);

                var mixed = Group(RootName, world.transform);
                var servicesRoot = Group("01 Shop counters — saved clerks and arrival-order queues", mixed);
                var standingAnchors = Group("02 Standing group return anchors", mixed);
                var walkingRoot = Group("03 Walking groups — reusable three-person prefabs", mixed);
                var soloRoot = Group("04 Solo travellers", mixed);
                var routesRoot = Group("05 Routes — hall, concourse and four safe platforms", mixed);
                var station = scene.GetRootGameObjects().First(g => g.GetComponentsInChildren<Transform>(true).Any(t => t.name == "02_Cafe_et_boulangerie"));
                var bays = station.GetComponentsInChildren<Transform>(true);
                var cafe = bays.Single(t => t.name == "02_Cafe_et_boulangerie");
                var relay = bays.Single(t => t.name == "03_Relay_presse_et_livres");
                var tickets = bays.Single(t => t.name == "01_Accueil_et_billetterie");
                // Make standing room behind the middle ticket counter while preserving its original chair.
                var middleChair = tickets.GetComponentsInChildren<Transform>(true).First(t => t.name == "Siege_agent" && Mathf.Abs(t.localPosition.x) < .1f);
                middleChair.localPosition = new Vector3(0,0,4.65f);
                ExtendNavigation(world, mixed);
                Physics.SyncTransforms();
                world.shopDesks = new[]
                {
                    CreateService(servicePrefab, servicesRoot, cafe, "Cafe de la gare", new Vector3(-.9f,0,4.35f),
                        new[] { P(-.9f,1.1f),P(-.9f,-.05f),P(.5f,-1.1f),P(.5f,-2.3f),P(.5f,-3.5f),P(.5f,-4.7f) }, new Color(.36f,.17f,.08f)),
                    CreateService(servicePrefab, servicesRoot, relay, "Relay - presse et livres", new Vector3(-4.9f,0,-2.15f),
                        new[] { P(-4.9f,-4.95f),P(-3.7f,-4.95f),P(-2.5f,-4.95f),P(-1.3f,-4.95f),P(-.1f,-4.95f) }, new Color(.63f,.075f,.09f)),
                    CreateService(servicePrefab, servicesRoot, tickets, "Billetterie - vente de billets", new Vector3(0,0,3.35f),
                        new[] { P(0,.75f),P(0,-.45f),P(0,-1.65f),P(0,-2.85f),P(0,-4.05f),P(0,-5.25f) }, new Color(.055f,.26f,.48f))
                };
                world.informationDesk.serviceKind = StationServiceKind.Information;
                world.informationDesk.serviceName = "Accueil - informations";
                ValidatePlannedRoutes(world);
                var passengers = new List<StationPassenger>(world.passengers);
                for (int i = 0; i < passengers.Count; i++)
                {
                    var passenger = passengers[i]; passenger.activity = StationPassengerActivity.Seated;
                    Configure(passenger, world, 1700 + i * 73, new Vector2(65,130));
                    passenger.luggage = null;
                    Record(passenger.gameObject);
                }
                int standingIndex = 0;
                foreach (var group in world.conversationGroups)
                {
                    foreach (var member in group.members)
                    {
                        var passenger = member.GetComponent<StationPassenger>();
                        if (!passenger) passenger = member.gameObject.AddComponent<StationPassenger>();
                        passenger.motion = member.motion; passenger.activity = StationPassengerActivity.StandingGroup;
                        passenger.conversationMember = member; member.passenger = passenger;
                        passenger.initialSeat = null; passenger.luggage = null;
                        var anchor = Group("Standing return " + (++standingIndex).ToString("00"), standingAnchors);
                        anchor.SetPositionAndRotation(member.transform.position, member.transform.rotation); passenger.homePoint = anchor;
                        passenger.name = "Passenger " + (18 + standingIndex).ToString("00") + " — standing conversation traveller";
                        Configure(passenger, world, 7201 + standingIndex * 59, new Vector2(45,110));
                        passengers.Add(passenger); Record(member.gameObject);
                    }
                }
                var groupRoutes = GroupRoutes();
                var groups = new List<StationWalkingGroup>();
                for (int i = 0; i < groupRoutes.Length; i++)
                {
                    var route = MakeRoute(routesRoot, "Group route " + (i + 1).ToString("00"), groupRoutes[i]);
                    var instance = Instantiate(groupPrefab, walkingRoot, "Walking group " + (i + 1).ToString("00") + " — voyageurs ensemble");
                    instance.transform.SetPositionAndRotation(route[0].position, Quaternion.LookRotation(Flat(route[1].position - route[0].position)));
                    var group = instance.GetComponent<StationWalkingGroup>(); group.waypoints = route; group.randomSeed = 9101 + i * 137;
                    group.pauseDuration = new Vector2(2.5f + i * .2f, 6 + i * .3f); group.formationOffsets = Formation.ToArray();
                    group.leaderSpeed = .96f + (i % 3) * .04f; group.followerSpeed = 1.15f; group.catchUpSpeed = 1.4f;
                    for (int j = 0; j < group.members.Length; j++)
                    {
                        var passenger = group.members[j]; passenger.activity = StationPassengerActivity.WalkingGroup; passenger.walkingGroup = group;
                        passenger.transform.position = Snap(passenger.transform.position); passenger.initialSeat = null;
                        Configure(passenger, world, 10003 + i * 193 + j * 43, new Vector2(65,140));
                        passenger.luggage = null;
                        Tint(passenger.gameObject, i + j); passenger.name = "Passenger " + (31 + i * 3 + j).ToString("00") + " — walking group " + (i + 1).ToString("00");
                        passengers.Add(passenger);
                    }
                    Record(instance); groups.Add(group);
                }
                world.walkingGroups = groups.ToArray();
                var soloRoutes = SoloRoutes();
                for (int i = 0; i < soloRoutes.Length; i++)
                {
                    var route = MakeRoute(routesRoot, "Solo route " + (i + 1).ToString("00"), soloRoutes[i]);
                    var instance = Instantiate(soloPrefab, soloRoot, "Passenger " + (49 + i).ToString("00") + " — solo traveller");
                    instance.transform.SetPositionAndRotation(route[0].position, Quaternion.LookRotation(Flat(route[1].position - route[0].position)));
                    var passenger = instance.GetComponent<StationPassenger>(); passenger.activity = StationPassengerActivity.SoloWalking;
                    passenger.wanderPoints = route; passenger.initialSeat = null;
                    Configure(passenger, world, 14011 + i * 89, new Vector2(65,140));
                    passenger.luggage = null;
                    Tint(instance, i + 2); passengers.Add(passenger); Record(instance);
                }
                world.passengers = passengers.ToArray();
                Physics.SyncTransforms(); ValidateCore(world, report);
                StationSocialPresentation.RefreshPopulation(world);
                Record(world.gameObject); EditorUtility.SetDirty(world); AssetDatabase.SaveAssets();
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save the mixed passenger scene.");
                report.success = true;
            }
            catch (Exception error)
            {
                report.error = error.ToString();
                // The command starts only with a clean scene and saves only after validation.
                // Restore that exact on-disk scene if construction fails midway; never leave a partial population.
                if (!string.IsNullOrEmpty(report.backup)) EditorSceneManager.OpenScene(StationSocialAuthoring.ScenePath,OpenSceneMode.Single);
                throw;
            }
            finally { Write(report); }
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Validate shops and mixed crowd")]
        public static void Validate()
        {
            var report = new MixedReport { scene = StationSocialAuthoring.ScenePath };
            try
            {
                var scene = SceneManager.GetSceneByPath(StationSocialAuthoring.ScenePath);
                if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Open the saved TrainStation scene first.");
                var world = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<StationSocialWorld>(true)).Single();
                ValidateCore(world, report); report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally { Write(report); }
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Refresh saved shop counter lighting")]
        public static void RefreshShopLighting()
        {
            RequireCleanEditor();
            var scene = SceneManager.GetSceneByPath(StationSocialAuthoring.ScenePath);
            if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Open the saved TrainStation scene before updating shop lighting.");
            var world = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<StationSocialWorld>(true)).Single();
            if (world.shopDesks == null || world.shopDesks.Length != 3) throw new InvalidOperationException("Populate the three shop services first.");
            var root = PrefabUtility.LoadPrefabContents(ServicePrefabPath);
            try { EnsureCounterLight(root.GetComponent<StationInformationDesk>()); PrefabUtility.SaveAsPrefabAsset(root,ServicePrefabPath); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            foreach (var desk in world.shopDesks) { EnsureCounterLight(desk); Record(desk.gameObject); }
            AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save the shop counter lights.");
        }

        static void EnsureCounterLight(StationInformationDesk desk)
        {
            var existing = desk.transform.Find(CounterLightName);
            var lightRoot = existing ? existing : Group(CounterLightName,desk.transform);
            var light = lightRoot.GetComponent<Light>();
            if (!light) light = lightRoot.gameObject.AddComponent<Light>();
            light.type = LightType.Point; light.color = new Color(1f,.86f,.70f); light.intensity = 4f; light.range = 8f;
            light.shadows = LightShadows.None; light.renderMode = LightRenderMode.ForcePixel; light.bounceIntensity = 1;
            var staff = desk.receptionist.transform.position;
            var customer = desk.queueSlots[0].position;
            lightRoot.position = new Vector3((staff.x + customer.x) * .5f,2.8f,(staff.z + customer.z) * .5f);
        }

        static void Configure(StationPassenger passenger, StationSocialWorld world, int seed, Vector2 interval)
        {
            passenger.world = world; passenger.informationDesk = world.informationDesk; passenger.randomSeed = seed;
            passenger.automaticRequests = true; passenger.requestInterval = interval;
            passenger.navigationTimeout = 300; passenger.approachTimeout = 360; passenger.queueWaitTimeout = 180;
            passenger.motion.agent.avoidancePriority = 35 + seed % 20;
        }

        static void ExtendNavigation(StationSocialWorld world, Transform mixed)
        {
            var scene = world.gameObject.scene;
            foreach (var rail in scene.GetRootGameObjects().Where(g => g.name.StartsWith("12 Rail operations", StringComparison.Ordinal)))
                (rail.GetComponent<NavMeshModifier>() ?? rail.AddComponent<NavMeshModifier>()).ignoreFromBuild = true;
            var all = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true)).ToArray();
            foreach (var item in all.Where(t => t.name.StartsWith("01 - Rail corridor", StringComparison.Ordinal) || t.name.StartsWith("02 - Eight terminus tracks", StringComparison.Ordinal) ||
                t.name.StartsWith("Banquette_", StringComparison.Ordinal) || t.name.StartsWith("Bench / Banc", StringComparison.Ordinal) || t.name == "Table_bistrot_et_chaises" || t.name.StartsWith("Siege_agent", StringComparison.Ordinal)))
            {
                var modifier = item.GetComponent<NavMeshModifier>() ?? item.gameObject.AddComponent<NavMeshModifier>();
                modifier.overrideArea = true; modifier.area = 1;
            }
            var safety = Group("06 Navigation safety — no track or platform-edge walking", mixed);
            foreach (float x in Tracks) AreaVolume(safety, "Track exclusion X " + x, new Vector3(x,0,129), new Vector3(4.9f,5,170));
            foreach (float x in Platforms) foreach (int side in new[] { -1,1 })
                AreaVolume(safety, "Platform edge exclusion X " + (x + side * 3.5f), new Vector3(x + side * 3.5f,0,129), new Vector3(1.05f,5,170));
            var surface = world.GetComponentInChildren<NavMeshSurface>();
            if (!surface) throw new InvalidOperationException("Saved station navigation surface is missing.");
            surface.name = "01 Navigation — baked hall, shops, concourse and safe platforms";
            surface.center = new Vector3(0,1.1f,93.5f); surface.size = new Vector3(95,3.2f,239);
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders; surface.overrideVoxelSize = true; surface.voxelSize = .08f;
            BakeSurface(surface);
        }
        static void AreaVolume(Transform parent, string name, Vector3 center, Vector3 size)
        { var item = Group(name,parent); item.position = center; var area = item.gameObject.AddComponent<NavMeshModifierVolume>(); area.area = 1; area.size = size; }
        static void BakeSurface(NavMeshSurface surface)
        {
            Physics.SyncTransforms(); surface.BuildNavMesh();
            if (!surface.navMeshData) throw new InvalidOperationException("Unity did not create the extended station navigation.");
            string path = AssetDatabase.GenerateUniqueAssetPath(NavigationPath);
            AssetDatabase.CreateAsset(surface.navMeshData,path); AssetDatabase.SaveAssets(); surface.RemoveData(); surface.AddData();
            if (NavMesh.CalculateTriangulation().indices.Length == 0) throw new InvalidOperationException("Extended station navigation is empty.");
        }

        static StationInformationDesk CreateService(GameObject prefab, Transform parent, Transform bay, string title, Vector3 staffLocal, Vector3[] queueLocal, Color uniform)
        {
            var queueWorld = StationQueueAuthoring.CompactNavigablePositions(parent.GetComponentInParent<StationSocialWorld>(),
                queueLocal.Select(p => Snap(bay.TransformPoint(p))).ToArray());
            var instance = Instantiate(prefab,parent,title + " — vente et file d'attente");
            instance.transform.SetPositionAndRotation(bay.position,bay.rotation);
            var desk = instance.GetComponent<StationInformationDesk>(); desk.serviceKind = StationServiceKind.Shop; desk.serviceName = title;
            desk.receptionRadius = 4.25f; desk.conversationDuration = 7; StationQueueAuthoring.Configure(desk);
            var staff = desk.receptionist; staff.transform.localPosition = staffLocal; staff.transform.localRotation = Quaternion.Euler(0,180,0);
            ColorModel(staff.gameObject, Material("Shop_" + title.Split(' ')[0], uniform));
            var slots = instance.transform.Find("Queue — physical arrival required"); desk.queueSlots = new Transform[queueLocal.Length];
            for (int i = 0; i < queueLocal.Length; i++)
            {
                var point = i < slots.childCount ? slots.GetChild(i) : Group("Queue " + i.ToString("00"), slots);
                point.gameObject.SetActive(true); point.name = i == 0 ? "00 Service front — transaction position" : "Queue position " + i.ToString("00");
                point.position = queueWorld[i]; point.rotation = bay.rotation; desk.queueSlots[i] = point;
            }
            for (int i = queueLocal.Length; i < slots.childCount; i++) slots.GetChild(i).gameObject.SetActive(false);
            var look = instance.transform.Find("Clerk look target"); look.position = desk.queueSlots[0].position + Vector3.up * 1.55f;
            staff.interactionLookTarget = look; EnsureCounterLight(desk); Record(instance); return desk;
        }

        static void UpgradeConversationMemberPrefab()
        {
            var root = PrefabUtility.LoadPrefabContents(StationCrowdAuthoring.MemberPrefabPath);
            try
            {
                var member = root.GetComponent<StationConversationMember>();
                var passenger = root.GetComponent<StationPassenger>() ?? root.AddComponent<StationPassenger>();
                passenger.motion = member.motion; passenger.activity = StationPassengerActivity.StandingGroup;
                passenger.conversationMember = member; member.passenger = passenger;
                passenger.requestInterval = new Vector2(45,110); passenger.approachTimeout = 360; passenger.navigationTimeout = 300;
                PrefabUtility.SaveAsPrefabAsset(root,StationCrowdAuthoring.MemberPrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        static GameObject EnsureClerkPrefab(HumanAnimationLibrary library)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(ClerkPrefabPath); if (existing) return existing;
            return WithPreview("Station_ShopClerk", root =>
            {
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(StationSocialAuthoring.ReceptionistPrefabPath);
                var actor = Instantiate(source,root.transform,"Station_ShopClerk");
                PrefabUtility.UnpackPrefabInstance(actor,PrefabUnpackMode.OutermostRoot,InteractionMode.AutomatedAction);
                actor.transform.SetParent(null); root = actor;
                foreach (var item in actor.GetComponentsInChildren<Transform>(true)) if (item.name.StartsWith("Badge",StringComparison.Ordinal)) item.name = "Badge — VENTE";
                ColorModel(actor,Material("Shop_Clerk_Uniform",new Color(.46f,.16f,.12f)));
                var saved = PrefabUtility.SaveAsPrefabAsset(actor,ClerkPrefabPath); UnityEngine.Object.DestroyImmediate(actor); return saved;
            });
        }
        static GameObject EnsureServicePrefab(GameObject clerkPrefab)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(ServicePrefabPath); if (existing) return existing;
            return WithPreview("Station_ShopServicePoint", root =>
            {
                var desk = root.AddComponent<StationInformationDesk>(); desk.serviceKind = StationServiceKind.Shop; desk.serviceName = "Commerce"; desk.receptionRadius = 4.25f;
                StationQueueAuthoring.Configure(desk);
                desk.receptionist = Instantiate(clerkPrefab,root.transform,"Clerk — shared greeting and conversation animations").GetComponent<StationReceptionist>();
                desk.receptionist.transform.localPosition = new Vector3(0,0,1.6f); desk.receptionist.transform.localRotation = Quaternion.Euler(0,180,0);
                var queue = Group("Queue — physical arrival required",root.transform); desk.queueSlots = new Transform[6];
                for (int i = 0; i < 6; i++) { desk.queueSlots[i] = Group("Queue " + i.ToString("00"),queue); desk.queueSlots[i].localPosition = new Vector3(0,0,-1.2f-i*StationQueueAuthoring.QueueSpacing); }
                desk.receptionist.interactionLookTarget = Group("Clerk look target",root.transform); desk.receptionist.interactionLookTarget.localPosition = new Vector3(0,1.55f,-1.2f);
                EnsureCounterLight(desk);
                return PrefabUtility.SaveAsPrefabAsset(root,ServicePrefabPath);
            });
        }
        static GameObject EnsureTravellerPrefab(string path, string name, StationPassengerActivity activity, HumanAnimationLibrary library)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path); if (existing) return existing;
            return WithPreview(name, holder =>
            {
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(StationSocialAuthoring.PassengerPrefabPath);
                var actor = Instantiate(source,holder.transform,name); PrefabUtility.UnpackPrefabInstance(actor,PrefabUnpackMode.OutermostRoot,InteractionMode.AutomatedAction);
                actor.transform.SetParent(null);
                var passenger = actor.GetComponent<StationPassenger>(); passenger.activity = activity; passenger.initialSeat = null;
                passenger.motion.agent.enabled = true; passenger.approachTimeout = 360; passenger.navigationTimeout = 300; passenger.queueWaitTimeout = 180;
                passenger.luggage = null;
                SamplePose(passenger.motion.animationPlayer.TargetAnimator,library.Find(passenger.motion.idleId).clip,.27f);
                var saved = PrefabUtility.SaveAsPrefabAsset(actor,path); UnityEngine.Object.DestroyImmediate(actor); return saved;
            });
        }
        static GameObject EnsureWalkingGroupPrefab(GameObject memberPrefab)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(WalkingGroupPrefabPath); if (existing) return existing;
            return WithPreview("Station_WalkingGroup", root =>
            {
                var group = root.AddComponent<StationWalkingGroup>(); group.members = new StationPassenger[3]; group.formationOffsets = Formation.ToArray();
                for (int i = 0; i < 3; i++)
                {
                    var actor = Instantiate(memberPrefab,root.transform,"Traveller " + (i+1).ToString("00")); actor.transform.localPosition = Formation[i];
                    var passenger = actor.GetComponent<StationPassenger>(); passenger.walkingGroup = group; group.members[i] = passenger; Tint(actor,i); Record(actor);
                }
                return PrefabUtility.SaveAsPrefabAsset(root,WalkingGroupPrefabPath);
            });
        }
        static GameObject WithPreview(string name, Func<GameObject,GameObject> build)
        {
            var scene = EditorSceneManager.NewPreviewScene(); var root = new GameObject(name); SceneManager.MoveGameObjectToScene(root,scene);
            try { return build(root); }
            finally { if (root) UnityEngine.Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(scene); }
        }

        static void ValidateCore(StationSocialWorld world, MixedReport report)
        {
            report.passengers = world.passengers.Length;
            report.seated = world.passengers.Count(p => p.activity == StationPassengerActivity.Seated);
            report.standing = world.passengers.Count(p => p.activity == StationPassengerActivity.StandingGroup);
            report.walkingInGroups = world.passengers.Count(p => p.activity == StationPassengerActivity.WalkingGroup);
            report.solo = world.passengers.Count(p => p.activity == StationPassengerActivity.SoloWalking);
            report.walkingGroups = world.walkingGroups.Length; report.shops = world.shopDesks.Length;
            report.staff = world.GetComponentsInChildren<StationReceptionist>(true).Length;
            report.humans = world.GetComponentsInChildren<StationActorMotion>(true).Length;
            report.withLuggage = world.passengers.Count(p => p.luggage && p.luggage.gameObject.activeSelf); report.withoutLuggage = report.passengers - report.withLuggage;
            report.freeSeats = world.seats.Length - world.passengers.Count(p => p.initialSeat);
            bool originalPopulation = report.passengers == 60 && report.standing == 12 && report.walkingInGroups == 18 &&
                report.solo == 12 && report.walkingGroups == 6 && world.conversationGroups.Length == 4;
            bool densePopulation = report.passengers == StationDensityAuthoring.TargetPassengers && report.standing == 72 && report.walkingInGroups == 90 &&
                report.solo == 120 && report.walkingGroups == 30 && world.conversationGroups.Length == 24;
            if ((!originalPopulation && !densePopulation) || report.seated != 18 || report.shops != 3 || report.staff != 4 || report.humans != report.passengers + 7 || report.freeSeats < 6)
                throw new InvalidOperationException("Mixed saved population counts are incomplete: " + JsonUtility.ToJson(report));
            if (report.withLuggage != 0 || report.withoutLuggage != report.passengers || world.passengers.Any(p => p.luggage) || world.passengers.Distinct().Count() != report.passengers)
                throw new InvalidOperationException("Mixed passengers must have distinct identities and no luggage.");
            var surface = world.GetComponentInChildren<NavMeshSurface>(); report.navigationAsset = surface ? AssetDatabase.GetAssetPath(surface.navMeshData) : "";
            if (string.IsNullOrEmpty(report.navigationAsset)) throw new InvalidOperationException("Extended NavMesh must be saved as a reusable asset.");
            report.navTriangles = NavMesh.CalculateTriangulation().indices.Length / 3;
            var desks = new[] { world.informationDesk }.Concat(world.shopDesks).ToArray();
            foreach (var desk in desks)
            {
                if (!desk || !desk.receptionist || desk.queueSlots.Length == 0) throw new InvalidOperationException("Service staff or queue is missing.");
                report.services.Add(new ServiceRecord { name = desk.serviceName, staff = desk.receptionist.transform.position, queue = desk.queueSlots.Select(p => p.position).ToArray() });
                CheckClear(world,desk.receptionist.transform.position,desk.receptionist.name);
                foreach (var slot in desk.queueSlots) { CheckClear(world,slot.position,desk.serviceName + " " + slot.name); CheckPath(slot.position,world.informationDesk.queueSlots[0].position); report.completeRoutes++; }
            }
            foreach (var passenger in world.passengers)
            {
                if (!passenger || !passenger.motion || passenger.world != world || passenger.informationDesk != world.informationDesk || !PrefabUtility.IsPartOfPrefabInstance(passenger))
                    throw new InvalidOperationException("Passenger prefab or scene references are incomplete.");
                var animator = passenger.motion.animationPlayer.TargetAnimator;
                if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman || animator.applyRootMotion)
                    throw new InvalidOperationException("Passenger Humanoid animation configuration is invalid: " + passenger.name);
                Vector3 start = passenger.initialSeat ? passenger.initialSeat.ApproachPosition : passenger.transform.position;
                if (!passenger.initialSeat) CheckClear(world,start,passenger.name);
                foreach (var desk in desks) { CheckPath(start,desk.queueSlots[0].position); report.completeRoutes++; }
                if (passenger.activity == StationPassengerActivity.StandingGroup && (!passenger.conversationMember || passenger.conversationMember.passenger != passenger || !passenger.homePoint))
                    throw new InvalidOperationException("Standing passenger needs a conversation bridge and fixed return anchor.");
                foreach (var point in passenger.wanderPoints ?? Array.Empty<Transform>()) { CheckPath(start,point.position); report.completeRoutes++; }
            }
            foreach (var group in world.walkingGroups)
            {
                if (!group || group.members.Length != 3 || group.waypoints.Length < 2 || !PrefabUtility.IsPartOfPrefabInstance(group)) throw new InvalidOperationException("Saved walking group is incomplete.");
                foreach (var member in group.members) if (!member || member.walkingGroup != group) throw new InvalidOperationException("Walking group member reference is invalid.");
                foreach (var point in group.waypoints) { CheckClear(world,point.position,point.name); CheckPath(point.position,world.informationDesk.queueSlots[0].position); report.completeRoutes++; }
            }
            foreach (float x in Tracks) foreach (float z in new[] { 60f,100f,150f,200f })
            {
                if (NavMesh.SamplePosition(new Vector3(x,0,z),out _,.5f,NavMesh.AllAreas)) throw new InvalidOperationException("Passenger NavMesh entered a train track at " + x + "," + z);
                report.checkedTrackExclusions++;
            }
            foreach (string path in new[] { ClerkPrefabPath,ServicePrefabPath,SoloPrefabPath,WalkingMemberPrefabPath,WalkingGroupPrefabPath,StationCrowdAuthoring.MemberPrefabPath })
            { if (!AssetDatabase.LoadAssetAtPath<GameObject>(path)) throw new InvalidOperationException("Missing reusable prefab: " + path); report.prefabs.Add(path); }
            if (AnimationMode.InAnimationMode()) throw new InvalidOperationException("An authoring animation preview was left active.");
        }
        static void CheckClear(StationSocialWorld world, Vector3 position, string label)
        {
            var hits = Physics.OverlapCapsule(position + Vector3.up * .43f,position + Vector3.up * 1.48f,.32f,~0,QueryTriggerInteraction.Ignore)
                .Where(c => c && !c.transform.IsChildOf(world.transform)).ToArray();
            if (hits.Length > 0) throw new InvalidOperationException(label + " intersects station geometry at " + position + ": " + string.Join(", ",hits.Select(c => c.name)));
        }
        static void CheckPath(Vector3 from, Vector3 to)
        { var path = new NavMeshPath(); if (!NavMesh.CalculatePath(Snap(from),Snap(to),NavMesh.AllAreas,path) || path.status != NavMeshPathStatus.PathComplete) throw new InvalidOperationException("Incomplete mixed passenger route from " + from + " to " + to); }
        static Transform[] MakeRoute(Transform parent, string name, Vector3[] positions)
        { var route = Group(name,parent); return positions.Select((p,i) => { var marker = Group("Waypoint " + i.ToString("00"),route); marker.position = Snap(p); return marker; }).ToArray(); }
        static void ValidatePlannedRoutes(StationSocialWorld world)
        {
            var errors = new List<string>();
            var routeSets = new[] { GroupRoutes(),SoloRoutes() };
            for (int kind = 0; kind < routeSets.Length; kind++)
                for (int route = 0; route < routeSets[kind].Length; route++)
                {
                    var points = routeSets[kind][route];
                    for (int index = 0; index < points.Length; index++)
                    {
                        string label = (kind == 0 ? "Group" : "Solo") + " route " + (route+1) + " point " + index;
                        try { CheckClear(world,Snap(points[index]),label); } catch (Exception error) { errors.Add(label + ": " + error.Message); }
                    }
                    if (kind != 0) continue;
                    var heading = Quaternion.LookRotation(Flat(points[1] - points[0]));
                    for (int member = 0; member < Formation.Length; member++)
                    {
                        string label = "Group " + (route+1) + " initial member " + member;
                        try { CheckClear(world,Snap(points[0] + heading * Formation[member]),label); } catch (Exception error) { errors.Add(label + ": " + error.Message); }
                    }
                }
            if (errors.Count > 0) throw new InvalidOperationException("Planned navigation/geometry checks failed: " + string.Join("; ",errors));
        }
        static Vector3[][] GroupRoutes() => new[]
        {
            new[] { P(-8,-17),P(-8,5),P(-9,26),P(-35,37),P(-34.5f,62),P(-34.5f,95),P(-34.5f,62),P(-35,37),P(-9,26) },
            new[] { P(7,-16),P(8,15),P(10,36),P(13.5f,62),P(13.5f,90),P(13.5f,62),P(10,36),P(8,15) },
            new[] { P(-18,8),P(-18,25),P(-10,37),P(-10.5f,61),P(-10.5f,95),P(-10.5f,61),P(-10,37),P(-18,25) },
            new[] { P(18,17),P(18,30),P(34,37),P(37.5f,61),P(37.5f,95),P(37.5f,61),P(34,37),P(18,30) },
            new[] { P(-34.5f,83),P(-34.5f,118),P(-34.5f,61),P(-35,36),P(-20.5f,34),P(-35,36),P(-34.5f,61) },
            new[] { P(10.8f,85),P(10.8f,115),P(10.8f,61),P(12,36),P(19,30),P(12,36),P(10.8f,61) }
        };
        static Vector3[][] SoloRoutes() => new[]
        {
            new[] { P(-19,-8),P(-18,19),P(-18,34),P(-9,37),P(-6,20),P(-7,-16) },
            new[] { P(-17,-20),P(-18,-3),P(-8,20),P(-5,34),P(7,23),P(7,-17) },
            new[] { P(18,-12),P(19,15),P(18,35),P(12,40),P(10,20),P(6,-13) },
            new[] { P(4,-18),P(5,19),P(18,28),P(34,37),P(34.5f,85),P(34,37) },
            new[] { P(-4,17),P(-9,30),P(-12,37),P(-13.5f,86),P(-12,37),P(7,24) },
            new[] { P(20,21),P(20,34),P(35,38),P(37.5f,104),P(35,38),P(8,25) },
            new[] { P(-37.5f,60),P(-37.5f,111),P(-36,38),P(-17,34),P(-18,8),P(-36,38) },
            new[] { P(-13.5f,83),P(-13.5f,119),P(-12,39),P(-7,27),P(-7,-7),P(-12,39) },
            new[] { P(14,109),P(14,151),P(12,39),P(7,25),P(7,-4),P(12,39) },
            new[] { P(34.5f,118),P(34.5f,144),P(36,39),P(18,34),P(18,6),P(36,39) },
            new[] { P(-38,146),P(-37.5f,175),P(-37.5f,111),P(-36,38),P(-18,28),P(-36,38) },
            new[] { P(37.5f,154),P(37.5f,183),P(37.5f,109),P(36,39),P(18,29),P(36,39) }
        };

        static Vector3 P(float x, float z) => new Vector3(x,0,z);
        static Vector3 Flat(Vector3 p) { p.y = 0; return p; }
        static Vector3 Snap(Vector3 point)
        { if (!NavMesh.SamplePosition(point,out var hit,.4f,NavMesh.AllAreas) || Mathf.Abs(hit.position.y) > .2f) throw new InvalidOperationException("No safe ground NavMesh near " + point); return hit.position; }
        static Transform Group(string name, Transform parent)
        { var item = new GameObject(name); item.transform.SetParent(parent,false); return item.transform; }
        static GameObject Instantiate(GameObject prefab, Transform parent, string name)
        { var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab,parent.gameObject.scene); instance.transform.SetParent(parent,false); instance.name = name; return instance; }
        static void Record(GameObject root)
        { foreach (var component in root.GetComponentsInChildren<Component>(true)) if (component && PrefabUtility.IsPartOfPrefabInstance(component)) PrefabUtility.RecordPrefabInstancePropertyModifications(component); }
        static void Tint(GameObject actor, int index)
        { var material = AssetDatabase.LoadAssetAtPath<Material>(StationSocialAuthoring.AssetRoot + "/Materials/Passenger_Colour_" + (index % 4) + ".mat"); if (material) ColorModel(actor,material); }
        static void ColorModel(GameObject actor, Material material)
        { foreach (var renderer in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (renderer.name.IndexOf("Surface",StringComparison.OrdinalIgnoreCase) >= 0) renderer.sharedMaterials = renderer.sharedMaterials.Select(_ => material).ToArray(); }
        static Material Material(string name, Color color)
        {
            string path = StationSocialAuthoring.AssetRoot + "/Materials/" + name + ".mat"; var material = AssetDatabase.LoadAssetAtPath<Material>(path); if (material) return material;
            material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name, color = color }; material.SetColor("_BaseColor",color); material.SetFloat("_Smoothness",.25f); AssetDatabase.CreateAsset(material,path); return material;
        }
        static void SamplePose(Animator animator, AnimationClip clip, float phase)
        {
            var bones = animator.GetComponentsInChildren<Transform>(true).Where(t => t != animator.transform).ToArray();
            var driver = ScriptableObject.CreateInstance<AnimationModeDriver>(); driver.hideFlags = HideFlags.HideAndDontSave;
            Vector3[] positions = null, scales = null; Quaternion[] rotations = null;
            try
            {
                AnimationMode.StartAnimationMode(driver); AnimationMode.BeginSampling();
                try { AnimationMode.SampleAnimationClip(animator.gameObject,clip,clip.length * phase); } finally { AnimationMode.EndSampling(); }
                positions = bones.Select(t => t.localPosition).ToArray(); rotations = bones.Select(t => t.localRotation).ToArray(); scales = bones.Select(t => t.localScale).ToArray();
            }
            finally { if (AnimationMode.InAnimationMode(driver)) AnimationMode.StopAnimationMode(driver); UnityEngine.Object.DestroyImmediate(driver); }
            for (int i = 0; i < bones.Length; i++) { bones[i].localPosition = positions[i]; bones[i].localRotation = rotations[i]; bones[i].localScale = scales[i]; }
            animator.transform.localPosition = Vector3.zero; animator.transform.localRotation = Quaternion.identity; animator.transform.localScale = Vector3.one;
        }
        static void RequireCleanEditor()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode()) throw new InvalidOperationException("Stop Play and animation preview before saved scene authoring.");
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save existing scene edits first. This command does not discard unsaved work.");
        }
        static void Write(MixedReport report)
        { Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true)); }
    }
}
#endif
