using System;
using System.Collections.Generic;
using System.Linq;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Exclusive, stable holding markers belong to journeys, independently of reusable actor slots.</summary>
    public sealed class DecisionHoldingSlotAllocator
    {
        sealed class Lease
        {
            public string journeyId;
            public int owner, slot;
        }
        readonly Dictionary<string, Lease> journeys = new Dictionary<string, Lease>(StringComparer.Ordinal);
        readonly Dictionary<int, string> owners = new Dictionary<int, string>();
        readonly bool[] reserved;

        public int Capacity => reserved.Length;
        public int Count => journeys.Count;

        public DecisionHoldingSlotAllocator(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            reserved = new bool[capacity];
        }

        public int SlotFor(string journeyId) => journeyId != null && journeys.TryGetValue(journeyId, out var lease) ? lease.slot : -1;

        public bool TryPeek(int preferredSlot, Func<int, bool> physicallyAvailable, out int slot)
        {
            slot = -1;
            int start = (int)(((long)preferredSlot % Capacity + Capacity) % Capacity);
            for (int offset = 0; offset < Capacity; ++offset)
            {
                int candidate = (start + offset) % Capacity;
                if (reserved[candidate] || (physicallyAvailable != null && !physicallyAvailable(candidate))) continue;
                slot = candidate; return true;
            }
            return false;
        }

        public bool TryAcquire(string journeyId, int owner, int preferredSlot, Func<int, bool> physicallyAvailable, out int slot)
        {
            slot = -1;
            if (string.IsNullOrWhiteSpace(journeyId) || owner == 0) return false;
            if (journeys.TryGetValue(journeyId, out var existing))
            {
                if (existing.owner != owner) return false;
                slot = existing.slot; return true;
            }
            // One reused actor cannot retain its previous journey's marker.
            if (owners.TryGetValue(owner, out var oldJourney)) Release(oldJourney);
            if (!TryPeek(preferredSlot, physicallyAvailable, out slot)) return false;
            var lease = new Lease { journeyId = journeyId, owner = owner, slot = slot };
            journeys.Add(journeyId, lease); owners.Add(owner, journeyId); reserved[slot] = true;
            return true;
        }

        public bool Release(string journeyId)
        {
            if (journeyId == null || !journeys.TryGetValue(journeyId, out var lease)) return false;
            journeys.Remove(journeyId); owners.Remove(lease.owner); reserved[lease.slot] = false;
            return true;
        }

        public string[] Prune(Func<string, int, bool> isCurrentLiveJourney)
        {
            if (isCurrentLiveJourney == null) throw new ArgumentNullException(nameof(isCurrentLiveJourney));
            var stale = journeys.Values.Where(l => !isCurrentLiveJourney(l.journeyId, l.owner)).Select(l => l.journeyId).ToArray();
            foreach (var id in stale) Release(id);
            return stale;
        }

        public void Clear()
        {
            journeys.Clear(); owners.Clear(); Array.Clear(reserved, 0, reserved.Length);
        }
    }
}
