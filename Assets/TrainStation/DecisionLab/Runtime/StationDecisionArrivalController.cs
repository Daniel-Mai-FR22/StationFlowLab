using System;
using System.Collections.Generic;
using System.Linq;
using StationFlow.TrainStation.RailOperations;
using UnityEngine;
using UnityEngine.AI;

namespace StationFlow.TrainStation.DecisionLab
{
    [Serializable]
    public sealed class StationDecisionSpawnPortal
    {
        public string entranceId;
        public int trackNumber;
        public Transform[] spawnPoints = Array.Empty<Transform>();
        [Min(1)] public int spawnPointsPerCoach = 6;
        [NonSerialized] internal float nextRelease;
        [NonSerialized] internal int nextSlot;
    }

    [Serializable]
    public sealed class DecisionJourneyRecord
    {
        public DecisionArrivalPlan plan;
        public DecisionJourneyStatus status;
        public float enteredSeconds = -1f, finishedSeconds = -1f;
        public string terminalReason = "";
        public int decisions, completedActivities;
        public Vector3 lastPosition;
        [NonSerialized] public StationDecisionPassenger livePassenger;
    }

    /// <summary>Daily logical journeys own identity; a bounded set of repaired actors only presents entered journeys.</summary>
    [DefaultExecutionOrder(160)]
    public sealed class StationDecisionArrivalController : MonoBehaviour
    {
        public StationDecisionWorld world;
        public StationDecisionDayScenario scenario;
        public StationDecisionPassenger[] actorPool = Array.Empty<StationDecisionPassenger>();
        public StationDecisionSpawnPortal[] portals = Array.Empty<StationDecisionSpawnPortal>();
        [Tooltip("Relative boarding flow by ground-level portal ID. Zero disables inbound assignment. Empty preserves the existing uniform entrance draw.")]
        public DecisionEntranceWeight[] entranceWeights = Array.Empty<DecisionEntranceWeight>();
        public IReadOnlyList<DecisionArrivalPlan> GeneratedPlan => generated;
        public IReadOnlyList<DecisionJourneyRecord> Journeys => records;
        public IEnumerable<DecisionJourneyRecord> ActiveJourneys => records.Where(r => r.status == DecisionJourneyStatus.Active);
        public int ActiveCount => records.Count(r => r.status == DecisionJourneyStatus.Active);
        public int ScheduledCount => records.Count(r => r.status == DecisionJourneyStatus.Scheduled || r.status == DecisionJourneyStatus.WaitingForEntry);
        public int EnteredCount => records.Count(r => r.enteredSeconds >= 0f);
        public int BoardingCount => records.Count(r => r.status == DecisionJourneyStatus.Boarded);
        public int BoardedCount => BoardingCount;
        public int ExitedCount => records.Count(r => r.status == DecisionJourneyStatus.Exited);
        public int CompletedCount => records.Count(r => Terminal(r.status));
        public int UnservedCount => records.Count(r => r.status == DecisionJourneyStatus.Unserved || r.status == DecisionJourneyStatus.Failed);
        public int RepresentedBoardedCount => records.Where(r => r.status == DecisionJourneyStatus.Boarded).Sum(r => r.plan.representedPassengers);
        public int RepresentedActiveCount => ActiveJourneys.Sum(r => r.plan.representedPassengers);
        public bool AllJourneysTerminal => IsReady && records.All(r => Terminal(r.status));
        public bool IsReady { get; private set; }
        public string ConfigurationIssue { get; private set; } = "";
        public event Action<DecisionJourneyRecord> JourneyEntered, JourneyCompleted;
        readonly List<DecisionArrivalPlan> generated = new List<DecisionArrivalPlan>();
        readonly List<DecisionJourneyRecord> records = new List<DecisionJourneyRecord>();
        readonly Dictionary<string, DecisionJourneyRecord> byId = new Dictionary<string, DecisionJourneyRecord>(StringComparer.Ordinal);
        readonly Dictionary<string, StationDecisionTravelGroup> groups = new Dictionary<string, StationDecisionTravelGroup>(StringComparer.Ordinal);
        readonly Dictionary<string, DecisionJourneyRecord[]> groupRecords = new Dictionary<string, DecisionJourneyRecord[]>(StringComparer.Ordinal);
        readonly DecisionArrivalAdmission admission = new DecisionArrivalAdmission();
        readonly List<DecisionJourneyRecord> pending = new List<DecisionJourneyRecord>();
        int nextDue;

