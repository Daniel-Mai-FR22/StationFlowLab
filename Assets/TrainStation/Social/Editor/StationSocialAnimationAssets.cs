#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using StationFlow.HumanAnimation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace StationFlow.TrainStation.Social.Editor
{
    public static class StationSocialAnimationAssets
    {
        public const string ResourceDirectory = "Assets/TrainStation/Social/Resources/TrainStationSocial";
        public const string LibraryPath = ResourceDirectory + "/StationSocialLibrary.asset";
        public const string SweepId = "station_sweep";

        public static HumanAnimationLibrary EnsureResources()
        {
            var source = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>("Assets/HumanAnimation/Resources/HumanAnimation/Mixamo/MixamoLibrary.asset");
            if (!source || source.Validate(true).Count != 0) throw new InvalidOperationException("Import and validate the Mixamo library before authoring station actors.");
            EnsureFolder(ResourceDirectory + "/Clips");
            string clipPath = ResourceDirectory + "/Clips/Station_Sweeping.anim";
            var sweep = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (!sweep)
            {
                sweep = new AnimationClip { name = "Station Sweeping — original Humanoid loop", frameRate = 30, wrapMode = WrapMode.Loop };
                foreach (string muscle in HumanTrait.MuscleName) Curve(sweep, muscle, AnimationCurve.Constant(0, 2.4f, 0));
                Curve(sweep, "Spine Front-Back", Cycle(.12f, .18f));
                Curve(sweep, "Chest Front-Back", AnimationCurve.Constant(0, 2.4f, .09f));
                Curve(sweep, "Spine Twist Left-Right", Cycle(-.13f, .13f));
                Curve(sweep, "Chest Twist Left-Right", Cycle(-.08f, .08f));
                Curve(sweep, "Head Nod Down-Up", AnimationCurve.Constant(0, 2.4f, -.12f));
                Curve(sweep, "Head Turn Left-Right", Cycle(-.10f, .10f));
                foreach (string side in new[] { "Left", "Right" })
                {
                    Curve(sweep, side + " Arm Down-Up", AnimationCurve.Constant(0, 2.4f, -.64f));
                    Curve(sweep, side + " Arm Front-Back", Cycle(.32f, .46f));
                    Curve(sweep, side + " Forearm Stretch", AnimationCurve.Constant(0, 2.4f, -.48f));
                    foreach (string digit in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                        for (int joint = 1; joint <= 3; joint++)
                            Curve(sweep, side + " " + digit + " " + joint + " Stretched", AnimationCurve.Constant(0, 2.4f, -.60f));
                }
                var settings = AnimationUtility.GetAnimationClipSettings(sweep);
                settings.loopTime = true; settings.loopBlend = true;
                settings.keepOriginalPositionY = true; settings.keepOriginalPositionXZ = true;
                settings.keepOriginalOrientation = true;
                AnimationUtility.SetAnimationClipSettings(sweep, settings);
                AssetDatabase.CreateAsset(sweep, clipPath);
            }
            EnsureStandingBodyChannels(sweep, source);
            var library = AssetDatabase.LoadAssetAtPath<HumanAnimationLibrary>(LibraryPath);
            if (!library)
            {
                library = ScriptableObject.CreateInstance<HumanAnimationLibrary>();
                library.name = "TrainStation social motions / Mixamo + original cleaning";
                AssetDatabase.CreateAsset(library, LibraryPath);
            }
            string controllerPath = ResourceDirectory + "/StationSocial.controller";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (!controller) controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            var motions = source.motions.Select(m => new HumanMotion { id = m.id, displayName = m.displayName,
                stateName = "Base Layer." + m.id, sourceAssetPath = m.sourceAssetPath, clip = m.clip,
                loop = m.loop, blendIn = m.blendIn, source = m.source, category = m.category }).ToList();
            motions.Add(new HumanMotion { id = SweepId, displayName = "Sweeping / original station animation",
                stateName = "Base Layer." + SweepId, sourceAssetPath = clipPath, clip = sweep, loop = true,
                blendIn = .25f, source = HumanMotionSource.Authored, category = HumanMotionCategory.Interaction });
            var machine = controller.layers[0].stateMachine;
            var ready = machine.states.Select(s => s.state).FirstOrDefault(s => s.name == "Ready") ?? machine.AddState("Ready");
            machine.defaultState = ready;
            foreach (var motion in motions)
            {
                var state = machine.states.Select(s => s.state).FirstOrDefault(s => s.name == motion.id)
                    ?? machine.AddState(motion.id, new Vector3(280, 60 + machine.states.Length * 60));
                state.motion = motion.clip; state.writeDefaultValues = true;
                state.iKOnFeet = StationFlow.HumanAnimation.Editor.HumanAnimationAuthoring.UsesFootIK(motion);
            }
            // The cleaner's tool grip uses this layer's Animator IK callback; other roles leave IK weights at zero.
            var layers = controller.layers; layers[0].iKPass = true; controller.layers = layers;
            library.motions = motions.ToArray(); library.controller = controller; library.upperBodyMask = source.upperBodyMask;
            EditorUtility.SetDirty(library); EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            var errors = library.Validate(true);
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
            return library;
        }
        static void Curve(AnimationClip clip, string muscle, AnimationCurve curve)
        {
            if (!HumanTrait.MuscleName.Contains(muscle)) throw new InvalidOperationException("Unknown Humanoid muscle: " + muscle);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), muscle), curve);
        }
        static void EnsureStandingBodyChannels(AnimationClip clip, HumanAnimationLibrary source)
        {
            // Humanoid body translation is separate from the muscle channels. Without RootT.y,
            // Write Defaults places the centre of mass at the floor when entering this state.
            var neutral = source.motions.First(m => m.displayName == "Neutral Idle").clip;
            var heightBinding = EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.y");
            var neutralHeight = AnimationUtility.GetEditorCurve(neutral, heightBinding);
            if (neutralHeight == null) throw new InvalidOperationException("Neutral Idle has no Humanoid body-height reference.");
            // Zero is the midpoint of each Humanoid muscle's range, not a standing pose.
            // Preserve neutral leg/ankle angles while the authored upper body sweeps.
            foreach (string muscle in HumanTrait.MuscleName.Where(n => n.Contains("Leg") || n.Contains("Foot") || n.Contains("Toes")))
            {
                var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), muscle);
                var reference = AnimationUtility.GetEditorCurve(neutral, binding);
                if (reference == null) throw new InvalidOperationException("Neutral Idle lacks standing muscle " + muscle);
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 2.4f, reference.Evaluate(0)));
            }
            string[] channels = { "RootT.x", "RootT.y", "RootT.z", "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
            foreach (string channel in channels)
            {
                var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), channel);
                if (AnimationUtility.GetEditorCurve(clip, binding) != null) continue;
                float value = channel == "RootT.y" ? neutralHeight.Evaluate(0) : channel == "RootQ.w" ? 1 : 0;
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 2.4f, value));
            }
            EditorUtility.SetDirty(clip);
        }
        static AnimationCurve Cycle(float a, float b) => new AnimationCurve(new Keyframe(0, a), new Keyframe(1.2f, b), new Keyframe(2.4f, a));
        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/'); EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
#endif
