using System;
using System.Collections.Generic;

namespace StationFlow.TrainStation.DecisionLab
{
    public struct DecisionPathPoint
    {
        public double x, y, z;
        public DecisionPathPoint(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    }

    /// <summary>Distance estimate for progress detection only; never grants arrival or changes a native route.</summary>
    public static class DecisionPathDistance
    {
        public static bool TryResolve(double nativeRemaining, bool hasCompletePath, bool pathPending, bool pathStale,
            DecisionPathPoint root, DecisionPathPoint navigationPosition, DecisionPathPoint destination,
            IReadOnlyList<DecisionPathPoint> corners, out double remaining)
        {
            remaining = double.PositiveInfinity;
            if (!hasCompletePath || pathPending) return false;
            if (Finite(nativeRemaining))
            { if (nativeRemaining < 0d) return false; remaining = nativeRemaining; return true; }
            // Unity may report Infinity while an agent traverses a valid complete corridor.
            // Only its current corridor is eligible: no new path, straight-line substitute or detached root.
            if (pathStale || corners == null || corners.Count < 2 || !Finite(root) || !Finite(navigationPosition) ||
                !Finite(destination) || Distance(root, navigationPosition) > .15d || !Finite(corners[0]) ||
                !Finite(corners[corners.Count - 1]) || Distance(corners[0], navigationPosition) > .25d ||
                Distance(corners[corners.Count - 1], destination) > .2d) return false;
            double length = Distance(navigationPosition, corners[0]);
            for (int i = 1; i < corners.Count; ++i)
            {
                if (!Finite(corners[i])) return false;
                length += Distance(corners[i - 1], corners[i]);
            }
            if (!Finite(length)) return false;
            remaining = length; return true;
        }

        static double Distance(DecisionPathPoint a, DecisionPathPoint b)
        { double x = a.x - b.x, y = a.y - b.y, z = a.z - b.z; return Math.Sqrt(x * x + y * y + z * z); }
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static bool Finite(DecisionPathPoint value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
