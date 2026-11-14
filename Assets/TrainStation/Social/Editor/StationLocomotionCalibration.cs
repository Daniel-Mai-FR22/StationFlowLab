#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.HumanAnimation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Measures the original Humanoid body trajectory in metres, without changing any imported Mixamo asset.</summary>
    public static class StationLocomotionCalibration
    {
        public const string ReportPath = "Reports/TrainStationSocial/locomotion-calibration.json";
        const string ModelPath = "Assets/HumanAnimation/Source/Mixamo/Models/XBot.fbx";
        const string WalkId = StationActorMotion.DefaultWalkId;
        const string SpeedProperty = "walkReferenceSpeed";
        const string ScaleProperty = "walkReferenceHumanScale";
        static readonly string[] PrefabPaths = { StationSocialAuthoring.PassengerPrefabPath, StationSocialAuthoring.CleanerPrefabPath };

        [Serializable] public sealed class PoseSample
        {
            public string evaluation;
            public float normalizedTime;
            public Vector3 modelPosition, hipsRelativeToModel, leftFootRelativeToModel, rightFootRelativeToModel;
        }
        [Serializable] public sealed class CalibrationReport
        {
            public bool success, applied, poseDriftDetected;
            public string measuredUtc, method, error, sourceModel = ModelPath, sourceFbx, extractedClip, motionId = WalkId;
            public string algorithm = "Original Humanoid Body RootT XZ end minus start, multiplied by source Animator.humanScale, divided by exact clip duration. RootT is normalized by human scale. Compare source FBX curves against extracted clip; do not infer speed from NavMesh settings.";
            public string scaleReference = "https://docs.unity.com/en-us/engine/6000.5/script-reference/unityengine/humanposehandler/gethumanpose";
            public float duration, humanScale, normalizedStrideDistance, strideMetres, referenceSpeed, sourceExtractedDifference;
            public float maximumPreviewHipExcursion, maximumAnimatorHipExcursion, maximumModelExcursion;
            public Vector3 sourceRootStart, sourceRootEnd;
            public bool sourceBakesRootXZ;
            public int calibratedPrefabs, inheritedSceneActors, preservedOverrides;
            public List<PoseSample> samples = new List<PoseSample>();
            public List<string> backups = new List<string>(), preserved = new List<string>();
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Measure walking stride")]
        public static void Measure()
        {
            RequireEditor();
            CalibrationReport report = null;
            try { report = MeasureCore(); report.success = true; }
            finally { if (report != null) Write(report); }
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Calibrate saved walking speed")]
        public static void Calibrate()
        {
            RequireEditor();
            RequireCleanScenes();
            CalibrationReport report = null;
            try
            {
                report = MeasureCore();
                Write(report); // Keep diagnostic evidence even if pose validation prevents an asset change.
                if (report.poseDriftDetected)
                    throw new InvalidOperationException("The walking pose drifts relative to its model root. Correct the social locomotion clip before applying speed calibration; inspect " + ReportPath);
                string backupDirectory = "BuildRequests/Backups/Locomotion-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                Directory.CreateDirectory(backupDirectory);
                foreach (string path in PrefabPaths) Backup(path, backupDirectory, report);
                Backup(StationSocialAuthoring.ScenePath, backupDirectory, report);
                Scene station = SceneManager.GetSceneByPath(StationSocialAuthoring.ScenePath);
                Scene previouslyActive = SceneManager.GetActiveScene();
                bool opened = !station.IsValid() || !station.isLoaded;
                if (opened) station = EditorSceneManager.OpenScene(StationSocialAuthoring.ScenePath, OpenSceneMode.Additive);
                try
                {
                    // Snapshot field overrides before prefab propagation. Manual per-instance stride corrections remain untouched.
                    var actors = station.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<StationActorMotion>(true))
                        .Where(m => m.GetComponent<StationPassenger>() || m.GetComponent<StationCleaner>()).ToArray();
                    var overrides = actors.Select(m => new ActorOverride(m)).ToArray();
                    foreach (string path in PrefabPaths) CalibratePrefab(path, report);
                    foreach (var saved in overrides)
                    {
                        saved.AssertPreserved();
                        if (saved.customSpeed || saved.customScale)
                        { report.preservedOverrides++; report.preserved.Add(saved.motion.name + ": existing per-instance calibration override retained"); }
                        else
                        {
                            var so = new SerializedObject(saved.motion);
                            var sourceMotion = PrefabUtility.GetCorrespondingObjectFromSource(saved.motion);
                            if (!sourceMotion) { report.preserved.Add(saved.motion.name + ": independent authored actor retained"); continue; }
                            var sourceProperties = new SerializedObject(sourceMotion);
                            RequireNear(Find(so, SpeedProperty).floatValue, Find(sourceProperties, SpeedProperty).floatValue, "Prefab speed did not propagate to " + saved.motion.name);
                            RequireNear(Find(so, ScaleProperty).floatValue, Find(sourceProperties, ScaleProperty).floatValue, "Prefab human scale did not propagate to " + saved.motion.name);
                            if (Mathf.Abs(Find(so, SpeedProperty).floatValue - report.referenceSpeed) < .00001f) report.inheritedSceneActors++;
                            else report.preserved.Add(saved.motion.name + ": inherited custom prefab calibration retained");
                        }
                    }
                    // Serialize newly introduced inherited fields without recording unnecessary per-instance overrides.
                    if (!EditorSceneManager.SaveScene(station)) throw new IOException("Could not save TrainStation calibration.");
                    report.applied = true; report.success = true;
                }
                finally
                {
                    if (opened && station.IsValid()) EditorSceneManager.CloseScene(station, true);
                    if (previouslyActive.IsValid() && previouslyActive.isLoaded) SceneManager.SetActiveScene(previouslyActive);
                }
            }
            catch (Exception error)
            {
                if (report == null) report = new CalibrationReport();
                report.error = error.ToString(); report.success = false;
                throw;
            }
            finally { if (report != null) Write(report); }
        }

        /// <summary>Read-only measurement for a candidate gait; no calibration or asset writes are performed.</summary>
        public static CalibrationReport MeasureMotion(string motionId, string libraryPath)
        {
            RequireEditor();
            var report = MeasureCore(motionId, libraryPath);
            report.success = true;
            return report;
        }

        static CalibrationReport MeasureCore(string motionId = WalkId, string libraryPath = StationSocialAnimationAssets.LibraryPath)
        {
            var library = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(libraryPath);
            var motion = library ? library.Find(motionId) : null;
            if (motion == null || !motion.clip) throw new InvalidOperationException("Saved social walking clip is missing.");
            var sourceClip = AssetDatabase.LoadAllAssetsAtPath(motion.sourceAssetPath).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal) && c.humanMotion);
            if (!sourceClip) throw new InvalidOperationException("Original imported Mixamo walking take is missing.");
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (!source) throw new InvalidOperationException("Source XBot model is missing.");
            var report = new CalibrationReport { measuredUtc = DateTime.UtcNow.ToString("O"), sourceFbx = motion.sourceAssetPath, motionId = motionId,
                extractedClip = AssetDatabase.GetAssetPath(motion.clip), duration = sourceClip.length,
                method = "Original Mixamo Humanoid Body RootT trajectory / duration, calibrated to XBot human scale" };
            if (report.duration < .1f) throw new InvalidOperationException("Walking source duration is invalid.");
            report.sourceRootStart = RootAt(sourceClip, 0);
            report.sourceRootEnd = RootAt(sourceClip, sourceClip.length);
            var sourceTravel = Flat(report.sourceRootEnd - report.sourceRootStart);
            var extractedTravel = Flat(RootAt(motion.clip, motion.clip.length) - RootAt(motion.clip, 0));
            report.sourceExtractedDifference = (sourceTravel - extractedTravel).magnitude;
            report.normalizedStrideDistance = sourceTravel.magnitude;
            // A cyclic in-place source with no retained original translation requires a different stance-foot estimator.
            // This particular source retains its full measured forward trajectory, so no guessed fallback is used.
            if (report.normalizedStrideDistance < .15f || !Finite(report.normalizedStrideDistance))
                throw new InvalidOperationException("Original source has no meaningful root trajectory; a stance-foot calibration is required for this different clip.");
            var importer = AssetImporter.GetAtPath(motion.sourceAssetPath) as ModelImporter;
            report.sourceBakesRootXZ = importer && importer.clipAnimations.Any(c => c.name == sourceClip.name && c.lockRootPositionXZ);
            Scene preview = EditorSceneManager.NewPreviewScene();
            GameObject instance = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(source, preview);
                instance.hideFlags = HideFlags.HideAndDontSave;
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;
                var animator = instance.GetComponentInChildren<Animator>();
                if (!animator || !animator.avatar || !animator.avatar.isHuman || !animator.avatar.isValid)
                    throw new InvalidOperationException("Source XBot does not have a valid Humanoid Avatar.");
                report.humanScale = animator.humanScale;
                if (!Finite(report.humanScale) || report.humanScale < .1f) throw new InvalidOperationException("XBot human scale is invalid.");
                report.strideMetres = report.normalizedStrideDistance * report.humanScale;
                report.referenceSpeed = report.strideMetres / report.duration;
                if (!Finite(report.referenceSpeed) || report.referenceSpeed < .3f || report.referenceSpeed > 3.5f)
                    throw new InvalidOperationException("Measured walking speed is outside credible walking bounds.");
                SamplePreview(animator, motion.clip, report);
                animator.runtimeAnimatorController = library.controller;
                animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind(); animator.Update(0);
                foreach (float phase in new[] { 0f, .25f, .5f, .75f, .95f, .999f })
                {
                    animator.Play(Animator.StringToHash(motion.StatePath), 0, phase);
                    animator.Update(0);
                    report.samples.Add(Capture(animator, phase, "Animator.Play + Update(0), applyRootMotion=false"));
                }
                report.maximumPreviewHipExcursion = Excursion(report.samples.Where(s => s.evaluation == "AnimationMode"));
                report.maximumAnimatorHipExcursion = Excursion(report.samples.Where(s => s.evaluation != "AnimationMode"));
                report.maximumModelExcursion = report.samples.Max(s => Flat(s.modelPosition).magnitude);
                report.poseDriftDetected = report.maximumAnimatorHipExcursion > .35f;
            }
            finally
            {
                if (instance) UnityEngine.Object.DestroyImmediate(instance);
                EditorSceneManager.ClosePreviewScene(preview);
            }
            return report;
        }

        static void SamplePreview(Animator animator, AnimationClip clip, CalibrationReport report)
        {
            var driver = ScriptableObject.CreateInstance<AnimationModeDriver>();
            driver.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                AnimationMode.StartAnimationMode(driver);
                foreach (float phase in new[] { 0f, .25f, .5f, .75f, .95f, .999f })
                {
                    AnimationMode.BeginSampling();
                    try { AnimationMode.SampleAnimationClip(animator.gameObject, clip, phase * clip.length); }
                    finally { AnimationMode.EndSampling(); }
                    report.samples.Add(Capture(animator, phase, "AnimationMode"));
                }
            }
            finally
            {
                if (AnimationMode.InAnimationMode(driver)) AnimationMode.StopAnimationMode(driver);
                UnityEngine.Object.DestroyImmediate(driver);
            }
        }

        static PoseSample Capture(Animator animator, float phase, string evaluation)
        {
            Vector3 Local(HumanBodyBones bone) => animator.transform.InverseTransformPoint(animator.GetBoneTransform(bone).position);
            return new PoseSample { evaluation = evaluation, normalizedTime = phase, modelPosition = animator.transform.position,
                hipsRelativeToModel = Local(HumanBodyBones.Hips), leftFootRelativeToModel = Local(HumanBodyBones.LeftFoot), rightFootRelativeToModel = Local(HumanBodyBones.RightFoot) };
        }
        static float Excursion(IEnumerable<PoseSample> samples)
        {
            var all = samples.ToArray(); float maximum = 0;
            foreach (var a in all) foreach (var b in all) maximum = Mathf.Max(maximum, Flat(a.hipsRelativeToModel - b.hipsRelativeToModel).magnitude);
            return maximum;
        }
        static Vector3 RootAt(AnimationClip clip, float time)
        {
            float Axis(string name)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), name));
                if (curve == null || curve.length < 2) throw new InvalidOperationException("Missing source body curve " + name);
                return curve.Evaluate(time);
            }
            return new Vector3(Axis("RootT.x"), Axis("RootT.y"), Axis("RootT.z"));
        }
        static void CalibratePrefab(string path, CalibrationReport report)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var motion = root.GetComponent<StationActorMotion>();
                if (!motion || motion.walkId != WalkId) { report.preserved.Add(path + ": custom walking clip retained"); return; }
                var so = new SerializedObject(motion);
                string yaml = File.ReadAllText(path);
                // Fields did not exist in the original authored prefabs. Once saved, preserve custom values;
                // 1.35/1 are the initial runtime defaults from before actual source measurement.
                bool customSpeed = yaml.Contains("  " + SpeedProperty + ":") &&
                    Mathf.Abs(Find(so, SpeedProperty).floatValue - 1.35f) > .00001f &&
                    Mathf.Abs(Find(so, SpeedProperty).floatValue - report.referenceSpeed) > .00001f;
                bool customScale = yaml.Contains("  " + ScaleProperty + ":") &&
                    Mathf.Abs(Find(so, ScaleProperty).floatValue - 1f) > .00001f &&
                    Mathf.Abs(Find(so, ScaleProperty).floatValue - report.humanScale) > .00001f;
                if (customSpeed || customScale) { report.preserved.Add(path + ": custom prefab calibration retained"); return; }
                Find(so, SpeedProperty).floatValue = report.referenceSpeed;
                Find(so, ScaleProperty).floatValue = report.humanScale;
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, path);
                report.calibratedPrefabs++;
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        sealed class ActorOverride
        {
            public readonly StationActorMotion motion;
            public readonly bool customSpeed, customScale;
            readonly float speed, scale;
            readonly Transform[] transforms;
            readonly Vector3[] positions, scales;
            readonly Quaternion[] rotations;
            public ActorOverride(StationActorMotion actor)
            {
                motion = actor; var so = new SerializedObject(actor);
                var speedProperty = Find(so, SpeedProperty); var scaleProperty = Find(so, ScaleProperty);
                customSpeed = speedProperty.prefabOverride; customScale = scaleProperty.prefabOverride;
                speed = speedProperty.floatValue; scale = scaleProperty.floatValue;
                transforms = actor.GetComponentsInChildren<Transform>(true);
                positions = transforms.Select(t => t.localPosition).ToArray(); rotations = transforms.Select(t => t.localRotation).ToArray(); scales = transforms.Select(t => t.localScale).ToArray();
            }
            public void AssertPreserved()
            {
                var so = new SerializedObject(motion);
                if (customSpeed) RequireNear(Find(so, SpeedProperty).floatValue, speed, "Manual walking-speed override changed.");
                if (customScale) RequireNear(Find(so, ScaleProperty).floatValue, scale, "Manual human-scale override changed.");
                for (int i = 0; i < transforms.Length; i++)
                    if (!transforms[i] || (transforms[i].localPosition - positions[i]).sqrMagnitude > 1e-10f ||
                        Quaternion.Angle(transforms[i].localRotation, rotations[i]) > .001f || (transforms[i].localScale - scales[i]).sqrMagnitude > 1e-10f)
                        throw new InvalidOperationException("Calibration unexpectedly changed an authored pose on " + motion.name);
            }
        }
        static SerializedProperty Find(SerializedObject owner, string name) => owner.FindProperty(name) ?? throw new InvalidOperationException("Compile runtime locomotion fields before calibration: " + name);
        static void RequireNear(float value, float expected, string error) { if (Mathf.Abs(value - expected) > .00001f) throw new InvalidOperationException(error); }
        static Vector3 Flat(Vector3 value) { value.y = 0; return value; }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static void RequireEditor()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode())
                throw new InvalidOperationException("Stop Play and animation preview before measuring locomotion.");
        }
        static void RequireCleanScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty)
                throw new InvalidOperationException("Save scene edits before applying calibration. No unsaved changes are discarded.");
        }
        static void Backup(string path, string directory, CalibrationReport report)
        { string backup = directory + "/" + Path.GetFileName(path); File.Copy(path, backup, false); report.backups.Add(backup); }
        static void Write(CalibrationReport report)
        { Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true)); }
    }
}
#endif
