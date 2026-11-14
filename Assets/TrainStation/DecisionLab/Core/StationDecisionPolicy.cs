using System;
using System.Collections.Generic;

namespace StationFlow.TrainStation.DecisionLab
{
    public enum DecisionActivity { Display, Cafe, Toilet, Shop, Rest, Info, Platform, Exit, Board }
    public enum DecisionTrainStatus { Scheduled, Delayed, Cancelled, Boarding, Closed, Departed }

    [Serializable]
    public sealed class DecisionNeeds
    {
        public float thirst, toilet, fatigue, shopping, information;
    }

    [Serializable]
    public sealed class DecisionContext
    {
        public DecisionActivity previousActivity;
        public DecisionTrainStatus trainStatus;
        public double nowSeconds, boardingCutoffSeconds;
        public bool platformKnown = true;
        public DecisionNeeds needs = new DecisionNeeds();
        public float familiarity = .5f, safetyMarginSeconds = 12f;
        public int displayVisits;
        public bool receivedDisruption;
        // A scenario may require an initial timetable visit; terminal train disruptions take precedence.
        public bool requireInitialDisplay;
        public bool alightingJourney;
        public double stationStopoverDeadlineSeconds;
        public int stationServicesRemaining;
    }

    [Serializable]
    public sealed class DecisionOption
    {
        public DecisionActivity activity;
        public string poiId;
        // Estimates in one clock. For an optional visit, reachable must also cover its onward route to the platform.
        public double travelSeconds, queueSeconds, durationSeconds, toPlatformSeconds, toExitSeconds;
        // Optional continuation after a counter visit (or a directly seated Rest activity).
        public double seatTravelSeconds, seatingTransitionSeconds, seatedDurationSeconds;
        public bool reachable = true, available = true;
        // True only for a valid, enabled destination whose occupied places can become free.
        public bool temporarilyFull;
        public float preference = 1f;
    }

    [Serializable]
    public sealed class DecisionEvaluation
    {
        public DecisionOption option;
        public double probability, slackSeconds, score;
        public string reason;
    }

    [Serializable]
    public sealed class DecisionPolicyParameters
    {
        public float urgentWindowSeconds = 25f;
        public float optionalNeedWeight = 4f;
        public float informationNeedWeight = 3f;
        public float repeatedActivityMultiplier = .15f;
        public float alightingInfoWeight = 1f, alightingShopWeight = 1f, alightingToiletWeight = 1f, alightingExitWeight = .15f;
    }

    /// <summary>Temporary capacity/clock waits do not consume the bounded no-route failure budget.</summary>
    public sealed class DecisionFailureBudget
    {
        public int ConsecutiveFailures { get; private set; }
        public void Reset() => ConsecutiveFailures = 0;
        public bool RecordNoChoice(bool legitimateWait, int maximumFailures = 80)
        {
            if (maximumFailures < 0) throw new ArgumentOutOfRangeException(nameof(maximumFailures));
            if (legitimateWait) { Reset(); return false; }
            return ++ConsecutiveFailures > maximumFailures;
        }
        public static bool HasTemporaryCapacityRecovery(DecisionContext context, IReadOnlyList<DecisionEvaluation> evaluations)
        {
            if (context != null && context.alightingJourney)
            {
                if (evaluations == null || !Finite(context.nowSeconds) || !Finite(context.stationStopoverDeadlineSeconds) ||
                    !NonNegative(context.safetyMarginSeconds)) return false;
                foreach (var evaluation in evaluations)
                {
                    var option = evaluation?.option;
                    if (option == null || option.available || !option.temporarilyFull || !option.reachable ||
                        !NonNegative(option.travelSeconds) || !NonNegative(option.queueSeconds) || !NonNegative(option.durationSeconds) ||
                        !NonNegative(option.toExitSeconds) || !NonNegative(option.seatTravelSeconds) ||
                        !NonNegative(option.seatingTransitionSeconds) || !NonNegative(option.seatedDurationSeconds) ||
                        !Finite(option.preference) || option.preference <= 0f) continue;
                    if (option.activity == DecisionActivity.Exit) return true;
                    double cost = option.travelSeconds + option.queueSeconds + option.durationSeconds + option.toExitSeconds +
                        option.seatTravelSeconds + option.seatingTransitionSeconds + option.seatedDurationSeconds + context.safetyMarginSeconds;
                    if (context.stationServicesRemaining > 0 && StationDecisionPolicy.IsAlightingStationService(option.activity) &&
                        Finite(cost) && cost <= context.stationStopoverDeadlineSeconds - context.nowSeconds) return true;
                }
                return false;
            }
            if (context == null || evaluations == null || !Finite(context.nowSeconds) || !Finite(context.boardingCutoffSeconds) ||
                !NonNegative(context.safetyMarginSeconds) || !Enum.IsDefined(typeof(DecisionTrainStatus), context.trainStatus)) return false;
            bool replanning = context.nowSeconds >= context.boardingCutoffSeconds || context.trainStatus == DecisionTrainStatus.Cancelled ||
                context.trainStatus == DecisionTrainStatus.Closed || context.trainStatus == DecisionTrainStatus.Departed;
            foreach (var evaluation in evaluations)
            {
                var option = evaluation?.option;
                if (option == null || option.available || !option.temporarilyFull || !option.reachable) continue;
                bool eligible = replanning ? option.activity == DecisionActivity.Info || option.activity == DecisionActivity.Exit :
                    context.requireInitialDisplay ? option.activity == DecisionActivity.Display :
                    option.activity == DecisionActivity.Display || option.activity == DecisionActivity.Info;
                if (!eligible || !NonNegative(option.travelSeconds) || !NonNegative(option.queueSeconds) || !NonNegative(option.durationSeconds) ||
                    !NonNegative(option.toPlatformSeconds) || !NonNegative(option.seatTravelSeconds) ||
                    !NonNegative(option.seatingTransitionSeconds) || !NonNegative(option.seatedDurationSeconds) ||
                    !Finite(option.preference) || option.preference <= 0f) continue;
                if (!replanning)
                {
                    double cost = option.travelSeconds + option.queueSeconds + option.durationSeconds + option.toPlatformSeconds +
                        option.seatTravelSeconds + option.seatingTransitionSeconds + option.seatedDurationSeconds + context.safetyMarginSeconds;
                    if (!NonNegative(context.safetyMarginSeconds) || !Finite(cost) || cost > context.boardingCutoffSeconds - context.nowSeconds) continue;
                }
                return true;
            }
            return false;
        }
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static bool NonNegative(double value) => Finite(value) && value >= 0d;
    }

