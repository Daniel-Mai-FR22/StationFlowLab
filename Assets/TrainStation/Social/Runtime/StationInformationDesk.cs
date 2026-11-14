using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

namespace StationFlow.TrainStation.Social
{
    [Serializable] public sealed class StationPassengerEvent : UnityEvent<StationPassenger> { }
    public enum StationServiceKind { Information, Shop }

    /// <summary>Explicit FIFO service. Only the first customer at the front marker can start a conversation.</summary>
    [DisallowMultipleComponent]
    public sealed class StationInformationDesk : MonoBehaviour
    {
        public StationServiceKind serviceKind;
        public string serviceName = "Accueil";
        [Tooltip("Authored reachable positions, beginning with the service position and continuing toward the queue tail.")]
        public Transform[] queueSlots = Array.Empty<Transform>();
        public StationReceptionist receptionist;
        [Min(.2f)] public float receptionRadius = 3f;
        [Min(.05f)] public float servicePositionTolerance = .35f;
        [Min(.05f), Tooltip("A customer joins only after reaching the current free position at the tail of the queue.")]
        public float arrivalTolerance = .22f;
        [Min(.1f)] public float conversationDuration = 7f;
        [Header("Queue observation and personal space")]
        [Tooltip("Each person reacts after seeing their immediate predecessor move, rather than following a global queue timer.")]
        public Vector2 queueReactionDelay = new Vector2(.28f, .65f);
        [Min(.05f)] public float observedAdvanceDistance = .18f;
        [Min(.4f)] public float minimumQueueSeparation = .70f;
        [Min(.1f)] public float queueAdvanceSpeed = .82f;
        [Min(.01f)] public float queueStoppingDistance = .06f;
        public StationPassengerEvent queueJoined = new StationPassengerEvent();
        public StationPassengerEvent customerApproached = new StationPassengerEvent();
        public StationPassengerEvent conversationStarted = new StationPassengerEvent();
        public StationPassengerEvent conversationEnded = new StationPassengerEvent();
        [SerializeField] List<StationPassenger> queue = new List<StationPassenger>();
        [SerializeField] StationPassenger activeCustomer;
        readonly HashSet<StationPassenger> approached = new HashSet<StationPassenger>();
        sealed class QueueProgress
        {
            public int slotIndex;
            public StationPassenger predecessor;
            public Vector3 predecessorPosition;
            public float reactsAt = -1f, lastAdvanceAt = -1f;
            public uint randomState;
        }
        readonly Dictionary<StationPassenger, QueueProgress> progress = new Dictionary<StationPassenger, QueueProgress>();
        static readonly List<StationInformationDesk> activeDesks = new List<StationInformationDesk>();
        float conversationEndsAt;

        public static IReadOnlyList<StationInformationDesk> ActiveDesks => activeDesks;
        public IReadOnlyList<StationPassenger> QueuePassengers => queue;
        public int QueueCount => queue.Count;
        public StationPassenger ActiveCustomer => activeCustomer;
        public bool Contains(StationPassenger passenger) => queue.Contains(passenger);
        public int GetQueueIndex(StationPassenger passenger) => queue.IndexOf(passenger);
        public Vector3 ReceptionistPosition => receptionist ? receptionist.transform.position : transform.position;
        public bool CanAcceptRequests => isActiveAndEnabled && receptionist && receptionist.isActiveAndEnabled &&
            queueSlots != null && queueSlots.Length > 0 && queueSlots[0];
        public bool HasQueueCapacity => TryGetFreeTailSlot(out _);
        public float GetLastQueueAdvanceTime(StationPassenger passenger) =>
            passenger && progress.TryGetValue(passenger, out var entry) ? entry.lastAdvanceAt : -1f;

        bool TryGetFreeTailSlot(out int index)
        {
            index = 0;
            if (!CanAcceptRequests || queue.Count >= queueSlots.Length) return false;
            if (queue.Count > 0)
            {
                var tail = queue[queue.Count - 1];
                if (!tail || !progress.TryGetValue(tail, out var entry)) return false;
                index = entry.slotIndex + 1;
                // A logical vacancy at the head is not a physical vacancy at the tail.
                if (index >= queueSlots.Length || !queueSlots[index] ||
                    PlanarDistance(tail.transform.position, queueSlots[index].position) < minimumQueueSeparation) return false;
            }
            return index < queueSlots.Length && queueSlots[index];
        }

