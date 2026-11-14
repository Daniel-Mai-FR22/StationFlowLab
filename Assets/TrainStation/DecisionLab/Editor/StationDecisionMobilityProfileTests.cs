using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Pure profile and immutable arrival-plan contracts; no Unity objects or scene state are modified.</summary>
    public static class StationDecisionMobilityProfileTests
    {
        public static int Run()
        { int count = 0; RunAll((name, test) => { test(); ++count; }); return count; }

        public static void RunAll(Action<string, Action> run)
        {
            run("mobility profiles replay by immutable journey without depending on actor or list order", Replay);
            run("adult cohorts contain varied ages health conditions and finite bounded walking paces", DiverseAdultProfiles);
            run("age and health factors govern individual walking and limited urgency speeds", IndividualFactors);
            run("companion pace respects the slowest member and a shared bounded party factor", PartyPace);
            run("mobility sampling preserves preexisting demand groups needs tickets and arrival random draws", ArrivalStreamPreserved);
            run("profile regeneration and timetable changes cannot mutate an earlier manifest snapshot", SnapshotIsolation);
            run("new mobility seeds vary profiles without changing generated arrival context", IndependentSeed);
            run("invalid profile identities and unsupported rig ages are rejected", InvalidInputs);
        }

        static void Replay()
        {
            var first = Plans(); var replay = Plans().ToDictionary(p => p.journeyId);
            DecisionMobilityProfiles.ApplyToPlans(first.AsEnumerable().Reverse().ToArray(), 20261020);
            foreach (var plan in first)
                Check(ProfileKey(plan) == ProfileKey(replay[plan.journeyId]), "Reordered profile generation changed replay.");
            var extra = Service("unrelated"); extra.arrivalSeconds += 50000f; extra.departureSeconds += 50000f;
            var extended = DecisionArrivalPlanner.Generate(new[] { extra, Service("mobility-fixture") }, new[] { "west", "east" }, Settings()).ToDictionary(p => p.journeyId);
            foreach (var plan in first) Check(ProfileKey(plan) == ProfileKey(extended[plan.journeyId]), "An unrelated service perturbed a mobility profile.");
        }

        static void DiverseAdultProfiles()
        {
            var profiles = Enumerable.Range(0, 2000).Select(i => DecisionMobilityProfiles.Generate(1234, "adult/" + i)).ToArray();
            Check(profiles.All(p => p.ageYears >= 18 && p.ageYears <= 85), "A child or unsupported adult rig was sampled.");
            Check(profiles.Select(p => p.ageYears).Distinct().Count() > 50 && profiles.Select(p => p.condition).Distinct().Count() == 3,
                "Age or health condition did not vary across the population.");
            Check(profiles.Select(p => p.preferredWalkSpeed).Distinct().Count() > 1900 &&
                profiles.All(p => p.preferredWalkSpeed >= .45f && p.preferredWalkSpeed <= 1.55f &&
                    p.preferredFastWalkSpeed > p.preferredWalkSpeed && p.preferredFastWalkSpeed <= 2.2f), "Profile paces are repetitive or outside finite adult bounds.");
        }

        static void IndividualFactors()
        {
            float previous = 1f;
            for (int age = 18; age <= 85; ++age)
            { float factor = DecisionMobilityProfiles.AgeSpeedFactor(age); Check(factor > 0f && factor <= previous, "Age factor is nonpositive or increases with age."); previous = factor; }
            foreach (var p in Enumerable.Range(0, 1000).Select(i => DecisionMobilityProfiles.Generate(77, "factor/" + i)))
            {
                Check(Near(p.preferredWalkSpeed, Math.Max(.45f, p.baseWalkSpeed * p.ageSpeedFactor * p.healthSpeedFactor)),
                    "Walking pace does not reflect the saved age and health factors.");
                float ratio = p.preferredFastWalkSpeed / p.preferredWalkSpeed;
                Check(p.condition == DecisionMobilityCondition.Healthy ? p.healthSpeedFactor == 1f && ratio >= 1.24f && ratio <= 1.501f :
                    p.condition == DecisionMobilityCondition.ReducedEndurance ? p.healthSpeedFactor >= .78f && p.healthSpeedFactor <= .90f && ratio >= 1.099f && ratio <= 1.251f :
                    p.healthSpeedFactor >= .52f && p.healthSpeedFactor <= .70f && ratio >= 1.039f && ratio <= 1.151f,
                    "Health condition did not constrain the walking or urgency profile.");
            }
        }

        static void PartyPace()
        {
            var plans = Plans();
            Check(plans.Any(p => string.IsNullOrEmpty(p.groupId)) && plans.Any(p => !string.IsNullOrEmpty(p.groupId)), "Fixture needs solos and parties.");
            foreach (var solo in plans.Where(p => string.IsNullOrEmpty(p.groupId)))
                Check(solo.mobilityProfile.groupPaceFactor == 1f && solo.walkSpeed == solo.mobilityProfile.preferredWalkSpeed &&
                    solo.fastWalkSpeed == solo.mobilityProfile.preferredFastWalkSpeed, "A solo traveller inherited a party pace.");
            foreach (var group in plans.Where(p => !string.IsNullOrEmpty(p.groupId)).GroupBy(p => p.groupId))
            {
                float factor = group.First().mobilityProfile.groupPaceFactor;
                float walk = group.Min(p => p.mobilityProfile.preferredWalkSpeed), fast = group.Min(p => p.mobilityProfile.preferredFastWalkSpeed);
                Check(factor >= .88f && factor <= .98f && group.All(p => p.mobilityProfile.groupPaceFactor == factor &&
                    Near(p.walkSpeed, walk * factor) && Near(p.fastWalkSpeed, fast * factor) &&
                    p.walkSpeed <= p.mobilityProfile.preferredWalkSpeed && p.fastWalkSpeed <= p.mobilityProfile.preferredFastWalkSpeed),
                    "A party exceeds its slowest member's pace or members disagree on the group factor.");
                Check(group.Select(p => p.mobilityProfile.preferredWalkSpeed).Distinct().Count() == group.Count(), "Companions shared the same mutable or sampled individual profile.");
            }
        }

        static void ArrivalStreamPreserved()
        {
            // Produced by the unchanged arrival generator with the mobility post-pass omitted.
            Check(ContextFingerprint(Plans()) == "92bd83a43defac38", "Mobility sampling changed the existing demand, group, needs, seat or arrival-time random stream.");
        }

        static void SnapshotIsolation()
        {
            var before = Plans(); string context = ContextFingerprint(before), profiles = string.Join("\n", before.Select(ProfileKey));
            var service = Service("mobility-fixture"); service.arrivalSeconds += 400f; service.departureSeconds += 400f;
            var after = DecisionArrivalPlanner.Generate(new[] { service }, new[] { "west", "east" }, Settings()).ToDictionary(p => p.journeyId);
            Check(before.All(p => ProfileKey(p) == ProfileKey(after[p.journeyId]) && !ReferenceEquals(p.mobilityProfile, after[p.journeyId].mobilityProfile)),
                "Timetable edits changed stable journey profiles or reused mutable profile objects.");
            after.Values.First().mobilityProfile.ageYears = 18;
            Check(ContextFingerprint(before) == context && string.Join("\n", before.Select(ProfileKey)) == profiles,
                "A later manifest mutated an earlier snapshot.");
        }

        static void IndependentSeed()
        {
            var plans = Plans(); string context = ContextFingerprint(plans), profiles = string.Join("\n", plans.Select(ProfileKey));
            DecisionMobilityProfiles.ApplyToPlans(plans, 983);
            Check(ContextFingerprint(plans) == context && string.Join("\n", plans.Select(ProfileKey)) != profiles,
                "Resampling only mobility changed nonmobility context or did not change profiles.");
        }

        static void InvalidInputs()
        {
            Reject(() => DecisionMobilityProfiles.Generate(3, null));
            Reject(() => DecisionMobilityProfiles.Generate(3, " "));
            Reject(() => DecisionMobilityProfiles.ApplyToPlans(null, 3));
            Reject(() => DecisionMobilityProfiles.ApplyToPlans(new DecisionArrivalPlan[] { null }, 3));
            Reject(() => DecisionMobilityProfiles.ApplyToPlans(new[] { new DecisionArrivalPlan { journeyId = "same" }, new DecisionArrivalPlan { journeyId = "same" } }, 3));
            Reject(() => DecisionMobilityProfiles.AgeSpeedFactor(17)); Reject(() => DecisionMobilityProfiles.AgeSpeedFactor(86));
            DecisionMobilityProfiles.ApplyToPlans(Array.Empty<DecisionArrivalPlan>(), 3);
        }

        static DecisionArrivalSettings Settings() => new DecisionArrivalSettings { scenarioDate = "2026-10-20", seed = 20261020, visualActorRatio = .5d, groupPeopleFraction = .4d };
        static DecisionArrivalService Service(string id) => new DecisionArrivalService { serviceId = id, trainNumber = "M42", trackNumber = 2,
            expectedBoarding = 190, expectedAlighting = 130, throughPassengers = 20, coachSeatCapacities = new[] { 55, 55, 55, 55 },
            arrivalSeconds = 6000f, departureSeconds = 7200f, doorSeconds = 8f };
        static List<DecisionArrivalPlan> Plans() => DecisionArrivalPlanner.Generate(new[] { Service("mobility-fixture") }, new[] { "west", "east" }, Settings());
        static string ProfileKey(DecisionArrivalPlan plan)
        {
            var p = plan.mobilityProfile;
            return FormattableString.Invariant($"{p.ageYears}|{p.condition}|{p.baseWalkSpeed:R}|{p.ageSpeedFactor:R}|{p.healthSpeedFactor:R}|{p.preferredWalkSpeed:R}|{p.preferredFastWalkSpeed:R}|{p.groupPaceFactor:R}|{plan.walkSpeed:R}|{plan.fastWalkSpeed:R}");
        }
        static string ContextFingerprint(IEnumerable<DecisionArrivalPlan> plans)
        {
            ulong hash = 14695981039346656037UL;
            foreach (var p in plans.OrderBy(p => p.journeyId, StringComparer.Ordinal))
            {
                string row = FormattableString.Invariant($"{p.journeyId}|{p.serviceId}|{p.trainNumber}|{p.scenarioDate}|{p.groupId}|{p.groupMemberIndex}|{p.groupSize}|{p.entranceId}|{p.trackNumber}|{p.purpose}|{p.arrivalProfile}|{p.representedPassengers}|{p.coachNumber}|{string.Join(",", p.seatNumbers)}|{p.ticketId}|{Bits(p.plannedEntrySeconds)}|{Bits(p.publishedArrivalSeconds)}|{Bits(p.publishedDepartureSeconds)}|{Bits(p.leadSeconds)}|{p.clippedAtSimulationStart}|{Bits(p.familiarity)}|{Bits(p.safetyMarginSeconds)}|{Bits(p.needs.thirst)}|{Bits(p.needs.toilet)}|{Bits(p.needs.fatigue)}|{Bits(p.needs.shopping)}|{Bits(p.needs.information)}|{p.alightingServiceVisitor}|{p.alightingServiceVisitLimit}|{Bits(p.alightingStopoverSeconds)}\n");
                foreach (char c in row) hash = unchecked((hash ^ c) * 1099511628211UL);
            }
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
        static string Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0).ToString("x8", CultureInfo.InvariantCulture);
        static bool Near(float a, float b) => Math.Abs(a - b) < .00001f;
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Reject(Action action)
        { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Invalid mobility input was accepted."); }
    }
}
