using System;
using System.Collections.Generic;
using System.Linq;
using StationFlow.TrainStation.DecisionLab;

static class Program
{
    static int passed;

    static int Main()
    {
        try
        {
            Run("probabilities normalize and retain rejection explanations", ProbabilityContract);
            Run("full detour budget includes queue, dwell, onward travel and reserve", FullBudget);
            Run("seated continuation includes seat travel, both transitions and seated dwell", SeatedBudget);
            Run("invalid seated estimates reject the option while zero costs preserve counter-only choices", InvalidSeatedBudget);
            Run("urgent passengers commit to the platform", UrgentPlatform);
            Run("open train with generous budget still allows cafe and toilet", OpenTrainWithTime);
            Run("urgency uses the fastest available platform access", AlternativePlatformAccess);
            Run("direct access may use safety reserve without making optional trips feasible", SafetyReserve);
            Run("each optional need influences its own probability", NeedsMatter);
            Run("information need and disruption change information choices", InformationMatters);
            Run("unknown platforms require information first", UnknownPlatform);
            Run("a required initial timetable visit is the sole normal choice", RequiredInitialDisplay);
            Run("early disruption and missed cutoff override the initial timetable requirement", EarlyReplanning);
            Run("cancelled, closed, departed and missed trains trigger replanning", Replanning);
            Run("boarding requires a platform arrival and open boarding", Boarding);
            Run("unreachable access and the exact boarding cutoff cannot invent a route", RouteAndCutoff);
            Run("seeded weighted choices replay and exclude rejected options", SeededChoices);
            Run("duration sampling is positive, finite, variable and reproducible", PositiveDurations);
            Run("empty and invalid inputs produce no invented choice", InvalidInputs);
            Run("policy is observational and repeated activities are discouraged", ObservationAndMemory);
            Run("finite native path estimates retain their exact values", FinitePathEstimate);
            Run("unknown native distance uses the complete current corridor and decreases with progress", UnknownPathEstimate);
            Run("fallback rejects stale incomplete detached or invalid native corridors", InvalidPathEstimate);
            Run("daily visual cohorts conserve boarding and alighting demand exactly", DailyDemandWeights);
            Run("doubling visible density preserves real demand and concrete seats in every coach", DoubledVisibleDensity);
            Run("dated plans replay deterministically and isolate unrelated services", DailyDeterminism);
            Run("arrival profiles use their configured departure-relative lead windows", DailyLeadProfiles);
            Run("parties share a service entrance profile and bounded staggered entry", DailyGroups);
            Run("past services are excluded and initial population clipping is explicit", DailyOperatingWindow);
            Run("timetable edits regenerate arrival plans without mutating old snapshots", DailyTimetableEdits);
            Run("invalid dates demand proportions and entrances are rejected", DailyInvalidInputs);
            AlightingStopoverTests.RunAll(Run);
            Run("warm-start portal congestion admits due companions contiguously without early entry", GroupAdmissionOrder);
            Run("party admission reserves pool capacity across portals before starting", GroupAdmissionCapacity);
            Run("blocked parties release their portal after the bounded window without renewal", GroupAdmissionExpiry);
            Run("failed spawn checks do not reserve and expired journeys release reserved capacity", GroupAdmissionRelease);
            Run("an entering party waits outside until both pool and physical portal capacity fit it", GroupAdmissionPhysicalCapacity);
            Run("recorded waiting displacement requires physical reapproach before safe boarding", PlatformDisplacementRecovery);
            Run("platform recovery cannot bypass train track door or cutoff gates", PlatformGateSafety);
            Run("concrete weighted tickets conserve demand with unique seats confined to one coach", ConcreteSeatBlocks);
            Run("coach sampling covers unequal composition without exceeding any coach capacity", UnequalCoachComposition);
            Run("through-passenger seats remain reserved from new boarding and alighting tickets", ThroughSeatReservation);
            Run("ticket manifests and companion groups replay within their assigned coach", CoachManifestIdentity);
            Run("arrival-time edits preserve ticket identity and old manifest seat snapshots", TicketTimetableEdits);
            Run("invalid coach capacities loads and ratios cannot produce tickets", InvalidSeatTickets);
            WeightedEntranceTests.RunAll(Run);
            MobilityProfileTests.RunAll(Run);
            PlatformAccessTests.RunAll(Run);
            ActivityForecastTests.RunAll(Run);
            DecisionFailureBudgetTests.RunAll(Run);
            ToiletQueueTests.RunAll(Run);
            HoldingSlotAllocatorTests.RunAll(Run);
            CoachBoardingQueueTests.RunAll(Run);
            Run("recorded inaccessible boarding deadline allows exit without falsifying train status", EstimatedQueueDeadlineMiss);
            Run("a feasible alternative direct route prevents an estimated missed train", FeasibleDeadlineAlternative);
            Run("using only the safety reserve still permits direct access before the cutoff", DeadlineSafetyReserve);
            Run("temporary unavailable or unreachable access cannot invent an estimated missed train", TemporaryAccessRecovery);
            Run("unknown platforms and required first displays retain their information rules", DeadlineInformationRequirement);
            Run("a timetable delay can recover an estimated miss without mutating policy inputs", DeadlineDelayRecovery);
            Console.WriteLine("PASS: " + passed + " decision-policy and navigation tests.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL after " + passed + " tests: " + error.Message);
            return 1;
        }
    }

    static void Run(string name, Action test) { test(); ++passed; Console.WriteLine("PASS " + name); }
    static DecisionArrivalPlan AdmissionPlan(string id, string group, float due = 0f)
    { return new DecisionArrivalPlan { journeyId = id, groupId = group, plannedEntrySeconds = due }; }

    static void GroupAdmissionOrder()
    {
        var admission = new DecisionArrivalAdmission();
        var party = new[] { AdmissionPlan("A", "G"), AdmissionPlan("B", "G", 1.1f), AdmissionPlan("C", "G", 2.2f), AdmissionPlan("D", "G", 3.3f) };
        var pending = new List<DecisionArrivalPlan>(new[] { party[0], AdmissionPlan("solo1", ""), AdmissionPlan("solo2", ""), party[1], party[2], party[3] });
        var entered = new List<string>(); double nextRelease = 0d;
        for (int step = 0; step < 140; ++step)
        {
            double now = step * .1d;
            foreach (var plan in pending.Where(p => p.plannedEntrySeconds <= now).OrderByDescending(p => admission.HasPriority(p, "door", now)).ToArray())
            {
                var remaining = pending.Where(p => p.groupId == plan.groupId).ToArray();
                if (now < nextRelease || !admission.CanAdmit(plan, "door", now, 8 - entered.Count, remaining.Length)) continue;
                Require(now >= plan.plannedEntrySeconds, "Companion entered before its immutable planned time.");
                entered.Add(plan.journeyId); pending.Remove(plan);
                admission.Admitted(plan, "door", now, remaining.Select(p => p.journeyId), 8d); nextRelease = now + 1.1d;
            }
        }
        Require(string.Join(",", entered) == "A,B,C,D,solo1,solo2", "Older solo arrivals split the entering party.");
        Require(admission.ReservedSlots(14d) == 0, "Completed party retained capacity.");
    }

