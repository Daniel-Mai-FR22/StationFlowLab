#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StationFlow.TrainStation.DecisionLab.Editor
{
    /// <summary>Controlled pose measurements in a private preview scene. Never changes saved animation assets.</summary>
    public static class StationDecisionGaitComparison
    {
        const string ModelPath = "Assets/HumanAnimation/Source/Mixamo/Models/XBot.fbx";
        const string WalkSource = "Assets/HumanAnimation/Source/Mixamo/Animations/male_standard_walking.fbx";
        const string IdleSource = "Assets/HumanAnimation/Source/Mixamo/Animations/Neutral_Idle.fbx";
        const string WalkClip = "Assets/HumanAnimation/Resources/HumanAnimation/Mixamo/Clips/mixamo_male_standard_walking_ef7424170f.anim";
        const string IdleClip = "Assets/HumanAnimation/Resources/HumanAnimation/Mixamo/Clips/mixamo_neutral_idle_bcadd5d2cf.anim";
        const string MatchedDirectory = "Assets/TrainStation/DecisionLab/Animation/MatchedXBot";
        const string MatchedWalk = MatchedDirectory + "/DecisionLab_Walk_XBot.fbx";
        const string MatchedIdle = MatchedDirectory + "/DecisionLab_Idle_XBot.fbx";
        public const string ReportPath = "Reports/TrainStationDecisionLab/gait-controlled-comparison.json";
        public const string MatchedReportPath = "Reports/TrainStationDecisionLab/gait-matched-avatar-comparison.json";
        const int PhaseIntervals = 64;
        static readonly string[] KneeMuscles = { "Left Lower Leg Stretch", "Right Lower Leg Stretch" };
        static readonly string[] ProtectedPaths =
        {
            ModelPath, ModelPath + ".meta", WalkSource, WalkSource + ".meta", IdleSource, IdleSource + ".meta",
            WalkClip, IdleClip, "Assets/TrainStation/Social/Resources/TrainStationSocial/StationSocial.controller",
            "Assets/TrainStation/Social/Resources/TrainStationSocial/StationSocialLibrary.asset",
            "Assets/TrainStation/Social/Prefabs/Station_SoloTraveller.prefab",
            "Assets/TrainStation/Scenes/TrainStationDecisionLab.unity"
        };

        [Serializable] sealed class FileEvidence { public string path, sha256Before, sha256After; }
        [Serializable] sealed class SceneEvidence { public int index, objectsBefore, objectsAfter; public string path; public bool dirtyBefore, dirtyAfter; }
        [Serializable] sealed class CurveEvidence
        {
            public string muscle; public int keyCount, keysAboveOne; public float minimum, maximum;
        }
        [Serializable] sealed class CandidateImport
        {
            public string sourcePath, candidatePath, sourceSha256, candidateSha256, avatarPath, avatarSetup;
            public bool identicalFbxBytes, validHumanMotion, loopTime, loopBlend, loopBlendPositionXZ, loopBlendPositionY;
        }
        [Serializable] sealed class PoseSample
        {
            public float requestedPhase, transitionSeconds, actualNormalizedTime, transitionNormalizedTime;
            public bool inTransition;
            public float leftLowerLegMuscle, rightLowerLegMuscle, maximumAbsoluteMuscle;
            public Vector3 bodyPosition, hips, head, leftHip, leftKnee, leftAnkle, rightHip, rightKnee, rightAnkle;
            public float leftKneeForwardOffset, rightKneeForwardOffset, leftSignedFlexionDegrees, rightSignedFlexionDegrees;
            public float leftAnkleHeight, rightAnkleHeight, maximumAnkleAboveHip, maximumKneeAboveHip;
            public bool skinMeasured;
            public float minimumSkinHeight, approximateLeftSoleHeight, approximateRightSoleHeight;
            public bool footAboveHead;
        }
        [Serializable] sealed class Condition
        {
            public string label, modelPath, avatarPath, clipPath, secondClipPath;
            public bool footIK, secondFootIK, stabilizeFeet;
            public float humanScale, duration, playbackSpeed = 1f, diagnosticMuscleCap = -1f;
            public Vector3 originalBodyRootStart, originalBodyRootEnd;
            public float normalizedRootTravel, sourceStrideMetres, sourceReferenceSpeed;
            public bool rootTranslationCurvesPresent, skinMeasured;
            public float minimumSkinHeight, minimumSupportSoleHeight, maximumSupportSoleHeight;
            public int negativeLeftSamples, negativeRightSamples, footAboveHeadSamples, severeRaisedLegSamples;
            public float minimumLeftForwardOffset, minimumRightForwardOffset, minimumSignedFlexionDegrees;
            public float maximumAnkleAboveHip, maximumKneeAboveHip, maximumAbsoluteMuscle;
            public List<CurveEvidence> kneeCurves = new List<CurveEvidence>();
            public List<PoseSample> samples = new List<PoseSample>();
        }
        [Serializable] sealed class ExtractionComparison
        {
            public string motion; public bool sameSampleCount;
            public float maximumBonePositionDifference, maximumKneeMuscleDifference, maximumSourceCurveDifference;
        }
        [Serializable] sealed class Report
        {
            public string measuredUtc, completedUtc, unityVersion;
            public string method = "Edit mode; private PreviewScene; each isolated phase uses Rebind, Play and Update(0) on a transient model/controller. Imported Humanoid FBX clips are compared with saved extracted clips on XBot, Foot IK off/on. Source-model own-Avatar samples and transient capped-curve candidates are separate diagnostic conditions. Blend sweeps use explicit positive Animator.Update steps. No live actor is sampled or advanced.";
            public string geometry = "Coordinates are relative to the instantiated model root. Forward is +Z; knee offset is knee minus its projection onto hip-to-ankle, dotted with +Z. Signed flexion uses SignedAngle(hip-to-knee,knee-to-ankle,+X). Ankle heights are bone heights above y=0, not measured shoe sole contacts. Severe-raised-leg counter means an ankle or knee exceeds hip height by0.2m; foot-above-head is also recorded independently.";
            public string interpretation = "Measurement completion is not an anatomical PASS. A transient muscle cap is a diagnostic candidate only, never deployed. Imported FBX means Unity's Humanoid conversion, not original raw FBX bone channels.";
            public string soleMeasurement = "CPU BakeMesh samples in model-root coordinates. Per-foot sole estimates use mesh vertices within 0.20m in XZ of the ankle and below ankle+0.04m, matching the previous StationGaitAudit method. This is an approximate mesh clearance measure, not a physics contact test. RootT travel is horizontal endpoint displacement multiplied by the evaluated target Avatar humanScale, divided by clip duration for reference speed.";
            public bool measurementCompleted, savedAssetsPreserved, openScenesPreserved;
            public int conditionCount, sampleCount;
            public List<FileEvidence> assets = new List<FileEvidence>();
            public List<SceneEvidence> scenes = new List<SceneEvidence>();
            public List<Condition> conditions = new List<Condition>();
            public List<ExtractionComparison> extractionComparisons = new List<ExtractionComparison>();
            public List<CandidateImport> candidateImports = new List<CandidateImport>();
            public List<string> errors = new List<string>();
        }

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Measure controlled gait comparison")]
        public static void Measure() => Run(false);

        [MenuItem("Efrei Research Lab/Train Station/Decision Lab/Measure matched XBot Avatar copies")]
        public static void CopyAndMeasureMatchedAvatar() => Run(true);

        static void Run(bool matchedAvatar)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || AnimationMode.InAnimationMode())
                throw new InvalidOperationException("Stop Play and animation previews before the controlled gait comparison.");
            var report = new Report { measuredUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion };
            string outputPath = matchedAvatar ? MatchedReportPath : ReportPath;
            if (matchedAvatar)
            {
                report.method = "Edit mode; private PreviewScene with an active disposable scratch scene. Two isolated Lab FBX copies retain their original bytes/import settings except Avatar setup: CopyFromOther using the matched XBot Avatar. Their Humanoid output is sampled on XBot with Foot IK off/on and walk/idle blend sweeps. No original animation asset or live actor is modified.";
                report.interpretation = "Measurement completion is not anatomical acceptance. Matched copies are diagnostic assets only until signed geometry and live-runtime checks establish their suitability; no saved actor/controller is rebound by this utility.";
            }
            foreach (string path in ProtectedPaths) report.assets.Add(new FileEvidence { path = path, sha256Before = Hash(path) });
            for (int i = 0; i < SceneManager.sceneCount; ++i)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                report.scenes.Add(new SceneEvidence { index = i, path = scene.path, dirtyBefore = scene.isDirty, objectsBefore = Count(scene) });
            }
            Scene previousActive = SceneManager.GetActiveScene();
            Scene preview = default, scratch = default;
            try
            {
                // Editor animation APIs may mark the active scene dirty even when all actors live in a PreviewScene.
                scratch = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.SetActiveScene(scratch);
                preview = EditorSceneManager.NewPreviewScene();
                AnimationClip walk = AssetDatabase.LoadAssetAtPath<AnimationClip>(WalkClip);
                AnimationClip idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(IdleClip);
                AnimationClip sourceWalk = ImportedClip(WalkSource), sourceIdle = ImportedClip(IdleSource);
                if (!walk || !idle || !sourceWalk || !sourceIdle) throw new InvalidOperationException("A requested Humanoid clip is missing.");
                if (matchedAvatar)
                {
                    walk = PrepareMatchedCopy(WalkSource, MatchedWalk, report);
                    idle = PrepareMatchedCopy(IdleSource, MatchedIdle, report);
                    foreach (bool ik in new[] { false, true })
                    {
                        report.conditions.Add(MeasurePhases(preview, ModelPath, walk, MatchedWalk, "walk_matched_xbot_ik_" + ik, ik));
                        report.conditions.Add(MeasurePhases(preview, ModelPath, idle, MatchedIdle, "idle_matched_xbot_ik_" + ik, ik));
                    }
                }
                else
                {
                    MeasureMotion(preview, report, "walk", WalkSource, sourceWalk, WalkClip, walk);
                    MeasureMotion(preview, report, "idle", IdleSource, sourceIdle, IdleClip, idle);
                }
                foreach (float speed in new[] { 1f, 4f })
                {
                    report.conditions.Add(MeasureBlend(preview, walk, idle, true, false, "walk_to_idle_" + speed, speed));
                    report.conditions.Add(MeasureBlend(preview, idle, walk, false, true, "idle_to_walk_" + speed, speed));
                }
                report.measurementCompleted = true;
            }
            catch (Exception error) { report.errors.Add(error.ToString()); }
            finally
            {
                if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
                if (previousActive.IsValid() && previousActive.isLoaded) SceneManager.SetActiveScene(previousActive);
                if (scratch.IsValid()) EditorSceneManager.CloseScene(scratch, true);
                report.savedAssetsPreserved = true;
                foreach (var file in report.assets)
                { file.sha256After = Hash(file.path); report.savedAssetsPreserved &= file.sha256After == file.sha256Before; }
                report.openScenesPreserved = SceneManager.sceneCount == report.scenes.Count;
                foreach (var saved in report.scenes)
                {
                    Scene scene = saved.index < SceneManager.sceneCount ? SceneManager.GetSceneAt(saved.index) : default;
                    saved.objectsAfter = Count(scene); saved.dirtyAfter = scene.IsValid() && scene.isDirty;
                    report.openScenesPreserved &= scene.IsValid() && scene.isLoaded && scene.path == saved.path &&
                        saved.objectsBefore == saved.objectsAfter && saved.dirtyBefore == saved.dirtyAfter;
                }
                report.completedUtc = DateTime.UtcNow.ToString("O");
                report.conditionCount = report.conditions.Count;
                report.sampleCount = report.conditions.Sum(c => c.samples.Count);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                File.WriteAllText(outputPath, JsonUtility.ToJson(report, true));
            }
            if (!report.measurementCompleted || !report.savedAssetsPreserved || !report.openScenesPreserved)
                throw new InvalidOperationException("Controlled gait comparison did not complete or preserve its inputs. See " + outputPath);
            Debug.Log("Controlled gait measurements completed: " + report.conditionCount + " conditions / " + report.sampleCount + " samples. " + outputPath);
        }

        static AnimationClip PrepareMatchedCopy(string sourcePath, string destinationPath, Report report)
        {
            if (!destinationPath.StartsWith(MatchedDirectory + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("Candidate import must remain inside the Lab's isolated animation directory.");
            EnsureAssetFolder(MatchedDirectory);
            if (!File.Exists(destinationPath))
            {
                if (!AssetDatabase.CopyAsset(sourcePath, destinationPath))
                    throw new InvalidOperationException("Could not copy source FBX to diagnostic candidate: " + destinationPath);
            }
            if (Hash(destinationPath) != Hash(sourcePath))
                throw new InvalidOperationException("Existing candidate FBX differs from its recorded source; refusing to replace it: " + destinationPath);
            Avatar xbotAvatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().FirstOrDefault(a => a.isValid && a.isHuman);
            var importer = AssetImporter.GetAtPath(destinationPath) as ModelImporter;
            if (!xbotAvatar || importer == null) throw new InvalidOperationException("Missing XBot Avatar or candidate ModelImporter.");
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            importer.sourceAvatar = xbotAvatar;
            importer.SaveAndReimport();
            AnimationClip clip = ImportedClip(destinationPath);
            if (!clip) throw new InvalidOperationException("Matched Avatar copy produced no Humanoid clip: " + destinationPath);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            report.candidateImports.Add(new CandidateImport
            {
                sourcePath = sourcePath, candidatePath = destinationPath, sourceSha256 = Hash(sourcePath), candidateSha256 = Hash(destinationPath),
                identicalFbxBytes = Hash(sourcePath) == Hash(destinationPath), avatarPath = AssetDatabase.GetAssetPath(importer.sourceAvatar),
                avatarSetup = importer.avatarSetup.ToString(), validHumanMotion = clip.humanMotion,
                loopTime = settings.loopTime, loopBlend = settings.loopBlend,
                loopBlendPositionXZ = settings.loopBlendPositionXZ, loopBlendPositionY = settings.loopBlendPositionY
            });
            return clip;
        }

        static void EnsureAssetFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static void MeasureMotion(Scene scene, Report report, string name, string sourcePath, AnimationClip source, string extractedPath, AnimationClip extracted)
        {
            foreach (bool ik in new[] { false, true })
            {
                Condition a = MeasurePhases(scene, ModelPath, source, sourcePath, name + "_imported_ik_" + ik, ik);
                Condition b = MeasurePhases(scene, ModelPath, extracted, extractedPath, name + "_extracted_ik_" + ik, ik);
                report.conditions.Add(a); report.conditions.Add(b);
                report.extractionComparisons.Add(Compare(name + "_ik_" + ik, a, b, source, extracted));
            }
            report.conditions.Add(MeasurePhases(scene, sourcePath, source, sourcePath, name + "_source_own_avatar_ik_false", false));
            foreach (float cap in new[] { 1f, .95f })
            {
                AnimationClip temporary = CappedCopy(extracted, cap);
                try
                {
                    foreach (bool ik in new[] { false, true })
                    {
                        Condition c = MeasurePhases(scene, ModelPath, temporary, extractedPath, name + "_transient_cap_" + cap + "_ik_" + ik, ik);
                        c.diagnosticMuscleCap = cap; report.conditions.Add(c);
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(temporary); }
            }
        }

        sealed class Rig : IDisposable
        {
            public sealed class SkinSample { public SkinnedMeshRenderer renderer; public Mesh mesh; public readonly List<Vector3> vertices = new List<Vector3>(); }
            public GameObject actor;
            public Animator animator;
            public HumanPoseHandler handler;
            public readonly List<SkinSample> skins = new List<SkinSample>();
            AnimatorController controller;
            AnimatorStateMachine machine;
            AnimatorState first, second;
            public readonly int firstHash = Animator.StringToHash("Base Layer.First");
            public readonly int secondHash = Animator.StringToHash("Base Layer.Second");
            public Rig(Scene scene, string modelPath, AnimationClip a, bool ikA, AnimationClip b = null, bool ikB = false)
            {
                try
                {
                    GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                    if (!model) throw new InvalidOperationException("Missing model: " + modelPath);
                    actor = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                    actor.hideFlags = HideFlags.HideAndDontSave;
                    actor.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    animator = actor.GetComponentInChildren<Animator>(true);
                    if (!animator || !animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman)
                        throw new InvalidOperationException("Model has no valid own Humanoid Avatar: " + modelPath);
                    foreach (var behavior in actor.GetComponentsInChildren<MonoBehaviour>(true)) behavior.enabled = false;
                    animator.enabled = true; animator.applyRootMotion = false; animator.stabilizeFeet = true;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.updateMode = AnimatorUpdateMode.Normal;
                    controller = new AnimatorController { name = "Transient gait comparison", hideFlags = HideFlags.HideAndDontSave };
                    machine = new AnimatorStateMachine { name = "Base Layer", hideFlags = HideFlags.HideAndDontSave };
                    first = machine.AddState("First"); first.hideFlags = HideFlags.HideAndDontSave; first.motion = a;
                    first.iKOnFeet = ikA; first.writeDefaultValues = true; machine.defaultState = first;
                    if (b)
                    {
                        second = machine.AddState("Second"); second.hideFlags = HideFlags.HideAndDontSave;
                        second.motion = b; second.iKOnFeet = ikB; second.writeDefaultValues = true;
                    }
                    controller.layers = new[] { new AnimatorControllerLayer { name = "Base Layer", defaultWeight = 1f, stateMachine = machine, iKPass = true } };
                    animator.runtimeAnimatorController = controller;
                    animator.speed = 1f; animator.Rebind(); animator.Update(0f);
                    handler = new HumanPoseHandler(animator.avatar, animator.transform);
                    foreach (var skin in actor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        if (skin.sharedMesh) skins.Add(new SkinSample { renderer = skin,
                            mesh = new Mesh { name = "Transient gait CPU skin", hideFlags = HideFlags.HideAndDontSave } });
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                handler?.Dispose(); handler = null;
                foreach (var skin in skins) if (skin.mesh) UnityEngine.Object.DestroyImmediate(skin.mesh);
                skins.Clear();
                if (actor) UnityEngine.Object.DestroyImmediate(actor);
                if (controller) UnityEngine.Object.DestroyImmediate(controller);
                if (first) UnityEngine.Object.DestroyImmediate(first);
                if (second) UnityEngine.Object.DestroyImmediate(second);
                if (machine) UnityEngine.Object.DestroyImmediate(machine);
            }
        }

        static Condition MeasurePhases(Scene scene, string model, AnimationClip clip, string path, string label, bool ik)
        {
            using (var rig = new Rig(scene, model, clip, ik))
            {
                Condition result = Describe(rig, model, clip, path, label, ik);
                for (int i = 0; i <= PhaseIntervals; ++i)
                {
                    float phase = i / (float)PhaseIntervals;
                    rig.animator.Rebind(); rig.animator.Play(rig.firstHash, 0, phase); rig.animator.Update(0f);
                    if (rig.animator.GetCurrentAnimatorStateInfo(0).fullPathHash != rig.firstHash)
                        throw new InvalidOperationException("Transient Animator did not evaluate the requested clip.");
                    result.samples.Add(Sample(rig, phase, 0f));
                }
                Summarize(result); return result;
            }
        }

        static Condition MeasureBlend(Scene scene, AnimationClip a, AnimationClip b, bool ikA, bool ikB, string label, float speed)
        {
            using (var rig = new Rig(scene, ModelPath, a, ikA, b, ikB))
            {
                Condition result = Describe(rig, ModelPath, a, AssetDatabase.GetAssetPath(a), label, ikA);
                result.secondClipPath = AssetDatabase.GetAssetPath(b); result.secondFootIK = ikB; result.playbackSpeed = speed;
                for (int phase = 0; phase < 8; ++phase)
                {
                    rig.animator.speed = speed; rig.animator.Rebind(); rig.animator.Play(rig.firstHash, 0, phase / 8f); rig.animator.Update(0f);
                    rig.animator.CrossFadeInFixedTime(rig.secondHash, .18f, 0, 0f);
                    for (int step = 0; step < 20; ++step)
                    {
                        rig.animator.Update(.015f);
                        result.samples.Add(Sample(rig, phase / 8f, (step + 1) * .015f));
                    }
                }
                Summarize(result); return result;
            }
        }

        static Condition Describe(Rig rig, string model, AnimationClip clip, string path, string label, bool ik)
        {
            var result = new Condition { label = label, modelPath = model, avatarPath = AssetDatabase.GetAssetPath(rig.animator.avatar),
                clipPath = path, footIK = ik, stabilizeFeet = rig.animator.stabilizeFeet, humanScale = rig.animator.humanScale, duration = clip.length };
            var rootX = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.x"));
            var rootY = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.y"));
            var rootZ = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.z"));
            result.rootTranslationCurvesPresent = rootX != null && rootY != null && rootZ != null;
            if (result.rootTranslationCurvesPresent)
            {
                result.originalBodyRootStart = new Vector3(rootX.Evaluate(0f), rootY.Evaluate(0f), rootZ.Evaluate(0f));
                result.originalBodyRootEnd = new Vector3(rootX.Evaluate(clip.length), rootY.Evaluate(clip.length), rootZ.Evaluate(clip.length));
                Vector3 advance = result.originalBodyRootEnd - result.originalBodyRootStart; advance.y = 0f;
                result.normalizedRootTravel = advance.magnitude;
                result.sourceStrideMetres = result.normalizedRootTravel * result.humanScale;
                result.sourceReferenceSpeed = result.sourceStrideMetres / Mathf.Max(.001f, clip.length);
            }
            foreach (string muscle in KneeMuscles)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), muscle));
                if (curve == null || curve.length == 0) throw new InvalidOperationException("Missing muscle curve: " + muscle + " / " + label);
                result.kneeCurves.Add(new CurveEvidence { muscle = muscle, keyCount = curve.length, keysAboveOne = curve.keys.Count(k => k.value > 1f),
                    minimum = curve.keys.Min(k => k.value), maximum = curve.keys.Max(k => k.value) });
            }
            return result;
        }

        static PoseSample Sample(Rig rig, float phase, float transitionSeconds)
        {
            Vector3 Point(HumanBodyBones bone)
            {
                Transform t = rig.animator.GetBoneTransform(bone);
                if (!t) throw new InvalidOperationException("Missing bone: " + bone);
                return rig.actor.transform.InverseTransformPoint(t.position);
            }
            var pose = new HumanPose(); rig.handler.GetHumanPose(ref pose);
            if (pose.muscles == null || pose.muscles.Any(v => !Finite(v))) throw new InvalidOperationException("Non-finite Humanoid muscle pose.");
            var result = new PoseSample
            {
                requestedPhase = phase, transitionSeconds = transitionSeconds,
                actualNormalizedTime = rig.animator.GetCurrentAnimatorStateInfo(0).normalizedTime,
                inTransition = rig.animator.IsInTransition(0), transitionNormalizedTime = rig.animator.GetAnimatorTransitionInfo(0).normalizedTime,
                bodyPosition = pose.bodyPosition,
                leftLowerLegMuscle = pose.muscles[Array.IndexOf(HumanTrait.MuscleName, KneeMuscles[0])],
                rightLowerLegMuscle = pose.muscles[Array.IndexOf(HumanTrait.MuscleName, KneeMuscles[1])],
                maximumAbsoluteMuscle = pose.muscles.Max(v => Mathf.Abs(v)),
                hips = Point(HumanBodyBones.Hips), head = Point(HumanBodyBones.Head),
                leftHip = Point(HumanBodyBones.LeftUpperLeg), leftKnee = Point(HumanBodyBones.LeftLowerLeg), leftAnkle = Point(HumanBodyBones.LeftFoot),
                rightHip = Point(HumanBodyBones.RightUpperLeg), rightKnee = Point(HumanBodyBones.RightLowerLeg), rightAnkle = Point(HumanBodyBones.RightFoot)
            };
            result.leftKneeForwardOffset = KneeOffset(result.leftHip, result.leftKnee, result.leftAnkle);
            result.rightKneeForwardOffset = KneeOffset(result.rightHip, result.rightKnee, result.rightAnkle);
            result.leftSignedFlexionDegrees = Vector3.SignedAngle(result.leftKnee - result.leftHip, result.leftAnkle - result.leftKnee, Vector3.right);
            result.rightSignedFlexionDegrees = Vector3.SignedAngle(result.rightKnee - result.rightHip, result.rightAnkle - result.rightKnee, Vector3.right);
            result.leftAnkleHeight = result.leftAnkle.y; result.rightAnkleHeight = result.rightAnkle.y;
            result.maximumAnkleAboveHip = Mathf.Max(result.leftAnkle.y - result.leftHip.y, result.rightAnkle.y - result.rightHip.y);
            result.maximumKneeAboveHip = Mathf.Max(result.leftKnee.y - result.leftHip.y, result.rightKnee.y - result.rightHip.y);
            result.footAboveHead = Mathf.Max(result.leftAnkle.y, result.rightAnkle.y) > result.head.y;
            MeasureSkin(rig, result);
            if (!Finite(result.leftKneeForwardOffset) || !Finite(result.rightKneeForwardOffset) || !Finite(result.maximumAnkleAboveHip))
                throw new InvalidOperationException("Non-finite sampled bone geometry.");
            return result;
        }

        static void MeasureSkin(Rig rig, PoseSample sample)
        {
            if (rig.skins.Count == 0) return;
            float low = float.PositiveInfinity, left = float.PositiveInfinity, right = float.PositiveInfinity;
            foreach (var skin in rig.skins)
            {
                skin.renderer.BakeMesh(skin.mesh, false);
                skin.mesh.GetVertices(skin.vertices);
                foreach (Vector3 vertex in skin.vertices)
                {
                    Vector3 point = rig.actor.transform.InverseTransformPoint(skin.renderer.transform.TransformPoint(vertex));
                    low = Mathf.Min(low, point.y);
                    if (NearFoot(point, sample.leftAnkle)) left = Mathf.Min(left, point.y);
                    if (NearFoot(point, sample.rightAnkle)) right = Mathf.Min(right, point.y);
                }
            }
            if (!Finite(low)) return;
            sample.skinMeasured = true; sample.minimumSkinHeight = low;
            sample.approximateLeftSoleHeight = Finite(left) ? left : sample.leftAnkle.y;
            sample.approximateRightSoleHeight = Finite(right) ? right : sample.rightAnkle.y;
        }

        static bool NearFoot(Vector3 point, Vector3 ankle) => point.y <= ankle.y + .04f &&
            new Vector2(point.x - ankle.x, point.z - ankle.z).sqrMagnitude < .2f * .2f;

        static float KneeOffset(Vector3 hip, Vector3 knee, Vector3 ankle)
        {
            Vector3 axis = ankle - hip;
            Vector3 closest = hip + axis * (Vector3.Dot(knee - hip, axis) / Mathf.Max(1e-10f, axis.sqrMagnitude));
            return Vector3.Dot(knee - closest, Vector3.forward);
        }

        static void Summarize(Condition c)
        {
            c.minimumLeftForwardOffset = c.samples.Min(s => s.leftKneeForwardOffset);
            c.minimumRightForwardOffset = c.samples.Min(s => s.rightKneeForwardOffset);
            c.minimumSignedFlexionDegrees = c.samples.Min(s => Mathf.Min(s.leftSignedFlexionDegrees, s.rightSignedFlexionDegrees));
            c.maximumAnkleAboveHip = c.samples.Max(s => s.maximumAnkleAboveHip);
            c.maximumKneeAboveHip = c.samples.Max(s => s.maximumKneeAboveHip);
            c.maximumAbsoluteMuscle = c.samples.Max(s => s.maximumAbsoluteMuscle);
            c.negativeLeftSamples = c.samples.Count(s => s.leftKneeForwardOffset < -.001f);
            c.negativeRightSamples = c.samples.Count(s => s.rightKneeForwardOffset < -.001f);
            c.footAboveHeadSamples = c.samples.Count(s => s.footAboveHead);
            c.severeRaisedLegSamples = c.samples.Count(s => s.maximumAnkleAboveHip > .2f || s.maximumKneeAboveHip > .2f);
            c.skinMeasured = c.samples.All(s => s.skinMeasured);
            if (c.skinMeasured)
            {
                c.minimumSkinHeight = c.samples.Min(s => s.minimumSkinHeight);
                c.minimumSupportSoleHeight = c.samples.Min(s => Mathf.Min(s.approximateLeftSoleHeight, s.approximateRightSoleHeight));
                c.maximumSupportSoleHeight = c.samples.Max(s => Mathf.Min(s.approximateLeftSoleHeight, s.approximateRightSoleHeight));
            }
        }

        static ExtractionComparison Compare(string motion, Condition a, Condition b, AnimationClip source, AnimationClip extracted)
        {
            var result = new ExtractionComparison { motion = motion, sameSampleCount = a.samples.Count == b.samples.Count };
            for (int i = 0; i < Mathf.Min(a.samples.Count, b.samples.Count); ++i)
            {
                PoseSample x = a.samples[i], y = b.samples[i];
                Vector3[] p = { x.hips, x.leftHip, x.leftKnee, x.leftAnkle, x.rightHip, x.rightKnee, x.rightAnkle };
                Vector3[] q = { y.hips, y.leftHip, y.leftKnee, y.leftAnkle, y.rightHip, y.rightKnee, y.rightAnkle };
                for (int k = 0; k < p.Length; ++k) result.maximumBonePositionDifference = Mathf.Max(result.maximumBonePositionDifference, Vector3.Distance(p[k], q[k]));
                result.maximumKneeMuscleDifference = Mathf.Max(result.maximumKneeMuscleDifference,
                    Mathf.Abs(x.leftLowerLegMuscle - y.leftLowerLegMuscle), Mathf.Abs(x.rightLowerLegMuscle - y.rightLowerLegMuscle));
            }
            foreach (string muscle in KneeMuscles)
            {
                var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), muscle);
                AnimationCurve x = AnimationUtility.GetEditorCurve(source, binding), y = AnimationUtility.GetEditorCurve(extracted, binding);
                for (int i = 0; i <= 256; ++i)
                    result.maximumSourceCurveDifference = Mathf.Max(result.maximumSourceCurveDifference, Mathf.Abs(x.Evaluate(source.length * i / 256f) - y.Evaluate(extracted.length * i / 256f)));
            }
            return result;
        }

        static AnimationClip CappedCopy(AnimationClip source, float cap)
        {
            AnimationClip copy = UnityEngine.Object.Instantiate(source);
            copy.name = "Transient knee cap " + cap; copy.hideFlags = HideFlags.HideAndDontSave;
            foreach (string muscle in KneeMuscles)
            {
                var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), muscle);
                AnimationCurve original = AnimationUtility.GetEditorCurve(source, binding);
                int intervals = Mathf.Max(2, Mathf.CeilToInt(source.length * 120f));
                var keys = new Keyframe[intervals + 1];
                for (int i = 0; i <= intervals; ++i)
                { float t = source.length * i / intervals; keys[i] = new Keyframe(t, Mathf.Min(cap, original.Evaluate(t))); }
                var curve = new AnimationCurve(keys) { preWrapMode = original.preWrapMode, postWrapMode = original.postWrapMode };
                for (int i = 0; i < curve.length; ++i)
                { AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear); AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear); }
                AnimationUtility.SetEditorCurve(copy, binding, curve);
            }
            return copy;
        }

        static AnimationClip ImportedClip(string path) => AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
            .FirstOrDefault(c => c.humanMotion && !c.name.StartsWith("__preview__", StringComparison.Ordinal));
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static int Count(Scene scene) => scene.IsValid() && scene.isLoaded ? scene.GetRootGameObjects().Sum(r => r.GetComponentsInChildren<Transform>(true).Length) : -1;
        static string Hash(string path)
        {
            using (var algorithm = SHA256.Create()) using (var stream = File.OpenRead(path))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
#endif
