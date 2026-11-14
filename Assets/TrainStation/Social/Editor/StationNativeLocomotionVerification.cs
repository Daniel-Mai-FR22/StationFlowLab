#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Short native-speed regression check; never changes the saved movement policy or cruise speeds.</summary>
    [InitializeOnLoad]
    public static class StationNativeLocomotionVerification
    {
        const string ScenePath = "Assets/TrainStation/Scenes/TrainStation.unity";
        const string StateKey = "StationFlow.StationNativeLocomotion.Verification";
        const string LogKey = StateKey + ".Errors";
        const string ReportPath = "Reports/TrainStationSocial/NativeLocomotion/play-check.json";
        [Serializable] sealed class SavedScene { public string path; public bool isLoaded, isActive; }
        [Serializable] sealed class SpeedSummary
        {
            public int samples, movingSamples, samplesBelowHalfIntent, externallyLimitedSamples, walkPoseSamples;
            public int headingOwnershipViolations, customRoutingSamples;
            public float meanRequestedSpeed, meanAgentSpeed, meanActualSpeed, meanActualToRequested, medianActualToRequested, p10ActualToRequested;
            public float minimumCrowdFactor = 1f, meanPlaybackMultiplier, meanScalarAnimationError;
        }
        [Serializable] sealed class Snapshot
        {
            public float simulatedSeconds, wallSeconds, timeScale;
            public int intentActors, movingActors, externallyLimitedActors, completedConversations;
            public float meanActualSpeed, meanRequestedSpeed, meanPlaybackMultiplier;
        }
        [Serializable] sealed class ActivitySummary
        {
            public string activity;
            public int intentSamples;
            public float meanRequestedSpeed, meanActualSpeed;
        }
        [Serializable] sealed class FreeWalk
        {
            public string actor, motion;
            public bool launched, arrived, passed, directionalLocomotion;
            public Vector3 start, goal;
            public float startedAt, elapsed, arrivalLimit, originalRequestedSpeed, agentAcceleration, agentAngularSpeed;
            public float originalAgentMaximumSpeed, maximumAgentSpeedDeviation;
            public float travelled, closestGoal = 1000f, clearSampleSeconds, clearMeanActualSpeed, clearMeanSpeedRatio;
            public float minimumCrowdFactor = 1f, clearMeanPlaybackMultiplier, clearMeanScalarAnimationError;
            public int clearSamples, queueDetours, stationaryDetours;
        }
        [Serializable] sealed class Run
        {
            public string startedUtc, completedUtc, phase, unityVersion, originalPlayScene;
            public string nativePolicy = "NavMeshAgent owns pathfinding, local avoidance, velocity and moving heading. Sampled movement must retain updateRotation, factor 1 and zero custom detour/turn state. Free-walk maximum speed stays unchanged. Zero saved coordinator components is valid. A focused comment-stripped source scan rejects retired Queue/Stationary planner calls, explicit .velocity assignments and .Move calls in the social runtime; this textual check is not a complete semantic audit.";
            public string method = "60 simulated seconds at timeScale 1 with all 300 saved passengers and their automatic events active. At 10s, borrow one available solo passenger, disable only its Passenger FSM, and place it once on a currently body-clear straight 8m route. Preserve its existing requested speed and native agent settings; do not set travel speed, avoidance limits, acceleration, angular speed or animation rate. Re-enable its FSM after arrival. Ordinary navigation-intent samples are separate from the isolated free-walk samples. No movement policy is changed by this check.";
            public string caveat = "Ordinary speed ratios include real congestion, queues, acceleration and turns, so are descriptive rather than throughput or realism gates. Free-walk speed is checked only after acceleration, before braking, with a currently clear local corridor. Scalar animation error compares playback times calibrated reference speed with actual speed; it does not measure foot contact or sliding. Simulated/wall ratio and Editor observation intervals include Editor scheduling, rendering and verification work; they are not GPU, isolated navigation cost or shipping FPS measurements. This is a finite regression run, not a matched-seed A/B benchmark.";
            public bool success, abort, capturedSettings, restoredSettings, restoredScenes, originalOptionsEnabled, originalPaused, originalRunInBackground;
            public bool predictionDisabled, contactSeparationDisabled;
            public bool nativeSourceAuditPassed;
            public int originalOptions, originalTargetFrameRate, originalVSyncCount, readyFrame, baselineObjects, finalObjects, passengers, actors, coordinators, automaticActors, initialConversations, finalConversations;
            public float originalTimeScale, startedAt, simulatedSeconds, wallSeconds, simulatedToWallRatio, minimumTimeScale = 1f, maximumTimeScale = 1f;
            public float observationMedianMs, observationP95Ms;
            public float walkingGroupObservedSeconds, walkingGroupHeldForCompanionsSeconds, walkingGroupAtWaypointSeconds;
            public double wallStartedAt;
            public long deadlineTicks;
            public SavedScene[] scenes;
            public SpeedSummary ordinary = new SpeedSummary();
            public FreeWalk freeWalk = new FreeWalk();
            public List<Snapshot> samples = new List<Snapshot>();
            public List<ActivitySummary> activities = new List<ActivitySummary>();
            public List<string> errors = new List<string>();
            public List<string> sourceAuditFindings = new List<string>();
        }
        static Run live;
        static StationSocialWorld world;
        static StationActorMotion[] actors;
        static StationPassenger fixture;
        static readonly List<float> ratios = new List<float>(), intervals = new List<float>();
        static double requestedSum, agentSum, actualSum, ratioSum, playbackSum, animationErrorSum;
        static double clearSpeedSum, clearRatioSum, clearPlaybackSum, clearErrorSum;
        static float previousTime, previousUnscaled, nextOrdinary, nextSnapshot, nextWrite;
        static Vector3 fixturePrevious;
        static int previousFrame = -1, initialQueueDetours, initialStationaryDetours;

        static StationNativeLocomotionVerification()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            Application.logMessageReceived += Log;
        }

        [MenuItem("Efrei Research Lab/Train Station Social/Verify native walking speed at 1x in Play")]
        public static void BeginPlayCheck()
        {
            Require(Load() == null, "Native locomotion verification is already active.");
            var run = new Run { startedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                phase = "opening", deadlineTicks = DateTime.UtcNow.AddMinutes(4).Ticks };
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating, "Finish Play/import before verification.");
                CleanScenes();
                var setup = EditorSceneManager.GetSceneManagerSetup();
                Require(setup.Length > 0 && setup.All(s => !string.IsNullOrEmpty(s.path)), "Save open scenes before verification.");
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
            var run = live ?? Load(); if (run == null) return;
            if (run.phase == "restoring" && !EditorApplication.isPlayingOrWillChangePlaymode) { Finish(run); return; }
            try
            {
                Require(DateTime.UtcNow.Ticks < run.deadlineTicks, "Native locomotion verification timed out in " + run.phase);
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                Require(string.IsNullOrEmpty(SessionState.GetString(LogKey, "")), "Unity logged an error during verification.");
                if (run.phase == "opening" && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    CleanScenes(); var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                    run.baselineObjects = Components<Transform>(scene).Length; run.phase = "entering"; Save(run); EditorApplication.isPlaying = true;
                }
                else if (run.phase == "initializing" && EditorApplication.isPlaying && Time.frameCount >= run.readyFrame)
                { live = run; Initialize(run); Save(run); }
                else if (run.phase == "observing" && EditorApplication.isPlaying && previousFrame != Time.frameCount)
                {
                    previousFrame = Time.frameCount; Observe(run);
                    if (!run.freeWalk.launched && run.simulatedSeconds >= 10f) LaunchFreeWalk(run);
                    if (run.freeWalk.launched && !run.freeWalk.arrived) ObserveFreeWalk(run);
                    if (run.simulatedSeconds >= 60f)
                    {
                        Require(run.freeWalk.passed, "The native 8m free walk did not pass.");
                        Require(run.ordinary.samples > 100 && run.ordinary.movingSamples > 100, "Insufficient ordinary movement observations.");
                        Require(run.ordinary.externallyLimitedSamples == 0, "Ordinary actors still received an external crowd speed limit.");
                        Require(run.ordinary.headingOwnershipViolations == 0, "A moving ordinary passenger disabled native agent heading control.");
                        Require(run.ordinary.customRoutingSamples == 0, "An ordinary passenger used retired custom route/turn control.");
                        Require(world.passengers.Count(p => p && p.automaticRequests) == run.automaticActors, "Automatic-event settings changed during measurement.");
                        run.finalObjects = Components<Transform>(SceneManager.GetActiveScene()).Length;
                        Require(run.finalObjects == run.baselineObjects && world.passengers.Length == 300, "Saved population or object count changed.");
                        Exit(run);
                    }
                    else Save(run);
                }
            }
            catch (Exception error) { run.errors.Add(error.ToString()); run.abort = true; Exit(run); }
        }

        static void Initialize(Run run)
        {
            world = Components<StationSocialWorld>(SceneManager.GetActiveScene()).Single();
            actors = Components<StationActorMotion>(SceneManager.GetActiveScene());
            run.passengers = world.passengers.Length; run.actors = actors.Length;
            Require(run.passengers == 300, "Expected the saved 300 passengers.");
            var coordinators = Components<StationCrowdCoordinator>(SceneManager.GetActiveScene()); run.coordinators = coordinators.Length;
            run.predictionDisabled = coordinators.All(c => !c.enablePrediction);
            run.contactSeparationDisabled = coordinators.All(c => !c.enableContactSeparation);
            Require(run.predictionDisabled && run.contactSeparationDisabled, "Saved native policy must have prediction and contact separation disabled; verifier does not override them.");
            AuditNativeSources(run);
            Require(run.nativeSourceAuditPassed, "Runtime source still contains a retired planner call or explicit velocity/Move override: " + string.Join("; ", run.sourceAuditFindings));
            run.automaticActors = world.passengers.Count(p => p && p.automaticRequests);
            Require(run.automaticActors > 0, "Automatic passenger events must remain enabled.");
            run.initialConversations = world.passengers.Sum(p => p.CompletedConversations);
            foreach (var activity in world.passengers.Select(p => p.activity).Distinct()) run.activities.Add(new ActivitySummary { activity = activity.ToString() });
            ratios.Clear(); intervals.Clear(); requestedSum = agentSum = actualSum = ratioSum = playbackSum = animationErrorSum = 0d;
            clearSpeedSum = clearRatioSum = clearPlaybackSum = clearErrorSum = 0d;
            Time.timeScale = 1f; Application.runInBackground = true;
            run.startedAt = previousTime = nextOrdinary = nextSnapshot = Time.time; previousUnscaled = Time.unscaledTime;
            run.wallStartedAt = EditorApplication.timeSinceStartup; run.phase = "observing";
        }

        static void Observe(Run run)
        {
            run.simulatedSeconds = Time.time - run.startedAt;
            run.wallSeconds = (float)(EditorApplication.timeSinceStartup - run.wallStartedAt);
            run.simulatedToWallRatio = run.wallSeconds > 0f ? run.simulatedSeconds / run.wallSeconds : 0f;
            run.minimumTimeScale = Mathf.Min(run.minimumTimeScale, Time.timeScale); run.maximumTimeScale = Mathf.Max(run.maximumTimeScale, Time.timeScale);
            Require(Mathf.Abs(Time.timeScale - 1f) < .001f, "Time scale changed from 1 during the native speed check.");
            float interval = Time.unscaledTime - previousUnscaled; previousUnscaled = Time.unscaledTime;
            if (interval > 0f) intervals.Add(interval * 1000f);
            foreach (var group in world.walkingGroups)
            {
                if (!group || !group.isActiveAndEnabled) continue;
                run.walkingGroupObservedSeconds += interval;
                if (group.WaitingForCompanions) run.walkingGroupHeldForCompanionsSeconds += interval;
                if (group.WaitingAtWaypoint) run.walkingGroupAtWaypointSeconds += interval;
            }
            if (Time.time < nextOrdinary) return; nextOrdinary = Time.time + .25f;
            var sample = new Snapshot { simulatedSeconds = run.simulatedSeconds, wallSeconds = run.wallSeconds, timeScale = Time.timeScale };
            float samplePlayback = 0f; int sampleWalking = 0;
            foreach (var passenger in world.passengers)
            {
                if (!passenger || passenger == fixture && !passenger.enabled) continue;
                var m = passenger.motion; if (!HasTravelIntent(m)) continue;
                var s = run.ordinary; float actual = m.ActualPlanarSpeed, requested = m.RequestedTravelSpeed, ratio = actual / requested;
                s.samples++; requestedSum += requested; agentSum += m.agent.speed; actualSum += actual; ratioSum += ratio; ratios.Add(ratio);
                var activity = run.activities.First(a => a.activity == passenger.activity.ToString()); activity.intentSamples++;
                activity.meanRequestedSpeed += (requested - activity.meanRequestedSpeed) / activity.intentSamples;
                activity.meanActualSpeed += (actual - activity.meanActualSpeed) / activity.intentSamples;
                s.minimumCrowdFactor = Mathf.Min(s.minimumCrowdFactor, m.CurrentCrowdSpeedFactor);
                sample.intentActors++; sample.meanActualSpeed += actual; sample.meanRequestedSpeed += requested;
                if (actual > .15f) { s.movingSamples++; sample.movingActors++; }
                if (actual > .15f && !m.agent.updateRotation) s.headingOwnershipViolations++;
                if (m.IsAvoidingQueue || m.IsAvoidingStationaryCrowd || m.IsTurningInPlace || m.QueueDetourCount != 0 || m.StationaryDetourCount != 0) s.customRoutingSamples++;
                if (ratio < .5f) s.samplesBelowHalfIntent++;
                if (m.CurrentCrowdSpeedFactor < .99f) { s.externallyLimitedSamples++; sample.externallyLimitedActors++; }
                if (m.IsWalkPose)
                {
                    s.walkPoseSamples++; playbackSum += m.AppliedWalkPlaybackSpeed;
                    animationErrorSum += Mathf.Abs(m.PresentedLocomotionSpeed - actual);
                    samplePlayback += m.AppliedWalkPlaybackSpeed; sampleWalking++;
                }
            }
            if (sample.intentActors > 0) { sample.meanActualSpeed /= sample.intentActors; sample.meanRequestedSpeed /= sample.intentActors; }
            if (sampleWalking > 0) sample.meanPlaybackMultiplier = samplePlayback / sampleWalking;
            run.finalConversations = world.passengers.Sum(p => p.CompletedConversations);
            sample.completedConversations = run.finalConversations - run.initialConversations;
            if (Time.time >= nextSnapshot) { run.samples.Add(sample); nextSnapshot = Time.time + 2f; }
            Summarize(run);
        }

        static void LaunchFreeWalk(Run run)
        {
            fixture = world.passengers.FirstOrDefault(p => p && p.isActiveAndEnabled && p.activity == StationPassengerActivity.SoloWalking && p.IsAvailableForActivity && HasTravelIntent(p.motion));
            Require(fixture, "No available ordinary solo traveller can be borrowed.");
            var m = fixture.motion; var f = run.freeWalk; f.actor = fixture.name;
            f.originalRequestedSpeed = m.RequestedTravelSpeed;
            f.originalAgentMaximumSpeed = m.agent.speed;
            Require(f.originalRequestedSpeed > .1f, "Borrowed passenger has no ordinary travel intent.");
            f.agentAcceleration = m.agent.acceleration; f.agentAngularSpeed = m.agent.angularSpeed; f.directionalLocomotion = m.directionalLocomotion;
            var center = FindClearCenter(m); f.start = center + Vector3.left * 4f; f.goal = center + Vector3.right * 4f;
            fixture.enabled = false;
            Require(Mathf.Abs(m.RequestedTravelSpeed - f.originalRequestedSpeed) < .001f, "Disabling fixture FSM changed its travel intent.");
            Require(m.EnableNavigationAt(f.start), "Could not place the saved fixture passenger on the native NavMesh.");
            m.transform.rotation = Quaternion.LookRotation(Vector3.right);
            Require(m.TryMoveTo(f.goal), "Native 8m route request failed: " + m.NavigationIssue); m.PlayWalk();
            fixturePrevious = m.transform.position; previousTime = Time.time;
            f.launched = true; f.startedAt = Time.time; f.arrivalLimit = Mathf.Min(20f, 8f / f.originalRequestedSpeed + 5f);
            initialQueueDetours = m.QueueDetourCount; initialStationaryDetours = m.StationaryDetourCount;
        }

        static void ObserveFreeWalk(Run run)
        {
            var f = run.freeWalk; var m = fixture.motion; float dt = Mathf.Max(0f, Time.time - previousTime); previousTime = Time.time;
            f.elapsed = Time.time - f.startedAt; f.motion = m.CurrentMotion;
            f.travelled += FlatDistance(fixturePrevious, m.transform.position); fixturePrevious = m.transform.position;
            f.closestGoal = Mathf.Min(f.closestGoal, FlatDistance(m.transform.position, f.goal));
            f.minimumCrowdFactor = Mathf.Min(f.minimumCrowdFactor, m.CurrentCrowdSpeedFactor);
            f.queueDetours = m.QueueDetourCount - initialQueueDetours; f.stationaryDetours = m.StationaryDetourCount - initialStationaryDetours;
            f.maximumAgentSpeedDeviation = Mathf.Max(f.maximumAgentSpeedDeviation, Mathf.Abs(m.agent.speed - f.originalAgentMaximumSpeed));
            Require(Mathf.Abs(m.RequestedTravelSpeed - f.originalRequestedSpeed) < .001f, "Fixture requested speed changed during its ordinary-speed route.");
            Require(f.maximumAgentSpeedDeviation < .001f, "The free-walk agent's native maximum speed was changed during travel.");
            Require(m.CurrentCrowdSpeedFactor >= .99f, "Free-walk actor received an external crowd speed limit.");
            Require(m.QueueDetourCount == 0 && m.StationaryDetourCount == 0 && !m.IsAvoidingQueue && !m.IsAvoidingStationaryCrowd && !m.IsTurningInPlace,
                "Free-walk actor used retired custom routing or turn control.");
            Require(m.ActualPlanarSpeed <= .15f || m.agent.updateRotation, "Native agent does not own heading while the free-walk actor is moving.");
            if (f.elapsed >= 1f && f.closestGoal > 1.25f && !m.IsTurningInPlace && m.IsWalkPose && HasTravelIntent(m) && LocallyClear(m))
            {
                f.clearSamples++; f.clearSampleSeconds += dt;
                clearSpeedSum += m.ActualPlanarSpeed * dt; clearRatioSum += m.ActualPlanarSpeed / f.originalRequestedSpeed * dt;
                clearPlaybackSum += m.AppliedWalkPlaybackSpeed * dt;
                clearErrorSum += Mathf.Abs(m.PresentedLocomotionSpeed - m.ActualPlanarSpeed) * dt;
                f.clearMeanActualSpeed = (float)(clearSpeedSum / f.clearSampleSeconds); f.clearMeanSpeedRatio = (float)(clearRatioSum / f.clearSampleSeconds);
                f.clearMeanPlaybackMultiplier = (float)(clearPlaybackSum / f.clearSampleSeconds); f.clearMeanScalarAnimationError = (float)(clearErrorSum / f.clearSampleSeconds);
            }
            if (m.HasArrived && f.closestGoal < .3f)
            {
                f.arrived = true;
                Require(f.clearSampleSeconds >= 2f, "Fewer than two seconds of clear steady walking; fixture is inconclusive.");
                Require(f.clearMeanSpeedRatio >= .85f, "Clear native walking did not recover at least 85% of the passenger's own intended speed.");
                Require(f.clearMeanScalarAnimationError <= Mathf.Max(.08f, f.originalRequestedSpeed * .15f), "Walking animation rate did not track actual movement in the clear steady samples.");
                Require(f.elapsed <= f.arrivalLimit, "Native 8m free walk exceeded its speed-derived arrival limit.");
                f.passed = true; m.StopNavigation(); fixture.enabled = true;
            }
            else Require(f.elapsed <= f.arrivalLimit, "Native 8m free walk did not arrive within its speed-derived limit; remaining=" + f.closestGoal);
        }

        static Vector3 FindClearCenter(StationActorMotion moving)
        {
            Vector3 best = Vector3.zero; float bestClearance = -1f;
            for (int z = -16; z <= 40; z += 2) for (int x = -20; x <= 20; x += 2)
            {
                var center = new Vector3(x, .02f, z); var start = center + Vector3.left * 4f; var end = center + Vector3.right * 4f;
                if (!NavMesh.SamplePosition(start, out var a, .1f, moving.agent.areaMask) || FlatDistance(start, a.position) > .08f ||
                    !NavMesh.SamplePosition(end, out var b, .1f, moving.agent.areaMask) || FlatDistance(end, b.position) > .08f ||
                    NavMesh.Raycast(a.position, b.position, out _, moving.agent.areaMask)) continue;
                float clearance = float.MaxValue;
                foreach (var other in actors)
                {
                    if (!other || other == moving) continue;
                    float gap = SegmentDistance(other.transform.position, a.position, b.position) - moving.agent.radius - (other.agent ? other.agent.radius : .32f);
                    clearance = Mathf.Min(clearance, gap);
                }
                if (clearance > bestClearance) { bestClearance = clearance; best = (a.position + b.position) * .5f; }
            }
            Require(bestClearance >= .6f, "No currently body-clear continuous 8m native corridor; no actor was moved to manufacture clearance.");
            return best;
        }
        static bool LocallyClear(StationActorMotion moving)
        {
            Vector3 ahead = moving.agent.steeringTarget - moving.transform.position; ahead.y = 0f;
            if (ahead.sqrMagnitude < .1f) return false;
            Vector3 end = moving.transform.position + ahead.normalized * Mathf.Min(2.5f, ahead.magnitude);
            foreach (var other in actors)
            {
                if (!other || other == moving) continue;
                float clearance = moving.agent.radius + (other.agent ? other.agent.radius : .32f) + .5f;
                if (SegmentDistance(other.transform.position, moving.transform.position, end) < clearance) return false;
            }
            return true;
        }
        static bool HasTravelIntent(StationActorMotion m) => m && m.isActiveAndEnabled && m.agent && m.agent.enabled && m.agent.isOnNavMesh &&
            m.agent.hasPath && !m.agent.pathPending && !m.agent.isStopped && m.RequestedTravelSpeed > .1f &&
            m.agent.remainingDistance > Mathf.Max(.3f, m.agent.stoppingDistance + .15f);
        static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            var segment = b - a; segment.y = 0f; var offset = p - a; offset.y = 0f;
            float t = segment.sqrMagnitude > .0001f ? Mathf.Clamp01(Vector3.Dot(offset, segment) / segment.sqrMagnitude) : 0f;
            return (offset - segment * t).magnitude;
        }
        static void Summarize(Run run)
        {
            var s = run.ordinary;
            if (s.samples > 0)
            {
                s.meanRequestedSpeed = (float)(requestedSum / s.samples); s.meanAgentSpeed = (float)(agentSum / s.samples);
                s.meanActualSpeed = (float)(actualSum / s.samples); s.meanActualToRequested = (float)(ratioSum / s.samples);
            }
            if (s.walkPoseSamples > 0) { s.meanPlaybackMultiplier = (float)(playbackSum / s.walkPoseSamples); s.meanScalarAnimationError = (float)(animationErrorSum / s.walkPoseSamples); }
        }
        static void AuditNativeSources(Run run)
        {
            // A focused textual audit, not a full semantic proof of every possible navigation write.
            // Initial Warp placement and destination/stop commands remain valid activity operations.
            string pattern = @"\bStation(?:Queue|Stationary)Avoidance\s*\.\s*Plan\s*\(|\.\s*velocity\s*=(?!=)|\.\s*Move\s*\(";
            foreach (string path in Directory.GetFiles("Assets/TrainStation/Social/Runtime", "*.cs", SearchOption.AllDirectories))
            {
                string source = File.ReadAllText(path);
                source = Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
                foreach (Match match in Regex.Matches(source, pattern))
                    run.sourceAuditFindings.Add(path.Replace('\\', '/') + ": " + match.Value.Trim());
            }
            run.nativeSourceAuditPassed = run.sourceAuditFindings.Count == 0;
        }
        static void Exit(Run run)
        {
            if (fixture && !fixture.enabled) { fixture.motion.StopNavigation(); fixture.enabled = true; }
            if (ratios.Count > 0) { ratios.Sort(); run.ordinary.medianActualToRequested = Percentile(ratios, .5f); run.ordinary.p10ActualToRequested = Percentile(ratios, .1f); }
            if (intervals.Count > 0) { intervals.Sort(); run.observationMedianMs = Percentile(intervals, .5f); run.observationP95Ms = Percentile(intervals, .95f); }
            run.phase = "exiting"; Save(run); Write(run);
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Time.timeScale = run.originalTimeScale; EditorApplication.isPlaying = false; }
            else Finish(run);
        }
        static void ModeChanged(PlayModeStateChange state)
        {
            var run = live ?? Load(); if (run == null) return;
            if (state == PlayModeStateChange.EnteredPlayMode) { run.phase = "initializing"; run.readyFrame = Time.frameCount + 10; }
            else if (state == PlayModeStateChange.ExitingPlayMode && run.phase != "exiting") { run.abort = true; run.errors.Add("Play stopped before verification completed."); run.phase = "exiting"; }
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
            string log = SessionState.GetString(LogKey, ""); if (!string.IsNullOrEmpty(log)) run.errors.Add("Unity errors:\n" + log);
            run.success = !run.abort && run.errors.Count == 0 && run.restoredSettings && run.restoredScenes && run.freeWalk.passed && run.simulatedSeconds >= 60f;
            run.phase = "complete"; run.completedUtc = DateTime.UtcNow.ToString("O"); SessionState.EraseString(StateKey); SessionState.EraseString(LogKey);
            Write(run); live = null; world = null; actors = null; fixture = null; ratios.Clear(); intervals.Clear(); previousFrame = -1;
            Debug.Log("Station native locomotion verification " + (run.success ? "PASSED" : "FAILED") + ". See " + ReportPath);
        }
        static void Log(string message, string trace, LogType type)
        {
            if (Load() == null || type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            string previous = SessionState.GetString(LogKey, ""); if (previous.Length < 16000) SessionState.SetString(LogKey, previous + message + "\n" + trace + "\n");
        }
        static Run Load() { string json = SessionState.GetString(StateKey, ""); return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Run>(json); }
        static void Save(Run run)
        {
            SessionState.SetString(StateKey, JsonUtility.ToJson(run));
            if (EditorApplication.timeSinceStartup < nextWrite) return;
            nextWrite = (float)EditorApplication.timeSinceStartup + 1f; Write(run);
        }
        static void Write(Run run) { Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath, JsonUtility.ToJson(run, true)); }
        static float Percentile(List<float> values, float p) => values[Mathf.Clamp(Mathf.CeilToInt((values.Count - 1) * p), 0, values.Count - 1)];
        static void CleanScenes() { for (int i = 0; i < SceneManager.sceneCount; i++) Require(!SceneManager.GetSceneAt(i).isDirty, "Save or revert scene edits; this check never discards user changes."); }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        static float FlatDistance(Vector3 a, Vector3 b) { a.y = b.y = 0f; return Vector3.Distance(a, b); }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
