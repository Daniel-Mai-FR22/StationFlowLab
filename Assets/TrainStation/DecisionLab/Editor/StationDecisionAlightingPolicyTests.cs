using System;
using System.Collections.Generic;
using System.Linq;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Pure tests shared by the Unity Editor and the standalone policy test executable.</summary>
    public static class StationDecisionAlightingPolicyTests
    {
        public static void Run() => RunAll((name, test) => test());
        public static void RunAll(Action<string, Action> run)
        {
            run("alighter service profiles replay per party without changing boarders or ticket demand", ProfileReplay);
            run("direct-exit and selected visitor shares have configurable bounded independent budgets", ProfileBounds);
            run("alighter policy ignores its old train and never boards or forces a display", IndependentClock);
            run("alighter service feasibility includes queue dwell exit and seated continuation", CompleteBudget);
            run("alighter expired budgets and completed visit limits retain the physical exit", ExitAfterVisits);
            run("alighter needs and configured service weights influence seeded choices", NeedsAndWeights);
            run("temporary alighter service capacity respects the independent clock and visit limit", TemporaryCapacity);
            run("invalid alighter settings or exit estimates cannot invent a station visit", InvalidInputs);
        }

        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static DecisionContext Context() => new DecisionContext {
            alightingJourney = true, stationStopoverDeadlineSeconds = 600, stationServicesRemaining = 2,
            nowSeconds = 100, boardingCutoffSeconds = -1000, trainStatus = DecisionTrainStatus.Departed,
            requireInitialDisplay = true, platformKnown = false, safetyMarginSeconds = 12,
            previousActivity = DecisionActivity.Exit, needs = new DecisionNeeds { information = .5f, shopping = .5f, toilet = .5f }
        };
        static DecisionOption Option(DecisionActivity activity, double dwell = 10) => new DecisionOption {
            activity = activity, poiId = activity.ToString(), travelSeconds = 20, queueSeconds = 5,
            durationSeconds = activity == DecisionActivity.Exit ? 0 : dwell, toExitSeconds = activity == DecisionActivity.Exit ? 0 : 40
        };
        static List<DecisionEvaluation> Evaluate(DecisionContext context, params DecisionOption[] options) => StationDecisionPolicy.Evaluate(context, options);
        static double Probability(List<DecisionEvaluation> values, DecisionActivity activity) => values.First(v => v.option.activity == activity).probability;
        static DecisionArrivalService Service() => new DecisionArrivalService {
            serviceId = "fixture-service", trainNumber = "100", trackNumber = 1, expectedBoarding = 400,
            expectedAlighting = 400, arrivalSeconds = 600, departureSeconds = 1500, doorSeconds = 10,
            coachSeatCapacities = new[] { 120, 120, 120, 120 }
        };
        static List<DecisionArrivalPlan> Generate(DecisionAlightingStopoverSettings stopovers) => DecisionArrivalPlanner.Generate(
            new[] { Service() }, new[] { "east", "west" }, new DecisionArrivalSettings { visualActorRatio = 1,
                alightingStopovers = stopovers, seed = 9123 });
        static string StopoverKey(DecisionArrivalPlan p) => p.journeyId + "|" + p.alightingServiceVisitor + "|" +
            p.alightingServiceVisitLimit + "|" + p.alightingStopoverSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        static string BaseKey(DecisionArrivalPlan p) => p.journeyId + "|" + p.groupId + "|" + p.entranceId + "|" +
            p.plannedEntrySeconds + "|" + p.representedPassengers + "|" + p.coachNumber + "|" + string.Join(",", p.seatNumbers) + "|" +
            p.walkSpeed + "|" + p.familiarity + "|" + p.needs.information + "|" + p.needs.shopping + "|" + p.needs.toilet;

        static void ProfileReplay()
        {
            var configured = Generate(new DecisionAlightingStopoverSettings());
            var replay = Generate(new DecisionAlightingStopoverSettings());
            var disabled = Generate(new DecisionAlightingStopoverSettings { serviceVisitorFraction = 0 });
            Check(configured.Select(StopoverKey).SequenceEqual(replay.Select(StopoverKey)), "Seeded alighting profiles did not replay.");
            Check(configured.Where(p => p.purpose == DecisionJourneyPurpose.Boarding).Select(BaseKey).SequenceEqual(
                disabled.Where(p => p.purpose == DecisionJourneyPurpose.Boarding).Select(BaseKey)), "Adding alighter visits changed a boarder profile.");
            Check(configured.Sum(p => p.representedPassengers) == 800, "Stopovers changed represented passenger demand.");
            foreach (var group in configured.Where(p => p.purpose == DecisionJourneyPurpose.Alighting && !string.IsNullOrEmpty(p.groupId)).GroupBy(p => p.groupId))
                Check(group.Select(p => p.alightingServiceVisitor + "|" + p.alightingServiceVisitLimit + "|" + p.alightingStopoverSeconds).Distinct().Count() == 1,
                    "Companions did not share their seeded station-service profile.");
        }

        static void ProfileBounds()
        {
            var plans = Generate(new DecisionAlightingStopoverSettings());
            var alighters = plans.Where(p => p.purpose == DecisionJourneyPurpose.Alighting).ToArray();
            int visitors = alighters.Count(p => p.alightingServiceVisitor);
            Check(visitors > 0 && visitors < alighters.Length, "The configured subset became every passenger or no passenger.");
            Check(alighters.All(p => p.alightingServiceVisitor ? p.alightingServiceVisitLimit >= 1 && p.alightingServiceVisitLimit <= 2 &&
                p.alightingStopoverSeconds >= 300 && p.alightingStopoverSeconds <= 720 : p.alightingServiceVisitLimit == 0 && p.alightingStopoverSeconds == 0),
                "An alighting profile escaped its configured bounds.");
            Check(Generate(new DecisionAlightingStopoverSettings { serviceVisitorFraction = 0 }).All(p => !p.alightingServiceVisitor), "Zero share did not retain direct exits.");
            Check(Generate(new DecisionAlightingStopoverSettings { serviceVisitorFraction = 1 }).Where(p => p.purpose == DecisionJourneyPurpose.Alighting)
                .All(p => p.alightingServiceVisitor), "Full share failed to select eligible alighters.");
        }

        static void IndependentClock()
        {
            var options = new[] { Option(DecisionActivity.Info), Option(DecisionActivity.Shop), Option(DecisionActivity.Toilet, 180),
                Option(DecisionActivity.Exit), Option(DecisionActivity.Platform), Option(DecisionActivity.Board), Option(DecisionActivity.Display) };
            var context = Context(); var baseline = Evaluate(context, options);
            Check(Probability(baseline, DecisionActivity.Info) > 0 && Probability(baseline, DecisionActivity.Shop) > 0 &&
                Probability(baseline, DecisionActivity.Toilet) > 0, "A departed train blocked a feasible station service.");
            Check(baseline.Where(v => v.option.activity == DecisionActivity.Platform || v.option.activity == DecisionActivity.Board ||
                v.option.activity == DecisionActivity.Display).All(v => v.probability == 0), "An alighter was sent to a platform, train or forced display.");
            context.trainStatus = DecisionTrainStatus.Cancelled; context.boardingCutoffSeconds = double.NaN;
            Check(baseline.Select(v => v.probability).SequenceEqual(Evaluate(context, options).Select(v => v.probability)),
                "The old train clock changed independent alighting probabilities.");
        }

        static void CompleteBudget()
        {
            var context = Context(); var service = Option(DecisionActivity.Toilet, 180);
            service.travelSeconds = 50; service.queueSeconds = 10; service.toExitSeconds = 45;
            service.seatTravelSeconds = 5; service.seatingTransitionSeconds = 4; service.seatedDurationSeconds = 30;
            double cost = 50 + 10 + 180 + 45 + 5 + 4 + 30 + context.safetyMarginSeconds;
            context.stationStopoverDeadlineSeconds = context.nowSeconds + cost;
            Check(Probability(Evaluate(context, service, Option(DecisionActivity.Exit)), DecisionActivity.Toilet) > 0, "Exact independent service budget was rejected.");
            context.stationStopoverDeadlineSeconds -= 1;
            Check(Probability(Evaluate(context, service, Option(DecisionActivity.Exit)), DecisionActivity.Toilet) == 0,
                "Service queue, dwell, exit or seated continuation was omitted from the independent budget.");
        }

        static void ExitAfterVisits()
        {
            var context = Context(); context.stationServicesRemaining = 0;
            var values = Evaluate(context, Option(DecisionActivity.Info), Option(DecisionActivity.Shop), Option(DecisionActivity.Toilet, 180), Option(DecisionActivity.Exit));
            Check(Probability(values, DecisionActivity.Exit) == 1, "Completed visit allowance did not direct the passenger to an exit.");
            context.stationServicesRemaining = 2; context.stationStopoverDeadlineSeconds = context.nowSeconds - 1;
            Check(Probability(Evaluate(context, Option(DecisionActivity.Info), Option(DecisionActivity.Exit)), DecisionActivity.Exit) == 1,
                "Expired independent budget stranded an alighter or kept it in a service.");
        }

        static void NeedsAndWeights()
        {
            var context = Context(); var options = new[] { Option(DecisionActivity.Info), Option(DecisionActivity.Shop), Option(DecisionActivity.Toilet), Option(DecisionActivity.Exit) };
            double low = Probability(Evaluate(context, options), DecisionActivity.Toilet);
            context.needs.toilet = 1;
            Check(Probability(Evaluate(context, options), DecisionActivity.Toilet) > low, "Alighter toilet need did not influence its probability.");
            var parameters = new DecisionPolicyParameters { alightingInfoWeight = 0, alightingToiletWeight = 4 };
            var values = StationDecisionPolicy.Evaluate(context, options, parameters);
            Check(Probability(values, DecisionActivity.Info) == 0, "A zero configured service weight was ignored.");
            var a = new Random(719); var b = new Random(719);
            for (int i = 0; i < 100; ++i)
                Check(StationDecisionPolicy.Choose(values, a).option.activity == StationDecisionPolicy.Choose(values, b).option.activity,
                    "Seeded alighter service choices did not replay.");
        }

        static void TemporaryCapacity()
        {
            var context = Context(); var option = Option(DecisionActivity.Info); option.available = false; option.temporarilyFull = true;
            var values = Evaluate(context, option);
            Check(DecisionFailureBudget.HasTemporaryCapacityRecovery(context, values), "A valid independent capacity wait consumed the route failure budget.");
            context.stationServicesRemaining = 0;
            Check(!DecisionFailureBudget.HasTemporaryCapacityRecovery(context, values), "A completed allowance retained an optional capacity wait.");
            context.stationServicesRemaining = 2; context.stationStopoverDeadlineSeconds = context.nowSeconds;
            Check(!DecisionFailureBudget.HasTemporaryCapacityRecovery(context, values), "An expired optional stopover retained a capacity wait.");
        }

        static void InvalidInputs()
        {
            foreach (var settings in new[] { new DecisionAlightingStopoverSettings { serviceVisitorFraction = float.NaN },
                new DecisionAlightingStopoverSettings { maximumVisits = 3 }, new DecisionAlightingStopoverSettings { minimumBudgetSeconds = -1 },
                new DecisionAlightingStopoverSettings { exitPreference = 0 }, new DecisionAlightingStopoverSettings { minimumInformationNeed = 2 } })
            {
                bool rejected = false; try { settings.Validate(); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Invalid stopover configuration was accepted.");
            }
            var option = Option(DecisionActivity.Shop); option.toExitSeconds = double.NaN;
            Check(Probability(Evaluate(Context(), option, Option(DecisionActivity.Exit)), DecisionActivity.Shop) == 0,
                "An invalid exit estimate invented a feasible service.");
        }
    }
}
