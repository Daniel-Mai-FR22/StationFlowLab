using UnityEngine;

namespace StationFlow.TrainStation.DecisionLab
{
    [CreateAssetMenu(fileName = "StationDecisionDayScenario", menuName = "StationFlow/Train Station/Daily arrival scenario")]
    public sealed class StationDecisionDayScenario : ScriptableObject
    {
        public string scenarioDate = "2026-10-06";
        public int seed = 20261006;
        [Header("Runtime initialization")]
        [Tooltip("Create a fresh valid timetable and passenger population on the current local date for each launch. Saved templates are never changed.")]
        public bool randomizeOnLaunch = true;
        [Tooltip("Use a recorded seed for an intentional replay instead of a new random seed.")]
        public bool useFixedRuntimeSeed;
        public int fixedRuntimeSeed = 20261020;
        [Tooltip("Optional ISO date for a fixed-seed replay. Empty uses the current local date.")]
        public string replayDateIso = "";
        [Range(.01f, 1f)] public float visualActorRatio = .2f;
        [Range(0f, 1f)] public float earlyRatio = .25f, ordinaryRatio = .6f, lateRatio = .15f, groupPeopleFraction = .35f;
        public Vector2 earlyLeadMinutes = new Vector2(20f, 40f), ordinaryLeadMinutes = new Vector2(10f, 20f), lateLeadMinutes = new Vector2(3f, 7f);
        [Header("Native avoidance")]
        [Tooltip("Global native NavMesh collision prediction horizon in seconds. Does not add manual steering or stops.")]
        [Range(.5f, 5f)] public float avoidancePredictionSeconds = 2f;
        [Range(1f, 8f)] public float defaultTimeScale = 4f;
        [Range(1, 128)] public int activeActorLimit = 128;
        [Min(.2f)] public float minimumEntryIntervalSeconds = 1.1f;
        [Min(1f)] public float groupCohesionDistance = 4f;
        [Min(1f)] public float maximumRegroupSeconds = 8f;
        public DecisionAlightingStopoverSettings alightingStopovers = new DecisionAlightingStopoverSettings();
        [TextArea] public string assumptions = "Illustrative fictional demand. Each visible actor represents a weighted passenger cohort; visible groups contain2–4 actors. Lead ranges and group share are configurable assumptions, not fitted station observations.";

        public DecisionArrivalSettings PlannerSettings() => new DecisionArrivalSettings { scenarioDate = scenarioDate, seed = seed,
            visualActorRatio = visualActorRatio, earlyRatio = earlyRatio, ordinaryRatio = ordinaryRatio, lateRatio = lateRatio,
            groupPeopleFraction = groupPeopleFraction, earlyMinSeconds = earlyLeadMinutes.x * 60d, earlyMaxSeconds = earlyLeadMinutes.y * 60d,
            ordinaryMinSeconds = ordinaryLeadMinutes.x * 60d, ordinaryMaxSeconds = ordinaryLeadMinutes.y * 60d,
            lateMinSeconds = lateLeadMinutes.x * 60d, lateMaxSeconds = lateLeadMinutes.y * 60d,
            alightingStopovers = alightingStopovers };
    }
}
