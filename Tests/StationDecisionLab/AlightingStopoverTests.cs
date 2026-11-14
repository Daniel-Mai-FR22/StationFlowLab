using System;
using StationFlow.TrainStation.DecisionLab;

static class AlightingStopoverTests
{
    public static void RunAll(Action<string, Action> run) => StationDecisionAlightingPolicyTests.RunAll(run);
}
