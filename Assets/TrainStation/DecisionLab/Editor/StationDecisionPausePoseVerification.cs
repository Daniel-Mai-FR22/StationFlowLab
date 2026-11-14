#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Actual UI-phase pause/capture transitions; measures poses without writing them.</summary>
    [InitializeOnLoad]
    public static class StationDecisionPausePoseVerification
    {
        const string Key = "StationFlow.PausePoseVerification";
        const string ScenePath = "Assets/TrainStation/Scenes/TrainStationDaily.unity";
        const string ReportPath = "Reports/PausePose20261028/pause-pose-check.json";
        static readonly HumanBodyBones[] Bones = { HumanBodyBones.Hips, HumanBodyBones.Head, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes };
        static readonly string[] Hall = { "HALL-01", "HALL-02", "HALL-03", "HALL-04" };
        [Serializable] public sealed class Pose
        {
            public string agent, motion, culling, graphMode;
            public bool walking, graphValid, graphPlaying, initialized, finite = true;
            public int state, nextState;
            public float normalizedTime, speed, leftKneeForward, rightKneeForward, ankleAboveHip, ankleBelowRoot, humanScale;
            public Vector3 rootPosition; public Quaternion rootRotation;
            public Vector3[] local, world;
        }
        [Serializable] public sealed class Sample
        {
            public string tag; public int frame; public float deltaTime, timeScale, simulation;
            public bool paused, recording; public double realtime;
            public List<Pose> poses = new List<Pose>();
        }
        [Serializable] public sealed class Comparison
        {
            public string from, to; public int matched, changed, fromFrame, toFrame;
            public float maxLocalGap, maxWorldGap; public string worstAgent;
        }
        [Serializable] public sealed class FileEvidence { public string path, before, after; }
        [Serializable] public sealed class SavedScene { public string path; public bool active, loaded; }
        [Serializable] public sealed class Run
        {
            public string label, phase, startedUtc, completedUtc, archivePath, oldPlayScene, actionPhase;
            public bool success, initialPaused, filesPreserved, settingsRestored, sceneSetupRestored, oldOptionsEnabled, oldPaused, oldBackground;
            public int oldOptions, oldFps, oldVsync, initializeFrame, step, actionFrame;
            public float oldScale; public long deadlineTicks; public double phaseStarted;
            public string pendingBoundary, pausedScreenshot; public bool firstRenderTaken, lateRenderTaken;
            public string method = "Actions use actionPhase: actual OnGUI Repaint after animation, or early Update before animation; other UI components are transiently disabled to avoid artificial Layout/Repaint state mismatches; sample immediately before/after actions and subsequent endFrameRendering. Pose diagnostics do not mutate transforms. Capture OFF/ON means skeleton toggle; both cases use four Hall cameras at480p. Drift is reported without aborting baseline.";
            public List<Sample> samples = new List<Sample>(); public List<Comparison> comparisons = new List<Comparison>();
            public List<string> captures = new List<string>(), errors = new List<string>();
            public List<FileEvidence> files = new List<FileEvidence>(); public List<SavedScene> scenes = new List<SavedScene>();
        }
        static Run run; static StationDecisionWorld world; static StationDecisionPausePoseTestDriver driver;
        static int guiFrame = -1, renderFrame = -1; static double nextPeriodic;
        static StationDecisionPausePoseVerification()
        {
            EditorApplication.update += Tick; EditorApplication.playModeStateChanged += ModeChanged;
            RenderPipelineManager.endFrameRendering += AfterRender; Application.logMessageReceived += OnLog;
        }
        public static void Begin(string label = "baseline")
        {
            Require(string.IsNullOrEmpty(SessionState.GetString(Key, "")), "Pause pose verification already pending.");
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling, "Stop Play before pause pose verification.");
            for (int i = 0; i < SceneManager.sceneCount; ++i) Require(!SceneManager.GetSceneAt(i).isDirty, "Save dirty scenes before pause pose verification.");
            run = new Run { label = label, actionPhase = label.Contains("early") ? "early-Update-before-animation" : "OnGUI-Repaint-after-animation", phase = "entering", startedUtc = DateTime.UtcNow.ToString("O"), deadlineTicks = DateTime.UtcNow.AddMinutes(3).Ticks,
                archivePath = "Reports/PausePose20261028/" + Safe(label) + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json",
                oldPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene), oldOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled,
                oldOptions = (int)EditorSettings.enterPlayModeOptions, oldPaused = EditorApplication.isPaused, oldBackground = Application.runInBackground,
                oldFps = Application.targetFrameRate, oldVsync = QualitySettings.vSyncCount, oldScale = Time.timeScale,
                scenes = EditorSceneManager.GetSceneManagerSetup().Select(s => new SavedScene { path = s.path, loaded = s.isLoaded, active = s.isActive }).ToList() };
            foreach (string p in new[] { ScenePath, "ProjectSettings/PlayerSettings.asset", "ProjectSettings/ProjectSettings.asset", "ProjectSettings/QualitySettings.asset", "Assets/TrainStation/DecisionLab/Resources/StationDecisionDayScenario.asset" }.Where(File.Exists))
                run.files.Add(new FileEvidence { path = p, before = Hash(p) });
            Save(); Write(); EditorSettings.enterPlayModeOptionsEnabled = false;
            EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            EditorApplication.isPaused = false; EditorApplication.isPlaying = true;
        }
        static void GuiTick()
        {
            if (guiFrame == Time.frameCount || run == null || run.phase != "running" || !world) return;
            guiFrame = Time.frameCount;
            try { Drive(); } catch (Exception e) { Fail(e.ToString()); Finish(); }
        }
        static void Initialize()
        {
            Require(SceneManager.GetActiveScene().path == ScenePath, "Unexpected Play scene.");
            world = UnityEngine.Object.FindObjectsByType<StationDecisionWorld>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single(w => w.gameObject.scene.path == ScenePath);
            Require(world.rail && world.rail.IsReady && world.CaptureSession && world.Paused, "Daily must initialize paused.");
            run.initialPaused = true; Application.runInBackground = true; QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
            foreach (var panel in UnityEngine.Object.FindObjectsByType<StationDecisionCollectionPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (panel.gameObject.scene == world.gameObject.scene) panel.enabled = false;
            foreach (var dashboard in UnityEngine.Object.FindObjectsByType<StationDecisionDashboard>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (dashboard.gameObject.scene == world.gameObject.scene) dashboard.enabled = false;
            driver = new GameObject("Pause pose verification (transient)").AddComponent<StationDecisionPausePoseTestDriver>(); driver.early = run.label.Contains("early"); driver.repaint = GuiTick;
            run.phase = "running"; run.phaseStarted = Time.realtimeSinceStartupAsDouble; run.step = 0; nextPeriodic = 0; guiFrame = renderFrame = -1;
            Save();
        }
        static void Drive()
        {
            double now = Time.realtimeSinceStartupAsDouble, age = now - run.phaseStarted;
            switch (run.step)
            {
                case 0: Action("initial-resume", () => world.BeginObservation()); Advance(); break;
                case 1:
                    if (age < 6) return;
                    Require(world.ActivePassengerCount > 0, "No active passengers available for pause probe.");
                    Action("off-pause", () => world.TogglePause()); Advance(); break;
                case 2: if (age < 2) return; Action("off-start", () => StartCapture(false)); Advance(); break;
                case 3: if (age < 4) return; Action("off-stop", () => world.StopDataCollection("pause_pose_probe_off")); Advance(); break;
                case 4: if (age < 1.5 || world.CaptureSession.IsFinalizing) return; Action("off-resume", () => world.BeginObservation()); Advance(); break;
                case 5: if (age < 3) return; Action("on-pause", () => world.TogglePause()); Advance(); break;
                case 6: if (age < 2) return; Action("on-start", () => StartCapture(true)); Advance(); break;
                case 7: if (age < 4) return; Action("on-recording-pause", () => world.TogglePause()); Advance(); break;
                case 8: if (age < 2) return; Action("on-stop-while-paused", () => world.StopDataCollection("pause_pose_probe_on")); Advance(); break;
                case 9: if (age < 1.5 || world.CaptureSession.IsFinalizing) return; Action("on-resume", () => world.BeginObservation()); Advance(); break;
                case 10: if (age < 3) return; Take("finished-running"); Finish(); break;
            }
        }
        static void StartCapture(bool skeletons)
        {
            var options = new StationDecisionCaptureOptions { video = true, timetable = false, profiles = false, trajectories = false, animations = false,
                skeletons = skeletons, decisions = false, events = false, videoWidth = 854, videoHeight = 480, selectedCameraIds = (string[])Hall.Clone() };
            Require(world.BeginDataCollection(options), "Capture start: " + world.CollectionError);
            run.captures.Add(world.OutputDirectory);
        }
        static void Action(string tag, System.Action perform)
        {
            Sample before = Take(tag + "-before"); perform(); Sample after = Take(tag + "-immediate"); Compare(before, after);
            run.pendingBoundary = tag; run.actionFrame = Time.frameCount; run.firstRenderTaken = run.lateRenderTaken = false;
            Save(); Write();
        }
        static void Advance() { ++run.step; run.phaseStarted = Time.realtimeSinceStartupAsDouble; Save(); }
        static void AfterRender(ScriptableRenderContext context, Camera[] cameras)
        {
            if (run == null || run.phase != "running" || !world || renderFrame == Time.frameCount) return;
            renderFrame = Time.frameCount;
            try
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (!string.IsNullOrEmpty(run.pendingBoundary) && Time.frameCount > run.actionFrame)
                {
                    string tag = run.pendingBoundary;
                    if (!run.firstRenderTaken)
                    {
                        Sample sample = Take(tag + "-first-render"); Compare(run.samples.Last(s => s.tag == tag + "-before"), sample);
                        run.firstRenderTaken = true;
                    }
                    if (!run.lateRenderTaken && now - run.phaseStarted > .7)
                    {
                        Sample sample = Take(tag + "-later-render"); Compare(run.samples.Last(s => s.tag == tag + "-before"), sample);
                        run.lateRenderTaken = true; if (tag == "on-recording-pause") { run.pausedScreenshot = "Reports/PausePose20261028/" + Safe(run.label) + "-recording-paused.png"; ScreenCapture.CaptureScreenshot(run.pausedScreenshot); } Save(); Write();
                    }
                }
                if (now >= nextPeriodic) { nextPeriodic = now + 1; Take("periodic-step-" + run.step); }
            }
            catch (Exception e) { Fail(e.ToString()); Finish(); }
        }
        static Sample Take(string tag)
        {
            var sample = new Sample { tag = tag, frame = Time.frameCount, deltaTime = Time.deltaTime, timeScale = Time.timeScale,
                simulation = world.ElapsedSeconds, paused = world.Paused, recording = world.IsCollecting, realtime = Time.realtimeSinceStartupAsDouble };
            foreach (var p in world.participants.Where(p => p && p.HasEntered && !p.Finished && p.gameObject.activeInHierarchy))
            {
                var motion = p.motion; var player = motion ? motion.animationPlayer : null; var a = player ? player.TargetAnimator : null;
                if (!a || !a.avatar || !a.isHuman) continue;
                var graph = a.playableGraph; var pose = new Pose { agent = p.JourneyId, motion = player.CurrentMotionId, walking = motion.LocomotionActive && motion.IsWalkPose,
                    culling = a.cullingMode.ToString(), initialized = a.isInitialized, speed = a.speed, humanScale = a.humanScale, graphValid = graph.IsValid(),
                    rootPosition = a.transform.position, rootRotation = a.transform.rotation, local = new Vector3[Bones.Length], world = new Vector3[Bones.Length] };
                if (graph.IsValid()) { pose.graphPlaying = graph.IsPlaying(); pose.graphMode = graph.GetTimeUpdateMode().ToString(); }
                if (a.isInitialized && a.layerCount > 0) { var s = a.GetCurrentAnimatorStateInfo(0); pose.state = s.fullPathHash; pose.normalizedTime = s.normalizedTime; pose.nextState = a.IsInTransition(0) ? a.GetNextAnimatorStateInfo(0).fullPathHash : 0; }
                for (int i = 0; i < Bones.Length; ++i) { var bone = a.GetBoneTransform(Bones[i]); if (!bone) { pose.finite = false; continue; } pose.world[i] = bone.position; pose.local[i] = a.transform.InverseTransformPoint(bone.position); if (!Finite(pose.local[i])) pose.finite = false; }
                pose.leftKneeForward = Knee(pose.local[2], pose.local[3], pose.local[4]); pose.rightKneeForward = Knee(pose.local[5], pose.local[6], pose.local[7]);
                pose.ankleAboveHip = Mathf.Max(pose.local[4].y, pose.local[7].y) - pose.local[0].y; pose.ankleBelowRoot = Mathf.Min(pose.local[4].y, pose.local[7].y);
                sample.poses.Add(pose);
            }
            run.samples.Add(sample); return sample;
        }
        static void Compare(Sample a, Sample b)
        {
            var lookup = a.poses.ToDictionary(p => p.agent); var result = new Comparison { from = a.tag, to = b.tag, fromFrame = a.frame, toFrame = b.frame };
            foreach (var p in b.poses) if (lookup.TryGetValue(p.agent, out var old))
            {
                ++result.matched; float local = 0, worldGap = 0;
                for (int i = 0; i < p.local.Length; ++i) { local = Mathf.Max(local, Vector3.Distance(old.local[i], p.local[i])); worldGap = Mathf.Max(worldGap, Vector3.Distance(old.world[i], p.world[i])); }
                if (local > .0001f) ++result.changed;
                if (local > result.maxLocalGap) { result.maxLocalGap = local; result.worstAgent = p.agent; }
                result.maxWorldGap = Mathf.Max(result.maxWorldGap, worldGap);
            }
            run.comparisons.Add(result);
        }
        static float Knee(Vector3 hip, Vector3 knee, Vector3 foot) { Vector3 axis = foot - hip; return (knee - hip - axis * (Vector3.Dot(knee - hip, axis) / Mathf.Max(.000001f, axis.sqrMagnitude))).z; }
        static bool Finite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        static void Tick()
        {
            Load(); if (run == null) return;
            try
            {
                if (DateTime.UtcNow.Ticks > run.deadlineTicks && run.phase != "restore" && run.phase != "exiting") { Fail("Probe deadline exceeded (OnGUI or rendering may not be progressing)."); Finish(); }
                if (run.phase == "initialize" && EditorApplication.isPlaying && Time.frameCount >= run.initializeFrame) Initialize();
                if (run.phase == "restore" && !EditorApplication.isPlayingOrWillChangePlaymode) Restore();
            }
            catch (Exception e) { Fail(e.ToString()); if (EditorApplication.isPlaying) Finish(); else { run.phase = "restore"; Save(); } }
        }
        static void Finish()
        {
            if (run == null || run.phase == "exiting") return;
            if (world && world.IsCollecting) { try { world.StopDataCollection("pause_pose_probe_cleanup"); } catch (Exception e) { Fail(e.ToString()); } }
            run.phase = "exiting"; Save(); Write(); EditorApplication.isPlaying = false;
        }
        static void ModeChanged(PlayModeStateChange state)
        {
            Load(); if (run == null) return;
            if (state == PlayModeStateChange.EnteredPlayMode) { run.phase = "initialize"; run.initializeFrame = Time.frameCount + 8; Save(); }
            if (state == PlayModeStateChange.ExitingPlayMode) { if (run.phase != "exiting") Fail("Play interrupted before probe completed."); run.phase = "exiting"; Save(); }
            if (state == PlayModeStateChange.EnteredEditMode) { run.phase = "restore"; world = null; driver = null; Save(); }
        }
        static void Restore()
        {
            EditorSettings.enterPlayModeOptionsEnabled = run.oldOptionsEnabled; EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)run.oldOptions;
            EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(run.oldPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(run.oldPlayScene);
            EditorApplication.isPaused = run.oldPaused; Application.runInBackground = run.oldBackground; Application.targetFrameRate = run.oldFps; QualitySettings.vSyncCount = run.oldVsync; Time.timeScale = run.oldScale;
            run.settingsRestored = EditorSettings.enterPlayModeOptionsEnabled == run.oldOptionsEnabled && (int)EditorSettings.enterPlayModeOptions == run.oldOptions && EditorApplication.isPaused == run.oldPaused && Application.targetFrameRate == run.oldFps && QualitySettings.vSyncCount == run.oldVsync && Time.timeScale == run.oldScale;
            var scenes = EditorSceneManager.GetSceneManagerSetup(); run.sceneSetupRestored = scenes.Length == run.scenes.Count && scenes.Select((s, i) => s.path == run.scenes[i].path && s.isActive == run.scenes[i].active && s.isLoaded == run.scenes[i].loaded).All(x => x);
            run.filesPreserved = true; foreach (var f in run.files) { f.after = File.Exists(f.path) ? Hash(f.path) : "missing"; if (f.before != f.after) { run.filesPreserved = false; Fail("Saved file changed: " + f.path); } }
            if (!run.settingsRestored || !run.sceneSetupRestored) Fail("Editor settings or scene setup were not restored.");
            if (!run.initialPaused || run.captures.Count != 2 || run.step != 10) Fail("Not all pause/capture phases completed.");
            run.phase = "complete"; run.completedUtc = DateTime.UtcNow.ToString("O"); run.success = run.errors.Count == 0; Write(); SessionState.EraseString(Key); run = null;
        }
        static void OnLog(string message, string stack, LogType type) { if (run != null && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)) Fail(message); }
        static string Safe(string text) => new string(text.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        static string Hash(string path) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Fail(string message) { if (run != null && run.errors.Count < 30 && !run.errors.Contains(message)) run.errors.Add(message); }
        static void Save() { if (run != null) SessionState.SetString(Key, JsonUtility.ToJson(run)); }
        static void Load() { if (run == null) { string json = SessionState.GetString(Key, ""); if (!string.IsNullOrEmpty(json)) run = JsonUtility.FromJson<Run>(json); } }
        static void Write() { if (run == null) return; Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); string json = JsonUtility.ToJson(run, true); File.WriteAllText(ReportPath, json, new UTF8Encoding(false)); File.WriteAllText(run.archivePath, json, new UTF8Encoding(false)); }
    }
}
#endif