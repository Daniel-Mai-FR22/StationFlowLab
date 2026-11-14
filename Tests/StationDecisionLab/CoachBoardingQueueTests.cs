using System;
using StationFlow.TrainStation.DecisionLab;

static class CoachBoardingQueueTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("coach doors board FIFO blocks and charge every represented passenger", WeightedBoarding);
        run("coach boarding requires the head physically at an open eligible door", PhysicalDoorEligibility);
        run("a completed door service cannot survive cutoff closure or physical displacement", ClosedOrDisplacedHead);
        run("removing the head never transfers its earned boarding time to a follower", HeadCancellation);
        run("coach queues preserve immutable ticket ownership across actor reuse", ImmutableQueueTickets);
        run("queue capacity and time estimates remain bounded and pause with the station clock", BoundedCapacityAndPause);
    }
    static DecisionCoachQueueTicket Ticket(string id, int represented = 1) => new DecisionCoachQueueTicket { journeyId = id, ticketId = "ticket/" + id, representedPassengers = represented };
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    static void WeightedBoarding()
    {
        var queue = new DecisionCoachBoardingQueue();
        Check(queue.Join(Ticket("A", 10), 12) && queue.Join(Ticket("B", 7), 12), "Valid door tickets could not queue.");
        Check(queue.Advance(0, true, true, 1) == DecisionCoachQueueProgress.Started, "The physical head did not begin boarding.");
        queue.Advance(9.999, true, true, 1);
        Check(!queue.CanBoard("A") && !queue.CanBoard("B"), "An actor hid before its represented passenger block boarded.");
        Check(queue.Advance(10, true, true, 1) == DecisionCoachQueueProgress.Ready && queue.CanBoard("A") && !queue.CanBoard("B"), "FIFO did not admit exactly the completed head.");
        Check(queue.EstimatedWait("B", 7, 1) == 7, "The follower estimate double-counted completed head boarding.");
        queue.Advance(100, true, true, 1);
        Check(queue.EstimatedWait("B", 7, 1) == 7, "A head waiting for final reservation consumed its follower's service budget.");
        queue.Remove("A"); queue.Advance(100, true, true, 1); queue.Advance(106.999, true, true, 1);
        Check(!queue.CanBoard("B"), "Follower inherited an earlier actor's admission.");
        queue.Advance(107, true, true, 1); Check(queue.CanBoard("B"), "Follower never received its own completed admission.");
    }
    static void PhysicalDoorEligibility()
    {
        var queue = new DecisionCoachBoardingQueue(); queue.Join(Ticket("A", 2), 12);
        queue.Advance(0, false, true, 1); queue.Advance(100, false, true, 1);
        Check(!queue.BoardingStarted && !queue.CanBoard("A"), "Waiting elsewhere consumed coach-door service.");
        queue.Advance(100, true, false, 1); queue.Advance(200, true, false, 1);
        Check(!queue.BoardingStarted, "Closed doors consumed boarding service.");
        queue.Advance(200, true, true, 1); queue.Advance(201.999, true, true, 1);
        Check(!queue.CanBoard("A"), "An absent train's earlier time was credited at its door.");
        queue.Advance(202, true, true, 1); Check(queue.CanBoard("A"), "Open native door did not complete the block.");
    }
    static void ClosedOrDisplacedHead()
    {
        foreach (bool physical in new[] { true, false })
        {
            var queue = new DecisionCoachBoardingQueue(); queue.Join(Ticket("A"), 12);
            queue.Advance(0, true, true, 1); queue.Advance(1, true, true, 1); Check(queue.CanBoard("A"), "Fixture never completed.");
            queue.Advance(2, physical, !physical, 1);
            Check(!queue.CanBoard("A") && !queue.BoardingStarted, "A closed or displaced head retained stale boarding permission.");
            queue.Advance(10, true, true, 1); queue.Advance(10.999, true, true, 1);
            Check(!queue.CanBoard("A"), "Reapproach retained old served time.");
        }
    }
    static void HeadCancellation()
    {
        var queue = new DecisionCoachBoardingQueue(); queue.Join(Ticket("A", 10), 12); queue.Join(Ticket("B", 2), 12); queue.Join(Ticket("C"), 12);
        queue.Advance(0, true, true, 1); queue.Advance(9, true, true, 1);
        queue.Remove("C"); Check(queue.ServedSeconds == 9, "Removing an unrelated tail cancelled native head progress.");
        queue.Remove("A"); Check(queue.HeadJourneyId == "B" && !queue.BoardingStarted && queue.ServedSeconds == 0, "Removed head left stale admission time.");
        queue.Advance(100, true, true, 1); queue.Advance(101, true, true, 1); Check(!queue.CanBoard("B"), "A follower boarded in its predecessor's elapsed window.");
    }
    static void ImmutableQueueTickets()
    {
        var queue = new DecisionCoachBoardingQueue(); var original = Ticket("journey1", 3);
        Check(queue.Join(original, 12), "Valid ticket rejected."); original.representedPassengers = 1;
        Check(queue.Tickets[0].representedPassengers == 3, "The queue retained a mutable caller ticket reference.");
        Check(!queue.Join(original, 12), "A queued journey silently changed its represented load.");
        var reuse = Ticket("journey2"); reuse.ticketId = "ticket/journey1";
        Check(!queue.Join(reuse, 12), "One concrete ticket was reused by another journey.");
        queue.Remove("journey1"); Check(!queue.Join(reuse, 12), "Released actor slots forgot concrete ticket ownership.");
        Check(queue.Join(Ticket("journey2"), 12), "A recycled actor could not acquire a different journey's valid ticket.");
    }
    static void BoundedCapacityAndPause()
    {
        var queue = new DecisionCoachBoardingQueue();
        Check(queue.Join(Ticket("A", 5), 2) && queue.Join(Ticket("B", 3), 2) && !queue.Join(Ticket("C"), 2), "A queue exceeded its physical saved slots.");
        queue.Advance(0, true, true, 1); queue.Advance(2, true, true, 1);
        for (int frame = 0; frame < 1000; ++frame) queue.Advance(2, true, true, 1);
        Check(queue.ServedSeconds == 2 && !queue.CanBoard("A"), "Paused station clock advanced coach boarding.");
        Check(queue.EstimatedWait("B", 3, 1) == 6 && queue.EstimatedWait("C", 4, 1) == 10, "Wait budget omitted represented passengers ahead or the requested block.");
        queue.Advance(3, true, false, 1);
        Check(queue.EstimatedWait("B", 3, 1) == 8, "Closed service retained provisional door progress in the estimate.");
    }
}
