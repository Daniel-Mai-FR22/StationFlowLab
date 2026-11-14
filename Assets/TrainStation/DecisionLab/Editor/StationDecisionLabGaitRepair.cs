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
using UnityEngine.SceneManagement;
using static StationFlow.TrainStation.DecisionLab.Editor.StationDecisionLabAuthoring;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Bind matched-XBot imported clips through Lab-owned assets; never alter source assets or prefab files.</summary>
    public static class StationDecisionLabGaitRepair
    {
        const string SourceLibrary = "Assets/TrainStation/Social/Resources/TrainStationSocial/StationSocialLibrary.asset";
        const string ModelPath = "Assets/HumanAnimation/Source/Mixamo/Models/XBot.fbx";
        const string SourceWalk = "Assets/HumanAnimation/Source/Mixamo/Animations/male_standard_walking.fbx";
        const string SourceIdle = "Assets/HumanAnimation/Source/Mixamo/Animations/Neutral_Idle.fbx";
        const string MatchedWalk = "Assets/TrainStation/DecisionLab/Animation/MatchedXBot/DecisionLab_Walk_XBot.fbx";
        const string MatchedIdle = "Assets/TrainStation/DecisionLab/Animation/MatchedXBot/DecisionLab_Idle_XBot.fbx";
        const string Root = "Assets/TrainStation/DecisionLab/Resources/MatchedXBot";
        const string OwnedLibrary = Root + "/LabMatchedXBotLibrary.asset";
        const string OwnedController = Root + "/LabMatchedXBot.controller";
        const string WalkId = "mixamo_male_standard_walking_ef7424170f";
        const string IdleId = "mixamo_neutral_idle_bcadd5d2cf";
        const string ReceiptPath = "Reports/TrainStationDecisionLab/gait-matched-avatar-authoring.json";
        const string CalibrationReport = "Reports/TrainStationDecisionLab/gait-matched-avatar-comparison.json";
        const float MatchedReferenceSpeed = 1.0877634286880493f;
        const float MatchedReferenceHumanScale = 1.0500364303588867f;
        const float MatchedStrideMetres = 1.3415749073028564f;
        const string Recipe = "matched-xbot-v2-imported-clips-preserve-ik-stabilization-measured-calibration";

        [Serializable] public sealed class Fingerprint { public string path, sha256; }
        [Serializable] public sealed class Receipt
        {
            public string recipe, checkedUtc, backupDirectory, error;
            public string status = "Lab-only matched-Avatar animation binding; runtime/visual verification is recorded separately.";
            public bool success, reusedOwnedAssets, protectedSourcesPreserved;
            public int playerBindingsChanged, motionActorCount, actorCalibrationsChanged;
            public List<Fingerprint> protectedBefore = new List<Fingerprint>(), ownedAfter = new List<Fingerprint>();
            public string matchedWalk = MatchedWalk, matchedIdle = MatchedIdle;
            public bool ikSettingsPreserved, stabilizationAndMotionSettingsPreserved, measuredCalibrationApplied;
            public string calibrationEvidence = CalibrationReport;
            public string calibrationMethod = "Matched clip RootT XZ endpoint displacement multiplied by XBot humanScale, divided by clip duration; NavMesh speed is not used.";
            public float measuredReferenceSpeed = MatchedReferenceSpeed, measuredReferenceHumanScale = MatchedReferenceHumanScale,
                measuredStrideMetres = MatchedStrideMetres;
        }

        sealed class PlayerSettings
        {
            public HumanAnimationPlayer player;
            public Avatar avatar;
            public bool playerStabilization, animatorStabilization;
            public PlayerSettings(HumanAnimationPlayer value)
            {
                player = value; avatar = value.TargetAnimator.avatar;
                playerStabilization = value.StabilizeFeet;
                animatorStabilization = value.TargetAnimator.stabilizeFeet;
            }
            public void AssertPreserved()
            {
                Require(player.TargetAnimator.avatar == avatar && player.StabilizeFeet == playerStabilization &&
                    player.TargetAnimator.stabilizeFeet == animatorStabilization,
                    "Matched import authoring changed an Avatar or stabilization setting: " + player.name);
            }
        }

        sealed class ActorSettings
        {
            public StationActorMotion actor;
            public string walkId, idleId;
            public float referenceSpeed, referenceHumanScale, strideScale;
            public ActorSettings(StationActorMotion value)
            {
                actor = value;
                walkId = value.walkId; idleId = value.idleId; referenceSpeed = value.walkReferenceSpeed;
                referenceHumanScale = value.walkReferenceHumanScale; strideScale = value.walkStrideScale;
            }
            public void AssertPreserved()
            {
                float expectedSpeed = walkId == WalkId ? MatchedReferenceSpeed : referenceSpeed;
                float expectedScale = walkId == WalkId ? MatchedReferenceHumanScale : referenceHumanScale;
                Require(actor.walkId == walkId && actor.idleId == idleId &&
                    actor.walkReferenceSpeed == expectedSpeed && actor.walkReferenceHumanScale == expectedScale && actor.walkStrideScale == strideScale,
                    "Matched import authoring changed a motion ID/stride setting or missed measured calibration: " + actor.name);
            }
        }

        public static void ApplyOpenLabScene()
        {
            RequireEditAndClean();
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == ScenePath, "Matched-Avatar authoring is restricted to the open saved Decision Lab scene.");
            var original = JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestPath));
            Require(Hash(SourceScene) == original.sourceSceneSha256 && Hash(SourceSchedule) == original.sourceScheduleSha256,
                "Original station scene/schedule do not match the preserved Lab creation baseline.");
            var source = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(SourceLibrary);
            Require(source && source.Validate(true).Count == 0, "The source motion library must be valid.");
            var sourceController = source.controller as AnimatorController;
            Require(sourceController, "The source library must use a saved AnimatorController.");
            var xbotAvatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().Single(a => a.isValid && a.isHuman);
            PrepareMissingMatchedCopy(SourceWalk, MatchedWalk, xbotAvatar);
            PrepareMissingMatchedCopy(SourceIdle, MatchedIdle, xbotAvatar);
            var matchedWalk = LoadMatchedClip(MatchedWalk, xbotAvatar);
            var matchedIdle = LoadMatchedClip(MatchedIdle, xbotAvatar);
            var sourceWalk = source.Find(WalkId); var sourceIdle = source.Find(IdleId);
            Require(sourceWalk != null && sourceIdle != null && sourceWalk.clip && sourceIdle.clip, "Reviewed source walk/idle entries are missing.");
            Require(Mathf.Abs(matchedWalk.length - sourceWalk.clip.length) < .0001f &&
                Mathf.Abs(matchedIdle.length - sourceIdle.clip.length) < .0001f,
                "Matched imports changed source durations; review speed calibration before binding.");
            AssertMeasuredCalibration(matchedWalk);
            var players = Components<HumanAnimationPlayer>(scene).Where(player =>
            {
                string path = AssetDatabase.GetAssetPath(player.Library);
                return path == SourceLibrary || path == OwnedLibrary;
            }).ToArray();
            Require(players.Length > 0, "No saved Lab players use the source or owned motion library.");
            foreach (var player in players)
            {
                Require(player.TargetAnimator, "Incomplete player binding: " + player.name);
                Require(player.TargetAnimator.avatar == xbotAvatar, "This matched import is specific to XBot: " + player.name);
            }
            var playerSet = new HashSet<HumanAnimationPlayer>(players);
            var actors = Components<StationActorMotion>(scene).Where(actor => playerSet.Contains(actor.animationPlayer)).ToArray();
            var playerSettings = players.Select(p => new PlayerSettings(p)).ToArray();
            var actorSettings = actors.Select(a => new ActorSettings(a)).ToArray();
            string[] protectedPaths = ProtectedPaths();
            var result = new Receipt { recipe = Recipe, checkedUtc = DateTime.UtcNow.ToString("O"),
                protectedBefore = Fingerprints(protectedPaths), motionActorCount = actors.Length };
            var previous = File.Exists(ReceiptPath) ? JsonUtility.FromJson<Receipt>(File.ReadAllText(ReceiptPath)) : null;
            bool reuse = File.Exists(OwnedLibrary) || File.Exists(OwnedController);
            if (reuse)
            {
                Require(previous != null && previous.success && previous.recipe == Recipe,
                    "Existing Lab animation assets lack a matching successful receipt; preserve and review them before regeneration.");
                AssertFingerprints(previous.protectedBefore, "A protected source changed since matched-Avatar authoring.");
                AssertFingerprints(previous.ownedAfter, "A Lab animation asset has been edited since authoring; preserve those edits.");
                result.reusedOwnedAssets = true;
            }
            result.backupDirectory = BackupLabFiles();
            try
            {
                HumanAnimationLibrary library;
                AnimatorController controller;
                if (reuse)
                {
                    library = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(OwnedLibrary);
                    controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(OwnedController);
                    Require(library && controller && library.controller == controller, "Lab animation references are incomplete.");
                }
                else
                {
                    EnsureAssetFolder(Root);
                    // CopyAsset duplicates the controller file and ALL embedded state/state-machine/transition subassets.
                    // Object.Instantiate(controller) is deliberately avoided because subasset references can remain shared.
                    Require(AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(sourceController), OwnedController), "Could not deep-copy the source controller.");
                    controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(OwnedController);
                    Require(controller, "Lab controller did not import.");
                    var replacements = new Dictionary<AnimationClip, AnimationClip> {
                        { sourceWalk.clip, matchedWalk }, { sourceIdle.clip, matchedIdle } };
                    foreach (var state in AllStates(controller))
                    {
                        Require(AssetDatabase.GetAssetPath(state) == OwnedController, "Controller state still belongs to a source asset.");
                        if (state.motion is AnimationClip clip && replacements.TryGetValue(clip, out var replacement)) state.motion = replacement;
                        // All saved Foot IK settings are deliberately preserved for the first live comparison.
                        EditorUtility.SetDirty(state);
                    }
                    EditorUtility.SetDirty(controller); AssetDatabase.SaveAssetIfDirty(controller);
                    library = ScriptableObject.CreateInstance<HumanAnimationLibrary>();
                    library.name = "Decision Lab matched XBot motions"; library.controller = controller; library.upperBodyMask = source.upperBodyMask;
                    library.motions = source.motions.Select(m => new HumanMotion { id = m.id, displayName = m.displayName,
                        stateName = m.stateName, sourceAssetPath = m.id == WalkId ? MatchedWalk : m.id == IdleId ? MatchedIdle : m.sourceAssetPath, loop = m.loop, blendIn = m.blendIn,
                        category = m.category, source = m.source, clip = replacements.TryGetValue(m.clip, out var copy) ? copy : m.clip }).ToArray();
                    AssetDatabase.CreateAsset(library, OwnedLibrary); AssetDatabase.SaveAssetIfDirty(library);
                }
                Require(library.Validate(true).Count == 0, "Lab animation library failed validation.");
                var sourceStates = AllStates(sourceController).ToDictionary(s => s.name, s => s.iKOnFeet, StringComparer.Ordinal);
                foreach (var state in AllStates(controller))
                    Require(state.name == StationActorMotion.AdaptiveWalkId ? state.iKOnFeet :
                        sourceStates.TryGetValue(state.name, out bool footIK) && footIK == state.iKOnFeet,
                        "Matched controller changed a state Foot IK flag: " + state.name);
                Require(library.Find(WalkId).clip == matchedWalk && library.Find(IdleId).clip == matchedIdle &&
                    library.Find(WalkId).sourceAssetPath == MatchedWalk && library.Find(IdleId).sourceAssetPath == MatchedIdle,
                    "Matched clip references/provenance do not match the reviewed imports.");
                // Recreate the Daily presentation from its own matched clips after any library regeneration.
                StationFlow.TrainStation.Social.Editor.StationSlowGaitAuthoring.EnsureResources(library);
                AssetDatabase.SaveAssetIfDirty(library); AssetDatabase.SaveAssetIfDirty(controller);
                result.ikSettingsPreserved = true;
                foreach (var player in players)
                {
                    var animator = player.TargetAnimator;
                    bool changed = player.Library != library || animator.runtimeAnimatorController != controller;
                    if (!changed) continue;
                    var serialized = new SerializedObject(player);
                    serialized.FindProperty("library").objectReferenceValue = library;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    animator.runtimeAnimatorController = controller;
                    Record(player); Record(animator); ++result.playerBindingsChanged;
                    Require(player.Library == library && animator.runtimeAnimatorController == controller, "Player override failed: " + player.name);
                }
                foreach (var actor in actors.Where(a => a.walkId == WalkId))
                {
                    if (actor.walkReferenceSpeed == MatchedReferenceSpeed && actor.walkReferenceHumanScale == MatchedReferenceHumanScale) continue;
                    actor.walkReferenceSpeed = MatchedReferenceSpeed;
                    actor.walkReferenceHumanScale = MatchedReferenceHumanScale;
                    Record(actor); ++result.actorCalibrationsChanged;
                }
                foreach (var settings in playerSettings) settings.AssertPreserved();
                foreach (var settings in actorSettings) settings.AssertPreserved();
                result.stabilizationAndMotionSettingsPreserved = true;
                result.measuredCalibrationApplied = true;
                if (result.playerBindingsChanged > 0 || result.actorCalibrationsChanged > 0)
                { EditorSceneManager.MarkSceneDirty(scene); Require(EditorSceneManager.SaveScene(scene, ScenePath), "Could not save Lab instance overrides."); }
                AssertFingerprints(result.protectedBefore, "Protected source changed during matched-Avatar authoring.");
                result.protectedSourcesPreserved = true;
                result.ownedAfter = Fingerprints(Directory.GetFiles(Root, "*", SearchOption.TopDirectoryOnly).Where(File.Exists));
                result.success = true;
            }
            catch (Exception error) { result.error = error.ToString(); throw; }
            finally
            {
                result.protectedSourcesPreserved = result.protectedBefore.All(f => File.Exists(f.path) && Hash(f.path) == f.sha256);
                Write(ReceiptPath, result);
            }
        }

        static AnimationClip LoadMatchedClip(string path, Avatar expectedAvatar)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            Require(importer && importer.animationType == ModelImporterAnimationType.Human &&
                importer.avatarSetup == ModelImporterAvatarSetup.CopyFromOther && importer.sourceAvatar == expectedAvatar,
                "Matched FBX must retain the reviewed XBot Avatar import configuration: " + path);
            var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal)).ToArray();
            Require(clips.Length == 1 && clips[0].humanMotion && !clips[0].legacy && clips[0].length > 0,
                "Expected exactly one usable matched Humanoid clip: " + path);
            return clips[0];
        }

        static void PrepareMissingMatchedCopy(string sourcePath, string destinationPath, Avatar avatar)
        {
            // Existing copies are never reimported here: LoadMatchedClip verifies their reviewed settings.
            // A fresh Lab copies the source importer settings and changes only the Avatar on its own FBX.
            Require(File.Exists(sourcePath), "Missing source FBX for the isolated matched-Avatar import: " + sourcePath);
            if (File.Exists(destinationPath))
            {
                Require(Hash(destinationPath) == Hash(sourcePath), "Matched FBX bytes differ from the source; preserve and review them: " + destinationPath);
                return;
            }
            var before = Fingerprints(new[] { sourcePath, sourcePath + ".meta", ModelPath, ModelPath + ".meta" });
            EnsureAssetFolder(Path.GetDirectoryName(destinationPath).Replace('\\', '/'));
            Require(AssetDatabase.CopyAsset(sourcePath, destinationPath), "Could not copy source FBX into the isolated Lab: " + destinationPath);
            var importer = AssetImporter.GetAtPath(destinationPath) as ModelImporter;
            Require(importer, "Matched copy has no ModelImporter: " + destinationPath);
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            importer.sourceAvatar = avatar;
            importer.SaveAndReimport();
            Require(Hash(destinationPath) == Hash(sourcePath), "Matched import unexpectedly changed FBX bytes: " + destinationPath);
            AssertFingerprints(before, "A protected source changed while preparing its Lab copy.");
        }

        static void EnsureAssetFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureAssetFolder(parent);
            Require(!string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(path))), "Could not create Lab asset folder: " + path);
        }

        static void AssertMeasuredCalibration(AnimationClip clip)
        {
            var x = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.x"));
            var z = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.z"));
            Require(x != null && z != null && clip.length > 0, "Matched walk RootT curves needed for measured calibration are missing.");
            var advance = new Vector2(x.Evaluate(clip.length) - x.Evaluate(0f), z.Evaluate(clip.length) - z.Evaluate(0f));
            float stride = advance.magnitude * MatchedReferenceHumanScale;
            float speed = stride / clip.length;
            Require(Mathf.Abs(stride - MatchedStrideMetres) < .000001f && Mathf.Abs(speed - MatchedReferenceSpeed) < .000001f,
                "Matched walk RootT no longer agrees with the reviewed measurement; measure again before changing calibration.");
        }

        static IEnumerable<AnimatorState> AllStates(AnimatorController controller) => controller.layers.SelectMany(layer => States(layer.stateMachine));
        static IEnumerable<AnimatorState> States(AnimatorStateMachine machine)
        { foreach (var state in machine.states) yield return state.state; foreach (var child in machine.stateMachines) foreach (var state in States(child.stateMachine)) yield return state; }
        static string[] ProtectedPaths()
        {
            var paths = AssetDatabase.GetDependencies(SourceLibrary, true).Concat(new[] { SourceScene, SourceSchedule, ModelPath, SourceWalk, SourceIdle, MatchedWalk, MatchedIdle })
                .Concat(Directory.GetFiles("Assets/TrainStation/Social/Prefabs", "*.prefab", SearchOption.AllDirectories));
            return paths.SelectMany(p => new[] { p, p + ".meta" }).Where(File.Exists).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }
        static List<Fingerprint> Fingerprints(IEnumerable<string> paths) => paths.OrderBy(p => p, StringComparer.Ordinal).Select(p => new Fingerprint { path = p, sha256 = Hash(p) }).ToList();
        static void AssertFingerprints(IEnumerable<Fingerprint> values, string message)
        { foreach (var value in values) Require(File.Exists(value.path) && Hash(value.path) == value.sha256, message + " " + value.path); }
        static string BackupLabFiles()
        {
            string root = "BuildRequests/Backups/LabMatchedXBot-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var paths = new[] { ScenePath, ScenePath + ".meta", ReceiptPath }
                .Concat(Directory.Exists(Root) ? Directory.GetFiles(Root, "*", SearchOption.AllDirectories) : Array.Empty<string>());
            foreach (string path in paths.Where(File.Exists))
            { string target = root + "/" + path; Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(path, target, false); }
            return root;
        }
        static void Record(UnityEngine.Object value)
        { EditorUtility.SetDirty(value); if (PrefabUtility.IsPartOfPrefabInstance(value)) PrefabUtility.RecordPrefabInstancePropertyModifications(value); }
    }
}
#endif
