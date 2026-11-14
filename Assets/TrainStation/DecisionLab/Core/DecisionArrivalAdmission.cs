using System;
using System.Collections.Generic;
using System.Linq;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Reserves a brief contiguous portal window and pool capacity for an entering party.</summary>
    public sealed class DecisionArrivalAdmission
    {
        sealed class Reservation
        {
            public string groupId;
            public double deadline;
            public readonly HashSet<string> remaining = new HashSet<string>(StringComparer.Ordinal);
        }
        readonly Dictionary<string, Reservation> portals = new Dictionary<string, Reservation>(StringComparer.Ordinal);
        readonly HashSet<string> startedGroups = new HashSet<string>(StringComparer.Ordinal);

        public void Expire(double now)
        {
            foreach (var key in portals.Where(p => now >= p.Value.deadline || p.Value.remaining.Count == 0).Select(p => p.Key).ToArray())
                portals.Remove(key);
        }

        public int ReservedSlots(double now) { Expire(now); return portals.Values.Sum(r => r.remaining.Count); }

        public bool HasPriority(DecisionArrivalPlan plan, string portalId, double now)
        {
            Expire(now);
            return plan != null && portalId != null && portals.TryGetValue(portalId, out var reservation) &&
                reservation.groupId == plan.groupId && reservation.remaining.Contains(plan.journeyId);
        }

        public bool CanAdmit(DecisionArrivalPlan plan, string portalId, double now, int freeActorSlots, int remainingGroupMembers,
            int clearPortalSlots = int.MaxValue)
        {
            Expire(now);
            if (plan == null || string.IsNullOrEmpty(portalId) || double.IsNaN(now) || double.IsInfinity(now) ||
                now < plan.plannedEntrySeconds || freeActorSlots <= 0 || clearPortalSlots <= 0) return false;
            portals.TryGetValue(portalId, out var own);
            if (own != null && (own.groupId != plan.groupId || !own.remaining.Contains(plan.journeyId))) return false;
            int reservedElsewhere = portals.Values.Where(r => r != own).Sum(r => r.remaining.Count);
            if (own != null) return freeActorSlots - reservedElsewhere >= 1;
            int needed = !string.IsNullOrEmpty(plan.groupId) && !startedGroups.Contains(plan.groupId) ? Math.Max(1, remainingGroupMembers) : 1;
            return freeActorSlots - reservedElsewhere >= needed && clearPortalSlots >= needed;
        }

        // Called only after the runtime NavMesh and physical occupancy checks succeed.
        public void Admitted(DecisionArrivalPlan plan, string portalId, double now, IEnumerable<string> remainingJourneyIds, double windowSeconds)
        {
            Expire(now);
            if (plan == null || string.IsNullOrEmpty(plan.groupId)) return;
            if (portals.TryGetValue(portalId, out var current) && current.groupId == plan.groupId)
            {
                current.remaining.Remove(plan.journeyId);
                if (current.remaining.Count == 0) portals.Remove(portalId);
                return;
            }
            // Expired parties cannot repeatedly monopolize a portal by extending their window.
            if (!startedGroups.Add(plan.groupId)) return;
            if (windowSeconds <= 0d || double.IsNaN(windowSeconds) || double.IsInfinity(windowSeconds)) return;
            var reservation = new Reservation { groupId = plan.groupId, deadline = now + windowSeconds };
            foreach (var id in remainingJourneyIds ?? Array.Empty<string>())
                if (!string.IsNullOrEmpty(id) && id != plan.journeyId) reservation.remaining.Add(id);
            if (reservation.remaining.Count > 0) portals[portalId] = reservation;
        }

        public void RemoveJourney(string journeyId)
        {
            foreach (var reservation in portals.Values) reservation.remaining.Remove(journeyId);
            foreach (var key in portals.Where(p => p.Value.remaining.Count == 0).Select(p => p.Key).ToArray()) portals.Remove(key);
        }
    }
}
