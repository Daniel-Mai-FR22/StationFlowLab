#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StationFlow.HumanAnimation;
using StationFlow.HumanAnimation.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.Social.Editor
{
    /// <summary>Explicit, backed-up migration of saved actors to the reviewed Mixamo walking gait.</summary>
    public static class StationGaitAuthoring
    {
        public const string CandidateWalkId = "mixamo_male_standard_walking_ef7424170f";
        public const string ReportPath = "Reports/TrainStationSocial/gait-upgrade.json";

        [Serializable] public sealed class UpgradeReport
        {
            public bool success;
            public string appliedUtc, scene, motionId = CandidateWalkId, backupDirectory, error;
            public int sceneActors, changedSceneActors, prefabActors, changedPrefabActors, savedPrefabs;
            public int objectsBefore, objectsAfter, camerasBefore, camerasAfter;
            public StationLocomotionCalibration.CalibrationReport calibration;
            public List<string> backups = new List<string>(), prefabs = new List<string>();
        }

        [MenuItem("Efrei Research Lab/Train Station/Social/Apply reviewed standard walking gait")]
        public static void Apply()
        {
            RequireCleanEditor();
            var report = new UpgradeReport { appliedUtc = DateTime.UtcNow.ToString("O"), scene = StationSocialAuthoring.ScenePath };
            Scene station = SceneManager.GetSceneByPath(report.scene);
            Scene activeBefore = SceneManager.GetActiveScene();
            bool opened = !station.IsValid() || !station.isLoaded;
            try
            {
                // Measure the imported source before touching any library, prefab or scene.
                report.calibration = StationLocomotionCalibration.MeasureMotion(CandidateWalkId, HumanAnimationAuthoring.MixamoLibraryPath);
                Require(!report.calibration.poseDriftDetected, "Candidate gait drifts relative to its model root.");
                Require(report.calibration.sourceExtractedDifference < .0001f, "Candidate source and extracted stride differ.");
                var sourceLibrary = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(HumanAnimationAuthoring.MixamoLibraryPath);
                Require(sourceLibrary && sourceLibrary.Find(CandidateWalkId)?.clip, "Candidate Mixamo source clip is missing.");
                var prefabPaths = AssetDatabase.FindAssets("t:Prefab", new[] { StationSocialAuthoring.AssetRoot + "/Prefabs" })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => AssetDatabase.LoadAssetAtPath<GameObject>(p).GetComponentsInChildren<StationActorMotion>(true).Length > 0)
                    // Update nested members before their containing reusable group/service prefabs.
                    .OrderBy(p => AssetDatabase.GetDependencies(p, true).Length).ThenBy(p => p, StringComparer.Ordinal).ToArray();
                Require(prefabPaths.Length > 0, "No saved social actor prefabs were found.");
                report.backupDirectory = "BuildRequests/Backups/StationGait-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                foreach (string path in prefabPaths.Concat(new[] { report.scene,
                    StationSocialAnimationAssets.LibraryPath,
                    StationSocialAnimationAssets.ResourceDirectory + "/StationSocial.controller",
                    StationSocialAnimationAssets.ResourceDirectory + "/Clips/Station_Sweeping.anim" }).Distinct())
                    Backup(path, report);

                if (opened) station = EditorSceneManager.OpenScene(report.scene, OpenSceneMode.Additive);
                var pose = new ScenePose(station);
                report.objectsBefore = pose.transforms.Length;
                report.camerasBefore = Components<Camera>(station).Length;
                var library = StationSocialAnimationAssets.EnsureResources();
                Require(library.Find(CandidateWalkId)?.clip, "Candidate was not included in the saved social library.");
                var controller = library.controller as AnimatorController;
                var state = controller ? controller.layers[0].stateMachine.states.Select(s => s.state)
                    .FirstOrDefault(s => s.name == CandidateWalkId) : null;
                Require(state && state.iKOnFeet, "Candidate saved Animator state must enable Foot IK.");

                foreach (string path in prefabPaths)
                {
                    var prefab = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        var actors = prefab.GetComponentsInChildren<StationActorMotion>(true);
                        report.prefabActors += actors.Length;
                        bool changed = false;
                        foreach (var actor in actors)
                            if (ApplyActor(actor, library, report.calibration)) { changed = true; report.changedPrefabActors++; }
                        if (changed)
                        {
                            Require(PrefabUtility.SaveAsPrefabAsset(prefab, path), "Could not save gait prefab " + path);
                            report.savedPrefabs++;
                        }
                        report.prefabs.Add(path);
                    }
                    finally { PrefabUtility.UnloadPrefabContents(prefab); }
                }

                var sceneActors = Components<StationActorMotion>(station);
                Require(sceneActors.Length > 0, "TrainStation contains no saved actors.");
                report.sceneActors = sceneActors.Length;
                foreach (var actor in sceneActors)
                    if (ApplyActor(actor, library, report.calibration)) report.changedSceneActors++;
                foreach (var actor in sceneActors) ValidateActor(actor, library, report.calibration);
                pose.AssertPreserved(station);
                report.objectsAfter = Components<Transform>(station).Length;
                report.camerasAfter = Components<Camera>(station).Length;
                Require(report.camerasBefore == report.camerasAfter, "Gait migration changed the saved cameras.");
                AssetDatabase.SaveAssets();
                Require(EditorSceneManager.SaveScene(station), "Could not save TrainStation gait upgrade.");
                report.success = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                if (opened && station.IsValid() && station.isLoaded && !station.isDirty) EditorSceneManager.CloseScene(station, true);
                if (activeBefore.IsValid() && activeBefore.isLoaded) SceneManager.SetActiveScene(activeBefore);
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            }
        }

        static bool ApplyActor(StationActorMotion actor, HumanAnimationLibrary library, StationLocomotionCalibration.CalibrationReport calibration)
        {
            var player = actor.animationPlayer;
            Require(player && player.TargetAnimator, "Actor lacks its saved animation player/Animator: " + actor.name);
            float strideScale = actor.walkStrideScale;
            bool changed = false;
            var motion = new SerializedObject(actor);
            changed |= SetString(motion, "walkId", CandidateWalkId);
            changed |= SetFloat(motion, "walkReferenceSpeed", calibration.referenceSpeed);
            changed |= SetFloat(motion, "walkReferenceHumanScale", calibration.humanScale);
            changed |= motion.ApplyModifiedPropertiesWithoutUndo();
            var playback = new SerializedObject(player);
            var savedLibrary = playback.FindProperty("library");
            if (savedLibrary.objectReferenceValue != library) { savedLibrary.objectReferenceValue = library; changed = true; }
            var feet = playback.FindProperty("stabilizeFeet");
            if (!feet.boolValue) { feet.boolValue = true; changed = true; }
            changed |= playback.ApplyModifiedPropertiesWithoutUndo();
            if (!player.TargetAnimator.stabilizeFeet)
            {
                player.TargetAnimator.stabilizeFeet = true;
                EditorUtility.SetDirty(player.TargetAnimator);
                if (PrefabUtility.IsPartOfPrefabInstance(player.TargetAnimator))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(player.TargetAnimator);
                changed = true;
            }
            Require(actor.walkStrideScale == strideScale, "A model's custom stride multiplier changed: " + actor.name);
            ValidateActor(actor, library, calibration);
            return changed;
        }

        static void ValidateActor(StationActorMotion actor, HumanAnimationLibrary library, StationLocomotionCalibration.CalibrationReport calibration)
        {
            Require(actor.walkId == CandidateWalkId && Mathf.Abs(actor.walkReferenceSpeed - calibration.referenceSpeed) < .00001f &&
                Mathf.Abs(actor.walkReferenceHumanScale - calibration.humanScale) < .00001f,
                "Saved gait calibration does not match its measured source: " + actor.name);
            Require(actor.animationPlayer.Library == library && actor.animationPlayer.StabilizeFeet && actor.animationPlayer.TargetAnimator.stabilizeFeet,
                "Saved gait playback configuration is incomplete: " + actor.name);
        }

        static bool SetString(SerializedObject owner, string name, string value)
        { var property = owner.FindProperty(name); if (property.stringValue == value) return false; property.stringValue = value; return true; }
        static bool SetFloat(SerializedObject owner, string name, float value)
        { var property = owner.FindProperty(name); if (Mathf.Abs(property.floatValue - value) < .000001f) return false; property.floatValue = value; return true; }
        static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();

        sealed class ScenePose
        {
            public readonly Transform[] transforms;
            readonly Vector3[] positions, scales;
            readonly Quaternion[] rotations;
            readonly Dictionary<StationActorMotion, float> strideScales;
            public ScenePose(Scene scene)
            {
                transforms = Components<Transform>(scene);
                positions = transforms.Select(t => t.localPosition).ToArray(); scales = transforms.Select(t => t.localScale).ToArray();
                rotations = transforms.Select(t => t.localRotation).ToArray();
                strideScales = Components<StationActorMotion>(scene).ToDictionary(a => a, a => a.walkStrideScale);
            }
            public void AssertPreserved(Scene scene)
            {
                Require(Components<Transform>(scene).Length == transforms.Length, "Gait migration created or removed saved objects.");
                for (int i = 0; i < transforms.Length; i++)
                    Require(transforms[i] && (transforms[i].localPosition - positions[i]).sqrMagnitude < 1e-9f &&
                        (transforms[i].localScale - scales[i]).sqrMagnitude < 1e-9f && Quaternion.Angle(transforms[i].localRotation, rotations[i]) < .01f,
                        "Gait migration changed an authored transform at index " + i);
                foreach (var pair in strideScales) Require(pair.Key && pair.Key.walkStrideScale == pair.Value, "Custom scene stride multiplier changed.");
            }
        }

        static void Backup(string path, UpgradeReport report)
        {
            if (!File.Exists(path)) return;
            string target = report.backupDirectory + "/" + path;
            Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(path, target, false); report.backups.Add(target);
            if (File.Exists(path + ".meta")) File.Copy(path + ".meta", target + ".meta", false);
        }
        static void RequireCleanEditor()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && !AnimationMode.InAnimationMode(), "Stop Play mode and animation preview before applying the gait upgrade.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                Require(!SceneManager.GetSceneAt(i).isDirty, "Save open scene changes before applying the gait upgrade.");
        }
        static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    }
}
#endif
