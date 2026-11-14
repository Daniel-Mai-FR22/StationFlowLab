using System;
using StationFlow.TrainStation.DecisionLab;

static class PlatformAccessTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("platform access requires assigned train and finite opening/cutoff window", Window);
        run("ticket scanner rejects wrong date, track, trip and cancelled service", TicketIdentity);
        run("scanner enforces FIFO and one-second service per visible actor", Fifo);
        run("a cutoff cancels the scan but cannot revoke an already granted traversal", Closing);
        run("a scanned ticket cannot be reused by a different journey", DuplicateTicket);
        run("queue estimates count visible visits and an occupied crossing", ActorBudget);
        run("leaving or displacement resets only the scan and preserves the remaining FIFO", Cleanup);
        run("retiming updates access window without changing ticket identity", Retiming);
        run("a later request physically arriving first registers before a remote request", PhysicalArrival);
        run("physical admission rejects a stale slot, a close predecessor and another floor", AdmissionGuards);
        run("a logical queue vacancy cannot advance into an occupied physical marker", PhysicalProgress);
        run("ticket-cohort weight does not multiply the visible actor scan", EqualScan);
    }

    static DecisionPlatformTicket Ticket(string journey = "A", string ticket = null, int weight = 10) => new DecisionPlatformTicket {
        journeyId = journey, ticketId = ticket ?? "ticket-" + journey, serviceId = "S", operatingDate = "2026-10-06", trackNumber = 2,
        representedPassengers = weight };
    static DecisionPlatformAccessView View(double now = 0) => new DecisionPlatformAccessView { operatingDate = "2026-10-06", serviceId = "S",
        trackNumber = 2, now = now, departure = 1000, cutoff = 880, leadSeconds = 1200, trainPresent = true, status = DecisionTrainStatus.Boarding };
    static void Check(bool yes, string message) { if (!yes) throw new InvalidOperationException(message); }
    static void Equal(DecisionPlatformAccessStatus expected, DecisionPlatformTicket ticket, DecisionPlatformAccessView view) =>
        Check(DecisionPlatformAccess.Evaluate(ticket, view) == expected, "Unexpected ticket access status: " + expected);
    static void Window()
    {
        var ticket = Ticket(); var view = View(-201);
        Equal(DecisionPlatformAccessStatus.BeforeWindow, ticket, view);
        view.now = -200; Equal(DecisionPlatformAccessStatus.Open, ticket, view);
        view.trainPresent = false; Equal(DecisionPlatformAccessStatus.TrainAbsent, ticket, view);
        view.trainPresent = true; view.now = 879.999; Equal(DecisionPlatformAccessStatus.Open, ticket, view);
        view.now = 880; Equal(DecisionPlatformAccessStatus.Closed, ticket, view);
        view.now = 1000; Equal(DecisionPlatformAccessStatus.Departed, ticket, view);
        view.now = double.NaN; Equal(DecisionPlatformAccessStatus.InvalidTicket, ticket, view);
    }
    static void TicketIdentity()
    {
        var view = View(); var ticket = Ticket(); ticket.operatingDate = "2026-10-05";
        Equal(DecisionPlatformAccessStatus.WrongDate, ticket, view);
        ticket = Ticket(); ticket.trackNumber = 4; Equal(DecisionPlatformAccessStatus.WrongTrack, ticket, view);
        ticket = Ticket(); ticket.serviceId = "Other"; Equal(DecisionPlatformAccessStatus.WrongService, ticket, view);
        ticket = Ticket(); ticket.ticketId = ""; Equal(DecisionPlatformAccessStatus.InvalidTicket, ticket, view);
        ticket = Ticket(); view.status = DecisionTrainStatus.Cancelled; Equal(DecisionPlatformAccessStatus.Cancelled, ticket, view);
    }
    static void Fifo()
    {
        var queue = new DecisionPlatformAccessQueue(); var view = View();
        Check(queue.Join(Ticket("A", weight: 10), view, 2, out _), "First ticket rejected.");
        Check(queue.Join(Ticket("B", weight: 3), view, 2, out _), "Second ticket rejected.");
        Check(!queue.Join(Ticket("C"), view, 2, out _), "Queue capacity exceeded.");
        Check(!queue.BeginScan("B", view), "A later arrival jumped the queue.");
        Check(!queue.CompleteScan(view, 1, out _), "A non-arrived head scanned itself.");
        Check(queue.BeginScan("A", view), "Head scan failed.");
        view.now = .99; Check(!queue.CompleteScan(view, 1, out _), "Scan completed before one second.");
        view.now = 1; Check(queue.CompleteScan(view, 1, out var ticket) && ticket.journeyId == "A", "One visible scan did not complete.");
        Check(!queue.BeginScan("B", view), "Second ticket scanned while the lane was occupied.");
        Check(!queue.Crossed("B"), "Another journey consumed the granted token.");
        Check(queue.Crossed("A") && queue.HasPassed("A"), "Physical crossing did not release the scanner.");
        Check(queue.BeginScan("B", view), "Next FIFO ticket did not advance.");
        view.now = 1.99; Check(!queue.CompleteScan(view, 1, out _), "Second scan completed too quickly.");
        view.now = 2; Check(queue.CompleteScan(view, 1, out _), "Second actor scan failed.");
    }
    static void Closing()
    {
        var queue = new DecisionPlatformAccessQueue(); var view = View(870);
        queue.Join(Ticket("A"), view, 10, out _); queue.BeginScan("A", view);
        view.now = 880; Check(!queue.CompleteScan(view, 1, out _) && queue.ScanningJourney == null, "A scan completed at cutoff.");
        view = View(); queue.BeginScan("A", view); view.now = 1;
        Check(queue.CompleteScan(view, 1, out _), "Fixture grant failed.");
        view.status = DecisionTrainStatus.Cancelled; view.now = 880;
        Check(queue.CanCross("A") && queue.Crossed("A"), "An existing native traversal was revoked mid-link.");
        Check(!queue.Join(Ticket("B"), view, 10, out _), "Cancellation allowed a new entrant.");
    }
    static void DuplicateTicket()
    {
        var queue = new DecisionPlatformAccessQueue(); var view = View();
        queue.Join(Ticket("A", "same"), view, 10, out _);
        Check(!queue.Join(Ticket("B", "same"), view, 10, out _), "A queued ticket was duplicated.");
        queue.BeginScan("A", view); view.now = 1; queue.CompleteScan(view, 1, out _); queue.Crossed("A");
        Check(!queue.Join(Ticket("B", "same"), view, 10, out _), "A scanned ticket was reused.");
        Check(queue.HasPassed("A") && !queue.HasPassed("B"), "Actor reuse changed the journey ledger.");
    }
    static void ActorBudget()
    {
        var queue = new DecisionPlatformAccessQueue(); var view = View();
        queue.Join(Ticket("A", weight: 10), view, 10, out _); queue.Join(Ticket("B", weight: 3), view, 10, out _);
        Check(Math.Abs(queue.EstimatedWait("B", 1, 3.5) - 5.5) < .001, "Waiting budget did not count the two visible actors.");
        queue.BeginScan("A", view); view.now = 1; queue.CompleteScan(view, 1, out _);
        Check(Math.Abs(queue.EstimatedWait("B", 1, 3.5) - 4.5) < .001, "Occupied crossing was omitted.");
    }
    static void Cleanup()
    {
        var queue = new DecisionPlatformAccessQueue(); var view = View();
        queue.Join(Ticket("A"), view, 10, out _); queue.Join(Ticket("B"), view, 10, out _); queue.BeginScan("A", view);
        queue.CancelScan(); Check(queue.HeadJourney == "A" && !queue.ScannerBusy, "Displacement lost FIFO position.");
        queue.Remove("A"); Check(queue.HeadJourney == "B" && queue.Position("B") == 0, "Cancellation did not advance FIFO.");
        queue.BeginScan("B", view); view.now = 1; queue.CompleteScan(view, 1, out _); queue.Crossed("B"); queue.Remove("B");
        Check(queue.HasPassed("B"), "Cleanup erased the immutable access proof.");
    }
    static void Retiming()
    {
        var ticket = Ticket(); var view = View(-201); Equal(DecisionPlatformAccessStatus.BeforeWindow, ticket, view);
        view.departure = 900; view.cutoff = 780; Equal(DecisionPlatformAccessStatus.Open, ticket, view);
        Check(ticket.ticketId == "ticket-A" && ticket.serviceId == "S", "Retiming mutated ticket identity.");
    }
    static void PhysicalArrival()
    {
        var queue = new DecisionPlatformAccessQueue(); var view = View();
        Check(!DecisionPhysicalQueueArrival.CanRegister(0,0,35,0,false,0,.22,.70), "A remote request reserved the head.");
        Check(DecisionPhysicalQueueArrival.CanRegister(0,0,.10,0,false,0,.22,.70) && queue.Join(Ticket("B"),view,10,out _), "The first physical arrival could not join.");
        Check(DecisionPhysicalQueueArrival.CanRegister(1,1,.10,0,true,.95,.22,.70) && queue.Join(Ticket("A"),view,10,out _), "The later physical arrival could not join.");
        Check(queue.HeadJourney == "B" && queue.Position("A") == 1, "Request order replaced physical arrival order.");
        var future = View(); future.trainPresent = false;
        Check(!queue.Join(Ticket("future"),future,10,out _) && queue.HeadJourney == "B", "A closed future trip reserved a head position.");
    }
    static void AdmissionGuards()
    {
        Check(!DecisionPhysicalQueueArrival.CanRegister(0,1,.05,0,false,0,.22,.70), "A stale tail snapshot registered.");
        Check(!DecisionPhysicalQueueArrival.CanRegister(1,1,.05,0,true,.69,.22,.70), "An arrival overlapped its predecessor.");
        Check(!DecisionPhysicalQueueArrival.CanRegister(1,1,.05,.16,false,0,.22,.70), "An actor on another floor registered.");
        Check(!DecisionPhysicalQueueArrival.CanRegister(1,1,double.NaN,0,false,0,.22,.70), "Nonfinite arrival data registered.");
    }
    static void PhysicalProgress()
    {
        Check(!DecisionPhysicalQueueArrival.CanAdvance(2,1,.05,true,.10,.10,.12,.70), "A logical vacancy displaced an occupied tail marker.");
        Check(!DecisionPhysicalQueueArrival.CanAdvance(2,1,.30,true,.95,.95,.12,.70), "An actor advanced again before reaching its own marker.");
        Check(DecisionPhysicalQueueArrival.CanAdvance(2,1,.05,true,.80,.80,.12,.70), "A physically cleared marker could not advance.");
        Check(!DecisionPhysicalQueueArrival.CanAdvance(1,1,.05,false,0,0,.12,.70), "An actor advanced beyond its FIFO ordinal.");
    }
    static void EqualScan()
    {
        foreach(int weight in new[] {1,3,10})
        {
            var queue = new DecisionPlatformAccessQueue(); var view = View();
            queue.Join(Ticket(weight:weight),view,10,out _); queue.BeginScan("A",view);
            view.now = .999; Check(!queue.CompleteScan(view,1,out _), "An actor skipped the scanner delay.");
            view.now = 1; Check(queue.CompleteScan(view,1,out var ticket) && ticket.representedPassengers == weight,
                "Cohort weight changed the actor scan or its conserved passenger count.");
        }
    }
}
