using System;
using System.Collections.Generic;
using System.Linq;
using StationFlow.TrainStation.RailOperations;
using UnityEngine;
using UnityEngine.AI;

namespace StationFlow.TrainStation.DecisionLab
{
    [DefaultExecutionOrder(170)]
    [DisallowMultipleComponent]
    public sealed class StationDecisionCoachQueue : MonoBehaviour
    {
        public StationDecisionWorld world;
        public int trackNumber, coachNumber;
        public Transform doorAnchor;
        public Transform[] queueAnchors = Array.Empty<Transform>();
        [Min(.1f)] public float boardingSecondsPerPassenger = 1f;
        [Range(.1f, 1f)] public float boardingHeadDistance = .5f;
        [Min(.05f)] public float arrivalTolerance = .22f;
        [Min(.4f)] public float minimumQueueSeparation = .70f;
        sealed class PhysicalQueueProgress
        {
            public int slotIndex;
            public string predecessorId;
            public Vector3 predecessorPosition;
            public float advancesAt = -1f;
        }
        readonly DecisionCoachBoardingQueue queue = new DecisionCoachBoardingQueue();
        readonly Dictionary<string, StationDecisionPassenger> actors = new Dictionary<string, StationDecisionPassenger>(StringComparer.Ordinal);
        readonly Dictionary<string, PhysicalQueueProgress> physicalProgress = new Dictionary<string, PhysicalQueueProgress>(StringComparer.Ordinal);
        readonly Dictionary<string, StationDecisionPassenger> departingActors = new Dictionary<string, StationDecisionPassenger>(StringComparer.Ordinal);
        static readonly List<StationDecisionCoachQueue> registry = new List<StationDecisionCoachQueue>();
        public int Count => queue.Count;
        public int RepresentedWaiting => queue.Tickets.Sum(t => t.representedPassengers);
        public string HeadJourneyId => queue.HeadJourneyId;
        public string BoardingStartedJourneyId => queue.BoardingJourneyId;
        public bool BoardingStarted => queue.BoardingStarted;
        public float BoardingElapsedSeconds => (float)queue.ServedSeconds;
        public float ArrivalTolerance => Mathf.Max(.05f, arrivalTolerance);
        public float MinimumQueueSeparation => Mathf.Max(.4f, minimumQueueSeparation);

        public static StationDecisionCoachQueue For(StationDecisionWorld world, int track, int coach)
        {
            if (!world || !world.IsDaily) return null;
            if (world.coachQueues != null && world.coachQueues.Length > 0)
                return world.coachQueues.FirstOrDefault(q => q && q.world == world && q.trackNumber == track && q.coachNumber == coach);
            registry.RemoveAll(q => !q);
            return registry.FirstOrDefault(q => q.world == world && q.trackNumber == track && q.coachNumber == coach);
        }
        void OnEnable() { if (!registry.Contains(this)) registry.Add(this); }
        void OnDisable() { registry.Remove(this); }
        public bool Contains(string journeyId) => queue.Position(journeyId) >= 0;
        public int QueueOrdinal(string journeyId) => queue.Position(journeyId) + 1;
        public int QueueSlotIndex(string journeyId) => journeyId != null && physicalProgress.TryGetValue(journeyId, out var entry) ? entry.slotIndex : -1;
        public string PredecessorJourneyId(string journeyId) => journeyId != null && physicalProgress.TryGetValue(journeyId, out var entry) ? entry.predecessorId ?? "" : "";
        public Vector3 PredecessorPosition(string journeyId)
        {
            string predecessor = PredecessorJourneyId(journeyId);
            if (!string.IsNullOrEmpty(predecessor) && actors.TryGetValue(predecessor, out var actor) && actor && actor.JourneyId == predecessor)
                return actor.transform.position;
            return journeyId != null && physicalProgress.TryGetValue(journeyId, out var entry) ? entry.predecessorPosition : Vector3.zero;
        }

