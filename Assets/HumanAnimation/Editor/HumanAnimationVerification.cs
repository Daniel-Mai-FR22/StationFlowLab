#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace StationFlow.HumanAnimation.Editor
{
    /// <summary>Real Play-mode retargeting checks, with domain reload and saved-scene restoration.</summary>
    [InitializeOnLoad]
    public static class HumanAnimationVerification
    {
        const string Key = "StationFlow.HumanAnimation.PlayCheck";
        const string ErrorKey = Key + ".Errors";
        const string ReportPath = "Reports/HumanAnimation/play-mode-check.json";
        const string StationPath = "Assets/TrainStation/Scenes/TrainStation.unity";
        [Serializable] sealed class SceneRecord { public string path; public bool isLoaded, isActive; }
        [Serializable] sealed class ClipResult
        {
            public string model, avatar, library, motion, clip;
            public float maximumBoneAngle, maximumVertexDisplacement, maximumMuscleDelta, rootDisplacement;
            public bool avatarPreserved, controllerStateReached, poseFinite, success;
        }
        [Serializable] sealed class SemanticResult
        {
            public string library, motion, weakestLimb;
            public float normalizedSampleTime, minimumDirectionDot;
            public bool success;
        }
        [Serializable] sealed class Run
        {
            public string startedUtc, completedUtc, unityVersion, phase, originalPlayScene;
            public bool success, abort, capturedSettings, originalOptionsEnabled, originalPaused, restoredScenes, restoredSettings;
            public bool stationIsolationPassed, playbackControlsPassed, invalidInputRejected;
            public int originalOptions, step, checkAfterFrame, baselineObjects, baselineCameras, baselineLights, baselineAnimationPlayers, mixamoClips, diagnosticClips, uiButtons;
            public long deadlineTicks;
            public SceneRecord[] scenes;
            public List<ClipResult> samples = new List<ClipResult>();
            public List<SemanticResult> semanticComparisons = new List<SemanticResult>();
            public List<string> errors = new List<string>();
        }
        static HumanAnimationVerification()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += ModeChanged;
            Application.logMessageReceived += Log;
        }
        [MenuItem("Efrei Research Lab/Human Animation/Check actual Play mode retargeting")]
        public static void BeginPlayCheck()
        {
            if (Load() != null) throw new InvalidOperationException("A human animation Play check is already running.");
            var run = new Run { startedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion, phase = "between" };
            try
            {
                Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating, "Finish Play/compilation/import before checking.");
                CleanScenes();
                var setup = EditorSceneManager.GetSceneManagerSetup();
                Require(setup.Length > 0 && setup.All(s => !string.IsNullOrEmpty(s.path)), "Save all open scenes first; unnamed scenes cannot be restored safely.");
                Require(File.Exists(HumanAnimationAuthoring.MixamoScenePath), "Create the saved MixamoAnimationLab with imported Mixamo clips first.");
                run.scenes = setup.Select(s => new SceneRecord { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray();
                run.originalPlayScene = AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene);
                run.originalOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
                run.originalOptions = (int)EditorSettings.enterPlayModeOptions;
                run.originalPaused = EditorApplication.isPaused;
                run.capturedSettings = true;
                SessionState.EraseString(ErrorKey);
                Save(run);
                EditorSceneManager.playModeStartScene = null;
                EditorSettings.enterPlayModeOptionsEnabled = false;
                EditorApplication.isPaused = false;
            }
            catch (Exception error) { run.errors.Add(error.Message); run.abort = true; Finish(run); }
        }
        static Run Load() { string json = SessionState.GetString(Key, ""); return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Run>(json); }
        static void Save(Run run) { run.deadlineTicks = DateTime.UtcNow.AddSeconds(180).Ticks; SessionState.SetString(Key, JsonUtility.ToJson(run)); }
        static void Log(string message, string trace, LogType type)
        {
            if (Load() == null || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            string prior = SessionState.GetString(ErrorKey, "");
            if (prior.Length < 12000) SessionState.SetString(ErrorKey, prior + message + "\n" + trace + "\n");
        }
        static void Tick()
        {
            var run = Load(); if (run == null) return;
            try
            {
                Require(DateTime.UtcNow.Ticks < run.deadlineTicks, "Play check timed out in " + run.phase);
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                if (run.phase == "between" && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    if (run.abort || run.step >= 2 || (run.step == 1 && !File.Exists(StationPath))) { Finish(run); return; }
                    CleanScenes();
                    var scene = EditorSceneManager.OpenScene(run.step == 0 ? HumanAnimationAuthoring.MixamoScenePath : StationPath, OpenSceneMode.Single);
                    run.baselineObjects = Components<Transform>(scene).Length;
                    run.baselineCameras = Components<Camera>(scene).Count(c => c.isActiveAndEnabled);
                    run.baselineLights = Components<Light>(scene).Count(c => c.isActiveAndEnabled);
                    run.baselineAnimationPlayers = Components<HumanAnimationPlayer>(scene).Length;
                    run.phase = "entering"; Save(run); EditorApplication.isPlaying = true;
                }
                else if (run.phase == "playing" && EditorApplication.isPlaying && Time.frameCount >= run.checkAfterFrame)
                {
                    if (run.step == 0) CheckLab(run); else CheckStation(run);
                    Require(string.IsNullOrEmpty(SessionState.GetString(ErrorKey, "")), "Unity logged an error during Play verification.");
                    run.step++; run.phase = "exiting"; Save(run); EditorApplication.isPlaying = false;
                }
                else if (run.phase == "exiting" && !EditorApplication.isPlayingOrWillChangePlaymode) { run.phase = "between"; Save(run); }
            }
            catch (Exception error)
            {
                run.errors.Add(error.ToString()); run.abort = true; run.phase = "exiting"; Save(run); Write(run);
                if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = false; else Finish(run);
            }
        }
        static void ModeChanged(PlayModeStateChange state)
        {
            var run = Load(); if (run == null) return;
            if (state == PlayModeStateChange.EnteredPlayMode) { run.phase = "playing"; run.checkAfterFrame = Time.frameCount + 8; }
            else if (state == PlayModeStateChange.ExitingPlayMode && run.phase != "exiting")
            { run.abort = true; run.errors.Add("Play was stopped before verification completed."); run.phase = "exiting"; }
            else if (state == PlayModeStateChange.EnteredEditMode) run.phase = "between";
            Save(run);
        }
        static void CheckLab(Run run)
        {
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == HumanAnimationAuthoring.MixamoScenePath, "Unexpected startup scene.");
            Require(Components<Transform>(scene).Length == run.baselineObjects, "Runtime created or removed lab objects.");
            var players = Components<HumanAnimationPlayer>(scene);
            Require(players.Length == 2, "Expected the saved XBot and Tall player instances.");
            Require(players[0].TargetAnimator.avatar != players[1].TargetAnimator.avatar, "Retarget targets must retain distinct Avatars.");
            var mixamo = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(HumanAnimationAuthoring.MixamoLibraryPath);
            Require(mixamo != null && mixamo.controller != null && mixamo.motions != null && mixamo.motions.Any(m => m != null && m.clip != null && m.source == HumanMotionSource.Mixamo), "An actual imported Mixamo library is required for delivery verification.");
            Require(players.All(p => p.Library == mixamo && p.TargetAnimator.runtimeAnimatorController == mixamo.controller), "Saved Mixamo lab players must already reference the shared Mixamo library/controller before verification configures anything.");
            var libraries = new[]
            {
                AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(HumanAnimationAuthoring.DiagnosticLibraryPath),
                mixamo
            };
            foreach (var library in libraries)
            {
                if (library == null || library.motions == null) continue;
                var available = library.motions.Where(m => m != null && m.clip != null).ToArray();
                if (available.Length == 0) continue;
                Require(library.Validate().Count == 0, "Invalid library: " + library.name);
                run.mixamoClips += available.Count(m => m.source == HumanMotionSource.Mixamo);
                run.diagnosticClips += available.Count(m => m.source == HumanMotionSource.Diagnostic);
                foreach (var player in players)
                {
                    Avatar avatar = player.TargetAnimator.avatar;
                    player.Configure(player.TargetAnimator, library);
                    Require(player.ValidateSetup(out var error), error);
                    foreach (var motion in available) CheckClip(run, player, motion, avatar);
                }
                foreach (var motion in available) CheckSemanticAgreement(run, players, motion);
            }
            Require(run.mixamoClips > 0 && run.samples.Count >= 4, "Actual Mixamo clips must be sampled on both Avatars.");
            Require(run.samples.Count(s => s.library == mixamo.name) == run.mixamoClips * 2, "Every available Mixamo clip must be verified on both independent Avatars.");
            CheckControls(run, players, scene);
        }
        static void CheckSemanticAgreement(Run run, HumanAnimationPlayer[] players, HumanMotion motion)
        {
            var result = new SemanticResult { library = players[0].Library.name, motion = motion.id, normalizedSampleTime = .37f, minimumDirectionDot = 1f };
            run.semanticComparisons.Add(result);
            foreach (var player in players)
            {
                Require(player.Play(motion.id), player.LastError);
                // Inspect the exact same normalized pose on each Avatar after separately testing API transitions.
                player.TargetAnimator.Play(Animator.StringToHash(motion.StatePath), 0, result.normalizedSampleTime);
                player.TargetAnimator.Update(0);
            }
            HumanBodyBones[] start = { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm,
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg };
            HumanBodyBones[] end = { HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot };
            for (int i = 0; i < start.Length; i++)
            {
                Vector3 Direction(Animator animator) => animator.transform.InverseTransformDirection(animator.GetBoneTransform(end[i]).position - animator.GetBoneTransform(start[i]).position).normalized;
                float dot = Vector3.Dot(Direction(players[0].TargetAnimator), Direction(players[1].TargetAnimator));
                if (dot < result.minimumDirectionDot) { result.minimumDirectionDot = dot; result.weakestLimb = start[i].ToString(); }
            }
            result.success = result.minimumDirectionDot > .5f;
            Require(result.success, "Semantic retargeting differs by over 60 degrees for " + result.weakestLimb + " / " + motion.id + ". A moving but anatomically reversed rig is not valid.");
        }
        static void CheckClip(Run run, HumanAnimationPlayer player, HumanMotion motion, Avatar avatar)
        {
            var animator = player.TargetAnimator;
            Vector3 root = player.transform.position, modelRoot = animator.transform.position;
            var result = new ClipResult { model = player.name, avatar = avatar.name, library = player.Library.name, motion = motion.id, clip = motion.clip.name };
            run.samples.Add(result);
            Require(player.Play(motion.id), player.LastError);
            // Complete the real CrossFade requested by the public player API before measuring.
            animator.Update(Mathf.Max(.02f, motion.blendIn + .025f));
            Require(!animator.IsInTransition(0), "Crossfade did not finish: " + motion.id);
            var bones = Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>().Where(b => b != HumanBodyBones.LastBone)
                .Select(animator.GetBoneTransform).Where(t => t != null).ToArray();
            Quaternion[] before = bones.Select(t => t.localRotation).ToArray();
            var skin = animator.GetComponentInChildren<SkinnedMeshRenderer>();
            Vector3[] vertices = BakedVertices(skin);
            using (var handler = new HumanPoseHandler(avatar, animator.transform))
            {
                var pose = new HumanPose(); handler.GetHumanPose(ref pose);
                float[] muscles = (float[])pose.muscles.Clone();
                animator.Update(Mathf.Clamp(motion.clip.length * .24f, .12f, .65f));
                handler.GetHumanPose(ref pose);
                result.poseFinite = pose.muscles.All(v => !float.IsNaN(v) && !float.IsInfinity(v));
                for (int i = 0; i < muscles.Length; i++) result.maximumMuscleDelta = Mathf.Max(result.maximumMuscleDelta, Mathf.Abs(muscles[i] - pose.muscles[i]));
            }
            for (int i = 0; i < bones.Length; i++) result.maximumBoneAngle = Mathf.Max(result.maximumBoneAngle, Quaternion.Angle(before[i], bones[i].localRotation));
            Vector3[] afterVertices = BakedVertices(skin);
            for (int i = 0; i < vertices.Length; i += 11) result.maximumVertexDisplacement = Mathf.Max(result.maximumVertexDisplacement, Vector3.Distance(vertices[i], afterVertices[i]));
            result.rootDisplacement = Mathf.Max(Vector3.Distance(root, player.transform.position), Vector3.Distance(modelRoot, animator.transform.position));
            result.avatarPreserved = animator.avatar == avatar;
            result.controllerStateReached = animator.GetCurrentAnimatorStateInfo(0).fullPathHash == Animator.StringToHash(motion.StatePath)
                && animator.GetCurrentAnimatorClipInfo(0).Any(info => info.clip == motion.clip);
            bool neutral = motion.id == "diagnostic_neutral";
            result.success = result.poseFinite && result.avatarPreserved && result.controllerStateReached && result.rootDisplacement < .0001f
                && (neutral || (result.maximumBoneAngle > .02f && result.maximumVertexDisplacement > .00001f && result.maximumMuscleDelta > .00001f));
            Require(result.success, "Retargeted pose/skin/root assertion failed for " + player.name + " / " + motion.id + ". See measured sample in report.");
        }
        static Vector3[] BakedVertices(SkinnedMeshRenderer renderer)
        {
            Require(renderer != null, "Diagnostic model has no saved skinned mesh.");
            var mesh = new Mesh();
            try
            {
                Require(renderer.sharedMesh != null && renderer.sharedMesh.vertices.All(FiniteVertex), "Saved skin contains non-finite vertices: " + renderer.name);
                renderer.BakeMesh(mesh);
                var vertices = mesh.vertices;
                Require(vertices.Length > 0 && vertices.All(FiniteVertex), "Baked skin contains non-finite vertices: " + renderer.name);
                return vertices;
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        static bool FiniteVertex(Vector3 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
            && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        static void CheckControls(Run run, HumanAnimationPlayer[] players, Scene scene)
        {
            foreach (var player in players)
            {
                var animator = player.TargetAnimator;
                player.Pause();
                float paused = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                animator.Update(.25f);
                Require(player.IsPaused && Mathf.Abs(paused - animator.GetCurrentAnimatorStateInfo(0).normalizedTime) < .0001f, "Pause did not hold the pose.");
                player.SetPlaybackSpeed(.5f); Require(!player.IsPaused && Mathf.Approximately(animator.speed, .5f), "Half speed failed.");
                player.SetPlaybackSpeed(2f); Require(Mathf.Approximately(animator.speed, 2f), "Double speed failed.");
                player.SetPlaybackSpeed(1f); player.Pause(); player.Rewind();
                Require(player.IsPaused && animator.GetCurrentAnimatorStateInfo(0).normalizedTime < .001f, "Rewind did not preserve pause at the start.");
                player.Resume(); Require(!player.IsPaused && animator.speed == 1f, "Resume failed.");
                string current = player.CurrentMotionId;
                Require(!player.Play("__missing_verification_motion__") && player.CurrentMotionId == current && !string.IsNullOrEmpty(player.LastError), "Unknown clip changed playback or was accepted.");
                Require(!player.Play(current, float.NaN), "Non-finite playback position was accepted.");
            }
            run.invalidInputRejected = true;
            var panel = Components<HumanAnimationPreviewPanel>(scene).Single();
            panel.players = players; panel.library = players[0].Library; panel.synchronizeAll = true;
            var buttons = Components<Button>(scene);
            run.uiButtons = buttons.Length;
            Require(buttons.Length >= 8 && buttons.All(b => b.onClick.GetPersistentEventCount() > 0), "Saved UI callbacks are missing.");
            var pauseButton = buttons.FirstOrDefault(b => b.onClick.GetPersistentMethodName(0) == "Pause");
            var resumeButton = buttons.FirstOrDefault(b => b.onClick.GetPersistentMethodName(0) == "Resume");
            Require(pauseButton != null && resumeButton != null, "Saved Pause/Resume buttons are missing.");
            pauseButton.onClick.Invoke(); Require(players.All(p => p.IsPaused), "Saved Pause callback did not reach both models.");
            resumeButton.onClick.Invoke(); Require(players.All(p => !p.IsPaused), "Saved Resume callback did not reach both models.");
            run.playbackControlsPassed = true;
        }
        static void CheckStation(Run run)
        {
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == StationPath, "Unexpected scene during station isolation check.");
            Require(Components<Transform>(scene).Length == run.baselineObjects, "Station objects changed during startup.");
            Require(Components<HumanAnimationPlayer>(scene).Length == run.baselineAnimationPlayers, "Animation library changed the station's saved player population.");
            Require(!Components<MonoBehaviour>(scene).Any(m => m != null && m.GetType().FullName == "StationFlow.SyntheticTraffic.DemoController"), "JunctionLab started inside TrainStation.");
            Require(Components<Camera>(scene).Count(c => c.isActiveAndEnabled) == run.baselineCameras, "Station camera activation changed.");
            Require(Components<Light>(scene).Count(c => c.isActiveAndEnabled) == run.baselineLights, "Station lighting activation changed.");
            run.stationIsolationPassed = true;
        }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
        static void CleanScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) Require(!SceneManager.GetSceneAt(i).isDirty, "Unsaved scene changes must be saved or reverted before checking. Nothing is saved/discarded automatically.");
        }
        static void Finish(Run run)
        {
            if (run.capturedSettings)
            {
                try
                {
                    EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(run.originalPlayScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(run.originalPlayScene);
                    EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)run.originalOptions;
                    EditorSettings.enterPlayModeOptionsEnabled = run.originalOptionsEnabled;
                    EditorApplication.isPaused = run.originalPaused; run.restoredSettings = true;
                    CleanScenes();
                    EditorSceneManager.RestoreSceneManagerSetup(run.scenes.Select(s => new SceneSetup { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray());
                    run.restoredScenes = true;
                }
                catch (Exception error) { run.errors.Add("Restoration failed: " + error); }
            }
            string logs = SessionState.GetString(ErrorKey, "");
            if (!string.IsNullOrEmpty(logs)) run.errors.Add("Unity logged errors:\n" + logs);
            run.success = !run.abort && run.errors.Count == 0 && run.restoredSettings && run.restoredScenes && run.playbackControlsPassed
                && run.invalidInputRejected && run.mixamoClips > 0 && run.samples.Count >= 4 && run.samples.All(s => s.success)
                && run.semanticComparisons.Count > 0 && run.semanticComparisons.All(s => s.success) && (!File.Exists(StationPath) || run.stationIsolationPassed);
            run.phase = "complete"; run.completedUtc = DateTime.UtcNow.ToString("O");
            SessionState.EraseString(Key); SessionState.EraseString(ErrorKey); Write(run);
            Debug.Log("HumanAnimation Play check " + (run.success ? "PASSED" : "FAILED") + "; " + run.mixamoClips + " Mixamo clips, " + run.samples.Count + " model/clip samples. See " + ReportPath);
        }
        static void Write(Run run) { Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath, JsonUtility.ToJson(run, true)); }

        [Serializable] sealed class PreviewSample
        {
            public string model, motion;
            public float maximumPreviewBoneAngle;
            public int restoredTransforms;
            public bool poseRestored, sceneClean, animatorReferencesPreserved;
        }
        [Serializable] sealed class PreviewReport
        {
            public string completedUtc, unityVersion;
            public bool success, sceneSetupRestored;
            public List<PreviewSample> samples = new List<PreviewSample>();
            public List<string> errors = new List<string>();
        }
        [MenuItem("Efrei Research Lab/Human Animation/Check Inspector preview and pose restoration")]
        public static void CheckEditorPreview()
        {
            var report = new PreviewReport { unityVersion = Application.unityVersion };
            SceneSetup[] prior = null;
            try
            {
                Require(Load() == null && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating, "Finish Play/import/other verification before checking the Inspector preview.");
                Require(!AnimationMode.InAnimationMode(), "Stop the existing Animation/Timeline preview first.");
                CleanScenes();
                var setup = EditorSceneManager.GetSceneManagerSetup();
                Require(setup.Length > 0 && setup.All(s => !string.IsNullOrEmpty(s.path)), "Save all open scenes before checking preview restoration.");
                Require(File.Exists(HumanAnimationAuthoring.MixamoScenePath), "Create the saved MixamoAnimationLab first.");
                prior = setup;
                var scene = EditorSceneManager.OpenScene(HumanAnimationAuthoring.MixamoScenePath, OpenSceneMode.Single);
                var library = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(HumanAnimationAuthoring.MixamoLibraryPath);
                var players = Components<HumanAnimationPlayer>(scene);
                Require(library != null && players.Length == 2 && players.All(p => p.Library == library), "Expected two saved players using the actual Mixamo library.");
                var available = library.motions.Where(m => m != null && m.clip != null && m.source == HumanMotionSource.Mixamo).ToArray();
                var motion = available.FirstOrDefault(m => m.id.IndexOf("walk", StringComparison.OrdinalIgnoreCase) >= 0) ?? available.FirstOrDefault();
                Require(motion != null, "No actual Mixamo clip is available for Inspector preview.");
                foreach (var player in players) CheckPreviewPlayer(report, player, motion, scene);
            }
            catch (Exception error) { report.errors.Add(error.ToString()); }
            finally
            {
                if (prior != null)
                    try { CleanScenes(); EditorSceneManager.RestoreSceneManagerSetup(prior); report.sceneSetupRestored = true; }
                    catch (Exception error) { report.errors.Add("Scene restoration failed; unsaved changes were preserved: " + error.Message); }
                report.completedUtc = DateTime.UtcNow.ToString("O");
                report.success = report.errors.Count == 0 && report.sceneSetupRestored && report.samples.Count == 2
                    && report.samples.All(s => s.poseRestored && s.sceneClean && s.animatorReferencesPreserved && s.maximumPreviewBoneAngle > .05f);
                Directory.CreateDirectory("Reports/HumanAnimation");
                File.WriteAllText("Reports/HumanAnimation/editor-preview-check.json", JsonUtility.ToJson(report, true));
            }
            Require(report.success, "Inspector preview regression failed; see Reports/HumanAnimation/editor-preview-check.json.");
            Debug.Log("HumanAnimation Inspector preview passed on both independent Avatars; sampled poses and scene setup restored without saving.");
        }
        static void CheckPreviewPlayer(PreviewReport report, HumanAnimationPlayer player, HumanMotion motion, Scene scene)
        {
            var sample = new PreviewSample { model = player.name, motion = motion.id }; report.samples.Add(sample);
            var transforms = player.GetComponentsInChildren<Transform>(true);
            Vector3[] positions = transforms.Select(t => t.localPosition).ToArray(), scales = transforms.Select(t => t.localScale).ToArray();
            Quaternion[] rotations = transforms.Select(t => t.localRotation).ToArray();
            var animator = player.TargetAnimator;
            Avatar avatar = animator.avatar; RuntimeAnimatorController controller = animator.runtimeAnimatorController;
            bool enabled = animator.enabled;
            var inspector = UnityEditor.Editor.CreateEditor(player, typeof(HumanAnimationPlayerEditor));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var sampleMethod = typeof(HumanAnimationPlayerEditor).GetMethod("Sample", flags);
            var stopMethod = typeof(HumanAnimationPlayerEditor).GetMethod("StopPreview", flags);
            var timeField = typeof(HumanAnimationPlayerEditor).GetField("normalizedTime", flags);
            try
            {
                Require(sampleMethod != null && stopMethod != null && timeField != null, "Inspector preview API changed; update the regression adapter.");
                timeField.SetValue(inspector, .37f);
                Require((bool)sampleMethod.Invoke(inspector, new object[] { motion }), "Inspector refused to preview the imported Humanoid clip.");
                for (int i = 0; i < transforms.Length; i++) sample.maximumPreviewBoneAngle = Mathf.Max(sample.maximumPreviewBoneAngle, Quaternion.Angle(rotations[i], transforms[i].localRotation));
                Require(sample.maximumPreviewBoneAngle > .05f, "Inspector scrubbing did not change any bone pose.");
                stopMethod.Invoke(inspector, null);
                Require(!AnimationMode.InAnimationMode(), "Inspector retained AnimationMode after StopPreview.");
                sample.poseRestored = true;
                for (int i = 0; i < transforms.Length; i++)
                {
                    bool restored = Vector3.Distance(positions[i], transforms[i].localPosition) < .00001f
                        && Vector3.Distance(scales[i], transforms[i].localScale) < .00001f && Quaternion.Angle(rotations[i], transforms[i].localRotation) < .03f;
                    if (restored) sample.restoredTransforms++; else sample.poseRestored = false;
                }
                sample.sceneClean = !scene.isDirty;
                sample.animatorReferencesPreserved = animator.avatar == avatar && animator.runtimeAnimatorController == controller && animator.enabled == enabled;
                Require(sample.poseRestored && sample.sceneClean && sample.animatorReferencesPreserved, "Inspector preview failed to restore transforms or changed scene/Animator references.");
            }
            finally
            {
                try { stopMethod?.Invoke(inspector, null); }
                finally { UnityEngine.Object.DestroyImmediate(inspector); }
            }
        }
    }
}
#endif
