using System;
using System.Collections.Generic;
using UnityEngine;

namespace StationFlow.TrainStation.RailOperations
{
    public enum StationTrainState { Offstage, Arriving, DoorsOpening, Boarding, DoorsClosing, Departing }

    [Serializable]
    public sealed class StationRailService
    {
        public string serviceId;
        public string trainNumber;
        public string origin;
        public string destination;
        [Min(1)] public int trackNumber;
        [Tooltip("Arrival seconds relative to the simulation's initial clock. Negative values describe a train already at the platform.")]
        public float arrivalOffsetSeconds;
        public float departureOffsetSeconds = 120f;
        [Min(.1f)] public float approachDurationSeconds = 60f;
        [Min(.1f)] public float departureDurationSeconds = 50f;
        [Min(.1f)] public float doorDurationSeconds = 8f;
        [Min(1)] public int seatedCapacity = 220;
        [Tooltip("Seats in each physical coach, numbered from the station head. Required for a dated timetable; empty preserves legacy aggregate capacity.")]
        public int[] coachSeatCapacities = Array.Empty<int>();
        public int CoachCount => coachSeatCapacities == null ? 0 : coachSeatCapacities.Length;
        [Min(0)] public int expectedBoarding;
        [Min(0)] public int expectedAlighting;
        [Min(0)] public int throughPassengers;
        [Tooltip("Logical passenger boarding closes this many seconds before departure; zero preserves the door-closing cutoff.")]
        [Min(0)] public float boardingCutoffLeadSeconds;
        [Tooltip("Ticketed access may open this many seconds before departure, while the matching train is present.")]
        [Min(0)] public float platformAccessLeadSeconds = 1200f;
    }

    [CreateAssetMenu(fileName = "TrainStationRailSchedule", menuName = "StationFlow/Train Station/Rail schedule")]
    public sealed class StationRailSchedule : ScriptableObject
    {
        [Range(0, 23)] public int startHour = 14;
        [Range(0, 59)] public int startMinute = 20;
        [Min(1f)] public float cycleDurationSeconds = 720f;
        public StationRailService[] services = Array.Empty<StationRailService>();
        [Tooltip("Opt-in finite dated timetable. Reuses one saved trainset per operational track; never repeats the next day.")]
        public bool finiteOperatingDay;
        public string operatingDateIso;
        [Min(0)] public float minimumTrackClearanceSeconds;
        public double StartClockSeconds => startHour * 3600d + startMinute * 60d;
        public double OperatingDayEndElapsedSeconds => 86400d - StartClockSeconds;