        public bool TryJoin(StationDecisionPassenger traveller)
        {
            if (!ValidTraveller(traveller) || !traveller.HasPlatformAccess) return false;
            string id = traveller.JourneyId;
            if (Contains(id)) return true;
            if (!TryGetFreeTailSlot(out int freeSlot, out var predecessor)) return false;
            Vector3 arrival = queueAnchors[freeSlot].position;
            bool hasPredecessor = predecessor;
            if (!DecisionPhysicalQueueArrival.CanRegister(freeSlot, freeSlot, PlanarDistance(traveller.transform.position, arrival),
                traveller.transform.position.y - arrival.y, hasPredecessor,
                hasPredecessor ? PlanarDistance(traveller.transform.position, predecessor.transform.position) : 0f,
                ArrivalTolerance, MinimumQueueSeparation)) return false;
            if (!queue.Join(new DecisionCoachQueueTicket { journeyId = id, ticketId = traveller.ArrivalPlan.ticketId,
                representedPassengers = traveller.RepresentedPassengers }, queueAnchors.Length)) return false;
            physicalProgress.Add(id, new PhysicalQueueProgress { slotIndex = freeSlot,
                predecessorId = hasPredecessor ? predecessor.JourneyId : "",
                predecessorPosition = hasPredecessor ? predecessor.transform.position : arrival });
            if (!actors.ContainsKey(id))
            {
                actors.Add(id, traveller);
                world.LogEvent("coach_queue_joined", traveller.serviceId, Detail() + "; ordinal=" + QueueOrdinal(id), id);
            }
            return true;
        }

        public Vector3 PositionFor(StationDecisionPassenger traveller)
        {
            if (queueAnchors == null || queueAnchors.Length == 0) return doorAnchor ? doorAnchor.position : transform.position;
            int index = QueueSlotIndex(traveller ? traveller.JourneyId : null);
            return index >= 0 && index < queueAnchors.Length && queueAnchors[index] ? queueAnchors[index].position : GetJoinPosition(traveller);
        }

        public Vector3 GetJoinPosition(StationDecisionPassenger traveller)
        {
            if (queueAnchors == null || queueAnchors.Length == 0) return doorAnchor ? doorAnchor.position : transform.position;
            // Keep approaching the same actual tail while its predecessor is clearing it. Registration,
            // rather than this read-only target, requires the physical personal-space gap to be open.
            if (TryGetTailSlotIndex(out int freeSlot, out _)) return queueAnchors[freeSlot].position;
            int lastSlot = queue.Count > 0 ? QueueSlotIndex(queue.Tickets[queue.Count - 1].journeyId) : -1;
            int waitingSlot = Math.Min(Math.Max(1, lastSlot + 2), queueAnchors.Length - 1);
            var waiting = queueAnchors[waitingSlot];
            if (!waiting) return doorAnchor.position;
            if (lastSlot + 1 < queueAnchors.Length) return waiting.position;
            var previous = queueAnchors.Length > 1 ? queueAnchors[queueAnchors.Length - 2] : doorAnchor;
            Vector3 direction = waiting.position - (previous ? previous.position : doorAnchor.position); direction.y = 0f;
            direction = direction.sqrMagnitude > .001f ? direction.normalized : Vector3.forward;
            Vector3 candidate = waiting.position + direction * Mathf.Max(.95f, MinimumQueueSeparation + ArrivalTolerance);
            int mask = traveller && traveller.motion && traveller.motion.agent ? traveller.motion.agent.areaMask : NavMesh.AllAreas;
            if (NavMesh.SamplePosition(candidate, out var hit, .2f, mask) && Mathf.Abs(hit.position.y - waiting.position.y) <= .15f)
                return hit.position;
            return waiting.position;
        }

        bool TryGetFreeTailSlot(out int slot, out StationDecisionPassenger predecessor)
        {
            if (!TryGetTailSlotIndex(out slot, out predecessor)) return false;
            Vector3 position = queueAnchors[slot].position;
            if (predecessor && PlanarDistance(predecessor.transform.position, position) < MinimumQueueSeparation) return false;
            return PositionClear(position, null);
        }

