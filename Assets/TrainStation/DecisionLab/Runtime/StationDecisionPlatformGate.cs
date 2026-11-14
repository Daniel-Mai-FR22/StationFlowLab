using System;
using System.Collections.Generic;
using System.Linq;
using StationFlow.TrainStation.RailOperations;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Scene-authored ticket scanner. Only the scanned actor receives its one-way platform link permission.</summary>
    [DefaultExecutionOrder(170)]
    public sealed class StationDecisionPlatformGate : MonoBehaviour
    {
        public const int EgressArea = 4;
        public const int IngressAreasMask = (1 << 3) | (1 << 5) | (1 << 6) | (1 << 7);
        public StationDecisionWorld world;
        public int trackNumber, ingressArea = 3;
        [Tooltip("One ticket scan per visible actor, like the station service queues. Cohort weights still conserve seats and train capacity.")]
        [Min(.1f)] public float scanSecondsPerActor = 1f;
        [Min(.1f)] public float expectedCrossingSeconds = 3.5f;
        [Min(.05f)] public float arrivalTolerance = .22f;
        [Min(.4f)] public float minimumQueueSeparation = .70f;
        [Min(.01f)] public float queuePositionTolerance = .12f;
        [Min(0f)] public float queueReactionSeconds = .2f;
        public Transform[] queueAnchors = Array.Empty<Transform>();
        public Transform[] holdingAnchors = Array.Empty<Transform>();
        public Transform hallCrossingPoint, platformCrossingPoint;
        public NavMeshLink ingressLink, egressLink;
        public Transform ingressLeaf;
        public Collider ingressLeafCollider;
        public Renderer indicator;
        readonly DecisionPlatformAccessQueue queue = new DecisionPlatformAccessQueue();
        readonly Dictionary<string, StationDecisionPassenger> actors = new Dictionary<string, StationDecisionPassenger>(StringComparer.Ordinal);
        sealed class QueueProgress
        {
            public int slotIndex;
            public StationDecisionPassenger predecessor;
            public string predecessorJourneyId;
            public Vector3 predecessorPosition;
            public float reactsAt = -1f;
        }
        sealed class DepartingOccupant
        {
            public StationDecisionPassenger actor;
            public string journeyId;
        }
        readonly Dictionary<string, QueueProgress> progress = new Dictionary<string, QueueProgress>(StringComparer.Ordinal);
        readonly List<DepartingOccupant> departures = new List<DepartingOccupant>();
        readonly Dictionary<string, StationDecisionPassenger> holdingActors = new Dictionary<string, StationDecisionPassenger>(StringComparer.Ordinal);
        DecisionHoldingSlotAllocator holdingSlots;
        Quaternion closedLeafRotation;

        public int CurrentQueuedCount => queue.Count;
        public int QueuedRepresentedPassengers => queue.Tickets.Sum(t => t.representedPassengers);
        public string ScanningJourneyId => queue.ScanningJourney;
        public string CrossingJourneyId => queue.CrossingJourney;
        public string OpenServiceId { get; private set; } = "";
        public string LastRejectionReason { get; private set; } = "";
        public int ScanCount { get; private set; }
        public int PassedCount { get; private set; }
        public int HoldingLeaseCount => holdingSlots != null ? holdingSlots.Count : 0;
        public int HoldingSlotIndex(string journeyId) => holdingSlots != null ? holdingSlots.SlotFor(journeyId) : -1;
        public bool IsOpen => !string.IsNullOrEmpty(OpenServiceId);
        public float ArrivalTolerance => arrivalTolerance;
        public float MinimumQueueSeparation => minimumQueueSeparation;
        public int QueueSlotIndex(string journeyId) => journeyId != null && progress.TryGetValue(journeyId, out var entry) ? entry.slotIndex : -1;
        public string PredecessorJourneyId(string journeyId) => journeyId != null && progress.TryGetValue(journeyId, out var entry) ? entry.predecessorJourneyId ?? "" : "";
        public Vector3 PredecessorPosition(string journeyId) => journeyId != null && progress.TryGetValue(journeyId, out var entry) ? entry.predecessorPosition : Vector3.zero;

        public static StationDecisionPlatformGate For(StationDecisionWorld world, int track)
        {
            if (!world || !world.IsDaily) return null;
            if (world.platformGates != null && world.platformGates.Length > 0)
                return world.platformGates.FirstOrDefault(g => g && g.trackNumber == track);
            return FindObjectsByType<StationDecisionPlatformGate>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault(g => g.world == world && g.trackNumber == track);
        }
        public static int DefaultAreaMask(int mask) => (mask & ~IngressAreasMask) | (1 << EgressArea);
        public static int PlanningAreaMask(int mask) => mask | IngressAreasMask | (1 << EgressArea);
        public int PlanningMask(int mask) => DefaultAreaMask(mask) | (1 << ingressArea);
        public int QueueOrdinal(string journeyId) => queue.Position(journeyId) + 1;
        public bool Contains(string journeyId) => queue.Position(journeyId) >= 0 || queue.CanCross(journeyId);
        public bool HasPassed(string journeyId)
        {
            bool passed = queue.HasPassed(journeyId);
            if (passed) ReleaseHolding(journeyId, "Already passed the platform control");
            return passed;
        }
        public bool CanProceed(StationDecisionPassenger traveller) => traveller && queue.CanCross(traveller.JourneyId);
        public Vector3 CrossingPosition(StationDecisionPassenger traveller) => platformCrossingPoint ? platformCrossingPoint.position + Vector3.forward * .55f : transform.position + Vector3.forward * 1.75f;
        public DecisionPlatformAccessStatus StatusFor(StationDecisionPassenger traveller) => AccessStatus(traveller);

        void Awake()
        {
            if (ingressLeaf) closedLeafRotation = ingressLeaf.localRotation;
            if (!world || !world.IsDaily) { enabled = false; return; }
            foreach (var actor in world.participants)
                if (actor && actor.motion && actor.motion.agent) actor.motion.agent.areaMask = DefaultAreaMask(actor.motion.agent.areaMask);
            SetLeaf(false);
        }

        public DecisionPlatformAccessStatus AccessStatus(StationDecisionPassenger traveller)
        {
            if (!traveller || traveller.IsAlighter || traveller.ArrivalPlan == null) return DecisionPlatformAccessStatus.InvalidTicket;
            return DecisionPlatformAccess.Evaluate(Ticket(traveller), View(traveller.serviceId));
        }

        public bool TryJoin(StationDecisionPassenger traveller, out string reason)
        {
            reason = "";
            if (!traveller || traveller.IsAlighter || traveller.ArrivalPlan == null)
            { reason = LastRejectionReason = "InvalidTicket"; return false; }
            if (queue.HasPassed(traveller.JourneyId)) { ReleaseHolding(traveller.JourneyId, "Already passed the platform control"); return true; }
            if (queue.CanCross(traveller.JourneyId)) { ReleaseHolding(traveller.JourneyId, "Already granted platform traversal"); return true; }
            var access = AccessStatus(traveller);
            if (access != DecisionPlatformAccessStatus.Open)
            { reason = LastRejectionReason = access.ToString(); return false; }
            if (queue.Position(traveller.JourneyId) >= 0) { ReleaseHolding(traveller.JourneyId, "Already physically in the FIFO"); return true; }
            if (!TryGetFreeTailIndex(out int freeIndex))
            { reason = LastRejectionReason = "Queue tail occupied"; return false; }
            var arrival = queueAnchors[freeIndex].position;
            var tickets = queue.Tickets;
            var predecessor = tickets.Length > 0 && actors.TryGetValue(tickets[tickets.Length - 1].journeyId, out var last) ? last : null;
            if (!DecisionPhysicalQueueArrival.CanRegister(freeIndex, freeIndex, PlanarDistance(traveller.transform.position, arrival),
                traveller.transform.position.y - arrival.y, predecessor,
                predecessor ? PlanarDistance(traveller.transform.position, predecessor.transform.position) : 0, arrivalTolerance, minimumQueueSeparation))
            { reason = LastRejectionReason = "Approaching queue tail"; return false; }
            if (queue.Join(Ticket(traveller), View(traveller.serviceId), queueAnchors.Length, out var status))
            {
                ReleaseHolding(traveller.JourneyId, "Physically joined the FIFO");
                if (!actors.ContainsKey(traveller.JourneyId))
                {
                    actors.Add(traveller.JourneyId, traveller);
                    progress.Add(traveller.JourneyId, new QueueProgress { slotIndex = freeIndex, predecessor = predecessor,
                        predecessorJourneyId = predecessor ? predecessor.JourneyId : "",
                        predecessorPosition = predecessor ? predecessor.transform.position : arrival });
                    world.LogEvent("platform_queue_joined", traveller.serviceId, "track=" + trackNumber + "; ordinal=" + QueueOrdinal(traveller.JourneyId), traveller.JourneyId);
                }
                return true;
            }
            reason = LastRejectionReason = status.ToString(); return false;
        }

        public Vector3 QueuePosition(StationDecisionPassenger traveller)
        {
            if (!traveller) return transform.position;
            int index = QueueSlotIndex(traveller.JourneyId);
            return index >= 0 && index < queueAnchors.Length && queueAnchors[index] ? queueAnchors[index].position : GetHoldingPosition(traveller);
        }

        public Vector3 GetJoinPosition(StationDecisionPassenger traveller)
        {
            if (traveller && progress.TryGetValue(traveller.JourneyId, out var entry) && entry.slotIndex < queueAnchors.Length)
                return queueAnchors[entry.slotIndex].position;
            if (AccessStatus(traveller) == DecisionPlatformAccessStatus.Open && TryGetTailIndex(out int index)) return queueAnchors[index].position;
            return GetHoldingPosition(traveller);
        }
        public Vector3 GetApproachPosition(StationDecisionPassenger traveller) => Contains(traveller ? traveller.JourneyId : null) ? QueuePosition(traveller) : GetJoinPosition(traveller);

        bool TryGetTailIndex(out int index)
        {
            index = 0;
            var tickets = queue.Tickets;
            if (tickets.Length >= queueAnchors.Length || queueAnchors.Length == 0) return false;
            if (tickets.Length > 0)
            {
                string tail = tickets[tickets.Length - 1].journeyId;
                if (!progress.TryGetValue(tail, out var entry)) return false;
                index = entry.slotIndex + 1;
            }
            if (index >= queueAnchors.Length || !queueAnchors[index]) return false;
            return true;
        }

        bool TryGetFreeTailIndex(out int index)
        {
            if (!TryGetTailIndex(out index)) return false;
            var candidate = queueAnchors[index].position;
            foreach (var pair in actors)
                if (IsSameActiveJourney(pair.Value, pair.Key) && PlanarDistance(pair.Value.transform.position, candidate) < minimumQueueSeparation) return false;
            foreach (var departed in departures)
                if (IsSameActiveJourney(departed.actor, departed.journeyId) && PlanarDistance(departed.actor.transform.position, candidate) < minimumQueueSeparation) return false;
            return true;
        }

        static bool IsSameActiveJourney(StationDecisionPassenger actor, string journeyId) => actor && actor.gameObject.activeInHierarchy && actor.JourneyId == journeyId;
        static float PlanarDistance(Vector3 a, Vector3 b) { a.y = b.y; return Vector3.Distance(a, b); }

        void RememberDeparture(StationDecisionPassenger actor, string journeyId)
        {
            if (IsSameActiveJourney(actor, journeyId) && !departures.Exists(d => d.actor == actor && d.journeyId == journeyId))
                departures.Add(new DepartingOccupant { actor = actor, journeyId = journeyId });
            progress.Remove(journeyId);
        }

        void UpdateQueueProgress()
        {
            departures.RemoveAll(d => !IsSameActiveJourney(d.actor, d.journeyId) ||
                !queueAnchors.Any(anchor => anchor && PlanarDistance(d.actor.transform.position, anchor.position) < minimumQueueSeparation));
            var tickets = queue.Tickets;
            for (int i = 0; i < tickets.Length; ++i)
            {
                string id = tickets[i].journeyId;
                if (!actors.TryGetValue(id, out var actor) || !actor || !progress.TryGetValue(id, out var entry)) continue;
                var ahead = i > 0 && actors.TryGetValue(tickets[i - 1].journeyId, out var previous) ? previous : null;
                if (entry.slotIndex <= i)
                {
                    entry.reactsAt = -1f; entry.predecessor = ahead; entry.predecessorJourneyId = ahead ? ahead.JourneyId : "";
                    entry.predecessorPosition = ahead ? ahead.transform.position : queueAnchors[entry.slotIndex].position;
                    continue;
                }
                int candidateIndex = entry.slotIndex - 1;
                var candidate = queueAnchors[candidateIndex];
                if (!candidate || !queueAnchors[entry.slotIndex]) continue;
                bool samePredecessor = ahead && entry.predecessor == ahead && entry.predecessorJourneyId == ahead.JourneyId;
                double observedProgress = samePredecessor ? PlanarDistance(ahead.transform.position, entry.predecessorPosition) : minimumQueueSeparation;
                bool canAdvance = DecisionPhysicalQueueArrival.CanAdvance(entry.slotIndex, i,
                    PlanarDistance(actor.transform.position, queueAnchors[entry.slotIndex].position), ahead, observedProgress,
                    ahead ? PlanarDistance(ahead.transform.position, candidate.position) : 0, queuePositionTolerance, minimumQueueSeparation);
                if (ahead && progress.TryGetValue(ahead.JourneyId, out var aheadEntry)) canAdvance &= aheadEntry.slotIndex < candidateIndex;
                if (IsSameActiveJourney(entry.predecessor, entry.predecessorJourneyId) && !samePredecessor)
                    canAdvance &= PlanarDistance(entry.predecessor.transform.position, entry.predecessorPosition) >= minimumQueueSeparation &&
                        PlanarDistance(entry.predecessor.transform.position, candidate.position) >= minimumQueueSeparation;
                foreach (var departed in departures)
                    if (IsSameActiveJourney(departed.actor, departed.journeyId))
                        canAdvance &= PlanarDistance(departed.actor.transform.position, candidate.position) >= minimumQueueSeparation;
                if (!canAdvance) { entry.reactsAt = -1f; continue; }
                if (entry.reactsAt < 0f) { entry.reactsAt = world.ElapsedSeconds + queueReactionSeconds; continue; }
                if (world.ElapsedSeconds < entry.reactsAt) continue;
                --entry.slotIndex; entry.reactsAt = -1f;
                entry.predecessor = ahead; entry.predecessorJourneyId = ahead ? ahead.JourneyId : "";
                entry.predecessorPosition = ahead ? ahead.transform.position : candidate.position;
            }
        }

        Vector3 GetHoldingPosition(StationDecisionPassenger traveller)
        {
            if (!traveller) return hallCrossingPoint ? hallCrossingPoint.position : transform.position;
            if (holdingAnchors == null || holdingAnchors.Length == 0) return traveller.transform.position;
            EnsureHoldingAllocator(); CleanupHolding();
            uint hash = 2166136261u;
            foreach (char value in traveller.JourneyId ?? "") hash = unchecked((hash ^ value) * 16777619u);
            int preferred = (int)(hash % (uint)holdingAnchors.Length);
            if (!IsHoldingJourney(traveller, traveller.JourneyId))
                return holdingSlots.TryPeek(preferred, index => HoldingMarkerClear(index, traveller), out int preview) ?
                    holdingAnchors[preview].position : traveller.transform.position;
            int owner = HoldingOwner(traveller);
            if (owner == 0) return traveller.transform.position;
            bool hadLease = holdingSlots.SlotFor(traveller.JourneyId) >= 0;
            if (!holdingSlots.TryAcquire(traveller.JourneyId, owner, preferred,
                index => HoldingMarkerClear(index, traveller), out int slot)) return traveller.transform.position;
            holdingActors[traveller.JourneyId] = traveller;
            if (!hadLease) world.LogEvent("platform_holding_claimed", traveller.serviceId,
                "track=" + trackNumber + "; slot=" + slot + "; owner=" + owner, traveller.JourneyId);
            return holdingAnchors[slot].position;
        }

        int HoldingOwner(StationDecisionPassenger actor)
        {
            if (!actor || !world || world.participants == null) return 0;
            int index = Array.IndexOf(world.participants, actor);
            return index >= 0 ? index + 1 : 0;
        }

        void EnsureHoldingAllocator()
        {
            if (holdingSlots != null && holdingSlots.Capacity == holdingAnchors.Length) return;
            holdingSlots = new DecisionHoldingSlotAllocator(holdingAnchors.Length); holdingActors.Clear();
        }
        bool IsHoldingJourney(StationDecisionPassenger actor, string journeyId) => IsSameActiveJourney(actor, journeyId) && !actor.Finished &&
            actor.CurrentActivity == DecisionActivity.Platform && actor.ArrivalPlan != null && actor.ArrivalPlan.trackNumber == trackNumber &&
            !queue.HasPassed(journeyId) && !Contains(journeyId);
        bool HoldingMarkerClear(int index, StationDecisionPassenger requesting)
        {
            if (index < 0 || index >= holdingAnchors.Length || !holdingAnchors[index]) return false;
            var point = holdingAnchors[index].position;
            // A released lease is not physically reusable until its departing body also clears the marker.
            return !world || world.participants == null || !world.participants.Any(actor => actor && actor != requesting &&
                actor.gameObject.activeInHierarchy && PlanarDistance(actor.transform.position, point) < minimumQueueSeparation);
        }
        void CleanupHolding()
        {
            foreach (var pair in holdingActors.ToArray())
                if (!IsHoldingJourney(pair.Value, pair.Key)) ReleaseHolding(pair.Key, "Journey inactive, recycled, finished or no longer waiting for this gate");
        }
        void ReleaseHolding(string journeyId, string reason)
        {
            if (holdingSlots == null || journeyId == null) return;
            int slot = holdingSlots.SlotFor(journeyId);
            holdingActors.TryGetValue(journeyId, out var actor);
            if (holdingSlots.Release(journeyId) && world)
                world.LogEvent("platform_holding_released", actor ? actor.serviceId : "",
                    "track=" + trackNumber + "; slot=" + slot + "; owner=" + HoldingOwner(actor) + "; " + reason, journeyId);
            holdingActors.Remove(journeyId);
        }

        void OnDisable() { holdingSlots?.Clear(); holdingActors.Clear(); }

        public float EstimatedWaitSeconds(StationDecisionPassenger traveller, double secondsUntilArrival = 0d)
        {
            if (!traveller || !world || queue.HasPassed(traveller.JourneyId)) return 0f;
            var view = View(traveller.serviceId);
            var service = world.rail.schedule.services.FirstOrDefault(s => s.serviceId == traveller.serviceId);
            double opensAt = Math.Max(view.departure - view.leadSeconds, service != null ? service.arrivalOffsetSeconds : view.now);
            return (float)DecisionAccessForecast.WaitingSeconds(view.now, opensAt, secondsUntilArrival,
                queue.EstimatedWait(traveller.JourneyId, scanSecondsPerActor, expectedCrossingSeconds));
        }

        public void Leave(StationDecisionPassenger traveller)
        {
            if (!traveller) return;
            ReleaseHolding(traveller.JourneyId, "Left the platform access activity");
            bool crossing = queue.CanCross(traveller.JourneyId);
            // A granted native traversal must finish on its floor. Cutoff still prevents subsequent boarding.
            if (crossing && traveller.motion && traveller.motion.agent && traveller.motion.agent.isOnOffMeshLink) return;
            bool removed = queue.Remove(traveller.JourneyId);
            if (removed) world.LogEvent("platform_queue_left", traveller.serviceId, "track=" + trackNumber, traveller.JourneyId);
            if (removed || progress.ContainsKey(traveller.JourneyId)) RememberDeparture(traveller, traveller.JourneyId);
            actors.Remove(traveller.JourneyId);
            if (traveller.motion && traveller.motion.agent) traveller.motion.agent.areaMask = DefaultAreaMask(traveller.motion.agent.areaMask);
            if (crossing) SetLeaf(false);
        }

        void Update()
        {
            if (!world || world.Paused || !world.rail || !world.rail.IsReady) return;
            CleanupHolding();
            OpenServiceId = "";
            foreach (var service in world.rail.schedule.services)
            {
                if (service.trackNumber != trackNumber) continue;
                var probe = new DecisionPlatformTicket { journeyId = "probe", ticketId = "probe", operatingDate = world.rail.schedule.operatingDateIso,
                    serviceId = service.serviceId, trackNumber = trackNumber };
                if (DecisionPlatformAccess.Evaluate(probe, View(service.serviceId)) == DecisionPlatformAccessStatus.Open)
                { OpenServiceId = service.serviceId; break; }
            }
            foreach (var ticket in queue.Tickets)
            {
                bool valid = actors.TryGetValue(ticket.journeyId, out var actor) && actor && actor.gameObject.activeInHierarchy &&
                    !actor.Finished && actor.JourneyId == ticket.journeyId;
                if (!valid || DecisionPlatformAccess.Evaluate(ticket, View(ticket.serviceId)) != DecisionPlatformAccessStatus.Open)
                {
                    queue.Remove(ticket.journeyId); actors.Remove(ticket.journeyId);
                    RememberDeparture(actor, ticket.journeyId);
                    if (valid) world.LogEvent("platform_queue_expired", ticket.serviceId, "track=" + trackNumber + "; " + AccessStatus(actor), ticket.journeyId);
                }
            }
            UpdateQueueProgress();
            string crossingId = queue.CrossingJourney;
            if (crossingId != null)
            {
                if (!actors.TryGetValue(crossingId, out var actor) || !actor || !actor.gameObject.activeInHierarchy || actor.JourneyId != crossingId || actor.Finished)
                { queue.Remove(crossingId); actors.Remove(crossingId); progress.Remove(crossingId); SetLeaf(false); }
                else if (platformCrossingPoint && actor.transform.position.z >= platformCrossingPoint.position.z - .08f &&
                    Mathf.Abs(actor.transform.position.x - transform.position.x) < 3.95f &&
                    (!actor.motion || !actor.motion.agent || !actor.motion.agent.isOnOffMeshLink))
                {
                    queue.Crossed(crossingId); ++PassedCount;
                    actor.motion.agent.areaMask = DefaultAreaMask(actor.motion.agent.areaMask);
                    actors.Remove(crossingId); SetLeaf(false);
                    world.LogEvent("platform_access_passed", actor.serviceId, "track=" + trackNumber + "; ticket=" + actor.ArrivalPlan.ticketId, crossingId);
                }
                UpdateIndicator(); return;
            }
            string head = queue.HeadJourney;
            if (head == null || !actors.TryGetValue(head, out var first) || !first) { UpdateIndicator(); return; }
            bool atScanner = QueueSlotIndex(head) == 0 && queueAnchors.Length > 0 && queueAnchors[0] && Vector3.Distance(first.transform.position, queueAnchors[0].position) <= .5f;
            if (!atScanner) { queue.CancelScan(); UpdateIndicator(); return; }
            var currentView = View(first.serviceId);
            if (queue.BeginScan(head, currentView)) world.LogEvent("platform_ticket_scan_started", first.serviceId, "track=" + trackNumber, head);
            if (queue.CompleteScan(currentView, scanSecondsPerActor, out var scanned))
            {
                ++ScanCount;
                first.motion.agent.areaMask = DefaultAreaMask(first.motion.agent.areaMask) | (1 << ingressArea);
                SetLeaf(true);
                RememberDeparture(first, scanned.journeyId);
                world.LogEvent("platform_access_granted", scanned.serviceId, "track=" + trackNumber + "; ticket=" + scanned.ticketId, scanned.journeyId);
            }
            UpdateIndicator();
        }

        DecisionPlatformTicket Ticket(StationDecisionPassenger traveller) => new DecisionPlatformTicket {
            journeyId = traveller.JourneyId, ticketId = traveller.ArrivalPlan.ticketId, serviceId = traveller.serviceId,
            operatingDate = traveller.ArrivalPlan.scenarioDate, trackNumber = traveller.ArrivalPlan.trackNumber, representedPassengers = traveller.RepresentedPassengers };

        DecisionPlatformAccessView View(string serviceId)
        {
            var result = new DecisionPlatformAccessView { trackNumber = trackNumber, serviceId = serviceId, now = world ? world.ElapsedSeconds : 0,
                status = DecisionTrainStatus.Departed };
            if (!world || !world.rail || !world.rail.schedule) return result;
            result.operatingDate = world.rail.schedule.operatingDateIso;
            var service = world.rail.schedule.services.FirstOrDefault(s => s != null && s.serviceId == serviceId);
            if (service == null) return result;
            var observed = world.ObserveTrip(serviceId);
            result.departure = observed.departureSeconds; result.cutoff = observed.cutoffSeconds; result.status = observed.status;
            result.leadSeconds = service.platformAccessLeadSeconds;
            result.trainPresent = service.trackNumber == trackNumber && observed.train && observed.train.serviceId == serviceId &&
                observed.train.trackNumber == trackNumber && (observed.train.State == StationTrainState.DoorsOpening || observed.train.State == StationTrainState.Boarding) &&
                observed.train.berthPoint && Vector3.Distance(observed.train.transform.position, observed.train.berthPoint.position) < .1f;
            return result;
        }

        void SetLeaf(bool open)
        {
            if (ingressLeaf) ingressLeaf.localRotation = open ? closedLeafRotation * Quaternion.Euler(0, 82, 0) : closedLeafRotation;
            if (ingressLeafCollider) ingressLeafCollider.enabled = !open;
        }
        void UpdateIndicator()
        {
            if (!indicator) return;
            var block = new MaterialPropertyBlock(); indicator.GetPropertyBlock(block);
            var color = queue.CrossingJourney != null ? new Color(.15f, .9f, .45f) : IsOpen ? new Color(.95f, .65f, .1f) : new Color(.9f, .12f, .12f);
            block.SetColor("_BaseColor", color); block.SetColor("_Color", color); indicator.SetPropertyBlock(block);
        }
    }
}