        void Awake()
        {
            try
            {
                if (!world || !scenario || !world.rail || !world.rail.schedule) throw new InvalidOperationException("Daily world/scenario/rail references are required.");
                if (actorPool == null || actorPool.Length == 0 || actorPool.Any(p => !p || !p.motion || !p.passenger) || actorPool.Distinct().Count() != actorPool.Length)
                    throw new InvalidOperationException("Daily actor pool must contain unique saved inactive actors.");
                if (actorPool.Any(p => p.gameObject.activeInHierarchy)) throw new InvalidOperationException("Daily pool actors must be saved inactive, before Play starts.");
                if (portals == null || portals.Any(p => p == null || string.IsNullOrWhiteSpace(p.entranceId) || p.spawnPoints == null ||
                    p.spawnPoints.Length == 0 || p.spawnPoints.Any(t => !t)) || portals.Select(p => p.entranceId).Distinct().Count() != portals.Length)
                    throw new InvalidOperationException("Arrival portals need unique IDs and saved spawn points.");
                world.participants = actorPool;
                world.seed = scenario.seed;
                var services = world.rail.schedule.services.Select(s => new DecisionArrivalService { serviceId = s.serviceId, trainNumber = s.trainNumber,
                    trackNumber = s.trackNumber, arrivalSeconds = s.arrivalOffsetSeconds, departureSeconds = s.departureOffsetSeconds,
                    doorSeconds = s.doorDurationSeconds, expectedBoarding = s.expectedBoarding, expectedAlighting = s.expectedAlighting,
                    coachSeatCapacities = s.coachSeatCapacities, throughPassengers = s.throughPassengers }).ToArray();
                var settings = scenario.PlannerSettings();
                settings.entranceWeights = entranceWeights;
                if (!string.IsNullOrEmpty(world.rail.schedule.operatingDateIso)) settings.scenarioDate = world.rail.schedule.operatingDateIso;
                generated.AddRange(DecisionArrivalPlanner.Generate(services, portals.Where(p => p.trackNumber == 0).Select(p => p.entranceId).ToArray(), settings));
                foreach (var plan in generated)
                {
                    if (!portals.Any(p => plan.purpose == DecisionJourneyPurpose.Alighting ? p.trackNumber == plan.trackNumber : p.entranceId == plan.entranceId && p.trackNumber == 0))
                        throw new InvalidOperationException("No matching arrival portal for " + plan.journeyId);
                    var record = new DecisionJourneyRecord { plan = plan, status = DecisionJourneyStatus.Scheduled };
                    records.Add(record); byId.Add(plan.journeyId, record);
                }
                foreach (var group in records.Where(r => !string.IsNullOrEmpty(r.plan.groupId)).GroupBy(r => r.plan.groupId))
                {
                    var members = group.ToArray(); groupRecords.Add(group.Key, members);
                    groups.Add(group.Key, new StationDecisionTravelGroup(this, group.Key, members));
                }
                IsReady = true;
            }
            catch (Exception e) { ConfigurationIssue = e.Message; Debug.LogError("Daily arrivals: " + e.Message, this); }
        }

