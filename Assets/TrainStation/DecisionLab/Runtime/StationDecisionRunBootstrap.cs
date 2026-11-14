using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using StationFlow.TrainStation.RailOperations;
using UnityEngine;

namespace StationFlow.TrainStation.DecisionLab
{
    [Serializable]
    public sealed class StationDecisionRunSession
    {
        public string runId, operatingDateIso, localTimezoneId, createdUtc, randomizationVersion;
        public int seed, localUtcOffsetMinutes, simulationStartUtcOffsetMinutes;
        public bool fixedSeedReplay;
        public DecisionTimetableSettings generationSettings;
        [NonSerialized] public StationRailSchedule schedule;
        [NonSerialized] public StationDecisionDayScenario scenario;
    }

    /// <summary>Called by the world Awake before daily arrivals Awake and the railway Start. Does not modify saved assets.</summary>
    public static class StationDecisionRunBootstrap
    {
        public static StationDecisionRunSession Create(StationRailOperations rail, StationDecisionDayScenario scenarioTemplate,
            bool fixedSeedReplay = false, int fixedSeed = 0, string replayDateIso = null)
        {
            if (!rail || !rail.schedule || !rail.schedule.finiteOperatingDay || !scenarioTemplate)
                throw new ArgumentException("A saved dated railway and daily scenario are required for launch randomization.");
            var now = DateTimeOffset.Now;
            string date = fixedSeedReplay && !string.IsNullOrEmpty(replayDateIso) ? replayDateIso : now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                throw new ArgumentException("Replay simulation date must be ISO yyyy-MM-dd.");
            int seed = fixedSeedReplay ? fixedSeed : FreshSeed();
            var settings = new DecisionTimetableSettings { operatingDateIso = date,
                initialClockSeconds = (int)rail.schedule.StartClockSeconds,
                minimumTrackClearanceSeconds = (int)Math.Ceiling(Math.Max(60, rail.schedule.minimumTrackClearanceSeconds)) };
            var tracks = PhysicalTracks(rail);
            var routes = rail.schedule.services.Where(s => s != null).GroupBy(s => (s.origin ?? "") + "\u001f" + (s.destination ?? ""), StringComparer.Ordinal)
                .Select(g => new DecisionTimetableRoute { origin = g.First().origin, destination = g.First().destination })
                .OrderBy(r => r.origin, StringComparer.Ordinal).ThenBy(r => r.destination, StringComparer.Ordinal).ToArray();
            var generated = DecisionDayTimetableGenerator.Generate(tracks, routes, settings, seed);
            var runtimeSchedule = UnityEngine.Object.Instantiate(rail.schedule);
            StationDecisionDayScenario runtimeScenario = null;
            try
            {
                runtimeSchedule.name = rail.schedule.name + " (randomized runtime only)";
                runtimeSchedule.operatingDateIso = date;
                runtimeSchedule.finiteOperatingDay = true;
                runtimeSchedule.cycleDurationSeconds = 86400;
                runtimeSchedule.minimumTrackClearanceSeconds = settings.minimumTrackClearanceSeconds;
                runtimeSchedule.services = generated.Select(s => new StationRailService {
                    serviceId = s.serviceId, trainNumber = s.trainNumber, origin = s.origin, destination = s.destination,
                    trackNumber = s.trackNumber, arrivalOffsetSeconds = s.arrivalOffsetSeconds, departureOffsetSeconds = s.departureOffsetSeconds,
                    approachDurationSeconds = s.approachDurationSeconds, departureDurationSeconds = s.departureDurationSeconds,
                    doorDurationSeconds = s.doorDurationSeconds, seatedCapacity = s.seatedCapacity, coachSeatCapacities = (int[])s.coachSeatCapacities.Clone(),
                    expectedBoarding = s.expectedBoarding, expectedAlighting = s.expectedAlighting, throughPassengers = 0,
                    boardingCutoffLeadSeconds = s.boardingCutoffLeadSeconds, platformAccessLeadSeconds = s.platformAccessLeadSeconds
                }).ToArray();
                var errors = runtimeSchedule.Validate();
                if (errors.Count > 0) throw new InvalidOperationException("Generated timetable failed validation: " + string.Join("; ", errors));
                runtimeScenario = UnityEngine.Object.Instantiate(scenarioTemplate);
                runtimeScenario.name = scenarioTemplate.name + " (randomized runtime only)";
                runtimeScenario.scenarioDate = date; runtimeScenario.seed = seed;
                // Instantiate can share plain serializable references; detach the only mutable nested policy settings too.
                if (scenarioTemplate.alightingStopovers != null)
                    runtimeScenario.alightingStopovers = JsonUtility.FromJson<DecisionAlightingStopoverSettings>(JsonUtility.ToJson(scenarioTemplate.alightingStopovers));
                runtimeScenario.assumptions += " Launch timetable is fictional and seeded; terminal platforms are assigned from physically available tracks, with peak/off-peak headways, bounded coach seats, loads, dwell and clearance. Physical coach counts are retained.";
                return new StationDecisionRunSession { runId = Guid.NewGuid().ToString("N"), seed = seed, operatingDateIso = date,
                    localTimezoneId = TimeZoneInfo.Local.Id, localUtcOffsetMinutes = (int)now.Offset.TotalMinutes,
                    simulationStartUtcOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.SpecifyKind(day.AddSeconds(settings.initialClockSeconds), DateTimeKind.Unspecified)).TotalMinutes,
                    createdUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), randomizationVersion = DecisionDayTimetableGenerator.Version,
                    fixedSeedReplay = fixedSeedReplay, generationSettings = settings, schedule = runtimeSchedule, scenario = runtimeScenario };
            }
            catch
            {
                Release(runtimeSchedule);
                if (runtimeScenario) Release(runtimeScenario);
                throw;
            }
        }

        static List<DecisionTimetableTrack> PhysicalTracks(StationRailOperations rail)
        {
            if (rail.trains == null || rail.trains.Length == 0 || rail.trains.Any(t => !t)) throw new ArgumentException("Saved physical trainsets are required.");
            var result = new List<DecisionTimetableTrack>();
            var ids = new HashSet<int>();
            foreach (var train in rail.trains.OrderBy(t => t.trackNumber))
            {
                if (!ids.Add(train.trackNumber)) throw new ArgumentException("Exactly one pooled physical trainset is required per track.");
                int count = train.PhysicalCoachCount;
                var service = rail.schedule.services.FirstOrDefault(s => s != null && s.trackNumber == train.trackNumber && s.CoachCount == count);
                if (count < 1 || service == null) throw new ArgumentException("Each physical trainset needs a matching authored coach layout.");
                var errors = train.ValidateCoachSetup(count);
                if (errors.Count != 0) throw new ArgumentException(string.Join("; ", errors));
                result.Add(new DecisionTimetableTrack { trackNumber = train.trackNumber, maximumCoachSeats = (int[])service.coachSeatCapacities.Clone() });
            }
            return result;
        }

        static int FreshSeed()
        {
            var bytes = new byte[4];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }

        static void Release(UnityEngine.Object value)
        { if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); }
    }
}