    /// <summary>
    /// A hand-authored conditional policy evaluated at activity boundaries, not each frame.
    /// The executor owns elapsed time, sampled activity durations, need updates and knowledge.
    /// All time values must use one clock. Scores are illustrative, not fitted passenger data.
    /// </summary>
    public static class StationDecisionPolicy
    {
        public static List<DecisionEvaluation> Evaluate(DecisionContext context, IReadOnlyList<DecisionOption> options)
        {
            return Evaluate(context, options, new DecisionPolicyParameters());
        }

        public static List<DecisionEvaluation> Evaluate(DecisionContext context, IReadOnlyList<DecisionOption> options,
            DecisionPolicyParameters parameters)
        {
            var result = new List<DecisionEvaluation>();
            if (options == null) return result;
            parameters = parameters ?? new DecisionPolicyParameters();
            if (context != null && context.alightingJourney) return EvaluateAlighting(context, options, parameters);
            bool validContext = context != null && Finite(context.nowSeconds) && Finite(context.boardingCutoffSeconds) &&
                NonNegative(context.safetyMarginSeconds) && Enum.IsDefined(typeof(DecisionTrainStatus), context.trainStatus);
            double remaining = validContext ? context.boardingCutoffSeconds - context.nowSeconds : 0d;
            validContext = validContext && Finite(remaining);
            double safety = validContext ? context.safetyMarginSeconds : 0d;
            bool replanning = validContext && (remaining <= 0d || context.trainStatus == DecisionTrainStatus.Cancelled ||
                context.trainStatus == DecisionTrainStatus.Closed || context.trainStatus == DecisionTrainStatus.Departed);
            double bestPlatformSlack = double.NegativeInfinity;
            bool hasValidDirectAccess = false;
            if (validContext && !replanning && context.platformKnown)
            {
                for (int i = 0; i < options.Count; ++i)
                {
                    var candidate = options[i];
                    if (InvalidOption(candidate) != null ||
                        (candidate.activity != DecisionActivity.Platform && candidate.activity != DecisionActivity.Board)) continue;
                    if (candidate.activity == DecisionActivity.Board && !CanBoard(context)) continue;
                    double directCost = ActivityCost(candidate);
                    if (!Finite(directCost)) continue;
                    hasValidDirectAccess = true;
                    if (directCost < remaining) bestPlatformSlack = Math.Max(bestPlatformSlack, remaining - directCost - safety);
                }
            }
            // The train can still be boarding while this traveller can no longer finish its estimated
            // access/queue before the cutoff. Preserve the observed train status and replan this journey.
            bool estimatedDeadlineMiss = validContext && !replanning && context.platformKnown && !context.requireInitialDisplay &&
                hasValidDirectAccess && double.IsNegativeInfinity(bestPlatformSlack);
            replanning |= estimatedDeadlineMiss;
            bool urgent = validContext && !replanning && context.platformKnown &&
                !double.IsNegativeInfinity(bestPlatformSlack) &&
                bestPlatformSlack <= SafeParameter(parameters.urgentWindowSeconds, 25d);

            double total = 0d;
            for (int i = 0; i < options.Count; ++i)
            {
                var option = options[i];
                var evaluation = new DecisionEvaluation { option = option, reason = "", slackSeconds = 0d };
                result.Add(evaluation);
                string invalid = InvalidOption(option);
                if (!validContext) { evaluation.reason = "Invalid decision context or clock."; continue; }
                if (invalid != null) { evaluation.reason = invalid; continue; }

                bool directTrain = option.activity == DecisionActivity.Platform || option.activity == DecisionActivity.Board;
                double activityCost = ActivityCost(option);
                double completeCost = activityCost + (directTrain ? 0d : option.toPlatformSeconds) + safety;
                if (!Finite(completeCost)) { evaluation.reason = "Activity time estimate overflowed."; continue; }
                evaluation.slackSeconds = remaining - completeCost;
                if (!Finite(evaluation.slackSeconds))
                { evaluation.reason = "Remaining time arithmetic overflowed."; evaluation.slackSeconds = 0d; continue; }

                if (replanning)
                {
                    if (option.activity != DecisionActivity.Info && option.activity != DecisionActivity.Exit)
                    { evaluation.reason = estimatedDeadlineMiss ? "Estimated access and queue cannot finish before the cutoff; seek help or leave." :
                        "The original train cannot be boarded; seek help or leave."; continue; }
                    // Replanning is not constrained by the missed/cancelled train's old deadline.
                    bool hasAsked = context.previousActivity == DecisionActivity.Info;
                    evaluation.score = option.preference * (option.activity == DecisionActivity.Info
                        ? (hasAsked ? .3d : 4d) : (hasAsked ? 4d : 1d));
                    evaluation.reason = estimatedDeadlineMiss ? "Replan because estimated access and queue cannot finish before the cutoff." :
                        "Replan after cancellation, closed boarding or missed cutoff.";
                }
                else
                {
                    if (context.requireInitialDisplay && option.activity != DecisionActivity.Display)
                    { evaluation.reason = "Observe the initial station timetable before choosing another activity."; continue; }
                    if (option.activity == DecisionActivity.Exit)
                    { evaluation.reason = "Leaving is reserved for replanning this train journey."; continue; }
                    if (!context.platformKnown && option.activity != DecisionActivity.Display && option.activity != DecisionActivity.Info)
                    { evaluation.reason = "Resolve the unknown platform at a display or information desk first."; continue; }
                    if (option.activity == DecisionActivity.Board && !CanBoard(context))
                    { evaluation.reason = "Boarding requires an open train and a traveller already at the platform."; continue; }
                    if (directTrain ? activityCost >= remaining : evaluation.slackSeconds < 0d)
                    { evaluation.reason = "Insufficient time for travel, queue, activity and the onward journey."; continue; }
                    if (urgent && !directTrain)
                    { evaluation.reason = "Boarding is urgent; commit to the platform or train."; continue; }
                    evaluation.score = option.preference * Score(context, option.activity, evaluation.slackSeconds, parameters);
                    evaluation.reason = directTrain && evaluation.slackSeconds < 0d
                        ? "Direct train access remains possible but uses the safety reserve."
                        : urgent ? "Urgent direct train access."
                        : !context.platformKnown ? "Obtain the missing platform information."
                        : "Feasible; weighted by needs, familiarity, information and previous activity.";
                }
                if (!Finite(evaluation.score) || evaluation.score <= 0d)
                { evaluation.score = 0d; evaluation.reason = "The configured preference gives this activity no weight."; }
                total += evaluation.score;
            }
            if (total > 0d && Finite(total))
                for (int i = 0; i < result.Count; ++i) result[i].probability = result[i].score / total;
            return result;
        }