    static void GroupAdmissionCapacity()
    {
        var admission = new DecisionArrivalAdmission(); var leader = AdmissionPlan("A", "G"); var follower = AdmissionPlan("B", "G", 1.1f);
        Require(!admission.CanAdmit(leader, "west", 0d, 3, 4), "Party began without capacity for all companions.");
        Require(admission.CanAdmit(leader, "west", 0d, 4, 4), "Whole party capacity was rejected.");
        admission.Admitted(leader, "west", 0d, new[] { "B", "C", "D" }, 8d);
        Require(!admission.CanAdmit(AdmissionPlan("solo", ""), "east", 1d, 3, 1), "Another portal consumed reserved party slots.");
        Require(admission.CanAdmit(AdmissionPlan("solo", ""), "east", 1d, 4, 1), "Unreserved spare capacity was unnecessarily blocked.");
        Require(!admission.CanAdmit(AdmissionPlan("other", "H"), "east", 1d, 5, 3), "A second party oversubscribed shared capacity.");
        Require(!admission.CanAdmit(follower, "west", 1d, 3, 3), "Reserved priority caused early entry.");
        Require(admission.CanAdmit(follower, "west", 1.2d, 3, 3), "Reserved companion could not use its capacity.");
        admission.Admitted(follower, "west", 1.2d, new[] { "C", "D" }, 8d);
        Require(admission.ReservedSlots(1.2d) == 2, "Admitted companion's reservation was not consumed.");
    }

    static void GroupAdmissionExpiry()
    {
        var admission = new DecisionArrivalAdmission(); var leader = AdmissionPlan("A", "G"); var solo = AdmissionPlan("solo", "");
        admission.Admitted(leader, "door", 0d, new[] { "B", "C" }, 8d);
        Require(!admission.CanAdmit(solo, "door", 7.999d, 8, 1), "Reservation ended before its bounded window.");
        Require(admission.CanAdmit(solo, "door", 8d, 8, 1), "Blocked portal did not reopen at the deadline.");
        admission.Admitted(AdmissionPlan("B", "G"), "door", 9d, new[] { "C" }, 8d);
        Require(admission.ReservedSlots(9d) == 0 && admission.CanAdmit(solo, "door", 9d, 8, 1), "Late companion renewed an expired monopoly.");
    }

    static void GroupAdmissionRelease()
    {
        var admission = new DecisionArrivalAdmission(); var leader = AdmissionPlan("A", "G"); var solo = AdmissionPlan("solo", "");
        Require(admission.CanAdmit(leader, "door", 0d, 4, 3), "Valid proposed admission was rejected.");
        Require(admission.ReservedSlots(0d) == 0 && admission.CanAdmit(solo, "door", 0d, 4, 1), "A failed physical spawn attempt locked the portal.");
        admission.Admitted(leader, "door", 0d, new[] { "B", "C" }, 8d);
        admission.RemoveJourney("B"); Require(admission.ReservedSlots(1d) == 1, "Expired journey retained a pool slot.");
        admission.RemoveJourney("C"); Require(admission.ReservedSlots(1d) == 0 && admission.CanAdmit(solo, "door", 1d, 4, 1), "Empty reservation retained the portal.");
    }

    static void GroupAdmissionPhysicalCapacity()
    {
        var admission = new DecisionArrivalAdmission(); var leader = AdmissionPlan("A", "G");
        Require(!admission.CanAdmit(leader, "main", 14.8d, 10, 4, 1), "Leader entered the only clear anchor and stranded its three companions.");
        Require(!admission.CanAdmit(leader, "main", 20d, 10, 4, 3), "Partial physical party capacity was accepted.");
        Require(admission.ReservedSlots(20d) == 0, "Deferred party monopolized capacity before entering.");
        Require(admission.CanAdmit(leader, "main", 38d, 10, 4, 4), "Whole party was rejected after the entry area cleared.");
        admission.Admitted(leader, "main", 38d, new[] { "B", "C", "D" }, 8d);
        Require(!admission.CanAdmit(AdmissionPlan("B", "G", 1.1f), "main", 39.1d, 9, 3, 0), "Reserved companion ignored a new physical obstruction.");
        Require(admission.CanAdmit(AdmissionPlan("B", "G", 1.1f), "main", 39.1d, 9, 3, 3), "Companion could not use the party's remaining clear anchors.");
        admission.Admitted(AdmissionPlan("B", "G", 1.1f), "main", 39.1d, new[] { "C", "D" }, 8d);
        Require(admission.CanAdmit(AdmissionPlan("C", "G", 2.2f), "main", 40.2d, 8, 2, 2), "Third companion lost physical admission priority.");
        admission.Admitted(AdmissionPlan("C", "G", 2.2f), "main", 40.2d, new[] { "D" }, 8d);
        Require(admission.CanAdmit(AdmissionPlan("D", "G", 3.3f), "main", 41.3d, 7, 1, 1), "Last companion could not use its final clear anchor.");
        admission.Admitted(AdmissionPlan("D", "G", 3.3f), "main", 41.3d, Array.Empty<string>(), 8d);
        Require(admission.ReservedSlots(41.3d) == 0, "Finished physical admission retained a reservation.");
    }

    static void PlatformDisplacementRecovery()
    {
        // Captured journey reached its anchor, drifted 2.352m while waiting, then saw its train open at608s.
        Require(DecisionPlatformGate.Evaluate(DecisionTrainStatus.Scheduled, true, false, false, true, .198d) == DecisionPlatformGateAction.Wait,
            "An arrived passenger did not wait for its future train.");
        Require(DecisionPlatformGate.Evaluate(DecisionTrainStatus.Scheduled, true, false, false, true, 2.352428917d) == DecisionPlatformGateAction.Reapproach,
            "Waiting displacement was left unrecovered until train arrival.");
        Require(DecisionPlatformGate.Evaluate(DecisionTrainStatus.Boarding, true, true, true, true, 2.352428917d) == DecisionPlatformGateAction.Reapproach,
            "Displaced traveller was deleted or boarded without physically regaining the anchor.");
        Require(DecisionPlatformGate.Evaluate(DecisionTrainStatus.Boarding, true, true, true, true, .2d) == DecisionPlatformGateAction.Board,
            "A physically returned traveller could not board its open assigned train.");
    }

