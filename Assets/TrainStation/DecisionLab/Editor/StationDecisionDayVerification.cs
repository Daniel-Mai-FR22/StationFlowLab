#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using StationFlow.TrainStation.RailOperations;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Full dated-plan checks plus a bounded actual-motion first-wave check; never skips the passenger clock.</summary>
    [InitializeOnLoad]
    public static class StationDecisionDayVerification
    {
        public const string ScenePath = "Assets/TrainStation/Scenes/TrainStationDaily.unity";
        const string Output = "Reports/TrainStationDaily";
        const string Key = "StationFlow.TrainStation.DailyVerification";
        static Run run;
        static StationDecisionWorld world;
        static StationDecisionArrivalController arrivals;
        static StationDecisionDashboard dashboard;
        static StationDecisionFollowCamera follow;
        static double nextPoll, nextSave, pausedAt;
        static float pauseClock;
        static int pauseEntered, pauseCompleted;
        static readonly Dictionary<string, Watch> watches = new Dictionary<string, Watch>();
        static readonly Dictionary<int, string> slotHistory = new Dictionary<int, string>();
        static readonly Dictionary<Transform, Vector3> pausedTransforms = new Dictionary<Transform, Vector3>();
        static readonly List<GroupWatch> groupWatches = new List<GroupWatch>();

        [Serializable] public sealed class FileEvidence { public string path, before, after; }
        [Serializable] public sealed class Hour { public int hour, departures, boardingDemand, alightingDemand, visibleBoarders, visibleAlighters; }
        [Serializable] public sealed class Saved
        {
            public bool success, filesPreserved, deterministicPlan, finiteBoundarySweep;
            public string checkedUtc, scene;
            public int services, actorSlots, journeys, representedBoarding, representedAlighting, initialClipped, routeChecks, timingChecks, seedSweeps;
            public int groups, soloActors, earlyActors, ordinaryActors, lastMinuteActors;
            public float visualActorRatio;
            public int configuredActiveLimit;
            public List<Hour> hours = new List<Hour>();
            public List<FileEvidence> files = new List<FileEvidence>();
            public List<string> errors = new List<string>();
            public string scope = "All dated services and generator demand/identity/capacity/ranges/groups, deterministic replay, eight seeds, complete saved portal-to-POI paths and finite timing boundaries. No full-day physical walking is claimed.";
        }
        [Serializable] public sealed class Person
        {
            public string id, service, group, purpose, finalStatus, lastExecution, issue, terminalReason;
            public int slot, weight, samples, decisions;
            public float entered, finished = -1f, distanceTravelled, maximumStationary, entryDeferral;
        }
        [Serializable] public sealed class GroupAdmission
        {
            public string id, service, purpose, exception;
            public int plannedMembers, enteredMembers;
            public float firstEntry, lastEntry, spread;
            public bool complete, windowChecked, boundApplies;
        }
        [Serializable] public sealed class Run
        {
            public string startedUtc, completedUtc, phase, archivePath, outputDirectory, oldPlayScene;
            public long deadlineTicks;
            public bool success, completionReached, filesPreserved, pauseChecked, oldOptionsEnabled, oldPaused, oldBackground;
            public bool selectedJourneyReusedSafely;
            public string selectedJourney;
            public int selectedSlot = -1;
            public int selectedReuseObservedFrame = -1;
            public int oldOptions, oldFps, oldVsync, initializeFrame, observations, entered, finished, boarded, exited, unserved;
            public int activePeak, representedActivePeak, poolReuses, liveIdentityChecks, capacityChecks, entranceChecks, groupWaitSamples, boardingInvariantChecks;
            public int queueEstimateChecks;
            public float oldScale, elapsed, pauseSeconds, maximumPausedDrift;
            public int planned, representedBoarded, representedAlighted, decisions, initialClipped;
            public float visualActorRatio;
            public int actorSlots, configuredActiveLimit;
            public int completeArrivalGroups, groupAdmissionChecks, groupAdmissionExceptions;
            public float maximumGroupEntrySpread;
            public List<Person> people = new List<Person>();
            public List<GroupAdmission> groupAdmissions = new List<GroupAdmission>();
            public List<FileEvidence> files = new List<FileEvidence>();
            public List<string> errors = new List<string>(), warnings = new List<string>();
            public string fixture = "Unmodified saved daily scenario, native locomotion and complete shared clock at configured4x, first1200sim. One >=1s real-time shared-clock pause. No force-selected activities, teleport, demand edit, or clock skipping.";
            public string scope = "Bounded first-wave integration, not all18 operating hours or full-density crowd validation. Weighted visible actors conserve passenger demand; their physical bodies do not reproduce the full represented crowd.";
        }
        sealed class Watch { public StationDecisionPassenger actor; public Person report; public Vector3 position, movementAnchor; public float time, lastMovement; }
        sealed class GroupWatch { public DecisionJourneyRecord[] members; public GroupAdmission report; }

        static StationDecisionDayVerification()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            Application.logMessageReceived += OnLog;
        }

        [MenuItem("Efrei Research Lab/Train Station/Daily/Validate saved day and entrances")]
        public static void CheckSavedScene()
        {
            RequireEditClean();
            if (SceneManager.GetActiveScene().path != ScenePath) EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var report = new Saved { checkedUtc = DateTime.UtcNow.ToString("O"), scene = ScenePath, files = SnapshotFiles() };
            try
            {
                var scene = SceneManager.GetActiveScene();
                var controller = Components<StationDecisionArrivalController>(scene).Single();
                var w = Components<StationDecisionWorld>(scene).Single();
                Require(controller.world == w && controller.scenario && w.rail && w.rail.schedule, "Daily binding missing.");
                var schedule = w.rail.schedule;
                Require(schedule.finiteOperatingDay, "An authoritative finite schedule date is required.");
                Require(schedule.Validate().Count == 0, string.Join("; ", schedule.Validate()));
                Require(w.rail.ValidateConfiguration().Count == 0, string.Join("; ", w.rail.ValidateConfiguration()));
                Require(schedule.services.Length == 96 && w.rail.trains.Length == 4, "Expected96 dated departures and four pooled trainsets.");
                int expectedSlots = controller.scenario.activeActorLimit;
                Require(controller.actorPool.Length == expectedSlots && controller.actorPool.Distinct().Count() == expectedSlots,
                    "Expected one unique saved actor slot per configured active slot.");
                Require(controller.actorPool.All(p => p && !p.gameObject.activeInHierarchy && p.motion && p.passenger && p.world == w &&
                    p.passenger.externalActivityControl && !p.passenger.automaticRequests), "Pool slots must be inactive and explicitly controlled.");
                Require(controller.scenario.activeActorLimit <= controller.actorPool.Length, "Configured active limit exceeds pool.");
                Require(w.participants.SequenceEqual(controller.actorPool), "World and arrival controller do not share the same actor pool.");
                Require(EditorBuildSettings.scenes.Any(s => s.enabled && s.path == ScenePath), "Daily scene must support its restart action.");
                var entrances = controller.portals.Where(p => p.trackNumber == 0).Select(p => p.entranceId).ToArray();
                Require(entrances.Length >= 1 && controller.portals.Select(p => p.entranceId).Distinct().Count() == controller.portals.Length, "Invalid portal identifiers.");
                var serviceDtos = ServiceInputs(schedule);
                var settings = controller.scenario.PlannerSettings(); settings.scenarioDate = schedule.operatingDateIso;
                settings.entranceWeights = controller.entranceWeights;
                var plan = DecisionArrivalPlanner.Generate(serviceDtos, entrances, settings);
                var replay = DecisionArrivalPlanner.Generate(serviceDtos, entrances, settings);
                report.deterministicPlan = plan.Count == replay.Count && plan.Select(p => JsonUtility.ToJson(p)).SequenceEqual(replay.Select(p => JsonUtility.ToJson(p)));
                Require(report.deterministicPlan, "Same seed changed the immutable plan.");
                VerifyPlan(plan, schedule, settings);
                for (int seed = 0; seed < 8; ++seed)
                {
                    settings.seed = controller.scenario.seed + seed;
                    VerifyPlan(DecisionArrivalPlanner.Generate(serviceDtos, entrances, settings), schedule, settings);
                    ++report.seedSweeps;
                }
                foreach (var service in schedule.services)
                {
                    int index = Array.IndexOf(schedule.services, service);
                    Require(service.seatedCapacity == 220 && service.throughPassengers == 0, "Terminus baseline requires220seats and zero through load.");
                    var times = new[] { service.arrivalOffsetSeconds - service.approachDurationSeconds - 1d,
                        service.arrivalOffsetSeconds + service.doorDurationSeconds + .5d,
                        service.departureOffsetSeconds + service.departureDurationSeconds + 1d, schedule.OperatingDayEndElapsedSeconds + 86400d };
                    var states = new[] { StationTrainState.Offstage, StationTrainState.Boarding, StationTrainState.Offstage, StationTrainState.Offstage };
                    for (int i = 0; i < times.Length; ++i)
                    {
                        var snapshot = schedule.GetSnapshot(index, times[i]); ++report.timingChecks;
                        Require(snapshot.service == service && snapshot.state == states[i], "Finite service boundary or next-day repetition failed: " + service.serviceId);
                    }
                }
                // Independent interval sampling supplements the schedule's pairwise validation.
                for (double clock = 0; clock <= 86400; clock += 30)
                {
                    var occupied = new HashSet<int>();
                    for (int i = 0; i < schedule.services.Length; ++i)
                    {
                        var state = schedule.GetSnapshot(i, clock - schedule.StartClockSeconds); ++report.timingChecks;
                        if (state.state != StationTrainState.Offstage) Require(occupied.Add(state.service.trackNumber), "Two services occupy one track in daily sweep.");
                    }
                }
                report.finiteBoundarySweep = true;
                foreach (var portal in controller.portals)
                {
                    Require(portal.spawnPoints.Length > 0 && portal.spawnPoints.All(t => t), "Portal lacks saved spawn points.");
                    foreach (var point in portal.spawnPoints)
                    {
                        Require(Snap(point.position, out var start), "Portal is not on the exact saved NavMesh: " + portal.entranceId);
                        var destinations = portal.trackNumber == 0 ? w.pois : w.pois.Where(p => p.activity == DecisionActivity.Exit).ToArray();
                        foreach (var poi in destinations)
                        {
                            Require(Route(start, poi.Position), "Incomplete portal route: " + portal.entranceId + " → " + poi.poiId); ++report.routeChecks;
                        }
                    }
                }
                report.services = schedule.services.Length; report.actorSlots = controller.actorPool.Length; report.journeys = plan.Count;
                report.visualActorRatio = controller.scenario.visualActorRatio; report.configuredActiveLimit = controller.scenario.activeActorLimit;
                report.representedBoarding = plan.Where(p => p.purpose == DecisionJourneyPurpose.Boarding).Sum(p => p.representedPassengers);
                report.representedAlighting = plan.Where(p => p.purpose == DecisionJourneyPurpose.Alighting).Sum(p => p.representedPassengers);
                report.initialClipped = plan.Count(p => p.clippedAtSimulationStart);
                report.groups = plan.Where(p => !string.IsNullOrEmpty(p.groupId)).Select(p => p.groupId).Distinct().Count();
                report.soloActors = plan.Count(p => string.IsNullOrEmpty(p.groupId));
                report.earlyActors = plan.Count(p => p.arrivalProfile == DecisionArrivalProfile.EarlyService);
                report.ordinaryActors = plan.Count(p => p.arrivalProfile == DecisionArrivalProfile.Ordinary);
                report.lastMinuteActors = plan.Count(p => p.arrivalProfile == DecisionArrivalProfile.LastMinute);
                for (int h = 0; h < 24; ++h)
                {
                    var services = schedule.services.Where(s => (int)((s.departureOffsetSeconds + schedule.StartClockSeconds) / 3600) == h).ToArray();
                    report.hours.Add(new Hour { hour = h, departures = services.Length, boardingDemand = services.Sum(s => s.expectedBoarding),
                        alightingDemand = services.Sum(s => s.expectedAlighting), visibleBoarders = plan.Count(p => p.purpose == DecisionJourneyPurpose.Boarding &&
                            (int)((p.plannedEntrySeconds + schedule.StartClockSeconds) / 3600) == h), visibleAlighters = plan.Count(p => p.purpose == DecisionJourneyPurpose.Alighting &&
                            (int)((p.plannedEntrySeconds + schedule.StartClockSeconds) / 3600) == h) });
                }
            }
            catch (Exception e) { report.errors.Add(e.ToString()); }
            report.filesPreserved = CheckFiles(report.files);
            if (!report.filesPreserved) report.errors.Add("Read-only day checks changed saved assets.");
            report.success = report.errors.Count == 0; Write(Output + "/saved-day-check.json", report);
            Require(report.success, string.Join("\n", report.errors));
        }

        static void VerifyPlan(List<DecisionArrivalPlan> plan, StationRailSchedule schedule, DecisionArrivalSettings settings)
        {
            Require(plan.Count > 0 && plan.Select(p => p.journeyId).Distinct().Count() == plan.Count, "Planned identities are empty or duplicated.");
            Require(plan.Zip(plan.Skip(1), (a, b) => a.plannedEntrySeconds <= b.plannedEntrySeconds).All(x => x), "Plan is not ordered by entry time.");
            foreach (var service in schedule.services)
            {
                var cohort = plan.Where(p => p.serviceId == service.serviceId).ToArray();
                Require(cohort.Where(p => p.purpose == DecisionJourneyPurpose.Boarding).Sum(p => p.representedPassengers) ==
                    (service.departureOffsetSeconds > 0 ? service.expectedBoarding : 0), "Boarding demand not conserved: " + service.serviceId);
                Require(cohort.Where(p => p.purpose == DecisionJourneyPurpose.Alighting).Sum(p => p.representedPassengers) ==
                    (service.departureOffsetSeconds > 0 ? service.expectedAlighting : 0), "Alighting demand not conserved: " + service.serviceId);
            }
            foreach (var p in plan)
            {
                var service = schedule.services.First(s => s.serviceId == p.serviceId);
                Require(!string.IsNullOrEmpty(p.ticketId) && p.coachNumber >= 1 && p.coachNumber <= service.CoachCount &&
                    p.seatNumbers != null && p.seatNumbers.Length == p.representedPassengers &&
                    p.seatNumbers.Distinct().Count() == p.representedPassengers &&
                    p.seatNumbers.All(n => n >= 1 && n <= service.coachSeatCapacities[p.coachNumber - 1]), "Invalid coach ticket: " + p.journeyId);
                Require(p.journeyId.StartsWith(settings.scenarioDate + "/", StringComparison.Ordinal) && p.representedPassengers > 0 &&
                    p.plannedEntrySeconds >= 0 && !float.IsNaN(p.plannedEntrySeconds), "Invalid dated identity/weight/time.");
                if (p.purpose == DecisionJourneyPurpose.Boarding)
                {
                    double minimum = p.arrivalProfile == DecisionArrivalProfile.EarlyService ? settings.earlyMinSeconds : p.arrivalProfile == DecisionArrivalProfile.Ordinary ? settings.ordinaryMinSeconds : settings.lateMinSeconds;
                    double maximum = p.arrivalProfile == DecisionArrivalProfile.EarlyService ? settings.earlyMaxSeconds : p.arrivalProfile == DecisionArrivalProfile.Ordinary ? settings.ordinaryMaxSeconds : settings.lateMaxSeconds;
                    Require(p.leadSeconds >= minimum-.001 && p.leadSeconds <= maximum+.001, "Arrival lead outside configured range.");
                    float expected = Mathf.Max(0, p.publishedDepartureSeconds - p.leadSeconds) + p.groupMemberIndex * 1.1f;
                    Require(Mathf.Abs(expected-p.plannedEntrySeconds) < .01f && p.clippedAtSimulationStart == (p.publishedDepartureSeconds-p.leadSeconds < 0), "Entry/clipped cohort does not match declared lead.");
                }
            }
            foreach (var cohort in plan.GroupBy(p => p.serviceId + "/" + p.purpose))
                Require(cohort.SelectMany(p => p.seatNumbers.Select(n => p.coachNumber + "/" + n)).Distinct().Count() ==
                    cohort.Sum(p => p.representedPassengers), "Duplicate allocated seat in service/direction: " + cohort.Key);
            foreach (var group in plan.Where(p => !string.IsNullOrEmpty(p.groupId)).GroupBy(p => p.groupId))
            {
                var members = group.ToArray(); int count = members.Length;
                Require(count >= 2 && count <= 4 && members.All(p => p.groupSize == count), "Incomplete party membership.");
                Require(members.Select(p => p.groupMemberIndex).OrderBy(i => i).SequenceEqual(Enumerable.Range(0, count)), "Duplicated party member index.");
                Require(members.Select(p => p.serviceId).Distinct().Count() == 1 && members.Select(p => p.purpose).Distinct().Count() == 1 &&
                    members.Select(p => p.entranceId).Distinct().Count() == 1 && members.Select(p => p.coachNumber).Distinct().Count() == 1, "Party trip/entrance/coach differs.");
            }
        }

        [MenuItem("Efrei Research Lab/Train Station/Daily/Check actual first20minutes")]
        public static void BeginPlayCheck()
        {
            Require(string.IsNullOrEmpty(SessionState.GetString(Key, "")), "A daily verification is already pending.");
            CheckSavedScene();
            run = new Run { startedUtc = DateTime.UtcNow.ToString("O"), phase = "entering", deadlineTicks = DateTime.UtcNow.AddMinutes(10).Ticks,
                archivePath = Output + "/day-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json", files = SnapshotFiles(),
                oldPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene), oldOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled,
                oldOptions = (int)EditorSettings.enterPlayModeOptions, oldPaused = EditorApplication.isPaused, oldBackground = Application.runInBackground,
                oldFps = Application.targetFrameRate, oldVsync = QualitySettings.vSyncCount, oldScale = Time.timeScale };
            Save(); EditorSettings.enterPlayModeOptionsEnabled = false;
            EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            EditorApplication.isPaused = false; EditorApplication.isPlaying = true;
        }
        static void Initialize()
        {
            Require(SceneManager.GetActiveScene().path == ScenePath, "Play loaded the wrong scene.");
            world = Components<StationDecisionWorld>(SceneManager.GetActiveScene()).Single();
            arrivals = Components<StationDecisionArrivalController>(SceneManager.GetActiveScene()).Single();
            dashboard = Components<StationDecisionDashboard>(SceneManager.GetActiveScene()).Single();
            follow = Components<StationDecisionFollowCamera>(SceneManager.GetActiveScene()).Single();
            Require(arrivals.IsReady && world.rail.IsReady, "Daily initialization failed: " + arrivals.ConfigurationIssue);
            run.outputDirectory = world.OutputDirectory; run.planned = arrivals.GeneratedPlan.Count;
            run.visualActorRatio = arrivals.scenario.visualActorRatio; run.actorSlots = arrivals.actorPool.Length;
            run.configuredActiveLimit = arrivals.scenario.activeActorLimit;
            run.initialClipped = arrivals.GeneratedPlan.Count(p => p.clippedAtSimulationStart);
            watches.Clear(); slotHistory.Clear(); pausedTransforms.Clear();
            groupWatches.Clear();
            foreach (var group in arrivals.Journeys.Where(r => !string.IsNullOrEmpty(r.plan.groupId)).GroupBy(r => r.plan.groupId))
                groupWatches.Add(new GroupWatch { members = group.ToArray() });
            arrivals.JourneyEntered += Entered; arrivals.JourneyCompleted += Completed;
            foreach (var record in arrivals.ActiveJourneys) AddEntered(record, false);
            Application.runInBackground = true; Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0;
            Time.timeScale = Mathf.Clamp(arrivals.scenario.defaultTimeScale, 1, 8); run.phase = "running";
        }
        static void Entered(DecisionJourneyRecord record) => AddEntered(record, true);
        static void AddEntered(DecisionJourneyRecord record, bool exactEntry)
        {
            var p = record.livePassenger; if (!p) { Check(false, "Entered journey has no actor."); return; }
            Check(!watches.ContainsKey(record.plan.journeyId), "Journey entered more than once: " + record.plan.journeyId);
            int slot = Array.IndexOf(arrivals.actorPool, p);
            if (slotHistory.TryGetValue(slot, out var previous) && previous != record.plan.journeyId)
            {
                Check(watches.TryGetValue(previous, out var prior) && prior.report.finished >= 0, "Pool reused an unfinished journey."); ++run.poolReuses;
            }
            slotHistory[slot] = record.plan.journeyId;
            Check(record.enteredSeconds >= record.plan.plannedEntrySeconds-.001f, "Passenger entered early.");
            var portal = arrivals.portals.First(x => record.plan.purpose == DecisionJourneyPurpose.Alighting ? x.trackNumber == record.plan.trackNumber : x.entranceId == record.plan.entranceId);
            Check(portal.spawnPoints.Where((t, i) => record.plan.purpose != DecisionJourneyPurpose.Alighting ||
                i / portal.spawnPointsPerCoach + 1 == record.plan.coachNumber).Any(t => Vector3.Distance(t.position, record.lastPosition) < .3f),
                "Journey did not enter at its declared entrance/coach portal."); ++run.entranceChecks;
            if (exactEntry) Check(p.DecisionCount == 0 && !p.Finished && !p.Boarded && !p.ReservedSeat && p.ActivityHistory.Count == 0,
                "Reused actor retained decisions, terminal state, seat, or history.");
            var result = new Person { id = record.plan.journeyId, service = record.plan.serviceId, group = record.plan.groupId,
                purpose = record.plan.purpose.ToString(), slot = slot, weight = record.plan.representedPassengers,
                entered = record.enteredSeconds, entryDeferral = record.enteredSeconds-record.plan.plannedEntrySeconds };
            run.people.Add(result);
            watches[record.plan.journeyId] = new Watch { actor = p, report = result, position = p.transform.position,
                movementAnchor = p.transform.position, time = world.ElapsedSeconds, lastMovement = world.ElapsedSeconds };
        }
        static void Completed(DecisionJourneyRecord record)
        {
            if (watches.TryGetValue(record.plan.journeyId, out var w))
            {
                w.report.finished = record.finishedSeconds; w.report.finalStatus = record.status.ToString();
                w.report.decisions = record.decisions; w.report.terminalReason = record.terminalReason;
                Check(!w.actor.ReservedSeat, "Terminal journey retains a seat.");
            }
            // Rejected boarding may terminate as Failed, so checking only Boarded
            // outcomes would silently miss a recoverable arrival/boarding handoff error.
            if (record.livePassenger && record.livePassenger.HasEntered)
            {
                ++run.boardingInvariantChecks;
                Check(!record.livePassenger.BoardingViolation,
                    "Entered journey triggered a boarding invariant: " + record.plan.journeyId + "; " + record.terminalReason);
            }
            if (record.status == DecisionJourneyStatus.Failed)
                run.warnings.Add(record.plan.journeyId + ": Failed terminal; " + record.terminalReason);
            if (record.status == DecisionJourneyStatus.Boarded)
            {
                var actual = world.ObserveTrip(record.plan.serviceId);
                Check(actual.status == DecisionTrainStatus.Boarding && world.ElapsedSeconds < actual.cutoffSeconds,
                    "Journey boarded outside actual gate.");
                Check(!record.livePassenger || !record.livePassenger.BoardingViolation, "Boarding invariant failed.");
                Check(record.livePassenger && record.livePassenger.HasPlatformAccess &&
                    Vector3.Distance(record.lastPosition, record.livePassenger.AssignedBoardingPosition) <= 1.5f,
                    "Boarding lacks scanned ticket or assigned coach proximity.");
                Check(record.livePassenger && record.livePassenger.BoardingQueueValidated, "Boarding bypassed its coach door queue.");
            }
        }
        static void Observe()
        {
            ++run.observations; run.elapsed = world.ElapsedSeconds;
            run.entered = arrivals.EnteredCount; run.finished = arrivals.CompletedCount; run.boarded = arrivals.BoardingCount;
            run.exited = arrivals.ExitedCount; run.unserved = arrivals.UnservedCount;
            run.representedBoarded = arrivals.RepresentedBoardedCount;
            run.representedAlighted = arrivals.Journeys.Where(r => r.plan.purpose == DecisionJourneyPurpose.Alighting && r.enteredSeconds >= 0).Sum(r => r.plan.representedPassengers);
            run.activePeak = Math.Max(run.activePeak, arrivals.ActiveCount); run.representedActivePeak = Math.Max(run.representedActivePeak, arrivals.RepresentedActiveCount);
            run.decisions = world.DecisionCount;
            ObserveGroupAdmissions();
            Check(arrivals.ActiveCount <= arrivals.scenario.activeActorLimit, "Active actor limit exceeded.");
            Check(arrivals.Journeys.Count == arrivals.GeneratedPlan.Count && arrivals.ActiveCount + arrivals.ScheduledCount + arrivals.CompletedCount == arrivals.Journeys.Count,
                "Journey ledger conservation failed.");
            var active = arrivals.ActiveJourneys.ToArray(); var seats = new HashSet<string>(); var actors = new HashSet<int>();
            if (string.IsNullOrEmpty(run.selectedJourney))
            {
                var selected = active.FirstOrDefault(r => r.plan.purpose == DecisionJourneyPurpose.Alighting);
                if (selected != null && selected.livePassenger)
                {
                    run.selectedJourney = selected.plan.journeyId; run.selectedSlot = Array.IndexOf(arrivals.actorPool, selected.livePassenger);
                    dashboard.SelectParticipant(run.selectedSlot);
                    Check(dashboard.SelectedPassenger == selected.livePassenger && follow.FollowedPassenger == selected.livePassenger && follow.IsFollowing,
                        "Explicit initial selection did not follow the chosen journey.");
                }
            }
            else if (!run.selectedJourneyReusedSafely)
            {
                var selected = arrivals.FindJourney(run.selectedJourney);
                var slotActor = arrivals.actorPool[run.selectedSlot];
                if (selected != null && selected.finishedSeconds >= 0 && world.ElapsedSeconds >= selected.finishedSeconds+.5f &&
                    slotActor.HasEntered && slotActor.gameObject.activeInHierarchy && slotActor.JourneyId != run.selectedJourney)
                {
                    // Editor update is not a camera LateUpdate boundary. The pool can
                    // expose a rebound component before RefreshFocus clears it this frame.
                    // Observe an actual subsequent camera focus update before asserting
                    // identity; Editor update can precede LateUpdate in either frame.
                    if (run.selectedReuseObservedFrame < 0) run.selectedReuseObservedFrame = Time.frameCount;
                    else if (follow.LastFocusUpdateFrame > run.selectedReuseObservedFrame)
                    {
                        Check(follow.TargetFrozen && (!follow.FollowedPassenger || follow.FollowedPassenger.JourneyId == run.selectedJourney),
                            "Camera silently followed a recycled actor's new journey.");
                        Check(!dashboard.SelectedPassenger || dashboard.SelectedPassenger.JourneyId == run.selectedJourney,
                            "Dashboard silently selected a recycled actor's new journey.");
                        run.selectedJourneyReusedSafely = true;
                    }
                }
            }
            foreach (var record in active)
            {
                var p = record.livePassenger; ++run.liveIdentityChecks;
                Check(p && p.gameObject.activeInHierarchy && p.HasEntered && !p.Finished && p.JourneyId == record.plan.journeyId &&
                    p.serviceId == record.plan.serviceId && p.GroupId == record.plan.groupId && p.RepresentedPassengers == record.plan.representedPassengers,
                    "Active journey identity/state changed.");
                if (!p) continue;
                ++run.boardingInvariantChecks;
                Check(!p.BoardingViolation, "Active journey triggered a boarding invariant: " + record.plan.journeyId);
                if (p.CoachQueue)
                {
                    ++run.queueEstimateChecks;
                    Check(p.CoachQueue.EstimatedWaitSeconds(p) <=
                        (p.CoachQueue.RepresentedWaiting + p.RepresentedPassengers) * p.CoachQueue.boardingSecondsPerPassenger + .01f,
                        "Coach service estimate counted the future train wait again.");
                }
                if (!p.IsAlighter && p.PlatformGate && p.transform.position.z > p.PlatformGate.platformCrossingPoint.position.z + .1f &&
                    Mathf.Abs(p.transform.position.x - p.PlatformGate.transform.position.x) < 3.95f)
                    Check(p.HasPlatformAccess || p.PlatformGate.CanProceed(p), "Passenger entered platform without gate token: " + p.JourneyId);
                Check(actors.Add(Array.IndexOf(arrivals.actorPool, p)), "Two active journeys share one pool actor.");
                if (!string.IsNullOrEmpty(p.CurrentSeatId)) Check(seats.Add(p.CurrentSeatId), "Two journeys share one seat.");
                if (!watches.TryGetValue(record.plan.journeyId, out var watch)) { AddEntered(record, false); watch = watches[record.plan.journeyId]; }
                var position = p.transform.position; float now = world.ElapsedSeconds;
                watch.report.distanceTravelled += Vector3.Distance(position, watch.position); watch.position = position;
                watch.report.lastExecution = p.ExecutionState; watch.report.issue = p.NavigationIssue; ++watch.report.samples;
                if (p.ExecutionState == "WaitingForGroup" || p.ExecutionState == "Regrouping") ++run.groupWaitSamples;
                bool walking = p.ExecutionState == "Walking" || p.ExecutionState == "WalkingToSeat" || p.ExecutionState == "ClearingCounter";
                if (!walking || Vector3.Distance(position, watch.movementAnchor) >= .15f)
                { watch.lastMovement = now; watch.movementAnchor = position; }
                else watch.report.maximumStationary = Mathf.Max(watch.report.maximumStationary, now-watch.lastMovement);
                watch.time = now;
            }
            var boardedByService = arrivals.Journeys.Where(r => r.status == DecisionJourneyStatus.Boarded)
                .GroupBy(r => r.plan.serviceId).ToDictionary(g => g.Key, g => g.Sum(r => r.plan.representedPassengers));
            foreach (var service in world.rail.schedule.services)
            {
                ++run.capacityChecks; int admitted = world.AdmittedPassengerCount(service.serviceId);
                Check(admitted >= 0 && admitted + service.throughPassengers <= service.seatedCapacity, "Weighted train capacity exceeded.");
                boardedByService.TryGetValue(service.serviceId, out int completedWeight);
                Check(admitted == completedWeight,
                    "Capacity ledger differs from completed weighted journeys.");
            }
            if (!run.pauseChecked && run.elapsed >= 30 && active.Length > 0)
            {
                pausedTransforms.Clear();
                foreach (var record in active)
                {
                    var p = record.livePassenger; pausedTransforms[p.transform] = p.transform.position;
                    var animator = p.motion.animationPlayer.TargetAnimator;
                    if (animator && animator.isHuman)
                        foreach (var bone in new[] { HumanBodyBones.Hips, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg })
                        { var t = animator.GetBoneTransform(bone); if (t) pausedTransforms[t] = t.position; }
                }
                pauseClock = world.ElapsedSeconds; pauseEntered = arrivals.EnteredCount; pauseCompleted = arrivals.CompletedCount;
                world.TogglePause(); pausedAt = EditorApplication.timeSinceStartup; run.phase = "paused";
            }
            if (run.elapsed >= 1200) Complete();
        }
        static void ObservePause()
        {
            Check(Math.Abs(world.ElapsedSeconds-pauseClock) < .0001 && arrivals.EnteredCount == pauseEntered && arrivals.CompletedCount == pauseCompleted,
                "Shared pause advanced rail clock or lifecycle.");
            foreach (var pair in pausedTransforms) if (pair.Key) run.maximumPausedDrift = Mathf.Max(run.maximumPausedDrift, Vector3.Distance(pair.Key.position, pair.Value));
            if (EditorApplication.timeSinceStartup-pausedAt < 1) return;
            run.pauseSeconds = (float)(EditorApplication.timeSinceStartup-pausedAt);
            Check(run.maximumPausedDrift < .002f, "Paused humanoid/root pose moved.");
            world.TogglePause(); run.pauseChecked = true; run.phase = "running";
        }
        static void ObserveGroupAdmissions()
        {
            foreach (var group in groupWatches)
            {
                if (group.report != null && group.report.complete && group.report.windowChecked) continue;
                var entered = group.members.Where(r => r.enteredSeconds >= 0).ToArray();
                if (entered.Length == 0) continue;
                if (group.report == null)
                {
                    var first = group.members[0].plan;
                    group.report = new GroupAdmission { id = first.groupId, service = first.serviceId, purpose = first.purpose.ToString(),
                        plannedMembers = group.members.Length, firstEntry = entered.Min(r => r.enteredSeconds) };
                    run.groupAdmissions.Add(group.report);
                }
                var result = group.report;
                result.enteredMembers = entered.Length; result.lastEntry = entered.Max(r => r.enteredSeconds);
                result.spread = result.lastEntry-result.firstEntry;
                run.maximumGroupEntrySpread = Mathf.Max(run.maximumGroupEntrySpread, result.spread);
                if (!result.complete && entered.Length == group.members.Length) { result.complete = true; ++run.completeArrivalGroups; }
                float windowEnd = result.firstEntry+arrivals.scenario.maximumRegroupSeconds;
                if (result.windowChecked || world.ElapsedSeconds < windowEnd) continue;
                result.windowChecked = true;
                var actual = world.ObserveTrip(result.service);
                var service = world.rail.schedule.services.First(s => s.serviceId == result.service);
                bool due = group.members.All(r => r.plan.plannedEntrySeconds <= windowEnd+.001f);
                bool gate = actual.status != DecisionTrainStatus.Cancelled &&
                    (group.members[0].plan.purpose == DecisionJourneyPurpose.Alighting ?
                        actual.departureSeconds-service.doorDurationSeconds > windowEnd : actual.cutoffSeconds > windowEnd);
                result.boundApplies = due && gate;
                if (result.boundApplies)
                {
                    ++run.groupAdmissionChecks;
                    Check(result.complete && result.spread <= arrivals.scenario.maximumRegroupSeconds+.001f,
                        "Due eligible party was not fully admitted within the bounded regroup window: " + result.id);
                }
                else
                {
                    ++run.groupAdmissionExceptions;
                    result.exception = !due ? "Some planned entries were not due within the window." : "Actual service eligibility expired or was cancelled within the window.";
                    run.warnings.Add(result.id + ": group admission bound not applicable; " + result.exception);
                }
            }
        }
        static void Complete()
        {
            run.completionReached = true;
            Check(run.unserved == 0, "The saved first-wave fixture contains Failed or unspawned journeys; missed trains must replan to a valid exit.");
            Check(run.pauseChecked && run.entered > arrivals.actorPool.Length && run.poolReuses > 0 && run.boarded > 0 && run.representedAlighted > 0,
                "First-wave entry/reuse/boarding/alighting/pause coverage was not reached.");
            Check(run.people.Any(p => !string.IsNullOrEmpty(p.group)), "No actual grouped traveller entered.");
            Check(run.completeArrivalGroups > 0 && run.groupAdmissionChecks > 0, "No complete eligible party admission was verified.");
            Check(run.selectedJourneyReusedSafely, "Natural pool-reuse selection/follow regression was not observed.");
            for (int coach = 1; coach <= 4; ++coach)
                Check(arrivals.Journeys.Any(r => r.plan.purpose == DecisionJourneyPurpose.Boarding && r.plan.coachNumber == coach &&
                    r.status == DecisionJourneyStatus.Boarded), "No passenger physically boarded coach " + coach + " during the first-wave check.");
            foreach (var p in run.people.Where(p => p.maximumStationary > 25))
                run.warnings.Add(p.id + ": actual root remained within0.15m for " + p.maximumStationary.ToString("F2") + " walking seconds.");
            if (world.Paused) world.TogglePause(); world.FlushLogs(); run.phase = "exiting"; Save(); WriteRun(); EditorApplication.isPlaying = false;
        }
        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return; nextPoll = EditorApplication.timeSinceStartup+.05;
            Load(); if (run == null || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            try
            {
                if (run.phase == "restoring" && !EditorApplication.isPlayingOrWillChangePlaymode) { Restore(); return; }
                Require(DateTime.UtcNow.Ticks < run.deadlineTicks, "Daily first-wave check wall-clock timeout.");
                if (run.phase == "initializing" && EditorApplication.isPlaying && Time.frameCount >= run.initializeFrame) Initialize();
                else if ((run.phase == "running" || run.phase == "paused") && EditorApplication.isPlaying)
                {
                    Require(world && arrivals, "Unexpected domain reload during physical day test; partial report retained.");
                    if (run.phase == "paused") ObservePause(); else Observe();
                }
                if (EditorApplication.timeSinceStartup >= nextSave) { nextSave = EditorApplication.timeSinceStartup+1; Save(); WriteRun(); }
            }
            catch (Exception e)
            {
                Check(false, e.ToString()); run.phase = "exiting"; Save(); WriteRun();
                if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false; else Restore();
            }
        }
        static void ModeChanged(PlayModeStateChange state)
        {
            Load(); if (run == null) return;
            if (state == PlayModeStateChange.EnteredPlayMode) { run.phase = "initializing"; run.initializeFrame = Time.frameCount+15; Save(); }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                if (!run.completionReached) Check(false, "Play stopped before physical verification completed.");
                run.phase = "exiting"; Save();
            }
            else if (state == PlayModeStateChange.EnteredEditMode) { run.phase = "restoring"; Save(); }
        }
        static void Restore()
        {
            EditorSettings.enterPlayModeOptionsEnabled = run.oldOptionsEnabled; EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)run.oldOptions;
            EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(run.oldPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(run.oldPlayScene);
            EditorApplication.isPaused = run.oldPaused; Application.runInBackground = run.oldBackground; Application.targetFrameRate = run.oldFps;
            QualitySettings.vSyncCount = run.oldVsync; Time.timeScale = run.oldScale;
            run.filesPreserved = CheckFiles(run.files); Check(run.filesPreserved, "Saved assets changed during Play verification.");
            Check(run.completionReached && run.observations > 0, "No completed physical test evidence.");
            run.completedUtc = DateTime.UtcNow.ToString("O"); run.phase = "complete"; run.success = run.errors.Count == 0;
            WriteRun(); SessionState.EraseString(Key); run = null; world = null; arrivals = null;
        }
        static void Check(bool okay, string error) { if (!okay && run != null && run.errors.Count < 100 && !run.errors.Contains(error)) run.errors.Add(error); }
        static void Require(bool okay, string message) { if (!okay) throw new InvalidOperationException(message); }
        static void OnLog(string message, string stack, LogType type) { if (run != null && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)) Check(false, message); }
        static void Save() { if (run != null) SessionState.SetString(Key, JsonUtility.ToJson(run)); }
        static void Load() { if (run == null) { string text = SessionState.GetString(Key, ""); if (!string.IsNullOrEmpty(text)) run = JsonUtility.FromJson<Run>(text); } }
        static void WriteRun() { if (run != null) { Write(Output+"/day-play-check.json", run); Write(run.archivePath, run); } }
        static void Write(string path, object value) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, JsonUtility.ToJson(value, true)); }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>(true)).ToArray();
        static void RequireEditClean()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling, "Daily check requires stopped Edit mode.");
            for (int i = 0; i < SceneManager.sceneCount; ++i) Require(!SceneManager.GetSceneAt(i).isDirty, "Save or explicitly discard dirty scenes before verification.");
        }
        static DecisionArrivalService[] ServiceInputs(StationRailSchedule schedule) => schedule.services.Select(s => new DecisionArrivalService
        { serviceId = s.serviceId, trainNumber = s.trainNumber, trackNumber = s.trackNumber, arrivalSeconds = s.arrivalOffsetSeconds,
            departureSeconds = s.departureOffsetSeconds, doorSeconds = s.doorDurationSeconds, expectedBoarding = s.expectedBoarding,
            expectedAlighting = s.expectedAlighting, coachSeatCapacities = s.coachSeatCapacities, throughPassengers = s.throughPassengers }).ToArray();
        static bool Snap(Vector3 point, out Vector3 position)
        { bool good = NavMesh.SamplePosition(point, out var hit, .2f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y-point.y) <= .15f; position = good ? hit.position : point; return good; }
        static bool Route(Vector3 from, Vector3 to)
        { if (!Snap(from, out var a) || !Snap(to, out var b)) return false; var path = new NavMeshPath(); return NavMesh.CalculatePath(a, b, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete; }
        static List<FileEvidence> SnapshotFiles()
        {
            var files = new HashSet<string> { StationDecisionLabAuthoring.SourceScene, StationDecisionLabAuthoring.SourceSchedule,
                StationDecisionLabAuthoring.ScenePath, StationDecisionLabAuthoring.SchedulePath, ScenePath };
            foreach (string guid in AssetDatabase.FindAssets("t:NavMeshData", new[] { "Assets/TrainStation" })) files.Add(AssetDatabase.GUIDToAssetPath(guid));
            var scene = SceneManager.GetActiveScene();
            foreach (var w in Components<StationDecisionWorld>(scene)) if (w.rail && w.rail.schedule) files.Add(AssetDatabase.GetAssetPath(w.rail.schedule));
            foreach (var a in Components<StationDecisionArrivalController>(scene)) if (a.scenario) files.Add(AssetDatabase.GetAssetPath(a.scenario));
            return files.Where(File.Exists).OrderBy(p => p, StringComparer.Ordinal).Select(p => new FileEvidence { path = p, before = Digest(p) }).ToList();
        }
        static bool CheckFiles(IEnumerable<FileEvidence> evidence)
        { bool okay = true; foreach (var file in evidence) { file.after = File.Exists(file.path) ? Digest(file.path) : "missing"; okay &= file.after == file.before; } return okay; }
        static string Digest(string path)
        { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    }
}
#endif
