#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.TrainStation.RailOperations;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Actual rail dispatcher and pure calendar checks in an isolated preview scene, without live passengers or asset mutations.</summary>
    public static class StationDecisionDayRailTests
    {
        public const string ReportPath="Reports/TrainStationDaily/rail-tests.json";
        [Serializable] public sealed class Report { public bool success; public string checkedUtc,error; public int checks,arrivals,departures; public List<string> passed=new List<string>(); }
        static Report report;

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Test finite daily railway")]
        public static void RunTests()
        {
            if(Application.isPlaying) throw new InvalidOperationException("Run calendar tests in Edit mode.");
            report=new Report{checkedUtc=DateTime.UtcNow.ToString("O")};
            var scene=EditorSceneManager.NewPreviewScene();
            var schedules=new List<StationRailSchedule>();
            try
            {
                var schedule=StationDecisionDayRailAuthoring.BuildSchedule("2026-10-06",0,0); schedules.Add(schedule);
                var authored=StationDecisionDayRailAuthoring.Inspect(schedule);
                Check(schedule.services.Length==96 && schedule.services.Select(s=>s.serviceId).Distinct().Count()==96,"96 distinct dated departures");
                Check(authored.departuresPerHour.Skip(6).Take(3).All(n=>n==8) && authored.departuresPerHour.Skip(16).Take(3).All(n=>n==8),"Morning and evening peak frequency");
                Check(authored.departuresPerHour[5]==4 && authored.departuresPerHour[12]==4 && authored.departuresPerHour[23]==0,"Offpeak and overnight timetable");
                Check(schedule.services.All(s=>s.expectedBoarding<=220 && s.expectedAlighting<=220 && s.throughPassengers==0),"Terminus capacity and full alighting demand");
                Check(schedule.services.All(s=>s.CoachCount==4 && s.coachSeatCapacities.All(n=>n==55) && s.platformAccessLeadSeconds==1200),"Four physical coaches with 55 seats each and a 20 minute ticketed access lead");
                Check(schedule.services.Any(s=>s.expectedBoarding==55) && schedule.services.Any(s=>s.expectedBoarding==88) && schedule.services.Any(s=>s.expectedBoarding==187),"Quiet, offpeak and peak loads");
                Check(authored.minimumSameTrackGapSeconds>=60 && authored.maxSimultaneousOccupiedTracks<=4,"Same-track clearance and four-berth bound");
                var first=schedule.services[0];
                var before=schedule.GetSnapshot(0,first.arrivalOffsetSeconds-first.approachDurationSeconds-.1);
                Check(before.state==StationTrainState.Offstage && before.arrivalSeconds==first.arrivalOffsetSeconds,"Future finite service retains today's time and is offstage");
                Check(schedule.GetSnapshot(0,first.arrivalOffsetSeconds).state==StationTrainState.DoorsOpening,"Arrival opens doors");
                Check(schedule.GetSnapshot(0,first.arrivalOffsetSeconds+8).state==StationTrainState.Boarding,"Boarding starts after door opening");
                Check(schedule.GetSnapshot(0,first.departureOffsetSeconds-120).boardingCutoffSeconds==first.departureOffsetSeconds-120,"Independent two-minute boarding cutoff");
                Check(schedule.GetSnapshot(0,first.departureOffsetSeconds-8).state==StationTrainState.DoorsClosing,"Physical doors close eight seconds before departure");
                Check(schedule.GetSnapshot(0,first.departureOffsetSeconds).state==StationTrainState.Departing,"Departure moves train");
                Check(schedule.GetSnapshot(0,first.departureOffsetSeconds+50).state==StationTrainState.Offstage,"Complete exit releases physical train");
                Check(schedule.GetSnapshot(0,first.arrivalOffsetSeconds+86400).state==StationTrainState.Offstage,"Finite service never reappears tomorrow");

                var rail=MakeRail(scene,schedule);
                var compositionProbe=UnityEngine.Object.Instantiate(schedule);schedules.Add(compositionProbe);
                compositionProbe.services[0].coachSeatCapacities[0]=54;
                Check(compositionProbe.Validate().Any(e=>e.Contains("summed coach seats")),"A coach composition cannot invent aggregate capacity");
                compositionProbe.services[0].coachSeatCapacities=new[]{55,55,55,55};compositionProbe.services[0].platformAccessLeadSeconds=float.NaN;
                Check(compositionProbe.Validate().Any(e=>e.Contains("platform access lead")),"Platform access lead must be finite");
                compositionProbe.services[0].platformAccessLeadSeconds=120;
                Check(compositionProbe.Validate().Any(e=>e.Contains("positive access window")),"A platform access window cannot begin at the boarding cutoff");
                compositionProbe.services[0].platformAccessLeadSeconds=1200;
                var firstTrain=rail.trains[0];var originalAnchors=firstTrain.coachBoardingAnchors;
                firstTrain.coachBoardingAnchors=new[]{originalAnchors[0]};
                Check(rail.ValidateConfiguration().Any(e=>e.Contains("one stationary boarding anchor")),"A dated train cannot omit coach walking anchors");
                firstTrain.coachBoardingAnchors=originalAnchors;
                var assignedCoach=firstTrain.visualRoot.GetChild(0);assignedCoach.name="Unconfigured module";
                Check(rail.ValidateConfiguration().Any(e=>e.Contains("saved physical car modules")),"Dated composition must match the actual physical modules");
                assignedCoach.name="Car 01";
                rail.ResetSimulation();
                Check(rail.IsReady && rail.trains.Length==4,"Four physical trainsets bind all96services");
                rail.SetSimulationTime(first.arrivalOffsetSeconds+20);
                Check(rail.GetServiceSnapshot(0).train && rail.GetServiceSnapshot(0).train.Service==first,"Current service snapshot owns the active pooled train");
                int nextSame=Array.FindIndex(schedule.services,s=>s!=first && s.trackNumber==first.trackNumber);
                Check(rail.GetServiceSnapshot(nextSame).train==null && rail.GetServiceSnapshot(nextSame).state==StationTrainState.Offstage,"Future same-track service cannot borrow current boarding state");
                var board=ObjectInScene(scene,"Test platform board").AddComponent<StationTimetableDisplay>(); board.operations=rail; board.mode=StationTimetableMode.Platform;
                var tracks=Enumerable.Range(0,4).Select(i=>board.GetRowValues(i).track).ToArray();
                Check(tracks.Distinct().Count()==4 && tracks.All(s=>!string.IsNullOrEmpty(s)),"Platform board announces one next/current service per track");

                rail.SetSimulationTime(first.arrivalOffsetSeconds-first.approachDurationSeconds-30);
                var originalArrivals=schedule.services.Select(s=>s.arrivalOffsetSeconds).ToArray();
                var originalDepartures=schedule.services.Select(s=>s.departureOffsetSeconds).ToArray();
                Check(rail.TryDelayService(first.serviceId,4000,out var affected,out var delayError),"Large valid delay accepted transactionally: "+delayError);
                Check(affected.Length>1 && schedule.Validate().Count==0,"Large delay propagates to conflicting later services");
                Check(Enumerable.Range(0,schedule.services.Length).Where(i=>schedule.services[i].trackNumber!=first.trackNumber)
                    .All(i=>schedule.services[i].arrivalOffsetSeconds==originalArrivals[i] && schedule.services[i].departureOffsetSeconds==originalDepartures[i]),"Delay preserves independent tracks");
                var delayedArrivals=schedule.services.Select(s=>s.arrivalOffsetSeconds).ToArray();
                var delayedDepartures=schedule.services.Select(s=>s.departureOffsetSeconds).ToArray();
                Check(!rail.TryDelayService(first.serviceId,100000,out _,out _),"Delay that exceeds the finite day is rejected");
                Check(schedule.services.Select(s=>s.arrivalOffsetSeconds).SequenceEqual(delayedArrivals) &&
                    schedule.services.Select(s=>s.departureOffsetSeconds).SequenceEqual(delayedDepartures),"Rejected delay rolls back every propagated edit");
                for(int i=0;i<schedule.services.Length;++i) {schedule.services[i].arrivalOffsetSeconds=originalArrivals[i];schedule.services[i].departureOffsetSeconds=originalDepartures[i];}
                rail.ResetSimulation(); rail.Resume();
                var arrived=new HashSet<string>();var departed=new HashSet<string>();
                rail.trainArrived.AddListener(t=>{ Check(t.Service!=null && t.State==StationTrainState.DoorsOpening,"Arrival callback sees its physical dated service");arrived.Add(t.Service.serviceId);++report.arrivals; });
                rail.trainDeparted.AddListener(t=>{ Check(t.Service!=null && t.State==StationTrainState.Departing,"Departure callback sees its physical dated service");departed.Add(t.Service.serviceId);++report.departures; });
                rail.AdvanceSimulation(86400);
                Check(arrived.Count==96 && departed.Count==96 && report.arrivals==96 && report.departures==96,"Whole-day chronological callbacks visit every service once");
                Check(schedule.services.All(s=>rail.GetArrivalCount(s.serviceId)==1 && rail.GetDepartureCount(s.serviceId)==1),"Per-service counters remain distinct across reused trainsets");
                Check(rail.DayComplete && rail.trains.All(t=>t.State==StationTrainState.Offstage),"Day ends with all trainsets offstage");
                rail.AdvanceSimulation(86400);
                Check(report.arrivals==96 && report.departures==96 && rail.ElapsedSimulationSeconds==86400,"Advancing past midnight never repeats the finite day");
                Check(rail.GetUpcomingServiceIndices().Length==0 && rail.GetUpcomingServiceIndices(true).Length==0 && string.IsNullOrEmpty(board.GetRowValues(0).serviceId),"Completed day leaves timetable rows empty");

                var legacy=ScriptableObject.CreateInstance<StationRailSchedule>(); schedules.Add(legacy);
                legacy.startHour=0;legacy.startMinute=0;legacy.cycleDurationSeconds=300;
                legacy.services=new[]{new StationRailService{serviceId="legacy",trainNumber="1",origin="A",destination="B",trackNumber=2,
                    arrivalOffsetSeconds=30,departureOffsetSeconds=100,approachDurationSeconds=10,departureDurationSeconds=10,doorDurationSeconds=5}};
                var legacyRail=MakeRail(scene,legacy);legacyRail.ResetSimulation();legacyRail.Resume();legacyRail.AdvanceSimulation(700);
                Check(!legacy.finiteOperatingDay && legacyRail.CompletedCycles==2 && legacyRail.GetArrivalCount("legacy")==3 && legacyRail.GetDepartureCount("legacy")==3,"Original opt-out repeating clock and counters are preserved");
                Check(legacyRail.GetServiceSnapshot(0).state==StationTrainState.Departing,"Legacy train remains on its third repeating departure");
                legacyRail.trains[0].serviceId="invalid";
                Check(legacyRail.ValidateConfiguration().Count>0,"Legacy mode retains strict one-service/one-train validation");
                report.success=true;
            }
            catch(Exception error) {report.error=error.ToString();throw;}
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                foreach(var schedule in schedules) if(schedule) UnityEngine.Object.DestroyImmediate(schedule);
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));File.WriteAllText(ReportPath,JsonUtility.ToJson(report,true));
            }
        }

        static StationRailOperations MakeRail(Scene scene,StationRailSchedule schedule)
        {
            var rail=ObjectInScene(scene,"Temporary rail test").AddComponent<StationRailOperations>(); rail.schedule=schedule;rail.playOnStart=true;rail.simulationRate=1;
            var tracks=schedule.services.Select(s=>s.trackNumber).Distinct().ToArray();var trains=new List<StationTrain>();
            foreach(int track in tracks)
            {
                var train=ObjectInScene(scene,"Temporary train "+track).AddComponent<StationTrain>();
                train.trackNumber=track;train.serviceId=schedule.services.First(s=>s.trackNumber==track).serviceId;
                train.visualRoot=new GameObject("Visual").transform;train.visualRoot.SetParent(train.transform,false);
                train.approachPoint=ObjectInScene(scene,"Approach").transform;train.approachPoint.position=new Vector3(track*12,0,360);
                train.berthPoint=ObjectInScene(scene,"Berth").transform;train.berthPoint.position=new Vector3(track*12,0,60);
                train.exitPoint=ObjectInScene(scene,"Exit").transform;train.exitPoint.position=new Vector3(track*12,0,372);
                if(schedule.finiteOperatingDay)
                {
                    train.coachBoardingAnchors=new Transform[4];
                    for(int coach=0;coach<4;++coach)
                    {
                        var car=new GameObject("Car "+(coach+1).ToString("00")).transform;car.SetParent(train.visualRoot,false);
                        car.localPosition=new Vector3(0,0,coach*20);
                        var anchor=ObjectInScene(scene,"Coach door "+(coach+1)).transform;
                        anchor.position=new Vector3(track*12+3,0,62.9f+coach*20);train.coachBoardingAnchors[coach]=anchor;
                    }
                }
                trains.Add(train);
            }
            rail.trains=trains.ToArray();return rail;
        }
        static GameObject ObjectInScene(Scene scene,string name)
        {var item=new GameObject(name);SceneManager.MoveGameObjectToScene(item,scene);return item;}
        static void Check(bool condition,string name)
        {++report.checks;if(!condition)throw new InvalidOperationException(name);if(!report.passed.Contains(name))report.passed.Add(name);}
    }
}
#endif
