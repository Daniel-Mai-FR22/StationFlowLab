using System;
using System.Linq;

namespace StationFlow.TrainStation.DecisionLab
{
    [Serializable]
    public sealed class StationDecisionCaptureOptions
    {
        public bool video = true, timetable = true, profiles = true, trajectories = true, animations = true, skeletons = true, decisions = true, events = true;
        public string[] selectedCameraIds = Array.Empty<string>();
        public int videoWidth = 1280, videoHeight = 720, jpegQuality = 75;
        // Nominal AVI playback rate only, never the requested or actual capture cadence.
        public int videoFps = 30;
        public StationDecisionCaptureOptions Copy() => new StationDecisionCaptureOptions
        { video = video, timetable = timetable, profiles = profiles, trajectories = trajectories, animations = animations, skeletons = skeletons, decisions = decisions, events = events,
            selectedCameraIds = (selectedCameraIds ?? Array.Empty<string>()).ToArray(), videoWidth = videoWidth,
            videoHeight = videoHeight, videoFps = videoFps, jpegQuality = jpegQuality };

        public int VideoResolutionIndex => Nearest(videoHeight, 480, 720, 1080);
        public int VideoFrameRateIndex => Nearest(videoFps, 20, 30, 50);
        public void SetVideoResolution(int index)
        {
            switch (index)
            {
                case 0: videoWidth = 854; videoHeight = 480; break;
                case 1: videoWidth = 1280; videoHeight = 720; break;
                case 2: videoWidth = 1920; videoHeight = 1080; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
        public void SetVideoFrameRate(int index)
        {
            switch (index)
            {
                case 0: videoFps = 20; break;
                case 1: videoFps = 30; break;
                case 2: videoFps = 50; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
        /// <summary>Maps saved resolution to the supported UI choices. Nominal AVI playback FPS has no UI control.</summary>
        public void NormalizeVideoChoicesForUi()
        {
            SetVideoResolution(VideoResolutionIndex);
        }
        static int Nearest(int value, int first, int second, int third)
        {
            long a = Math.Abs((long)value - first), b = Math.Abs((long)value - second), c = Math.Abs((long)value - third);
            return a <= b && a <= c ? 0 : b <= c ? 1 : 2;
        }
        public void Validate()
        {
            if (videoWidth < 160 || videoWidth > 1920 || videoHeight < 90 || videoHeight > 1080 || videoFps < 1 || videoFps > 50 || jpegQuality < 10 || jpegQuality > 100)
                throw new ArgumentException("Video must be 160–1920 × 90–1080 pixels, nominal AVI playback 1–50 fps and JPEG quality 10–100.");
            if (selectedCameraIds != null && selectedCameraIds.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Camera selections must contain nonempty camera IDs.");
        }
    }
}
