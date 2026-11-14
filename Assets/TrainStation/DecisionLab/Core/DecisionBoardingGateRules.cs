using System;

namespace StationFlow.TrainStation.DecisionLab
{
    public enum DecisionPlatformGateAction { Wait, Replan, Reapproach, Board, Reject }

    /// <summary>A displaced waiting traveller must physically regain its assigned anchor; doors and cutoff remain mandatory.</summary>
    public static class DecisionPlatformGate
    {
        public static DecisionPlatformGateAction Evaluate(DecisionTrainStatus status, bool matchingPlatform,
            bool trainReady, bool doorsOpen, bool beforeCutoff, double anchorDistance)
        {
            if (!beforeCutoff || status == DecisionTrainStatus.Cancelled || status == DecisionTrainStatus.Closed || status == DecisionTrainStatus.Departed)
                return DecisionPlatformGateAction.Replan;
            bool finiteDistance = !double.IsNaN(anchorDistance) && !double.IsInfinity(anchorDistance) && anchorDistance >= 0d;
            if (matchingPlatform && finiteDistance && anchorDistance >= 1d) return DecisionPlatformGateAction.Reapproach;
            if (status != DecisionTrainStatus.Boarding) return DecisionPlatformGateAction.Wait;
            return matchingPlatform && trainReady && doorsOpen && finiteDistance && anchorDistance < 1d ?
                DecisionPlatformGateAction.Board : DecisionPlatformGateAction.Reject;
        }
    }
}
