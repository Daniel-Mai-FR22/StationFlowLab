#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Compacts the existing saved queue markers without replacing actors, services or navigation.</summary>
    public static class StationQueueAuthoring
    {
        public const float QueueSpacing = .82f;
        public const string ReportPath = "Reports/TrainStationSocial/queue-authoring.json";

        [Serializable] public sealed class QueueRecord
        {
            public string service;
            public bool frontPreserved, referencesPreserved;
            public Vector3[] before, after;
            public float[] oldSpacing, newSpacing;
            public Vector2 reactionDelay;
            public float advanceSpeed, minimumSeparation, arrivalTolerance, stoppingDistance;
            public int completePaths, clearanceSamples;
        }
        [Serializable] public sealed class Report
        {
            public bool success, savedSceneClean, otherTransformsPreserved, populationPreserved;
            public string completedUtc, scene, backupDirectory, updatedPrefab, error;
            public float targetSpacing = QueueSpacing;
            public int passengers, objectsBefore, objectsAfter, changedMarkers, completePaths, clearanceSamples;
            public List<QueueRecord> queues = new List<QueueRecord>();
        }
        sealed class Pose
        {
            public Transform target, parent;
            public Vector3 position, scale;
            public Quaternion rotation;
            public Pose(Transform t) { target=t; parent=t.parent; position=t.localPosition; scale=t.localScale; rotation=t.localRotation; }
            public bool Preserved => target && target.parent==parent && target.localPosition==position && target.localScale==scale && target.localRotation==rotation;
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Apply compact natural queues")]
        public static void Apply()
        {
            var scene=RequireSavedStation();
            var report=new Report { scene=scene.path, updatedPrefab=StationMixedCrowdAuthoring.ServicePrefabPath };
            bool prefabSaved=false, sceneSaved=false;
            try
            {
                var world=World(scene);
                var desks=Desks(world);
                Require(desks.Length==4,"Expected the accueil and three saved shop service desks.");
                var queueMarkers=new HashSet<Transform>(desks.SelectMany(d=>d.queueSlots));
                var poses=Components<Transform>(scene).Where(t=>!queueMarkers.Contains(t)).Select(t=>new Pose(t)).ToArray();
                var passengers=world.passengers.ToArray();
                report.objectsBefore=Components<Transform>(scene).Length;
                report.backupDirectory="BuildRequests/Backups/StationCompactQueues-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                Directory.CreateDirectory(report.backupDirectory);
                File.Copy(scene.path,Path.Combine(report.backupDirectory,"TrainStation.unity"),false);
                File.Copy(report.updatedPrefab,Path.Combine(report.backupDirectory,"Station_ShopServicePoint.prefab"),false);

                foreach(var desk in desks)
                {
                    var original=desk.queueSlots.ToArray();
                    var record=new QueueRecord { service=desk.serviceName, before=original.Select(t=>t.position).ToArray() };
                    record.oldSpacing=Distances(record.before);
                    var compact=CompactNavigablePositions(world,record.before);
                    for(int i=0;i<original.Length;i++)
                    {
                        if((original[i].position-compact[i]).sqrMagnitude>.00000001f) report.changedMarkers++;
                        original[i].position=compact[i];
                        Record(original[i]);
                    }
                    Configure(desk); Record(desk);
                    record.frontPreserved=desk.queueSlots[0].position==record.before[0];
                    record.referencesPreserved=original.SequenceEqual(desk.queueSlots);
                    Require(record.frontPreserved && record.referencesPreserved,"Queue migration changed the service front or marker references.");
                    report.queues.Add(record);
                }
                Physics.SyncTransforms();
                ValidateCore(world,report);
                report.otherTransformsPreserved=poses.All(p=>p.Preserved);
                report.populationPreserved=passengers.SequenceEqual(world.passengers);
                report.objectsAfter=Components<Transform>(scene).Length;
                Require(report.otherTransformsPreserved && report.populationPreserved && report.objectsBefore==report.objectsAfter,
                    "Compact queue authoring altered unrelated transforms, objects or passenger references.");

                var prefab=PrefabUtility.LoadPrefabContents(report.updatedPrefab);
                try
                {
                    var desk=prefab.GetComponent<StationInformationDesk>();
                    Require(desk,"The reusable shop service prefab is missing its desk component.");
                    var compact=CompactPositions(desk.queueSlots.Select(t=>t.position).ToArray());
                    for(int i=0;i<compact.Length;i++) desk.queueSlots[i].position=compact[i];
                    Configure(desk);
                    Require(Distances(compact).All(d=>Mathf.Abs(d-QueueSpacing)<.005f),"Reusable service prefab spacing is invalid.");
                    PrefabUtility.SaveAsPrefabAsset(prefab,report.updatedPrefab); prefabSaved=true;
                }
                finally { PrefabUtility.UnloadPrefabContents(prefab); }
                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkSceneDirty(scene);
                Require(EditorSceneManager.SaveScene(scene),"Could not save the compact queue markers in TrainStation.");
                sceneSaved=true; report.savedSceneClean=!scene.isDirty; report.success=true;
            }
            catch(Exception error)
            {
                report.error=error.ToString();
                if(!sceneSaved && !string.IsNullOrEmpty(report.backupDirectory))
                {
                    if(prefabSaved)
                    {
                        File.Copy(Path.Combine(report.backupDirectory,"Station_ShopServicePoint.prefab"),report.updatedPrefab,true);
                        AssetDatabase.ImportAsset(report.updatedPrefab,ImportAssetOptions.ForceUpdate);
                    }
                    EditorSceneManager.OpenScene(StationSocialAuthoring.ScenePath,OpenSceneMode.Single);
                }
                throw;
            }
            finally { Write(report); }
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Validate compact natural queues")]
        public static void Validate()
        {
            var scene=RequireSavedStation();
            var report=new Report { scene=scene.path, updatedPrefab=StationMixedCrowdAuthoring.ServicePrefabPath };
            try
            {
                report.objectsBefore=Components<Transform>(scene).Length;
                ValidateCore(World(scene),report);
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(report.updatedPrefab);
                Require(prefab,"The reusable shop service prefab is missing.");
                var desk=prefab.GetComponent<StationInformationDesk>();
                Require(desk && Distances(desk.queueSlots.Select(t=>t.position).ToArray()).All(d=>Mathf.Abs(d-QueueSpacing)<.005f),"The reusable service prefab queue spacing is not compact.");
                ValidateSettings(desk);
                report.objectsAfter=Components<Transform>(scene).Length;
                report.otherTransformsPreserved=report.objectsBefore==report.objectsAfter;
                report.populationPreserved=true; report.savedSceneClean=!scene.isDirty; report.success=true;
            }
            catch(Exception error) { report.error=error.ToString(); throw; }
            finally { Write(report); }
        }

        /// <summary>Retains the front and follows the original authored polyline, including the shop corner.</summary>
        public static Vector3[] CompactPositions(Vector3[] route) => CompactPositions(route,route.Length);

        static Vector3[] CompactPositions(Vector3[] route,int markerCount)
        {
            Require(route!=null && route.Length>0,"A queue needs at least one authored marker.");
            if(markerCount==route.Length && (route.Length==1 || Distances(route).All(d=>Mathf.Abs(d-QueueSpacing)<.0005f))) return route.ToArray();
            var result=new Vector3[markerCount]; result[0]=route[0];
            int segment=1; var cursor=route[0];
            for(int i=1;i<result.Length;i++)
            {
                var center=result[i-1];
                while(segment<route.Length && FlatDistance(center,route[segment])<QueueSpacing-.00001f)
                { cursor=route[segment]; segment++; }
                Require(segment<route.Length,"The queue polyline is too short for the requested marker spacing.");
                var direction=route[segment]-cursor;
                var relative=cursor-center;
                float a=direction.x*direction.x+direction.z*direction.z;
                float b=2*(relative.x*direction.x+relative.z*direction.z);
                float c=relative.x*relative.x+relative.z*relative.z-QueueSpacing*QueueSpacing;
                Require(a>.000001f && b*b-4*a*c>=0,"Cannot resample a degenerate queue segment.");
                float t=(-b+Mathf.Sqrt(Mathf.Max(0,b*b-4*a*c)))/(2*a);
                Require(t>=-.0001f && t<=1.0001f,"Compacted queue point falls outside its existing route.");
                cursor=Vector3.Lerp(cursor,route[segment],Mathf.Clamp01(t)); result[i]=cursor;
            }
            return result;
        }

        /// <summary>Follows the authored route but retains floor clearance around a divider or concave NavMesh corner.</summary>
        public static Vector3[] CompactNavigablePositions(StationSocialWorld world,Vector3[] route)
        {
            Require(world && route!=null && route.Length>0,"A saved queue needs a social world and markers.");
            Physics.SyncTransforms();
            var floorRoute=new List<Vector3> { route[0] };
            for(int i=1;i<route.Length;i++)
            {
                var path=new NavMeshPath();
                Require(NavMesh.CalculatePath(route[i-1],route[i],NavMesh.AllAreas,path) && path.status==NavMeshPathStatus.PathComplete,
                    "Existing authored queue has no complete floor path between markers "+(i-1)+" and "+i+".");
                foreach(var corner in path.corners.Skip(1))
                    if(FlatDistance(floorRoute[floorRoute.Count-1],corner)>.0001f) floorRoute.Add(corner);
            }
            var desired=CompactPositions(floorRoute.ToArray(),route.Length);
            var result=new Vector3[route.Length]; result[0]=route[0];
            for(int i=1;i<result.Length;i++)
            {
                var direction=desired[i]-result[i-1]; direction.y=0;
                Require(direction.sqrMagnitude>.000001f,"A compact queue step has no direction.");
                direction.Normalize();
                bool found=false; float best=float.PositiveInfinity; Vector3 selected=default;
                // The nearest valid heading wins. Every candidate retains the exact 0.82 m center distance.
                // This handles the Relay divider, where the old sparse marker chord crossed an unwalkable corner.
                for(int step=0;step<=72;step++)
                {
                    float angle=step==0 ? 0 : ((step+1)/2)*5f*(step%2==1 ? 1 : -1);
                    var candidate=result[i-1]+Quaternion.Euler(0,angle,0)*direction*QueueSpacing;
                    if(!NavMesh.SamplePosition(candidate,out var hit,.035f,NavMesh.AllAreas) ||
                        FlatDistance(candidate,hit.position)>.002f || Mathf.Abs(candidate.y-hit.position.y)>.04f) continue;
                    candidate.y=hit.position.y;
                    if(NavMesh.Raycast(result[i-1],candidate,out _,NavMesh.AllAreas)) continue;
                    bool clear=true;
                    for(int sample=1;sample<=5;sample++)
                        if(!BodyClear(world,Vector3.Lerp(result[i-1],candidate,sample/5f))) { clear=false; break; }
                    if(!clear) continue;
                    float score=(candidate-desired[i]).sqrMagnitude;
                    if(score>=best) continue;
                    best=score; selected=candidate; found=true;
                }
                Require(found,"Cannot author a compact 0.82 m queue step with floor and body clearance at marker "+i+" near "+desired[i]+".");
                result[i]=selected;
            }
            return result;
        }

        public static void Configure(StationInformationDesk desk)
        {
            desk.queueReactionDelay=new Vector2(.28f,.65f);
            desk.observedAdvanceDistance=.18f;
            desk.minimumQueueSeparation=.70f;
            desk.queueAdvanceSpeed=.82f;
            desk.queueStoppingDistance=.06f;
            desk.arrivalTolerance=.22f;
            desk.servicePositionTolerance=.35f;
        }

        static void ValidateCore(StationSocialWorld world,Report report)
        {
            Require(world,"The saved station has no social world.");
            var desks=Desks(world); Require(desks.Length==4,"Expected all four saved service queues.");
            report.passengers=world.passengers.Length;
            var front=world.informationDesk.queueSlots[0].position;
            foreach(var desk in desks)
            {
                Require(desk.queueSlots.Length>1 && desk.queueSlots.All(t=>t),"A queue marker is missing: "+desk.serviceName);
                var record=report.queues.FirstOrDefault(r=>r.service==desk.serviceName);
                if(record==null) { record=new QueueRecord {service=desk.serviceName,frontPreserved=true,referencesPreserved=true}; report.queues.Add(record); }
                record.after=desk.queueSlots.Select(t=>t.position).ToArray(); record.newSpacing=Distances(record.after);
                record.reactionDelay=desk.queueReactionDelay; record.advanceSpeed=desk.queueAdvanceSpeed;
                record.minimumSeparation=desk.minimumQueueSeparation; record.arrivalTolerance=desk.arrivalTolerance;
                record.stoppingDistance=desk.queueStoppingDistance;
                ValidateSettings(desk);
                Require(record.newSpacing.All(d=>Mathf.Abs(d-QueueSpacing)<.005f),"Queue markers are not 0.82 m apart: "+desk.serviceName);
                for(int i=0;i<record.after.Length;i++)
                {
                    var point=record.after[i];
                    Require(NavMesh.SamplePosition(point,out var hit,.06f,NavMesh.AllAreas) && Vector3.Distance(point,hit.position)<.06f,
                        "Compacted queue marker is off the floor NavMesh: "+desk.serviceName+" / "+i);
                    CheckClear(world,point,desk.serviceName+" / "+i); record.clearanceSamples++;
                    CheckPath(point,front); record.completePaths++;
                    if(i==0) continue;
                    Require(!NavMesh.Raycast(record.after[i-1],point,out _,NavMesh.AllAreas),"Compacted queue segment crosses a NavMesh obstacle: "+desk.serviceName);
                    for(int sample=1;sample<5;sample++)
                    { CheckClear(world,Vector3.Lerp(record.after[i-1],point,sample/5f),desk.serviceName+" segment "+i); record.clearanceSamples++; }
                }
                report.completePaths+=record.completePaths; report.clearanceSamples+=record.clearanceSamples;
            }
        }
        static void CheckClear(StationSocialWorld world,Vector3 point,string label)
        {
            var blocked=Physics.OverlapCapsule(point+Vector3.up*.43f,point+Vector3.up*1.48f,.32f,~0,QueryTriggerInteraction.Ignore)
                .Where(c=>c && !c.transform.IsChildOf(world.transform)).ToArray();
            Require(blocked.Length==0,"Queue body clearance intersects station geometry at "+label+": "+string.Join(", ",blocked.Select(c=>c.name)));
        }
        static bool BodyClear(StationSocialWorld world,Vector3 point) =>
            !Physics.OverlapCapsule(point+Vector3.up*.43f,point+Vector3.up*1.48f,.32f,~0,QueryTriggerInteraction.Ignore)
                .Any(c=>c && !c.transform.IsChildOf(world.transform));
        static void ValidateSettings(StationInformationDesk desk)
        {
            Require(desk.queueReactionDelay==new Vector2(.28f,.65f) && Mathf.Abs(desk.observedAdvanceDistance-.18f)<.0001f &&
                Mathf.Abs(desk.minimumQueueSeparation-.70f)<.0001f && Mathf.Abs(desk.queueAdvanceSpeed-.82f)<.0001f &&
                Mathf.Abs(desk.queueStoppingDistance-.06f)<.0001f && Mathf.Abs(desk.arrivalTolerance-.22f)<.0001f &&
                Mathf.Abs(desk.servicePositionTolerance-.35f)<.0001f,"Natural queue settings have not been saved: "+desk.serviceName);
        }
        static void CheckPath(Vector3 start,Vector3 end)
        { var path=new NavMeshPath(); Require(NavMesh.CalculatePath(start,end,NavMesh.AllAreas,path) && path.status==NavMeshPathStatus.PathComplete,"A compact queue marker cannot reach the accueil."); }
        static float FlatDistance(Vector3 a,Vector3 b) { var delta=a-b; delta.y=0; return delta.magnitude; }
        static float[] Distances(Vector3[] points) => points.Skip(1).Select((point,i)=>FlatDistance(point,points[i])).ToArray();
        static StationInformationDesk[] Desks(StationSocialWorld world) => new[]{world.informationDesk}.Concat(world.shopDesks).Where(d=>d).ToArray();
        static StationSocialWorld World(Scene scene) => Components<StationSocialWorld>(scene).Single();
        static T[] Components<T>(Scene scene) where T:Component => scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<T>(true)).ToArray();
        static Scene RequireSavedStation()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && !AnimationMode.InAnimationMode(),"Stop Play and animation preview before authoring saved queues.");
            for(int i=0;i<SceneManager.sceneCount;i++) Require(!SceneManager.GetSceneAt(i).isDirty,"Save existing scene edits first; queue authoring does not discard unsaved work.");
            var scene=SceneManager.GetSceneByPath(StationSocialAuthoring.ScenePath);
            Require(scene.IsValid() && scene.isLoaded,"Open the saved TrainStation scene first."); return scene;
        }
        static void Record(UnityEngine.Object value)
        { EditorUtility.SetDirty(value); if(PrefabUtility.IsPartOfPrefabInstance(value)) PrefabUtility.RecordPrefabInstancePropertyModifications(value); }
        static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
        static void Write(Report report)
        { report.completedUtc=DateTime.UtcNow.ToString("O"); Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true)); }
    }
}
#endif
