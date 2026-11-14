#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Adds persistent prefab instances to the existing station without rebuilding its environment or navigation.</summary>
    public static class StationDensityAuthoring
    {
        public const string RootName = "10 Dense passenger population — 300 saved travellers";
        public const string ReportPath = "Reports/TrainStationSocial/density-authoring.json";
        public const int TargetPassengers = 300;
        static readonly Vector2 FrequentRequests = new Vector2(12,35);
        static readonly float[] PlatformCenters = { -36,-12,12,36 };
        static readonly Vector3[] StandingOffsets = { new Vector3(0,0,.95f), new Vector3(-.822724f,0,-.475f), new Vector3(.822724f,0,-.475f) };
        static readonly Vector3[] WalkingOffsets = { Vector3.zero,new Vector3(-.72f,0,-1.35f),new Vector3(.72f,0,-1.35f) };
        const float BodyClearanceRadius = .44f;
        const float MinimumActorSpacing = 1.30f;

        [Serializable] public sealed class RegionRecord
        {
            public string name;
            public int addedStandingGroups, addedWalkingGroups, addedSoloTravellers;
        }
        [Serializable] public sealed class DensityReport
        {
            public bool success, alreadyPopulated, originalTransformsPreserved, originalPassengerReferencesPreserved;
            public string completedUtc, scene, backupDirectory, navigationAsset, error;
            public int originalPassengers, addedPassengers, passengers, seated, standing, walkingInGroups, solo, standingGroups, walkingGroups;
            public int animatedActors, originalObjects, savedObjects, savedCameras, savedLights, freeSeats, noLuggagePassengers;
            public int savedRoutesChecked, savedWaypoints, preservedSceneTransforms, extraApproachingCustomersPerService;
            public int rejectedCandidates, acceptedGroupPlacements, acceptedSoloPlacements;
            public float minimumAddedPassengerSpacing = float.MaxValue;
            public Vector2 requestInterval;
            public List<string> updatedPrefabs = new List<string>();
            public List<RegionRecord> regions = new List<RegionRecord>();
        }

        sealed class Snapshot
        {
            public Transform transform, parent;
            public Vector3 position, scale;
            public Quaternion rotation;
            public Snapshot(Transform value)
            { transform=value; parent=value.parent; position=value.localPosition; rotation=value.localRotation; scale=value.localScale; }
            public bool Preserved => transform && transform.parent==parent &&
                (transform.localPosition-position).sqrMagnitude<.00000001f &&
                (transform.localScale-scale).sqrMagnitude<.00000001f && Quaternion.Angle(transform.localRotation,rotation)<.002f;
        }
        sealed class Region
        {
            public string name;
            public int index;
            public readonly List<Vector3> candidates = new List<Vector3>();
        }
        sealed class Placement
        {
            public Region region;
            public Vector3 center;
            public Quaternion rotation;
            public Vector3[] positions;
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Populate 300 saved passengers")]
        public static void Populate()
        {
            RequireCleanEditor();
            var report = new DensityReport { scene=StationSocialAuthoring.ScenePath };
            var scene = SceneManager.GetSceneByPath(report.scene);
            if (!scene.IsValid() || !scene.isLoaded) scene=EditorSceneManager.OpenScene(report.scene,OpenSceneMode.Single);
            try
            {
                var world=World(scene);
                if (world.transform.Find(RootName))
                { report.alreadyPopulated=true; ValidateCore(world,report); report.success=true; return; }
                Require(world.passengers.Length==60 && world.conversationGroups.Length==4 && world.walkingGroups.Length==6,
                    "The density migration expects the saved 60-passenger station; it never replaces an existing custom population.");
                Require(world.passengers.All(p=>p && !p.luggage),"Remove passenger luggage before increasing the population.");
                var navigation=world.GetComponentInChildren<NavMeshSurface>();
                Require(navigation && navigation.navMeshData && NavMesh.CalculateTriangulation().indices.Length>0,"Bake the corrected saved station navigation before adding passengers.");
                var standingPrefab=AssetDatabase.LoadAssetAtPath<GameObject>(StationCrowdAuthoring.GroupPrefabPath);
                var walkingPrefab=AssetDatabase.LoadAssetAtPath<GameObject>(StationMixedCrowdAuthoring.WalkingGroupPrefabPath);
                var soloPrefab=AssetDatabase.LoadAssetAtPath<GameObject>(StationMixedCrowdAuthoring.SoloPrefabPath);
                Require(standingPrefab && walkingPrefab && soloPrefab,"Saved reusable passenger/group prefabs are missing.");
                foreach (var prefab in new[]{standingPrefab,walkingPrefab,soloPrefab})
                    Require(StationNoLuggageAuthoring.BagRoots(prefab).Length==0,"A passenger prefab still contains luggage.");

                var originalPassengers=world.passengers.ToArray();
                var snapshots=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Transform>(true)).Select(t=>new Snapshot(t)).ToArray();
                report.originalPassengers=originalPassengers.Length; report.originalObjects=snapshots.Length;
                report.backupDirectory="BuildRequests/Backups/StationDensity300-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                Directory.CreateDirectory(report.backupDirectory); Backup(report.scene,report.backupDirectory);
                ConfigurePrefabIntervals(report);
                Physics.SyncTransforms();

                var occupied=world.GetComponentsInChildren<StationActorMotion>(true).Select(a=>a.transform.position).ToList();
                var standingPositions=world.passengers.Where(p=>p.activity==StationPassengerActivity.StandingGroup).Select(p=>p.transform.position).ToList();
                var regions=CreateRegions();
                var standingPlans=new List<Placement>(); var walkingPlans=new List<Placement>(); var soloPlans=new List<Placement>();
                // Keep a substantial new population in the hall while distributing the rest along all four platforms.
                int[] standingCounts={8,3,3,3,3};
                int[] walkingCounts={12,3,3,3,3};
                int[] soloCounts={60,12,12,12,12};
                for(int regionIndex=0;regionIndex<regions.Length;regionIndex++)
                {
                    var region=regions[regionIndex];
                    report.regions.Add(new RegionRecord{name=region.name,addedStandingGroups=standingCounts[regionIndex],addedWalkingGroups=walkingCounts[regionIndex],addedSoloTravellers=soloCounts[regionIndex]});
                    for(int i=0;i<standingCounts[regionIndex];i++)
                    {
                        var plan=FindGroupPlacement(world,region,StandingOffsets,occupied,report,9001+regionIndex*97+i*31);
                        standingPlans.Add(plan); standingPositions.AddRange(plan.positions);
                    }
                }
                for(int regionIndex=0;regionIndex<regions.Length;regionIndex++)
                {
                    var region=regions[regionIndex];
                    for(int i=0;i<walkingCounts[regionIndex];i++)
                        walkingPlans.Add(FindGroupPlacement(world,region,WalkingOffsets,occupied,report,13001+regionIndex*101+i*37));
                }
                for(int regionIndex=0;regionIndex<regions.Length;regionIndex++)
                {
                    var region=regions[regionIndex];
                    for(int i=0;i<soloCounts[regionIndex];i++)
                        soloPlans.Add(FindSoloPlacement(world,region,occupied,report,17011+regionIndex*103+i*41));
                }

                var density=Group(RootName,world.transform);
                var standingRoot=Group("01 Additional standing conversation groups",density);
                var walkingRoot=Group("02 Additional walking groups",density);
                var soloRoot=Group("03 Additional solo travellers",density);
                var anchorsRoot=Group("04 Fixed home anchors and shared regional routes",density);
                var homeRoot=Group("Standing return anchors",anchorsRoot);
                var regionalRoutes=new Dictionary<int,Transform[]>();
                foreach(var region in regions) regionalRoutes.Add(region.index,CreateRoutePool(world,region,standingPositions,anchorsRoot));
                var people=new List<StationPassenger>(originalPassengers);
                var standingGroups=new List<StationConversationGroup>(world.conversationGroups);
                var walkingGroups=new List<StationWalkingGroup>(world.walkingGroups);

                foreach(var plan in standingPlans)
                {
                    int groupIndex=standingGroups.Count+1;
                    var instance=Instantiate(standingPrefab,standingRoot,"Conversation group "+groupIndex.ToString("00")+" — "+plan.region.name);
                    instance.transform.SetPositionAndRotation(plan.center,plan.rotation);
                    var group=instance.GetComponent<StationConversationGroup>();
                    Require(group && group.members.Length==3,"A standing group prefab must contain three members.");
                    group.randomSeed=21001+groupIndex*113; group.initialDelay=new Vector2(.3f,3); group.speakingDuration=new Vector2(4,8); group.pauseDuration=new Vector2(.5f,1.5f);
                    for(int i=0;i<group.members.Length;i++)
                    {
                        var member=group.members[i]; var passenger=member.GetComponent<StationPassenger>();
                        Require(passenger,"The saved conversation member needs its StationPassenger component.");
                        member.group=group; member.passenger=passenger; passenger.conversationMember=member;
                        passenger.activity=StationPassengerActivity.StandingGroup; passenger.walkingGroup=null; passenger.initialSeat=null;
                        passenger.transform.SetPositionAndRotation(plan.positions[i],Quaternion.LookRotation(Flat(plan.center-plan.positions[i])));
                        var home=Group("Passenger "+(people.Count+1).ToString("000")+" — standing home",homeRoot);
                        home.SetPositionAndRotation(passenger.transform.position,passenger.transform.rotation); passenger.homePoint=home;
                        ConfigurePassenger(passenger,world,people.Count+1,"standing group "+groupIndex.ToString("00")); people.Add(passenger);
                    }
                    Record(instance); standingGroups.Add(group);
                }
                foreach(var plan in walkingPlans)
                {
                    int groupIndex=walkingGroups.Count+1;
                    var instance=Instantiate(walkingPrefab,walkingRoot,"Walking group "+groupIndex.ToString("00")+" — "+plan.region.name);
                    instance.transform.SetPositionAndRotation(plan.center,plan.rotation);
                    var group=instance.GetComponent<StationWalkingGroup>();
                    Require(group && group.members.Length==3,"A walking group prefab must contain three members.");
                    group.randomSeed=31001+groupIndex*127; group.pauseDuration=new Vector2(2,5); group.formationOffsets=WalkingOffsets.ToArray();
                    group.leaderSpeed=1.0f+(groupIndex%3)*.035f; group.followerSpeed=1.15f; group.catchUpSpeed=1.35f;
                    group.spacing=1.10f; group.waypoints=ChooseRoute(regionalRoutes[plan.region.index],plan.center,groupIndex,6);
                    for(int i=0;i<group.members.Length;i++)
                    {
                        var passenger=group.members[i]; passenger.activity=StationPassengerActivity.WalkingGroup;
                        passenger.walkingGroup=group; passenger.conversationMember=null; passenger.homePoint=null; passenger.initialSeat=null;
                        passenger.transform.SetPositionAndRotation(plan.positions[i],plan.rotation);
                        ConfigurePassenger(passenger,world,people.Count+1,"walking group "+groupIndex.ToString("00")); people.Add(passenger);
                    }
                    Record(instance); walkingGroups.Add(group);
                }
                foreach(var plan in soloPlans)
                {
                    int ordinal=people.Count+1;
                    var instance=Instantiate(soloPrefab,soloRoot,"Passenger "+ordinal.ToString("000")+" — solo traveller");
                    instance.transform.SetPositionAndRotation(plan.center,plan.rotation);
                    var passenger=instance.GetComponent<StationPassenger>(); passenger.activity=StationPassengerActivity.SoloWalking;
                    passenger.initialSeat=null; passenger.homePoint=null; passenger.walkingGroup=null; passenger.conversationMember=null;
                    passenger.wanderPoints=ChooseRoute(regionalRoutes[plan.region.index],plan.center,ordinal,7);
                    passenger.wanderPause=new Vector2(1.5f,5); ConfigurePassenger(passenger,world,ordinal,"solo traveller");
                    people.Add(passenger); Record(instance);
                }
                world.passengers=people.ToArray(); world.conversationGroups=standingGroups.ToArray(); world.walkingGroups=walkingGroups.ToArray();
                world.extraApproachingCustomersPerService=4;
                foreach(var passenger in world.passengers)
                {
                    passenger.automaticRequests=true; passenger.requestInterval=FrequentRequests; passenger.luggage=null;
                    if(PrefabUtility.IsPartOfPrefabInstance(passenger)) PrefabUtility.RecordPrefabInstancePropertyModifications(passenger);
                }
                report.addedPassengers=people.Count-originalPassengers.Length;
                report.originalPassengerReferencesPreserved=originalPassengers.All(p=>world.passengers.Contains(p));
                report.originalTransformsPreserved=snapshots.All(s=>s.Preserved); report.preservedSceneTransforms=snapshots.Count(s=>s.Preserved);
                Require(report.originalPassengerReferencesPreserved && report.originalTransformsPreserved,"The migration changed an existing scene transform or passenger identity.");
                Physics.SyncTransforms(); ValidateCore(world,report);
                StationSocialPresentation.RefreshPopulation(world); EditorUtility.SetDirty(world); Record(density.gameObject);
                AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(scene);
                Require(EditorSceneManager.SaveScene(scene),"Could not save the 300-passenger scene.");
                report.success=true;
            }
            catch(Exception error)
            {
                report.error=error.ToString();
                // Only a clean source scene is accepted, and it is saved only after full validation.
                if(!string.IsNullOrEmpty(report.backupDirectory)) EditorSceneManager.OpenScene(report.scene,OpenSceneMode.Single);
                throw;
            }
            finally { Write(report); }
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Validate saved 300 passenger population")]
        public static void Validate()
        {
            var report=new DensityReport{scene=StationSocialAuthoring.ScenePath};
            try
            {
                var scene=SceneManager.GetSceneByPath(report.scene); Require(scene.IsValid()&&scene.isLoaded,"Open TrainStation before checking its saved crowd.");
                ValidateCore(World(scene),report); report.success=true;
            }
            catch(Exception error) { report.error=error.ToString(); throw; }
            finally { Write(report); }
        }

        static void ConfigurePrefabIntervals(DensityReport report)
        {
            var paths=AssetDatabase.FindAssets("t:Prefab",new[]{StationSocialAuthoring.AssetRoot+"/Prefabs"}).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p=>AssetDatabase.LoadAssetAtPath<GameObject>(p).GetComponentsInChildren<StationPassenger>(true).Length>0)
                .OrderBy(p=>AssetDatabase.GetDependencies(p,true).Length).ThenBy(p=>p,StringComparer.Ordinal).ToArray();
            foreach(string path in paths)
            {
                Backup(path,report.backupDirectory);
                var prefab=PrefabUtility.LoadPrefabContents(path);
                try
                {
                    bool changed=false;
                    foreach(var passenger in prefab.GetComponentsInChildren<StationPassenger>(true))
                    {
                        if(passenger.requestInterval==FrequentRequests && passenger.automaticRequests && !passenger.luggage)continue;
                        passenger.requestInterval=FrequentRequests; passenger.automaticRequests=true; passenger.luggage=null; changed=true;
                    }
                    if(changed){Record(prefab);Require(PrefabUtility.SaveAsPrefabAsset(prefab,path),"Could not save frequent activity defaults in "+path);report.updatedPrefabs.Add(path);}
                }
                finally { PrefabUtility.UnloadPrefabContents(prefab); }
            }
        }
        static void ConfigurePassenger(StationPassenger passenger, StationSocialWorld world, int ordinal, string role)
        {
            passenger.name="Passenger "+ordinal.ToString("000")+" — "+role;
            passenger.world=world; passenger.informationDesk=world.informationDesk; passenger.luggage=null;
            passenger.randomSeed=47017+ordinal*137; passenger.automaticRequests=true; passenger.requestInterval=FrequentRequests;
            passenger.navigationTimeout=300; passenger.approachTimeout=360; passenger.queueWaitTimeout=180;
            passenger.motion.agent.enabled=true; passenger.motion.agent.avoidancePriority=35+ordinal%20;
            Tint(passenger.gameObject,ordinal); Record(passenger.gameObject);
        }
        static Region[] CreateRegions()
        {
            var hall=new Region{name="Hall and cross concourse",index=0};
            for(float z=-19.5f;z<=24;z+=2.35f) for(float x=-18.8f;x<=18.8f;x+=2.35f) hall.candidates.Add(P(x,z));
            for(float z=29.2f;z<=39.5f;z+=2.45f) for(float x=-43.5f;x<=43.5f;x+=2.45f) hall.candidates.Add(P(x,z));
            var regions=new List<Region>{hall};
            for(int i=0;i<PlatformCenters.Length;i++)
            {
                var region=new Region{name="Platform "+(i+1),index=i+1};
                for(float z=56;z<=202;z+=2.7f) foreach(float offset in new[]{-.85f,0f,.85f}) region.candidates.Add(P(PlatformCenters[i]+offset,z));
                regions.Add(region);
            }
            return regions.ToArray();
        }
        static Placement FindGroupPlacement(StationSocialWorld world, Region region, Vector3[] offsets, List<Vector3> occupied, DensityReport report, int seed)
        {
            foreach(var candidate in Shuffled(region.candidates,seed))
            {
                var yaws=region.index==0?new[]{0f,90f,180f,270f}:new[]{0f,180f};
                foreach(float yaw in yaws)
                {
                    var rotation=Quaternion.Euler(0,yaw,0);
                    if(!TryPlacementPoint(world,candidate,occupied,out var center)){report.rejectedCandidates++;continue;}
                    var points=new List<Vector3>();bool valid=true;
                    foreach(var offset in offsets)
                    {
                        if(!TryPlacementPoint(world,center+rotation*offset,occupied,out var point) || points.Any(p=>FlatDistance(p,point)<1.20f)){valid=false;break;}
                        points.Add(point);
                    }
                    if(!valid){report.rejectedCandidates++;continue;}
                    occupied.AddRange(points);report.acceptedGroupPlacements++;
                    return new Placement{region=region,center=center,rotation=rotation,positions=points.ToArray()};
                }
            }
            throw new InvalidOperationException("No unobstructed saved group footprint remains in "+region.name+". Do not loosen collision checks; adjust the region layout.");
        }
        static Placement FindSoloPlacement(StationSocialWorld world, Region region, List<Vector3> occupied, DensityReport report, int seed)
        {
            foreach(var candidate in Shuffled(region.candidates,seed))
            {
                if(!TryPlacementPoint(world,candidate,occupied,out var point)){report.rejectedCandidates++;continue;}
                occupied.Add(point);report.acceptedSoloPlacements++;
                return new Placement{region=region,center=point,rotation=Quaternion.Euler(0,seed%360,0),positions=new[]{point}};
            }
            throw new InvalidOperationException("No clear saved solo-passenger position remains in "+region.name);
        }
        static bool TryPlacementPoint(StationSocialWorld world, Vector3 candidate, List<Vector3> occupied, out Vector3 point)
        {
            if(!TrySafeGround(world,candidate,out point))return false;
            foreach(var other in occupied) if(FlatDistance(point,other)<MinimumActorSpacing)return false;
            foreach(var seat in world.seats) if(seat&&FlatDistance(point,seat.ApproachPosition)<1.45f)return false;
            foreach(var cleaner in world.cleaners) if(cleaner) foreach(var work in cleaner.workPoints) if(work&&FlatDistance(point,work.position)<1.20f)return false;
            foreach(var desk in Services(world))
            {
                if(FlatDistance(point,desk.queueSlots[0].position)<2.3f)return false;
                foreach(var slot in desk.queueSlots)if(slot&&FlatDistance(point,slot.position)<1.4f)return false;
            }
            return true;
        }
        static bool TrySafeGround(StationSocialWorld world, Vector3 candidate, out Vector3 point)
        {
            point=candidate;
            if(!NavMesh.SamplePosition(candidate,out var hit,.22f,NavMesh.AllAreas) || Mathf.Abs(hit.position.y)>.085f || FlatDistance(hit.position,candidate)>.18f)return false;
            point=hit.position;
            var hits=Physics.OverlapCapsule(point+Vector3.up*.46f,point+Vector3.up*1.47f,BodyClearanceRadius,~0,QueryTriggerInteraction.Ignore);
            foreach(var obstacle in hits)
                if(obstacle && !obstacle.GetComponentInParent<StationActorMotion>() && !(obstacle.isTrigger)) return false;
            return true;
        }
        static Transform[] CreateRoutePool(StationSocialWorld world, Region region, List<Vector3> standingPositions, Transform parent)
        {
            var selected=new List<Vector3>();
            foreach(var candidate in Shuffled(region.candidates,61001+region.index*151))
            {
                if(!TrySafeGround(world,candidate,out var point))continue;
                if(standingPositions.Any(p=>FlatDistance(p,point)<1.9f))continue;
                if(selected.Any(p=>FlatDistance(p,point)<(region.index==0?6f:11f)))continue;
                if(Services(world).Any(d=>d.queueSlots.Any(s=>FlatDistance(s.position,point)<2f)))continue;
                if(!PathComplete(point,world.informationDesk.queueSlots[0].position))continue;
                selected.Add(point);if(selected.Count>=(region.index==0?24:12))break;
            }
            Require(selected.Count>=8,"Too few clear waypoints remain in "+region.name);
            var root=Group(region.name+" — editable walking destinations",parent);
            return selected.Select((p,i)=>{var waypoint=Group("Destination "+(i+1).ToString("00"),root);waypoint.position=p;return waypoint;}).ToArray();
        }
        static Transform[] ChooseRoute(Transform[] pool, Vector3 start, int seed, int count)
        {
            var chosen=new List<Transform>();var random=new System.Random(seed*131+17);
            var closest=pool.OrderBy(p=>FlatDistance(p.position,start)).Take(Mathf.Min(pool.Length,Mathf.Max(count,10))).ToList();
            while(chosen.Count<Mathf.Min(count,closest.Count))
            {
                var remaining=closest.Where(p=>!chosen.Contains(p)).ToArray();
                var point=remaining[random.Next(remaining.Length)];chosen.Add(point);
            }
            return chosen.ToArray();
        }

        static void ValidateCore(StationSocialWorld world, DensityReport report)
        {
            var people=world.passengers;var scene=world.gameObject.scene;
            report.passengers=people.Length;report.seated=people.Count(p=>p&&p.activity==StationPassengerActivity.Seated);
            report.standing=people.Count(p=>p&&p.activity==StationPassengerActivity.StandingGroup);
            report.walkingInGroups=people.Count(p=>p&&p.activity==StationPassengerActivity.WalkingGroup);
            report.solo=people.Count(p=>p&&p.activity==StationPassengerActivity.SoloWalking);
            report.standingGroups=world.conversationGroups.Length;report.walkingGroups=world.walkingGroups.Length;
            report.animatedActors=world.GetComponentsInChildren<StationActorMotion>(true).Length;
            report.freeSeats=world.seats.Length-people.Count(p=>p&&p.initialSeat);
            report.noLuggagePassengers=people.Count(p=>p&&!p.luggage);
            report.requestInterval=FrequentRequests;report.extraApproachingCustomersPerService=world.extraApproachingCustomersPerService;
            var surface=world.GetComponentInChildren<NavMeshSurface>();report.navigationAsset=surface?AssetDatabase.GetAssetPath(surface.navMeshData):"";
            var roots=scene.GetRootGameObjects();report.savedObjects=roots.Sum(g=>g.GetComponentsInChildren<Transform>(true).Length);
            report.savedCameras=roots.Sum(g=>g.GetComponentsInChildren<Camera>(true).Length);report.savedLights=roots.Sum(g=>g.GetComponentsInChildren<Light>(true).Length);
            Require(report.passengers==300&&report.seated==18&&report.standing==72&&report.walkingInGroups==90&&report.solo==120,"The saved density composition must be18 seated,72 standing,90 grouped walkers and120 solo walkers.");
            Require(report.standingGroups==24&&report.walkingGroups==30&&report.animatedActors==307&&report.freeSeats==6,"Saved group/staff/seat counts changed unexpectedly.");
            Require(report.noLuggagePassengers==300&&people.Distinct().Count()==300&&people.Select(p=>p.name).Distinct().Count()==300,"Passenger identities must be unique and luggage-free.");
            Require(!string.IsNullOrEmpty(report.navigationAsset)&&report.extraApproachingCustomersPerService==4,"Saved navigation or congestion budget is missing.");
            var desks=Services(world).ToArray();
            var addedRoot=world.transform.Find(RootName);Require(addedRoot,"The editor-authored density hierarchy is missing.");
            var addedPeople=addedRoot.GetComponentsInChildren<StationPassenger>(true);
            Require(addedPeople.Length==240,"The density migration must add exactly240 saved passengers.");
            foreach(var passenger in people)
            {
                Require(passenger&&passenger.motion&&passenger.motion.agent&&passenger.world==world&&passenger.informationDesk==world.informationDesk,"A passenger has missing motion, navigation or service references.");
                Require(passenger.automaticRequests&&passenger.requestInterval==FrequentRequests,"A passenger did not inherit the12–35 second random event interval.");
                Require(PrefabUtility.IsPartOfPrefabInstance(passenger),"A saved passenger lost its reusable prefab link.");
                var animator=passenger.motion.animationPlayer.TargetAnimator;
                Require(animator&&animator.avatar&&animator.avatar.isValid&&animator.avatar.isHuman&&!animator.applyRootMotion,"A passenger's Humanoid configuration is invalid.");
                var start=passenger.initialSeat?passenger.initialSeat.ApproachPosition:passenger.transform.position;
                foreach(var desk in desks){Require(PathComplete(start,desk.queueSlots[0].position),"No complete service route from "+passenger.name+" to "+desk.serviceName);report.savedRoutesChecked++;}
                if(passenger.activity==StationPassengerActivity.StandingGroup)
                    Require(passenger.homePoint&&passenger.conversationMember&&passenger.conversationMember.passenger==passenger,"Standing group return references are incomplete.");
                if(passenger.activity==StationPassengerActivity.WalkingGroup)Require(passenger.walkingGroup&&passenger.walkingGroup.members.Contains(passenger),"Walking membership is inconsistent.");
                if(passenger.activity==StationPassengerActivity.SoloWalking)Require(passenger.wanderPoints.Length>=2,"Solo walking destinations are missing.");
            }
            foreach(var passenger in addedPeople)
            {
                Require(TrySafeGround(world,passenger.transform.position,out _),"A newly authored traveller intersects station geometry: "+passenger.name);
                foreach(var other in people)
                {
                    if(other==passenger)continue;
                    float separation=FlatDistance(passenger.transform.position,other.transform.position);
                    report.minimumAddedPassengerSpacing=Mathf.Min(report.minimumAddedPassengerSpacing,separation);
                    Require(separation>=1.19f,"Newly authored passengers overlap: "+passenger.name+" / "+other.name);
                }
            }
            var points=people.SelectMany(p=>p.wanderPoints??Array.Empty<Transform>()).Concat(world.walkingGroups.SelectMany(g=>g.waypoints??Array.Empty<Transform>())).Where(p=>p).Distinct().ToArray();
            report.savedWaypoints=points.Length;
            foreach(var point in points){Require(PathComplete(point.position,world.informationDesk.queueSlots[0].position),"Walking waypoint cannot reach the hall: "+point.name);report.savedRoutesChecked++;}
            Require(roots.Sum(g=>StationNoLuggageAuthoring.BagRoots(g).Length)==0,"Luggage was reintroduced into the station.");
            Require(!roots.SelectMany(g=>g.GetComponentsInChildren<Transform>(true)).Any(t=>GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject)>0),"A saved scene object has a missing script.");
            Require(!AnimationMode.InAnimationMode(),"An animation preview was left running.");
        }
        static bool PathComplete(Vector3 from, Vector3 to)
        {
            if(!NavMesh.SamplePosition(from,out var a,.25f,NavMesh.AllAreas)||!NavMesh.SamplePosition(to,out var b,.25f,NavMesh.AllAreas))return false;
            var path=new NavMeshPath();return NavMesh.CalculatePath(a.position,b.position,NavMesh.AllAreas,path)&&path.status==NavMeshPathStatus.PathComplete;
        }
        static IEnumerable<StationInformationDesk> Services(StationSocialWorld world)=>new[]{world.informationDesk}.Concat(world.shopDesks).Where(d=>d);
        static StationSocialWorld World(Scene scene)=>scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<StationSocialWorld>(true)).Single();
        static IEnumerable<Vector3> Shuffled(List<Vector3> positions,int seed)
        {var array=positions.ToArray();var random=new System.Random(seed);for(int i=array.Length-1;i>0;i--){int j=random.Next(i+1);var value=array[i];array[i]=array[j];array[j]=value;}return array;}
        static Vector3 P(float x,float z)=>new Vector3(x,0,z);
        static Vector3 Flat(Vector3 value){value.y=0;return value;}
        static float FlatDistance(Vector3 a,Vector3 b)=>Flat(a-b).magnitude;
        static Transform Group(string name,Transform parent){var go=new GameObject(name);go.transform.SetParent(parent,false);return go.transform;}
        static GameObject Instantiate(GameObject prefab,Transform parent,string name)
        {var go=(GameObject)PrefabUtility.InstantiatePrefab(prefab,parent.gameObject.scene);go.transform.SetParent(parent,false);go.name=name;return go;}
        static void Tint(GameObject actor,int ordinal)
        {
            var material=AssetDatabase.LoadAssetAtPath<Material>(StationSocialAuthoring.AssetRoot+"/Materials/Passenger_Colour_"+(ordinal%4)+".mat");
            if(!material)return;
            foreach(var renderer in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))if(renderer.name.IndexOf("Surface",StringComparison.OrdinalIgnoreCase)>=0)renderer.sharedMaterials=renderer.sharedMaterials.Select(_=>material).ToArray();
        }
        static void Record(GameObject root){foreach(var component in root.GetComponentsInChildren<Component>(true))if(component&&PrefabUtility.IsPartOfPrefabInstance(component))PrefabUtility.RecordPrefabInstancePropertyModifications(component);}
        static void Backup(string path,string directory){if(File.Exists(path))File.Copy(path,Path.Combine(directory,Path.GetFileName(path)),false);if(File.Exists(path+".meta"))File.Copy(path+".meta",Path.Combine(directory,Path.GetFileName(path)+".meta"),false);}
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        static void RequireCleanEditor()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode&&!AnimationMode.InAnimationMode(),"Stop Play and animation preview before saving the denser population.");
            for(int i=0;i<SceneManager.sceneCount;i++)Require(!SceneManager.GetSceneAt(i).isDirty,"Save existing scene changes first; population authoring does not discard user edits.");
        }
        static void Write(DensityReport report){report.completedUtc=DateTime.UtcNow.ToString("O");Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true));}
    }
}
#endif
