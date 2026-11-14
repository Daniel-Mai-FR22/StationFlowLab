using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Physical arrival FIFO outside the WC, with exclusive use-place leases until physical clearance.</summary>
    [DefaultExecutionOrder(170)]
    [DisallowMultipleComponent]
    public sealed class StationDecisionToiletQueue : MonoBehaviour
    {
        public StationDecisionWorld world;
        public StationDecisionPoi poi;
        public Transform[] queueSlots = Array.Empty<Transform>();
        public Transform[] usePoints = Array.Empty<Transform>();
        public Transform exitPoint;
        [Min(.05f)] public float arrivalTolerance = .22f;
        [Min(.4f)] public float minimumQueueSeparation = .70f;
        [Min(0f)] public float queueReactionSeconds = .2f;
        [Min(.05f)] public float queuePositionTolerance = .12f;
        [Min(0f)] public float movementAllowanceSeconds = 4f;
        sealed class PhysicalProgress
        {
            public int slot;
            public string predecessorId;
            public Vector3 predecessorPosition;
            public float advancesAt = -1f;
        }
        DecisionToiletQueue queue;
        readonly Dictionary<string, StationDecisionPassenger> actors = new Dictionary<string, StationDecisionPassenger>(StringComparer.Ordinal);
        readonly Dictionary<string, PhysicalProgress> progress = new Dictionary<string, PhysicalProgress>(StringComparer.Ordinal);
        readonly Dictionary<string, StationDecisionPassenger> departing = new Dictionary<string, StationDecisionPassenger>(StringComparer.Ordinal);
        static readonly List<StationDecisionToiletQueue> registry = new List<StationDecisionToiletQueue>();
        public int Count => queue?.Count ?? 0;
        public int Capacity => usePoints?.Length ?? 0;
        public int ActiveUseCount => queue?.ActiveUseCount ?? 0;
        public float ArrivalTolerance => Mathf.Max(.05f, arrivalTolerance);
        public float MinimumQueueSeparation => Mathf.Max(.4f, minimumQueueSeparation);
        public bool CanAcceptRequests => isActiveAndEnabled && ValidLayout && (queue == null || queue.Count < queueSlots.Length);
        public bool Available => CanAcceptRequests;
        public string HeadJourneyId => queue?.HeadJourneyId;
        bool ValidLayout => world && poi && exitPoint && queueSlots != null && queueSlots.Length > 0 && queueSlots.All(p => p) &&
            usePoints != null && usePoints.Length > 0 && usePoints.All(p => p);
        public static StationDecisionToiletQueue For(StationDecisionWorld targetWorld)
        {
            if (!targetWorld || !targetWorld.IsDaily) return null;
            registry.RemoveAll(q => !q);
            return registry.FirstOrDefault(q => q.world == targetWorld);
        }
        void OnEnable() { if (!registry.Contains(this)) registry.Add(this); }
        void OnDisable() { registry.Remove(this); }
        bool EnsureQueue()
        {
            if (!ValidLayout) return false;
            if (queue == null) queue = new DecisionToiletQueue(Capacity);
            return queue.Capacity == Capacity;
        }
        public bool Contains(string journeyId) => queue != null && queue.Position(journeyId) >= 0;
        public bool IsUsing(string journeyId) => queue != null && queue.HasUse(journeyId);
        public bool HasStartedUsing(string journeyId) => queue != null && queue.HasStarted(journeyId);
        public bool IsReleasing(string journeyId) => queue != null && queue.IsReleasing(journeyId);
        public int UseSlotIndex(string journeyId) => queue == null ? -1 : queue.UseIndex(journeyId);
        public float UseDurationSeconds(string journeyId) => queue == null ? 0f : (float)queue.Duration(journeyId);
        public float UseStartedAtSeconds(string journeyId) => queue == null ? -1f : (float)queue.StartedAt(journeyId);
        public int QueueOrdinal(string journeyId) => queue == null ? 0 : queue.Position(journeyId) + 1;
        public int QueueSlotIndex(string journeyId) => journeyId != null && progress.TryGetValue(journeyId, out var entry) ? entry.slot : -1;
        public string PredecessorJourneyId(string journeyId) => journeyId != null && progress.TryGetValue(journeyId, out var entry) ? entry.predecessorId ?? "" : "";
        public Vector3 PredecessorPosition(string journeyId)
        {
            string id = PredecessorJourneyId(journeyId);
            if (!string.IsNullOrEmpty(id) && actors.TryGetValue(id, out var actor) && actor && actor.JourneyId == id) return actor.transform.position;
            return journeyId != null && progress.TryGetValue(journeyId, out var entry) ? entry.predecessorPosition : Vector3.zero;
        }
        public Vector3 PositionFor(StationDecisionPassenger traveller)
        {
            if (TryGetUsePosition(traveller, out var use)) return use;
            int index = QueueSlotIndex(traveller ? traveller.JourneyId : null);
            return index >= 0 && index < queueSlots.Length ? queueSlots[index].position : GetApproachPosition(traveller);
        }
        public Vector3 GetApproachPosition(StationDecisionPassenger traveller)
        {
            if (!ValidLayout) return poi ? poi.Position : transform.position;
            if (TryGetTail(out int tail, out _)) return queueSlots[tail].position;
            var last = queueSlots[queueSlots.Length - 1];
            var previous = queueSlots.Length > 1 ? queueSlots[queueSlots.Length - 2] : exitPoint;
            Vector3 direction = last.position - previous.position; direction.y = 0f;
            direction = direction.sqrMagnitude > .001f ? direction.normalized : Vector3.left;
            Vector3 candidate = last.position + direction * Mathf.Max(.95f, MinimumQueueSeparation + ArrivalTolerance);
            int mask = traveller && traveller.motion && traveller.motion.agent ? traveller.motion.agent.areaMask : NavMesh.AllAreas;
            return NavMesh.SamplePosition(candidate, out var hit, .2f, mask) && Mathf.Abs(hit.position.y - last.position.y) < .15f ? hit.position : last.position;
        }
        public bool TryJoin(StationDecisionPassenger traveller, out string reason)
        {
            reason = "WC queue layout or passenger is unavailable.";
            if (!ValidTraveller(traveller) || !EnsureQueue()) return false;
            string id = traveller.JourneyId;
            if (Contains(id) || IsUsing(id)) { reason = ""; return true; }
            reason = "Approach the free physical WC queue tail.";
            if (!TryGetTail(out int tail, out var ahead)) return false;
            Vector3 arrival = queueSlots[tail].position;
            if (!PositionClear(arrival, traveller) || !DecisionPhysicalQueueArrival.CanRegister(tail, tail,
                PlanarDistance(traveller.transform.position, arrival), traveller.transform.position.y - arrival.y,
                ahead, ahead ? PlanarDistance(traveller.transform.position, ahead.transform.position) : 0f,
                ArrivalTolerance, MinimumQueueSeparation)) return false;
            if (!queue.Join(id, queueSlots.Length)) return false;
            actors[id] = traveller;
            progress[id] = new PhysicalProgress { slot = tail, predecessorId = ahead ? ahead.JourneyId : "",
                predecessorPosition = ahead ? ahead.transform.position : arrival };
            reason = "";
            Log("toilet_queue_joined", traveller, Detail(traveller) + "; ordinal=" + QueueOrdinal(id) +
                "; slot=" + tail + "; arrivalOffset=" + F(PlanarDistance(traveller.transform.position, arrival)) +
                "; predecessor=" + PredecessorJourneyId(id) + "; predecessorGap=" + F(ahead ? PlanarDistance(traveller.transform.position, ahead.transform.position) : -1f));
            return true;
        }
        bool TryGetTail(out int slot, out StationDecisionPassenger predecessor)
        {
            slot = 0; predecessor = null;
            if (!EnsureQueue() || queue.Count >= queueSlots.Length) return false;
            if (queue.Count > 0)
            {
                string id = queue.WaitingJourneyIds[queue.Count - 1];
                if (!progress.TryGetValue(id, out var entry) || !actors.TryGetValue(id, out predecessor) || !predecessor ||
                    predecessor.JourneyId != id || !predecessor.gameObject.activeInHierarchy) return false;
                slot = entry.slot + 1;
            }
            return slot >= 0 && slot < queueSlots.Length;
        }
        public bool TryBeginUse(StationDecisionPassenger traveller, out Vector3 destination)
        {
            destination = default;
            if (!ValidTraveller(traveller) || !EnsureQueue()) return false;
            if (TryGetUsePosition(traveller, out destination)) return !queue.IsReleasing(traveller.JourneyId);
            bool physicallyAtHead = QueueSlotIndex(traveller.JourneyId) == 0 &&
                PlanarDistance(traveller.transform.position, queueSlots[0].position) <= Mathf.Max(.05f, queuePositionTolerance) &&
                Mathf.Abs(traveller.transform.position.y - queueSlots[0].position.y) <= .15f;
            bool[] cleared = usePoints.Select(point => PositionClear(point.position, traveller)).ToArray();
            if (!queue.TryReserve(traveller.JourneyId, physicallyAtHead, cleared, out int index)) return false;
            progress.Remove(traveller.JourneyId);
            departing[traveller.JourneyId] = traveller;
            destination = usePoints[index].position;
            Log("toilet_use_reserved", traveller, Detail(traveller) + "; useIndex=" + index);
            return true;
        }
        public bool TryGetUsePosition(StationDecisionPassenger traveller, out Vector3 destination)
        {
            destination = default;
            int index = queue == null || !traveller ? -1 : queue.UseIndex(traveller.JourneyId);
            if (index < 0 || usePoints == null || index >= usePoints.Length || !usePoints[index]) return false;
            destination = usePoints[index].position; return true;
        }
        public bool MarkUseStarted(StationDecisionPassenger traveller, float duration)
        {
            if (!ValidTraveller(traveller) || !TryGetUsePosition(traveller, out var use) ||
                PlanarDistance(traveller.transform.position, use) > Mathf.Max(.05f, queuePositionTolerance) || Mathf.Abs(traveller.transform.position.y - use.y) > .15f ||
                !queue.MarkStarted(traveller.JourneyId, world.ElapsedSeconds, duration)) return false;
            Log("toilet_use_started", traveller, Detail(traveller) + "; useIndex=" + queue.UseIndex(traveller.JourneyId) + "; duration=" + F(duration));
            return true;
        }
        public void CompleteUse(StationDecisionPassenger traveller, bool completed = true)
        {
            if (!traveller || queue == null || completed && !queue.HasFinishedDuration(traveller.JourneyId, world.ElapsedSeconds) ||
                !queue.BeginRelease(traveller.JourneyId)) return;
            Log(completed ? "toilet_use_completed" : "toilet_use_interrupted", traveller,
                Detail(traveller) + "; useIndex=" + queue.UseIndex(traveller.JourneyId) +
                "; startedAt=" + F(UseStartedAtSeconds(traveller.JourneyId)) + "; duration=" + F(UseDurationSeconds(traveller.JourneyId)));
        }
        public void Leave(StationDecisionPassenger traveller)
        {
            if (!traveller || queue == null) return;
            string id = traveller.JourneyId;
            if (queue.RemoveWaiting(id)) Log("toilet_queue_left", traveller, Detail(traveller));
            if (queue.BeginRelease(id)) Log("toilet_use_interrupted", traveller, Detail(traveller) + "; useIndex=" + queue.UseIndex(id));
            if (traveller.gameObject.activeInHierarchy && !traveller.Finished) departing[id] = traveller;
            progress.Remove(id);
            if (!queue.HasUse(id)) actors.Remove(id);
            ReleaseIfClear(id, traveller);
        }
        public float EstimatedWaitSeconds(StationDecisionPassenger traveller)
        {
            if (!EnsureQueue()) return 99999f;
            return (float)queue.EstimatedWait(traveller ? traveller.JourneyId : null, world.ElapsedSeconds,
                Mathf.Max(.1f, poi.meanDurationSeconds), Mathf.Max(0f, movementAllowanceSeconds));
        }
        void Update()
        {
            if (!world || !world.IsDaily || world.Paused || !EnsureQueue()) return;
            foreach (var pair in actors.ToArray())
            {
                bool stale = !pair.Value || pair.Value.Finished || !pair.Value.gameObject.activeInHierarchy || pair.Value.JourneyId != pair.Key;
                if (stale)
                {
                    queue.RemoveWaiting(pair.Key); queue.BeginRelease(pair.Key); queue.ReleaseCleared(pair.Key, true);
                    actors.Remove(pair.Key); progress.Remove(pair.Key); departing.Remove(pair.Key);
                }
                else ReleaseIfClear(pair.Key, pair.Value);
            }
            foreach (var pair in departing.ToArray())
                if (!pair.Value || !pair.Value.gameObject.activeInHierarchy || pair.Value.JourneyId != pair.Key || pair.Value.Finished ||
                    !queueSlots.Any(point => PlanarDistance(pair.Value.transform.position, point.position) < MinimumQueueSeparation)) departing.Remove(pair.Key);
            UpdatePhysicalProgress();
        }
        void ReleaseIfClear(string id, StationDecisionPassenger traveller)
        {
            if (!queue.IsReleasing(id)) return;
            int index = queue.UseIndex(id);
            bool clear = !traveller || traveller.Finished || !traveller.gameObject.activeInHierarchy || traveller.JourneyId != id ||
                PlanarDistance(traveller.transform.position, usePoints[index].position) >= MinimumQueueSeparation;
            if (!queue.ReleaseCleared(id, clear)) return;
            if (traveller && traveller.JourneyId == id) Log("toilet_use_released", traveller, "useIndex=" + index + "; physical clearance");
            actors.Remove(id); progress.Remove(id);
        }
        void UpdatePhysicalProgress()
        {
            string[] ids = queue.WaitingJourneyIds;
            for (int logical = 0; logical < ids.Length; ++logical)
            {
                string id = ids[logical];
                if (!actors.TryGetValue(id, out var actor) || !actor || !progress.TryGetValue(id, out var entry)) continue;
                string aheadId = logical > 0 ? ids[logical - 1] : "";
                StationDecisionPassenger ahead = null; if (logical > 0) actors.TryGetValue(aheadId, out ahead);
                if (entry.slot <= logical || entry.slot <= 0)
                { entry.advancesAt = -1f; entry.predecessorId = aheadId; if (ahead) entry.predecessorPosition = ahead.transform.position; continue; }
                var current = queueSlots[entry.slot]; var candidate = queueSlots[entry.slot - 1];
                bool predecessorChanged = entry.predecessorId != aheadId;
                bool aheadCleared = !ahead || QueueSlotIndex(aheadId) < entry.slot - 1;
                bool canAdvance = aheadCleared && PositionClear(candidate.position, actor) &&
                    DecisionPhysicalQueueArrival.CanAdvance(entry.slot, logical, PlanarDistance(actor.transform.position, current.position), ahead,
                        ahead ? predecessorChanged ? MinimumQueueSeparation : PlanarDistance(ahead.transform.position, entry.predecessorPosition) : 0f,
                        ahead ? PlanarDistance(ahead.transform.position, candidate.position) : 0f,
                        Mathf.Max(.05f, queuePositionTolerance), MinimumQueueSeparation);
                if (!canAdvance) { entry.advancesAt = -1f; continue; }
                if (entry.advancesAt < 0f) { entry.advancesAt = world.ElapsedSeconds + Mathf.Max(0f, queueReactionSeconds); continue; }
                if (world.ElapsedSeconds < entry.advancesAt) continue;
                --entry.slot; entry.advancesAt = -1f; entry.predecessorId = aheadId;
                entry.predecessorPosition = ahead ? ahead.transform.position : candidate.position;
            }
        }
        bool PositionClear(Vector3 position, StationDecisionPassenger excluded)
        {
            foreach (var pair in actors)
                if (pair.Value && pair.Value != excluded && pair.Value.gameObject.activeInHierarchy && pair.Value.JourneyId == pair.Key &&
                    PlanarDistance(pair.Value.transform.position, position) < MinimumQueueSeparation) return false;
            foreach (var pair in departing)
                if (pair.Value && pair.Value != excluded && pair.Value.gameObject.activeInHierarchy && pair.Value.JourneyId == pair.Key &&
                    PlanarDistance(pair.Value.transform.position, position) < MinimumQueueSeparation) return false;
            return true;
        }
        bool ValidTraveller(StationDecisionPassenger traveller) => traveller && world && traveller.world == world && !traveller.Finished &&
            traveller.gameObject.activeInHierarchy && traveller.ArrivalPlan != null && !string.IsNullOrEmpty(traveller.JourneyId);
        void Log(string kind, StationDecisionPassenger traveller, string detail) => world.LogEvent(kind, traveller.serviceId, detail, traveller.JourneyId);
        string Detail(StationDecisionPassenger traveller) => "poi=" + (poi ? poi.poiId : "") + "; queued=" + Count + "; occupied=" + ActiveUseCount + "; capacity=" + Capacity;
        static string F(float value) => value.ToString("0.000", CultureInfo.InvariantCulture);
        static float PlanarDistance(Vector3 first, Vector3 second) { Vector3 delta = first - second; delta.y = 0f; return delta.magnitude; }
    }
}