        void Start() { if (IsReady && !world.Paused) world.SetSimulationSpeed(scenario.defaultTimeScale); }
        void Update()
        {
            if (!IsReady || !world || world.Paused || !world.rail.IsReady) return;
            float now = world.ElapsedSeconds;
            while (nextDue < records.Count && records[nextDue].plan.plannedEntrySeconds <= now)
            { var record = records[nextDue++]; record.status = DecisionJourneyStatus.WaitingForEntry; pending.Add(record); }
            int spawned = 0;
            // Reserved companions precede older unrelated arrivals, but only once their own planned time is due.
            var dueOrder = pending.OrderByDescending(r => admission.HasPriority(r.plan, PortalFor(r)?.entranceId, now)).ToArray();
            foreach (var record in dueOrder)
            {
                var observation = world.ObserveTrip(record.plan.serviceId);
                bool alighting = record.plan.purpose == DecisionJourneyPurpose.Alighting;
                var liveService = world.rail.schedule.services.FirstOrDefault(s => s != null && s.serviceId == record.plan.serviceId);
                bool expired = liveService == null || (alighting ? observation.status == DecisionTrainStatus.Cancelled ||
                    now >= observation.departureSeconds - liveService.doorDurationSeconds : now > observation.departureSeconds + 120f);
                if (expired)
                { EndUnentered(record, alighting ? "No open train door before alighting release expired." : "Entrance/pool could not admit the planned arrival before its late-entry limit."); pending.Remove(record); continue; }
                if (spawned >= 2) continue;
                if (alighting && (!observation.train || observation.train.Service == null || observation.train.Service.serviceId != record.plan.serviceId ||
                    observation.train.State != StationTrainState.Boarding || observation.train.DoorOpenFraction <= .95f)) continue;
                var portal = PortalFor(record);
                if (portal == null || now < portal.nextRelease) continue;
                var freeActors = actorPool.Take(Mathf.Clamp(scenario.activeActorLimit, 1, actorPool.Length)).Where(p => !p.gameObject.activeInHierarchy).ToArray();
                var remainingParty = RemainingParty(record);
                if (!admission.CanAdmit(record.plan, portal.entranceId, now, freeActors.Length, remainingParty.Length)) continue;
                var actor = freeActors[0];
                if (!TrySpawnPosition(portal, record.plan, out var position, out var rotation, out int clearSlots, out int nextSlot) ||
                    !admission.CanAdmit(record.plan, portal.entranceId, now, freeActors.Length, remainingParty.Length, clearSlots)) continue;
                actor.PrepareJourney(record.plan, this);
                actor.transform.SetPositionAndRotation(position, rotation);
                if (actor.motion && actor.motion.agent) actor.motion.agent.enabled = false;
                record.livePassenger = actor;
                admission.Admitted(record.plan, portal.entranceId, now, remainingParty.Select(r => r.plan.journeyId), scenario.maximumRegroupSeconds);
                portal.nextSlot = nextSlot;
                portal.nextRelease = now + Mathf.Max(.2f, scenario.minimumEntryIntervalSeconds);
                actor.gameObject.SetActive(true);
                ++spawned; pending.Remove(record);
            }
        }

        StationDecisionSpawnPortal PortalFor(DecisionJourneyRecord record) => portals.FirstOrDefault(p => record.plan.purpose == DecisionJourneyPurpose.Alighting ?
            p.trackNumber == record.plan.trackNumber : p.trackNumber == 0 && p.entranceId == record.plan.entranceId);
        DecisionJourneyRecord[] RemainingParty(DecisionJourneyRecord record) => !string.IsNullOrEmpty(record.plan.groupId) &&
            groupRecords.TryGetValue(record.plan.groupId, out var party) ? party.Where(r => !r.livePassenger &&
                (r.status == DecisionJourneyStatus.Scheduled || r.status == DecisionJourneyStatus.WaitingForEntry)).ToArray() : new[] { record };

