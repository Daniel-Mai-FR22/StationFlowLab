using System;
using System.Linq;
using StationFlow.TrainStation.DecisionLab;

static class ActivityForecastTests
{
    public static void RunAll(Action<string, Action> run)
    {
        run("gate forecast overlaps preceding activity with opening delay without discarding the queue", Overlap);
        run("walking to the control consumes opening delay while future scans remain reserved", WalkingOverlap);
        run("a later train arrival or opening retains its residual wait", RetimedOpening);
        run("recorded early reading visit is feasible when opening wait uses predicted arrival", RecordedReading);
        run("longer WC use retains its onward journey and cannot steal an urgent boarding budget", ToiletBudget);
        run("WC durations replay within two to four minutes with variation", BoundedToiletDuration);
        run("invalid forecasts and duration bounds cannot invent feasible visits", InvalidInputs);
    }

    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static void Equal(double expected, double actual, string message) => Check(Math.Abs(expected - actual) < .001, message);

    static void Overlap()
    {
        Equal(610, DecisionAccessForecast.WaitingSeconds(100, 700, 0, 10), "Immediate opening delay was omitted.");
        Equal(310, DecisionAccessForecast.WaitingSeconds(100, 700, 300, 10), "Activity time was counted twice.");
        Equal(10, DecisionAccessForecast.WaitingSeconds(100, 700, 900, 10), "An elapsed opening removed the real queue estimate.");
    }

    static void WalkingOverlap()
    {
        Equal(80, DecisionAccessForecast.WaitingSeconds(100, 500, 350, 30), "Walking before the scanner did not reduce residual opening delay.");
        Equal(30, DecisionAccessForecast.WaitingSeconds(100, 500, 400, 30), "The opening boundary dropped the FIFO wait.");
        Equal(30, DecisionAccessForecast.WaitingSeconds(100, 500, 450, 30), "Reaching an open gate invented negative waiting time.");
    }

    static void RetimedOpening()
    {
        Equal(60, DecisionAccessForecast.WaitingSeconds(50, 700, 600, 10), "Future arrival did not retain a remaining closed interval.");
        Equal(260, DecisionAccessForecast.WaitingSeconds(50, 900, 600, 10), "A later arrival/opening was ignored.");
        Equal(10, DecisionAccessForecast.WaitingSeconds(50, 400, 600, 10), "An earlier opening removed scanner service time.");
    }

    static DecisionContext Context(double now, double cutoff) => new DecisionContext {
        nowSeconds = now, boardingCutoffSeconds = cutoff, platformKnown = true,
        previousActivity = DecisionActivity.Display, trainStatus = DecisionTrainStatus.Scheduled,
        safetyMarginSeconds = 15, needs = new DecisionNeeds { shopping = .7983155f, toilet = .68f }
    };

    static void RecordedReading()
    {
        // Native capture 3be3aa: early B005 at 657.895s; current-time opening wait wrongly
        // rejected a 540-second reading stay even though most of that wait elapses during it.
        var context = Context(657.8945922851562, 1980);
        const double travel = 31.94278907775879, counter = 7, seatTravel = 5.773387908935547;
        const double transitions = 10.5, reading = 540, nativeOnward = 69.57879638671875;
        double beforeOnward = travel + counter + seatTravel + transitions + reading;
        var shop = new DecisionOption { activity = DecisionActivity.Shop, poiId = "shop-relay", travelSeconds = travel,
            durationSeconds = counter, seatTravelSeconds = seatTravel, seatingTransitionSeconds = transitions,
            seatedDurationSeconds = reading, toPlatformSeconds = nativeOnward + DecisionAccessForecast.WaitingSeconds(context.nowSeconds, 1380, 0, 10) };
        var platform = new DecisionOption { activity = DecisionActivity.Platform, poiId = "platform", travelSeconds = 59.3566,
            queueSeconds = DecisionAccessForecast.WaitingSeconds(context.nowSeconds, 1380, 40, 10) };
        var original = StationDecisionPolicy.Evaluate(context, new[] { shop, platform }).First(e => e.option == shop);
        Check(original.probability == 0 && original.slackSeconds < 0, "Original native trace no longer demonstrates the lost reading opportunity.");
        shop.toPlatformSeconds = nativeOnward + DecisionAccessForecast.WaitingSeconds(context.nowSeconds, 1380, beforeOnward + 50, 10);
        var corrected = StationDecisionPolicy.Evaluate(context, new[] { shop, platform }).First(e => e.option == shop);
        Check(corrected.probability > 0 && corrected.slackSeconds > 500, "Corrected forecast still double-counted opening delay.");
        Equal(540, shop.seatedDurationSeconds, "Forecast repair shortened reading to manufacture coverage.");
    }

    static void ToiletBudget()
    {
        var context = Context(0, 700);
        var toilet = new DecisionOption { activity = DecisionActivity.Toilet, poiId = "WC", travelSeconds = 30,
            queueSeconds = 180, durationSeconds = 180,
            toPlatformSeconds = 100 + DecisionAccessForecast.WaitingSeconds(0, 400, 30 + 180 + 180 + 50, 10) };
        var platform = new DecisionOption { activity = DecisionActivity.Platform, poiId = "platform", travelSeconds = 100,
            queueSeconds = DecisionAccessForecast.WaitingSeconds(0, 400, 50, 10) };
        Check(StationDecisionPolicy.Evaluate(context, new[] { toilet, platform }).First(e => e.option == toilet).probability > 0,
            "An early journey lost a feasible three-minute WC visit and outside queue.");
        context.boardingCutoffSeconds = 220;
        var late = StationDecisionPolicy.Evaluate(context, new[] { toilet, platform });
        Check(late.First(e => e.option == toilet).probability == 0, "A long WC visit bypassed the train deadline.");
        Check(toilet.queueSeconds == 180 && toilet.durationSeconds == 180, "Timing changed demand or service length.");
    }

    static void BoundedToiletDuration()
    {
        var first = new Random(20261006); var replay = new Random(20261006);
        float minimum = 240, maximum = 120; double sum = 0;
        for (int i = 0; i < 10000; ++i)
        {
            float sample = StationDecisionPolicy.SampleBoundedDuration(first, 180, 60, 120, 240);
            Equal(sample, StationDecisionPolicy.SampleBoundedDuration(replay, 180, 60, 120, 240), "WC duration did not replay.");
            Check(sample >= 120 && sample <= 240 && !float.IsNaN(sample), "WC visit violated its two-to-four-minute bounds.");
            minimum = Math.Min(minimum, sample); maximum = Math.Max(maximum, sample); sum += sample;
        }
        Check(minimum < 125 && maximum > 235 && sum / 10000 > 178 && sum / 10000 < 182,
            "WC distribution lost its variation or three-minute center.");
        Equal(180, StationDecisionPolicy.SampleBoundedDuration(first, 180, 0, 120, 240), "Zero spread changed the configured duration.");
    }

    static void InvalidInputs()
    {
        Check(double.IsPositiveInfinity(DecisionAccessForecast.WaitingSeconds(0, 400, double.NaN, 10)), "NaN arrival manufactured zero wait.");
        Check(double.IsPositiveInfinity(DecisionAccessForecast.WaitingSeconds(0, 400, -1, 10)), "Negative travel manufactured a forecast.");
        Check(double.IsPositiveInfinity(DecisionAccessForecast.WaitingSeconds(0, 400, 1, -10)), "Negative queue time was accepted.");
        bool rejected = false;
        try { StationDecisionPolicy.SampleBoundedDuration(new Random(1), 180, 60, 240, 120); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Inverted visit bounds were accepted.");
    }
}
