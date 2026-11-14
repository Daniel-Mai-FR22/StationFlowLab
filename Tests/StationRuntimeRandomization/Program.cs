using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using StationFlow.TrainStation.DecisionLab;

static class Program
{
    static int passed;
    static int Main()
    {
        try
        {
            Run("fixed seed and date reproduce the complete ordered timetable", Replay);
            Run("seeds randomize parameters and platform orders rather than just identifiers", Variation);
            Run("256 generated days obey full occupancy, doors, capacity and date constraints", ManyDays);
            Run("single-platform dispatch waits until physical movement and clearance finish", Contention);
            Run("unequal physical coach layouts are retained without exceeding their authored seats", CoachLayouts);
            Run("physical track enumeration order does not change a replay", CanonicalTracks);
            Run("peak hours generate denser departures than ordinary hours", PeakHeadways);
            Run("input templates and previously generated days remain immutable", Isolation);
            Run("changing the date changes dated service identities while retaining seeded parameters", DatedIdentity);
            Run("invalid dates tracks capacities ranges and access windows are rejected", InvalidInputs);
            Console.WriteLine("PASS " + passed + " runtime timetable tests"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void Run(string name, Action test) { test(); ++passed; Console.WriteLine("PASS " + name); }
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static DecisionTimetableSettings Settings() => new DecisionTimetableSettings { operatingDateIso = "2026-10-20" };
    static DecisionTimetableTrack[] Tracks() => new[] {
        new DecisionTimetableTrack { trackNumber = 2, maximumCoachSeats = new[] {55,55,55,55} },
        new DecisionTimetableTrack { trackNumber = 4, maximumCoachSeats = new[] {55,55,55,55} },
        new DecisionTimetableTrack { trackNumber = 5, maximumCoachSeats = new[] {55,55,55,55} },
        new DecisionTimetableTrack { trackNumber = 7, maximumCoachSeats = new[] {55,55,55,55} }
    };
    static DecisionTimetableRoute[] Routes() => new[] {
        new DecisionTimetableRoute { origin = "Dijon", destination = "Lyon-Part-Dieu" },
        new DecisionTimetableRoute { origin = "Paris-Est", destination = "Reims" },
        new DecisionTimetableRoute { origin = "Strasbourg", destination = "Nancy" }
    };
    static List<DecisionGeneratedRailService> Generate(int seed, DecisionTimetableTrack[] tracks = null, DecisionTimetableSettings settings = null)
        => DecisionDayTimetableGenerator.Generate(tracks ?? Tracks(), Routes(), settings ?? Settings(), seed);
    static string Signature(IEnumerable<DecisionGeneratedRailService> services)
    {
        var sb = new StringBuilder();
        foreach (var s in services) sb.Append(s.serviceId).Append('|').Append(s.trainNumber).Append('|').Append(s.origin).Append('|').Append(s.destination)
            .Append('|').Append(s.trackNumber).Append('|').Append(s.arrivalOffsetSeconds).Append('|').Append(s.departureOffsetSeconds)
            .Append('|').Append(s.approachDurationSeconds).Append('|').Append(s.departureDurationSeconds).Append('|').Append(s.doorDurationSeconds)
            .Append('|').Append(string.Join(",", s.coachSeatCapacities)).Append('|').Append(s.seatedCapacity).Append('|').Append(s.expectedBoarding)
            .Append('|').Append(s.expectedAlighting).Append('|').Append(s.boardingCutoffLeadSeconds).Append('|').Append(s.platformAccessLeadSeconds).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
    static void Replay()
    {
        Check(Signature(Generate(20261020)) == Signature(Generate(20261020)), "Same seed/date did not replay.");
        Check(Signature(Generate(int.MinValue)) == Signature(Generate(int.MinValue)), "Negative seed did not replay.");
    }
    static void Variation()
    {
        var days = Enumerable.Range(0, 64).Select(seed => Generate(seed)).ToArray();
        Check(days.Select(Signature).Distinct().Count() == days.Length, "Different seeds produced duplicate complete datasets.");
        Check(days.Select(d => string.Join(",", d.Take(16).Select(s => s.trackNumber))).Distinct().Count() > 60, "Platform ordering is effectively fixed.");
        Check(days.Select(d => string.Join(",", d.Take(16).Select(s => s.departureOffsetSeconds))).Distinct().Count() == days.Length, "Clock jitter is fixed.");
        Check(days.SelectMany(d => d).Select(s => s.seatedCapacity).Distinct().Count() > 10, "Coach capacities never vary.");
        Check(days.Any(d => d.Zip(d.Skip(1), (a,b) => a.trackNumber == b.trackNumber).Any(same => same)), "Dispatch is secretly round-robin.");
    }
    static void ManyDays()
    {
        for (int seed = -128; seed < 128; ++seed) ValidateDay(Generate(seed), Tracks(), Settings());
    }
    static void ValidateDay(List<DecisionGeneratedRailService> services, DecisionTimetableTrack[] tracks, DecisionTimetableSettings settings)
    {
        Check(services.Count > 0, "No services.");
        Check(services.Select(s => s.serviceId).Distinct().Count() == services.Count, "Duplicate service IDs.");
        Check(services.Select(s => s.trainNumber).Distinct().Count() == services.Count, "Duplicate train numbers.");
        Check(services.Select(s => s.trackNumber).Distinct().OrderBy(i => i).SequenceEqual(tracks.Select(t => t.trackNumber).OrderBy(i => i)), "Some physical tracks were ignored or invented.");
        int previousDeparture = int.MinValue;
        foreach (var s in services)
        {
            int absoluteArrival = s.arrivalOffsetSeconds + settings.initialClockSeconds, absoluteDeparture = s.departureOffsetSeconds + settings.initialClockSeconds;
            Check(absoluteDeparture > previousDeparture, "Services not chronological."); previousDeparture = absoluteDeparture;
            Check(absoluteArrival - s.approachDurationSeconds >= 0 && absoluteDeparture + s.departureDurationSeconds <= 86400, "Train leaves dated day.");
            Check(absoluteDeparture >= settings.firstDepartureClockSeconds && absoluteDeparture <= settings.lastDepartureClockSeconds, "Departure outside operating window.");
            Check(s.departureOffsetSeconds - s.arrivalOffsetSeconds > Math.Max(s.boardingCutoffLeadSeconds,s.doorDurationSeconds) + s.doorDurationSeconds, "No safe boarding window.");
            var track = tracks.Single(t => t.trackNumber == s.trackNumber);
            Check(s.coachSeatCapacities.Length == track.maximumCoachSeats.Length, "Invented physical coaches.");
            Check(s.coachSeatCapacities.Select((n,i) => n >= Math.Ceiling(track.maximumCoachSeats[i] * settings.minimumCoachSeatRatio) && n <= track.maximumCoachSeats[i]).All(x => x), "Seat capacity out of physical range.");
            Check(s.seatedCapacity == s.coachSeatCapacities.Sum() && s.expectedBoarding > 0 && s.expectedBoarding <= s.seatedCapacity && s.expectedAlighting > 0 && s.expectedAlighting <= s.seatedCapacity, "Demand/capacity invalid.");
        }
        foreach (var group in services.GroupBy(s => s.trackNumber))
        {
            DecisionGeneratedRailService previous = null;
            foreach (var s in group)
            {
                if (previous != null) Check(previous.departureOffsetSeconds + previous.departureDurationSeconds + settings.minimumTrackClearanceSeconds <= s.arrivalOffsetSeconds - s.approachDurationSeconds, "Occupied track intervals overlap.");
                previous = s;
            }
        }
    }
    static void Contention()
    {
        var tracks = new[] { Tracks()[0] };
        var settings = Settings(); settings.ordinaryMinimumHeadwaySeconds = settings.peakMinimumHeadwaySeconds = 1;
        settings.ordinaryMaximumHeadwaySeconds = settings.peakMaximumHeadwaySeconds = 2;
        ValidateDay(Generate(881, tracks, settings), tracks, settings);
    }
    static void CoachLayouts()
    {
        var tracks = new[] {new DecisionTimetableTrack {trackNumber = 9, maximumCoachSeats = new[]{38,80}}, new DecisionTimetableTrack {trackNumber = 14, maximumCoachSeats = new[]{12,23,70}}};
        ValidateDay(Generate(913, tracks), tracks, Settings());
    }
    static void CanonicalTracks() => Check(Signature(Generate(19,Tracks().Reverse().ToArray())) == Signature(Generate(19)), "Enumeration order changed replay.");
    static void PeakHeadways()
    {
        var peak = new List<int>(); var ordinary = new List<int>();
        for (int seed = 0; seed < 32; ++seed)
        {
            var day = Generate(seed);
            for (int i = 0; i + 1 < day.Count; ++i)
            {
                var s = day[i]; (DecisionDayTimetableGenerator.IsPeak(s.departureOffsetSeconds + Settings().initialClockSeconds) ? peak : ordinary).Add(day[i+1].departureOffsetSeconds - s.departureOffsetSeconds);
            }
        }
        Check(peak.Average() < ordinary.Average() * .8, "Peak departure density is not higher.");
    }
    static void Isolation()
    {
        var tracks = Tracks(); var settings = Settings(); var routes = Routes();
        var day = DecisionDayTimetableGenerator.Generate(tracks,routes,settings,12); string signature = Signature(day);
        var second = DecisionDayTimetableGenerator.Generate(tracks,routes,settings,12); second[0].coachSeatCapacities[0] = 1;
        Check(Signature(day) == signature && tracks[0].maximumCoachSeats.All(n => n == 55), "Generation shared mutable coach arrays.");
        Check(settings.operatingDateIso == "2026-10-20" && routes[0].origin == "Dijon", "Generation mutated templates.");
    }
    static void DatedIdentity()
    {
        var a = Generate(61); var settings = Settings(); settings.operatingDateIso = "2026-10-21"; var b = Generate(61, settings:settings);
        Check(a.Select(s => s.departureOffsetSeconds).SequenceEqual(b.Select(s => s.departureOffsetSeconds)), "Date perturbed seeded timing.");
        Check(a.Select(s => s.serviceId).Intersect(b.Select(s => s.serviceId)).Count() == 0, "Different operating days share service identities.");
    }
    static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Invalid input accepted."); }
    static void InvalidInputs()
    {
        Reject(() => { var s = Settings(); s.operatingDateIso = "2026-02-30"; Generate(0,settings:s); });
        Reject(() => { var s = Settings(); s.minimumCoachSeatRatio = double.NaN; Generate(0,settings:s); });
        Reject(() => { var s = Settings(); s.minimumCoachSeatRatio = 1.01; Generate(0,settings:s); });
        Reject(() => { var s = Settings(); s.minimumDwellSeconds = 120; Generate(0,settings:s); });
        Reject(() => { var s = Settings(); s.platformAccessLeadSeconds = 120; Generate(0,settings:s); });
        Reject(() => { var s = Settings(); s.minimumTrackClearanceSeconds = -1; Generate(0,settings:s); });
        Reject(() => { var t = Tracks(); t[1].trackNumber = t[0].trackNumber; Generate(0,t); });
        Reject(() => { var t = Tracks(); t[1].maximumCoachSeats = new[] {int.MaxValue, int.MaxValue}; Generate(0,t); });
        Reject(() => { var t = Tracks(); t[1].maximumCoachSeats[0] = 0; Generate(0,t); });
        Reject(() => DecisionDayTimetableGenerator.Generate(Tracks(),Array.Empty<DecisionTimetableRoute>(),Settings(),0));
    }
}