        bool TryGetTailSlotIndex(out int slot, out StationDecisionPassenger predecessor)
        {
            slot = 0; predecessor = null;
            if (queueAnchors == null || queueAnchors.Length == 0 || queue.Count >= queueAnchors.Length) return false;
            if (queue.Count > 0)
            {
                string tailId = queue.Tickets[queue.Count - 1].journeyId;
                if (!physicalProgress.TryGetValue(tailId, out var tailProgress) || !actors.TryGetValue(tailId, out predecessor) ||
                    !predecessor || predecessor.JourneyId != tailId || !predecessor.gameObject.activeInHierarchy) return false;
                slot = tailProgress.slotIndex + 1;
            }
            if (slot < 0 || slot >= queueAnchors.Length || !queueAnchors[slot]) return false;
            return true;
        }

        public bool CanBoard(StationDecisionPassenger traveller) => ValidTraveller(traveller) && QueueSlotIndex(traveller.JourneyId) == 0 && queue.CanBoard(traveller.JourneyId) &&
            Eligible(traveller) && Vector3.Distance(traveller.transform.position, doorAnchor.position) <= boardingHeadDistance;

        public float EstimatedWaitSeconds(StationDecisionPassenger traveller)
        {
            if (!traveller || !world) return 0f;
            // Platform access already charges the wait for the assigned train. This sequential stage charges only door service.
            return (float)queue.EstimatedWait(traveller.JourneyId, traveller.RepresentedPassengers, boardingSecondsPerPassenger);
        }

        public void Leave(StationDecisionPassenger traveller)
        {
            if (!traveller) return;
            string id = traveller.JourneyId;
            if (queue.Remove(id)) world.LogEvent("coach_queue_left", traveller.serviceId, Detail(), id);
            if (traveller.gameObject.activeInHierarchy && !traveller.Finished) departingActors[id] = traveller;
            actors.Remove(id); physicalProgress.Remove(id);
        }

        void Update()
        {
            if (!world || !world.IsDaily || world.Paused || !world.rail || !world.rail.IsReady) return;
            foreach (var pair in actors.ToArray())
                if (!pair.Value || pair.Value.Finished || !pair.Value.gameObject.activeInHierarchy || pair.Value.JourneyId != pair.Key)
                {
                    if (queue.Remove(pair.Key)) world.LogEvent("coach_queue_left", pair.Value ? pair.Value.serviceId : "", Detail() + "; stale binding removed", pair.Key);
                    actors.Remove(pair.Key); physicalProgress.Remove(pair.Key);
                }
            foreach (var pair in departingActors.ToArray())
                if (!pair.Value || !pair.Value.gameObject.activeInHierarchy || pair.Value.JourneyId != pair.Key || pair.Value.Finished ||
                    !queueAnchors.Any(a => a && PlanarDistance(pair.Value.transform.position, a.position) < MinimumQueueSeparation)) departingActors.Remove(pair.Key);
            UpdatePhysicalQueueProgress();
            actors.TryGetValue(queue.HeadJourneyId ?? "", out var head);
            bool atDoor = head && QueueSlotIndex(head.JourneyId) == 0 && doorAnchor && Vector3.Distance(head.transform.position, doorAnchor.position) <= boardingHeadDistance;
            var result = queue.Advance(world.ElapsedSeconds, atDoor, Eligible(head), boardingSecondsPerPassenger);
            if (head && result == DecisionCoachQueueProgress.Started)
                world.LogEvent("coach_boarding_started", head.serviceId, Detail() + "; represented=" + head.RepresentedPassengers, head.JourneyId);
            if (head && result == DecisionCoachQueueProgress.Ready)
                world.LogEvent("coach_boarding_ready", head.serviceId, Detail() + "; elapsed=" + BoardingElapsedSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), head.JourneyId);
        }

