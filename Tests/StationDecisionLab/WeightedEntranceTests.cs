using System;
using StationFlow.TrainStation.DecisionLab.Editor;

static class WeightedEntranceTests
{
    public static void RunAll(Action<string, Action> run) => StationDecisionEntrancePlannerTests.RunAll(run);
}