        public static bool IsAlightingStationService(DecisionActivity activity) =>
            activity == DecisionActivity.Info || activity == DecisionActivity.Shop || activity == DecisionActivity.Toilet;

        static List<DecisionEvaluation> EvaluateAlighting(DecisionContext context, IReadOnlyList<DecisionOption> options,
            DecisionPolicyParameters parameters)
        {
            var result = new List<DecisionEvaluation>();
            bool valid = Finite(context.nowSeconds) && Finite(context.stationStopoverDeadlineSeconds) &&
                NonNegative(context.safetyMarginSeconds) && context.stationServicesRemaining >= 0;
            double remaining = context.stationStopoverDeadlineSeconds - context.nowSeconds;
            valid &= Finite(remaining);
            double total = 0d;
            foreach (var option in options)
            {
                var evaluation = new DecisionEvaluation { option = option, reason = "" };
                result.Add(evaluation);
                if (!valid) { evaluation.reason = "Invalid independent alighting stopover context."; continue; }
                string invalid = InvalidOption(option);
                if (invalid != null) { evaluation.reason = invalid; continue; }
                if (option.activity == DecisionActivity.Exit)
                {
                    evaluation.score = option.preference * SafeParameter(parameters.alightingExitWeight, .15d);
                    evaluation.reason = "Leave through a reachable station exit after alighting.";
                }
                else
                {
                    if (!IsAlightingStationService(option.activity))
                    { evaluation.reason = "Alighters may use station services or exit, never board or return to a platform."; continue; }
                    if (context.stationServicesRemaining == 0)
                    { evaluation.reason = "The seeded station-service allowance is complete; leave the station."; continue; }
                    double cost = ActivityCost(option) + option.toExitSeconds + context.safetyMarginSeconds;
                    if (!Finite(cost)) { evaluation.reason = "Station-service time estimate overflowed."; continue; }
                    evaluation.slackSeconds = remaining - cost;
                    if (!Finite(evaluation.slackSeconds) || evaluation.slackSeconds < 0d)
                    { evaluation.reason = "Insufficient independent stopover time for the service and station exit."; continue; }
                    double weight = option.activity == DecisionActivity.Info ? SafeParameter(parameters.alightingInfoWeight, 1d) :
                        option.activity == DecisionActivity.Shop ? SafeParameter(parameters.alightingShopWeight, 1d) :
                        SafeParameter(parameters.alightingToiletWeight, 1d);
                    evaluation.score = option.preference * weight * Score(context, option.activity, evaluation.slackSeconds, parameters);
                    evaluation.reason = "Feasible station service within the independent alighting budget and visit allowance.";
                }
                if (!Finite(evaluation.score) || evaluation.score <= 0d)
                { evaluation.score = 0d; evaluation.reason = "The configured preference gives this activity no weight."; }
                total += evaluation.score;
            }
            if (total > 0d && Finite(total))
                foreach (var evaluation in result) evaluation.probability = evaluation.score / total;
            return result;
        }

