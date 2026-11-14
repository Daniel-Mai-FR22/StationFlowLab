using System;
using System.Collections.Generic;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Rates from original render timestamps. Queries do not erase delayed worker history.</summary>
    public sealed class StationDecisionVideoRateTracker
    {
        readonly object gate = new object();
        readonly Queue<double> renderTimes = new Queue<double>();
        double? previousRenderTime;

        public StationDecisionVideoRateSample Add(double renderRealSeconds)
        {
            Validate(renderRealSeconds);
            lock (gate)
            {
                if (previousRenderTime.HasValue && renderRealSeconds <= previousRenderTime.Value)
                    throw new ArgumentException("Render timestamps must increase strictly.", nameof(renderRealSeconds));
                double? interval = previousRenderTime.HasValue ? renderRealSeconds - previousRenderTime.Value : (double?)null;
                previousRenderTime = renderRealSeconds;
                while (renderTimes.Count > 0 && renderTimes.Peek() <= renderRealSeconds - 1d) renderTimes.Dequeue();
                renderTimes.Enqueue(renderRealSeconds);
                return new StationDecisionVideoRateSample(interval, interval.HasValue ? 1d / interval.Value : (double?)null, renderTimes.Count);
            }
        }

        /// <summary>Saved render stamps in (realNow - 1, realNow], divided by one real second; startup may contain less than a second of observations.</summary>
        public double GetFpsAt(double realNow) => GetFrameCountAt(realNow);
        public int GetFrameCountAt(double realNow)
        {
            Validate(realNow);
            lock (gate)
            {
                int count = 0;
                foreach (double timestamp in renderTimes) if (timestamp > realNow - 1d && timestamp <= realNow) ++count;
                return count;
            }
        }
        static void Validate(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d) throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    public readonly struct StationDecisionVideoRateSample
    {
        public readonly double? intervalRealSeconds, fpsInstantaneous;
        public readonly double rollingFps1s;
        public StationDecisionVideoRateSample(double? intervalRealSeconds, double? fpsInstantaneous, double rollingFps1s)
        { this.intervalRealSeconds = intervalRealSeconds; this.fpsInstantaneous = fpsInstantaneous; this.rollingFps1s = rollingFps1s; }
    }
}
