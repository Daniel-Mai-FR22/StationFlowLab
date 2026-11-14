#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.HumanAnimation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Saved presentation assets and static validation; no scene or NavMesh modification.</summary>
    public static class StationSlowGaitAuthoring
    {
        public const string ReportPath = "Reports/Navigation20261028/short-stride-authoring.json";
        public const string MatchedLibraryPath = "Assets/TrainStation/DecisionLab/Resources/MatchedXBot/LabMatchedXBotLibrary.asset";
        const string IdleId = "mixamo_neutral_idle_bcadd5d2cf";
        [Serializable] public sealed class Report
        {
            public bool success, runtimeValidationRequired = true;
            public string utc, error, libraryPath, controllerPath, standPath;
            public string limitation = "Static authoring validates asset references and stride mapping only. Acceptance requires native-gait-report.json from --station-gait-probe plus the live Pause/Resume and crowd trials. Failed synchronous editor-pose reports are excluded.";
            public string method = "Two equal-duration clips from the actual Daily matched-XBot library. Neutral standing and its own matched walk blend at a minimum cadence of0.65. No source import, Avatar or existing ordinary walk state is changed.";
            public float referenceSpeed, duration;
            public int mathematicalCases;
            public List<string> backups = new List<string>();
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Prepare and verify short stride presentation")]
        public static void PrepareAndVerify()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Short-stride authoring requires Edit mode.");
            var report = new Report { utc = DateTime.UtcNow.ToString("O"), libraryPath = MatchedLibraryPath };
            try
            {
                var library = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(report.libraryPath);
                Require(library, "The actual Daily matched-XBot animation library is missing.");
                report.controllerPath = AssetDatabase.GetAssetPath(library.controller);
                report.standPath = ReferencePath(library);
                string folder = "Reports/Navigation20261028/Backups/ShortStride-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                Directory.CreateDirectory(folder);
                foreach (string path in new[] { report.controllerPath, report.libraryPath, report.standPath })
                    if (File.Exists(path))
                    {
                        string backup = folder + "/" + Path.GetFileName(path);
                        File.Copy(path, backup, false); report.backups.Add(backup);
                        if (File.Exists(path + ".meta")) File.Copy(path + ".meta", backup + ".meta", false);
                    }
                EnsureResources(library);
                AssetDatabase.SaveAssets();
                var issues = library.Validate(true);
                Require(issues.Count == 0, string.Join("\n", issues));
                VerifyMapping(report);
                VerifyAssets(library, report);
                report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            }
        }

        /// <summary>Also called when the shared station library is regenerated, so its declared blend cannot disappear.</summary>
        public static void EnsureResources(HumanAnimationLibrary library)
        {
            var walk = library.Find(StationActorMotion.DefaultWalkId);
            var neutral = library.Find(IdleId);
            var controller = library.controller as AnimatorController;
            Require(controller && walk != null && walk.clip && neutral != null && neutral.clip, "Short stride requires the saved Humanoid walk, neutral idle and editable controller.");
            string controllerPath = AssetDatabase.GetAssetPath(controller);
            string standPath = ReferencePath(library);
            string clipsFolder = Path.GetDirectoryName(standPath).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(clipsFolder))
                AssetDatabase.CreateFolder(Path.GetDirectoryName(clipsFolder).Replace('\\', '/'), Path.GetFileName(clipsFolder));
            var stand = AssetDatabase.LoadAssetAtPath<AnimationClip>(standPath);
            if (!stand)
            {
                stand = new AnimationClip { name = "Station stride reference - neutral standing", frameRate = walk.clip.frameRate, wrapMode = WrapMode.Loop };
                AssetDatabase.CreateAsset(stand, standPath);
            }
            // Hold a genuine standing pose, including body height and neutral knees. All curves have the
            // walk's exact cycle duration, avoiding the variable effective cadence of differently sized clips.
            foreach (var binding in AnimationUtility.GetCurveBindings(stand)) AnimationUtility.SetEditorCurve(stand, binding, null);
            foreach (var binding in AnimationUtility.GetCurveBindings(neutral.clip))
            {
                if (binding.type != typeof(Animator)) continue;
                var curve = AnimationUtility.GetEditorCurve(neutral.clip, binding);
                if (curve == null) continue;
                float value = curve.Evaluate(0f);
                if (binding.propertyName == "RootT.x" || binding.propertyName == "RootT.z") value = 0f;
                AnimationUtility.SetEditorCurve(stand, binding, AnimationCurve.Constant(0f, walk.clip.length, value));
            }
            var settings = AnimationUtility.GetAnimationClipSettings(neutral.clip);
            settings.startTime = 0f; settings.stopTime = walk.clip.length;
            settings.loopTime = true; settings.loopBlend = true;
            AnimationUtility.SetAnimationClipSettings(stand, settings);
            EditorUtility.SetDirty(stand);
            if (!controller.parameters.Any(p => p.name == StationActorMotion.StrideParameter))
                controller.AddParameter(StationActorMotion.StrideParameter, AnimatorControllerParameterType.Float);
            Require(controller.parameters.Any(p => p.name == StationActorMotion.StrideParameter && p.type == AnimatorControllerParameterType.Float), "Stride parameter must be Float.");
            var machine = controller.layers[0].stateMachine;
            var state = machine.states.Select(s => s.state).FirstOrDefault(s => s.name == StationActorMotion.AdaptiveWalkId)
                ?? machine.AddState(StationActorMotion.AdaptiveWalkId);
            var tree = state.motion as BlendTree;
            if (!tree)
            {
                tree = new BlendTree { name = "Station adaptive stride", hideFlags = HideFlags.HideInHierarchy };
                AssetDatabase.AddObjectToAsset(tree, controller);
            }
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = StationActorMotion.StrideParameter;
            tree.useAutomaticThresholds = false;
            tree.minThreshold = 0f; tree.maxThreshold = 1f;
            tree.children = new[] {
                new ChildMotion { motion = stand, threshold = 0f, timeScale = 1f },
                new ChildMotion { motion = walk.clip, threshold = 1f, timeScale = 1f }
            };
            state.motion = tree; state.iKOnFeet = true; state.writeDefaultValues = true; state.speed = 1f;
            var motions = library.motions.ToList();
            var entry = motions.FirstOrDefault(m => m.id == StationActorMotion.AdaptiveWalkId);
            if (entry == null) { entry = new HumanMotion { id = StationActorMotion.AdaptiveWalkId }; motions.Add(entry); }
            entry.displayName = "Marche adaptative - pas raccourcis";
            entry.stateName = "Base Layer." + entry.id; entry.sourceAssetPath = controllerPath;
            entry.clip = walk.clip; entry.loop = true; entry.blendIn = .15f;
            entry.source = HumanMotionSource.Authored; entry.category = HumanMotionCategory.Locomotion;
            entry.blendParameter = StationActorMotion.StrideParameter;
            entry.blendChildren = new[] {
                new HumanMotionBlendChild { clip = stand, threshold = 0f },
                new HumanMotionBlendChild { clip = walk.clip, threshold = 1f }
            };
            library.motions = motions.ToArray();
            EditorUtility.SetDirty(tree); EditorUtility.SetDirty(state); EditorUtility.SetDirty(controller); EditorUtility.SetDirty(library);
        }

        static string ReferencePath(HumanAnimationLibrary library) =>
            Path.GetDirectoryName(AssetDatabase.GetAssetPath(library.controller)).Replace('\\', '/') + "/Clips/Station_StrideReference.anim";

        static void VerifyMapping(Report report)
        {
            foreach (float reference in new[] { .8f, 1.0877634f, 1.4f })
                for (int i = 1; i <= 180; i++)
                {
                    float speed = i / 100f;
                    StationActorMotion.CalculateWalkPresentation(speed, reference, true, out float cadence, out float stride);
                    Require(Mathf.Abs(cadence * stride * reference - speed) < .00001f, "Short stride nominal speed differs from the measured navigation speed.");
                    Require(cadence >= StationActorMotion.MinimumStepCadence && stride >= 0f && stride <= 1f, "Invalid cadence or stride.");
                    if (speed >= reference * StationActorMotion.MinimumStepCadence) Require(stride == 1f, "Ordinary walking must preserve its full stride.");
                    report.mathematicalCases++;
                }
        }

        static void VerifyAssets(HumanAnimationLibrary library, Report report)
        {
            var entry = library.Find(StationActorMotion.AdaptiveWalkId);
            var walk = library.Find(StationActorMotion.DefaultWalkId);
            var controller = library.controller as AnimatorController;
            var state = controller.layers[0].stateMachine.states.Select(s => s.state).First(s => s.name == entry.id);
            var tree = state.motion as BlendTree;
            Require(tree && state.iKOnFeet && tree.blendParameter == StationActorMotion.StrideParameter, "Adaptive state must use its saved stride blend and Foot IK.");
            Require(tree.children.Length == 2 && tree.children[1].motion == walk.clip && entry.clip == walk.clip,
                "Adaptive tree must retain the actual matched walk, not a different library's clip.");
            Require(tree.children[0].motion == entry.blendChildren[0].clip && tree.children[0].threshold == 0f &&
                tree.children[1].threshold == 1f && tree.children.All(c => c.timeScale == 1f),
                "Saved tree and declared blend sources differ.");
            Require(AssetDatabase.GetAssetPath(entry.blendChildren[0].clip) == report.standPath, "Standing reference belongs to another library.");
            Require(controller.parameters.Any(p => p.name == StationActorMotion.StrideParameter && p.type == AnimatorControllerParameterType.Float),
                "Stride parameter is missing or not Float.");
            report.duration = walk.clip.length;
            var trajectory = AnimationUtility.GetEditorCurve(walk.clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.z"));
            Require(trajectory != null, "Matched walk has no retained body trajectory.");
            // Static reference only; the runtime probe uses each actual saved actor's calibrated humanoid scale.
            report.referenceSpeed = Mathf.Abs(trajectory.Evaluate(walk.clip.length) - trajectory.Evaluate(0f)) * 1.05003643f / walk.clip.length;
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
#endif
