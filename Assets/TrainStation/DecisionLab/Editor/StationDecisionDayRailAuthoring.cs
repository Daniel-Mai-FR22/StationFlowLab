#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using StationFlow.TrainStation.RailOperations;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.AI;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Fictional dated service plan over the four already-authored terminal tracks.</summary>
    public static class StationDecisionDayRailAuthoring
    {
        public const string DailyScenePath = "Assets/TrainStation/Scenes/TrainStationDaily.unity";
        public const string ReportPath = "Reports/TrainStationDaily/rail-authoring.json";
        public static readonly int[] OperationalTracks = { 2, 4, 5, 7 };

        [Serializable] public sealed class Report
        {
            public bool success;
            public string date, scene, schedule, error;
            public int services, trainsets, maxSimultaneousOccupiedTracks;
            public int[] departuresPerHour;
            public float minimumSameTrackGapSeconds;
            public string assumptions = "Synthetic Valmont weekday: 05:00–23:00; 4 departures/hour, 8/hour at 06–09 and 16–19. Four terminal tracks, 10–12 minute turnaround, 60s approach, 50s exit, 8s doors, 60s dispatch clearance. Each 220-seat arrival empties; no through passengers. Loads: quiet .25, offpeak .40, peak .85. These are scenario assumptions, not an operational SNCF timetable or signalling model.";
        }

        public static StationRailSchedule BuildSchedule(string date, int startHour = 7, int startMinute = 40)
        {
            if (!DateTime.TryParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed))
                throw new ArgumentException("Use an ISO date such as 2026-10-06.",nameof(date));
            var schedule = ScriptableObject.CreateInstance<StationRailSchedule>();
            schedule.name = "Valmont weekday " + date;
            schedule.finiteOperatingDay = true; schedule.operatingDateIso = date;
            schedule.startHour = startHour; schedule.startMinute = startMinute;
            schedule.cycleDurationSeconds = 86400; schedule.minimumTrackClearanceSeconds = 60;
            var services = new List<StationRailService>();
            string[] origins = { "Dijon", "Orléans", "Strasbourg", "Paris-Est" };
            string[] destinations = { "Lyon-Part-Dieu", "Tours", "Nancy", "Reims" };
            int[] numbers = { 862400, 175800, 882300, 841700 };
            int[] routeCounts = new int[4];
            for (int hour=5;hour<23;++hour)
            {
                bool peak = hour >= 6 && hour < 9 || hour >= 16 && hour < 19;
                int perHour = peak ? 8 : 4;
                float load = peak ? .85f : hour < 6 || hour >= 21 ? .25f : .40f;
                for (int slot=0;slot<perHour;++slot)
                {
                    int route=slot%4, track=OperationalTracks[route], clock=hour*3600+slot*(3600/perHour);
                    float departure=(float)(clock-schedule.StartClockSeconds), dwell=600+(route%3)*60;
                    services.Add(new StationRailService {
                        serviceId="valmont-"+parsed.ToString("yyyyMMdd",CultureInfo.InvariantCulture)+"-"+clock.ToString("D5",CultureInfo.InvariantCulture)+"-v"+track,
                        trainNumber=(numbers[route]+ ++routeCounts[route]).ToString(CultureInfo.InvariantCulture),
                        origin=origins[route],destination=destinations[route],trackNumber=track,
                        arrivalOffsetSeconds=departure-dwell,departureOffsetSeconds=departure,
                        approachDurationSeconds=60,departureDurationSeconds=50,doorDurationSeconds=8,
                        seatedCapacity=220,coachSeatCapacities=new[]{55,55,55,55},expectedBoarding=Mathf.RoundToInt(220*load),expectedAlighting=Mathf.RoundToInt(220*load),
                        throughPassengers=0,boardingCutoffLeadSeconds=120 });
                }
            }
            schedule.services=services.ToArray();
            var errors=schedule.Validate();
            if(errors.Count>0) { UnityEngine.Object.DestroyImmediate(schedule); throw new InvalidOperationException(string.Join("; ",errors)); }
            return schedule;
        }

        public static StationRailSchedule CreateScheduleAsset(string path, string date, int startHour = 7, int startMinute = 40)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Create the daily timetable in Edit mode.");
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/TrainStation/DecisionLab/",StringComparison.Ordinal) || !path.EndsWith(".asset",StringComparison.Ordinal))
                throw new ArgumentException("The day schedule must be a new Lab-owned .asset path.",nameof(path));
            var existing=AssetDatabase.LoadAssetAtPath<StationRailSchedule>(path);
            if(existing)
            {
                if(!existing.finiteOperatingDay || existing.operatingDateIso!=date || existing.startHour!=startHour || existing.startMinute!=startMinute || existing.Validate().Count>0)
                    throw new InvalidOperationException("An incompatible schedule already exists at "+path+"; it was preserved.");
                return existing;
            }
            var schedule=BuildSchedule(date,startHour,startMinute);
            Directory.CreateDirectory(Path.GetDirectoryName(path)); AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.CreateAsset(schedule,path); AssetDatabase.SaveAssets();
            return schedule;
        }

        /// <summary>Configures the caller's copied Daily scene in memory; never opens/copies/saves a scene or modifies shared prefabs.</summary>
        public static void ConfigureOpenScene(StationRailSchedule schedule)
        {
            if(Application.isPlaying) throw new InvalidOperationException("Configure the daily railway in Edit mode.");
            var scene=SceneManager.GetActiveScene();
            if(scene.path!=DailyScenePath) throw new InvalidOperationException("Only the isolated TrainStationDaily scene is accepted.");
            if(!schedule || !schedule.finiteOperatingDay) throw new ArgumentException("Assign the finite daily schedule.");
            var rail=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<StationRailOperations>(true)).Single();
            if(rail.trains.Length!=4 || !rail.trains.Select(t=>t.trackNumber).OrderBy(n=>n).SequenceEqual(OperationalTracks))
                throw new InvalidOperationException("The daily scene must retain the existing four trainsets on tracks2/4/5/7.");
            rail.schedule=schedule; rail.simulationRate=1; rail.playOnStart=true;
            ConfigureCoachAnchors(rail);
            var errors=rail.ValidateConfiguration();
            if(errors.Count>0) throw new InvalidOperationException(string.Join("; ",errors));
            rail.EvaluatePreview(0);
            EditorUtility.SetDirty(rail);
            foreach(var train in rail.trains)
                foreach(var item in train.GetComponentsInChildren<Component>(true))
                {
                    if(!item) continue;
                    EditorUtility.SetDirty(item);
                    if(PrefabUtility.IsPartOfPrefabInstance(item)) PrefabUtility.RecordPrefabInstancePropertyModifications(item);
                }
            var report=Inspect(schedule); report.scene=scene.path; report.schedule=AssetDatabase.GetAssetPath(schedule); report.trainsets=rail.trains.Length;
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true));
        }

        /// <summary>Creates only Daily-owned stationary markers from actual saved platform-side door geometry.</summary>
        public static void ConfigureCoachAnchors(StationRailOperations rail)
        {
            if (Application.isPlaying || SceneManager.GetActiveScene().path != DailyScenePath || !rail)
                throw new InvalidOperationException("Coach anchors are authored only in the isolated stopped Daily scene.");
            var world = rail.gameObject.scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<StationDecisionWorld>(true)).Single();
            Transform anchorRoot = rail.transform.Find("Daily coach door anchors");
            if (!anchorRoot) { anchorRoot = new GameObject("Daily coach door anchors").transform; anchorRoot.SetParent(rail.transform, false); }
            foreach (var train in rail.trains)
            {
                var cars = train.visualRoot.Cast<Transform>().Where(t => t.name.StartsWith("Car ", StringComparison.Ordinal)).ToArray();
                if (cars.Length != 4) throw new InvalidOperationException("Daily service geometry requires four saved car modules.");
                var platform = world.pois.Single(p => p && p.activity == DecisionActivity.Platform && p.trackNumber == train.trackNumber);
                var anchors = new Transform[cars.Length];
                for (int coach = 0; coach < cars.Length; ++coach)
                {
                    var doors = train.doorPanels.Where(d => d && d.IsChildOf(cars[coach])).ToArray();
                    if (doors.Length < 2) throw new InvalidOperationException("No complete platform-side door pair on coach " + (coach + 1));
                    // Pair leaves share the door centre; selecting the near-head door gives a distinct reachable target per coach.
                    var nearestLeaves = doors.Select(d => train.transform.InverseTransformPoint(d.position)).OrderBy(p => p.z).Take(2).ToArray();
                    Vector3 localDoor = (nearestLeaves[0] + nearestLeaves[1]) * .5f;
                    Vector3 desired = new Vector3(platform.Position.x, platform.Position.y, train.berthPoint.TransformPoint(localDoor).z);
                    if (!NavMesh.SamplePosition(desired, out var hit, .2f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - desired.y) > .15f)
                        throw new InvalidOperationException("Coach " + (coach + 1) + " has no saved platform floor near its actual door: " + desired);
                    var path = new NavMeshPath();
                    if (!NavMesh.CalculatePath(platform.Position, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                        throw new InvalidOperationException("Coach " + (coach + 1) + " is not reachable from its platform entrance.");
                    string name = "Voie " + train.trackNumber + " — coach " + (coach + 1).ToString("00") + " door";
                    var marker = anchorRoot.Find(name);
                    if (!marker) { marker = new GameObject(name).transform; marker.SetParent(anchorRoot, false); }
                    marker.SetPositionAndRotation(hit.position, train.berthPoint.rotation); anchors[coach] = marker;
                    EditorUtility.SetDirty(marker);
                }
                train.coachBoardingAnchors = anchors;
                EditorUtility.SetDirty(train);
                if (PrefabUtility.IsPartOfPrefabInstance(train)) PrefabUtility.RecordPrefabInstancePropertyModifications(train);
            }
        }

        public static void ConfigureCoachQueues(StationRailOperations rail, StationDecisionWorld world)
        {
            if (Application.isPlaying || SceneManager.GetActiveScene().path != DailyScenePath || !rail || !world)
                throw new InvalidOperationException("Coach queues are authored only in the isolated stopped Daily scene.");
            Transform queueRoot = rail.transform.Find("Daily coach boarding queues");
            if (!queueRoot) { queueRoot = new GameObject("Daily coach boarding queues").transform; queueRoot.SetParent(rail.transform, false); }
            foreach (var train in rail.trains)
            for (int coach = 1; coach <= train.coachBoardingAnchors.Length; ++coach)
            {
                if (!train.TryGetCoachBoardingAnchor(coach, out var door)) throw new InvalidOperationException("Coach door anchors must be authored before their queues.");
                string name = "Voie " + train.trackNumber + " — coach " + coach.ToString("00") + " boarding FIFO";
                var root = queueRoot.Find(name);
                if (!root) { root = new GameObject(name).transform; root.SetParent(queueRoot, false); }
                var queue = root.GetComponent<StationDecisionCoachQueue>();
                if (!queue) queue = root.gameObject.AddComponent<StationDecisionCoachQueue>();
                queue.world = world; queue.trackNumber = train.trackNumber; queue.coachNumber = coach;
                queue.doorAnchor = door; queue.boardingSecondsPerPassenger = 1f; queue.boardingHeadDistance = .5f;
                queue.queueAnchors = new Transform[12];
                queue.queueAnchors[0] = door;
                var placed = new List<Vector3> { door.position };
                for (int slot = 1; slot < queue.queueAnchors.Length; ++slot)
                {
                    if (!TryCoachQueuePosition(door.position, slot, placed, out var position))
                        throw new InvalidOperationException("Coach queue has no clear native west-side floor within its own coach: " + name + " / " + slot);
                    string label = "FIFO position " + slot.ToString("00");
                    var marker = root.Find(label);
                    if (!marker) { marker = new GameObject(label).transform; marker.SetParent(root, false); }
                    marker.SetPositionAndRotation(position, Quaternion.identity); queue.queueAnchors[slot] = marker;
                    placed.Add(position);
                    EditorUtility.SetDirty(marker);
                }
                EditorUtility.SetDirty(queue);
            }
            world.coachQueues = queueRoot.GetComponentsInChildren<StationDecisionCoachQueue>(true);
            EditorUtility.SetDirty(world);
        }

        static bool TryCoachQueuePosition(Vector3 door, int slot, List<Vector3> placed, out Vector3 position)
        {
            // Existing platform benches can occupy the inner west column. Keep the queue on the west side,
            // retain a monotonic longitudinal line, and use the outer west column before shifting along this coach.
            float firstZ = Math.Max(slot * .95f, placed[placed.Count - 1].z - door.z);
            for (float offset = 0f; firstZ + offset <= 12f + .001f; offset += .25f)
            foreach (float x in new[] { -.9f, -2f })
            {
                Vector3 desired = door + new Vector3(x, 0f, firstZ + offset);
                if (!NavMesh.SamplePosition(desired, out var hit, .2f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - desired.y) > .15f ||
                    hit.position.z < placed[placed.Count - 1].z - .001f ||
                    placed.Any(p => Vector3.Distance(p, hit.position) < .949f)) continue;
                if (Physics.OverlapCapsule(hit.position + Vector3.up * .35f, hit.position + Vector3.up * 1.75f, .27f,
                    ~0, QueryTriggerInteraction.Ignore).Length != 0) continue;
                var path = new NavMeshPath();
                if (!NavMesh.CalculatePath(door, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;
                position = hit.position; return true;
            }
            position = door; return false;
        }

        public static Report Inspect(StationRailSchedule schedule)
        {
            var report=new Report {date=schedule.operatingDateIso,services=schedule.services.Length,departuresPerHour=new int[24],minimumSameTrackGapSeconds=float.PositiveInfinity};
            foreach(var service in schedule.services)
                ++report.departuresPerHour[(int)((schedule.StartClockSeconds+service.departureOffsetSeconds)/3600)];
            foreach(var group in schedule.services.GroupBy(s=>s.trackNumber))
            {
                var ordered=group.OrderBy(s=>s.arrivalOffsetSeconds).ToArray();
                for(int i=1;i<ordered.Length;++i)
                    report.minimumSameTrackGapSeconds=Mathf.Min(report.minimumSameTrackGapSeconds,
                        ordered[i].arrivalOffsetSeconds-ordered[i].approachDurationSeconds-ordered[i-1].departureOffsetSeconds-ordered[i-1].departureDurationSeconds);
            }
            foreach(double time in schedule.services.SelectMany(s=>new[]{(double)s.arrivalOffsetSeconds-s.approachDurationSeconds,(double)s.departureOffsetSeconds+s.departureDurationSeconds}))
            {
                int occupied=schedule.services.Count(s=>time>=s.arrivalOffsetSeconds-s.approachDurationSeconds && time<s.departureOffsetSeconds+s.departureDurationSeconds);
                report.maxSimultaneousOccupiedTracks=Math.Max(report.maxSimultaneousOccupiedTracks,occupied);
            }
            report.error=string.Join("; ",schedule.Validate());
            report.success=report.error.Length==0 && report.services==96 && report.minimumSameTrackGapSeconds>=schedule.minimumTrackClearanceSeconds && report.maxSimultaneousOccupiedTracks<=4;
            if(!report.success) throw new InvalidOperationException("Daily timetable authoring failed: "+report.error);
            return report;
        }
    }
}
#endif
