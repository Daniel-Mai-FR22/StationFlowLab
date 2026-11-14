#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using StationFlow.TrainStation.RailOperations;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Real saved-scene and Play-mode acceptance checks for the mixed station population.</summary>
    [InitializeOnLoad]
    public static class StationMixedCrowdVerification
    {
        const string ScenePath = "Assets/TrainStation/Scenes/TrainStation.unity";
        const string StateKey = "StationFlow.StationMixedCrowd.Verification";
        const string LogKey = StateKey + ".Errors";
        const string ReportPath = "Reports/TrainStationSocial/mixed-crowd-play-check.json";
        [Serializable] sealed class SavedScene { public string path; public bool isLoaded, isActive; }
        [Serializable] sealed class SavedReport
        {
            public string scene, unityVersion, checkedUtc;
            public bool success;
            public int passengers, seated, standing, walkingTogether, walkingAlone, shopStaff, animatedActors;
            public int conversationGroups, walkingGroups, withLuggage, withoutLuggage, completeRoutes, navMeshSurfaces;
            public int sceneObjects, savedTrainsets, timetableDisplays;
            public List<string> errors = new List<string>();
        }
        [Serializable] sealed class VisitResult
        {
            public string passenger, activity, service, kind, originalSeat, finalState;
            public bool requested, admitted, talking, staffTalking, completed, returned, resumedMovement;
            public bool hasLuggage;
            public float requestedAt, admittedAt, completedAt, returnedAt, pathLength, approachDistance;
            public float maximumLuggageDistance, maximumLuggageTravel, locomotionWorldDistance, locomotionAnimatedDistance;
            public float maximumSettledPlaybackError;
            public int motionSamples, settledMotionSamples, initialConversations;
            public List<string> states = new List<string>();
            public bool cancelledBeforeCompletion;
            public VisitSnapshot beforeCancellation, cancellationSnapshot;
            public List<VisitSnapshot> navigationSnapshots = new List<VisitSnapshot>();
        }
        [Serializable] sealed class NearbySnapshot
        {
            public string actor, state;
            public Vector3 position, velocity, destination;
            public bool stopped, hasPath, turning, avoidingQueue, avoidingStationary;
            public float requestedSpeed, actualSpeed, effectiveAgentSpeed, crowdFactor;
        }
        [Serializable] sealed class VisitSnapshot
        {
            public string reason, state, issue, navigationIssue, motion, currentService, pathStatus, stoppedSource;
            public string currentSeat, seatReservedBy, seatOccupant, walkingGroup, groupLeader, groupLeaderState;
            public float sampledAt, noProgressSeconds, yaw, remaining, requestedSpeed, actualSpeed, effectiveAgentSpeed, crowdFactor;
            public float navigationElapsed, approachElapsed, navigationTimeout, approachTimeout;
            public Vector3 position, finalTarget, agentTarget, steeringTarget, velocity, desiredVelocity;
            public Vector3 seatApproach, groupLeaderPosition;
            public Vector3[] pathCorners;
            public bool navigationEnabled, stopped, hasPath, pathPending, requestActive, avoidingQueue, avoidingStationary, turning;
            public bool queuePlannerWait, stationaryPlannerWait, queueSpacingHold, waitingOutsideQueue;
            public bool groupWaitingForCompanions, recoveringWalkingGroup, participatesInGroupCohesion;
            public int queueDetours, stationaryDetours;
            public List<NearbySnapshot> nearbyAtStall = new List<NearbySnapshot>();
        }
        [Serializable] sealed class AutomaticVisitDiagnostic
        {
            public string passenger, activity, finalState;
            public bool requested, available, cancelledBeforeCompletion;
            public int initialConversations, completedConversations;
            public float requestedAt;
            public VisitSnapshot latest, beforeCancellation, cancellationSnapshot;
            public List<VisitSnapshot> navigationSnapshots = new List<VisitSnapshot>();
        }
        sealed class VisitWatch
        {
            public float nextSnapshotAt, lastProgressAt;
            public Vector3 progressAnchor;
            public string previousState;
            public VisitSnapshot previous;
        }
        [Serializable] sealed class DeskResult
        {
            public string name;
            public int queuePeak;
            public List<string> admissions = new List<string>(), starts = new List<string>(), ends = new List<string>();
        }
        [Serializable] sealed class MovementResult
        {
            public string name, activity;
            public float distance, maximumGroupSpread;
            public int movingSamples;
            public float maximumSpreadAt;
            public string maximumSpreadContext;
        }
        [Serializable] sealed class Run
        {
            public string startedUtc, completedUtc, unityVersion, phase, originalPlayScene, farPassenger, nearPassenger;
            public bool success, abort, capturedSettings, originalOptionsEnabled, originalPaused, originalRunInBackground;
            public bool restoredSettings, restoredScenes, noReservationPassed, fifoPassed, physicalAdmissionPassed;
            public bool allCategoryVisitsPassed, automaticEventsPassed, staffUnavailablePassed, gaitPassed, railPassed;
            public bool seatConflictRequested, seatConflictOccupied, alternativeSeatPassed, atomicSeatPassed;
            public bool shopCapture, hallCapture;
            public int originalOptions, originalTargetFrameRate, originalVSyncCount, checkAfterFrame;
            public int baselineObjects, baselineCameras, baselineLights, framesObserved, displayChecks, trainArrivals, trainDepartures;
            public int regroupTargets;
            public float originalTimeScale, startedAt, phaseStartedAt, simulatedSeconds, disabledAt;
            public long deadlineTicks;
            public SavedScene[] scenes;
            public List<VisitResult> visits = new List<VisitResult>();
            public List<DeskResult> desks = new List<DeskResult>();
            public List<MovementResult> movement = new List<MovementResult>();
            public List<AutomaticVisitDiagnostic> automaticVisits = new List<AutomaticVisitDiagnostic>();
            public List<string> automaticActivities = new List<string>(), errors = new List<string>();
            public List<string> observedTrainStates = new List<string>();
        }

        static Run liveRun;
        static StationSocialWorld world;
        static StationRailOperations rail;
        static StationTimetableDisplay[] displays;
        static StationInformationDesk[] desks;
        static StationPassenger far, near, cancellationPassenger;
        static StationPassenger seatTraveller, seatBlocker;
        static StationSeat conflictSeat;
        static StationInformationDesk fifoDesk, cancellationDesk;
        static readonly Dictionary<StationPassenger, VisitResult> activeVisits = new Dictionary<StationPassenger, VisitResult>();
        static readonly Dictionary<StationPassenger, Vector3> lastPositions = new Dictionary<StationPassenger, Vector3>();
        static readonly Dictionary<StationPassenger, Vector3> visitStartPositions = new Dictionary<StationPassenger, Vector3>();
        static readonly Dictionary<StationPassenger, Vector3> returnPositions = new Dictionary<StationPassenger, Vector3>();
        static readonly Dictionary<StationPassenger, Vector3> luggageStarts = new Dictionary<StationPassenger, Vector3>();
        static readonly Dictionary<StationPassenger, float> previousSpeeds = new Dictionary<StationPassenger, float>();
        static readonly Dictionary<StationPassenger, float> steadySince = new Dictionary<StationPassenger, float>();
        static readonly Dictionary<StationPassenger, MovementResult> movement = new Dictionary<StationPassenger, MovementResult>();
        static readonly Dictionary<StationPassenger, float> originalSpeeds = new Dictionary<StationPassenger, float>();
        static readonly HashSet<StationPassenger> automaticParticipants = new HashSet<StationPassenger>();
        static readonly Dictionary<StationPassenger, VisitWatch> visitWatches = new Dictionary<StationPassenger, VisitWatch>();
        static readonly Dictionary<StationPassenger, AutomaticVisitDiagnostic> automaticDiagnostics = new Dictionary<StationPassenger, AutomaticVisitDiagnostic>();
        static readonly List<StationActorMotion> nearbyMotion = new List<StationActorMotion>();
        static readonly Dictionary<string, FieldInfo> diagnosticFields = new Dictionary<string, FieldInfo>();
        static float previousSampleAt;
        static double nextPoll;
        static double nextProgressWrite;

        static StationMixedCrowdVerification()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            Application.logMessageReceived += Log;
        }

        [MenuItem("Efrei Research Lab/Train Station Social/Validate saved mixed population")]
        public static void ValidateSavedScene()
        {
            Require(!Application.isPlaying, "Saved validation requires Edit mode.");
            var report = InspectSavedScene(SceneManager.GetActiveScene());
            Write("Reports/TrainStationSocial/mixed-crowd-saved-check.json", report);
            Require(report.success, string.Join("\n", report.errors));
        }

        static SavedReport InspectSavedScene(Scene scene)
        {
            var report = new SavedReport { scene = scene.path, unityVersion = Application.unityVersion, checkedUtc = DateTime.UtcNow.ToString("O") };
            try
            {
                Require(scene.path == ScenePath, "Open the saved TrainStation scene.");
                var social = Components<StationSocialWorld>(scene).Single();
                var people = social.passengers;
                Require(people != null && (people.Length == 60 || people.Length == 300) && people.All(p => p), "Expected the saved sixty- or three-hundred-passenger population.");
                bool densePopulation = people.Length == 300;
                Require(people.Distinct().Count() == people.Length && Components<StationPassenger>(scene).Length == people.Length, "Passenger registration is duplicated or incomplete.");
                report.passengers = people.Length;
                report.seated = people.Count(p => p.activity == StationPassengerActivity.Seated);
                report.standing = people.Count(p => p.activity == StationPassengerActivity.StandingGroup);
                report.walkingTogether = people.Count(p => p.activity == StationPassengerActivity.WalkingGroup);
                report.walkingAlone = people.Count(p => p.activity == StationPassengerActivity.SoloWalking);
                Require(report.seated == 18 && report.standing == (densePopulation ? 72 : 12) &&
                    report.walkingTogether == (densePopulation ? 90 : 18) && report.walkingAlone == (densePopulation ? 120 : 12),
                    "Passenger categories do not match the authored population.");
                Require(social.informationDesk && social.informationDesk.receptionist, "Accueil references are incomplete.");
                Require(social.shopDesks != null && social.shopDesks.Length >= 2 && social.shopDesks.All(d => d && d.receptionist), "Author at least two reusable shop desks with staff.");
                var services = new[] { social.informationDesk }.Concat(social.shopDesks).ToArray();
                Require(services.Distinct().Count() == services.Length, "Service registration contains a duplicate desk.");
                Require(services.Select(d => d.receptionist).Distinct().Count() == services.Length, "Each service needs its own staff member.");
                report.shopStaff = social.shopDesks.Length;
                report.conversationGroups = social.conversationGroups.Length;
                report.walkingGroups = social.walkingGroups.Length;
                Require(report.conversationGroups == (densePopulation ? 24 : 4) && report.walkingGroups == (densePopulation ? 30 : 6), "Saved standing/walking group counts do not match the population.");
                Require(social.walkingGroups.All(g => g && g.members != null && g.members.Length == 3 && g.members.All(p => p && p.walkingGroup == g)), "Walking group membership is not fully serialized.");
                Require(social.conversationGroups.SelectMany(g => g.members).All(m => m && people.Any(p => p.conversationMember == m)), "Standing group members lack a registered activity controller.");
                Require(social.seats.Length == 24 && social.cleaners.Length == 3, "Existing seats and cleaners were not preserved.");
                Require(people.Where(p => p.activity == StationPassengerActivity.Seated).Select(p => p.initialSeat).All(s => s) &&
                    people.Where(p => p.activity == StationPassengerActivity.Seated).Select(p => p.initialSeat).Distinct().Count() == 18, "Seated passengers need eighteen distinct initial seats.");
                foreach (var desk in services)
                {
                    Require(desk.queueSlots != null && desk.queueSlots.Length >= 3 && desk.queueSlots.All(t => t), "A service has incomplete authored queue points: " + desk.name);
                    Require(desk.IsWithinServiceArea(desk.queueSlots[0].position), "Service front is outside staff interaction range: " + desk.name);
                    Require(desk.queueJoined != null && desk.customerApproached != null && desk.conversationStarted != null && desk.conversationEnded != null, "Saved service events are missing.");
                    Require(PrefabUtility.GetCorrespondingObjectFromOriginalSource(desk.receptionist.gameObject), "Staff is not a prefab instance: " + desk.receptionist.name);
                    foreach (var slot in desk.queueSlots) { CheckRoute(slot.position, desk.queueSlots[0].position, desk.name + "/" + slot.name); report.completeRoutes++; }
                }
                foreach (var passenger in people)
                {
                    Require(passenger.world == social && passenger.informationDesk == social.informationDesk && passenger.motion && passenger.motion.agent, "Passenger references are incomplete: " + passenger.name);
                    Require(passenger.automaticRequests, "Automatic random activities are not enabled on saved passenger: " + passenger.name);
                    Require(passenger.motion.agent.obstacleAvoidanceType != ObstacleAvoidanceType.NoObstacleAvoidance, "Passenger avoidance is disabled: " + passenger.name);
                    var start = passenger.activity == StationPassengerActivity.Seated ? passenger.initialSeat.ApproachPosition : passenger.transform.position;
                    foreach (var desk in services) { CheckRoute(start, desk.queueSlots[0].position, passenger.name + " -> " + desk.name); report.completeRoutes++; }
                    if (VisibleLuggage(passenger)) report.withLuggage++; else report.withoutLuggage++;
                    if (passenger.activity == StationPassengerActivity.SoloWalking)
                    {
                        Require(passenger.wanderPoints != null && passenger.wanderPoints.Length >= 2 && passenger.wanderPoints.All(t => t), "Solo walker has no saved itinerary: " + passenger.name);
                        foreach (var point in passenger.wanderPoints) { CheckRoute(start, point.position, passenger.name + " itinerary"); report.completeRoutes++; }
                    }
                }
                Require(report.withLuggage == 0 && report.withoutLuggage == people.Length && people.All(p => !p.luggage), "All saved passengers must have no luggage objects or references.");
                foreach (var group in social.walkingGroups)
                {
                    Require(group.waypoints != null && group.waypoints.Length >= 2 && group.waypoints.All(t => t), "Walking group has no saved itinerary.");
                    for (int i = 0; i < group.waypoints.Length; i++) { CheckRoute(group.waypoints[i].position, group.waypoints[(i + 1) % group.waypoints.Length].position, group.name + " itinerary"); report.completeRoutes++; }
                }
                var actors = Components<StationActorMotion>(scene);
                report.animatedActors = actors.Length;
                Require(actors.Length == people.Length + social.cleaners.Length + services.Length, "Animated actor count does not match the registered population and staff.");
                foreach (var actor in actors)
                {
                    Require(actor.animationPlayer && actor.animationPlayer.ValidateSetup(out _), "Invalid Humanoid animation setup: " + actor.name);
                    Require(actor.animationPlayer.Library && actor.animationPlayer.Library.Validate().Count == 0, "Invalid saved motion library: " + actor.name);
                    Require(PrefabUtility.GetCorrespondingObjectFromOriginalSource(actor.gameObject), "Human has no reusable prefab source: " + actor.name);
                    Require(actor.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(s => s.sharedMesh && s.sharedMesh.vertexCount > 0), "Human has no saved skin: " + actor.name);
                }
                var surfaces = Components<NavMeshSurface>(scene);
                report.navMeshSurfaces = surfaces.Length;
                Require(surfaces.Length > 0 && surfaces.All(s => s.navMeshData && AssetDatabase.Contains(s.navMeshData)), "Navigation data must be baked into a saved asset.");
                report.sceneObjects = Components<Transform>(scene).Length;
                Require(Components<Transform>(scene).All(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject) == 0), "Scene contains a missing script.");
                report.savedTrainsets = Components<StationTrain>(scene).Length;
                report.timetableDisplays = Components<StationTimetableDisplay>(scene).Length;
                Require(report.savedTrainsets == 4 && report.timetableDisplays == 23, "Saved rail operations were not preserved.");
                Require(Components<StationRailOperations>(scene).Single().ValidateConfiguration().Count == 0, "Rail configuration is invalid.");
                report.success = true;
            }
            catch (Exception error) { report.errors.Add(error.ToString()); }
            return report;
        }

        [MenuItem("Efrei Research Lab/Train Station Social/Check actual Play mode mixed population")]
        public static void BeginPlayCheck()
        {
            Require(Load() == null, "A mixed crowd Play check is already running.");
            var run = new Run { startedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion, phase = "opening", deadlineTicks = DateTime.UtcNow.AddSeconds(300).Ticks };
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating, "Finish Play/import before checking.");
                CleanScenes(); var setup = EditorSceneManager.GetSceneManagerSetup();
                Require(setup.Length > 0 && setup.All(s => !string.IsNullOrEmpty(s.path)), "Save all open scenes before checking.");
                run.scenes = setup.Select(s => new SavedScene { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray();
                run.originalPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene);
                run.originalOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled; run.originalOptions = (int)EditorSettings.enterPlayModeOptions;
                run.originalPaused = EditorApplication.isPaused; run.originalTimeScale = Time.timeScale;
                run.originalRunInBackground = Application.runInBackground; run.originalTargetFrameRate = Application.targetFrameRate; run.originalVSyncCount = QualitySettings.vSyncCount;
                run.capturedSettings = true; SessionState.EraseString(LogKey); Save(run);
                EditorSettings.enterPlayModeOptionsEnabled = false; EditorSceneManager.playModeStartScene = null; EditorApplication.isPaused = false;
            }
            catch (Exception error) { run.errors.Add(error.ToString()); run.abort = true; Finish(run); }
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + .035;
            var run = liveRun ?? Load(); if (run == null) return;
            if (run.phase == "restoring" && !EditorApplication.isPlayingOrWillChangePlaymode) { Finish(run); return; }
            if (run.abort && run.phase == "exiting") { if (!EditorApplication.isPlayingOrWillChangePlaymode) Finish(run); return; }
            try
            {
                Require(DateTime.UtcNow.Ticks < run.deadlineTicks, "Mixed crowd check timed out in " + run.phase + ". " + DescribeActive());
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                Require(run.errors.Count == 0, string.Join("\n", run.errors));
                Require(string.IsNullOrEmpty(SessionState.GetString(LogKey, "")), "Unity logged an error during the mixed crowd check.");
                if (run.phase == "opening" && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    CleanScenes(); var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                    var saved = InspectSavedScene(scene); Write("Reports/TrainStationSocial/mixed-crowd-saved-check.json", saved);
                    Require(saved.success, string.Join("\n", saved.errors));
                    run.baselineObjects = Components<Transform>(scene).Length;
                    run.baselineCameras = Components<Camera>(scene).Count(c => c.isActiveAndEnabled);
                    run.baselineLights = Components<Light>(scene).Count(l => l.isActiveAndEnabled);
                    run.phase = "entering"; Save(run); EditorApplication.isPlaying = true;
                }
                else if (run.phase == "initializing" && EditorApplication.isPlaying && Time.frameCount >= run.checkAfterFrame)
                { liveRun = run; Initialize(run); Save(run); }
                else if (EditorApplication.isPlaying && run.phase != "entering" && run.phase != "initializing" && run.phase != "exiting")
                {
                    Observe(run); AdvanceScenario(run); Save(run);
                }
            }
            catch (Exception error)
            {
                run.errors.Add(error.ToString()); run.abort = true; run.phase = "exiting"; Save(run); Write(ReportPath, run);
                if (EditorApplication.isPlayingOrWillChangePlaymode) { Time.timeScale = run.originalTimeScale; EditorApplication.isPlaying = false; }
                else Finish(run);
            }
        }

        static void Initialize(Run run)
        {
            var scene = SceneManager.GetActiveScene();
            world = Components<StationSocialWorld>(scene).Single(); rail = Components<StationRailOperations>(scene).Single();
            displays = Components<StationTimetableDisplay>(scene); desks = new[] { world.informationDesk }.Concat(world.shopDesks).ToArray();
            activeVisits.Clear(); lastPositions.Clear(); visitStartPositions.Clear(); returnPositions.Clear(); luggageStarts.Clear(); previousSpeeds.Clear(); steadySince.Clear(); movement.Clear(); originalSpeeds.Clear(); automaticParticipants.Clear(); visitWatches.Clear(); automaticDiagnostics.Clear();
            seatTraveller = null; seatBlocker = null; conflictSeat = null;
            run.startedAt = Time.time; previousSampleAt = Time.time;
            foreach (var passenger in world.passengers)
            {
                passenger.automaticRequests = false; passenger.transitionDurationScale = .3f;
                passenger.approachTimeout = 400f; passenger.navigationTimeout = 180f; passenger.queueWaitTimeout = 500f;
                originalSpeeds[passenger] = passenger.motion.RequestedTravelSpeed;
                lastPositions[passenger] = passenger.transform.position; previousSpeeds[passenger] = 0f; steadySince[passenger] = Time.time;
                var entry = new MovementResult { name = passenger.name, activity = passenger.activity.ToString() }; movement[passenger] = entry; run.movement.Add(entry);
                var captured = passenger;
                passenger.requestStarted.AddListener(() => OnRequestedAutomatically(captured));
            }
            Require(world.passengers.Where(p => p.activity == StationPassengerActivity.Seated).All(p => p.CurrentSeat && p.CurrentSeat.Occupant == p), "Authored seated passengers did not initialize into their seats.");
            foreach (var desk in desks)
            {
                desk.conversationDuration = 4f;
                var captured = desk; run.desks.Add(new DeskResult { name = desk.name });
                desk.queueJoined.AddListener(p => OnServiceEvent(captured, p, "admit"));
                desk.conversationStarted.AddListener(p => OnServiceEvent(captured, p, "start"));
                desk.conversationEnded.AddListener(p => OnServiceEvent(captured, p, "end"));
            }
            rail.trainArrived.AddListener(_ => { if (liveRun != null) liveRun.trainArrivals++; });
            rail.trainDeparted.AddListener(_ => { if (liveRun != null) liveRun.trainDepartures++; });
            Require(rail.IsReady, "Saved train operations failed initialization: " + rail.ConfigurationIssue);
            fifoDesk = world.shopDesks[0];
            var ranked = world.passengers.Where(p => p.activity == StationPassengerActivity.StandingGroup && p.IsAvailableForActivity)
                .Select(p => new { passenger = p, length = PathLength(p.transform.position, fifoDesk.queueSlots[0].position) })
                // This scenario deliberately slows its first customer to .55 m/s. The dense
                // station also has groups 230+ m away, beyond the ordinary approach timeout
                // at that artificial test speed. Keep the FIFO pair within the hall/concourse;
                // saved-route and dense stress checks cover the distant platform passengers.
                .Where(x => x.length <= 75f).OrderBy(x => x.length).ToArray();
            Require(ranked.Length >= 2 && ranked.Last().length > ranked[0].length + 2f, "FIFO scenario needs different authored travel distances.");
            far = ranked.Last().passenger; near = ranked[0].passenger;
            far.motion.SetTravelSpeed(.55f); near.motion.SetTravelSpeed(1.8f);
            run.farPassenger = far.name; run.nearPassenger = near.name;
            Require(!fifoDesk.TryJoin(far) && fifoDesk.QueueCount == 0, "A distant non-requesting passenger can reserve a shop queue position.");
            run.phase = "arrival FIFO"; run.phaseStartedAt = Time.time;
            Request(run, far, fifoDesk);
            far.motion.SetTravelSpeed(.55f);
            Require(fifoDesk.QueueCount == 0 && !fifoDesk.Contains(far), "A distant shop request reserved a position before arrival.");
            run.noReservationPassed = true;
            Application.runInBackground = true; Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0; Time.timeScale = 4f;
        }

        static void Request(Run run, StationPassenger passenger, StationInformationDesk desk)
        {
            Require(passenger.IsAvailableForActivity, "Passenger is not ready for a new visit: " + passenger.name + " / " + passenger.CurrentState);
            Require(!activeVisits.ContainsKey(passenger), "The check issued two concurrent activities to one passenger.");
            var start = passenger.CurrentSeat ? passenger.CurrentSeat.ApproachPosition : passenger.transform.position;
            var entry = new VisitResult
            {
                passenger = passenger.name, activity = passenger.activity.ToString(), service = desk.name, kind = desk == world.informationDesk ? "Information" : "Shop",
                originalSeat = passenger.CurrentSeat ? passenger.CurrentSeat.seatId : "", hasLuggage = VisibleLuggage(passenger),
                requestedAt = Time.time - run.startedAt, initialConversations = passenger.CompletedConversations,
                pathLength = PathLength(start, desk.queueSlots[0].position)
            };
            run.visits.Add(entry); activeVisits[passenger] = entry; visitStartPositions[passenger] = passenger.transform.position;
            if (passenger.luggage) luggageStarts[passenger] = passenger.luggage.position;
            int count = desk.QueueCount;
            entry.requested = passenger.TryVisitService(desk);
            Require(entry.requested, "A valid visit could not begin: " + passenger.name + " -> " + desk.name);
            Require(desk.QueueCount == count && !desk.Contains(passenger), "Requesting a service reserved a queue slot before physical arrival.");
            visitWatches[passenger] = new VisitWatch { nextSnapshotAt = Time.time, lastProgressAt = Time.time, progressAnchor = passenger.transform.position };
        }

        static void AdvanceScenario(Run run)
        {
            float elapsed = Time.time - run.phaseStartedAt;
            if (run.phase == "arrival FIFO")
            {
                if (!run.visits.Any(v => v.passenger == near.name) && elapsed > .75f) { Request(run, near, fifoDesk); near.motion.SetTravelSpeed(1.8f); }
                var result = run.desks.Single(d => d.name == fifoDesk.name);
                if (result.admissions.Count > 0) Require(result.admissions[0] == near.name, "The distant first request overtook the later physically arriving customer.");
                if (run.visits.Count == 2 && run.visits.All(v => v.completed && v.returned))
                {
                    Require(result.admissions.SequenceEqual(result.starts) && result.starts.SequenceEqual(result.ends), "Shop service does not follow physical admission FIFO.");
                    Require(result.admissions.SequenceEqual(new[] { near.name, far.name }), "Later nearby customer did not receive service first.");
                    run.fifoPassed = true; far.motion.SetTravelSpeed(originalSpeeds[far]); near.motion.SetTravelSpeed(originalSpeeds[near]);
                    activeVisits.Clear(); BeginCategoryVisits(run);
                }
                Require(elapsed < 440f, "Physical shop FIFO scenario did not return both actors. " + DescribeActive());
            }
            else if (run.phase == "all category visits")
            {
                var categoryVisits = run.visits.Skip(2).ToArray();
                if (categoryVisits.Length == 8 && categoryVisits.All(v => v.completed && v.returned && (v.activity == "Seated" || v.activity == "StandingGroup" || v.resumedMovement)))
                {
                    Require(Enum.GetValues(typeof(StationPassengerActivity)).Cast<StationPassengerActivity>().All(a =>
                        categoryVisits.Any(v => v.activity == a.ToString() && v.kind == "Shop") && categoryVisits.Any(v => v.activity == a.ToString() && v.kind == "Information")), "A passenger category did not complete both shop and accueil visits.");
                    run.allCategoryVisitsPassed = true; activeVisits.Clear(); BeginAutomatic(run);
                }
                Require(elapsed < 420f, "A mixed activity did not complete and return. " + DescribeActive());
            }
            else if (run.phase == "automatic events")
            {
                if (run.automaticActivities.Distinct().Count() == 4 && automaticParticipants.All(p => p.IsAvailableForActivity))
                {
                    run.automaticEventsPassed = true; automaticParticipants.Clear(); BeginCancellation(run);
                }
                Require(elapsed < 300f, "Automatic random activities did not trigger and finish for all four categories. " + string.Join(", ", run.automaticActivities) +
                    ". " + string.Join("; ", automaticParticipants.Select(p => p.name + ":" + p.CurrentState + " / " + p.LastIssue)));
            }
            else if (run.phase == "staff unavailable")
            {
                if (run.disabledAt == 0f && cancellationDesk.ActiveCustomer == cancellationPassenger)
                {
                    Require(cancellationPassenger.CurrentState.ToString() == "Talking", "Cancellation scenario did not reach an actual staff conversation.");
                    cancellationDesk.receptionist.enabled = false; run.disabledAt = Time.time;
                }
                if (run.disabledAt > 0f && Time.time > run.disabledAt + .5f)
                {
                    Require(cancellationDesk.QueueCount == 0 && !cancellationDesk.ActiveCustomer, "Disabling shop staff left an active customer or queue reservation.");
                    Require(!cancellationPassenger.CurrentService, "Cancelled customer retained the unavailable service.");
                    if (cancellationPassenger.IsAvailableForActivity)
                    {
                        cancellationDesk.receptionist.enabled = true;
                        Require(!cancellationDesk.receptionist.CurrentCustomer && cancellationDesk.receptionist.motion.CurrentMotion == cancellationDesk.receptionist.motion.idleId, "Re-enabled shop staff kept talking without a customer.");
                        run.staffUnavailablePassed = true; FinalAssertions(run);
                        run.phase = "exiting"; Save(run); Time.timeScale = run.originalTimeScale; EditorApplication.isPlaying = false;
                    }
                }
                Require(elapsed < 250f, "Staff unavailability did not recover the customer activity.");
            }
        }

        static void BeginCategoryVisits(Run run)
        {
            run.phase = "all category visits"; run.phaseStartedAt = Time.time;
            int index = 0;
            foreach (StationPassengerActivity activity in Enum.GetValues(typeof(StationPassengerActivity)))
            {
                var candidates = world.passengers.Where(p => p.activity == activity && p.IsAvailableForActivity && p != near && p != far).ToArray();
                Require(candidates.Length >= 2, "Need two available passengers for each activity category: " + activity);
                var shop = world.shopDesks[index % world.shopDesks.Length];
                var shopping = candidates.OrderBy(p => PathLength(p.CurrentSeat ? p.CurrentSeat.ApproachPosition : p.transform.position, shop.queueSlots[0].position)).First();
                var information = candidates.Where(p => p != shopping).OrderBy(p => PathLength(p.CurrentSeat ? p.CurrentSeat.ApproachPosition : p.transform.position, world.informationDesk.queueSlots[0].position)).First();
                if (activity == StationPassengerActivity.Seated)
                {
                    seatTraveller = shopping; conflictSeat = shopping.CurrentSeat;
                    seatBlocker = world.passengers.Where(p => p.activity == StationPassengerActivity.Seated && p != shopping && p != information && p.IsAvailableForActivity)
                        .OrderBy(p => FlatDistance(p.transform.position, conflictSeat.ApproachPosition)).First();
                }
                Request(run, shopping, shop); Request(run, information, world.informationDesk); index++;
            }
        }

        static void BeginAutomatic(Run run)
        {
            run.phase = "automatic events"; run.phaseStartedAt = Time.time;
            foreach (StationPassengerActivity activity in Enum.GetValues(typeof(StationPassengerActivity)))
            {
                var actor = world.passengers.First(p => p.activity == activity && p.IsAvailableForActivity && p != seatBlocker && p != seatTraveller);
                var diagnostic = new AutomaticVisitDiagnostic { passenger = actor.name, activity = actor.activity.ToString(), initialConversations = actor.CompletedConversations };
                automaticDiagnostics[actor] = diagnostic; run.automaticVisits.Add(diagnostic);
                visitWatches[actor] = new VisitWatch { nextSnapshotAt = Time.time, lastProgressAt = Time.time, progressAnchor = actor.transform.position };
                automaticParticipants.Add(actor); actor.requestInterval = new Vector2(.2f, .4f); actor.ScheduleNextRequest(); actor.automaticRequests = true;
            }
        }

        static void BeginCancellation(Run run)
        {
            run.phase = "staff unavailable"; run.phaseStartedAt = Time.time;
            cancellationDesk = world.shopDesks.Last(); cancellationDesk.conversationDuration = 12f;
            cancellationPassenger = world.passengers.Where(p => p.activity == StationPassengerActivity.SoloWalking && p.IsAvailableForActivity)
                .OrderBy(p => PathLength(p.transform.position, cancellationDesk.queueSlots[0].position)).First();
            Require(cancellationPassenger.TryVisitService(cancellationDesk), "Cannot begin shop staff availability test.");
        }

        static void OnRequestedAutomatically(StationPassenger passenger)
        {
            if (liveRun == null || liveRun.phase != "automatic events" || !automaticParticipants.Contains(passenger)) return;
            if (!liveRun.automaticActivities.Contains(passenger.activity.ToString())) liveRun.automaticActivities.Add(passenger.activity.ToString());
            if (automaticDiagnostics.TryGetValue(passenger, out var diagnostic))
            { diagnostic.requested = true; diagnostic.requestedAt = Time.time - liveRun.startedAt; }
            passenger.automaticRequests = false;
        }

        static void Observe(Run run)
        {
            Require(world && rail, "The check lost its live scene references.");
            run.framesObserved++; run.simulatedSeconds = Time.time - run.startedAt;
            run.regroupTargets = world.walkingGroups.Sum(g => g.RegroupTargets);
            float delta = Mathf.Max(0f, Time.time - previousSampleAt); previousSampleAt = Time.time;
            if (conflictSeat && !run.seatConflictRequested && conflictSeat.IsAvailable)
            {
                Require(seatBlocker.TryRequestSeat(conflictSeat), "A second seated passenger could not move into the vacated seat.");
                Require(conflictSeat.ReservedBy == seatBlocker && !conflictSeat.TryReserve(seatTraveller) && !conflictSeat.TryOccupy(seatTraveller), "Two passengers could claim the same seat.");
                run.seatConflictRequested = true; run.atomicSeatPassed = true;
            }
            if (run.seatConflictRequested && conflictSeat.Occupant == seatBlocker) run.seatConflictOccupied = true;
            foreach (var passenger in world.passengers)
            {
                var motion = passenger.motion; var position = passenger.transform.position;
                Require(Finite(position) && passenger.CurrentState.ToString() != "Disabled", "Invalid live passenger: " + passenger.name);
                float travelled = FlatDistance(lastPositions[passenger], position); lastPositions[passenger] = position;
                var moving = movement[passenger]; moving.distance += travelled;
                if (motion.ActualPlanarSpeed > .15f) moving.movingSamples++;
                if (passenger.activity == StationPassengerActivity.WalkingGroup && passenger.walkingGroup && passenger.IsAvailableForActivity)
                {
                    var companions = passenger.walkingGroup.members.Where(p => p && p.IsAvailableForActivity).ToArray();
                    if (companions.Length > 1)
                    {
                        float spread = companions.Max(p => FlatDistance(position, p.transform.position));
                        if (spread > moving.maximumGroupSpread)
                        {
                            moving.maximumGroupSpread = spread;
                            moving.maximumSpreadAt = Time.time - run.startedAt;
                            var group = passenger.walkingGroup;
                            moving.maximumSpreadContext = "leader=" + (group.CurrentLeader ? group.CurrentLeader.name : "none") +
                                "; waypoint=" + group.CurrentWaypointIndex + "; atWaypoint=" + group.WaitingAtWaypoint +
                                "; cohesionWait=" + group.WaitingForCompanions + "; recoveries=" + group.RegroupRecoveryCount + "; " +
                                string.Join("; ", group.members.Where(p => p).Select(p => p.name + " " + p.CurrentState +
                                    " position=" + p.transform.position.ToString("F2") + " speed=" + p.motion.ActualPlanarSpeed.ToString("F2") +
                                    " nav=" + p.motion.NavigationIssue + " destination=" + p.motion.agent.destination.ToString("F2") +
                                    " remaining=" + p.motion.agent.remainingDistance.ToString("F2") + " stopped=" + p.motion.agent.isStopped +
                                    " velocity=" + p.motion.agent.velocity.ToString("F2") + " desired=" + p.motion.agent.desiredVelocity.ToString("F2") +
                                    " steering=" + p.motion.agent.steeringTarget.ToString("F2") + " corners=" +
                                    string.Join(",", p.motion.agent.path.corners.Select(c => c.ToString("F2")))));
                        }
                    }
                }
                if (activeVisits.TryGetValue(passenger, out var visit))
                {
                    ObserveVisitNavigation(run, passenger, visit);
                    string state = passenger.CurrentState.ToString(); visit.finalState = state;
                    if (!visit.states.Contains(state)) visit.states.Add(state);
                    if (state == "ApproachingQueue") visit.approachDistance += travelled;
                    if (motion.LocomotionActive && motion.IsWalkPose && delta > 0f && delta < .6f)
                    {
                        visit.motionSamples++; visit.locomotionWorldDistance += motion.ActualPlanarSpeed * delta;
                        visit.locomotionAnimatedDistance += motion.AppliedWalkPlaybackSpeed * motion.EffectiveWalkReferenceSpeed * delta;
                        if (Mathf.Abs(previousSpeeds[passenger] - motion.ActualPlanarSpeed) > .035f) steadySince[passenger] = Time.time;
                        if (Time.time - steadySince[passenger] > .5f && motion.ActualPlanarSpeed > .25f)
                        {
                            visit.settledMotionSamples++;
                            visit.maximumSettledPlaybackError = Mathf.Max(visit.maximumSettledPlaybackError, Mathf.Abs(motion.AppliedWalkPlaybackSpeed * motion.EffectiveWalkReferenceSpeed - motion.ActualPlanarSpeed));
                        }
                    }
                    if (visit.hasLuggage && passenger.luggage)
                    {
                        visit.maximumLuggageTravel = Mathf.Max(visit.maximumLuggageTravel, Vector3.Distance(luggageStarts[passenger], passenger.luggage.position));
                        if (motion.ActualPlanarSpeed > .1f) visit.maximumLuggageDistance = Mathf.Max(visit.maximumLuggageDistance, FlatDistance(position, passenger.luggage.position));
                    }
                    if (visit.completed && passenger.IsAvailableForActivity)
                    {
                        if (!visit.returned)
                        {
                            visit.returned = true; visit.returnedAt = Time.time - run.startedAt; returnPositions[passenger] = position;
                            if (passenger.activity == StationPassengerActivity.Seated) Require(passenger.CurrentSeat && passenger.CurrentSeat.Occupant == passenger, "Seated traveller did not occupy a valid seat after service.");
                        }
                        if (FlatDistance(returnPositions[passenger], position) > .6f && motion.ActualPlanarSpeed > .1f) visit.resumedMovement = true;
                    }
                }
                previousSpeeds[passenger] = motion.ActualPlanarSpeed;
            }
            ObserveAutomaticNavigation(run);
            foreach (var desk in desks)
            {
                var result = run.desks.Single(d => d.name == desk.name); result.queuePeak = Mathf.Max(result.queuePeak, desk.QueueCount);
                if (!desk.ActiveCustomer) continue;
                var passenger = desk.ActiveCustomer;
                Require(desk.GetQueueIndex(passenger) == 0 && desk.IsWithinServiceArea(passenger.transform.position), "Service began away from the queue head.");
                Require(desk.receptionist.CurrentCustomer == passenger, "Staff and queue disagree on their current customer.");
                Require(passenger.motion.CurrentMotion == passenger.motion.talkId && desk.receptionist.motion.CurrentMotion == desk.receptionist.motion.talkId, "Customer/staff conversation animation was not applied to both actors.");
                if (activeVisits.TryGetValue(passenger, out var visit)) { visit.talking = true; visit.staffTalking = true; }
                if (desk != world.informationDesk && !run.shopCapture && passenger.motion.animationPlayer.TargetAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime > .12f)
                { StationSocialCapture.CaptureLiveShop(desk); run.shopCapture = true; }
            }
            if (!run.hallCapture && run.simulatedSeconds > 20f) { StationSocialCapture.CaptureLiveHall(); run.hallCapture = true; }
            if (run.framesObserved % 20 == 0)
            {
                Require(Components<Transform>(SceneManager.GetActiveScene()).Length == run.baselineObjects, "Mixed social runtime created or removed scene objects.");
                foreach (var seat in world.seats) Require(!seat.Occupant || seat.ReservedBy == seat.Occupant, "Seat occupancy lost atomic ownership.");
            }
            Require(rail.IsReady && string.IsNullOrEmpty(rail.ConfigurationIssue), "Rail operations failed during the social simulation.");
            foreach (var train in rail.trains) if (!run.observedTrainStates.Contains(train.State.ToString())) run.observedTrainStates.Add(train.State.ToString());
            foreach (var display in displays)
            {
                for (int i = 0; i < display.rows.Length; i++)
                {
                    var row = display.rows[i]; if (row == null) continue; var value = display.GetRowValues(i);
                    Require(!row.time || row.time.text == (value.time ?? ""), "Timetable time differs from the running rail clock.");
                    Require(!row.status || row.status.text == (value.status ?? ""), "Timetable status differs from the actual train state.");
                }
                run.displayChecks++;
            }
        }

        static void ObserveVisitNavigation(Run run, StationPassenger passenger, VisitResult visit)
        {
            if (!visitWatches.TryGetValue(passenger, out var watch)) return;
            if (FlatDistance(passenger.transform.position, watch.progressAnchor) >= .20f)
            { watch.progressAnchor = passenger.transform.position; watch.lastProgressAt = Time.time; }
            float noProgress = Time.time - watch.lastProgressAt;
            string state = passenger.CurrentState.ToString();
            // Keep the immediately preceding observed state in memory so a failed approach
            // is diagnosed before a later return-route retry can overwrite LastIssue.
            bool cancelled = visit.requested && !visit.completed && passenger.CompletedConversations == visit.initialConversations &&
                !passenger.HasInformationRequest && (state == "ReturningToActivity" || state == "ReturningToSeat" ||
                state == "WaitingForRoute" || state == "WaitingForSeat" || passenger.IsAvailableForActivity);
            bool snapshotDue = Time.time >= watch.nextSnapshotAt || state != watch.previousState || cancelled;
            var snapshot = CaptureVisitSnapshot(run, passenger, noProgress, snapshotDue && noProgress >= 2f);
            if (snapshotDue)
            {
                snapshot.reason = cancelled ? "Request cancelled before required conversation" : state != watch.previousState ? "State changed" : "Ten-second navigation sample";
                visit.navigationSnapshots.Add(snapshot);
                watch.nextSnapshotAt = Time.time + 10f;
            }
            if (cancelled)
            {
                visit.cancelledBeforeCompletion = true;
                visit.beforeCancellation = watch.previous;
                if (visit.beforeCancellation != null) visit.beforeCancellation.reason = "Previous observation before cancellation";
                visit.cancellationSnapshot = snapshot;
                visit.finalState = state;
                if (!visit.states.Contains(state)) visit.states.Add(state);
                // These are only the ten required visits. The separate deliberate staff
                // cancellation scenario is not in activeVisits and is unaffected.
                Require(false, "Required visit cancelled before service: " + passenger.name + " / " + passenger.LastIssue +
                    ". See cancellationSnapshot and beforeCancellation; later retries cannot satisfy this original visit.");
            }
            watch.previous = snapshot; watch.previousState = state;
        }

        static void ObserveAutomaticNavigation(Run run)
        {
            foreach (var passenger in automaticParticipants)
            {
                if (!automaticDiagnostics.TryGetValue(passenger, out var diagnostic) || !visitWatches.TryGetValue(passenger, out var watch)) continue;
                if (FlatDistance(passenger.transform.position, watch.progressAnchor) >= .20f)
                { watch.progressAnchor = passenger.transform.position; watch.lastProgressAt = Time.time; }
                float noProgress = Time.time - watch.lastProgressAt;
                string state = passenger.CurrentState.ToString();
                diagnostic.finalState = state; diagnostic.available = passenger.IsAvailableForActivity;
                diagnostic.completedConversations = passenger.CompletedConversations - diagnostic.initialConversations;
                bool cancelled = diagnostic.requested && diagnostic.completedConversations == 0 && !passenger.HasInformationRequest &&
                    (state == "ReturningToActivity" || state == "ReturningToSeat" || state == "WaitingForRoute" || state == "WaitingForSeat" || passenger.IsAvailableForActivity);
                bool due = Time.time >= watch.nextSnapshotAt || state != watch.previousState || (cancelled && !diagnostic.cancelledBeforeCompletion);
                var snapshot = CaptureVisitSnapshot(run, passenger, noProgress, due && noProgress >= 2f);
                diagnostic.latest = snapshot;
                if (due)
                {
                    snapshot.reason = cancelled ? "Automatic request cancelled before conversation" : state != watch.previousState ? "Automatic participant state changed" : "Ten-second automatic participant sample";
                    diagnostic.navigationSnapshots.Add(snapshot); watch.nextSnapshotAt = Time.time + 10f;
                }
                if (cancelled && !diagnostic.cancelledBeforeCompletion)
                {
                    diagnostic.cancelledBeforeCompletion = true; diagnostic.beforeCancellation = watch.previous; diagnostic.cancellationSnapshot = snapshot;
                    if (diagnostic.beforeCancellation != null) diagnostic.beforeCancellation.reason = "Previous automatic observation before cancellation";
                }
                watch.previous = snapshot; watch.previousState = state;
            }
        }

        static VisitSnapshot CaptureVisitSnapshot(Run run, StationPassenger passenger, float noProgress, bool includeNearby)
        {
            var motion = passenger.motion; var agent = motion.agent;
            bool attached = agent && agent.enabled && agent.isOnNavMesh;
            bool queueWait = PrivateValue(motion, "yieldingToQueue", false);
            bool standingWait = PrivateValue(motion, "yieldingToStandingCrowd", false);
            bool spacingHold = PrivateValue(passenger, "queueSpacingHold", false);
            var seat = passenger.CurrentSeat;
            var group = passenger.walkingGroup;
            var leader = group ? group.CurrentLeader : null;
            var result = new VisitSnapshot
            {
                sampledAt = Time.time - run.startedAt, state = passenger.CurrentState.ToString(), issue = passenger.LastIssue,
                navigationIssue = motion.NavigationIssue, motion = motion.CurrentMotion, currentService = passenger.CurrentService ? passenger.CurrentService.name : "none",
                position = passenger.transform.position, yaw = passenger.transform.eulerAngles.y,
                finalTarget = PrivateValue(motion, "lastDestination", Vector3.zero),
                agentTarget = attached ? agent.destination : Vector3.zero, steeringTarget = attached ? agent.steeringTarget : Vector3.zero,
                velocity = attached ? agent.velocity : Vector3.zero, desiredVelocity = attached ? agent.desiredVelocity : Vector3.zero,
                remaining = attached && !float.IsInfinity(agent.remainingDistance) && !float.IsNaN(agent.remainingDistance) ? agent.remainingDistance : -1f,
                pathCorners = attached ? agent.path.corners : Array.Empty<Vector3>(), pathStatus = attached ? agent.pathStatus.ToString() : "detached",
                navigationEnabled = attached, stopped = attached && agent.isStopped, hasPath = attached && agent.hasPath, pathPending = attached && agent.pathPending,
                requestedSpeed = motion.RequestedTravelSpeed, actualSpeed = motion.ActualPlanarSpeed, effectiveAgentSpeed = agent ? agent.speed : 0f,
                crowdFactor = motion.CurrentCrowdSpeedFactor, avoidingQueue = motion.IsAvoidingQueue, avoidingStationary = motion.IsAvoidingStationaryCrowd,
                queueDetours = motion.QueueDetourCount, stationaryDetours = motion.StationaryDetourCount, turning = motion.IsTurningInPlace,
                queuePlannerWait = queueWait, stationaryPlannerWait = standingWait, queueSpacingHold = spacingHold,
                requestActive = passenger.HasInformationRequest, waitingOutsideQueue = PrivateValue(passenger, "waitingOutsideQueue", false),
                noProgressSeconds = noProgress, navigationElapsed = Time.time - PrivateValue(passenger, "navigationStartedAt", Time.time),
                approachElapsed = Time.time - PrivateValue(passenger, "approachStartedAt", Time.time),
                navigationTimeout = passenger.navigationTimeout, approachTimeout = passenger.approachTimeout,
                stoppedSource = !attached ? "detached" : !agent.isStopped ? "not stopped (speed cap or turn braking may still apply)" :
                    standingWait ? "stationary planner wait" : queueWait ? "queue planner wait" : spacingHold ? "queue spacing hold" : "scripted stop; no planner wait flag",
                currentSeat = seat ? seat.seatId : "none", seatApproach = seat ? seat.ApproachPosition : Vector3.zero,
                seatReservedBy = seat && seat.ReservedBy ? seat.ReservedBy.name : "none", seatOccupant = seat && seat.Occupant ? seat.Occupant.name : "none",
                walkingGroup = group ? group.name : "none", groupLeader = leader ? leader.name : "none", groupLeaderState = leader ? leader.CurrentState.ToString() : "none",
                groupLeaderPosition = leader ? leader.transform.position : Vector3.zero, groupWaitingForCompanions = group && group.WaitingForCompanions,
                recoveringWalkingGroup = passenger.IsRecoveringWalkingGroup, participatesInGroupCohesion = passenger.ParticipatesInWalkingCohesion
            };
            if (includeNearby)
            {
                StationCrowdSpace.GetNeighbours(passenger.transform.position, 3f, nearbyMotion);
                foreach (var other in nearbyMotion)
                {
                    if (!other || other == motion) continue;
                    var nav = other.agent; bool onNav = nav && nav.enabled && nav.isOnNavMesh;
                    var person = other.GetComponent<StationPassenger>();
                    result.nearbyAtStall.Add(new NearbySnapshot
                    {
                        actor = other.name, state = person ? person.CurrentState.ToString() : "staff/cleaner", position = other.transform.position,
                        velocity = onNav ? nav.velocity : Vector3.zero, destination = onNav ? nav.destination : Vector3.zero,
                        stopped = onNav && nav.isStopped, hasPath = onNav && nav.hasPath,
                        requestedSpeed = other.RequestedTravelSpeed, actualSpeed = other.ActualPlanarSpeed, effectiveAgentSpeed = nav ? nav.speed : 0f,
                        crowdFactor = other.CurrentCrowdSpeedFactor, turning = other.IsTurningInPlace,
                        avoidingQueue = other.IsAvoidingQueue, avoidingStationary = other.IsAvoidingStationaryCrowd
                    });
                }
            }
            return result;
        }

        static T PrivateValue<T>(object instance, string field, T fallback)
        {
            string key = instance.GetType().FullName + "." + field;
            if (!diagnosticFields.TryGetValue(key, out var info))
            {
                info = instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
                diagnosticFields[key] = info;
            }
            return info != null && info.GetValue(instance) is T value ? value : fallback;
        }

        static void OnServiceEvent(StationInformationDesk desk, StationPassenger passenger, string kind)
        {
            var run = liveRun; if (run == null) return;
            var service = run.desks.Single(d => d.name == desk.name);
            activeVisits.TryGetValue(passenger, out var visit);
            if (kind == "admit")
            {
                service.admissions.Add(passenger.name);
                if (!desk.TryGetQueueSlot(passenger, out var slot) || FlatDistance(passenger.transform.position, slot.position) > desk.arrivalTolerance + .005f)
                    run.errors.Add("A passenger joined a service queue before physically reaching its tail: " + passenger.name);
                else run.physicalAdmissionPassed = true;
                if (visit != null) { visit.admitted = true; visit.admittedAt = Time.time - run.startedAt; }
            }
            else if (kind == "start")
            {
                service.starts.Add(passenger.name);
                if (service.starts.Count > service.admissions.Count || service.starts[service.starts.Count - 1] != service.admissions[service.starts.Count - 1]) run.errors.Add("Service order differs from physical arrival FIFO: " + desk.name);
            }
            else
            {
                service.ends.Add(passenger.name);
                if (visit != null) { visit.completed = true; visit.completedAt = Time.time - run.startedAt; }
            }
        }

        static void FinalAssertions(Run run)
        {
            Require(run.fifoPassed && run.noReservationPassed && run.physicalAdmissionPassed && run.allCategoryVisitsPassed && run.automaticEventsPassed && run.staffUnavailablePassed, "One of the mixed activity scenarios did not complete.");
            Require(run.visits.Count == 10 && run.visits.All(v => v.requested && v.admitted && v.talking && v.staffTalking && v.completed && v.returned), "Every deterministic visit must include real travel, both talking animations, completion and return.");
            Require(run.visits.All(v => v.admittedAt > v.requestedAt && v.approachDistance > .5f && v.motionSamples > 0), "A visit did not demonstrate physical navigation before service.");
            foreach (var visit in run.visits.Where(v => v.locomotionWorldDistance > 1f))
            {
                float ratio = Mathf.Abs(visit.locomotionAnimatedDistance - visit.locomotionWorldDistance) / visit.locomotionWorldDistance;
                Require(ratio < .22f, "Walk playback no longer follows measured travel speed: " + visit.passenger + " error=" + ratio.ToString("P1"));
                Require(visit.maximumSettledPlaybackError < .22f, "Settled walking speed and animation disagree: " + visit.passenger);
            }
            Require(run.visits.Sum(v => v.settledMotionSamples) > 10, "No settled gait synchronization samples were collected."); run.gaitPassed = true;
            Require(run.visits.All(v => !v.hasLuggage) && world.passengers.All(p => !p.luggage), "A removed suitcase or luggage reference returned during a service visit.");
            Require(run.seatConflictRequested && run.seatConflictOccupied && run.atomicSeatPassed && seatTraveller.CurrentSeat && seatTraveller.CurrentSeat != conflictSeat && conflictSeat.Occupant == seatBlocker, "Returning traveller failed to choose another seat when the original seat was taken.");
            run.alternativeSeatPassed = true;
            Require(run.movement.Where(m => m.activity == "SoloWalking").All(m => m.distance > 2f && m.movingSamples > 5), "A solo walker never moved along its authored route.");
            Require(run.movement.Where(m => m.activity == "WalkingGroup").All(m => m.distance > 2f && m.movingSamples > 5), "A walking group member never moved with the group.");
            Require(world.walkingGroups.All(g => g.members.All(p => movement[p].maximumGroupSpread < 18f)), "Walking companions separated excessively while following the same group route.");
            Require(run.trainArrivals > 0 && run.trainDepartures > 0 && run.observedTrainStates.Contains("Arriving") && run.observedTrainStates.Contains("Departing") && run.displayChecks > 23, "Train arrivals/departures and synchronized displays were not exercised."); run.railPassed = true;
            var scene = SceneManager.GetActiveScene();
            Require(Components<Transform>(scene).Length == run.baselineObjects, "Social activity changed the number of saved scene objects.");
            Require(Components<Camera>(scene).Count(c => c.isActiveAndEnabled) == run.baselineCameras && Components<Light>(scene).Count(c => c.isActiveAndEnabled) == run.baselineLights, "Social activity changed cameras or lighting.");
            Require(!Components<MonoBehaviour>(scene).Any(m => m && m.GetType().FullName == "StationFlow.SyntheticTraffic.DemoController"), "Junction Lab was injected into TrainStation.");
        }

        static void ModeChanged(PlayModeStateChange state)
        {
            var run = liveRun ?? Load(); if (run == null) return;
            if (state == PlayModeStateChange.EnteredPlayMode) { run.phase = "initializing"; run.checkAfterFrame = Time.frameCount + 10; }
            else if (state == PlayModeStateChange.ExitingPlayMode && run.phase != "exiting") { run.abort = true; run.errors.Add("Play was stopped before mixed population verification completed."); run.phase = "exiting"; }
            else if (state == PlayModeStateChange.EnteredEditMode) run.phase = "restoring";
            Save(run);
        }
        static void Finish(Run run)
        {
            if (run.capturedSettings)
            {
                try
                {
                    Time.timeScale = run.originalTimeScale; Application.runInBackground = run.originalRunInBackground; Application.targetFrameRate = run.originalTargetFrameRate; QualitySettings.vSyncCount = run.originalVSyncCount;
                    EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(run.originalPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(run.originalPlayScene);
                    EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)run.originalOptions; EditorSettings.enterPlayModeOptionsEnabled = run.originalOptionsEnabled; EditorApplication.isPaused = run.originalPaused;
                    run.restoredSettings = true; CleanScenes(); EditorSceneManager.RestoreSceneManagerSetup(run.scenes.Select(s => new SceneSetup { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray()); run.restoredScenes = true;
                }
                catch (Exception error) { run.errors.Add("Restoration failed: " + error); }
            }
            var log = SessionState.GetString(LogKey, ""); if (!string.IsNullOrEmpty(log)) run.errors.Add("Unity errors:\n" + log);
            run.success = !run.abort && run.errors.Count == 0 && run.restoredSettings && run.restoredScenes && run.allCategoryVisitsPassed && run.automaticEventsPassed && run.fifoPassed && run.staffUnavailablePassed && run.gaitPassed && run.railPassed && run.alternativeSeatPassed && run.atomicSeatPassed;
            run.phase = "complete"; run.completedUtc = DateTime.UtcNow.ToString("O"); SessionState.EraseString(StateKey); SessionState.EraseString(LogKey);
            Write(ReportPath, run); liveRun = null; world = null; rail = null; activeVisits.Clear(); automaticParticipants.Clear(); visitWatches.Clear(); automaticDiagnostics.Clear();
            Debug.Log("Station mixed population Play verification " + (run.success ? "PASSED" : "FAILED") + ". See " + ReportPath);
        }
        static void Log(string message, string trace, LogType type)
        {
            if (Load() == null || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            var previous = SessionState.GetString(LogKey, ""); if (previous.Length < 16000) SessionState.SetString(LogKey, previous + message + "\n" + trace + "\n");
        }
        static Run Load() { var json = SessionState.GetString(StateKey, ""); return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Run>(json); }
        static void Save(Run run)
        {
            SessionState.SetString(StateKey, JsonUtility.ToJson(run));
            if (EditorApplication.timeSinceStartup < nextProgressWrite) return;
            nextProgressWrite = EditorApplication.timeSinceStartup + 2;
            Write(ReportPath, run);
        }
        static bool VisibleLuggage(StationPassenger p) => p.luggage && p.luggage.gameObject.activeInHierarchy && p.luggage.GetComponentsInChildren<Renderer>(true).Any(r => r.enabled && r.gameObject.activeInHierarchy);
        static void CleanScenes() { for (int i = 0; i < SceneManager.sceneCount; i++) Require(!SceneManager.GetSceneAt(i).isDirty, "Save or revert unsaved scenes before checking. The check never discards user edits."); }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        static void CheckRoute(Vector3 from, Vector3 to, string label) { try { PathLength(from, to); } catch (Exception error) { throw new InvalidOperationException(label + ": " + error.Message); } }
        static float PathLength(Vector3 from, Vector3 to)
        {
            Require(NavMesh.SamplePosition(from, out var start, .7f, NavMesh.AllAreas), "No baked walking surface at route start " + from);
            Require(NavMesh.SamplePosition(to, out var end, .7f, NavMesh.AllAreas), "No baked walking surface at route destination " + to);
            var path = new NavMeshPath(); Require(NavMesh.CalculatePath(start.position, end.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete, "No complete pedestrian route from " + from + " to " + to);
            float result = 0f; for (int i = 1; i < path.corners.Length; i++) result += Vector3.Distance(path.corners[i - 1], path.corners[i]); return result;
        }
        static string DescribeActive() => string.Join("; ", activeVisits.Select(p => p.Key.name + ":" + p.Key.CurrentState + " / " + p.Key.LastIssue));
        static float FlatDistance(Vector3 a, Vector3 b) { a.y = b.y = 0f; return Vector3.Distance(a, b); }
        static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Write(string path, object value) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, JsonUtility.ToJson(value, true)); }
    }
}
#endif