        void UpdatePhysicalQueueProgress()
        {
            var tickets = queue.Tickets;
            for (int logical = 0; logical < tickets.Length; ++logical)
            {
                string id = tickets[logical].journeyId;
                if (!actors.TryGetValue(id, out var actor) || !actor || !physicalProgress.TryGetValue(id, out var entry)) continue;
                StationDecisionPassenger ahead = null;
                string aheadId = logical > 0 ? tickets[logical - 1].journeyId : "";
                if (logical > 0) actors.TryGetValue(aheadId, out ahead);
                if (entry.slotIndex <= logical || entry.slotIndex <= 0)
                { entry.advancesAt = -1f; entry.predecessorId = aheadId; if (ahead) entry.predecessorPosition = ahead.transform.position; continue; }
                var current = queueAnchors[entry.slotIndex]; var candidate = queueAnchors[entry.slotIndex - 1];
                if (!current || !candidate) { entry.advancesAt = -1f; continue; }
                bool predecessorChanged = entry.predecessorId != aheadId;
                bool aheadCleared = !ahead || QueueSlotIndex(aheadId) < entry.slotIndex - 1;
                bool canAdvance = aheadCleared && PositionClear(candidate.position, actor) &&
                    DecisionPhysicalQueueArrival.CanAdvance(entry.slotIndex, logical,
                        PlanarDistance(actor.transform.position, current.position), ahead,
                        ahead ? predecessorChanged ? MinimumQueueSeparation : PlanarDistance(ahead.transform.position, entry.predecessorPosition) : 0f,
                        ahead ? PlanarDistance(ahead.transform.position, candidate.position) : 0f, .12f, MinimumQueueSeparation);
                if (!canAdvance) { entry.advancesAt = -1f; continue; }
                if (entry.advancesAt < 0f) { entry.advancesAt = world.ElapsedSeconds + .2f; continue; }
                if (world.ElapsedSeconds < entry.advancesAt) continue;
                --entry.slotIndex; entry.advancesAt = -1f;
                entry.predecessorId = aheadId;
                entry.predecessorPosition = ahead ? ahead.transform.position : candidate.position;
            }
        }

        bool PositionClear(Vector3 position, StationDecisionPassenger excluded)
        {
            foreach (var pair in actors)
                if (pair.Value && pair.Value != excluded && pair.Value.gameObject.activeInHierarchy && pair.Value.JourneyId == pair.Key &&
                    PlanarDistance(pair.Value.transform.position, position) < MinimumQueueSeparation) return false;
            foreach (var pair in departingActors)
                if (pair.Value && pair.Value != excluded && pair.Value.gameObject.activeInHierarchy && pair.Value.JourneyId == pair.Key &&
                    PlanarDistance(pair.Value.transform.position, position) < MinimumQueueSeparation) return false;
            return true;
        }
        static float PlanarDistance(Vector3 first, Vector3 second)
        { Vector3 difference = first - second; difference.y = 0f; return difference.magnitude; }

        bool ValidTraveller(StationDecisionPassenger traveller)
        {
            if (!traveller || traveller.world != world || traveller.Finished || !traveller.gameObject.activeInHierarchy || traveller.IsAlighter || traveller.ArrivalPlan == null || !doorAnchor) return false;
            var plan = traveller.ArrivalPlan;
            if (plan.trackNumber != trackNumber || plan.coachNumber != coachNumber || plan.serviceId != traveller.serviceId ||
                !world || !world.rail || !world.rail.schedule || plan.scenarioDate != world.rail.schedule.operatingDateIso || string.IsNullOrEmpty(plan.ticketId) ||
                plan.seatNumbers == null || plan.seatNumbers.Length != plan.representedPassengers || plan.seatNumbers.Distinct().Count() != plan.seatNumbers.Length) return false;
            var service = world && world.rail && world.rail.schedule ? world.rail.schedule.services.FirstOrDefault(s => s.serviceId == plan.serviceId) : null;
            return service != null && service.coachSeatCapacities != null && coachNumber >= 1 && coachNumber <= service.coachSeatCapacities.Length &&
                plan.seatNumbers.All(s => s >= 1 && s <= service.coachSeatCapacities[coachNumber - 1]);
        }
        bool Eligible(StationDecisionPassenger traveller)
        {
            if (!ValidTraveller(traveller) || !traveller.HasPlatformAccess || !world || world.Paused) return false;
            var trip = world.ObserveTrip(traveller.serviceId);
            return trip.status == DecisionTrainStatus.Boarding && trip.train && trip.train.Service != null && trip.train.Service.serviceId == traveller.serviceId &&
                trip.train.trackNumber == trackNumber && trip.train.State == StationTrainState.Boarding && trip.train.DoorOpenFraction > .95f &&
                world.ElapsedSeconds < trip.cutoffSeconds;
        }
        string Detail() => "track=" + trackNumber + "; coach=" + coachNumber;
    }
}