        static double Score(DecisionContext context, DecisionActivity activity, double slack, DecisionPolicyParameters parameters)
        {
            var needs = context.needs ?? new DecisionNeeds();
            double familiarity = Unit(context.familiarity);
            double needWeight = SafeParameter(parameters.optionalNeedWeight, 4d);
            double infoWeight = SafeParameter(parameters.informationNeedWeight, 3d);
            double value;
            switch (activity)
            {
                case DecisionActivity.Cafe: value = .1d + needWeight * Unit(needs.thirst); break;
                case DecisionActivity.Toilet: value = .1d + needWeight * Unit(needs.toilet); break;
                case DecisionActivity.Shop: value = .1d + needWeight * Unit(needs.shopping); break;
                case DecisionActivity.Rest: value = .1d + needWeight * Unit(needs.fatigue); break;
                case DecisionActivity.Display:
                    value = .15d + infoWeight * Unit(needs.information) + (context.platformKnown ? 0d : 4d) +
                        (context.receivedDisruption ? 4d : 0d);
                    value /= 1d + .5d * Math.Max(0, context.displayVisits);
                    break;
                case DecisionActivity.Info:
                    value = .1d + infoWeight * Unit(needs.information) + .5d * (1d - familiarity) +
                        (context.platformKnown ? 0d : 2d) + (context.receivedDisruption ? 3d : 0d);
                    break;
                case DecisionActivity.Platform:
                    value = .5d + (1d - familiarity) + 2d * (1d - Math.Min(1d, Math.Max(0d, slack) / 180d));
                    break;
                case DecisionActivity.Board: value = 8d; break;
                default: return 0d;
            }
            if (activity == context.previousActivity && activity != DecisionActivity.Platform && activity != DecisionActivity.Board)
                value *= UnitParameter(parameters.repeatedActivityMultiplier, .15d);
            return value;
        }

