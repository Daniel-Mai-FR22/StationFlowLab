using System;
using System.Collections.Generic;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>A visual actor represents a distinct seat block in one physical coach.</summary>
    public sealed class DecisionSeatBlock
    {
        public readonly int coachNumber;
        public readonly int[] seatNumbers;
        public int RepresentedPassengers => seatNumbers.Length;
        public DecisionSeatBlock(int coach, int[] seats) { coachNumber = coach; seatNumbers = seats; }
    }

    public static class DecisionSeatTicketAllocator
    {
        public static List<DecisionSeatBlock> Allocate(int[] coachSeatCapacities, int demand, int throughPassengers, double visualActorRatio)
        {
            if (coachSeatCapacities == null || coachSeatCapacities.Length == 0 || demand < 0 || throughPassengers < 0 ||
                double.IsNaN(visualActorRatio) || double.IsInfinity(visualActorRatio) || visualActorRatio <= 0d || visualActorRatio > 1d)
                throw new ArgumentException("A concrete ticket allocation requires positive coach capacities, valid loads and a sampling ratio.");
            long total = 0;
            foreach (int capacity in coachSeatCapacities)
            { if (capacity < 1) throw new ArgumentException("Every coach requires at least one seat."); total += capacity; }
            if (total > int.MaxValue || (long)demand + throughPassengers > total)
                throw new ArgumentException("Ticket loads exceed the configured train seats.");
            int[] reserved = Apportion(coachSeatCapacities, throughPassengers);
            var available = new int[coachSeatCapacities.Length];
            for (int i = 0; i < available.Length; ++i) available[i] = coachSeatCapacities[i] - reserved[i];
            int[] loads = Apportion(available, demand);
            var result = new List<DecisionSeatBlock>();
            for (int coach = 0; coach < loads.Length; ++coach)
            {
                int load = loads[coach];
                if (load == 0) continue;
                int actors = Math.Max(1, Math.Min(load, (int)Math.Ceiling(Math.Round(load * visualActorRatio, 6))));
                int nextSeat = reserved[coach] + 1;
                for (int actor = 0; actor < actors; ++actor)
                {
                    int represented = load / actors + (actor < load % actors ? 1 : 0);
                    var seats = new int[represented];
                    for (int seat = 0; seat < seats.Length; ++seat) seats[seat] = nextSeat++;
                    result.Add(new DecisionSeatBlock(coach + 1, seats));
                }
            }
            return result;
        }

        // Largest-remainder proportional allocation conserves every passenger and respects each coach's seats.
        static int[] Apportion(int[] capacities, int demand)
        {
            var loads = new int[capacities.Length];
            if (demand == 0) return loads;
            long total = 0; foreach (int capacity in capacities) total += capacity;
            if (total == 0 || demand > total) throw new ArgumentException("No available seats for the requested load.");
            var remainder = new long[capacities.Length]; int assigned = 0;
            for (int i = 0; i < capacities.Length; ++i)
            {
                long numerator = (long)demand * capacities[i];
                loads[i] = (int)(numerator / total); remainder[i] = numerator % total; assigned += loads[i];
            }
            while (assigned < demand)
            {
                int best = -1;
                for (int i = 0; i < capacities.Length; ++i)
                    if (loads[i] < capacities[i] && (best < 0 || remainder[i] > remainder[best])) best = i;
                if (best < 0) throw new InvalidOperationException("Seat apportionment exhausted its capacity.");
                ++loads[best]; remainder[best] = -1; ++assigned;
            }
            return loads;
        }
    }
}