        /// <summary>The next free queue position, or an unreserved waiting point beyond a full queue.</summary>
        public bool TryGetApproachPosition(out Vector3 position, StationPassenger passenger = null)
        {
            position = transform.position;
            if (!CanAcceptRequests) return false;
            if (TryGetFreeTailSlot(out var freeIndex))
            {
                position = queueSlots[freeIndex].position;
                return true;
            }
            var tail = queueSlots[queueSlots.Length - 1];
            if (!tail) return false;
            var previous = queueSlots.Length > 1 ? queueSlots[queueSlots.Length - 2] : null;
            var direction = previous ? tail.position - previous.position : tail.position - ReceptionistPosition;
            direction.y = 0f;
            float spacing = Mathf.Max(.9f, direction.magnitude);
            direction = direction.sqrMagnitude > .001f ? direction.normalized : -queueSlots[0].forward;
            // Full-queue arrivals wait locally without reserving a place; avoid one shared crowded waiting point.
            uint seed = passenger ? unchecked((uint)passenger.randomSeed) : 1u;
            seed = unchecked(seed * 747796405u + 2891336453u);
            float lateral = passenger ? ((seed & 65535u) / 65535f - .5f) * 2.4f : 0f;
            float extraDepth = passenger ? ((seed >> 16) / 65535f) * 2.2f : 0f;
            var candidate = tail.position + direction * (spacing + extraDepth) + Vector3.Cross(Vector3.up, direction) * lateral;
            int mask = passenger && passenger.motion && passenger.motion.agent ? passenger.motion.agent.areaMask : NavMesh.AllAreas;
            if (!NavMesh.SamplePosition(candidate, out var hit, .75f, mask) || Mathf.Abs(hit.position.y - tail.position.y) > .15f)
                return false;
            position = hit.position;
            return true;
        }

        public bool TryJoin(StationPassenger passenger)
        {
            if (!CanAcceptRequests || !passenger || !passenger.isActiveAndEnabled || passenger.CurrentService != this) return false;
            if (queue.Contains(passenger)) return true;
            if (!TryGetFreeTailSlot(out var freeIndex) || passenger.CurrentState != StationPassengerState.ApproachingQueue) return false;
            var arrival = queueSlots[freeIndex].position;
            var offset = passenger.transform.position - arrival;
            if (Mathf.Abs(offset.y) > 1.2f) return false;
            offset.y = 0f;
            if (offset.sqrMagnitude > arrivalTolerance * arrivalTolerance) return false;
            var predecessor = queue.Count > 0 ? queue[queue.Count - 1] : null;
            if (predecessor && PlanarDistance(passenger.transform.position, predecessor.transform.position) < minimumQueueSeparation) return false;
            progress[passenger] = new QueueProgress
            {
                slotIndex = freeIndex, predecessor = predecessor,
                predecessorPosition = predecessor ? predecessor.transform.position : arrival,
                randomState = unchecked((uint)passenger.randomSeed * 747796405u + 2891336453u)
            };
            queue.Add(passenger);
            queueJoined.Invoke(passenger);
            // Arrival listeners may cancel the request or disable a participant.
            return CanAcceptRequests && passenger && passenger.isActiveAndEnabled && queue.Contains(passenger);
        }

        public bool TryGetQueueSlot(StationPassenger passenger, out Transform slot)
        {
            slot = passenger && progress.TryGetValue(passenger, out var entry) && entry.slotIndex < queueSlots.Length
                ? queueSlots[entry.slotIndex] : null;
            return slot;
        }

        static float PlanarDistance(Vector3 a, Vector3 b)
        {
            var difference = a - b; difference.y = 0f; return difference.magnitude;
        }

        void UpdateQueueProgress()
        {
            for (int i = 0; i < queue.Count; ++i)
            {
                var passenger = queue[i];
                if (!passenger || !progress.TryGetValue(passenger, out var entry)) continue;
                if (entry.slotIndex <= i)
                {
                    entry.reactsAt = -1f;
                    entry.predecessor = i > 0 ? queue[i - 1] : null;
                    if (entry.predecessor) entry.predecessorPosition = entry.predecessor.transform.position;
                    continue;
                }
                if (passenger.CurrentState != StationPassengerState.WaitingInQueue)
                { entry.reactsAt = -1f; continue; }
                var candidate = queueSlots[entry.slotIndex - 1];
                var current = queueSlots[entry.slotIndex];
                if (!candidate || !current) continue;
                var ahead = i > 0 ? queue[i - 1] : null;
                bool opening;
                if (ahead)
                {
                    // Observe a real gap opening ahead, not merely the new list index.
                    float step = PlanarDistance(candidate.position, current.position);
                    opening = PlanarDistance(ahead.transform.position, passenger.transform.position) >=
                        Mathf.Max(minimumQueueSeparation + .12f, step + observedAdvanceDistance);
                    if (entry.predecessor == ahead)
                        opening &= PlanarDistance(ahead.transform.position, entry.predecessorPosition) >= observedAdvanceDistance;
                }
                else opening = true;
                // A departing customer may still be standing in the space we need.
                if (entry.predecessor && entry.predecessor.isActiveAndEnabled && !Contains(entry.predecessor))
                    opening &= PlanarDistance(entry.predecessor.transform.position, entry.predecessorPosition) >= observedAdvanceDistance &&
                        PlanarDistance(entry.predecessor.transform.position, candidate.position) >= minimumQueueSeparation;
                if (!opening) { entry.reactsAt = -1f; continue; }
                if (entry.reactsAt < 0f)
                {
                    entry.randomState = unchecked(entry.randomState * 1664525u + 1013904223u);
                    float low = Mathf.Max(.05f, Mathf.Min(queueReactionDelay.x, queueReactionDelay.y));
                    float high = Mathf.Max(low, Mathf.Max(queueReactionDelay.x, queueReactionDelay.y));
                    entry.reactsAt = Time.time + Mathf.Lerp(low, high, (entry.randomState & 65535u) / 65535f);
                    continue;
                }
                if (Time.time < entry.reactsAt) continue;
                // Move one marker only. Further vacancies require another observation and reaction.
                --entry.slotIndex;
                entry.lastAdvanceAt = Time.time;
                entry.reactsAt = -1f;
                if (entry.predecessor != ahead)
                {
                    entry.predecessor = ahead;
                    entry.predecessorPosition = ahead ? ahead.transform.position : candidate.position;
                }
            }
        }

