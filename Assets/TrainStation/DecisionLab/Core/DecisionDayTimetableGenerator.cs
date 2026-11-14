using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationFlow.TrainStation.DecisionLab
{
    [Serializable]
    public sealed class DecisionTimetableTrack
    {
        public int trackNumber;
        // Maximum service capacity of each physical coach. Generation never adds coaches or seats.
        public int[] maximumCoachSeats = Array.Empty<int>();
    }

    [Serializable]
    public sealed class DecisionTimetableRoute
    {
        public string origin, destination;
    }

    [Serializable]
    public sealed class DecisionTimetableSettings
    {
        public string operatingDateIso;
        public int initialClockSeconds = 7 * 3600 + 40 * 60;
        public int firstDepartureClockSeconds = 5 * 3600;
        public int lastDepartureClockSeconds = 22 * 3600 + 45 * 60;
        public int peakMinimumHeadwaySeconds = 420, peakMaximumHeadwaySeconds = 720;
        public int ordinaryMinimumHeadwaySeconds = 660, ordinaryMaximumHeadwaySeconds = 1080;
        public int minimumDwellSeconds = 360, maximumDwellSeconds = 1080;
        public int minimumApproachSeconds = 45, maximumApproachSeconds = 85;
        public int minimumDepartureSeconds = 45, maximumDepartureSeconds = 70;
        public int minimumDoorSeconds = 6, maximumDoorSeconds = 10;
        public int minimumTrackClearanceSeconds = 60;
        public int boardingCutoffLeadSeconds = 120, platformAccessLeadSeconds = 1200;
        public double minimumCoachSeatRatio = .85;
    }

    [Serializable]
    public sealed class DecisionGeneratedRailService
    {
        public string serviceId, trainNumber, origin, destination;
        public int trackNumber, arrivalOffsetSeconds, departureOffsetSeconds;
        public int approachDurationSeconds, departureDurationSeconds, doorDurationSeconds;
        public int[] coachSeatCapacities;
        public int seatedCapacity, expectedBoarding, expectedAlighting;
        public int boardingCutoffLeadSeconds, platformAccessLeadSeconds;
    }

    /// <summary>Pure reproducible fictional terminal timetable. The seed controls both train parameters and dispatch order.</summary>
    public static class DecisionDayTimetableGenerator
    {
        public const string Version = "terminal-day-xorshift32-v1";

        public static List<DecisionGeneratedRailService> Generate(IReadOnlyList<DecisionTimetableTrack> tracks,
            IReadOnlyList<DecisionTimetableRoute> routes, DecisionTimetableSettings settings, int seed)
        {
            Validate(tracks, routes, settings);
            // Canonical track ordering makes replay independent of Unity's object enumeration order.
            var orderedTracks = new List<DecisionTimetableTrack>(tracks);
            orderedTracks.Sort((a, b) => a.trackNumber.CompareTo(b.trackNumber));
            var random = new RandomBits(seed);
            var availableAt = new int[orderedTracks.Count];
            var serviceCounts = new int[orderedTracks.Count];
            var result = new List<DecisionGeneratedRailService>();
            int departure = settings.firstDepartureClockSeconds + random.Integer(0, 8 * 60);
            int serial = 0;
            string date = settings.operatingDateIso.Replace("-", "");
            int numberPrefix = random.Integer(100, 899);
            while (departure <= settings.lastDepartureClockSeconds)
            {
                int dwell = random.Integer(settings.minimumDwellSeconds, settings.maximumDwellSeconds);
                int approach = random.Integer(settings.minimumApproachSeconds, settings.maximumApproachSeconds);
                int leaving = random.Integer(settings.minimumDepartureSeconds, settings.maximumDepartureSeconds);
                int doors = random.Integer(settings.minimumDoorSeconds, settings.maximumDoorSeconds);
                int selected = SelectTrack(orderedTracks.Count, departure - dwell - approach, availableAt, serviceCounts, random);
                if (selected < 0)
                {
                    int earliest = int.MaxValue;
                    foreach (int available in availableAt) earliest = Math.Min(earliest, available + dwell + approach);
                    departure = Math.Max(departure, earliest);
                    selected = SelectTrack(orderedTracks.Count, departure - dwell - approach, availableAt, serviceCounts, random);
                }
                if (departure > settings.lastDepartureClockSeconds || departure + leaving > 86400) break;
                if (selected < 0) throw new InvalidOperationException("No feasible physical track after the earliest release.");
                var track = orderedTracks[selected];
                var route = routes[random.Integer(0, routes.Count - 1)];
                var seats = new int[track.maximumCoachSeats.Length];
                int capacity = 0;
                for (int i = 0; i < seats.Length; ++i)
                {
                    seats[i] = random.Integer(Math.Max(1, (int)Math.Ceiling(track.maximumCoachSeats[i] * settings.minimumCoachSeatRatio)), track.maximumCoachSeats[i]);
                    capacity = checked(capacity + seats[i]);
                }
                bool peak = IsPeak(departure);
                double low = peak ? .45 : .25, high = peak ? .95 : .8;
                int boarding = Math.Max(1, (int)Math.Round(capacity * (low + random.Unit() * (high - low)), MidpointRounding.AwayFromZero));
                int alighting = Math.Max(1, (int)Math.Round(capacity * (low + random.Unit() * (high - low)), MidpointRounding.AwayFromZero));
                ++serial;
                result.Add(new DecisionGeneratedRailService {
                    serviceId = "daily-" + date + "-" + serial.ToString("D3", CultureInfo.InvariantCulture) + "-v" + track.trackNumber,
                    trainNumber = (numberPrefix * 1000 + serial).ToString(CultureInfo.InvariantCulture), origin = route.origin, destination = route.destination,
                    trackNumber = track.trackNumber, arrivalOffsetSeconds = departure - dwell - settings.initialClockSeconds,
                    departureOffsetSeconds = departure - settings.initialClockSeconds, approachDurationSeconds = approach,
                    departureDurationSeconds = leaving, doorDurationSeconds = doors, coachSeatCapacities = seats, seatedCapacity = capacity,
                    expectedBoarding = boarding, expectedAlighting = alighting, boardingCutoffLeadSeconds = settings.boardingCutoffLeadSeconds,
                    platformAccessLeadSeconds = settings.platformAccessLeadSeconds
                });
                availableAt[selected] = checked(departure + leaving + settings.minimumTrackClearanceSeconds);
                ++serviceCounts[selected];
                int lowHeadway = peak ? settings.peakMinimumHeadwaySeconds : settings.ordinaryMinimumHeadwaySeconds;
                int highHeadway = peak ? settings.peakMaximumHeadwaySeconds : settings.ordinaryMaximumHeadwaySeconds;
                departure = checked(departure + random.Integer(lowHeadway, highHeadway));
            }
            foreach (int count in serviceCounts) if (count == 0) throw new ArgumentException("Operating window is too short to serve every physical track.");
            return result;
        }

        public static bool IsPeak(int clockSeconds) => clockSeconds >= 7 * 3600 && clockSeconds < 9 * 3600 ||
            clockSeconds >= 16 * 3600 && clockSeconds < 19 * 3600;

        static int SelectTrack(int count, int approachStartsAt, int[] availableAt, int[] serviceCounts, RandomBits random)
        {
            bool unusedFeasible = false;
            for (int i = 0; i < count; ++i) if (availableAt[i] <= approachStartsAt && serviceCounts[i] == 0) unusedFeasible = true;
            int selected = -1; double best = double.NegativeInfinity;
            for (int i = 0; i < count; ++i)
            {
                if (availableAt[i] > approachStartsAt || unusedFeasible && serviceCounts[i] != 0) continue;
                // Random dispatch with mild usage balancing; no cyclic or fixed platform sequence.
                double score = random.Unit() / Math.Sqrt(1d + serviceCounts[i]);
                if (score > best) { best = score; selected = i; }
            }
            return selected;
        }

        static void Validate(IReadOnlyList<DecisionTimetableTrack> tracks, IReadOnlyList<DecisionTimetableRoute> routes, DecisionTimetableSettings s)
        {
            if (s == null || !DateTime.TryParseExact(s.operatingDateIso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ArgumentException("An ISO simulation date is required.");
            if (s.initialClockSeconds < 0 || s.initialClockSeconds >= 86400 || s.initialClockSeconds % 60 != 0 ||
                s.firstDepartureClockSeconds < 0 || s.lastDepartureClockSeconds >= 86400 || s.firstDepartureClockSeconds >= s.lastDepartureClockSeconds ||
                s.minimumTrackClearanceSeconds < 0 || s.boardingCutoffLeadSeconds < 0 || s.platformAccessLeadSeconds <= Math.Max(s.boardingCutoffLeadSeconds, s.maximumDoorSeconds) ||
                double.IsNaN(s.minimumCoachSeatRatio) || double.IsInfinity(s.minimumCoachSeatRatio) || s.minimumCoachSeatRatio <= 0 || s.minimumCoachSeatRatio > 1)
                throw new ArgumentException("Invalid dated operating window, clearance, access cutoff or seat ratio.");
            Range(s.peakMinimumHeadwaySeconds, s.peakMaximumHeadwaySeconds); Range(s.ordinaryMinimumHeadwaySeconds, s.ordinaryMaximumHeadwaySeconds);
            Range(s.minimumDwellSeconds, s.maximumDwellSeconds); Range(s.minimumApproachSeconds, s.maximumApproachSeconds);
            Range(s.minimumDepartureSeconds, s.maximumDepartureSeconds); Range(s.minimumDoorSeconds, s.maximumDoorSeconds);
            if (s.minimumDwellSeconds <= Math.Max(s.maximumDoorSeconds, s.boardingCutoffLeadSeconds) + s.maximumDoorSeconds ||
                s.firstDepartureClockSeconds < s.maximumDwellSeconds + s.maximumApproachSeconds ||
                (long)s.lastDepartureClockSeconds + s.maximumDepartureSeconds > 86400)
                throw new ArgumentException("The complete dwell, doors and approach/departure must fit inside the operating day.");
            if (tracks == null || tracks.Count == 0 || tracks.Count > 64) throw new ArgumentException("Supply the physical terminal tracks.");
            var ids = new HashSet<int>();
            foreach (var track in tracks)
            {
                if (track == null || track.trackNumber < 1 || !ids.Add(track.trackNumber) || track.maximumCoachSeats == null || track.maximumCoachSeats.Length == 0 || track.maximumCoachSeats.Length > 64)
                    throw new ArgumentException("Physical track IDs must be unique and each must contain coaches.");
                long capacity = 0;
                foreach (int seats in track.maximumCoachSeats) { if (seats < 1) throw new ArgumentException("Coach capacity must be positive."); capacity += seats; }
                if (capacity > int.MaxValue) throw new ArgumentException("Coach seat totals overflow capacity.");
            }
            if (routes == null || routes.Count == 0) throw new ArgumentException("Supply at least one fictional route.");
            foreach (var route in routes) if (route == null || string.IsNullOrWhiteSpace(route.origin) || string.IsNullOrWhiteSpace(route.destination))
                throw new ArgumentException("A route requires its origin and destination.");
        }

        static void Range(int low, int high)
        { if (low < 1 || high < low || high > 86400) throw new ArgumentException("Durations must be positive, ordered and bounded by one day."); }

        sealed class RandomBits
        {
            uint state;
            public RandomBits(int seed) { state = unchecked((uint)seed) ^ 0x9e3779b9u; if (state == 0) state = 0x6d2b79f5u; }
            uint Next() { uint x = state; x ^= x << 13; x ^= x >> 17; x ^= x << 5; return state = x; }
            public double Unit() => Next() / 4294967296d;
            public int Integer(int low, int high)
            {
                uint range = checked((uint)((long)high - low + 1));
                uint threshold = unchecked(0u - range) % range, value;
                do value = Next(); while (value < threshold);
                return checked(low + (int)(value % range));
            }
        }
    }
}
