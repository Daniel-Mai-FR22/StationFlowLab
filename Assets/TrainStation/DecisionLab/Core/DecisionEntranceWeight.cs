using System;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Relative daily boarding flow through one saved ground-level entrance.</summary>
    [Serializable]
    public sealed class DecisionEntranceWeight
    {
        public string entranceId;
        // Zero keeps an entrance available for exits while disabling its inbound assignment.
        public double weight = 1d;
    }
}
