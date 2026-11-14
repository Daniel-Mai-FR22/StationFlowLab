using System;
using StationFlow.TrainStation.DecisionLab;

static class ToiletQueueTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("WC FIFO reserves two exclusive places and cannot overtake the physical head", FifoAndCapacity);
        run("WC completion retains capacity until physical departure clears its place", PhysicalRelease);
        run("WC interruption removes only its own waiting or occupied journey", InterruptedCleanup);
        run("WC waiting estimate schedules predecessors across two remaining use durations", RemainingBudget);
        run("WC reservations and start clocks reject malformed or duplicate transitions", InvalidAndIdempotent);
    }
    static void Check(bool yes, string message) { if (!yes) throw new InvalidOperationException(message); }
    static void FifoAndCapacity()
    {
        var queue = new DecisionToiletQueue(2);
        Check(!queue.TryReserve(null, true, new[] { true, true }, out _), "Empty WC head invented an anonymous use lease.");
        Check(queue.Join("A", 16) && queue.Join("B", 16) && queue.Join("C", 16), "Valid WC arrivals were refused.");
        Check(!queue.TryReserve("B", true, new[] { true, true }, out _), "A later arrival overtook the WC head.");
        Check(!queue.TryReserve("A", false, new[] { true, true }, out _), "A remote head reserved a WC place.");
        Check(queue.TryReserve("A", true, new[] { false, true }, out int first) && first == 1, "Physically occupied use point was reserved.");
        Check(queue.TryReserve("B", true, new[] { true, true }, out int second) && second == 0, "Second clear WC place was refused.");
        Check(!queue.TryReserve("C", true, new[] { true, true }, out _) && queue.ActiveUseCount == 2 && queue.HeadJourneyId == "C", "WC capacity exceeded two.");
    }
    static void PhysicalRelease()
    {
        var queue = new DecisionToiletQueue(1); queue.Join("A", 16); queue.Join("B", 16);
        queue.TryReserve("A", true, new[] { true }, out _); queue.MarkStarted("A", 10d, 180d);
        Check(!queue.HasFinishedDuration("A", 189.99d) && queue.HasFinishedDuration("A", 190d), "WC dwell ended before its exact sampled duration.");
        Check(queue.BeginRelease("A") && queue.ActiveUseCount == 1, "Completion freed the still-occupied WC point.");
        Check(!queue.ReleaseCleared("A", false) && !queue.TryReserve("B", true, new[] { true }, out _), "A follower entered before physical clearance.");
        Check(queue.ReleaseCleared("A", true) && queue.TryReserve("B", true, new[] { true }, out _), "A clear departing WC lease prevented the next head.");
    }
    static void InterruptedCleanup()
    {
        var queue = new DecisionToiletQueue(2); queue.Join("A", 16); queue.Join("B", 16); queue.Join("C", 16);
        Check(queue.RemoveWaiting("B") && queue.Position("C") == 1 && queue.HeadJourneyId == "A", "Removing a cancelled WC wait changed unrelated FIFO order.");
        queue.TryReserve("A", true, new[] { true, true }, out _);
        Check(queue.BeginRelease("A") && !queue.ReleaseCleared("A", false), "Interrupted occupied WC lease vanished before departure.");
        Check(queue.TryReserve("C", true, new[] { true, true }, out int use) && use == 1, "A cancelled first use blocked an independent free place.");
        Check(queue.ReleaseCleared("A", true) && queue.HasUse("C"), "Interrupted cleanup released another user's WC place.");
    }
    static void RemainingBudget()
    {
        var queue = new DecisionToiletQueue(2); queue.Join("A", 16); queue.Join("B", 16);
        queue.TryReserve("A", true, new[] { true, true }, out _); queue.TryReserve("B", true, new[] { true, true }, out _);
        queue.MarkStarted("A", 0d, 180d); queue.MarkStarted("B", 0d, 240d);
        queue.Join("C", 16); queue.Join("D", 16); queue.Join("E", 16);
        Check(Math.Abs(queue.EstimatedWait("C", 120d, 180d, 4d) - 64d) < 1e-8, "WC head ignored remaining occupied durations.");
        Check(Math.Abs(queue.EstimatedWait("D", 120d, 180d, 4d) - 128d) < 1e-8, "WC follower was not allocated to the next of two places.");
        Check(Math.Abs(queue.EstimatedWait("E", 120d, 180d, 4d) - 252d) < 1e-8, "WC second wave ignored the earlier queued use.");
        Check(queue.EstimatedWait("A", 120d, 180d, 4d) == 0d, "Already-reserved use charged itself another queue wait.");
    }
    static void InvalidAndIdempotent()
    {
        var queue = new DecisionToiletQueue(2);
        Check(!queue.Join("", 16) && !queue.Join("A", 0), "Invalid WC identities or wait capacity were accepted.");
        Check(queue.Join("A", 1) && queue.Join("A", 1) && !queue.Join("B", 1) && queue.Count == 1, "WC retry duplicated arrival or exceeded queue slots.");
        Check(!queue.TryReserve("A", true, new[] { true }, out _), "Malformed resource clearance state reserved a place.");
        Check(queue.TryReserve("A", true, new[] { true, true }, out int first) && queue.TryReserve("A", false, new[] { false, false }, out int again) && first == again && queue.ActiveUseCount == 1, "Reservation retry consumed another use place.");
        Check(!queue.Join("A", 16) && !queue.MarkStarted("A", double.NaN, 180d) && !queue.MarkStarted("A", 0d, -1d), "An occupied user rejoined or supplied an invalid clock.");
        Check(queue.MarkStarted("A", 0d, 180d) && !queue.MarkStarted("A", 5d, 180d), "Duplicate start reset the WC dwell clock.");
        Check(!queue.ReleaseCleared("A", true), "A still-active WC lease released without ending its use.");
        Check(queue.BeginRelease("A") && !queue.BeginRelease("A") && !queue.MarkStarted("A", 5d, 180d), "Releasing WC lease restarted or double-completed.");
        Check(double.IsPositiveInfinity(queue.EstimatedWait("B", double.NaN, 180d, 4d)), "Invalid wait estimate invented a finite WC budget.");
    }
}
