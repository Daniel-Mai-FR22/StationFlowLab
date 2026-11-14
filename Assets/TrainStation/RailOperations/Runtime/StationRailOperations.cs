using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;

namespace StationFlow.TrainStation.RailOperations
{
    [Serializable] public sealed class StationTrainEvent : UnityEvent<StationTrain> { }

    /// <summary>One deterministic clock for saved trains and station displays. No runtime model or UI creation.</summary>
    [DisallowMultipleComponent]
    public sealed class StationRailOperations : MonoBehaviour
    {
        const double MaximumElapsedSeconds = 1e12;
        const int MaximumBoundaryNotifications = 4096;
        public StationRailSchedule schedule;
        public StationTrain[] trains = Array.Empty<StationTrain>();
        [Min(0f)] public float simulationRate = 3f;
        public bool playOnStart = true;
        public StationTrainEvent trainArrived = new StationTrainEvent();
        public StationTrainEvent trainDeparted = new StationTrainEvent();
        public StationTrainEvent trainStateChanged = new StationTrainEvent();
        public UnityEvent simulationReset = new UnityEvent();
        [Tooltip("A huge manual advance keeps exact counters and final poses, but coalesces thousands of intermediate notifications.")]
        public UnityEvent timelineSkipped = new UnityEvent();
        [SerializeField] bool paused;
        [SerializeField] double elapsedSimulationSeconds;
        [SerializeField] string configurationIssue;
        [SerializeField] bool lastAdvanceCoalesced;
        readonly Dictionary<string, StationTrain> trainsByService = new Dictionary<string, StationTrain>(StringComparer.Ordinal);
        readonly Dictionary<string, long> serviceArrivals = new Dictionary<string, long>(StringComparer.Ordinal);
        readonly Dictionary<string, long> serviceDepartures = new Dictionary<string, long>(StringComparer.Ordinal);
        readonly Dictionary<int, StationRailService[]> servicesByTrack = new Dictionary<int, StationRailService[]>();
        readonly List<StationTrain> changedTrains = new List<StationTrain>();
        readonly List<Boundary> boundaries = new List<Boundary>();
        bool initialized;
        bool advancing;
        int timelineRevision;

        struct Boundary
        {
            public double time;
            public int serviceIndex;
            public int kind;
        }

        public bool IsPaused => paused;
        public bool IsReady => initialized && string.IsNullOrEmpty(configurationIssue);
        public string ConfigurationIssue => configurationIssue;
        public string LastError => configurationIssue;
        public bool LastAdvanceCoalesced => lastAdvanceCoalesced;
        public double ElapsedSimulationSeconds => elapsedSimulationSeconds;
        /// <summary>Absolute simulation seconds from midnight, continuing beyond 24 hours without wrapping the timeline.</summary>
        public double ClockSeconds => (schedule ? schedule.StartClockSeconds : 0d) + elapsedSimulationSeconds;
        public string ClockText => FormatClock(ClockSeconds);
        public long CompletedCycles => schedule && !schedule.finiteOperatingDay && schedule.cycleDurationSeconds > 0 ? (long)Math.Floor(elapsedSimulationSeconds / schedule.cycleDurationSeconds) : 0;
        public bool DayComplete => schedule && schedule.finiteOperatingDay && elapsedSimulationSeconds >= schedule.OperatingDayEndElapsedSeconds;
        public long GetArrivalCount(string serviceId) => serviceId != null && serviceArrivals.TryGetValue(serviceId, out var count) ? count : 0;
        public long GetDepartureCount(string serviceId) => serviceId != null && serviceDepartures.TryGetValue(serviceId, out var count) ? count : 0;

        void Start() => ResetSimulation();

        void Update()
        {
            if (!IsReady || paused || simulationRate == 0f || DayComplete) return;
            AdvanceSimulation(Time.deltaTime * (double)simulationRate);
        }

        public static string FormatClock(double absoluteSeconds)
        {
            if (!StationRailSchedule.Finite(absoluteSeconds)) return "--:--:--";
            int seconds = (int)Math.Floor(StationRailSchedule.Mod(absoluteSeconds, 86400d));
            return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        }