    static void PlatformGateSafety()
    {
        foreach (DecisionTrainStatus status in Enum.GetValues(typeof(DecisionTrainStatus)))
        foreach (bool platform in new[] { false, true })
        foreach (bool train in new[] { false, true })
        foreach (bool doors in new[] { false, true })
        foreach (bool cutoff in new[] { false, true })
        foreach (double distance in new[] { 0d, .999d, 1d, 2.352d, double.NaN, double.PositiveInfinity })
        {
            var result = DecisionPlatformGate.Evaluate(status, platform, train, doors, cutoff, distance);
            if (result == DecisionPlatformGateAction.Board)
                Require(status == DecisionTrainStatus.Boarding && platform && train && doors && cutoff && distance >= 0d && distance < 1d,
                    "Recovery bypassed an authoritative boarding gate.");
            if (!cutoff || status == DecisionTrainStatus.Cancelled || status == DecisionTrainStatus.Closed || status == DecisionTrainStatus.Departed)
                Require(result == DecisionPlatformGateAction.Replan, "An unavailable trip kept reapproaching instead of replanning.");
        }
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static DecisionContext Context() { return new DecisionContext { nowSeconds = 100d, boardingCutoffSeconds = 700d }; }
    static DecisionOption Option(DecisionActivity activity, double travel = 5d, double dwell = 5d)
    { return new DecisionOption { activity = activity, poiId = activity.ToString(), travelSeconds = travel, durationSeconds = dwell, toPlatformSeconds = 15d }; }
    static double Probability(List<DecisionEvaluation> values, DecisionActivity activity)
    { foreach (var value in values) if (value.option != null && value.option.activity == activity) return value.probability; return 0d; }
    static void Normalized(List<DecisionEvaluation> values)
    {
        double sum = 0d;
        foreach (var value in values)
        {
            Require(!double.IsNaN(value.probability) && !double.IsInfinity(value.probability) && value.probability >= 0d, "Invalid probability.");
            sum += value.probability;
        }
        Require(Math.Abs(sum - 1d) < 1e-12d, "Probabilities do not sum to one.");
    }

    static void ProbabilityContract()
    {
        var unavailable = Option(DecisionActivity.Shop); unavailable.available = false;
        var unreachable = Option(DecisionActivity.Toilet); unreachable.reachable = false;
        var badTime = Option(DecisionActivity.Rest); badTime.queueSeconds = double.NaN;
        var zeroPreference = Option(DecisionActivity.Info); zeroPreference.preference = 0f;
        var options = new[] { Option(DecisionActivity.Cafe), Option(DecisionActivity.Platform), unavailable, unreachable, badTime, zeroPreference, null };
        var values = StationDecisionPolicy.Evaluate(Context(), options);
        Require(values.Count == options.Length, "Rejected candidates disappeared from explanation output.");
        Normalized(values);
        for (int i = 2; i < values.Count; ++i)
            Require(values[i].probability == 0d && !string.IsNullOrWhiteSpace(values[i].reason), "Invalid option was eligible or unexplained.");
    }

    static void FullBudget()
    {
        var context = Context(); context.boardingCutoffSeconds = 200d; context.safetyMarginSeconds = 10f;
        var cafe = Option(DecisionActivity.Cafe, 20d, 30d); cafe.queueSeconds = 20d; cafe.toPlatformSeconds = 25d;
        var platform = Option(DecisionActivity.Platform, 30d, 0d);
        var result = StationDecisionPolicy.Evaluate(context, new[] { cafe, platform });
        Require(result[0].probability == 0d && Math.Abs(result[0].slackSeconds + 5d) < 1e-9, "Detour feasibility ignored a cost.");
        cafe.queueSeconds = 15d;
        result = StationDecisionPolicy.Evaluate(context, new[] { cafe, platform });
        Require(result[0].probability > 0d && result[0].slackSeconds == 0d, "An exactly feasible trip was rejected.");
        Normalized(result);
    }

    static void UrgentPlatform()
    {
        var context = Context(); context.boardingCutoffSeconds = 140d; context.needs.thirst = 1f;
        var result = StationDecisionPolicy.Evaluate(context, new[] { Option(DecisionActivity.Cafe, 1d, 1d), Option(DecisionActivity.Info, 1d, 1d), Option(DecisionActivity.Platform, 10d, 0d) });
        Require(Probability(result, DecisionActivity.Platform) == 1d, "Urgent context did not commit to platform.");
    }

    static void SeatedBudget()
    {
        var context = Context(); context.boardingCutoffSeconds = 200d; context.safetyMarginSeconds = 10f;
        var cafe = Option(DecisionActivity.Cafe, 10d, 7d);
        cafe.queueSeconds = 7d; cafe.seatTravelSeconds = 8d; cafe.seatingTransitionSeconds = 6d;
        cafe.seatedDurationSeconds = 30d; cafe.toPlatformSeconds = 20d;
        var platform = Option(DecisionActivity.Platform, 20d, 0d);
        var result = StationDecisionPolicy.Evaluate(context, new[] { cafe, platform });
        Require(result[0].probability > 0d && result[0].slackSeconds == 2d, "The seated continuation cost is incomplete.");
        cafe.seatedDurationSeconds += 3d;
        result = StationDecisionPolicy.Evaluate(context, new[] { cafe, platform });
        Require(result[0].probability == 0d && result[0].slackSeconds == -1d, "A seated stay crossing the full trip budget was allowed.");
        cafe.seatedDurationSeconds -= 3d; cafe.seatingTransitionSeconds += 3d;
        result = StationDecisionPolicy.Evaluate(context, new[] { cafe, platform });
        Require(result[0].probability == 0d, "Standing/sitting transition time was ignored.");
        cafe.seatingTransitionSeconds -= 3d; cafe.seatTravelSeconds += 3d;
        result = StationDecisionPolicy.Evaluate(context, new[] { cafe, platform });
        Require(result[0].probability == 0d, "Travel from the counter exit to the seat was ignored.");
        Normalized(result);
    }

    static void InvalidSeatedBudget()
    {
        var context = Context();
        var cafe = Option(DecisionActivity.Cafe);
        var platform = Option(DecisionActivity.Platform);
        var baseline = StationDecisionPolicy.Evaluate(context, new[] { cafe, platform });
        Require(baseline[0].probability > 0d, "An unconfigured counter-only service stopped working.");
        cafe.seatedDurationSeconds = double.NaN;
        var result = StationDecisionPolicy.Evaluate(context, new[] { cafe, platform });
        Require(result[0].probability == 0d, "NaN seated dwell was accepted.");
        cafe.seatedDurationSeconds = 0d; cafe.seatingTransitionSeconds = -1d;
        result = StationDecisionPolicy.Evaluate(context, new[] { cafe, platform });
        Require(result[0].probability == 0d, "Negative seated transition time was accepted.");
        cafe.seatingTransitionSeconds = 0d; cafe.seatTravelSeconds = double.PositiveInfinity;
        result = StationDecisionPolicy.Evaluate(context, new[] { cafe, platform });
        Require(result[0].probability == 0d, "Infinite seat route was accepted.");
        cafe.seatTravelSeconds = 0d;
        result = StationDecisionPolicy.Evaluate(context, new[] { cafe, platform });
        Require(result[0].probability == baseline[0].probability, "Zero seat continuation changed the existing policy.");
    }

    static void SafetyReserve()
    {
        var context = Context(); context.boardingCutoffSeconds = 120d; context.safetyMarginSeconds = 12f;
        var result = StationDecisionPolicy.Evaluate(context, new[] { Option(DecisionActivity.Platform, 15d, 0d), Option(DecisionActivity.Cafe, 1d, 1d) });
        Require(result[0].probability == 1d && result[0].slackSeconds < 0d, "Safety reserve made possible direct access disappear.");
        context.boardingCutoffSeconds = 110d;
        result = StationDecisionPolicy.Evaluate(context, new[] { Option(DecisionActivity.Platform, 15d, 0d) });
        Require(result[0].probability == 0d, "Impossible arrival was permitted.");
    }

    static void OpenTrainWithTime()
    {
        var context = Context(); context.trainStatus = DecisionTrainStatus.Boarding;
        context.needs.thirst = 1f; context.needs.toilet = 1f;
        var result = StationDecisionPolicy.Evaluate(context, new[] { Option(DecisionActivity.Cafe), Option(DecisionActivity.Toilet), Option(DecisionActivity.Platform, 10d, 0d) });
        Normalized(result);
        Require(result[0].probability > 0d && result[1].probability > 0d,
            "Open doors incorrectly suppressed optional activities despite a generous time budget.");
        context.boardingCutoffSeconds = 140d;
        result = StationDecisionPolicy.Evaluate(context, new[] { Option(DecisionActivity.Cafe), Option(DecisionActivity.Toilet), Option(DecisionActivity.Platform, 10d, 0d) });
        Require(result[2].probability == 1d, "Approaching cutoff did not make the same open train urgent.");
    }

    static void AlternativePlatformAccess()
    {
        var context = Context(); context.boardingCutoffSeconds = 220d;
        var shortAccess = Option(DecisionActivity.Platform, 10d, 0d);
        var longAccess = Option(DecisionActivity.Platform, 100d, 0d);
        var cafe = Option(DecisionActivity.Cafe, 2d, 2d);
        var result = StationDecisionPolicy.Evaluate(context, new[] { shortAccess, longAccess, cafe });
        Require(result[2].probability > 0d, "A slower alternative made a feasible short route appear urgent.");
        shortAccess.reachable = false;
        result = StationDecisionPolicy.Evaluate(context, new[] { shortAccess, longAccess, cafe });
        Require(result[2].probability == 0d && result[1].probability == 1d, "Unavailable short access still suppressed urgency.");
    }

    static void NeedsMatter()
    {
        var options = new[] { Option(DecisionActivity.Cafe), Option(DecisionActivity.Toilet), Option(DecisionActivity.Rest), Option(DecisionActivity.Shop), Option(DecisionActivity.Platform) };
        var baseline = StationDecisionPolicy.Evaluate(Context(), options);
        var activities = new[] { DecisionActivity.Cafe, DecisionActivity.Toilet, DecisionActivity.Rest, DecisionActivity.Shop };
        for (int i = 0; i < activities.Length; ++i)
        {
            var context = Context();
            if (i == 0) context.needs.thirst = 1f;
            if (i == 1) context.needs.toilet = 1f;
            if (i == 2) context.needs.fatigue = 1f;
            if (i == 3) context.needs.shopping = 1f;
            var result = StationDecisionPolicy.Evaluate(context, options);
            Require(Probability(result, activities[i]) > Probability(baseline, activities[i]), "A need failed to influence its activity.");
            Normalized(result);
        }
    }

    static void InformationMatters()
    {
        var context = Context(); context.previousActivity = DecisionActivity.Rest;
        var options = new[] { Option(DecisionActivity.Display), Option(DecisionActivity.Info), Option(DecisionActivity.Platform) };
        var baseline = StationDecisionPolicy.Evaluate(context, options);
        context.needs.information = 1f;
        var informedNeed = StationDecisionPolicy.Evaluate(context, options);
        Require(Probability(informedNeed, DecisionActivity.Display) + Probability(informedNeed, DecisionActivity.Info) >
            Probability(baseline, DecisionActivity.Display) + Probability(baseline, DecisionActivity.Info), "Information need had no effect.");
        context.needs.information = 0f; context.receivedDisruption = true;
        var disrupted = StationDecisionPolicy.Evaluate(context, options);
        Require(Probability(disrupted, DecisionActivity.Display) > Probability(baseline, DecisionActivity.Display), "Disruption was ignored.");
    }

    static void UnknownPlatform()
    {
        var context = Context(); context.platformKnown = false; context.needs.thirst = 1f;
        var result = StationDecisionPolicy.Evaluate(context, new[] { Option(DecisionActivity.Display), Option(DecisionActivity.Info), Option(DecisionActivity.Cafe), Option(DecisionActivity.Platform), Option(DecisionActivity.Board) });
        Normalized(result);
        Require(result[0].probability > 0d && result[1].probability > 0d, "Missing information has no acquisition option.");
        for (int i = 2; i < result.Count; ++i) Require(result[i].probability == 0d, "Unknown platform allowed a premature commitment.");
    }

    static void Replanning()
    {
        foreach (var status in new[] { DecisionTrainStatus.Cancelled, DecisionTrainStatus.Closed, DecisionTrainStatus.Departed, DecisionTrainStatus.Scheduled })
        {
            var context = Context(); context.trainStatus = status; context.boardingCutoffSeconds = status == DecisionTrainStatus.Scheduled ? 99d : 700d;
            var result = StationDecisionPolicy.Evaluate(context, new[] { Option(DecisionActivity.Cafe), Option(DecisionActivity.Platform), Option(DecisionActivity.Info), Option(DecisionActivity.Exit) });
            Normalized(result);
            Require(result[0].probability == 0d && result[1].probability == 0d && result[2].probability > 0d && result[3].probability > 0d, "Replanning choices violate train status.");
        }
    }

    static void RequiredInitialDisplay()
    {
        var context = Context(); context.requireInitialDisplay = true; context.platformKnown = false;
        context.needs.thirst = 1f; context.needs.information = 1f;
        var result = StationDecisionPolicy.Evaluate(context, new[] { Option(DecisionActivity.Display), Option(DecisionActivity.Info),
            Option(DecisionActivity.Cafe), Option(DecisionActivity.Platform), Option(DecisionActivity.Exit) });
        Normalized(result);
        Require(result[0].probability == 1d, "Initial timetable requirement did not select Display exclusively.");
        for (int i = 1; i < result.Count; ++i) Require(result[i].probability == 0d, "Another activity bypassed initial observation.");
        context.requireInitialDisplay = false;
        result = StationDecisionPolicy.Evaluate(context, new[] { Option(DecisionActivity.Display), Option(DecisionActivity.Info) });
        Require(result[1].probability > 0d, "Releasing the initial requirement did not restore the information choice.");
    }

    static void EarlyReplanning()
    {
        foreach (var status in new[] { DecisionTrainStatus.Cancelled, DecisionTrainStatus.Closed, DecisionTrainStatus.Departed, DecisionTrainStatus.Scheduled })
        {
            var context = Context(); context.requireInitialDisplay = true; context.platformKnown = false;
            context.trainStatus = status;
            if (status == DecisionTrainStatus.Scheduled) context.boardingCutoffSeconds = context.nowSeconds;
            var result = StationDecisionPolicy.Evaluate(context, new[] { Option(DecisionActivity.Display), Option(DecisionActivity.Info),
                Option(DecisionActivity.Exit), Option(DecisionActivity.Platform) });
            Normalized(result);
            Require(result[0].probability == 0d && result[1].probability > 0d && result[2].probability > 0d && result[3].probability == 0d,
                "Replanning before first observation was blocked by the initial timetable requirement.");
        }
    }

    static void Boarding()
    {
        var context = Context(); context.trainStatus = DecisionTrainStatus.Boarding;
        var options = new[] { Option(DecisionActivity.Board), Option(DecisionActivity.Platform) };
        Require(StationDecisionPolicy.Evaluate(context, options)[0].probability == 0d, "Traveller boarded without reaching the platform.");
        context.previousActivity = DecisionActivity.Platform;
        Require(StationDecisionPolicy.Evaluate(context, options)[0].probability > 0d, "Open boarding was unavailable from the platform.");
        context.trainStatus = DecisionTrainStatus.Scheduled;
        Require(StationDecisionPolicy.Evaluate(context, options)[0].probability == 0d, "Boarding opened before the train.");
    }

    static void SeededChoices()
    {
        var invalid = Option(DecisionActivity.Shop); invalid.available = false;
        var options = new[] { Option(DecisionActivity.Cafe), Option(DecisionActivity.Toilet), Option(DecisionActivity.Platform), invalid };
        var result = StationDecisionPolicy.Evaluate(Context(), options);
        var first = new Random(12345); var replay = new Random(12345); var observed = new HashSet<DecisionActivity>();
        for (int i = 0; i < 200; ++i)
        {
            var a = StationDecisionPolicy.Choose(result, first); var b = StationDecisionPolicy.Choose(result, replay);
            Require(a.option == b.option && a.option != invalid, "Seeded replay differs or selected invalid destination.");
            observed.Add(a.option.activity);
        }
        Require(observed.Count > 1, "Weighted choice became a deterministic maximum.");
    }

    static void RouteAndCutoff()
    {
        var context = Context(); context.boardingCutoffSeconds = 120d;
        context.trainStatus = DecisionTrainStatus.Boarding; context.previousActivity = DecisionActivity.Platform;
        var atCutoff = Option(DecisionActivity.Board, 20d, 0d);
        var blockedPlatform = Option(DecisionActivity.Platform, 1d, 0d); blockedPlatform.reachable = false;
        var tooLongCafe = Option(DecisionActivity.Cafe, 30d, 30d);
        var result = StationDecisionPolicy.Evaluate(context, new[] { atCutoff, blockedPlatform, tooLongCafe });
        Require(StationDecisionPolicy.Choose(result, new Random(1)) == null, "Impossible routes or exact-cutoff boarding produced an action.");
        atCutoff.travelSeconds = 19d;
        result = StationDecisionPolicy.Evaluate(context, new[] { atCutoff, blockedPlatform, tooLongCafe });
        Require(result[0].probability == 1d, "Arrival strictly before the cutoff was rejected.");
        context.nowSeconds = context.boardingCutoffSeconds;
        var info = Option(DecisionActivity.Info, 60d, 60d);
        result = StationDecisionPolicy.Evaluate(context, new[] { atCutoff, info, Option(DecisionActivity.Exit) });
        Require(result[0].probability == 0d && result[1].probability > 0d && result[2].probability > 0d,
            "Cutoff did not close boarding and release the old budget for replanning.");
    }

    static void PositiveDurations()
    {
        var first = new Random(44); var replay = new Random(44); var samples = new HashSet<float>();
        for (int i = 0; i < 2000; ++i)
        {
            float a = StationDecisionPolicy.SamplePositiveDuration(first, 2f, 10f);
            float b = StationDecisionPolicy.SamplePositiveDuration(replay, 2f, 10f);
            Require(a > 0f && !float.IsInfinity(a) && !float.IsNaN(a) && a == b, "Invalid or nonreproducible duration.");
            samples.Add(a);
        }
        Require(samples.Count > 100, "Activity duration does not vary.");
        foreach (var mean in new[] { float.NaN, float.PositiveInfinity, -1f, 0f, float.MaxValue })
        {
            float value = StationDecisionPolicy.SamplePositiveDuration(first, mean, float.MaxValue);
            Require(value > 0f && !float.IsNaN(value) && !float.IsInfinity(value), "Invalid duration parameters escaped sanitization.");
        }
    }

    static void InvalidInputs()
    {
        var invalid = Option(DecisionActivity.Cafe); invalid.durationSeconds = -1d;
        var values = StationDecisionPolicy.Evaluate(Context(), new[] { invalid });
        Require(StationDecisionPolicy.Choose(values, new Random(1)) == null, "Rejected candidates generated a choice.");
        Require(StationDecisionPolicy.Evaluate(Context(), null).Count == 0, "Null option list was not empty.");
        values = StationDecisionPolicy.Evaluate(null, new[] { Option(DecisionActivity.Platform) });
        Require(values[0].probability == 0d, "Null context was accepted.");
        var context = Context(); context.nowSeconds = double.PositiveInfinity;
        Require(StationDecisionPolicy.Evaluate(context, new[] { Option(DecisionActivity.Platform) })[0].probability == 0d, "Invalid clock was accepted.");
    }

    static void ObservationAndMemory()
    {
        var context = Context(); context.needs.thirst = .8f; context.previousActivity = DecisionActivity.Display;
        var cafe = Option(DecisionActivity.Cafe);
        var options = new[] { cafe, Option(DecisionActivity.Platform) };
        var before = StationDecisionPolicy.Evaluate(context, options);
        Require(context.needs.thirst == .8f && context.nowSeconds == 100d && cafe.durationSeconds == 5d, "Evaluation mutated its input.");
        context.previousActivity = DecisionActivity.Cafe;
        var after = StationDecisionPolicy.Evaluate(context, options);
        Require(after[0].probability < before[0].probability, "Previous activity did not discourage repetition.");
    }

    static void FinitePathEstimate()
    {
        var root = new DecisionPathPoint(0d, 0d, 0d);
        foreach (double native in new[] { 0d, .15d, 24.735d })
            Require(DecisionPathDistance.TryResolve(native, true, false, false, root, root, root, null, out double actual) && actual == native,
                "Finite native estimate changed or required an unnecessary corner fallback.");
        Require(!DecisionPathDistance.TryResolve(5d, false, false, false, root, root, root, null, out _), "Incomplete path was accepted.");
        Require(!DecisionPathDistance.TryResolve(5d, true, true, false, root, root, root, null, out _), "Pending path was accepted.");
        Require(!DecisionPathDistance.TryResolve(-1d, true, false, false, root, root, root, null, out _), "Negative native estimate was accepted.");
    }

    static void UnknownPathEstimate()
    {
        var root = new DecisionPathPoint(0d, 0d, 0d); var bend = new DecisionPathPoint(0d, 0d, 12d);
        var destination = new DecisionPathPoint(9d, 0d, 12d);
        var corners = new[] { root, bend, destination };
        Require(DecisionPathDistance.TryResolve(double.PositiveInfinity, true, false, false, root, root, destination, corners, out double before) &&
            Math.Abs(before - 21d) < 1e-9d, "Fallback cut the corner or failed on a valid unknown native estimate.");
        var moved = new DecisionPathPoint(0d, 0d, 4d); corners[0] = moved;
        Require(DecisionPathDistance.TryResolve(double.PositiveInfinity, true, false, false, moved, moved, destination, corners, out double after) &&
            Math.Abs(after - 17d) < 1e-9d && before - after > .2d, "Real corridor progress would not reset the existing progress threshold.");
        Require(DecisionPathDistance.TryResolve(double.NaN, true, false, false, moved, moved, destination, corners, out double repeat) &&
            repeat == after, "Stationary samples changed the distance or non-finite handling differs.");
        Require(corners[0].z == 4d && corners[1].z == 12d && corners[2].x == 9d, "Read-only distance estimate mutated the corridor.");
    }

    static void InvalidPathEstimate()
    {
        var root = new DecisionPathPoint(0d, 0d, 0d); var destination = new DecisionPathPoint(0d, 0d, 10d);
        var corners = new[] { root, destination };
        Require(!DecisionPathDistance.TryResolve(double.PositiveInfinity, true, false, true, root, root, destination, corners, out _), "Stale corridor used.");
        Require(!DecisionPathDistance.TryResolve(double.PositiveInfinity, false, false, false, root, root, destination, corners, out _), "Incomplete corridor used.");
        Require(!DecisionPathDistance.TryResolve(double.PositiveInfinity, true, true, false, root, root, destination, corners, out _), "Pending corridor used.");
        Require(!DecisionPathDistance.TryResolve(double.PositiveInfinity, true, false, false, root, new DecisionPathPoint(.2d, 0d, 0d), destination, corners, out _),
            "Detached actor accepted a navigation estimate.");
        Require(!DecisionPathDistance.TryResolve(double.PositiveInfinity, true, false, false, root, root, new DecisionPathPoint(0d, 0d, 11d), corners, out _),
            "Corridor for another destination accepted.");
        Require(!DecisionPathDistance.TryResolve(double.PositiveInfinity, true, false, false, root, root, destination, new[] { new DecisionPathPoint(0d, 0d, 1d), destination }, out _),
            "Outdated first corner accepted.");
        Require(!DecisionPathDistance.TryResolve(double.PositiveInfinity, true, false, false, root, root, destination, new[] { root }, out _), "Incomplete corner array accepted.");
        Require(!DecisionPathDistance.TryResolve(double.PositiveInfinity, true, false, false, root, root, destination, new[] { root, new DecisionPathPoint(double.NaN, 0d, 3d), destination }, out _),
            "Non-finite corner accepted.");
    }

    static DecisionArrivalService DailyService(string id = "T0800", int boarding = 220, int alighting = 107)
    { return new DecisionArrivalService { serviceId = id, trainNumber = "TER1", trackNumber = 2, arrivalSeconds = 3500f, departureSeconds = 4200f, doorSeconds = 8f,
        expectedBoarding = boarding, expectedAlighting = alighting }; }
    static List<DecisionArrivalPlan> Plan(DecisionArrivalService service, DecisionArrivalSettings settings = null)
    { return DecisionArrivalPlanner.Generate(new[] { service }, new[] { "west", "east" }, settings ?? new DecisionArrivalSettings()); }
    static void DailyDemandWeights()
    {
        foreach (int demand in new[] { 0, 1, 9, 10, 11, 107, 220, 999 })
        {
            var plans = Plan(DailyService(boarding: demand, alighting: demand), new DecisionArrivalSettings { visualActorRatio = (double).1f });
            foreach (var purpose in new[] { DecisionJourneyPurpose.Boarding, DecisionJourneyPurpose.Alighting })
            {
                var subset = plans.Where(p => p.purpose == purpose).ToArray();
                Require(subset.Sum(p => p.representedPassengers) == demand, "Weighted demand was lost or invented.");
                Require(subset.Length == (int)Math.Ceiling(demand / 10d), "Float .1 sampling created an extra visual actor.");
                Require(subset.All(p => p.representedPassengers > 0), "A visible actor represented no passengers.");
            }
        }
    }
    static string PlanKey(DecisionArrivalPlan p) => p.journeyId + "|" + p.entranceId + "|" + p.groupId + "|" + p.arrivalProfile + "|" +
        p.plannedEntrySeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" + p.representedPassengers;
    static void DoubledVisibleDensity()
    {
        foreach (int demand in new[] { 1, 55, 88, 107, 132, 165, 187, 220 })
        {
            var service = DailyService(boarding: demand, alighting: demand); service.coachSeatCapacities = new[] { 55, 55, 55, 55 };
            var sparse = Plan(service, new DecisionArrivalSettings { visualActorRatio = (double).1f });
            var dense = Plan(service, new DecisionArrivalSettings { visualActorRatio = (double).2f });
            foreach (var purpose in new[] { DecisionJourneyPurpose.Boarding, DecisionJourneyPurpose.Alighting })
            {
                var oldPlans = sparse.Where(p => p.purpose == purpose).ToArray();
                var newPlans = dense.Where(p => p.purpose == purpose).ToArray();
                Require(newPlans.Sum(p => p.representedPassengers) == demand && newPlans.Length >= oldPlans.Length,
                    "Doubling visible sampling lost demand or reduced the actor count.");
                var oldSeats = oldPlans.SelectMany(p => p.seatNumbers.Select(s => p.coachNumber + "/" + s)).OrderBy(s => s).ToArray();
                var newSeats = newPlans.SelectMany(p => p.seatNumbers.Select(s => p.coachNumber + "/" + s)).OrderBy(s => s).ToArray();
                Require(oldSeats.SequenceEqual(newSeats) && newSeats.Distinct().Count() == newSeats.Length,
                    "Density changes reassigned or duplicated real passenger seats.");
                foreach (var coach in newPlans.GroupBy(p => p.coachNumber))
                {
                    int load = coach.Sum(p => p.representedPassengers);
                    Require(coach.Count() == (int)Math.Ceiling(Math.Round(load * .2d, 6)) && load <= 55,
                        "Twenty-percent sampling exceeds real coach capacity or creates extra visible cohorts.");
                }
            }
        }
    }
    static void DailyDeterminism()
    {
        var service = DailyService(); var first = Plan(service); var replay = Plan(service);
        Require(first.Select(PlanKey).SequenceEqual(replay.Select(PlanKey)), "Seeded daily planning did not replay.");
        var expanded = DecisionArrivalPlanner.Generate(new[] { DailyService("other"), service }, new[] { "west", "east" }, new DecisionArrivalSettings());
        Require(first.Select(PlanKey).SequenceEqual(expanded.Where(p => p.serviceId == service.serviceId).Select(PlanKey)), "Unrelated service changed existing arrival draws.");
        Require(first.Select(p => p.journeyId).Distinct().Count() == first.Count, "Duplicate journey identity.");
        var otherDate = Plan(service, new DecisionArrivalSettings { scenarioDate = "2026-10-07" });
        Require(!first.Select(p => p.journeyId).Intersect(otherDate.Select(p => p.journeyId)).Any(), "Date change reused identity.");
    }
    static void DailyLeadProfiles()
    {
        foreach (var profile in new[] { DecisionArrivalProfile.EarlyService, DecisionArrivalProfile.Ordinary, DecisionArrivalProfile.LastMinute })
        {
            var settings = new DecisionArrivalSettings { earlyRatio = profile == DecisionArrivalProfile.EarlyService ? 1 : 0,
                ordinaryRatio = profile == DecisionArrivalProfile.Ordinary ? 1 : 0, lateRatio = profile == DecisionArrivalProfile.LastMinute ? 1 : 0,
                groupPeopleFraction = 0d };
            double low = profile == DecisionArrivalProfile.EarlyService ? 1200 : profile == DecisionArrivalProfile.Ordinary ? 600 : 180;
            double high = profile == DecisionArrivalProfile.EarlyService ? 2400 : profile == DecisionArrivalProfile.Ordinary ? 1200 : 420;
            foreach (var plan in Plan(DailyService(), settings).Where(p => p.purpose == DecisionJourneyPurpose.Boarding))
                Require(plan.arrivalProfile == profile && plan.leadSeconds >= low && plan.leadSeconds <= high &&
                    Math.Abs(plan.publishedDepartureSeconds - plan.plannedEntrySeconds - plan.leadSeconds) < .001d, "Profile lead is outside its window.");
        }
    }
    static void DailyGroups()
    {
        var plans = Plan(DailyService(), new DecisionArrivalSettings { groupPeopleFraction = .35d });
        Require(plans.Any(p => string.IsNullOrEmpty(p.groupId)) && plans.Any(p => !string.IsNullOrEmpty(p.groupId)), "Missing solos or parties.");
        foreach (var group in plans.Where(p => !string.IsNullOrEmpty(p.groupId)).GroupBy(p => p.groupId))
        {
            var members = group.OrderBy(p => p.groupMemberIndex).ToArray();
            Require(members.Length >= 2 && members.Length <= 4 && members.All(p => p.groupSize == members.Length), "Invalid party size.");
            Require(members.Select(p => p.entranceId).Distinct().Count() == 1 && members.Select(p => p.serviceId).Distinct().Count() == 1 &&
                members.Select(p => p.arrivalProfile).Distinct().Count() == 1, "Companions do not share arrival context.");
            Require(members.Last().plannedEntrySeconds - members.First().plannedEntrySeconds <= 3.301f, "Party entries are too far apart.");
            Require(!ReferenceEquals(members[0].needs, members[1].needs), "Mutable needs are shared between companions.");
        }
    }
    static void DailyOperatingWindow()
    {
        var past = DailyService(); past.arrivalSeconds = -900f; past.departureSeconds = -100f;
        Require(Plan(past).Count == 0, "Already departed services generated late historical travellers.");
        var present = DailyService(); present.arrivalSeconds = -200f; present.departureSeconds = 200f;
        var plans = Plan(present);
        Require(plans.Any(p => p.clippedAtSimulationStart) && plans.All(p => p.plannedEntrySeconds >= 0f), "Initial population clipping is hidden or negative.");
        Require(plans.Where(p => p.purpose == DecisionJourneyPurpose.Alighting).All(p => p.entranceId == "platform-2"), "Alighters use a street entrance.");
    }
    static void DailyTimetableEdits()
    {
        var service = DailyService(); var before = Plan(service); var keys = before.Select(PlanKey).ToArray();
        service.arrivalSeconds += 600f; service.departureSeconds += 600f;
        var after = Plan(service);
        Require(before.Select(PlanKey).SequenceEqual(keys), "Changing timetable mutated an earlier manifest snapshot.");
        foreach (var plan in before)
        {
            var revised = after.Single(p => p.journeyId == plan.journeyId);
            Require(Math.Abs(revised.plannedEntrySeconds - plan.plannedEntrySeconds - 600f) < .001f, "Explicit timetable regeneration did not shift planned entry.");
        }
    }
    static void DailyInvalidInputs()
    {
        Action<Action> rejected = action => { bool thrown = false; try { action(); } catch (ArgumentException) { thrown = true; } Require(thrown, "Invalid daily input was accepted."); };
        rejected(() => Plan(DailyService(), new DecisionArrivalSettings { scenarioDate = "not-a-date" }));
        rejected(() => Plan(DailyService(), new DecisionArrivalSettings { visualActorRatio = double.NaN }));
        rejected(() => Plan(DailyService(), new DecisionArrivalSettings { groupPeopleFraction = 2d }));
        rejected(() => Plan(DailyService(boarding: -1)));
        rejected(() => DecisionArrivalPlanner.Generate(new[] { DailyService() }, new[] { "same", "same" }, new DecisionArrivalSettings()));
        rejected(() => DecisionArrivalPlanner.Generate(new[] { DailyService(), DailyService() }, new[] { "west" }, new DecisionArrivalSettings()));
    }

    static void ConcreteSeatBlocks()
    {
        foreach (int demand in new[] { 0, 1, 3, 4, 55, 88, 187, 220 })
        foreach (double ratio in new[] { .1d, .25d, 1d })
        {
            var blocks = DecisionSeatTicketAllocator.Allocate(new[] { 55, 55, 55, 55 }, demand, 0, ratio);
            Require(blocks.Sum(b => b.RepresentedPassengers) == demand, "Concrete seats lost weighted demand.");
            var keys = blocks.SelectMany(b => b.seatNumbers.Select(s => b.coachNumber + "/" + s)).ToArray();
            Require(keys.Length == keys.Distinct().Count(), "The same seat was ticketed twice.");
            Require(blocks.All(b => b.coachNumber >= 1 && b.coachNumber <= 4 && b.seatNumbers.Length > 0 &&
                b.seatNumbers.All(s => s >= 1 && s <= 55)), "A ticket spans coaches or invented a seat.");
            foreach (var coach in blocks.GroupBy(b => b.coachNumber))
            {
                int load = coach.Sum(b => b.RepresentedPassengers);
                Require(coach.Count() == (int)Math.Ceiling(Math.Round(load * ratio, 6)), "Per-coach sampling count is inconsistent.");
            }
        }
        Require(DecisionSeatTicketAllocator.Allocate(new[] { 55, 55, 55, 55 }, 187, 0, .1d).Count == 20,
            "187 passengers should be sampled in five actors per coach.");
    }

    static void UnequalCoachComposition()
    {
        var capacities = new[] { 9, 17, 53, 1 };
        foreach (int demand in Enumerable.Range(0, 81))
        {
            var blocks = DecisionSeatTicketAllocator.Allocate(capacities, demand, 0, .1d);
            Require(blocks.Sum(b => b.RepresentedPassengers) == demand, "Unequal coach allocation loses passengers.");
            foreach (var coach in blocks.GroupBy(b => b.coachNumber))
                Require(coach.Sum(b => b.RepresentedPassengers) <= capacities[coach.Key - 1] &&
                    coach.SelectMany(b => b.seatNumbers).All(s => s <= capacities[coach.Key - 1]), "Small coach exceeds its real seats.");
        }
        var full = DecisionSeatTicketAllocator.Allocate(capacities, 80, 0, 1d);
        Require(full.GroupBy(b => b.coachNumber).All(g => g.Count() == capacities[g.Key - 1]), "Full load did not fill the exact composition.");
    }

    static void ThroughSeatReservation()
    {
        var blocks = DecisionSeatTicketAllocator.Allocate(new[] { 55, 55, 55, 55 }, 212, 8, 1d);
        Require(blocks.Count == 212 && blocks.All(b => b.seatNumbers.Single() >= 3), "Reserved through seats were sold again.");
        foreach (var coach in blocks.GroupBy(b => b.coachNumber))
            Require(coach.SelectMany(b => b.seatNumbers).SequenceEqual(Enumerable.Range(3, 53)), "Seat reservation did not conserve the remaining cabin.");
        Require(DecisionSeatTicketAllocator.Allocate(new[] { 2, 1 }, 0, 3, .1d).Count == 0, "Empty new demand invented a ticket.");
    }

    static DecisionArrivalService CoachService()
    { var service = DailyService(boarding: 187, alighting: 187); service.coachSeatCapacities = new[] { 55, 55, 55, 55 }; return service; }
    static string TicketKey(DecisionArrivalPlan p) => p.ticketId + "|" + p.coachNumber + "|" + string.Join(",", p.seatNumbers);
    static void CoachManifestIdentity()
    {
        var plans = Plan(CoachService());
        Require(plans.Select(p => p.ticketId).Distinct().Count() == plans.Count && plans.All(p => !string.IsNullOrEmpty(p.ticketId)), "Ticket identity was reused.");
        Require(plans.All(p => p.representedPassengers == p.seatNumbers.Length), "An actor's seat block differs from its represented load.");
        Require(plans.Select(TicketKey).SequenceEqual(Plan(CoachService()).Select(TicketKey)), "Concrete ticket manifests do not replay.");
        foreach (var purpose in plans.GroupBy(p => p.purpose))
        {
            var seats = purpose.SelectMany(p => p.seatNumbers.Select(s => p.coachNumber + "/" + s)).ToArray();
            Require(seats.Length == 187 && seats.Distinct().Count() == 187, "Incoming or outbound tickets overbook seats.");
        }
        foreach (var party in plans.Where(p => !string.IsNullOrEmpty(p.groupId)).GroupBy(p => p.groupId))
            Require(party.Select(p => p.coachNumber).Distinct().Count() == 1, "A cohesive group was assigned distant coach doors.");
    }

    static void TicketTimetableEdits()
    {
        var service = CoachService(); var old = Plan(service); var snapshots = old.Select(TicketKey).ToArray();
        service.arrivalSeconds += 600; service.departureSeconds += 600;
        var revised = Plan(service);
        foreach (var plan in old) Require(TicketKey(plan) == TicketKey(revised.Single(p => p.journeyId == plan.journeyId)), "Retiming changed the reserved coach or seat block.");
        service.coachSeatCapacities[0] = 60; service.coachSeatCapacities[1] = 50;
        Plan(service);
        Require(old.Select(TicketKey).SequenceEqual(snapshots), "Composition changes mutated old immutable ticket snapshots.");
    }

    static void InvalidSeatTickets()
    {
        Action<Action> reject = action => { bool thrown = false; try { action(); } catch (ArgumentException) { thrown = true; } Require(thrown, "Invalid concrete ticket allocation was accepted."); };
        reject(() => DecisionSeatTicketAllocator.Allocate(null, 1, 0, .1d));
        reject(() => DecisionSeatTicketAllocator.Allocate(new[] { 55, 0 }, 1, 0, .1d));
        reject(() => DecisionSeatTicketAllocator.Allocate(new[] { 55, -1 }, 0, 0, .1d));
        reject(() => DecisionSeatTicketAllocator.Allocate(new[] { 55 }, 54, 2, .1d));
        reject(() => DecisionSeatTicketAllocator.Allocate(new[] { int.MaxValue, 1 }, 1, 0, .1d));
        reject(() => DecisionSeatTicketAllocator.Allocate(new[] { 55 }, 1, -1, .1d));
        reject(() => DecisionSeatTicketAllocator.Allocate(new[] { 55 }, 1, 0, double.NaN));
        reject(() => DecisionSeatTicketAllocator.Allocate(new[] { 55 }, 1, 0, 0d));
        var service = CoachService(); service.expectedBoarding = 221;
        reject(() => Plan(service));
    }

    static void EstimatedQueueDeadlineMiss()
    {
        foreach (var status in new[] { DecisionTrainStatus.Scheduled, DecisionTrainStatus.Delayed, DecisionTrainStatus.Boarding })
        {
            var context = Context(); context.trainStatus = status; context.nowSeconds = 378.1573486328125d; context.boardingCutoffSeconds = 630d;
            context.previousActivity = DecisionActivity.Toilet;
            var platform = Option(DecisionActivity.Platform, 107.0257568359375d, 0d); platform.queueSeconds = 230d;
            var exit = Option(DecisionActivity.Exit, 19.210693359375d, 0d);
            var result = StationDecisionPolicy.Evaluate(context, new[] { platform, exit });
            Require(result[0].probability == 0d && result[1].probability == 1d && result[1].reason.Contains("estimated access and queue"),
                "A traveller with a reachable exit retried an impossible estimated deadline.");
            Require(context.trainStatus == status && context.boardingCutoffSeconds == 630d, "Estimated miss falsified the observed rail status or cutoff.");
        }
    }
    static void FeasibleDeadlineAlternative()
    {
        var context = Context(); context.boardingCutoffSeconds = 200d;
        var crowded = Option(DecisionActivity.Platform, 60d, 0d); crowded.queueSeconds = 60d;
        var alternate = Option(DecisionActivity.Platform, 30d, 0d);
        var values = StationDecisionPolicy.Evaluate(context, new[] { crowded, alternate, Option(DecisionActivity.Exit) });
        Require(values[0].probability == 0d && values[1].probability == 1d && values[2].probability == 0d, "A slow access estimate hid a feasible alternative.");
    }
    static void DeadlineSafetyReserve()
    {
        var context = Context(); context.boardingCutoffSeconds = 120d; context.safetyMarginSeconds = 12f;
        var platform = Option(DecisionActivity.Platform, 10d, 0d); platform.queueSeconds = 9.9d;
        var result = StationDecisionPolicy.Evaluate(context, new[] { platform, Option(DecisionActivity.Exit) });
        Require(result[0].probability == 1d && result[0].slackSeconds < 0d && result[1].probability == 0d,
            "Safety margin was incorrectly treated as a hard missed deadline.");
        platform.queueSeconds = 10d;
        result = StationDecisionPolicy.Evaluate(context, new[] { platform, Option(DecisionActivity.Exit) });
        Require(result[0].probability == 0d && result[1].probability == 1d, "An exact-cutoff estimated arrival was still accepted.");
    }
    static void TemporaryAccessRecovery()
    {
        foreach (bool unreachable in new[] { true, false })
        {
            var context = Context(); context.boardingCutoffSeconds = 200d;
            var platform = Option(DecisionActivity.Platform, 500d, 0d); platform.reachable = !unreachable; platform.available = unreachable;
            var display = Option(DecisionActivity.Display, 1d, 0d); display.toPlatformSeconds = 1d;
            var values = StationDecisionPolicy.Evaluate(context, new[] { platform, display, Option(DecisionActivity.Exit) });
            Require(values[1].probability == 1d && values[2].probability == 0d, "A temporary path or capacity failure invented a missed journey.");
            platform.reachable = platform.available = true; platform.travelSeconds = 10d;
            values = StationDecisionPolicy.Evaluate(context, new[] { platform, Option(DecisionActivity.Exit) });
            Require(values[0].probability == 1d && values[1].probability == 0d, "A recovered direct route did not remain usable.");
        }
    }
    static void DeadlineInformationRequirement()
    {
        var context = Context(); context.boardingCutoffSeconds = 200d; context.platformKnown = false;
        var platform = Option(DecisionActivity.Platform, 150d, 0d);
        var display = Option(DecisionActivity.Display, 1d, 0d); display.toPlatformSeconds = 1d;
        var values = StationDecisionPolicy.Evaluate(context, new[] { platform, display, Option(DecisionActivity.Exit) });
        Require(values[1].probability == 1d && values[2].probability == 0d, "An unknown platform bypassed information via an inferred exit.");
        context.platformKnown = true; context.requireInitialDisplay = true;
        values = StationDecisionPolicy.Evaluate(context, new[] { platform, display, Option(DecisionActivity.Exit) });
        Require(values[1].probability == 1d && values[2].probability == 0d, "An initial timetable requirement was overridden without a terminal disruption.");
        context.requireInitialDisplay = false;
        values = StationDecisionPolicy.Evaluate(context, new[] { platform, display, Option(DecisionActivity.Exit) });
        Require(values[2].probability == 1d && values[1].probability == 0d, "Known hard access failure did not replan after the initial observation.");
    }
    static void DeadlineDelayRecovery()
    {
        var context = Context(); context.boardingCutoffSeconds = 200d;
        var platform = Option(DecisionActivity.Platform, 60d, 0d); platform.queueSeconds = 60d;
        var exit = Option(DecisionActivity.Exit);
        var result = StationDecisionPolicy.Evaluate(context, new[] { platform, exit });
        Require(result[1].probability == 1d, "Fixture did not recognize the predicted missed access.");
        context.boardingCutoffSeconds = 500d; context.trainStatus = DecisionTrainStatus.Delayed;
        result = StationDecisionPolicy.Evaluate(context, new[] { platform, exit });
        Require(result[0].probability == 1d && result[1].probability == 0d && context.trainStatus == DecisionTrainStatus.Delayed &&
            platform.travelSeconds == 60d && platform.queueSeconds == 60d && platform.available && platform.reachable,
            "Retiming did not recover the journey or the policy mutated an option to do it.");
    }
}
