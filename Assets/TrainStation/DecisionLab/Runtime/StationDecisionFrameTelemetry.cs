using System;
using System.Diagnostics;
using UnityEngine;

namespace StationFlow.TrainStation.DecisionLab
{
    [Serializable]
    public sealed class StationDecisionCameraFrameRate
    {
        public string cameraId;
        public double videoFpsRolling1s;
        public long framesWritten, requestsDropped;
    }

    [Serializable]
    public sealed class StationDecisionMeasuredFrameRates
    {
        public double windowSeconds = 1d;
        public double gameFpsRolling1s;
        public StationDecisionCameraFrameRate[] cameras = Array.Empty<StationDecisionCameraFrameRate>();
    }

    /// <summary>Observed Unity-frame rate. Sampling is independent of simulation speed and pause.</summary>
    [DefaultExecutionOrder(1500), DisallowMultipleComponent]
    public sealed class StationDecisionFrameTelemetry : MonoBehaviour
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly StationDecisionVideoRateTracker gameFrames = new StationDecisionVideoRateTracker();
        StationDecisionCaptureSession session;
        int previousFrame = -1;
        string captureRunId;
        StationDecisionVideoRateTracker capturedGameFrames;
        int previousCaptureFrame = -1;
        double nextReportAt = 1d;

        public double GameFpsRolling1s { get; private set; }
        public void Initialize(StationDecisionCaptureSession owner) { session = owner; }

        void LateUpdate()
        {
            double now = clock.Elapsed.TotalSeconds;
            if (previousFrame != Time.frameCount)
            {
                gameFrames.Add(now);
                previousFrame = Time.frameCount;
            }
            GameFpsRolling1s = gameFrames.GetFpsAt(now);
            if (!session || !session.IsRecording)
            {
                captureRunId = null;
                capturedGameFrames = null;
                nextReportAt = 1d;
                return;
            }
            if (captureRunId != session.RunId)
            {
                captureRunId = session.RunId;
                capturedGameFrames = new StationDecisionVideoRateTracker();
                previousCaptureFrame = -1;
                nextReportAt = 1d;
            }
            var stamp = session.GetStamp();
            if (stamp == null) return;
            if (previousCaptureFrame != Time.frameCount)
            {
                capturedGameFrames.Add(stamp.realElapsedSeconds);
                previousCaptureFrame = Time.frameCount;
            }
            // During collection both game and video windows use the exact shared runtime stamp.
            GameFpsRolling1s = capturedGameFrames.GetFpsAt(stamp.realElapsedSeconds);
            if (stamp.realElapsedSeconds < nextReportAt) return;
            session.RecordMeasuredPerformance(new StationDecisionMeasuredFrameRates {
                windowSeconds = 1d,
                gameFpsRolling1s = GameFpsRolling1s,
                cameras = session.GetActualVideoRates()
            });
            // No fabricated catch-up samples if a runtime frame took longer than a reporting interval.
            nextReportAt = Math.Floor(stamp.realElapsedSeconds) + 1d;
        }
    }
}