        public List<string> ValidateConfiguration()
        {
            var errors = schedule ? schedule.Validate() : new List<string> { "Assign a saved rail schedule." };
            if (!StationRailSchedule.Finite(simulationRate) || simulationRate < 0f) errors.Add("Simulation rate must be finite and nonnegative.");
            if (trains == null || trains.Length == 0) { errors.Add("Assign the saved trainset instances."); return errors; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var tracks = new HashSet<int>();
            var serviceIds = new HashSet<string>(StringComparer.Ordinal);
            if (schedule && schedule.services != null)
                foreach (var service in schedule.services) if (service != null && !string.IsNullOrWhiteSpace(service.serviceId)) serviceIds.Add(service.serviceId);
            foreach (var train in trains)
            {
                if (!train) { errors.Add("The train list contains an empty reference."); continue; }
                errors.AddRange(train.ValidateSetup());
                if (schedule && schedule.finiteOperatingDay)
                {
                    if (!tracks.Add(train.trackNumber)) errors.Add("Finite timetable requires exactly one pooled trainset per track: " + train.trackNumber);
                    if (schedule.services == null || !schedule.services.Any(s => s != null && s.trackNumber == train.trackNumber))
                        errors.Add(train.name + ": pooled track has no dated services.");
                    else
                    {
                        foreach (int coachCount in schedule.services.Where(s => s != null && s.trackNumber == train.trackNumber).Select(s => s.CoachCount).Distinct())
                            errors.AddRange(train.ValidateCoachSetup(coachCount));
                    }
                    continue;
                }
                if (!ids.Add(train.serviceId ?? "")) errors.Add("More than one saved train uses service ID " + train.serviceId + ".");
                if (!serviceIds.Contains(train.serviceId ?? "")) errors.Add(train.name + ": its service ID is not in the assigned schedule.");
                if (!schedule || schedule.services == null) continue;
                foreach (var service in schedule.services)
                    if (service != null && service.serviceId == train.serviceId && service.trackNumber != train.trackNumber)
                        errors.Add(train.name + ": train and timetable track numbers differ.");
            }
            if (schedule && schedule.finiteOperatingDay && schedule.services != null)
            {
                foreach (var service in schedule.services)
                    if (service != null && !tracks.Contains(service.trackNumber)) errors.Add("No pooled trainset for track " + service.trackNumber + ".");
            }
            else foreach (var serviceId in serviceIds) if (!ids.Contains(serviceId)) errors.Add("No saved train is assigned to service " + serviceId + ".");
            return errors;
        }

        bool Initialize(bool force = false)
        {
            if (initialized && !force) return IsReady;
            initialized = true;
            var errors = ValidateConfiguration();
            configurationIssue = string.Join("\n", errors);
            trainsByService.Clear();
            servicesByTrack.Clear();
            if (errors.Count > 0)
            {
                paused = true;
                if (Application.isPlaying) Debug.LogError("[Station rail] " + configurationIssue, this);
                return false;
            }
            if (schedule.finiteOperatingDay)
            {
                foreach (var service in schedule.services) trainsByService.Add(service.serviceId, trains.Single(t => t.trackNumber == service.trackNumber));
                foreach (var group in schedule.services.GroupBy(s => s.trackNumber))
                    servicesByTrack[group.Key] = group.OrderBy(s => s.arrivalOffsetSeconds).ToArray();
            }
            else
            {
                foreach (var train in trains) trainsByService.Add(train.serviceId, train);
                foreach (var service in schedule.services) trainsByService[service.serviceId].Bind(service);
            }
            return true;
        }

        public void Pause() { paused = true; }
        public void Resume() { if (Initialize()) paused = false; }
        public void TogglePause() { if (paused) Resume(); else Pause(); }

        [ContextMenu("Reset rail simulation")]
        public void ResetSimulation()
        {
            if (!Initialize(true)) return;
            ++timelineRevision;
            elapsedSimulationSeconds = 0d;
            lastAdvanceCoalesced = false;
            serviceArrivals.Clear(); serviceDepartures.Clear();
            paused = !playOnStart;
            foreach (var train in trains) train.ResetCounts();
            EvaluateAll(false);
            if (Application.isPlaying) simulationReset.Invoke();
        }

        /// <summary>Editor authoring preview; staged trains remain visible and inspectable.</summary>
        public void EvaluatePreview(double elapsedSeconds = 0d)
        {
            if (Application.isPlaying) return;
            if (!ValidTime(elapsedSeconds) || !Initialize(true)) return;
            elapsedSimulationSeconds = schedule.finiteOperatingDay ? Math.Min(elapsedSeconds, schedule.OperatingDayEndElapsedSeconds) : elapsedSeconds;
            lastAdvanceCoalesced = false;
            EvaluateAll(false);
        }

        /// <summary>Explicit seek. It synchronizes all poses atomically without replaying historical arrivals/departures.</summary>
        public void SetSimulationTime(double elapsedSeconds)
        {
            if (!ValidTime(elapsedSeconds) || !Initialize()) return;
            ++timelineRevision;
            elapsedSimulationSeconds = schedule.finiteOperatingDay ? Math.Min(elapsedSeconds, schedule.OperatingDayEndElapsedSeconds) : elapsedSeconds;
            lastAdvanceCoalesced = false;
            EvaluateAll(Application.isPlaying);
        }

        public void AdvanceSimulation(double seconds)
        {
            if (advancing || !Initialize() || paused || seconds == 0d) return;
            double target = elapsedSimulationSeconds + seconds;
            if (!StationRailSchedule.Finite(seconds) || seconds < 0d || !ValidTime(target))
            {
                configurationIssue = "AdvanceSimulation requires a finite nonnegative interval within the supported timeline.";
                paused = true;
                return;
            }
            if (schedule.finiteOperatingDay) target = Math.Min(target, schedule.OperatingDayEndElapsedSeconds);
            advancing = true;
            int revision = timelineRevision;
            try
            {
                double previous = elapsedSimulationSeconds;
                lastAdvanceCoalesced = !CollectBoundaries(previous, target);
                if (lastAdvanceCoalesced)
                {
                    foreach (var service in schedule.services)
                    {
                        var train = trainsByService[service.serviceId];
                        long arrivals = CrossingCount(previous, target, service.arrivalOffsetSeconds);
                        long departures = CrossingCount(previous, target, service.departureOffsetSeconds);
                        train.AddArrivals(arrivals); train.AddDepartures(departures);
                        AddCount(serviceArrivals, service.serviceId, arrivals); AddCount(serviceDepartures, service.serviceId, departures);
                    }
                    elapsedSimulationSeconds = target;
                    EvaluateAll(true);
                    if (!this || !isActiveAndEnabled || timelineRevision != revision) return;
                    timelineSkipped.Invoke();
                    return;
                }
                for (int index = 0; index < boundaries.Count;)
                {
                    double boundaryTime = boundaries[index].time;
                    int end = index + 1;
                    while (end < boundaries.Count && boundaries[end].time == boundaryTime) ++end;
                    elapsedSimulationSeconds = boundaryTime;
                    // Simultaneous services form one atomic clock boundary, even if a listener pauses it.
                    for (int i = index; i < end; ++i)
                    {
                        var boundary = boundaries[i];
                        var train = trainsByService[schedule.services[boundary.serviceIndex].serviceId];
                        string serviceId = schedule.services[boundary.serviceIndex].serviceId;
                        if (boundary.kind == 1) { train.AddArrivals(1); AddCount(serviceArrivals,serviceId,1); }
                        else if (boundary.kind == 4) { train.AddDepartures(1); AddCount(serviceDepartures,serviceId,1); }
                    }
                    EvaluateAll(true);
                    if (!this || !isActiveAndEnabled || timelineRevision != revision) return;
                    for (int i = index; i < end; ++i)
                    {
                        var boundary = boundaries[i];
                        var train = trainsByService[schedule.services[boundary.serviceIndex].serviceId];
                        if (boundary.kind == 1) trainArrived.Invoke(train);
                        else if (boundary.kind == 4) trainDeparted.Invoke(train);
                        if (!this || !isActiveAndEnabled || timelineRevision != revision) return;
                    }
                    if (!this || !isActiveAndEnabled || paused || timelineRevision != revision) return;
                    index = end;
                }
                elapsedSimulationSeconds = target;
                EvaluateAll(true);
            }
            finally { advancing = false; }
        }

        bool CollectBoundaries(double previous, double target)
        {
            boundaries.Clear();
            double period = schedule.cycleDurationSeconds;
            for (int i = 0; i < schedule.services.Length; ++i)
            {
                var service = schedule.services[i];
                var offsets = new double[] { service.arrivalOffsetSeconds - service.approachDurationSeconds,
                    service.arrivalOffsetSeconds, service.arrivalOffsetSeconds + service.doorDurationSeconds,
                    service.departureOffsetSeconds - service.doorDurationSeconds, service.departureOffsetSeconds,
                    service.departureOffsetSeconds + service.departureDurationSeconds };
                for (int kind = 0; kind < offsets.Length; ++kind)
                {
                    if (schedule.finiteOperatingDay)
                    {
                        if (offsets[kind] > previous && offsets[kind] <= target)
                            boundaries.Add(new Boundary { time = offsets[kind], serviceIndex = i, kind = kind });
                        continue;
                    }
                    long first = (long)Math.Floor((previous - offsets[kind]) / period) + 1;
                    long last = (long)Math.Floor((target - offsets[kind]) / period);
                    if (last < first) continue;
                    if (last - first + 1 > MaximumBoundaryNotifications - boundaries.Count) { boundaries.Clear(); return false; }
                    for (long cycle = first; cycle <= last; ++cycle)
                        boundaries.Add(new Boundary { time = offsets[kind] + cycle * period, serviceIndex = i, kind = kind });
                }
            }
            boundaries.Sort((a, b) => { int order = a.time.CompareTo(b.time); if (order != 0) return order;
                order = a.serviceIndex.CompareTo(b.serviceIndex); return order != 0 ? order : a.kind.CompareTo(b.kind); });
            return true;
        }

        long CrossingCount(double previous, double target, double offset) => schedule.finiteOperatingDay
            ? (offset > previous && offset <= target ? 1 : 0)
            : (long)Math.Floor((target - offset) / schedule.cycleDurationSeconds) - (long)Math.Floor((previous - offset) / schedule.cycleDurationSeconds);

        static void AddCount(Dictionary<string,long> counts, string id, long increment)
        { counts.TryGetValue(id,out long previous); counts[id] = previous + increment; }

        void EvaluateAll(bool notify)
        {
            changedTrains.Clear();
            foreach (var train in trains)
            {
                if (!train) continue;
                bool reassigned = false;
                if (schedule.finiteOperatingDay)
                {
                    // Keep the physical train bound until it has completely exited. Otherwise stage the next service.
                    var services = servicesByTrack[train.trackNumber];
                    var assigned = services.FirstOrDefault(s => elapsedSimulationSeconds >= s.arrivalOffsetSeconds - s.approachDurationSeconds &&
                        elapsedSimulationSeconds < s.departureOffsetSeconds + s.departureDurationSeconds)
                        ?? services.FirstOrDefault(s => s.arrivalOffsetSeconds - s.approachDurationSeconds > elapsedSimulationSeconds)
                        ?? services.Last();
                    reassigned = train.Service != assigned;
                    train.Bind(assigned, true);
                }
                if (train.Evaluate(elapsedSimulationSeconds, schedule.cycleDurationSeconds, schedule.StartClockSeconds, Application.isPlaying, schedule.finiteOperatingDay) || reassigned)
                    changedTrains.Add(train);
            }
            if (!notify) return;
            // Poses and doors are all current before any listener reads the shared snapshot.
            var notifications = changedTrains.ToArray();
            int revision = timelineRevision;
            foreach (var train in notifications)
            {
                if (!this || !isActiveAndEnabled || timelineRevision != revision) return;
                trainStateChanged.Invoke(train);
            }
        }

        public StationRailServiceSnapshot GetServiceSnapshot(int index)
        {
            if (!schedule || schedule.services == null || index < 0 || index >= schedule.services.Length) return default;
            var service = schedule.services[index];
            if (service == null) return default;
            StationTrain train = null;
            if (!trainsByService.TryGetValue(service.serviceId ?? "", out train) && trains != null)
                foreach (var candidate in trains) if (candidate && candidate.serviceId == service.serviceId) { train = candidate; break; }
            var result = schedule.GetSnapshot(index,elapsedSimulationSeconds);
            bool bound = train && train.Service == service;
            result.train = schedule.finiteOperatingDay && !bound ? null : train;
            if (bound) { result.state = train.State; result.doorOpenFraction = train.DoorOpenFraction; }
            return result;
        }

        /// <summary>Upcoming dated services; finite arrivals disappear after arrival, departures after departure.</summary>
        public int[] GetUpcomingServiceIndices(bool arrivals = false, int? track = null)
        {
            if (!schedule || schedule.services == null) return Array.Empty<int>();
            return Enumerable.Range(0,schedule.services.Length).Where(i => schedule.services[i] != null &&
                (!track.HasValue || schedule.services[i].trackNumber == track.Value) &&
                (!schedule.finiteOperatingDay || (arrivals ? schedule.services[i].arrivalOffsetSeconds : schedule.services[i].departureOffsetSeconds) >= elapsedSimulationSeconds))
                .OrderBy(i => arrivals ? GetServiceSnapshot(i).arrivalSeconds : GetServiceSnapshot(i).departureSeconds).ToArray();
        }

        /// <summary>Transactional delay for one finite-day departure. Future same-track movements are held offstage and shifted together.</summary>
        public bool TryDelayService(string serviceId, float delaySeconds, out string[] affectedServiceIds, out string error)
        {
            affectedServiceIds = Array.Empty<string>(); error = "";
            if (!schedule || !schedule.finiteOperatingDay) { error = "Safe dispatch delay requires the finite operating-day timetable."; return false; }
            if (!Initialize()) { error = configurationIssue; return false; }
            if (!StationRailSchedule.Finite(delaySeconds) || delaySeconds <= 0) { error = "Delay must be finite and positive."; return false; }
            var service = schedule.services.FirstOrDefault(s => s.serviceId == serviceId);
            if (service == null || elapsedSimulationSeconds >= service.departureOffsetSeconds - service.doorDurationSeconds)
            { error = "This departure is missing or its doors are already closing."; return false; }
            var arrivals = schedule.services.Select(s => s.arrivalOffsetSeconds).ToArray();
            var departures = schedule.services.Select(s => s.departureOffsetSeconds).ToArray();
            var changed = new List<string>();
            service.departureOffsetSeconds += delaySeconds; changed.Add(service.serviceId);
            float previousClear = service.departureOffsetSeconds + service.departureDurationSeconds + schedule.minimumTrackClearanceSeconds;
            foreach (var next in schedule.services.Where(s => s.trackNumber == service.trackNumber && s != service &&
                s.arrivalOffsetSeconds > service.arrivalOffsetSeconds).OrderBy(s => s.arrivalOffsetSeconds))
            {
                float start = next.arrivalOffsetSeconds - next.approachDurationSeconds;
                float shift = Mathf.Max(0,previousClear-start);
                if (shift > 0)
                {
                    if (start <= elapsedSimulationSeconds) { error = "A conflicting approach has already begun."; break; }
                    next.arrivalOffsetSeconds += shift; next.departureOffsetSeconds += shift; changed.Add(next.serviceId);
                }
                previousClear = next.departureOffsetSeconds + next.departureDurationSeconds + schedule.minimumTrackClearanceSeconds;
            }
            if (string.IsNullOrEmpty(error)) error = string.Join("; ",schedule.Validate());
            if (!string.IsNullOrEmpty(error))
            {
                for (int i=0;i<schedule.services.Length;++i) { schedule.services[i].arrivalOffsetSeconds=arrivals[i]; schedule.services[i].departureOffsetSeconds=departures[i]; }
                return false;
            }
            affectedServiceIds = changed.ToArray();
            ++timelineRevision; EvaluateAll(Application.isPlaying);
            return true;
        }

        static bool ValidTime(double value) => StationRailSchedule.Finite(value) && value >= 0d && value <= MaximumElapsedSeconds;
    }
}
