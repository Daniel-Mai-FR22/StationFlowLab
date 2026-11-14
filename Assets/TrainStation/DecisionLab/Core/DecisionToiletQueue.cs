using System;
using System.Collections.Generic;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>FIFO arrival order and bounded WC leases, independent of navigation and simulation sampling.</summary>
    public sealed class DecisionToiletQueue
    {
        sealed class Lease
        {
            public string journeyId;
            public double startedAt = -1d, duration;
            public bool releasing;
        }
        readonly List<string> waiting = new List<string>();
        readonly Lease[] uses;
        public DecisionToiletQueue(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            uses = new Lease[capacity];
        }
        public int Capacity => uses.Length;
        public int Count => waiting.Count;
        public int ActiveUseCount { get { int count = 0; foreach (var use in uses) if (use != null) ++count; return count; } }
        public string HeadJourneyId => waiting.Count > 0 ? waiting[0] : null;
        public string[] WaitingJourneyIds => waiting.ToArray();
        public int Position(string id) => string.IsNullOrEmpty(id) ? -1 : waiting.IndexOf(id);
        public int UseIndex(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int index = 0; index < uses.Length; ++index) if (uses[index]?.journeyId == id) return index;
            return -1;
        }
        public string UseJourneyId(int index) => index >= 0 && index < uses.Length ? uses[index]?.journeyId : null;
        public bool HasUse(string id) => UseIndex(id) >= 0;
        public bool IsReleasing(string id) { int index = UseIndex(id); return index >= 0 && uses[index].releasing; }
        public bool HasStarted(string id) { int index = UseIndex(id); return index >= 0 && uses[index].startedAt >= 0d; }
        public double StartedAt(string id) { int index = UseIndex(id); return index >= 0 ? uses[index].startedAt : -1d; }
        public double Duration(string id) { int index = UseIndex(id); return index >= 0 ? uses[index].duration : 0d; }
        public bool HasFinishedDuration(string id, double now)
        { int index = UseIndex(id); return index >= 0 && uses[index].startedAt >= 0d && NonNegative(now) && now + 1e-5d >= uses[index].startedAt + uses[index].duration; }
        public bool Join(string id, int maximumWaiting)
        {
            if (string.IsNullOrEmpty(id) || maximumWaiting < 1 || HasUse(id)) return false;
            if (Position(id) >= 0) return true;
            if (waiting.Count >= maximumWaiting) return false;
            waiting.Add(id); return true;
        }
        public bool RemoveWaiting(string id) => waiting.Remove(id);
        public bool TryReserve(string id, bool headPhysicallyReady, bool[] physicallyClearPlaces, out int useIndex)
        {
            useIndex = -1;
            if (string.IsNullOrEmpty(id)) return false;
            int existing = UseIndex(id);
            if (existing >= 0) { useIndex = existing; return true; }
            if (!headPhysicallyReady || HeadJourneyId != id || physicallyClearPlaces == null || physicallyClearPlaces.Length != uses.Length) return false;
            for (int index = 0; index < uses.Length; ++index)
            {
                if (uses[index] != null || !physicallyClearPlaces[index]) continue;
                uses[index] = new Lease { journeyId = id };
                waiting.RemoveAt(0); useIndex = index; return true;
            }
            return false;
        }
        public bool MarkStarted(string id, double now, double duration)
        {
            int index = UseIndex(id);
            if (index < 0 || uses[index].releasing || !NonNegative(now) || !Finite(duration) || duration <= 0d || !Finite(now + duration)) return false;
            if (uses[index].startedAt >= 0d) return false;
            uses[index].startedAt = now; uses[index].duration = duration; return true;
        }
        // Completion initiates departure; it does not free a place still occupied by a body.
        public bool BeginRelease(string id)
        {
            int index = UseIndex(id);
            if (index < 0 || uses[index].releasing) return false;
            uses[index].releasing = true; return true;
        }
        public bool ReleaseCleared(string id, bool bodyPhysicallyClearOrDespawned)
        {
            int index = UseIndex(id);
            if (index < 0 || !uses[index].releasing || !bodyPhysicallyClearOrDespawned) return false;
            uses[index] = null; return true;
        }
        public double EstimatedWait(string id, double now, double expectedDuration, double movementAllowance)
        {
            if (!NonNegative(now) || !Finite(expectedDuration) || expectedDuration <= 0d || !NonNegative(movementAllowance)) return double.PositiveInfinity;
            if (HasUse(id)) return 0d;
            var available = new double[uses.Length];
            for (int index = 0; index < uses.Length; ++index)
            {
                var lease = uses[index];
                available[index] = lease == null ? 0d : lease.releasing ? movementAllowance : lease.startedAt < 0d
                    ? expectedDuration + movementAllowance : Math.Max(0d, lease.startedAt + lease.duration - now) + movementAllowance;
            }
            int predecessors = Position(id); if (predecessors < 0) predecessors = waiting.Count;
            for (int job = 0; job < predecessors; ++job)
            {
                int index = Earliest(available); available[index] += expectedDuration + movementAllowance;
            }
            return available[Earliest(available)] + (predecessors > 0 ? movementAllowance : 0d);
        }
        static int Earliest(double[] values)
        { int best = 0; for (int index = 1; index < values.Length; ++index) if (values[index] < values[best]) best = index; return best; }
        static bool NonNegative(double value) => Finite(value) && value >= 0d;
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
