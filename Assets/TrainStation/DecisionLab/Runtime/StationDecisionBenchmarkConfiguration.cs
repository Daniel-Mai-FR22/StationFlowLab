using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace StationFlow.TrainStation.DecisionLab.Diagnostics
{
    // Pure CLI configuration shared by the opt-in Player driver and standalone tests.
    [Serializable]
    public sealed class StationDecisionBenchmarkConfiguration
    {
        public string label = "capture-4", variant = "all-data-video", date = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), output;
        public string admission = "default", renderProfile = "default", dataWriter = "default", cameraSelection = "hall", skeletons = "default";
        public int cameras = 4, seed = 20261020, fps = 30, width = 1280, height = 720, readbackSlots;
        public double warmup = 20, seconds = 20;
        public float simulationSpeed = 1;

        public static StationDecisionBenchmarkConfiguration Parse(string[] args, string defaultOutputRoot)
        {
            var config = new StationDecisionBenchmarkConfiguration();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; ++i)
            {
                string argument = args[i];
                if (!argument.StartsWith("--station-benchmark-", StringComparison.Ordinal)) continue;
                string key = argument == "--station-benchmark-playback-fps" ? "--station-benchmark-fps" : argument;
                Require(seen.Add(key), "Duplicate benchmark argument: " + argument);
                Require(i + 1 < args.Length, "Missing argument: " + argument);
                string value = args[++i];
                switch (key)
                {
                    case "--station-benchmark-label": config.label = value; break;
                    case "--station-benchmark-variant": config.variant = value; break;
                    case "--station-benchmark-output": config.output = Path.GetFullPath(value); break;
                    case "--station-benchmark-cameras":
                        config.cameraSelection = value == "all" ? "all" : "hall";
                        config.cameras = value == "all" ? 0 : int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--station-benchmark-seed": config.seed = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--station-benchmark-date": config.date = value; break;
                    case "--station-benchmark-seconds": config.seconds = double.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--station-benchmark-warmup": config.warmup = double.Parse(value, CultureInfo.InvariantCulture); break;
                    // AVI playback metadata only; capture attempts follow admitted Unity frames.
                    case "--station-benchmark-fps": config.fps = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--station-benchmark-sim-speed": config.simulationSpeed = float.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--station-benchmark-admission": config.admission = value; break;
                    case "--station-benchmark-readback-slots": config.readbackSlots = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--station-benchmark-render-profile": config.renderProfile = value; break;
                    case "--station-benchmark-data-writer": config.dataWriter = value; break;
                    case "--station-benchmark-skeletons": config.skeletons = value; break;
                    case "--station-benchmark-resolution":
                        if (value == "480") { config.width = 854; config.height = 480; }
                        else if (value == "720") { config.width = 1280; config.height = 720; }
                        else if (value == "HD" || value == "1080") { config.width = 1920; config.height = 1080; }
                        else throw new ArgumentException("Resolution must be 480, 720 or HD.");
                        break;
                    default: throw new ArgumentException("Unknown argument: " + argument);
                }
            }
            Require(!string.IsNullOrWhiteSpace(config.label) && config.label.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'), "Label must be a safe single name.");
            Require(new[] { "observation", "all-off", "data-only", "all-data-video", "video-only" }.Contains(config.variant), "Unknown variant.");
            bool video = config.variant == "video-only" || config.variant == "all-data-video";
            Require(video ? config.cameraSelection == "all" || new[] { 1, 2, 4 }.Contains(config.cameras) : config.cameraSelection == "hall" && config.cameras == 0, "Video requires 1/2/4 Hall cameras or explicit all; nonvideo requires 0.");
            Require(config.warmup >= 5 && config.warmup <= 120 && config.seconds >= 20 && config.seconds <= 300, "Require 5–120s warmup and 20–300s measurement.");
            Require(config.fps >= 1 && config.fps <= 50, "Nominal AVI playback FPS must be 1–50; it does not control capture cadence.");
            Require(config.simulationSpeed == 1, "Benchmark speed must be 1 to match the collection speed lock.");
            Require(new[] { "default", "baseline", "common" }.Contains(config.admission), "Admission must be default, baseline or common.");
            Require(config.readbackSlots >= 0 && config.readbackSlots <= 4, "Readback slots must be 0 (production default), 1, 2, 3 or 4.");
            Require(new[] { "default", "full", "ao-half", "ao-off", "balanced", "copies", "lean" }.Contains(config.renderProfile), "Unknown capture rendering profile.");
            Require(new[] { "default", "main", "background" }.Contains(config.dataWriter), "Data writer must be default, main or background.");
            Require(new[] { "default", "on", "off" }.Contains(config.skeletons), "Skeleton override must be default, on or off.");
            Require(config.skeletons != "on" || config.variant != "observation" && config.variant != "all-off", "Skeleton capture requires a data or video capture variant.");
            Require(config.variant != "observation" || config.dataWriter == "default", "A data writer override requires a capture session.");
            Require(video || config.admission == "default" && config.readbackSlots == 0 && config.renderProfile == "default", "Video pipeline controls require a video variant.");
            Require(DateTime.TryParseExact(config.date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ignored), "Date must be yyyy-MM-dd.");
            if (string.IsNullOrWhiteSpace(config.output)) config.output = Path.Combine(defaultOutputRoot, config.label);
            return config;
        }

        public bool ResolveSkeletons() => skeletons == "on" || skeletons == "default" && (variant == "all-data-video" || variant == "data-only");

        // Explicit all selects every installed CCTV ID. Numeric controls retain the original first-N Hall cameras.
        public string[] ResolveCameraIds(IEnumerable<string> installedIds, IEnumerable<string> hallIds)
        {
            if (variant != "video-only" && variant != "all-data-video") return Array.Empty<string>();
            var installed = SortedUniqueCameraIds(installedIds, "installed", false);
            if (cameraSelection == "all") return installed;
            Require(cameraSelection == "hall" && new[] { 1, 2, 4 }.Contains(cameras), "Invalid Hall camera selection.");
            var hall = SortedUniqueCameraIds(hallIds, "Hall controls", false);
            Require(hall.Length >= cameras, "Not enough authored Hall camera controls.");
            var selected = hall.Take(cameras).ToArray();
            foreach (string id in selected) Require(installed.Contains(id, StringComparer.Ordinal), "Selected Hall camera is not installed: " + id);
            return selected;
        }

        public static void RequireExactCameraSet(IEnumerable<string> expected, IEnumerable<string> actual, string role)
        {
            var wanted = SortedUniqueCameraIds(expected, "expected " + role, true);
            var observed = SortedUniqueCameraIds(actual, "actual " + role, true);
            Require(wanted.SequenceEqual(observed, StringComparer.Ordinal), "Camera IDs differ from selection: " + role);
        }

        static string[] SortedUniqueCameraIds(IEnumerable<string> ids, string role, bool allowEmpty)
        {
            Require(ids != null, "Camera IDs are missing: " + role);
            var result = ids.ToArray();
            Require(allowEmpty || result.Length > 0, "No cameras are installed: " + role);
            Require(result.All(id => !string.IsNullOrWhiteSpace(id)), "Camera IDs must be nonempty: " + role);
            Require(result.Distinct(StringComparer.Ordinal).Count() == result.Length, "Duplicate camera IDs: " + role);
            Array.Sort(result, StringComparer.Ordinal); return result;
        }

        static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
    }
}
