using System;
using System.Collections.Generic;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Opening delay begins when the traveller reaches the control, after preceding activities and walking.</summary>
    public static class DecisionAccessForecast
    {
        public static double WaitingSeconds(double now, double opensAt, double secondsUntilArrival, double queueSeconds)
        {
            if (!DecisionPlatformAccess.Finite(now) || !DecisionPlatformAccess.Finite(opensAt) ||
                !DecisionPlatformAccess.Finite(secondsUntilArrival) || secondsUntilArrival < 0 ||
                !DecisionPlatformAccess.Finite(queueSeconds) || queueSeconds < 0)
                return double.PositiveInfinity;
            double arrival = now + secondsUntilArrival;
            if (!DecisionPlatformAccess.Finite(arrival)) return double.PositiveInfinity;
            return Math.Max(0, opensAt - arrival) + queueSeconds;
        }
    }

    public enum DecisionPlatformAccessStatus
    {
        Open, BeforeWindow, TrainAbsent, WrongDate, WrongTrack, WrongService, InvalidTicket,
        Cancelled, Closed, Departed
    }

    /// <summary>Physical admission and advancement rules shared by native arrival queues.</summary>
    public static class DecisionPhysicalQueueArrival
    {
        public static bool CanRegister(int expectedSlot, int currentSlot, double distanceToSlot, double heightDifference,
            bool hasPredecessor, double predecessorDistance, double arrivalTolerance, double minimumSeparation)
        {
            return expectedSlot >= 0 && expectedSlot == currentSlot &&
                DecisionPlatformAccess.Finite(distanceToSlot) && distanceToSlot >= 0 &&
                DecisionPlatformAccess.Finite(heightDifference) && Math.Abs(heightDifference) <= .15 &&
                DecisionPlatformAccess.Finite(arrivalTolerance) && arrivalTolerance > 0 && distanceToSlot <= arrivalTolerance &&
                DecisionPlatformAccess.Finite(minimumSeparation) && minimumSeparation > 0 &&
                (!hasPredecessor || DecisionPlatformAccess.Finite(predecessorDistance) && predecessorDistance >= minimumSeparation);
        }

        public static bool CanAdvance(int physicalSlot, int logicalIndex, double ownDistanceToSlot, bool hasPredecessor,
            double predecessorProgress, double predecessorDistanceToCandidate, double arrivalTolerance, double minimumSeparation)
        {
            return logicalIndex >= 0 && physicalSlot > logicalIndex &&
                DecisionPlatformAccess.Finite(ownDistanceToSlot) && ownDistanceToSlot >= 0 &&
                DecisionPlatformAccess.Finite(arrivalTolerance) && arrivalTolerance > 0 && ownDistanceToSlot <= arrivalTolerance &&
                DecisionPlatformAccess.Finite(minimumSeparation) && minimumSeparation > 0 &&
                (!hasPredecessor || DecisionPlatformAccess.Finite(predecessorProgress) && predecessorProgress >= minimumSeparation &&
                    DecisionPlatformAccess.Finite(predecessorDistanceToCandidate) && predecessorDistanceToCandidate >= minimumSeparation);
        }
    }

    [Serializable]
    public sealed class DecisionPlatformTicket
    {
        public string journeyId, ticketId, operatingDate, serviceId;
        public int trackNumber, representedPassengers = 1;
    }

    public struct DecisionPlatformAccessView
    {
        public string operatingDate, serviceId;
        public int trackNumber;
        public double now, departure, cutoff, leadSeconds;
        public bool trainPresent;
        public DecisionTrainStatus status;
    }

    /// <summary>Ticket identity and the access window are independent of the entrance-spawn plan.</summary>
    public static class DecisionPlatformAccess
    {
        public static DecisionPlatformAccessStatus Evaluate(DecisionPlatformTicket ticket, DecisionPlatformAccessView view)
        {
            if (ticket == null || string.IsNullOrWhiteSpace(ticket.journeyId) || string.IsNullOrWhiteSpace(ticket.ticketId) ||
                string.IsNullOrWhiteSpace(ticket.serviceId) || string.IsNullOrWhiteSpace(ticket.operatingDate) || ticket.representedPassengers < 1 ||
                string.IsNullOrWhiteSpace(view.serviceId) || string.IsNullOrWhiteSpace(view.operatingDate) ||
                !Finite(view.now) || !Finite(view.departure) || !Finite(view.cutoff) ||
                !Finite(view.leadSeconds) || view.leadSeconds < 0 || view.cutoff > view.departure)
                return DecisionPlatformAccessStatus.InvalidTicket;
            if (!string.Equals(ticket.operatingDate, view.operatingDate, StringComparison.Ordinal)) return DecisionPlatformAccessStatus.WrongDate;
            if (ticket.trackNumber != view.trackNumber) return DecisionPlatformAccessStatus.WrongTrack;
            if (!string.Equals(ticket.serviceId, view.serviceId, StringComparison.Ordinal)) return DecisionPlatformAccessStatus.WrongService;
            if (view.status == DecisionTrainStatus.Cancelled) return DecisionPlatformAccessStatus.Cancelled;
            if (view.status == DecisionTrainStatus.Departed || view.now >= view.departure) return DecisionPlatformAccessStatus.Departed;
            if (view.status == DecisionTrainStatus.Closed || view.now >= view.cutoff) return DecisionPlatformAccessStatus.Closed;
            if (view.now < view.departure - view.leadSeconds) return DecisionPlatformAccessStatus.BeforeWindow;
            return view.trainPresent ? DecisionPlatformAccessStatus.Open : DecisionPlatformAccessStatus.TrainAbsent;
        }

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>One physical scanner, immutable journey identities, and FIFO admission without frame-rate-dependent throughput.</summary>
    public sealed class DecisionPlatformAccessQueue
    {
        readonly List<DecisionPlatformTicket> waiting = new List<DecisionPlatformTicket>();
        readonly HashSet<string> scannedTickets = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> passed = new HashSet<string>(StringComparer.Ordinal);
        string scanningJourney, crossingJourney;
        double scanStarted;
        public int Count => waiting.Count;
        public string HeadJourney => waiting.Count == 0 ? null : waiting[0].journeyId;
        public string ScanningJourney => scanningJourney;
        public string CrossingJourney => crossingJourney;
        public bool ScannerBusy => scanningJourney != null || crossingJourney != null;
        public DecisionPlatformTicket[] Tickets => waiting.ToArray();

        public int Position(string journeyId) => waiting.FindIndex(t => t.journeyId == journeyId);
        public bool HasPassed(string journeyId) => journeyId != null && passed.Contains(journeyId);
        public bool CanCross(string journeyId) => journeyId != null && crossingJourney == journeyId;

        public bool Join(DecisionPlatformTicket ticket, DecisionPlatformAccessView view, int capacity, out DecisionPlatformAccessStatus status)
        {
            status = DecisionPlatformAccess.Evaluate(ticket, view);
            if (status != DecisionPlatformAccessStatus.Open || capacity < 1) return false;
            if (HasPassed(ticket.journeyId) || CanCross(ticket.journeyId) || Position(ticket.journeyId) >= 0) return true;
            if (waiting.Count >= capacity || scannedTickets.Contains(ticket.ticketId) || waiting.Exists(t => t.ticketId == ticket.ticketId))
            { status = DecisionPlatformAccessStatus.InvalidTicket; return false; }
            waiting.Add(ticket);
            return true;
        }

        public bool BeginScan(string journeyId, DecisionPlatformAccessView view)
        {
            if (ScannerBusy || waiting.Count == 0 || waiting[0].journeyId != journeyId ||
                DecisionPlatformAccess.Evaluate(waiting[0], view) != DecisionPlatformAccessStatus.Open) return false;
            scanningJourney = journeyId; scanStarted = view.now; return true;
        }

        public void CancelScan() { scanningJourney = null; }

        public bool CompleteScan(DecisionPlatformAccessView view, double scanSeconds, out DecisionPlatformTicket ticket)
        {
            ticket = null;
            if (scanningJourney == null || waiting.Count == 0 || waiting[0].journeyId != scanningJourney) return false;
            if (DecisionPlatformAccess.Evaluate(waiting[0], view) != DecisionPlatformAccessStatus.Open)
            { scanningJourney = null; return false; }
            if (!DecisionPlatformAccess.Finite(scanSeconds) || scanSeconds < 0 || view.now - scanStarted < scanSeconds) return false;
            ticket = waiting[0]; waiting.RemoveAt(0);
            scannedTickets.Add(ticket.ticketId); crossingJourney = scanningJourney; scanningJourney = null;
            return true;
        }

        public bool Crossed(string journeyId)
        {
            if (!CanCross(journeyId)) return false;
            passed.Add(journeyId); crossingJourney = null; return true;
        }

        public bool Remove(string journeyId)
        {
            bool removed = waiting.RemoveAll(t => t.journeyId == journeyId) > 0;
            if (scanningJourney == journeyId) { scanningJourney = null; removed = true; }
            if (crossingJourney == journeyId) { crossingJourney = null; removed = true; }
            return removed;
        }

        public double EstimatedWait(string journeyId, double scanSecondsPerActor, double crossingSeconds)
        {
            if (HasPassed(journeyId) || CanCross(journeyId)) return 0;
            int index = Position(journeyId);
            int ahead = index < 0 ? waiting.Count : index;
            double wait = Math.Max(0, scanSecondsPerActor);
            for (int i = 0; i < ahead; ++i) wait += Math.Max(0, scanSecondsPerActor + crossingSeconds);
            return wait + (crossingJourney != null ? Math.Max(0, crossingSeconds) : 0);
        }
    }
}
