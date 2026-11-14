using System;
using System.Collections.Generic;

namespace StationFlow.TrainStation.DecisionLab
{
    public enum DecisionMobilityCondition { Healthy, ReducedEndurance, MobilityLimited }

    /// <summary>A synthetic adult mobility snapshot, sampled once per immutable journey, rather than per pooled actor.</summary>
    [Serializable]
    public sealed class DecisionMobilityProfile
    {
        public int ageYears;
        public DecisionMobilityCondition condition;
        public float baseWalkSpeed, ageSpeedFactor, healthSpeedFactor;
        public float preferredWalkSpeed, preferredFastWalkSpeed;
        public float groupPaceFactor = 1f;
    }

    /// <summary>Illustrative simulation parameters, not clinical measurements or a fitted population model.</summary>
    public static class DecisionMobilityProfiles
    {
        public const string ModelVersion = "adult-mobility-v1";

        public static DecisionMobilityProfile Generate(int seed, string journeyId)
        {
            if (string.IsNullOrWhiteSpace(journeyId)) throw new ArgumentException("A mobility profile requires a stable journey ID.");
            var random = new Random(unchecked(seed ^ Hash("mobility|" + journeyId)));
            double ageDraw = random.NextDouble();
            int age = ageDraw < .42d ? random.Next(18, 40) : ageDraw < .8d ? random.Next(40, 65) : random.Next(65, 86);
            double healthDraw = random.NextDouble();
            // Older cohorts have a greater probability of a reduced-mobility profile.
            double healthyShare = age < 65 ? .78d : .55d;
            double limitedShare = age < 65 ? .06d : .15d;
            var condition = healthDraw < healthyShare ? DecisionMobilityCondition.Healthy :
                healthDraw < 1d - limitedShare ? DecisionMobilityCondition.ReducedEndurance : DecisionMobilityCondition.MobilityLimited;
            float baseline = Range(random, 1.20f, 1.55f);
            float ageFactor = AgeSpeedFactor(age);
            float healthFactor = condition == DecisionMobilityCondition.Healthy ? 1f :
                condition == DecisionMobilityCondition.ReducedEndurance ? Range(random, .78f, .90f) : Range(random, .52f, .70f);
            float walk = Math.Max(.45f, baseline * ageFactor * healthFactor);
            float fastFactor = condition == DecisionMobilityCondition.Healthy ? Range(random, 1.25f, 1.50f) :
                condition == DecisionMobilityCondition.ReducedEndurance ? Range(random, 1.10f, 1.25f) : Range(random, 1.04f, 1.15f);
            return new DecisionMobilityProfile { ageYears = age, condition = condition, baseWalkSpeed = baseline,
                ageSpeedFactor = ageFactor, healthSpeedFactor = healthFactor,
                preferredWalkSpeed = walk, preferredFastWalkSpeed = Math.Min(2.2f, walk * fastFactor) };
        }

        public static float AgeSpeedFactor(int ageYears)
        {
            if (ageYears < 18 || ageYears > 85) throw new ArgumentOutOfRangeException(nameof(ageYears), "Existing avatars use adult rigs (18–85 years).");
            if (ageYears < 40) return 1f;
            if (ageYears < 65) return .98f - (ageYears - 40) / 24f * .10f;
            return .86f - (ageYears - 65) / 20f * .16f;
        }

        /// <summary>Called only while building a new plan. It does not consume any arrival, need or ticket random stream.</summary>
        public static void ApplyToPlans(IReadOnlyList<DecisionArrivalPlan> plans, int seed)
        {
            if (plans == null) throw new ArgumentNullException(nameof(plans));
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var groups = new Dictionary<string, List<DecisionArrivalPlan>>(StringComparer.Ordinal);
            foreach (var plan in plans)
            {
                if (plan == null || string.IsNullOrWhiteSpace(plan.journeyId) || !identities.Add(plan.journeyId))
                    throw new ArgumentException("Mobility plans require nonempty unique immutable journey IDs.");
                plan.mobilityProfile = Generate(seed, plan.journeyId);
                plan.walkSpeed = plan.mobilityProfile.preferredWalkSpeed;
                plan.fastWalkSpeed = plan.mobilityProfile.preferredFastWalkSpeed;
                if (string.IsNullOrEmpty(plan.groupId)) continue;
                if (!groups.TryGetValue(plan.groupId, out var group)) groups.Add(plan.groupId, group = new List<DecisionArrivalPlan>());
                group.Add(plan);
            }
            foreach (var group in groups)
            {
                if (group.Value.Count < 2) continue;
                float walk = float.PositiveInfinity, fast = float.PositiveInfinity;
                foreach (var member in group.Value)
                { walk = Math.Min(walk, member.mobilityProfile.preferredWalkSpeed); fast = Math.Min(fast, member.mobilityProfile.preferredFastWalkSpeed); }
                // A small, seeded coordination cost increases with party size. No companion is asked to exceed its own pace.
                var random = new Random(unchecked(seed ^ Hash("group-pace|" + group.Key)));
                float factor = group.Value.Count == 2 ? Range(random, .94f, .98f) :
                    group.Value.Count == 3 ? Range(random, .90f, .96f) : Range(random, .88f, .94f);
                foreach (var member in group.Value)
                { member.mobilityProfile.groupPaceFactor = factor; member.walkSpeed = walk * factor; member.fastWalkSpeed = fast * factor; }
            }
        }

        static float Range(Random random, float minimum, float maximum) => minimum + (float)random.NextDouble() * (maximum - minimum);
        static int Hash(string value)
        { uint hash = 2166136261; foreach (char c in value) hash = unchecked((hash ^ c) * 16777619); return unchecked((int)hash); }
    }
}
