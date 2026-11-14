using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using StationFlow.TrainStation.DecisionLab.Diagnostics;

static class Program
{
    static int checks;
    static void Check(bool condition, string message) { ++checks; if (!condition) throw new Exception(message); }
    static StationDecisionBenchmarkConfiguration Parse(params string[] values) => StationDecisionBenchmarkConfiguration.Parse(values.Contains("--station-benchmark-date") ? values : new[] { "--station-benchmark-date", "2026-10-20" }.Concat(values).ToArray(), Path.GetTempPath());
    static void Reject(string message, params string[] values)
    {
        bool rejected = false;
        try { Parse(values); } catch (ArgumentException) { rejected = true; }
        Check(rejected, message);
    }

    static void Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        var defaults = Parse("-batchmode", "-screen-width", "960");
        Check(defaults.admission == "default" && defaults.readbackSlots == 0 && defaults.renderProfile == "default" && defaults.dataWriter == "default", "Unspecified controls overrode production defaults.");
        Check(defaults.warmup == 20 && defaults.seconds == 20 && defaults.width == 1280 && defaults.height == 720 && defaults.simulationSpeed == 1, "Controlled experiment defaults drifted.");
        Check(defaults.fps == 30 && defaults.cameras == 4 && defaults.cameraSelection == "hall", "Nominal playback metadata or camera count default changed.");
        Check(Path.GetFileName(defaults.output) == defaults.label, "Default output omitted the safe run label.");
        foreach (int slots in new[] { 1, 2, 3, 4 })
            foreach (string admission in new[] { "baseline", "common" })
                foreach (string profile in new[] { "full", "ao-half", "ao-off", "balanced", "copies", "lean" })
                {
                    var config = Parse("--station-benchmark-readback-slots", slots.ToString(), "--station-benchmark-admission", admission, "--station-benchmark-render-profile", profile);
                    Check(config.readbackSlots == slots && config.admission == admission && config.renderProfile == profile && config.fps == 30, "Independent A/B controls changed a different parameter.");
                }
        foreach (string mode in new[] { "main", "background" })
            foreach (string variant in new[] { "all-off", "data-only", "all-data-video", "video-only" })
            {
                int cameras = variant == "all-off" || variant == "data-only" ? 0 : 4;
                var config = Parse("--station-benchmark-data-writer", mode, "--station-benchmark-variant", variant, "--station-benchmark-cameras", cameras.ToString());
                Check(config.dataWriter == mode && config.renderProfile == "default" && config.readbackSlots == 0 && config.admission == "default", "Writer ablation altered rendering or admission defaults.");
                var writerOptions = new JsonSerializerOptions { IncludeFields = true };
                var roundTrip = JsonSerializer.Deserialize<StationDecisionBenchmarkConfiguration>(JsonSerializer.Serialize(config, writerOptions), writerOptions);
                Check(roundTrip.dataWriter == mode && roundTrip.variant == variant, "Report serialization lost data writer ablation settings.");
            }
        Reject("Writer control was accepted without a capture session.", "--station-benchmark-variant", "observation", "--station-benchmark-cameras", "0", "--station-benchmark-data-writer", "background");
        Reject("Unknown writer mode was accepted.", "--station-benchmark-data-writer", "unbounded");
        Reject("Duplicate writer modes hid conflicting settings.", "--station-benchmark-data-writer", "main", "--station-benchmark-data-writer", "background");
        foreach (string variant in new[] { "observation", "all-off", "data-only", "all-data-video", "video-only" })
        {
            string count = variant == "all-data-video" || variant == "video-only" ? "4" : "0";
            var def = Parse("--station-benchmark-variant", variant, "--station-benchmark-cameras", count);
            Check(def.ResolveSkeletons() == (variant == "all-data-video" || variant == "data-only"), "Default skeleton override changed variant semantics.");
            var off = Parse("--station-benchmark-variant", variant, "--station-benchmark-cameras", count, "--station-benchmark-skeletons", "off");
            Check(!off.ResolveSkeletons() && off.skeletons == "off", "Skeleton off override was ignored.");
            if (variant == "observation" || variant == "all-off") Reject("Skeleton on altered a no-data control.", "--station-benchmark-variant", variant, "--station-benchmark-cameras", count, "--station-benchmark-skeletons", "on");
            else Check(Parse("--station-benchmark-variant", variant, "--station-benchmark-cameras", count, "--station-benchmark-skeletons", "on").ResolveSkeletons(), "Skeleton on override was ignored.");
        }
        Reject("Unknown skeleton override was accepted.", "--station-benchmark-skeletons", "all-bones");
        Reject("Duplicate skeleton overrides hid conflicting choices.", "--station-benchmark-skeletons", "on", "--station-benchmark-skeletons", "off");
        var hallIds = new[] { "HALL-01", "HALL-02", "HALL-03", "HALL-04" };
        var inventory = new[] { "PLATFORM-02", "HALL-04", "HALL-02", "STREET-01", "HALL-03", "HALL-01" };
        foreach (int count in new[] { 1, 2, 4 })
        {
            var config = Parse("--station-benchmark-cameras", count.ToString());
            Check(config.cameraSelection == "hall" && config.ResolveCameraIds(inventory, hallIds).SequenceEqual(hallIds.Take(count)), "Numeric camera controls no longer select the authored Hall subset.");
        }
        foreach (string variant in new[] { "all-data-video", "video-only" })
        {
            var config = Parse("--station-benchmark-variant", variant, "--station-benchmark-cameras", "all");
            var selected = config.ResolveCameraIds(inventory, hallIds);
            Check(config.cameraSelection == "all" && config.cameras == 0 && selected.SequenceEqual(inventory.OrderBy(id => id, StringComparer.Ordinal)), "Explicit all omitted or duplicated an installed camera.");
            Check(selected.Length == inventory.Length && config.readbackSlots == 0 && config.renderProfile == "default" && config.dataWriter == "default", "All camera selection changed production pipeline controls.");
            var allOptions = new JsonSerializerOptions { IncludeFields = true };
            var allRestored = JsonSerializer.Deserialize<StationDecisionBenchmarkConfiguration>(JsonSerializer.Serialize(config, allOptions), allOptions);
            Check(allRestored.cameraSelection == "all" && allRestored.ResolveCameraIds(inventory, hallIds).SequenceEqual(selected), "Report roundtrip lost explicit all selection semantics.");
        }
        var allConfig = Parse("--station-benchmark-cameras", "all");
        void RejectSelection(Action action, string message)
        { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, message); }
        RejectSelection(() => allConfig.ResolveCameraIds(Array.Empty<string>(), hallIds), "All selection accepted an empty inventory.");
        RejectSelection(() => allConfig.ResolveCameraIds(null, hallIds), "All selection accepted an unavailable inventory.");
        RejectSelection(() => allConfig.ResolveCameraIds(new[] { "HALL-01", "HALL-01" }, hallIds), "All selection accepted duplicate installed IDs.");
        RejectSelection(() => allConfig.ResolveCameraIds(new[] { "HALL-01", " " }, hallIds), "All selection accepted blank installed IDs.");
        RejectSelection(() => Parse("--station-benchmark-cameras", "4").ResolveCameraIds(new[] { "HALL-01", "HALL-02" }, hallIds), "Hall selection silently skipped missing cameras.");
        Check(allConfig.ResolveCameraIds(new[] { "PLATFORM-01", "STREET-01" }, Array.Empty<string>()).Length == 2, "All selection incorrectly depends on Hall cameras existing.");
        StationDecisionBenchmarkConfiguration.RequireExactCameraSet(inventory, inventory.Reverse(), "test finalized order");
        Check(true, "Equivalent finalized camera sets with different order were rejected.");
        RejectSelection(() => StationDecisionBenchmarkConfiguration.RequireExactCameraSet(inventory, inventory.Take(5), "test missing"), "Finalized set silently lost a selected camera.");
        RejectSelection(() => StationDecisionBenchmarkConfiguration.RequireExactCameraSet(new[] { "HALL-01", "HALL-02" }, new[] { "HALL-01", "HALL-01" }, "test duplicate"), "Duplicate finalized IDs hid a missing camera at the same count.");
        RejectSelection(() => StationDecisionBenchmarkConfiguration.RequireExactCameraSet(new[] { "HALL-01" }, new[] { "PLATFORM-01" }, "test replacement"), "Wrong finalized camera passed a count-only check.");
        RejectSelection(() => StationDecisionBenchmarkConfiguration.RequireExactCameraSet(inventory, null, "test absent"), "Missing finalized camera list was accepted.");
        StationDecisionBenchmarkConfiguration.RequireExactCameraSet(Array.Empty<string>(), Array.Empty<string>(), "nonvideo");
        Check(true, "No-video capture could not finalize an empty selection.");
        foreach (string variant in new[] { "observation", "all-off", "data-only" })
            Reject("All camera selection was accepted without a video stream.", "--station-benchmark-variant", variant, "--station-benchmark-cameras", "all");
        Reject("Unsupported numeric camera count was accepted.", "--station-benchmark-cameras", "16");
        Reject("Duplicate all/numeric camera selectors hid conflicting settings.", "--station-benchmark-cameras", "all", "--station-benchmark-cameras", "4");
        var legacy = Parse("--station-benchmark-fps", "17");
        var explicitPlayback = Parse("--station-benchmark-playback-fps", "17");
        Check(legacy.fps == 17 && explicitPlayback.fps == 17 && legacy.seconds == explicitPlayback.seconds, "Nominal playback alias broke compatibility.");
        var fractional = Parse("--station-benchmark-warmup", "20.25", "--station-benchmark-seconds", "20.5");
        Check(fractional.warmup == 20.25 && fractional.seconds == 20.5, "Host locale changed CLI real-time windows.");
        foreach (string variant in new[] { "observation", "all-off", "data-only" })
        {
            var config = Parse("--station-benchmark-variant", variant, "--station-benchmark-cameras", "0");
            Check(config.variant == variant && config.admission == "default", "Nonvideo variant did not remain isolated.");
            Reject("Pipeline controls were silently ignored on a nonvideo case.", "--station-benchmark-variant", variant, "--station-benchmark-cameras", "0", "--station-benchmark-readback-slots", "2");
        }
        Reject("Slot count beyond the bounded ring was accepted.", "--station-benchmark-readback-slots", "5");
        Reject("Negative slot count was accepted.", "--station-benchmark-readback-slots", "-1");
        Reject("Invalid admission policy was accepted.", "--station-benchmark-admission", "adaptive");
        Reject("Unsupported rendering policy was accepted.", "--station-benchmark-render-profile", "no-shadows");
        Reject("Accelerated simulation invalidated the A/B workload.", "--station-benchmark-sim-speed", "4");
        Reject("Nonfinite warmup was accepted.", "--station-benchmark-warmup", "NaN");
        Reject("Nonfinite measurement was accepted.", "--station-benchmark-seconds", "Infinity");
        Reject("Measurement shorter than 20 real seconds was accepted.", "--station-benchmark-seconds", "19.9");
        Reject("Path traversal in the label was accepted.", "--station-benchmark-label", "../escape");
        Reject("Conflicting duplicate CLI values were accepted.", "--station-benchmark-readback-slots", "2", "--station-benchmark-readback-slots", "4");
        Reject("Duplicate playback aliases hid conflicting settings.", "--station-benchmark-fps", "30", "--station-benchmark-playback-fps", "20");
        Reject("Unknown benchmark control was accepted.", "--station-benchmark-unknown", "value");
        Reject("Missing control value was accepted.", "--station-benchmark-readback-slots");
        Reject("Invalid date was accepted.", "--station-benchmark-date", "2026-02-30");
        var options = new JsonSerializerOptions { IncludeFields = true };
        var restored = JsonSerializer.Deserialize<StationDecisionBenchmarkConfiguration>(JsonSerializer.Serialize(defaults, options), options);
        Check(restored.admission == "default" && restored.renderProfile == "default" && restored.readbackSlots == 0 && restored.warmup == 20 && restored.dataWriter == "default", "Report serialization lost requested A/B settings.");
        Console.WriteLine($"PASS: {checks} benchmark configuration checks.");
    }
}
