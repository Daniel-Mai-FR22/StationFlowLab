#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.HumanAnimation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Calibrate retained Mixamo trajectories before enabling directional presentation on saved actors.</summary>
    public static class StationDirectionalLocomotionAuthoring
    {
        public const string ReportPath = "Reports/TrainStationSocial/CrowdNaturalness/directional-calibration.json";
        public const string LeftStrafeId = "mixamo_left_strafe_walking_c95be7c053";
        public const string RightStrafeId = "mixamo_right_strafe_walking_1918c30aab";
        public const string LeftTurnId = "station_left_turn_extracted_yaw";
        public const string RightTurnId = "station_right_turn_extracted_yaw";
        const string SourceLeftTurnId = "mixamo_left_turn_90_c4c159e68f";
        const string SourceRightTurnId = "mixamo_right_turn_90_e0abf15f6a";

        [Serializable] public sealed class TurnCalibration
        {
            public string motionId, sourceAsset;
            public float duration, signedYawDegrees, referenceDegreesPerSecond, extractedYawDifference;
            public string stationClip;
            public float previewEndBodyYaw, previewMaximumBodyYaw, previewMaximumHipExcursion;
        }
        [Serializable] public sealed class Report
        {
            public string checkedUtc, error;
            public bool success;
            public string translationMethod = "Original Mixamo Humanoid RootT XZ displacement / duration, scaled by the reference XBot Avatar; no guessed gait speeds.";
            public string rotationMethod = "Accumulated original Humanoid RootQ yaw / exact clip duration. In-place root yaw is applied by StationActorMotion; Animator root motion remains disabled.";
            public StationLocomotionCalibration.CalibrationReport leftStrafe, rightStrafe;
            public TurnCalibration leftTurn, rightTurn;
            public List<string> backups = new List<string>();
        }
        static Report measured;

        [MenuItem("Efrei Research Lab/Train Station/Social/Measure directional locomotion")]
        public static void Measure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Directional calibration requires Edit mode.");
            var report = new Report { checkedUtc = DateTime.UtcNow.ToString("O") };
            try
            {
                report.leftStrafe = StationLocomotionCalibration.MeasureMotion(LeftStrafeId, StationSocialAnimationAssets.LibraryPath);
                report.rightStrafe = StationLocomotionCalibration.MeasureMotion(RightStrafeId, StationSocialAnimationAssets.LibraryPath);
                foreach (var stride in new[] { report.leftStrafe, report.rightStrafe })
                {
                    Require(!stride.poseDriftDetected, "Directional clip drifts relative to the model root: " + stride.motionId);
                    Require(stride.sourceExtractedDifference < .0001f, "Extracted directional trajectory differs from its Mixamo source.");
                }
                report.leftTurn = MeasureTurn(SourceLeftTurnId);
                report.rightTurn = MeasureTurn(SourceRightTurnId);
                Require(report.leftTurn.signedYawDegrees < -20f && report.rightTurn.signedYawDegrees > 20f,
                    "Imported turn directions do not match their saved labels.");
                BackupStationAssets(report);
                PrepareTurnAsset(SourceLeftTurnId, LeftTurnId, report.leftTurn);
                PrepareTurnAsset(SourceRightTurnId, RightTurnId, report.rightTurn);
                report.success = true;
                measured = report;
            }
            catch (Exception error) { report.error = error.ToString(); measured = null; throw; }
            finally
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            }
        }

        static void BackupStationAssets(Report report)
        {
            string directory = "BuildRequests/Backups/StationDirectional-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            Directory.CreateDirectory(directory);
            foreach (var path in new[] { StationSocialAnimationAssets.LibraryPath,
                StationSocialAnimationAssets.ResourceDirectory + "/StationSocial.controller",
                StationSocialAnimationAssets.ResourceDirectory + "/Clips/" + LeftTurnId + ".anim",
                StationSocialAnimationAssets.ResourceDirectory + "/Clips/" + RightTurnId + ".anim" })
            {
                if (!File.Exists(path)) continue;
                string backup = directory + "/" + Path.GetFileName(path);
                File.Copy(path, backup, false);
                report.backups.Add(backup);
            }
        }

        static void PrepareTurnAsset(string sourceId, string stationId, TurnCalibration result)
        {
            var library = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(StationSocialAnimationAssets.LibraryPath);
            var original = library.Find(sourceId);
            string path = StationSocialAnimationAssets.ResourceDirectory + "/Clips/" + stationId + ".anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (!clip)
            {
                clip = UnityEngine.Object.Instantiate(original.clip);
                clip.name = stationId;
                AssetDatabase.CreateAsset(clip, path);
            }
            else EditorUtility.CopySerialized(original.clip, clip);
            clip.name = stationId;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            // Original import bakes its 90-degree body turn into the pose. Extract it instead:
            // NavMesh actor yaw will apply it exactly once while Animator root motion is off.
            settings.loopBlendOrientation = false;
            settings.loopBlendPositionXZ = false;
            settings.keepOriginalOrientation = true;
            settings.keepOriginalPositionXZ = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            EditorUtility.SetDirty(clip);
            var motions = library.motions.ToList();
            var motion = motions.FirstOrDefault(m => m.id == stationId);
            if (motion == null) { motion = new HumanMotion { id = stationId }; motions.Add(motion); }
            motion.displayName = "Station " + (stationId == LeftTurnId ? "left" : "right") + " turn / extracted Mixamo yaw";
            motion.stateName = "Base Layer." + stationId; motion.sourceAssetPath = original.sourceAssetPath;
            motion.clip = clip; motion.loop = false; motion.blendIn = .18f;
            motion.source = HumanMotionSource.Mixamo; motion.category = HumanMotionCategory.Locomotion;
            var controller = library.controller as AnimatorController;
            Require(controller, "Station locomotion requires its saved editable Animator Controller.");
            var machine = controller.layers[0].stateMachine;
            var state = machine.states.Select(s => s.state).FirstOrDefault(s => s.name == stationId) ?? machine.AddState(stationId);
            state.motion = clip; state.iKOnFeet = true; state.writeDefaultValues = true;
            library.motions = motions.ToArray();
            EditorUtility.SetDirty(library); EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            result.stationClip = path;
            ValidateTurnPose(library, motion, result);
        }

        static void ValidateTurnPose(HumanAnimationLibrary library, HumanMotion motion, TurnCalibration result)
        {
            var preview = EditorSceneManager.NewPreviewScene();
            GameObject instance = null;
            try
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/HumanAnimation/Source/Mixamo/Models/XBot.fbx");
                Require(model, "Turn calibration model is missing.");
                instance = (GameObject)PrefabUtility.InstantiatePrefab(model, preview);
                instance.hideFlags = HideFlags.HideAndDontSave;
                var animator = instance.GetComponentInChildren<Animator>();
                animator.runtimeAnimatorController = library.controller;
                animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind(); animator.Update(0f);
                animator.Play(Animator.StringToHash(motion.StatePath), 0, 0f); animator.Update(0f);
                var hip = animator.GetBoneTransform(HumanBodyBones.Hips);
                Vector3 firstForward = Vector3.ProjectOnPlane(animator.transform.InverseTransformDirection(hip.forward), Vector3.up);
                Vector3 firstHip = animator.transform.InverseTransformPoint(hip.position);
                for (int i = 1; i <= 60; i++)
                {
                    animator.Update(motion.clip.length / 60f);
                    Vector3 forward = Vector3.ProjectOnPlane(animator.transform.InverseTransformDirection(hip.forward), Vector3.up);
                    float yaw = Vector3.SignedAngle(firstForward, forward, Vector3.up);
                    Vector3 drift = animator.transform.InverseTransformPoint(hip.position) - firstHip; drift.y = 0f;
                    result.previewMaximumBodyYaw = Mathf.Max(result.previewMaximumBodyYaw, Mathf.Abs(yaw));
                    result.previewMaximumHipExcursion = Mathf.Max(result.previewMaximumHipExcursion, drift.magnitude);
                    if (i == 60) result.previewEndBodyYaw = yaw;
                }
                Require(Mathf.Abs(result.previewEndBodyYaw) < 20f && result.previewMaximumBodyYaw < 50f,
                    "Turn still rotates the body in the pose and would double actor yaw: " + motion.id);
                Require(result.previewMaximumHipExcursion < .35f, "Turn shifts the body away from its navigation root: " + motion.id);
            }
            finally
            {
                if (instance) UnityEngine.Object.DestroyImmediate(instance);
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        /// <summary>Called by the scene/prefab migration; applies only values measured from saved source clips.</summary>
        public static void ConfigureMotion(StationActorMotion actor)
        {
            if (!actor) return;
            if (measured == null || !measured.success) Measure();
            Require(actor.animationPlayer && actor.animationPlayer.Library, "Actor has no saved animation library: " + actor.name);
            foreach (var id in new[] { LeftStrafeId, RightStrafeId, LeftTurnId, RightTurnId })
                Require(actor.animationPlayer.Library.Find(id)?.clip, "Actor library is missing directional motion " + id);
            actor.leftStrafeId = LeftStrafeId; actor.rightStrafeId = RightStrafeId;
            actor.leftTurnId = LeftTurnId; actor.rightTurnId = RightTurnId;
            actor.leftStrafeReferenceSpeed = measured.leftStrafe.referenceSpeed;
            actor.rightStrafeReferenceSpeed = measured.rightStrafe.referenceSpeed;
            actor.leftTurnDegreesPerSecond = measured.leftTurn.referenceDegreesPerSecond;
            actor.rightTurnDegreesPerSecond = measured.rightTurn.referenceDegreesPerSecond;
            actor.strafeEnterAngle = 60f; actor.strafeExitAngle = 38f; actor.directionalPoseHold = .32f;
            actor.turnBeforeTravelAngle = 115f; actor.directionalLocomotion = true;
            EditorUtility.SetDirty(actor);
            if (PrefabUtility.IsPartOfPrefabInstance(actor)) PrefabUtility.RecordPrefabInstancePropertyModifications(actor);
        }

        static TurnCalibration MeasureTurn(string id)
        {
            var library = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(StationSocialAnimationAssets.LibraryPath);
            var motion = library ? library.Find(id) : null;
            Require(motion != null && motion.clip, "Missing saved turn clip " + id);
            var source = AssetDatabase.LoadAllAssetsAtPath(motion.sourceAssetPath).OfType<AnimationClip>()
                .FirstOrDefault(c => c.humanMotion && !c.name.StartsWith("__preview__", StringComparison.Ordinal));
            Require(source && source.length > .1f, "Missing original turn trajectory " + id);
            float yaw = AccumulatedYaw(source), extracted = AccumulatedYaw(motion.clip);
            float rate = Mathf.Abs(yaw) / source.length;
            Require(Mathf.Abs(yaw) >= 20f && Mathf.Abs(yaw) <= 200f && rate >= 15f && rate <= 300f,
                "Turn source has no usable measured body rotation: " + id);
            Require(Mathf.Abs(yaw - extracted) < .05f, "Extracted turn rotation differs from its source: " + id);
            return new TurnCalibration { motionId = id, sourceAsset = motion.sourceAssetPath, duration = source.length,
                signedYawDegrees = yaw, referenceDegreesPerSecond = rate, extractedYawDifference = Mathf.Abs(yaw - extracted) };
        }

        static float AccumulatedYaw(AnimationClip clip)
        {
            var curves = new[] { "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" }
                .Select(name => AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), name))).ToArray();
            Require(curves.All(c => c != null && c.length > 0), "Turn source lacks body quaternion channels: " + clip.name);
            Vector3 Forward(float time)
            {
                var q = new Quaternion(curves[0].Evaluate(time), curves[1].Evaluate(time), curves[2].Evaluate(time), curves[3].Evaluate(time));
                return Vector3.ProjectOnPlane(q.normalized * Vector3.forward, Vector3.up).normalized;
            }
            float yaw = 0f; Vector3 previous = Forward(0f);
            for (int i = 1; i <= 60; i++)
            {
                Vector3 current = Forward(clip.length * i / 60f);
                yaw += Vector3.SignedAngle(previous, current, Vector3.up); previous = current;
            }
            return yaw;
        }

        static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    }
}
#endif