        bool TrySpawnPosition(StationDecisionSpawnPortal portal, DecisionArrivalPlan plan, out Vector3 position, out Quaternion rotation, out int clearSlots, out int nextSlot)
        {
            position = default; rotation = Quaternion.identity; clearSlots = 0; nextSlot = portal.nextSlot;
            var clearPositions = new List<Vector3>();
            for (int offset = 0; offset < portal.spawnPoints.Length; ++offset)
            {
                int index = (portal.nextSlot + offset) % portal.spawnPoints.Length; var point = portal.spawnPoints[index];
                if (portal.trackNumber > 0 && plan.coachNumber > 0 && index / Mathf.Max(1, portal.spawnPointsPerCoach) + 1 != plan.coachNumber) continue;
                if (!NavMesh.SamplePosition(point.position, out var hit, .2f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - point.position.y) > .15f) continue;
                bool occupied = actorPool.Any(p => p && p.gameObject.activeInHierarchy && (p.transform.position - hit.position).sqrMagnitude < .9f * .9f);
                if (occupied) continue;
                foreach (var collider in Physics.OverlapCapsule(hit.position + Vector3.up * .4f, hit.position + Vector3.up * 1.5f, .27f, ~0, QueryTriggerInteraction.Ignore))
                    if (collider && collider.enabled && collider.bounds.max.y >= hit.position.y + .25f) { occupied = true; break; }
                if (occupied) continue;
                // Count mutually usable positions, not multiple markers resolving to the same occupied volume.
                if (clearPositions.Any(p => (p - hit.position).sqrMagnitude < .9f * .9f)) continue;
                clearPositions.Add(hit.position);
                if (clearSlots++ == 0) { position = hit.position; rotation = point.rotation; nextSlot = (index + 1) % portal.spawnPoints.Length; }
            }
            return clearSlots > 0;
        }

        public DecisionJourneyRecord FindJourney(string journeyId) => journeyId != null && byId.TryGetValue(journeyId, out var value) ? value : null;
        public StationDecisionTravelGroup GetGroup(string groupId) => !string.IsNullOrEmpty(groupId) && groups.TryGetValue(groupId, out var value) ? value : null;
        public void MarkEntered(StationDecisionPassenger passenger)
        {
            var record = FindJourney(passenger.JourneyId);
            if (record == null || record.livePassenger != passenger || record.status == DecisionJourneyStatus.Active) return;
            record.status = DecisionJourneyStatus.Active; record.enteredSeconds = world.ElapsedSeconds; record.lastPosition = passenger.transform.position;
            world.LogEvent("journey_entered", passenger.serviceId, "Virtual entry; portal=" + record.plan.entranceId + "; planned=" + record.plan.plannedEntrySeconds +
                "; weight=" + record.plan.representedPassengers, passenger.JourneyId);
            JourneyEntered?.Invoke(record);
        }
        public void CompleteJourney(StationDecisionPassenger passenger, bool boarded, bool exited, string reason)
        {
            var record = FindJourney(passenger.JourneyId); if (record == null || Terminal(record.status)) return;
            record.status = boarded ? DecisionJourneyStatus.Boarded : exited ? DecisionJourneyStatus.Exited : DecisionJourneyStatus.Failed;
            record.finishedSeconds = world.ElapsedSeconds; record.terminalReason = reason; record.lastPosition = passenger.transform.position;
            record.decisions = passenger.DecisionCount; record.completedActivities = passenger.CompletedActivities;
            world.LogEvent("journey_finished", passenger.serviceId, record.status + "; " + reason, passenger.JourneyId);
            GetGroup(record.plan.groupId)?.MemberFinished(record.plan.journeyId);
            JourneyCompleted?.Invoke(record); record.livePassenger = null;
        }
        void EndUnentered(DecisionJourneyRecord record, string reason)
        {
            admission.RemoveJourney(record.plan.journeyId);
            record.status = DecisionJourneyStatus.Unserved; record.finishedSeconds = world.ElapsedSeconds; record.terminalReason = reason;
            world.LogEvent("journey_unserved", record.plan.serviceId, reason, record.plan.journeyId); JourneyCompleted?.Invoke(record);
        }
        static bool Terminal(DecisionJourneyStatus state) => state == DecisionJourneyStatus.Boarded || state == DecisionJourneyStatus.Exited ||
            state == DecisionJourneyStatus.Unserved || state == DecisionJourneyStatus.Failed;
    }
}
