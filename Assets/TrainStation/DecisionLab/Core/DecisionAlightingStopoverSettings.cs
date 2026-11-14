using System;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Illustrative, seeded station visits after alighting; independent of the departed train's clock.</summary>
    [Serializable]
    public sealed class DecisionAlightingStopoverSettings
    {
        public float serviceVisitorFraction = .4f;
        public int minimumVisits = 1, maximumVisits = 2;
        public float minimumBudgetSeconds = 300f, maximumBudgetSeconds = 720f;
        public float infoPreference = 1f, shopPreference = 1f, toiletPreference = 1f, exitPreference = .15f;
        public float minimumInformationNeed = .3f, minimumShoppingNeed = .2f, minimumToiletNeed = .2f;

        public void Validate()
        {
            if (!Unit(serviceVisitorFraction) || minimumVisits < 1 || maximumVisits < minimumVisits || maximumVisits > 2 ||
                !Positive(minimumBudgetSeconds) || !Positive(maximumBudgetSeconds) || maximumBudgetSeconds < minimumBudgetSeconds ||
                !NonNegative(infoPreference) || !NonNegative(shopPreference) || !NonNegative(toiletPreference) || !Positive(exitPreference) ||
                serviceVisitorFraction > 0f && infoPreference + shopPreference + toiletPreference <= 0f ||
                !Unit(minimumInformationNeed) || !Unit(minimumShoppingNeed) || !Unit(minimumToiletNeed))
                throw new ArgumentException("Invalid alighting station-service share, visit limit, independent budget, weights or needs.");
        }

        public void ApplyToPlan(DecisionArrivalPlan plan, int seed)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            Validate();
            plan.alightingServiceVisitor = false; plan.alightingServiceVisitLimit = 0; plan.alightingStopoverSeconds = 0f;
            if (plan.purpose != DecisionJourneyPurpose.Alighting) return;
            string identity = string.IsNullOrEmpty(plan.groupId) ? plan.journeyId : plan.groupId;
            var random = new Random(unchecked(seed ^ Hash("alighting-stopover|" + identity) ^ 1299721));
            if (random.NextDouble() >= serviceVisitorFraction) return;
            plan.alightingServiceVisitor = true;
            plan.alightingServiceVisitLimit = random.Next(minimumVisits, maximumVisits + 1);
            plan.alightingStopoverSeconds = minimumBudgetSeconds +
                (float)random.NextDouble() * (maximumBudgetSeconds - minimumBudgetSeconds);
            if (plan.needs == null) plan.needs = new DecisionNeeds();
            plan.needs.information = Math.Max(plan.needs.information, minimumInformationNeed);
            plan.needs.shopping = Math.Max(plan.needs.shopping, minimumShoppingNeed);
            plan.needs.toilet = Math.Max(plan.needs.toilet, minimumToiletNeed);
        }

        static int Hash(string value)
        { uint hash = 2166136261; foreach (char c in value ?? "") hash = unchecked((hash ^ c) * 16777619); return unchecked((int)hash); }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Positive(float value) => Finite(value) && value > 0f;
        static bool NonNegative(float value) => Finite(value) && value >= 0f;
        static bool Unit(float value) => NonNegative(value) && value <= 1f;
    }
}
