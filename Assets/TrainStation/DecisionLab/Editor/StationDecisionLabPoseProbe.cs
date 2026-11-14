#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.HumanAnimation;
using StationFlow.TrainStation.Social;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Observe evaluated live poses. Never samples, rebinds, updates or writes an actor's animation.</summary>
    [InitializeOnLoad]
    public static class StationDecisionLabPoseProbe
    {
        public const string ReportPath = "Reports/TrainStationDecisionLab/pose-probe.json";
        const string ControlKey = "StationFlow.DecisionLab.PoseDiagnostic";
        const int ActorLimit = 64, SelectedLimit = 90, CrowdDetailLimit = 192, SevereLimit = 12;
        static readonly HumanBodyBones[] LegBones = { HumanBodyBones.Hips, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes };
        sealed class AfterAnimationPoseProbe { }

        [Serializable] public sealed class Options
        {
            public string variant = "baseline";
            public float runningSeconds = 45f, pausedUnscaledSeconds = 30f, resumedSeconds = 10f;
            public int selectedIndex = 5;
            public bool followSelected = true, stopOnSevere = false;
            public bool exercisePausedControls;
        }
        [Serializable] public sealed class Bone
        {
            public string bone, path;
            public Vector3 worldPosition, modelPosition, localPosition, localScale, lossyScale;
            public Quaternion worldRotation, localRotation;
            public float determinant;
        }
        [Serializable] public sealed class Clip
        {
            public string name, path; public float weight, length; public bool humanMotion, looping;
        }
        [Serializable] public sealed class State
        {
            public string path; public int hash; public float normalizedTime, length, speed, speedMultiplier;
            public bool foundAuthoredState, footIK, writeDefaults; public List<Clip> clips = new List<Clip>();
        }
        [Serializable] public sealed class GraphNode
        {
            public string path, type, state; public double time, speed; public int inputs;
        }
        [Serializable] public sealed class Skin
        {
            public string path, meshPath, rootBone; public bool enabled, visible, updateWhenOffscreen, baked, truncated;
            public int vertices; public Vector3 lossyScale, rendererCenter, rendererSize, bakedWorldMin, bakedWorldMax;
            public float minimumWorldY, leftSoleEstimateY, rightSoleEstimateY;
        }
        [Serializable] public sealed class Pose
        {
            public string actor, passengerId, path, phase, activity, execution, motion, playerMotion, libraryPath, requestedClipPath;
            public string animatorPath, avatarPath, controllerPath, updateMode, cullingMode, graphUpdateMode, severeReason, reviewReason, animationError;
            public int frame; public double realtime; public float simulationTime, timeScale, deltaTime, playbackSpeed, animatorSpeed, humanScale, actualSpeed;
            public bool walking, active, animatorEnabled, animatorInitialized, animatorHuman, avatarValid, rootMotion, stabilizeFeet, playerPaused;
            public bool transition, graphValid, graphPlaying, bonesFinite, paused, graphNodesTruncated;
            public float transitionPhase, leftKneeAngle, rightKneeAngle, leftKneeForward, rightKneeForward, leftExtension, rightExtension;
            public float leftFootForward, rightFootForward, leftAnkleAboveRoot, rightAnkleAboveRoot, footAboveHip, kneeAboveHip;
            public Vector3 rootPosition, rootForward, modelForward, hips, head, leftUpper, leftKnee, leftFoot, leftToes, rightUpper, rightKnee, rightFoot, rightToes;
            public Vector3 leftKneeModel, rightKneeModel, leftFootModel, rightFootModel;
            public State current, next;
            public List<Bone> bones = new List<Bone>(), rootToModel = new List<Bone>();
            public List<GraphNode> graphNodes = new List<GraphNode>();
            public List<Skin> skins = new List<Skin>();
        }
        [Serializable] public sealed class ActorStatistics
        {
            public string actor, passengerId, path;
            public int samples, runningSamples, pausedSamples, resumedSamples, severeSamples, negativeKneeSamples;
            public float minimumKneeAngle = 180f, minimumKneeForward = 999f, maximumFootAboveHip = -999f, maximumKneeAboveHip = -999f;
            public float maximumPausedBoneDisplacement, maximumPausedModelBoneDisplacement, maximumPausedPerFrameBoneDisplacement, maximumPausedPhaseChange;
        }
        [Serializable] public sealed class Report
        {
            public string startedUtc, stoppedUtc, checkedUtc, scene, phase, archivePath, outputDirectory, sourceSceneHash, sourceScheduleHash, labSceneHash;
            public string unityVersion, stopReason;
            public bool active, autonomous, completed, severeObserved, domainReloadObserved, hooksInstalled, originalsPreserved, labPreserved;
            public Options options;
            public int postAnimationFrames, actorPoseSamples, maxEligibleWalkers, omittedActorObservations, skinBakes, variantActors, transientControllers, transientLibraries;
            public float startedSimulation, pausedAtSimulation, resumedAtSimulation, finishedSimulation, pausedDuration, captureCostMilliseconds;
            public bool pausedOrbitChecked, pausedDelayChecked, pausedCancellationChecked, pausedClockPreserved;
            public string pausedScreenshot, resumedScreenshot, delayEventAfter, cancellationEventAfter;
            public List<ActorStatistics> actors = new List<ActorStatistics>();
            public List<Pose> selected = new List<Pose>(), crowdDetails = new List<Pose>(), severe = new List<Pose>();
            public List<Pose> selectedRunning = new List<Pose>(), selectedPaused = new List<Pose>(), selectedResumed = new List<Pose>(), phaseBoundaries = new List<Pose>();
            public List<string> animationPlayerErrors = new List<string>();
            public string variantMethod;
            public List<string> errors = new List<string>();
            public string method = "PlayerLoop callback appended after all PreLateUpdate animation/LateUpdate/constraint stages. Every frame observes bounded active locomotion actors. Selected detail at10Hz, round-robin crowd detail at2Hz, bounded severe snapshots. CPU BakeMesh uses transient meshes only; no Animator.Update/Rebind/Play/SampleAnimation, actor, camera or skin-renderer writes.";
            public string interpretation = "Negative signed knee projection is a review metric, not an anatomical verdict. Severe flags are invalid joints, ankles above standing hips/head, or an elevated nearly folded knee. Skin sole estimates use nearby vertices and are not contact proof. Collection completion is not gait acceptance.";
        }
        [Serializable] sealed class Control
        {
            public string phase, originalPlayScene, startupError; public Options options;
            public bool oldOptionsEnabled, oldPaused, oldBackground; public int oldOptions, oldFrameRate, oldVsync;
            public float oldTimeScale; public long deadlineTicks;
        }
        static Report report;
        static Control control;
        static StationDecisionWorld world;
        static StationDecisionDashboard dashboard;
        static StationActorMotion[] motions;
        static readonly Dictionary<StationActorMotion, ActorStatistics> stats = new Dictionary<StationActorMotion, ActorStatistics>();
        static readonly Dictionary<StationActorMotion, Pose> pausedPrevious = new Dictionary<StationActorMotion, Pose>();
        static readonly Dictionary<StationActorMotion, Pose> pausedLast = new Dictionary<StationActorMotion, Pose>();
        static readonly HashSet<StationActorMotion> severeRecorded = new HashSet<StationActorMotion>();
        static readonly Dictionary<int, string> statePaths = new Dictionary<int, string>();
        static readonly List<UnityEngine.Object> transientObjects = new List<UnityEngine.Object>();
        static readonly List<PlayableGraph> stoppedGraphs = new List<PlayableGraph>();
        static int variantActors, transientControllers, transientLibraries;
        static bool variantPrepared;
        static double beganRealtime, pausedRealtime, nextSelected, nextCrowd, nextWrite;
        static int crowdCursor;
        static bool requestCompletion;

        static StationDecisionLabPoseProbe()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
        }

        public static void Start()
        {
            Require(EditorApplication.isPlaying && SceneManager.GetActiveScene().path == ScenePath, "Start a pose probe while playing Decision Lab.");
            Require(report == null || !report.active, "A pose probe is already active.");
            Initialize(false, new Options { followSelected = false });
        }
        public static void Capture()
        {
            Require(EditorApplication.isPlaying && SceneManager.GetActiveScene().path == ScenePath, "A pose capture requires running Decision Lab.");
            Require(report == null || !report.active, "Stop the active pose probe before taking a one-shot capture.");
            Initialize(false, new Options { followSelected = false });
            report.phase = "one-shot-current-evaluated-pose";
            Observe(); Complete("one-shot", true);
        }
        public static void Stop()
        {
            if (report != null && report.active) Complete("explicit stop", true);
        }
        public static void WriteStatus()
        {
            Require(report != null, "No pose probe has run in this Editor session.");
            report.checkedUtc = DateTime.UtcNow.ToString("O");
            Write(ReportPath, report);
            if (!string.IsNullOrEmpty(report.archivePath)) Write(report.archivePath, report);
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Diagnose running and paused poses")]
        public static void BeginDiagnostic() => BeginDiagnostic(new Options());
        public static void BeginDiagnostic(Options options)
        {
            RequireEditAndClean();
            Require(string.IsNullOrEmpty(SessionState.GetString(ControlKey, "")), "A pose diagnostic is already active.");
            Require(options != null && new[] { "baseline", "saved-repair", "no-stabilization", "no-foot-ik", "graph-stop-on-pause" }.Contains(options.variant), "Unknown runtime-only pose variant.");
            Require(options.runningSeconds > 0 && options.pausedUnscaledSeconds > 0 && options.resumedSeconds > 0, "Diagnostic phase durations must be positive.");
            Open();
            control = new Control { phase = "entering", options = options, deadlineTicks = DateTime.UtcNow.AddMinutes(6).Ticks,
                originalPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene),
                oldOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled, oldOptions = (int)EditorSettings.enterPlayModeOptions,
                oldPaused = EditorApplication.isPaused, oldBackground = Application.runInBackground, oldFrameRate = Application.targetFrameRate,
                oldVsync = QualitySettings.vSyncCount, oldTimeScale = Time.timeScale };
            SaveControl(); EditorSettings.enterPlayModeOptionsEnabled = false;
            EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            EditorApplication.isPaused = false; EditorApplication.isPlaying = true;
        }

        static void Initialize(bool autonomous, Options options)
        {
            var scene = SceneManager.GetActiveScene();
            world = Components<StationDecisionWorld>(scene).Single();
            dashboard = Components<StationDecisionDashboard>(scene).Single();
            Require(world.rail && world.rail.IsReady, "Rail/world initialization is incomplete.");
            motions = Components<StationActorMotion>(scene).Where(m => m && m.gameObject.activeInHierarchy && m.animationPlayer && m.animationPlayer.TargetAnimator).ToArray();
            stats.Clear(); pausedPrevious.Clear(); pausedLast.Clear(); severeRecorded.Clear(); statePaths.Clear(); crowdCursor = 0; requestCompletion = false;
            beganRealtime = EditorApplication.timeSinceStartup; nextSelected = nextCrowd = nextWrite = 0;
            report = new Report { startedUtc = DateTime.UtcNow.ToString("O"), scene = scene.path, unityVersion = Application.unityVersion,
                active = true, autonomous = autonomous, options = options, phase = autonomous ? "running-baseline" : "observing-current-run",
                startedSimulation = world.ElapsedSeconds, outputDirectory = world.OutputDirectory,
                sourceSceneHash = Hash(SourceScene), sourceScheduleHash = Hash(SourceSchedule), labSceneHash = Hash(ScenePath),
                archivePath = ReportDirectory + "/pose-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + options.variant + ".json" };
            report.variantActors = variantActors; report.transientControllers = transientControllers; report.transientLibraries = transientLibraries;
            report.variantMethod = options.variant == "baseline" ? "Saved library, controller, IK and foot stabilization unchanged." :
                options.variant == "no-stabilization" ? "Live HumanAnimationPlayer serialized stabilizeFeet and Animator.stabilizeFeet disabled before first motion request; sources untouched." :
                options.variant == "no-foot-ik" ? "Independent transient controller/state machine/states preserving saved named-state parameters and clip references, all state Foot IK disabled; transient library assigned only to live players before first motion request." :
                "Existing Animator graphs stopped before world pause and played before resume; Animators remain enabled.";
            if (autonomous)
            {
                Application.runInBackground = true; Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0; Time.timeScale = 1f;
                if (options.followSelected) dashboard.SelectParticipant(Mathf.Clamp(options.selectedIndex, 0, world.participants.Length - 1));
            }
            var loop = PlayerLoop.GetCurrentPlayerLoop(); Remove(ref loop);
            report.hooksInstalled = Install(ref loop);
            Require(report.hooksInstalled, "Could not append the post-animation observation phase.");
            PlayerLoop.SetPlayerLoop(loop); WriteStatus();
        }

        static void AfterAnimation()
        {
            if (report == null || !report.active) return;
            try { Observe(); }
            catch (Exception error) { report.errors.Add(error.ToString()); requestCompletion = true; }
        }
        static void Observe()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ++report.postAnimationFrames;
            var selected = dashboard ? dashboard.SelectedPassenger : null;
            var selectedMotion = selected ? selected.motion : null;
            var eligible = motions.Where(m => m && m.gameObject.activeInHierarchy && (m.IsWalkPose || m == selectedMotion)).OrderBy(m => m == selectedMotion ? 0 : 1).ToArray();
            report.maxEligibleWalkers = Mathf.Max(report.maxEligibleWalkers, eligible.Length);
            report.omittedActorObservations += Mathf.Max(0, eligible.Length - ActorLimit);
            double now = EditorApplication.timeSinceStartup;
            bool selectedDue = now >= nextSelected, crowdDue = now >= nextCrowd;
            StationActorMotion roundRobin = eligible.Length == 0 ? null : eligible[crowdCursor++ % Math.Min(eligible.Length, ActorLimit)];
            foreach (var motion in eligible.Take(ActorLimit))
            {
                Pose pose = ReadPose(motion, false, false);
                ++report.actorPoseSamples;
                if (!stats.TryGetValue(motion, out var aggregate))
                {
                    aggregate = new ActorStatistics { actor = motion.name, passengerId = pose.passengerId, path = pose.path };
                    stats.Add(motion, aggregate); report.actors.Add(aggregate);
                    AddBounded(report.crowdDetails, ReadPose(motion, true, true), CrowdDetailLimit);
                }
                ++aggregate.samples;
                if (!string.IsNullOrEmpty(pose.animationError) && !report.animationPlayerErrors.Contains(pose.passengerId + ": " + pose.animationError))
                    report.animationPlayerErrors.Add(pose.passengerId + ": " + pose.animationError);
                if (world.Paused) ++aggregate.pausedSamples;
                else if (report.phase == "resumed") ++aggregate.resumedSamples;
                else ++aggregate.runningSamples;
                aggregate.minimumKneeAngle = Mathf.Min(aggregate.minimumKneeAngle, Mathf.Min(pose.leftKneeAngle, pose.rightKneeAngle));
                aggregate.minimumKneeForward = Mathf.Min(aggregate.minimumKneeForward, Mathf.Min(pose.leftKneeForward, pose.rightKneeForward));
                aggregate.maximumFootAboveHip = Mathf.Max(aggregate.maximumFootAboveHip, pose.footAboveHip);
                aggregate.maximumKneeAboveHip = Mathf.Max(aggregate.maximumKneeAboveHip, pose.kneeAboveHip);
                if (!string.IsNullOrEmpty(pose.reviewReason)) ++aggregate.negativeKneeSamples;
                if (world.Paused)
                {
                    if (pausedPrevious.TryGetValue(motion, out var previous))
                    {
                        aggregate.maximumPausedBoneDisplacement = Mathf.Max(aggregate.maximumPausedBoneDisplacement,
                            new[] { Vector3.Distance(previous.leftKnee, pose.leftKnee), Vector3.Distance(previous.rightKnee, pose.rightKnee),
                                Vector3.Distance(previous.leftFoot, pose.leftFoot), Vector3.Distance(previous.rightFoot, pose.rightFoot) }.Max());
                        aggregate.maximumPausedModelBoneDisplacement = Mathf.Max(aggregate.maximumPausedModelBoneDisplacement,
                            ModelGap(previous, pose));
                        aggregate.maximumPausedPhaseChange = Mathf.Max(aggregate.maximumPausedPhaseChange, Mathf.Abs(previous.current.normalizedTime - pose.current.normalizedTime));
                    }
                    else pausedPrevious[motion] = pose; // Compare with the first paused evaluated pose, not just the previous frame.
                    if (pausedLast.TryGetValue(motion, out var last))
                        aggregate.maximumPausedPerFrameBoneDisplacement = Mathf.Max(aggregate.maximumPausedPerFrameBoneDisplacement, ModelGap(last, pose));
                    pausedLast[motion] = pose;
                }
                if (!string.IsNullOrEmpty(pose.severeReason))
                {
                    ++aggregate.severeSamples; report.severeObserved = true;
                    if (severeRecorded.Add(motion) && report.severe.Count < SevereLimit) report.severe.Add(ReadPose(motion, true, true));
                    if (report.options.stopOnSevere) requestCompletion = true;
                }
                if (motion == selectedMotion && selectedDue)
                {
                    var detail = ReadPose(motion, true, crowdDue);
                    AddBounded(report.selected, detail, SelectedLimit);
                    AddBounded(world.Paused ? report.selectedPaused : report.phase == "resumed" ? report.selectedResumed : report.selectedRunning, detail, 100);
                }
                if (motion == roundRobin && crowdDue) AddBounded(report.crowdDetails, ReadPose(motion, true, false), CrowdDetailLimit);
            }
            if (selectedDue) nextSelected = now + .1d;
            if (crowdDue) nextCrowd = now + .5d;
            watch.Stop(); report.captureCostMilliseconds = Mathf.Max(report.captureCostMilliseconds, (float)watch.Elapsed.TotalMilliseconds);
        }

        static Pose ReadPose(StationActorMotion motion, bool detail, bool bake)
        {
            var player = motion.animationPlayer; var animator = player.TargetAnimator; var traveller = motion.GetComponent<StationDecisionPassenger>();
            var pose = new Pose { actor = motion.name, passengerId = traveller ? traveller.passengerId : "", path = Hierarchy(motion.transform),
                phase = report.phase, frame = Time.frameCount, realtime = EditorApplication.timeSinceStartup, simulationTime = world.ElapsedSeconds,
                timeScale = Time.timeScale, deltaTime = Time.deltaTime, paused = world.Paused, activity = traveller ? traveller.CurrentActivity.ToString() : "",
                execution = traveller ? traveller.ExecutionState : "", motion = motion.CurrentMotion, playerMotion = player.CurrentMotionId,
                playbackSpeed = player.PlaybackSpeed, animatorSpeed = animator.speed, actualSpeed = motion.ActualPlanarSpeed, walking = motion.IsWalkPose,
                active = motion.gameObject.activeInHierarchy, playerPaused = player.IsPaused, animatorEnabled = animator.enabled,
                animationError = player.LastError,
                animatorInitialized = animator.isInitialized, animatorHuman = animator.isHuman, avatarValid = animator.avatar && animator.avatar.isValid,
                humanScale = animator.isHuman ? animator.humanScale : 1f, rootMotion = animator.applyRootMotion, stabilizeFeet = animator.stabilizeFeet,
                updateMode = animator.updateMode.ToString(), cullingMode = animator.cullingMode.ToString(), rootPosition = motion.transform.position,
                rootForward = motion.transform.forward, modelForward = animator.transform.forward };
            if (!animator.isHuman || !animator.isInitialized)
            { pose.severeReason = "Animator is not an initialized Humanoid."; pose.current = new State(); pose.next = new State(); return pose; }
            Vector3 At(HumanBodyBones bone) { var item = animator.GetBoneTransform(bone); return item ? item.position : new Vector3(float.NaN, 0, 0); }
            pose.hips = At(HumanBodyBones.Hips); pose.head = At(HumanBodyBones.Head);
            pose.leftUpper = At(HumanBodyBones.LeftUpperLeg); pose.leftKnee = At(HumanBodyBones.LeftLowerLeg); pose.leftFoot = At(HumanBodyBones.LeftFoot); pose.leftToes = At(HumanBodyBones.LeftToes);
            pose.rightUpper = At(HumanBodyBones.RightUpperLeg); pose.rightKnee = At(HumanBodyBones.RightLowerLeg); pose.rightFoot = At(HumanBodyBones.RightFoot); pose.rightToes = At(HumanBodyBones.RightToes);
            pose.leftKneeModel = animator.transform.InverseTransformPoint(pose.leftKnee); pose.rightKneeModel = animator.transform.InverseTransformPoint(pose.rightKnee);
            pose.leftFootModel = animator.transform.InverseTransformPoint(pose.leftFoot); pose.rightFootModel = animator.transform.InverseTransformPoint(pose.rightFoot);
            pose.bonesFinite = new[] { pose.hips, pose.head, pose.leftUpper, pose.leftKnee, pose.leftFoot, pose.leftToes, pose.rightUpper, pose.rightKnee, pose.rightFoot, pose.rightToes }.All(Finite);
            pose.leftKneeAngle = Vector3.Angle(pose.leftUpper - pose.leftKnee, pose.leftFoot - pose.leftKnee);
            pose.rightKneeAngle = Vector3.Angle(pose.rightUpper - pose.rightKnee, pose.rightFoot - pose.rightKnee);
            pose.leftKneeForward = KneeForward(pose.leftUpper, pose.leftKnee, pose.leftFoot, pose.modelForward);
            pose.rightKneeForward = KneeForward(pose.rightUpper, pose.rightKnee, pose.rightFoot, pose.modelForward);
            pose.leftExtension = Extension(pose.leftUpper, pose.leftKnee, pose.leftFoot); pose.rightExtension = Extension(pose.rightUpper, pose.rightKnee, pose.rightFoot);
            pose.leftFootForward = Vector3.Dot((pose.leftToes - pose.leftFoot).normalized, pose.modelForward);
            pose.rightFootForward = Vector3.Dot((pose.rightToes - pose.rightFoot).normalized, pose.modelForward);
            pose.leftAnkleAboveRoot = pose.leftFoot.y - pose.rootPosition.y; pose.rightAnkleAboveRoot = pose.rightFoot.y - pose.rootPosition.y;
            pose.footAboveHip = Mathf.Max(pose.leftFoot.y, pose.rightFoot.y) - pose.hips.y;
            pose.kneeAboveHip = Mathf.Max(pose.leftKnee.y, pose.rightKnee.y) - pose.hips.y;
            if (!pose.bonesFinite) pose.severeReason = "Non-finite or missing Humanoid joint.";
            else if (pose.walking && (Mathf.Max(pose.leftFoot.y, pose.rightFoot.y) > pose.head.y - .05f || pose.footAboveHip > .3f * pose.humanScale))
                pose.severeReason = "Walking ankle is above the standing hips/head threshold.";
            else if (pose.walking && pose.kneeAboveHip > .25f * pose.humanScale && Mathf.Min(pose.leftKneeAngle, pose.rightKneeAngle) < 10f)
                pose.severeReason = "Elevated walking knee is almost completely folded.";
            if (Mathf.Min(pose.leftKneeForward, pose.rightKneeForward) < -.01f * pose.humanScale)
                pose.reviewReason = "Negative knee projection in model-forward frame; inspect raw geometry/clip phase rather than classify as catastrophic.";
            pose.current = ReadState(animator, false, detail); pose.next = ReadState(animator, true, detail);
            pose.transition = animator.IsInTransition(0); pose.transitionPhase = pose.transition ? animator.GetAnimatorTransitionInfo(0).normalizedTime : 0;
            if (!detail) return pose;
            pose.animatorPath = Hierarchy(animator.transform); pose.avatarPath = AssetDatabase.GetAssetPath(animator.avatar);
            pose.controllerPath = AssetDatabase.GetAssetPath(animator.runtimeAnimatorController); pose.libraryPath = AssetDatabase.GetAssetPath(player.Library);
            var requested = player.Library ? player.Library.Find(player.CurrentMotionId) : null;
            pose.requestedClipPath = requested != null ? AssetDatabase.GetAssetPath(requested.clip) : "";
            foreach (var bone in LegBones) { var transform = animator.GetBoneTransform(bone); if (transform) pose.bones.Add(ReadBone(transform, animator.transform, bone.ToString())); }
            for (var node = animator.transform; node; node = node.parent)
            { pose.rootToModel.Add(ReadBone(node, animator.transform, "model-chain")); if (node == motion.transform) break; }
            var graph = animator.playableGraph; pose.graphValid = graph.IsValid();
            if (pose.graphValid)
            {
                pose.graphPlaying = graph.IsPlaying(); pose.graphUpdateMode = graph.GetTimeUpdateMode().ToString();
                var visited = new HashSet<Playable>();
                for (int i = 0; i < graph.GetRootPlayableCount(); ++i) ReadGraph(graph.GetRootPlayable(i), "root" + i, pose, visited);
            }
            if (bake) BakeSkins(motion, pose);
            return pose;
        }

        static State ReadState(Animator animator, bool next, bool detail)
        {
            var state = next ? animator.GetNextAnimatorStateInfo(0) : animator.GetCurrentAnimatorStateInfo(0);
            var result = new State { hash = state.fullPathHash, normalizedTime = state.normalizedTime, length = state.length, speed = state.speed, speedMultiplier = state.speedMultiplier };
            if (!detail) return result;
            var controller = animator.runtimeAnimatorController;
            while (controller is AnimatorOverrideController overrides) controller = overrides.runtimeAnimatorController;
            if (controller is AnimatorController authored)
                foreach (var layer in authored.layers)
                {
                    var found = FindState(layer.stateMachine, layer.name, state.fullPathHash);
                    if (!found) continue;
                    result.foundAuthoredState = true; result.footIK = found.iKOnFeet; result.writeDefaults = found.writeDefaultValues;
                    result.path = statePaths.TryGetValue(state.fullPathHash, out var path) ? path : found.name; break;
                }
            foreach (var item in next ? animator.GetNextAnimatorClipInfo(0) : animator.GetCurrentAnimatorClipInfo(0))
                if (item.clip) result.clips.Add(new Clip { name = item.clip.name, path = AssetDatabase.GetAssetPath(item.clip), weight = item.weight,
                    length = item.clip.length, humanMotion = item.clip.humanMotion, looping = item.clip.isLooping });
            return result;
        }
        static AnimatorState FindState(AnimatorStateMachine machine, string path, int hash)
        {
            foreach (var item in machine.states)
            {
                string full = path + "." + item.state.name;
                if (Animator.StringToHash(full) == hash) { statePaths[hash] = full; return item.state; }
            }
            foreach (var child in machine.stateMachines)
            { var found = FindState(child.stateMachine, path + "." + child.stateMachine.name, hash); if (found) return found; }
            return null;
        }
        static Bone ReadBone(Transform node, Transform model, string name) => new Bone { bone = name, path = Hierarchy(node),
            worldPosition = node.position, modelPosition = model.InverseTransformPoint(node.position), localPosition = node.localPosition,
            worldRotation = node.rotation, localRotation = node.localRotation, localScale = node.localScale, lossyScale = node.lossyScale,
            determinant = node.localToWorldMatrix.determinant };
        static void ReadGraph(Playable node, string path, Pose pose, HashSet<Playable> visited)
        {
            if (!node.IsValid() || !visited.Add(node)) return;
            if (pose.graphNodes.Count >= 32) { pose.graphNodesTruncated = true; return; }
            pose.graphNodes.Add(new GraphNode { path = path, type = node.GetPlayableType().FullName, time = node.GetTime(), speed = node.GetSpeed(), state = node.GetPlayState().ToString(), inputs = node.GetInputCount() });
            for (int i = 0; i < node.GetInputCount(); ++i) ReadGraph(node.GetInput(i), path + "/" + i, pose, visited);
        }
        static void BakeSkins(StationActorMotion motion, Pose pose)
        {
            foreach (var renderer in motion.GetComponentsInChildren<SkinnedMeshRenderer>(true).Take(8))
            {
                var skin = new Skin { path = Hierarchy(renderer.transform), meshPath = AssetDatabase.GetAssetPath(renderer.sharedMesh),
                    rootBone = renderer.rootBone ? Hierarchy(renderer.rootBone) : "", enabled = renderer.enabled, visible = renderer.isVisible,
                    updateWhenOffscreen = renderer.updateWhenOffscreen, lossyScale = renderer.transform.lossyScale,
                    rendererCenter = renderer.bounds.center, rendererSize = renderer.bounds.size };
                pose.skins.Add(skin);
                if (!renderer.sharedMesh || !renderer.gameObject.activeInHierarchy) continue;
                var mesh = new Mesh { name = "Pose probe transient bake", hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    renderer.BakeMesh(mesh, false); ++report.skinBakes;
                    var vertices = mesh.vertices; skin.vertices = vertices.Length;
                    skin.bakedWorldMin = Vector3.one * float.PositiveInfinity; skin.bakedWorldMax = Vector3.one * float.NegativeInfinity;
                    skin.leftSoleEstimateY = skin.rightSoleEstimateY = float.PositiveInfinity;
                    foreach (var vertex in vertices)
                    {
                        var point = renderer.transform.TransformPoint(vertex);
                        skin.bakedWorldMin = Vector3.Min(skin.bakedWorldMin, point); skin.bakedWorldMax = Vector3.Max(skin.bakedWorldMax, point);
                        if (NearFoot(point, pose.leftFoot)) skin.leftSoleEstimateY = Mathf.Min(skin.leftSoleEstimateY, point.y);
                        if (NearFoot(point, pose.rightFoot)) skin.rightSoleEstimateY = Mathf.Min(skin.rightSoleEstimateY, point.y);
                    }
                    skin.baked = vertices.Length > 0; skin.minimumWorldY = skin.baked ? skin.bakedWorldMin.y : 0;
                    if (float.IsInfinity(skin.leftSoleEstimateY)) skin.leftSoleEstimateY = pose.leftFoot.y;
                    if (float.IsInfinity(skin.rightSoleEstimateY)) skin.rightSoleEstimateY = pose.rightFoot.y;
                }
                finally { UnityEngine.Object.DestroyImmediate(mesh); }
            }
        }
        static bool NearFoot(Vector3 point, Vector3 foot) => point.y < foot.y + .05f && new Vector2(point.x - foot.x, point.z - foot.z).sqrMagnitude < .04f;
        static float KneeForward(Vector3 hip, Vector3 knee, Vector3 foot, Vector3 forward)
        { var axis = foot - hip; var onAxis = hip + axis * (Vector3.Dot(knee - hip, axis) / Mathf.Max(.000001f, axis.sqrMagnitude)); return Vector3.Dot(knee - onAxis, forward); }
        static float Extension(Vector3 hip, Vector3 knee, Vector3 foot) => Vector3.Distance(hip, foot) / Mathf.Max(.000001f, Vector3.Distance(hip, knee) + Vector3.Distance(knee, foot));
        static float ModelGap(Pose a, Pose b) => new[] { Vector3.Distance(a.leftKneeModel, b.leftKneeModel), Vector3.Distance(a.rightKneeModel, b.rightKneeModel),
            Vector3.Distance(a.leftFootModel, b.leftFootModel), Vector3.Distance(a.rightFootModel, b.rightFootModel) }.Max();
        static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        static string Hierarchy(Transform item) => item.parent ? Hierarchy(item.parent) + "/" + item.name : item.name;
        static void AddBounded<T>(List<T> values, T value, int maximum) { values.Add(value); if (values.Count > maximum) values.RemoveAt(0); }

        static void CaptureBoundary(string label)
        {
            var selected = dashboard ? dashboard.SelectedPassenger : null;
            if (!selected || !selected.motion || !world || report == null) return;
            var pose = ReadPose(selected.motion, true, true); pose.phase = label;
            AddBounded(report.phaseBoundaries, pose, 16);
        }
        static void CapturePauseBaseline()
        {
            pausedPrevious.Clear(); pausedLast.Clear();
            foreach (var motion in motions.Where(m => m && m.gameObject.activeInHierarchy && m.IsWalkPose).Take(ActorLimit))
            {
                var pose = ReadPose(motion, false, false);
                pausedPrevious[motion] = pose; pausedLast[motion] = pose;
            }
        }
        static string CaptureGameEvidence(string label)
        {
            string path = Path.GetFullPath(ReportDirectory + "/pose-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + label + ".png");
            ScreenCapture.CaptureScreenshot(path);
            return path;
        }
        static void CheckPausedControls(double now)
        {
            double duration = now - pausedRealtime;
            if (!report.pausedOrbitChecked && duration >= 3d)
            {
                var follow = dashboard.followCamera;
                Require(follow && follow.IsFollowing, "Saved repair lost the selected orbit camera.");
                float yaw = follow.CurrentYaw, distance = follow.RequestedDistance;
                follow.Orbit(new Vector2(80f, -20f)); follow.Zoom(-1f);
                report.pausedOrbitChecked = !Mathf.Approximately(yaw, follow.CurrentYaw) && !Mathf.Approximately(distance, follow.RequestedDistance);
                Require(report.pausedOrbitChecked, "Orbit or zoom did not respond during pause.");
            }
            if (!report.pausedDelayChecked && duration >= 6d)
            {
                string before = world.LastEvent; world.InjectDelay(); report.delayEventAfter = world.LastEvent;
                report.pausedDelayChecked = before != world.LastEvent;
                Require(report.pausedDelayChecked, "Paused delay control did not apply an event.");
            }
            if (!report.pausedCancellationChecked && duration >= 10d)
            {
                string before = world.LastEvent; world.InjectCancellation(); report.cancellationEventAfter = world.LastEvent;
                report.pausedCancellationChecked = before != world.LastEvent;
                Require(report.pausedCancellationChecked, "Paused cancellation control did not apply an event.");
            }
            report.pausedClockPreserved = Mathf.Abs(world.ElapsedSeconds - report.pausedAtSimulation) < .001f;
            Require(report.pausedClockPreserved, "Paused controls advanced the rail simulation clock.");
            if (duration >= 20d && string.IsNullOrEmpty(report.pausedScreenshot)) report.pausedScreenshot = CaptureGameEvidence("saved-repair-paused");
        }
        static void CollectPlayerErrors()
        {
            if (motions == null || report == null) return;
            foreach (var motion in motions.Where(m => m && m.animationPlayer && !string.IsNullOrEmpty(m.animationPlayer.LastError)))
            {
                string message = motion.name + ": " + motion.animationPlayer.LastError;
                if (!report.animationPlayerErrors.Contains(message)) report.animationPlayerErrors.Add(message);
            }
        }
        static void ApplyRuntimeVariant(string variant)
        {
            if (variantPrepared) return;
            variantActors = transientControllers = transientLibraries = 0;
            var players = Components<HumanAnimationPlayer>(SceneManager.GetActiveScene()).Where(p => p && p.gameObject.activeInHierarchy && p.TargetAnimator).ToArray();
            if (variant == "no-stabilization")
            {
                foreach (var player in players)
                {
                    var serialized = new SerializedObject(player);
                    serialized.FindProperty("stabilizeFeet").boolValue = false;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    player.TargetAnimator.stabilizeFeet = false; ++variantActors;
                }
            }
            else if (variant == "no-foot-ik")
            {
                var libraries = new Dictionary<HumanAnimationLibrary, HumanAnimationLibrary>();
                foreach (var player in players)
                {
                    // Replacing a live controller after motion playback has begun would introduce an unwanted reset.
                    Require(string.IsNullOrEmpty(player.CurrentMotionId), "No-foot-IK setup arrived after a motion request. Stop and rerun; no animation state is forced.");
                    var original = player.Library;
                    Require(original && original.controller is AnimatorController, "No-foot-IK requires the saved simple AnimatorController library.");
                    if (!libraries.TryGetValue(original, out var library))
                    {
                        library = UnityEngine.Object.Instantiate(original); library.name = original.name + " — transient no-foot-IK";
                        library.hideFlags = HideFlags.HideAndDontSave; transientObjects.Add(library);
                        library.controller = MakeControllerWithoutFootIK((AnimatorController)original.controller);
                        libraries.Add(original, library); ++transientLibraries;
                    }
                    player.Configure(player.TargetAnimator, library); ++variantActors;
                }
            }
            variantPrepared = true;
        }
        static AnimatorController MakeControllerWithoutFootIK(AnimatorController source)
        {
            var controller = new AnimatorController { name = source.name + " — transient no-foot-IK", hideFlags = HideFlags.HideAndDontSave };
            transientObjects.Add(controller); ++transientControllers;
            controller.parameters = source.parameters.Select(p => new AnimatorControllerParameter { name = p.name, type = p.type,
                defaultBool = p.defaultBool, defaultFloat = p.defaultFloat, defaultInt = p.defaultInt }).ToArray();
            var layers = new List<AnimatorControllerLayer>();
            foreach (var original in source.layers)
            {
                var saved = original.stateMachine;
                Require(original.syncedLayerIndex < 0 && saved.stateMachines.Length == 0 && saved.anyStateTransitions.Length == 0 &&
                    saved.entryTransitions.Length == 0 && saved.behaviours.Length == 0 &&
                    saved.states.All(s => s.state.transitions.Length == 0 && s.state.behaviours.Length == 0),
                    "No-foot-IK cloning intentionally supports only the saved library's independent named states.");
                var machine = new AnimatorStateMachine { name = saved.name, hideFlags = HideFlags.HideAndDontSave };
                transientObjects.Add(machine);
                var children = new List<ChildAnimatorState>();
                foreach (var child in saved.states)
                {
                    var s = child.state;
                    var copy = new AnimatorState { name = s.name, hideFlags = HideFlags.HideAndDontSave, motion = s.motion, speed = s.speed,
                        cycleOffset = s.cycleOffset, mirror = s.mirror, iKOnFeet = false, writeDefaultValues = s.writeDefaultValues,
                        speedParameter = s.speedParameter, speedParameterActive = s.speedParameterActive,
                        cycleOffsetParameter = s.cycleOffsetParameter, cycleOffsetParameterActive = s.cycleOffsetParameterActive,
                        mirrorParameter = s.mirrorParameter, mirrorParameterActive = s.mirrorParameterActive,
                        timeParameter = s.timeParameter, timeParameterActive = s.timeParameterActive, tag = s.tag };
                    transientObjects.Add(copy); children.Add(new ChildAnimatorState { state = copy, position = child.position });
                    if (saved.defaultState == s) machine.defaultState = copy;
                }
                machine.states = children.ToArray();
                layers.Add(new AnimatorControllerLayer { name = original.name, stateMachine = machine, avatarMask = original.avatarMask,
                    blendingMode = original.blendingMode, defaultWeight = original.defaultWeight, iKPass = original.iKPass,
                    syncedLayerIndex = -1 });
            }
            controller.layers = layers.ToArray(); return controller;
        }
        static void StopGraphs()
        {
            stoppedGraphs.Clear();
            foreach (var motion in motions)
            {
                if (!motion || !motion.animationPlayer || !motion.animationPlayer.TargetAnimator) continue;
                var graph = motion.animationPlayer.TargetAnimator.playableGraph;
                if (graph.IsValid() && graph.IsPlaying()) { graph.Stop(); stoppedGraphs.Add(graph); }
            }
        }
        static void ResumeGraphs()
        {
            foreach (var graph in stoppedGraphs) if (graph.IsValid()) graph.Play();
            stoppedGraphs.Clear();
        }

        static void Tick()
        {
            if (control == null) { string json = SessionState.GetString(ControlKey, ""); if (!string.IsNullOrEmpty(json)) control = JsonUtility.FromJson<Control>(json); }
            try
            {
                if (control != null)
                {
                    if (control.phase == "restoring" && !EditorApplication.isPlayingOrWillChangePlaymode) { Restore(); return; }
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                    Require(DateTime.UtcNow.Ticks < control.deadlineTicks, "Pose diagnostic timed out.");
                    if (control.phase == "abort-reload")
                    { if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false; else Restore(); return; }
                    if (control.phase == "initializing" && EditorApplication.isPlaying && Time.frameCount >= 15)
                    { Require(string.IsNullOrEmpty(control.startupError), control.startupError); Initialize(true, control.options); control.phase = "running"; SaveControl(); }
                }
                if (report == null || !report.active) return;
                Require(EditorApplication.isPlaying && world, "Pose probe lost its running world.");
                double now = EditorApplication.timeSinceStartup;
                if (requestCompletion) { Complete(report.errors.Count == 0 ? "requested severe stop" : "probe error", report.errors.Count == 0); return; }
                if (report.autonomous)
                {
                    if (report.options.exercisePausedControls && report.phase == "paused") CheckPausedControls(now);
                    if (report.options.exercisePausedControls && report.phase == "resumed" && world.ElapsedSeconds - report.resumedAtSimulation >= 6f && string.IsNullOrEmpty(report.resumedScreenshot))
                        report.resumedScreenshot = CaptureGameEvidence("saved-repair-resumed");
                    if (report.phase == "running-baseline" && world.ElapsedSeconds - report.startedSimulation >= report.options.runningSeconds)
                    {
                        CaptureBoundary("before-pause");
                        if (report.options.variant == "graph-stop-on-pause") StopGraphs();
                        world.TogglePause(); pausedRealtime = now; report.pausedAtSimulation = world.ElapsedSeconds; report.phase = "paused";
                        CapturePauseBaseline(); CaptureBoundary("pause-start");
                    }
                    else if (report.phase == "paused" && now - pausedRealtime >= report.options.pausedUnscaledSeconds)
                    {
                        report.pausedDuration = (float)(now - pausedRealtime); CaptureBoundary("pause-end"); ResumeGraphs();
                        world.TogglePause(); report.resumedAtSimulation = world.ElapsedSeconds; report.phase = "resumed"; CaptureBoundary("resume-start");
                    }
                    else if (report.phase == "resumed" && world.ElapsedSeconds - report.resumedAtSimulation >= report.options.resumedSeconds)
                    { Complete("all diagnostic phases observed", true); return; }
                }
                else if (now - beganRealtime >= 2d) { Complete("two-second observation complete", true); return; }
                if (now >= nextWrite) { nextWrite = now + 1d; WriteStatus(); }
            }
            catch (Exception error)
            {
                if (report != null) { report.errors.Add(error.ToString()); Complete("error", false); }
                else if (control != null)
                {
                    report = new Report { startedUtc = DateTime.UtcNow.ToString("O"), stoppedUtc = DateTime.UtcNow.ToString("O"), options = control.options,
                        phase = "startup-error", autonomous = true, stopReason = "Could not initialize pose diagnostic",
                        archivePath = ReportDirectory + "/pose-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + control.options.variant + ".json" };
                    report.errors.Add(error.ToString()); WriteStatus();
                    control.phase = "exiting"; SaveControl();
                    if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false; else Restore();
                    Debug.LogException(error);
                }
            }
        }
        static void Complete(string reason, bool completed)
        {
            if (report == null) return;
            CollectPlayerErrors();
            report.active = false; report.completed = completed; report.stopReason = reason; report.stoppedUtc = DateTime.UtcNow.ToString("O");
            CaptureBoundary("completion");
            if (world) { report.finishedSimulation = world.ElapsedSeconds; world.FlushLogs(); }
            RemoveHooks();
            report.originalsPreserved = Hash(SourceScene) == report.sourceSceneHash && Hash(SourceSchedule) == report.sourceScheduleHash;
            report.labPreserved = Hash(ScenePath) == report.labSceneHash;
            if (!report.originalsPreserved || !report.labPreserved) report.errors.Add("A saved scene or source schedule changed during observation.");
            WriteStatus();
            if (report.autonomous && control != null)
            {
                ResumeGraphs();
                if (world && world.Paused) world.TogglePause();
                control.phase = "exiting"; SaveControl(); EditorApplication.isPlaying = false;
            }
        }
        static void ModeChanged(PlayModeStateChange mode)
        {
            if (control == null) { string json = SessionState.GetString(ControlKey, ""); if (!string.IsNullOrEmpty(json)) control = JsonUtility.FromJson<Control>(json); }
            if (mode == PlayModeStateChange.EnteredPlayMode && control != null)
            {
                control.phase = "initializing";
                try { ApplyRuntimeVariant(control.options.variant); }
                catch (Exception error) { control.startupError = error.ToString(); }
                SaveControl();
            }
            if (mode == PlayModeStateChange.ExitingPlayMode && report != null && report.active)
            { report.errors.Add("Play ended before pose observation completed."); report.active = false; report.stoppedUtc = DateTime.UtcNow.ToString("O"); ResumeGraphs(); RemoveHooks(); WriteStatus(); }
            if (mode == PlayModeStateChange.EnteredEditMode && control != null) { control.phase = "restoring"; SaveControl(); }
        }
        static void BeforeReload()
        {
            if (report == null || !report.active) return;
            report.domainReloadObserved = true; report.errors.Add("Assembly reload interrupted live pose observation; samples are retained without mixing reset state.");
            report.active = false; report.stoppedUtc = DateTime.UtcNow.ToString("O"); ResumeGraphs(); RemoveHooks(); WriteStatus();
            if (control != null) { control.phase = "abort-reload"; SaveControl(); }
        }
        static void Restore()
        {
            if (control == null) return;
            Time.timeScale = control.oldTimeScale; Application.runInBackground = control.oldBackground;
            Application.targetFrameRate = control.oldFrameRate; QualitySettings.vSyncCount = control.oldVsync;
            EditorSettings.enterPlayModeOptionsEnabled = control.oldOptionsEnabled; EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)control.oldOptions;
            EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(control.originalPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(control.originalPlayScene);
            EditorApplication.isPaused = control.oldPaused; SessionState.EraseString(ControlKey); control = null;
            for (int i = transientObjects.Count - 1; i >= 0; --i) if (transientObjects[i]) UnityEngine.Object.DestroyImmediate(transientObjects[i]);
            transientObjects.Clear(); variantPrepared = false;
        }
        static void SaveControl() => SessionState.SetString(ControlKey, JsonUtility.ToJson(control));
        static bool Install(ref PlayerLoopSystem loop)
        {
            if (loop.type == typeof(UnityEngine.PlayerLoop.PreLateUpdate))
            {
                var children = (loop.subSystemList ?? Array.Empty<PlayerLoopSystem>()).ToList();
                children.Add(new PlayerLoopSystem { type = typeof(AfterAnimationPoseProbe), updateDelegate = AfterAnimation });
                loop.subSystemList = children.ToArray(); return true;
            }
            if (loop.subSystemList == null) return false;
            for (int i = 0; i < loop.subSystemList.Length; ++i)
            { var child = loop.subSystemList[i]; if (Install(ref child)) { loop.subSystemList[i] = child; return true; } }
            return false;
        }
        static void RemoveHooks() { var loop = PlayerLoop.GetCurrentPlayerLoop(); Remove(ref loop); PlayerLoop.SetPlayerLoop(loop); }
        static void Remove(ref PlayerLoopSystem loop)
        {
            if (loop.subSystemList == null) return;
            var children = new List<PlayerLoopSystem>();
            foreach (var original in loop.subSystemList)
            { if (original.type == typeof(AfterAnimationPoseProbe)) continue; var child = original; Remove(ref child); children.Add(child); }
            loop.subSystemList = children.ToArray();
        }
    }
}
#endif
