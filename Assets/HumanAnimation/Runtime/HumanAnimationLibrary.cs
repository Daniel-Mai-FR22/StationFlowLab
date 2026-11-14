using System;
using System.Collections.Generic;
using UnityEngine;

namespace StationFlow.HumanAnimation
{
    public enum HumanMotionCategory { Locomotion, Posture, Gesture, Interaction, Diagnostic }
    public enum HumanMotionSource { Mixamo, Diagnostic, Authored }

    [Serializable]
    public sealed class HumanMotionBlendChild
    {
        public AnimationClip clip;
        public float threshold;
    }

    [Serializable]
    public sealed class HumanMotion
    {
        public string id;
        public string displayName;
        [Tooltip("Full Animator state path, for example Base Layer.walk.")]
        public string stateName;
        [Tooltip("Original imported FBX or authored animation asset path. Kept for provenance.")]
        public string sourceAssetPath;
        [Tooltip("Single clip, or the full-stride reference clip and cycle duration of a declared blend tree.")]
        public AnimationClip clip;
        [Tooltip("Empty for a single clip. Otherwise the saved one-dimensional Animator blend parameter.")]
        public string blendParameter;
        public HumanMotionBlendChild[] blendChildren = Array.Empty<HumanMotionBlendChild>();
        public bool loop;
        [Min(0f)] public float blendIn = .15f;
        public HumanMotionSource source;
        public HumanMotionCategory category;

        public bool IsAvailable => clip != null;
        public string Label => string.IsNullOrWhiteSpace(displayName) ? id : displayName;
        public string StatePath => string.IsNullOrWhiteSpace(stateName) ? "Base Layer." + id : stateName;
    }

    /// <summary>A shared catalog; every character keeps its own imported Humanoid Avatar.</summary>
    [CreateAssetMenu(fileName = "HumanAnimationLibrary", menuName = "StationFlow/Human animation/Library")]
    public sealed class HumanAnimationLibrary : ScriptableObject
    {
        public RuntimeAnimatorController controller;
        public AvatarMask upperBodyMask;
        public HumanMotion[] motions = Array.Empty<HumanMotion>();

        public HumanMotion Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || motions == null) return null;
            foreach (var motion in motions)
                if (motion != null && string.Equals(motion.id, id, StringComparison.Ordinal)) return motion;
            return null;
        }

        /// <summary>
        /// Missing clips are permitted catalog placeholders unless requireAllClips is true.
        /// Callers can display availability without pretending an unimported Mixamo motion works.
        /// </summary>
        public List<string> Validate(bool requireAllClips = false)
        {
            var issues = new List<string>();
            if (!controller) issues.Add("The shared Animator Controller is missing.");
            if (motions == null || motions.Length == 0)
            {
                issues.Add("The animation catalog is empty.");
                return issues;
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < motions.Length; i++)
            {
                var motion = motions[i];
                if (motion == null) { issues.Add($"Catalog entry {i} is null."); continue; }
                if (string.IsNullOrWhiteSpace(motion.id)) issues.Add($"Catalog entry {i} has no ID.");
                else if (!ids.Add(motion.id)) issues.Add($"Duplicate motion ID: {motion.id}.");
                if (!paths.Add(motion.StatePath)) issues.Add($"Duplicate Animator state: {motion.StatePath}.");
                if (float.IsNaN(motion.blendIn) || float.IsInfinity(motion.blendIn) || motion.blendIn < 0f)
                    issues.Add($"{motion.id}: blend duration must be a finite nonnegative number.");
                if (!motion.clip)
                {
                    if (requireAllClips) issues.Add($"{motion.id}: {motion.source} clip has not been imported.");
                    continue;
                }
                if (motion.clip.legacy) issues.Add($"{motion.id}: Legacy clips cannot use the Humanoid controller.");
                if (!motion.clip.humanMotion) issues.Add($"{motion.id}: clip has no Humanoid muscle animation.");
                if (motion.clip.isLooping != motion.loop)
                    issues.Add($"{motion.id}: catalog loop setting differs from the imported clip.");
                if (!string.IsNullOrEmpty(motion.blendParameter))
                {
                    if (motion.blendChildren == null || motion.blendChildren.Length < 2)
                        issues.Add($"{motion.id}: a blend must declare at least two source clips.");
                    else foreach (var child in motion.blendChildren)
                    {
                        if (child == null || !child.clip || child.clip.legacy || !child.clip.humanMotion)
                            issues.Add($"{motion.id}: invalid Humanoid blend child.");
                        else if (Mathf.Abs(child.clip.length - motion.clip.length) > .001f)
                            issues.Add($"{motion.id}: blend children must share the reference cycle duration.");
                    }
                }
            }
            return issues;
        }
    }
}
