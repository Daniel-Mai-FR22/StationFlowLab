using UnityEngine;

namespace StationFlow.TrainStation.Social
{
    /// <summary>Inert compatibility marker for older assets; NavMeshAgent exclusively owns locomotion.</summary>
    [AddComponentMenu(""), DisallowMultipleComponent]
    public sealed class StationCrowdCoordinator : MonoBehaviour
    {
        // Retained API cannot activate a velocity cap, transform correction or planner.
        // The native authoring migration removes this component from saved assets.
        public bool enablePrediction { get => false; set { } }
        public bool enableContactSeparation { get => false; set { } }
        public bool IsActiveCoordinator => false;
        public int YieldingActors => 0;
        public int CorrectedActors => 0;
        public int NeighbourChecks => 0;
        public int CorrectionCount => 0;
        public float MaxCorrectionDistance => 0f;
        void OnEnable() { enabled = false; }
    }
}