        public List<string> Validate()
        {
            var errors = new List<string>();
            if (startHour < 0 || startHour > 23 || startMinute < 0 || startMinute > 59) errors.Add("The initial clock must be a valid hour and minute.");
            if (!Finite(cycleDurationSeconds) || cycleDurationSeconds < 1f) errors.Add("The repeat period must be finite and at least one second.");
            if (finiteOperatingDay && !DateTime.TryParseExact(operatingDateIso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _)) errors.Add("A finite timetable needs an ISO operating date.");
            if (!Finite(minimumTrackClearanceSeconds) || minimumTrackClearanceSeconds < 0) errors.Add("Track clearance must be finite and nonnegative.");
            if (services == null || services.Length == 0) { errors.Add("Add at least one saved rail service."); return errors; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var service in services)
            {
                if (service == null) { errors.Add("The schedule contains an empty service."); continue; }
                string label = string.IsNullOrWhiteSpace(service.serviceId) ? "Unnamed service" : service.serviceId;
                if (string.IsNullOrWhiteSpace(service.serviceId) || !ids.Add(service.serviceId)) errors.Add(label + ": service IDs must be nonempty and unique.");
                if (string.IsNullOrWhiteSpace(service.trainNumber) || string.IsNullOrWhiteSpace(service.origin) || string.IsNullOrWhiteSpace(service.destination))
                    errors.Add(label + ": train number, origin and destination are required for the station displays.");
                if (service.trackNumber < 1) errors.Add(label + ": assign a positive track number.");
                if (!Finite(service.arrivalOffsetSeconds) || !Finite(service.departureOffsetSeconds) ||
                    !Finite(service.approachDurationSeconds) || !Finite(service.departureDurationSeconds) || !Finite(service.doorDurationSeconds))
                { errors.Add(label + ": all timetable durations must be finite."); continue; }
                if (service.approachDurationSeconds <= 0f || service.departureDurationSeconds <= 0f || service.doorDurationSeconds <= 0f)
                    errors.Add(label + ": movement and door durations must be positive.");
                if (Math.Abs((double)service.arrivalOffsetSeconds) > cycleDurationSeconds)
                    errors.Add(label + ": the arrival offset must be within one repeat period of the initial clock.");
                if (service.departureOffsetSeconds - service.arrivalOffsetSeconds < 2f * service.doorDurationSeconds)
                    errors.Add(label + ": the platform stop must allow doors to open and close before departure.");
                if (finiteOperatingDay)
                {
                    long coachSeats = 0;
                    if (service.coachSeatCapacities == null || service.coachSeatCapacities.Length == 0)
                        errors.Add(label + ": define the seats in every physical coach for concrete tickets.");
                    else
                    {
                        foreach (int seats in service.coachSeatCapacities)
                        { if (seats < 1) errors.Add(label + ": each coach must have a positive seat count."); coachSeats += seats; }
                        if (coachSeats != service.seatedCapacity) errors.Add(label + ": summed coach seats must equal seated capacity.");
                    }
                    if (service.arrivalOffsetSeconds - service.approachDurationSeconds + StartClockSeconds < 0 ||
                        service.departureOffsetSeconds + service.departureDurationSeconds + StartClockSeconds > 86400d)
                        errors.Add(label + ": the complete movement must stay within the dated operating day.");
                    if (service.seatedCapacity < 1 || service.expectedBoarding < 0 || service.expectedAlighting < 0 || service.throughPassengers < 0 ||
                        (long)service.expectedBoarding + service.throughPassengers > service.seatedCapacity ||
                        (long)service.expectedAlighting + service.throughPassengers > service.seatedCapacity)
                        errors.Add(label + ": planned passenger loads exceed the train capacity.");
                    if (!Finite(service.platformAccessLeadSeconds) || service.platformAccessLeadSeconds < 0)
                        errors.Add(label + ": platform access lead must be finite and nonnegative.");
                    else if (service.platformAccessLeadSeconds <= Math.Max(service.doorDurationSeconds, service.boardingCutoffLeadSeconds))
                        errors.Add(label + ": platform access must open before the boarding cutoff, leaving a positive access window.");
                    if (!Finite(service.boardingCutoffLeadSeconds) || service.boardingCutoffLeadSeconds < 0 ||
                        service.departureOffsetSeconds - service.arrivalOffsetSeconds <= Math.Max(service.doorDurationSeconds,service.boardingCutoffLeadSeconds) + service.doorDurationSeconds)
                        errors.Add(label + ": boarding cutoff must leave a positive boarding window after the doors open.");
                }
                double occupiedDuration = service.departureOffsetSeconds + service.departureDurationSeconds -
                    (service.arrivalOffsetSeconds - service.approachDurationSeconds);
                if (!finiteOperatingDay && occupiedDuration >= cycleDurationSeconds)
                    errors.Add(label + ": this train would overlap its own next occurrence; lengthen the repeat period.");
            }
            if (errors.Count != 0) return errors;
            // Compare occupied track intervals on a repeating timeline, including intervals spanning the cycle boundary.
            for (int a = 0; a < services.Length; ++a)
            for (int b = a + 1; b < services.Length; ++b)
            {
                var first = services[a]; var second = services[b];
                if (first.trackNumber != second.trackNumber) continue;
                if (finiteOperatingDay)
                {
                    double aStart = first.arrivalOffsetSeconds - first.approachDurationSeconds;
                    double aEnd = first.departureOffsetSeconds + first.departureDurationSeconds + minimumTrackClearanceSeconds;
                    double bStart = second.arrivalOffsetSeconds - second.approachDurationSeconds;
                    double bEnd = second.departureOffsetSeconds + second.departureDurationSeconds + minimumTrackClearanceSeconds;
                    if (Math.Max(aStart,bStart) < Math.Min(aEnd,bEnd))
                        errors.Add($"Track {first.trackNumber}: {first.serviceId} overlaps {second.serviceId}, including clearance.");
                    continue;
                }
                double firstStart = Mod(first.arrivalOffsetSeconds - first.approachDurationSeconds, cycleDurationSeconds);
                double firstEnd = firstStart + first.departureOffsetSeconds + first.departureDurationSeconds - first.arrivalOffsetSeconds + first.approachDurationSeconds;
                double secondStart = Mod(second.arrivalOffsetSeconds - second.approachDurationSeconds, cycleDurationSeconds);
                double secondDuration = second.departureOffsetSeconds + second.departureDurationSeconds - second.arrivalOffsetSeconds + second.approachDurationSeconds;
                for (int shift = -1; shift <= 1; ++shift)
                {
                    double start = secondStart + shift * cycleDurationSeconds;
                    if (Math.Max(firstStart, start) >= Math.Min(firstEnd, start + secondDuration)) continue;
                    errors.Add($"Track {first.trackNumber}: {first.serviceId} overlaps {second.serviceId} on the repeating timetable.");
                    break;
                }
            }
            return errors;
        }