        /// <summary>Service area is tied to the authored front slot, so proximity behind the desk cannot trigger service.</summary>
        public bool IsWithinServiceArea(Vector3 position)
        {
            if (queueSlots.Length == 0 || !queueSlots[0]) return false;
            var front = queueSlots[0].position;
            var horizontal = position - front;
            horizontal.y = 0f;
            return Mathf.Abs(position.y - front.y) < 1.2f &&
                   horizontal.sqrMagnitude <= servicePositionTolerance * servicePositionTolerance &&
                   Vector3.Distance(position, ReceptionistPosition) <= receptionRadius;
        }

        void Update()
        {
            for (var i = queue.Count - 1; i >= 0; --i)
                if (!queue[i] || !queue[i].isActiveAndEnabled || queue[i].CurrentService != this) Remove(queue[i], false);
            if (!receptionist || !receptionist.isActiveAndEnabled) { CancelAll(); return; }
            UpdateQueueProgress();
            if (activeCustomer)
            {
                if (!IsWithinServiceArea(activeCustomer.transform.position)) { Cancel(activeCustomer); return; }
                if (Time.time < conversationEndsAt) return;
                var completed = activeCustomer;
                activeCustomer = null;
                queue.Remove(completed);
                progress.Remove(completed);
                approached.Remove(completed);
                receptionist.EndConversation();
                conversationEnded.Invoke(completed);
                if (completed && completed.isActiveAndEnabled) completed.NotifyConversationEnded();
                return;
            }
            if (queue.Count == 0) return;
            var frontCustomer = queue[0];
            if (!progress.TryGetValue(frontCustomer, out var frontProgress) || frontProgress.slotIndex != 0) return;
            if (!frontCustomer || !frontCustomer.ReadyForConversation || !IsWithinServiceArea(frontCustomer.transform.position)) return;
            if (approached.Add(frontCustomer)) customerApproached.Invoke(frontCustomer);
            // User-authored arrival events may cancel the request, move the customer, or disable the desk.
            if (!isActiveAndEnabled || !receptionist || !receptionist.isActiveAndEnabled ||
                !frontCustomer || !frontCustomer.isActiveAndEnabled || queue.Count == 0 || queue[0] != frontCustomer ||
                !frontCustomer.ReadyForConversation || !IsWithinServiceArea(frontCustomer.transform.position)) return;
            activeCustomer = frontCustomer;
            conversationEndsAt = Time.time + Mathf.Max(.1f, conversationDuration);
            receptionist.BeginConversation(frontCustomer);
            frontCustomer.NotifyConversationStarted();
            conversationStarted.Invoke(frontCustomer);
        }

        public void Cancel(StationPassenger passenger) => Remove(passenger, true);

        void Remove(StationPassenger passenger, bool notify)
        {
            var wasInQueue = queue.Remove(passenger);
            if (passenger) progress.Remove(passenger);
            approached.Remove(passenger);
            if (activeCustomer == passenger)
            {
                activeCustomer = null;
                if (receptionist) receptionist.EndConversation();
            }
            if (notify && passenger && passenger.CurrentService == this &&
                (wasInQueue || passenger.HasInformationRequest)) passenger.NotifyDeskUnavailable();
        }

        void CancelAll()
        {
            if (queue.Count == 0) return;
            var abandoned = queue.ToArray();
            queue.Clear();
            progress.Clear();
            approached.Clear();
            activeCustomer = null;
            if (receptionist) receptionist.EndConversation();
            foreach (var passenger in abandoned) if (passenger && passenger.CurrentService == this) passenger.NotifyDeskUnavailable();
        }

        void OnEnable() { if (!activeDesks.Contains(this)) activeDesks.Add(this); }
        void OnDisable() { activeDesks.Remove(this); if (Application.isPlaying) CancelAll(); }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(.05f, .8f, .7f);
            for (var i = 0; i < queueSlots.Length; ++i)
            {
                if (!queueSlots[i]) continue;
                Gizmos.DrawWireSphere(queueSlots[i].position, i == 0 ? servicePositionTolerance : .25f);
                if (i > 0 && queueSlots[i - 1]) Gizmos.DrawLine(queueSlots[i - 1].position, queueSlots[i].position);
            }
        }
    }
}
