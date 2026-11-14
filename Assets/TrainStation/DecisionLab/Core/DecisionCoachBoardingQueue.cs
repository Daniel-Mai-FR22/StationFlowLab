using System;
using System.Collections.Generic;

namespace StationFlow.TrainStation.DecisionLab
{
    public enum DecisionCoachQueueProgress { None, Started, Ready }
    public sealed class DecisionCoachQueueTicket
    {
        public string journeyId, ticketId;
        public int representedPassengers;
    }

    /// <summary>One coach door admits FIFO seat blocks, with time charged for every represented passenger.</summary>
    public sealed class DecisionCoachBoardingQueue
    {
        readonly List<DecisionCoachQueueTicket> waiting = new List<DecisionCoachQueueTicket>();
        readonly Dictionary<string, string> ticketOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        string boardingJourney;
        double previousTime, servedSeconds;
        bool ready;
        public int Count => waiting.Count;
        public string HeadJourneyId => Count > 0 ? waiting[0].journeyId : null;
        public string BoardingJourneyId => boardingJourney;
        public bool BoardingStarted => boardingJourney != null;
        public double ServedSeconds => servedSeconds;
        public DecisionCoachQueueTicket[] Tickets => waiting.ToArray();
        public int Position(string journeyId) => waiting.FindIndex(t => t.journeyId == journeyId);
        public bool CanBoard(string journeyId) => ready && journeyId != null && HeadJourneyId == journeyId && boardingJourney == journeyId;

        public bool Join(DecisionCoachQueueTicket ticket, int capacity)
        {
            if (ticket == null || string.IsNullOrWhiteSpace(ticket.journeyId) || string.IsNullOrWhiteSpace(ticket.ticketId) ||
                ticket.representedPassengers < 1 || capacity < 1) return false;
            int existing = Position(ticket.journeyId);
            if (existing >= 0) return waiting[existing].ticketId == ticket.ticketId && waiting[existing].representedPassengers == ticket.representedPassengers;
            if (Count >= capacity || ticketOwners.TryGetValue(ticket.ticketId, out var owner) && owner != ticket.journeyId) return false;
            ticketOwners[ticket.ticketId] = ticket.journeyId;
            waiting.Add(new DecisionCoachQueueTicket { journeyId = ticket.journeyId, ticketId = ticket.ticketId, representedPassengers = ticket.representedPassengers });
            return true;
        }

        public DecisionCoachQueueProgress Advance(double now, bool headAtDoor, bool trainEligible, double secondsPerPassenger)
        {
            if (!Finite(now) || !Finite(secondsPerPassenger) || secondsPerPassenger <= 0d) throw new ArgumentException("Boarding clock and duration must be finite.");
            if (Count == 0 || !headAtDoor || !trainEligible)
            { ResetService(); previousTime = now; return DecisionCoachQueueProgress.None; }
            if (boardingJourney != HeadJourneyId)
            { ResetService(); boardingJourney = HeadJourneyId; previousTime = now; return DecisionCoachQueueProgress.Started; }
            if (now < previousTime) { ResetService(); previousTime = now; return DecisionCoachQueueProgress.None; }
            servedSeconds = Math.Min(waiting[0].representedPassengers * secondsPerPassenger, servedSeconds + now - previousTime); previousTime = now;
            if (!ready && servedSeconds >= waiting[0].representedPassengers * secondsPerPassenger)
            { ready = true; return DecisionCoachQueueProgress.Ready; }
            return DecisionCoachQueueProgress.None;
        }

        public bool Remove(string journeyId)
        {
            int index = Position(journeyId);
            if (index < 0) return false;
            waiting.RemoveAt(index);
            if (index == 0) ResetService();
            return true;
        }

        public double EstimatedWait(string journeyId, int unjoinedPassengers, double secondsPerPassenger)
        {
            if (!Finite(secondsPerPassenger) || secondsPerPassenger <= 0d) return double.PositiveInfinity;
            int index = Position(journeyId), end = index >= 0 ? index : Count - 1;
            double seconds = index >= 0 ? 0d : Math.Max(1, unjoinedPassengers) * secondsPerPassenger;
            for (int i = 0; i <= end; ++i) seconds += waiting[i].representedPassengers * secondsPerPassenger;
            return Math.Max(0d, seconds - servedSeconds);
        }

        void ResetService() { boardingJourney = null; servedSeconds = 0d; ready = false; }
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