        /// <summary>Pure dated/repeating timing snapshot, with absolute station-clock timestamps.</summary>
        public StationRailServiceSnapshot GetSnapshot(int index, double elapsed)
        {
            if (services == null || index < 0 || index >= services.Length || services[index] == null) return default;
            var service = services[index];
            var pose = StationRailTiming.Evaluate(service, elapsed, cycleDurationSeconds, StartClockSeconds, finiteOperatingDay);
            return new StationRailServiceSnapshot { service = service, state = pose.state, arrivalSeconds = pose.arrivalClock,
                departureSeconds = pose.departureClock, doorOpenFraction = pose.doors,
                boardingCutoffSeconds = pose.departureClock - Math.Max(service.doorDurationSeconds,service.boardingCutoffLeadSeconds) };
        }

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        internal static double Mod(double value, double modulus) => (value % modulus + modulus) % modulus;
    }

    [Serializable]
    public struct StationRailServiceSnapshot
    {
        public StationRailService service;
        public StationTrain train;
        public StationTrainState state;
        public double arrivalSeconds;
        public double departureSeconds;
        public double boardingCutoffSeconds;
        public float doorOpenFraction;
    }

    internal struct StationRailPose
    {
        public StationTrainState state;
        public float progress;
        public float doors;
        public long occurrence;
        public double arrivalClock;
        public double departureClock;
    }

    internal static class StationRailTiming
    {
        internal static StationRailPose Evaluate(StationRailService service, double elapsed, double period, double startClock, bool finiteDay = false)
        {
            double approachStart = service.arrivalOffsetSeconds - service.approachDurationSeconds;
            long occurrence = finiteDay ? 0 : (long)Math.Floor((elapsed - approachStart) / period);
            double arrival = service.arrivalOffsetSeconds + occurrence * period;
            double departure = service.departureOffsetSeconds + occurrence * period;
            var pose = new StationRailPose { occurrence = occurrence, arrivalClock = startClock + arrival, departureClock = startClock + departure };
            if (finiteDay && elapsed < approachStart) { pose.state = StationTrainState.Offstage; return pose; }
            if (elapsed < arrival)
            {
                pose.state = StationTrainState.Arriving;
                pose.progress = Unit((elapsed - arrival + service.approachDurationSeconds) / service.approachDurationSeconds);
            }
            else if (elapsed < arrival + service.doorDurationSeconds)
            {
                pose.state = StationTrainState.DoorsOpening;
                pose.progress = 1f;
                pose.doors = Smooth(Unit((elapsed - arrival) / service.doorDurationSeconds));
            }
            else if (elapsed < departure - service.doorDurationSeconds)
            {
                pose.state = StationTrainState.Boarding;
                pose.progress = 1f;
                pose.doors = 1f;
            }
            else if (elapsed < departure)
            {
                pose.state = StationTrainState.DoorsClosing;
                pose.progress = 1f;
                pose.doors = 1f - Smooth(Unit((elapsed - departure + service.doorDurationSeconds) / service.doorDurationSeconds));
            }
            else if (elapsed < departure + service.departureDurationSeconds)
            {
                pose.state = StationTrainState.Departing;
                pose.progress = Unit((elapsed - departure) / service.departureDurationSeconds);
            }
            else
            {
                pose.state = StationTrainState.Offstage;
                // Boards announce the next occurrence once the completed train has left the visible corridor.
                if (!finiteDay) { pose.arrivalClock += period; pose.departureClock += period; }
            }
            return pose;
        }

        internal static float Unit(double value) => Mathf.Clamp01((float)value);
        internal static float Smooth(float value) => value * value * value * (value * (value * 6f - 15f) + 10f);
        internal static float SmoothDerivative(float value) => 30f * value * value * (value - 1f) * (value - 1f);
    }
}
