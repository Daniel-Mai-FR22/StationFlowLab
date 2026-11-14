#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    public static class StationDecisionRunInitializationVerification
    {
        public const string ReportPath = "Reports/TrainStationDaily/RuntimeInitialization/bootstrap-verification.json";
        [Serializable] sealed class Report
        {
            public bool success, templatesUnmodified, fixedSeedReplay, runtimeObjectsDetached, freshLaunchesDiffer, allSchedulesValid;
            public string checkedUtc, dateSource, generatorVersion, error;
            public int seededSchedulesChecked, minimumServices, maximumServices;
            public StationDecisionRunSession firstLaunch, secondLaunch;
            public string[] firstPlatformOrder, secondPlatformOrder;
        }

        public static void Run()
        {
            var report = new Report { checkedUtc = DateTime.UtcNow.ToString("O"), dateSource = "DateTimeOffset.Now in the running computer's local timezone",
                generatorVersion = DecisionDayTimetableGenerator.Version, minimumServices = int.MaxValue };
            var sessions = new List<StationDecisionRunSession>();
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Bootstrap verification requires a stopped editor.");
                var world = UnityEngine.Object.FindObjectsByType<StationDecisionWorld>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Single(w => w.gameObject.scene == SceneManager.GetActiveScene());
                var controller = world.arrivalController ? world.arrivalController : world.GetComponent<StationDecisionArrivalController>();
                Require(controller && controller.scenario && world.rail && world.rail.schedule, "Daily world, scenario and railway references are required.");
                string scheduleBefore = EditorJsonUtility.ToJson(world.rail.schedule), scenarioBefore = EditorJsonUtility.ToJson(controller.scenario);
                var first = StationDecisionRunBootstrap.Create(world.rail, controller.scenario); sessions.Add(first);
                var second = StationDecisionRunBootstrap.Create(world.rail, controller.scenario); sessions.Add(second);
                report.firstLaunch = first; report.secondLaunch = second;
                report.firstPlatformOrder = first.schedule.services.Select(s => s.trackNumber.ToString(CultureInfo.InvariantCulture)).ToArray();
                report.secondPlatformOrder = second.schedule.services.Select(s => s.trackNumber.ToString(CultureInfo.InvariantCulture)).ToArray();
                string today = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                Require(first.operatingDateIso == today && second.operatingDateIso == today, "Fresh launches did not use the local operating date.");
                Require(Guid.TryParse(first.runId, out _) && first.runId != second.runId, "Fresh launches must have independent UUID identities.");
                report.freshLaunchesDiffer = JsonUtility.ToJson(first.schedule) != JsonUtility.ToJson(second.schedule);
                Require(report.freshLaunchesDiffer, "Fresh launch schedules unexpectedly repeated.");
                report.runtimeObjectsDetached = first.schedule != world.rail.schedule && first.scenario != controller.scenario &&
                    !EditorUtility.IsPersistent(first.schedule) && !EditorUtility.IsPersistent(first.scenario);
                Require(report.runtimeObjectsDetached, "Bootstrap returned persistent source assets.");
                var replay = StationDecisionRunBootstrap.Create(world.rail, controller.scenario, true, first.seed, first.operatingDateIso); sessions.Add(replay);
                report.fixedSeedReplay = SameSchedule(first, replay);
                Require(report.fixedSeedReplay, "Replay seed/date changed the complete schedule.");
                for (int seed = -16; seed < 16; ++seed)
                {
                    var session = StationDecisionRunBootstrap.Create(world.rail, controller.scenario, true, seed, "2026-10-20"); sessions.Add(session);
                    var errors = session.schedule.Validate(); Require(errors.Count == 0, string.Join("; ", errors));
                    Require(session.schedule.services.All(s => world.rail.trains.Any(t => t && t.trackNumber == s.trackNumber && t.PhysicalCoachCount == s.CoachCount)), "Generated train exceeds its physical coach layout.");
                    report.minimumServices = Math.Min(report.minimumServices,session.schedule.services.Length);
                    report.maximumServices = Math.Max(report.maximumServices,session.schedule.services.Length);
                    ++report.seededSchedulesChecked;
                }
                // Mutable runtime nested policy and coach arrays must not leak into any source object.
                first.scenario.alightingStopovers.serviceVisitorFraction = .123f;
                first.schedule.services[0].coachSeatCapacities[0] = 1;
                report.templatesUnmodified = scheduleBefore == EditorJsonUtility.ToJson(world.rail.schedule) && scenarioBefore == EditorJsonUtility.ToJson(controller.scenario);
                Require(report.templatesUnmodified, "Bootstrap mutated an authored template.");
                report.allSchedulesValid = true; report.success = true;
            }
            catch (Exception e) { report.error = e.ToString(); throw; }
            finally
            {
                foreach (var session in sessions)
                { if (session.schedule) UnityEngine.Object.DestroyImmediate(session.schedule); if (session.scenario) UnityEngine.Object.DestroyImmediate(session.scenario); }
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            }
        }

        static bool SameSchedule(StationDecisionRunSession a, StationDecisionRunSession b)
        {
            string aName = a.schedule.name, bName = b.schedule.name;
            try { a.schedule.name = b.schedule.name = "Replay comparison"; return JsonUtility.ToJson(a.schedule) == JsonUtility.ToJson(b.schedule); }
            finally { a.schedule.name = aName; b.schedule.name = bName; }
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
