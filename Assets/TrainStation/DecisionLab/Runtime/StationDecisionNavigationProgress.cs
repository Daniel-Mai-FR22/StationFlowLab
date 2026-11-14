using System.Collections.Generic;
using StationFlow.TrainStation.Social;
using UnityEngine;
using UnityEngine.AI;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Read-only native-path progress estimate shared by Lab travel and its diagnostics.</summary>
    public static class StationDecisionNavigationProgress
    {
        // All callers run on Unity's main thread; reuse only the scalar conversion buffer.
        static readonly List<DecisionPathPoint> points = new List<DecisionPathPoint>(32);

        public static bool TryGetRemainingDistance(StationActorMotion motion, out float remaining)
        {
            remaining = float.PositiveInfinity;
            var agent = motion ? motion.agent : null;
            if (!agent || !agent.enabled || !agent.isOnNavMesh || agent.pathPending || !agent.hasPath ||
                agent.pathStatus != NavMeshPathStatus.PathComplete) return false;
            float native = agent.remainingDistance;
            if (!float.IsNaN(native) && !float.IsInfinity(native))
            { if (native < 0f) return false; remaining = native; return true; }
            points.Clear();
            foreach (var corner in agent.path.corners) points.Add(Point(corner));
            if (!DecisionPathDistance.TryResolve(native, true, false, agent.isPathStale, Point(motion.transform.position),
                Point(agent.nextPosition), Point(agent.destination), points, out double length) || length > float.MaxValue) return false;
            remaining = (float)length; return true;
        }

        static DecisionPathPoint Point(Vector3 value) => new DecisionPathPoint(value.x, value.y, value.z);
    }
}
