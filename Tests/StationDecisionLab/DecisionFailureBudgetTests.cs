using System;
using System.Linq;
using StationFlow.TrainStation.DecisionLab;

static class DecisionFailureBudgetTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("clock and temporary exit capacity waits cannot exhaust the no-route budget", CapacityWaitThenRelease);
        run("disabled unavailable destinations and unreachable capacity cannot wait forever", BrokenDestinations);
        run("a committed action or recycled journey resets consecutive failure accounting", ResetAfterProgress);
        run("capacity recovery respects mandatory information and valid time estimates", EligibleRecovery);
    }
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static DecisionContext Context(double now = 253.419, double cutoff = 180) => new DecisionContext {
        nowSeconds = now, boardingCutoffSeconds = cutoff, trainStatus = DecisionTrainStatus.Closed,
        platformKnown = false, requireInitialDisplay = true, safetyMarginSeconds = 12
    };
    static DecisionOption Exit(bool full = true, bool reachable = true) => new DecisionOption {
        activity = DecisionActivity.Exit, poiId = "exit-hall", travelSeconds = .7623,
        available = false, temporarilyFull = full, reachable = reachable
    };
    static bool Capacity(DecisionContext context, DecisionOption option) =>
        DecisionFailureBudget.HasTemporaryCapacityRecovery(context, StationDecisionPolicy.Evaluate(context, new[] { option }));
    static void CapacityWaitThenRelease()
    {
        var budget = new DecisionFailureBudget();
        for (int i = 0; i < 40; ++i) Check(!budget.RecordNoChoice(true), "Dated pre-cutoff wait consumed failure budget.");
        var context = Context(); var exit = Exit();
        Check(Capacity(context, exit), "Recorded reachable but full exit was mistaken for a broken route.");
        for (int i = 0; i < 41; ++i) Check(!budget.RecordNoChoice(Capacity(context, exit)), "Crossing the old global 80-decision limit failed a legitimate exit wait.");
        Check(budget.ConsecutiveFailures == 0, "Temporary capacity wait accumulated no-route failures.");
        exit.available = true; exit.temporarilyFull = false;
        var chosen = StationDecisionPolicy.Choose(StationDecisionPolicy.Evaluate(context, new[] { exit }), new Random(1));
        Check(chosen != null && chosen.option.activity == DecisionActivity.Exit, "Free exit capacity did not permit actual replanning.");
        budget.Reset(); Check(budget.ConsecutiveFailures == 0, "Exit commitment retained old retry failures.");
    }
    static void BrokenDestinations()
    {
        var context = Context();
        foreach (var option in new[] { Exit(false), Exit(true, false) })
        {
            Check(!Capacity(context, option), "Disabled or unreachable destination invented recoverable capacity.");
            var budget = new DecisionFailureBudget();
            for (int i = 0; i < 80; ++i) Check(!budget.RecordNoChoice(Capacity(context, option)), "Bounded failure occurred before 80 consecutive attempts.");
            Check(budget.RecordNoChoice(false), "A destination with no recovery remained active forever.");
        }
    }
    static void ResetAfterProgress()
    {
        var budget = new DecisionFailureBudget();
        for (int i = 0; i < 80; ++i) budget.RecordNoChoice(false);
        budget.Reset(); Check(!budget.RecordNoChoice(false) && budget.ConsecutiveFailures == 1, "Commit or recycle used a previous journey's failure counter.");
        budget.RecordNoChoice(true); Check(budget.ConsecutiveFailures == 0, "An observed recovery wait did not reset consecutive failures.");
    }
    static void EligibleRecovery()
    {
        var context = Context(10, 300); context.trainStatus = DecisionTrainStatus.Scheduled;
        var display = new DecisionOption { activity = DecisionActivity.Display, temporarilyFull = true, available = false,
            travelSeconds = 30, durationSeconds = 3, toPlatformSeconds = 100 };
        Check(Capacity(context, display), "A feasible required display did not wait for its occupied places.");
        Check(!Capacity(context, Exit()), "Initial information rule was bypassed by exit capacity.");
        display.toPlatformSeconds = 1000; Check(!Capacity(context, display), "Insufficient activity budget invented recoverable information capacity.");
        display.toPlatformSeconds = double.PositiveInfinity; Check(!Capacity(context, display), "Invalid native estimate invented recovery.");
        context = Context(); var shop = new DecisionOption { activity = DecisionActivity.Shop, temporarilyFull = true, available = false };
        Check(!Capacity(context, shop), "Optional shopping masked failed exit or help routes after cutoff.");
        context.trainStatus = (DecisionTrainStatus)999;
        Check(!Capacity(context, Exit()), "Invalid train context invented recoverable capacity.");
    }
}