        public static DecisionEvaluation Choose(IReadOnlyList<DecisionEvaluation> evaluations, Random random)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            if (evaluations == null) return null;
            double total = 0d;
            for (int i = 0; i < evaluations.Count; ++i)
                if (CanChoose(evaluations[i])) total += evaluations[i].probability;
            if (!Finite(total) || total <= 0d) return null;
            double draw = random.NextDouble() * total;
            DecisionEvaluation last = null;
            for (int i = 0; i < evaluations.Count; ++i)
            {
                var evaluation = evaluations[i];
                if (!CanChoose(evaluation)) continue;
                last = evaluation;
                draw -= evaluation.probability;
                if (draw < 0d) return evaluation;
            }
            return last; // Floating point rounding at the upper cumulative boundary.
        }

        /// <summary>Positive truncated normal dwell time; spread is a standard deviation, in the caller's clock seconds.</summary>
        public static float SamplePositiveDuration(Random random, float mean, float spread)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            double safeMean = Finite(mean) && mean > 0f ? mean : .1d;
            double safeSpread = Finite(spread) && spread > 0f ? spread : 0d;
            if (safeSpread == 0d) return (float)safeMean;
            for (int attempt = 0; attempt < 16; ++attempt)
            {
                double u = 1d - random.NextDouble();
                double v = random.NextDouble();
                double normal = Math.Sqrt(-2d * Math.Log(u)) * Math.Cos(2d * Math.PI * v);
                double sample = safeMean + safeSpread * normal;
                if (sample >= .001d && sample <= float.MaxValue) return (float)sample;
            }
            return (float)safeMean;
        }

        /// <summary>Truncated normal duration with explicit lower and upper bounds in the caller's clock.</summary>
        public static float SampleBoundedDuration(Random random, float mean, float spread, float minimum, float maximum)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            if (!Finite(minimum) || !Finite(maximum) || minimum <= 0 || maximum < minimum)
                throw new ArgumentException("Duration bounds must be finite, positive and ordered.");
            double safeMean = Finite(mean) ? Math.Max(minimum, Math.Min(maximum, mean)) : (minimum + (double)maximum) / 2d;
            double safeSpread = Finite(spread) && spread > 0f ? spread : 0d;
            if (safeSpread == 0d || minimum == maximum) return (float)safeMean;
            for (int attempt = 0; attempt < 32; ++attempt)
            {
                double normal = Math.Sqrt(-2d * Math.Log(1d - random.NextDouble())) * Math.Cos(2d * Math.PI * random.NextDouble());
                double sample = safeMean + safeSpread * normal;
                if (sample >= minimum && sample <= maximum) return (float)sample;
            }
            return (float)safeMean;
        }

        static string InvalidOption(DecisionOption option)
        {
            if (option == null) return "Missing option.";
            if (!Enum.IsDefined(typeof(DecisionActivity), option.activity)) return "Unknown activity.";
            if (!option.available) return "The destination is unavailable.";
            if (!option.reachable) return "The destination is unreachable.";
            if (!NonNegative(option.travelSeconds) || !NonNegative(option.queueSeconds) ||
                !NonNegative(option.durationSeconds) || !NonNegative(option.toPlatformSeconds) || !NonNegative(option.toExitSeconds) ||
                !NonNegative(option.seatTravelSeconds) || !NonNegative(option.seatingTransitionSeconds) ||
                !NonNegative(option.seatedDurationSeconds)) return "Invalid activity time estimate.";
            if (!Finite(option.preference) || option.preference <= 0f) return "The activity preference is zero or invalid.";
            return null;
        }

        static bool CanBoard(DecisionContext context)
        {
            return context.platformKnown && context.trainStatus == DecisionTrainStatus.Boarding &&
                (context.previousActivity == DecisionActivity.Platform || context.previousActivity == DecisionActivity.Board);
        }

        static double ActivityCost(DecisionOption option) => option.travelSeconds + option.queueSeconds + option.durationSeconds +
            option.seatTravelSeconds + option.seatingTransitionSeconds + option.seatedDurationSeconds;

        static bool CanChoose(DecisionEvaluation value)
        { return value != null && value.option != null && Finite(value.probability) && value.probability > 0d; }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static bool NonNegative(double value) { return Finite(value) && value >= 0d; }
        static double Unit(double value) { return Finite(value) ? Math.Max(0d, Math.Min(1d, value)) : 0d; }
        static double SafeParameter(double value, double fallback) { return NonNegative(value) ? value : fallback; }
        static double UnitParameter(double value, double fallback) { return Finite(value) ? Unit(value) : fallback; }
    }
}
