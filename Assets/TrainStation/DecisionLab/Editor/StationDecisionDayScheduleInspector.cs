#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StationFlow.TrainStation.RailOperations;
using UnityEditor;
using UnityEngine;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    [CustomEditor(typeof(StationRailSchedule))]
    public sealed class StationDecisionDayScheduleInspector : UnityEditor.Editor
    {
        StationRailSchedule draft;
        string dateText, startText, search = "";
        string[] arrivals, departures;
        bool[] details;
        int page;
        const int PageSize = 8;

        void OnEnable() { Reload(); Undo.undoRedoPerformed += Reload; }
        void OnDisable() { Undo.undoRedoPerformed -= Reload; if(draft) DestroyImmediate(draft); }
        void Reload()
        {
            if(draft) DestroyImmediate(draft);
            var source=target as StationRailSchedule;
            if(!source || !source.finiteOperatingDay) return;
            draft=Instantiate(source);draft.hideFlags=HideFlags.HideAndDontSave;
            dateText=source.operatingDateIso;startText=$"{source.startHour:00}:{source.startMinute:00}";
            arrivals=source.services.Select(s=>Clock(source.StartClockSeconds+s.arrivalOffsetSeconds)).ToArray();
            departures=source.services.Select(s=>Clock(source.StartClockSeconds+s.departureOffsetSeconds)).ToArray();
            details=new bool[source.services.Length];page=0;Repaint();
        }

        public override void OnInspectorGUI()
        {
            var source=(StationRailSchedule)target;
            if(!source.finiteOperatingDay) { DrawDefaultInspector();return; }
            if(Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Timetable editing is disabled in Play. Use the simulation's delay/cancellation controls, then stop Play to edit the saved day.",MessageType.Info);
                using(new EditorGUI.DisabledScope(true)) DrawDefaultInspector();
                return;
            }
            if(!draft || draft.services.Length!=source.services.Length) Reload();
            EditorGUILayout.LabelField("Dated operating day",EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Edit published station times, not simulation offsets. Changing the start time preserves every published arrival/departure. Apply validates the entire day. Runtime edits use a separate clone; restart Play after applying.",MessageType.Info);
            dateText=EditorGUILayout.DelayedTextField("Date (yyyy-MM-dd)",dateText);
            startText=EditorGUILayout.DelayedTextField("Simulation starts (HH:mm)",startText);
            draft.minimumTrackClearanceSeconds=EditorGUILayout.DelayedFloatField("Track clearance (seconds)",draft.minimumTrackClearanceSeconds);
            EditorGUILayout.LabelField("Physical trainsets","One per track: 2, 4, 5, 7");
            search=EditorGUILayout.TextField("Filter train / route / track",search);
            var matches=Enumerable.Range(0,draft.services.Length).Where(i=>Matches(draft.services[i],search)).ToArray();
            int pages=Math.Max(1,(matches.Length+PageSize-1)/PageSize);page=Mathf.Clamp(page,0,pages-1);
            using(new EditorGUILayout.HorizontalScope())
            {
                using(new EditorGUI.DisabledScope(page==0)) if(GUILayout.Button("Previous")) --page;
                GUILayout.Label($"{matches.Length} services · page {page+1}/{pages}",EditorStyles.centeredGreyMiniLabel);
                using(new EditorGUI.DisabledScope(page>=pages-1)) if(GUILayout.Button("Next")) ++page;
            }
            foreach(int index in matches.Skip(page*PageSize).Take(PageSize)) DrawService(index);

            StationRailSchedule candidate=null;
            try
            {
                var errors=BuildCandidate(draft,dateText,startText,arrivals,departures,out candidate);
                if(errors.Count==0) EditorGUILayout.HelpBox("Valid dated timetable: occupancy, clearance, boarding window and seated capacity passed.",MessageType.Info);
                else EditorGUILayout.HelpBox(string.Join("\n",errors.Take(12))+(errors.Count>12?$"\n… and {errors.Count-12} more errors.":""),MessageType.Error);
                using(new EditorGUILayout.HorizontalScope())
                {
                    using(new EditorGUI.DisabledScope(errors.Count>0))
                        if(GUILayout.Button("Apply valid timetable"))
                        {
                            Undo.RecordObject(source,"Edit dated train timetable");
                            candidate.name=source.name;candidate.hideFlags=source.hideFlags;
                            EditorUtility.CopySerialized(candidate,source);
                            EditorUtility.SetDirty(source);AssetDatabase.SaveAssetIfDirty(source);
                            Reload();GUIUtility.ExitGUI();
                        }
                    if(GUILayout.Button("Reload saved values")) {Reload();GUIUtility.ExitGUI();}
                }
                EditorGUILayout.HelpBox("Service identity stays fixed when a time changes. A date change updates generated valmont-YYYYMMDD IDs; custom IDs are retained. Changing the saved timetable regenerates passenger journeys on the next Play.",MessageType.None);
            }
            finally { if(candidate) DestroyImmediate(candidate); }
        }

        void DrawService(int index)
        {
            var service=draft.services[index];
            using(new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"TER {service.trainNumber} · {service.origin} → {service.destination}",EditorStyles.boldLabel);
                float labelWidth=EditorGUIUtility.labelWidth;
                try
                {
                    EditorGUIUtility.labelWidth=62;
                    using(new EditorGUILayout.HorizontalScope())
                    {
                        arrivals[index]=EditorGUILayout.DelayedTextField("Arrival",arrivals[index]);
                        departures[index]=EditorGUILayout.DelayedTextField("Departure",departures[index]);
                    }
                }
                finally {EditorGUIUtility.labelWidth=labelWidth;}
                if(TryClock(departures[index],true,out int departure))
                    EditorGUILayout.LabelField("Boarding closes",Clock(departure-Math.Max(service.doorDurationSeconds,service.boardingCutoffLeadSeconds)));
                details[index]=EditorGUILayout.Foldout(details[index],$"Track {service.trackNumber} · capacity {service.seatedCapacity} · board {service.expectedBoarding} / alight {service.expectedAlighting}",true);
                if(!details[index]) return;
                EditorGUILayout.LabelField("Stable service ID",EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(DatedId(service.serviceId,dateText),EditorStyles.textField,GUILayout.Height(18));
                service.trainNumber=EditorGUILayout.DelayedTextField("Train number",service.trainNumber);
                service.origin=EditorGUILayout.DelayedTextField("Origin",service.origin);
                service.destination=EditorGUILayout.DelayedTextField("Destination",service.destination);
                service.trackNumber=EditorGUILayout.DelayedIntField("Operational track",service.trackNumber);
                EditorGUILayout.LabelField("Physical composition", "4 saved coaches, numbered from the station head");
                if (service.coachSeatCapacities == null || service.coachSeatCapacities.Length != 4)
                    EditorGUILayout.HelpBox("This scene has four physical coaches. Define four positive seat counts; changing coach count requires new train geometry.", MessageType.Error);
                else
                {
                    for (int coach = 0; coach < service.coachSeatCapacities.Length; ++coach)
                        service.coachSeatCapacities[coach] = EditorGUILayout.DelayedIntField("Coach " + (coach + 1) + " seats", service.coachSeatCapacities[coach]);
                    long total = service.coachSeatCapacities.Sum(s => (long)s);
                    service.seatedCapacity = total >= 0 && total <= int.MaxValue ? (int)total : 0;
                    EditorGUILayout.LabelField("Total seated capacity", service.seatedCapacity.ToString(CultureInfo.InvariantCulture));
                }
                service.expectedBoarding=EditorGUILayout.DelayedIntField("Planned boarding",service.expectedBoarding);
                service.expectedAlighting=EditorGUILayout.DelayedIntField("Planned alighting",service.expectedAlighting);
                service.throughPassengers=EditorGUILayout.DelayedIntField("Through passengers",service.throughPassengers);
                service.boardingCutoffLeadSeconds=EditorGUILayout.DelayedFloatField("Cutoff before departure (s)",service.boardingCutoffLeadSeconds);
                service.platformAccessLeadSeconds=EditorGUILayout.DelayedFloatField("Platform access before departure (s)",service.platformAccessLeadSeconds);
                service.approachDurationSeconds=EditorGUILayout.DelayedFloatField("Approach duration (s)",service.approachDurationSeconds);
                service.departureDurationSeconds=EditorGUILayout.DelayedFloatField("Exit duration (s)",service.departureDurationSeconds);
                service.doorDurationSeconds=EditorGUILayout.DelayedFloatField("Door phase duration (s)",service.doorDurationSeconds);
            }
        }

        /// <summary>Builds an isolated candidate. Absolute clock fields are converted only after the new start is parsed.</summary>
        public static List<string> BuildCandidate(StationRailSchedule source,string date,string start,string[] arrivalClocks,string[] departureClocks,out StationRailSchedule candidate)
        {
            var errors=new List<string>();candidate=Instantiate(source);candidate.hideFlags=HideFlags.HideAndDontSave;
            bool validDate=DateTime.TryParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _);
            if(!validDate) errors.Add("Date must be a valid yyyy-MM-dd.");
            if(!TryClock(start,false,out int startSeconds) || startSeconds%60!=0) errors.Add("Start must use HH:mm with whole minutes.");
            else {candidate.startHour=startSeconds/3600;candidate.startMinute=startSeconds/60%60;}
            candidate.operatingDateIso=date;
            if(arrivalClocks==null || departureClocks==null || arrivalClocks.Length!=candidate.services.Length || departureClocks.Length!=candidate.services.Length)
            {errors.Add("The time fields do not match the saved services.");return errors;}
            for(int i=0;i<candidate.services.Length;++i)
            {
                var service=candidate.services[i];
                if(TryClock(arrivalClocks[i],true,out int arrival)) service.arrivalOffsetSeconds=(float)(arrival-candidate.StartClockSeconds);
                else errors.Add(service.trainNumber+": arrival needs HH:mm or HH:mm:ss.");
                if(TryClock(departureClocks[i],true,out int departure)) service.departureOffsetSeconds=(float)(departure-candidate.StartClockSeconds);
                else errors.Add(service.trainNumber+": departure needs HH:mm or HH:mm:ss.");
                if(validDate) service.serviceId=DatedId(service.serviceId,date);
                if(!StationDecisionDayRailAuthoring.OperationalTracks.Contains(service.trackNumber)) errors.Add(service.trainNumber+": no saved trainset on track "+service.trackNumber+".");
                if(service.CoachCount!=4) errors.Add(service.trainNumber+": this scene requires four physical coaches with individual seat capacities.");
            }
            foreach(int track in StationDecisionDayRailAuthoring.OperationalTracks)
                if(!candidate.services.Any(s=>s!=null && s.trackNumber==track))
                    errors.Add("Saved pooled trainset on track "+track+" requires at least one dated service. Keep a service on each saved operational track (2, 4, 5, 7).");
            errors.AddRange(candidate.Validate());
            return errors;
        }

        static string DatedId(string id,string date) => DateTime.TryParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed)
            ? Regex.Replace(id??"",@"^valmont-\d{8}-","valmont-"+parsed.ToString("yyyyMMdd",CultureInfo.InvariantCulture)+"-") : id;
        static bool Matches(StationRailService service,string query) => string.IsNullOrWhiteSpace(query) ||
            (service.trainNumber+" "+service.origin+" "+service.destination+" "+service.trackNumber+" "+service.serviceId).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0;
        public static string Clock(double seconds)
        {
            int whole=(int)Math.Round(seconds);return whole%60==0?$"{whole/3600:00}:{whole/60%60:00}":$"{whole/3600:00}:{whole/60%60:00}:{whole%60:00}";
        }
        static bool TryClock(string value,bool secondsAllowed,out int result)
        {
            result=0;var parts=(value??"").Trim().Split(':');
            if(parts.Length!=2 && !(secondsAllowed && parts.Length==3)) return false;
            if(!int.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out int hour) ||
                !int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out int minute)) return false;
            int second=0;if(parts.Length==3 && !int.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out second)) return false;
            if(hour<0 || hour>23 || minute<0 || minute>59 || second<0 || second>59) return false;
            result=hour*3600+minute*60+second;return true;
        }

        [MenuItem("Efrei Research Lab/Train Station/Daily/Test timetable editor rebasing")]
        public static void RunTests()
        {
            if(Application.isPlaying) throw new InvalidOperationException("Stop Play before editor tests.");
            var source=StationDecisionDayRailAuthoring.BuildSchedule("2026-10-06");
            StationRailSchedule candidate=null, unusedTrackSchedule=null;
            int checks=0;
            try
            {
                string original=JsonUtility.ToJson(source);
                var arrivals=source.services.Select(s=>Clock(source.StartClockSeconds+s.arrivalOffsetSeconds)).ToArray();
                var departures=source.services.Select(s=>Clock(source.StartClockSeconds+s.departureOffsetSeconds)).ToArray();
                var errors=BuildCandidate(source,"2026-10-07","09:15",arrivals,departures,out candidate);
                Verify(errors.Count==0,"Rebased candidate must remain valid.",ref checks);
                Verify(candidate.services.Select(s=>candidate.StartClockSeconds+s.arrivalOffsetSeconds).SequenceEqual(source.services.Select(s=>source.StartClockSeconds+s.arrivalOffsetSeconds)),"Changing start preserves every arrival.",ref checks);
                Verify(candidate.services.Select(s=>candidate.StartClockSeconds+s.departureOffsetSeconds).SequenceEqual(source.services.Select(s=>source.StartClockSeconds+s.departureOffsetSeconds)),"Changing start preserves every departure, including half-minute services.",ref checks);
                Verify(candidate.services.All(s=>s.serviceId.StartsWith("valmont-20261007-",StringComparison.Ordinal)),"Date updates generated IDs.",ref checks);
                Verify(JsonUtility.ToJson(source)==original,"Candidate construction never mutates the source.",ref checks);
                DestroyImmediate(candidate);candidate=null;
                departures[0]="04:00";
                errors=BuildCandidate(source,"2026-10-06","07:40",arrivals,departures,out candidate);
                Verify(errors.Count>0,"Departure before arrival is rejected.",ref checks);
                Verify(DatedId("custom-trip","2026-10-07")=="custom-trip","Custom trip IDs remain stable.",ref checks);
                Verify(!TryClock("25:00",true,out _) && !TryClock("07:90",true,out _) && !TryClock("07:40:30",false,out _),"Invalid clock inputs are rejected.",ref checks);
                DestroyImmediate(candidate);candidate=null;
                departures[0]=Clock(source.StartClockSeconds+source.services[0].departureOffsetSeconds);
                var composition=source.services[0].coachSeatCapacities;
                source.services[0].coachSeatCapacities=new[]{110,110};
                errors=BuildCandidate(source,"2026-10-06","07:40",arrivals,departures,out candidate);
                Verify(errors.Any(e=>e.Contains("four physical coaches")),"The saved four-car scene rejects incompatible physical composition edits.",ref checks);
                source.services[0].coachSeatCapacities=composition;DestroyImmediate(candidate);candidate=null;
                unusedTrackSchedule=Instantiate(source);
                unusedTrackSchedule.services=unusedTrackSchedule.services.Where(s=>s.trackNumber!=7).ToArray();
                Verify(unusedTrackSchedule.Validate().Count==0,"Unused-track fixture has otherwise valid timing and capacity.",ref checks);
                errors=BuildCandidate(unusedTrackSchedule,"2026-10-06","07:40",
                    unusedTrackSchedule.services.Select(s=>Clock(unusedTrackSchedule.StartClockSeconds+s.arrivalOffsetSeconds)).ToArray(),
                    unusedTrackSchedule.services.Select(s=>Clock(unusedTrackSchedule.StartClockSeconds+s.departureOffsetSeconds)).ToArray(),out candidate);
                Verify(errors.Any(e=>e.StartsWith("Saved pooled trainset on track 7 requires",StringComparison.Ordinal)),"Timetable edits cannot leave a saved pooled track unused.",ref checks);
                Directory.CreateDirectory("Reports/TrainStationDaily");
                File.WriteAllText("Reports/TrainStationDaily/timetable-inspector-tests.json",$"{{\"success\":true,\"checks\":{checks}}}");
            }
            finally {if(candidate)DestroyImmediate(candidate);if(unusedTrackSchedule)DestroyImmediate(unusedTrackSchedule);DestroyImmediate(source);}
        }
        static void Verify(bool value,string message,ref int count) {++count;if(!value)throw new InvalidOperationException(message);}
    }
}
#endif
