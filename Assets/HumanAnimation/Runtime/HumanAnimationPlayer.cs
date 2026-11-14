using UnityEngine;

namespace StationFlow.HumanAnimation
{
    /// <summary>
    /// Explicit presentation playback only. This component has no navigation, scenario logic,
    /// global startup hook, or character creation. A child Animator is supported.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HumanAnimationPlayer : MonoBehaviour
    {
        [SerializeField] Animator animator;
        [SerializeField] HumanAnimationLibrary library;
        [SerializeField, Tooltip("Off for in-place reuse. Enable only when the caller owns motion handling.")]
        bool applyRootMotion;
        [SerializeField, Tooltip("Stabilize Humanoid feet while blending between saved animation states.")]
        bool stabilizeFeet = true;
        [SerializeField, Range(.05f, 4f)] float playbackSpeed = 1f;

        string currentMotionId;
        bool paused;

        public Animator TargetAnimator => animator;
        public HumanAnimationLibrary Library => library;
        public string CurrentMotionId => currentMotionId;
        public bool IsPaused => paused;
        public float PlaybackSpeed => playbackSpeed;
        public bool ApplyRootMotion => applyRootMotion;
        public bool StabilizeFeet => stabilizeFeet;
        public string LastError { get; private set; } = "";

        /// <summary>Authoring/assembly entry point. Never replaces the model's own Avatar.</summary>
        public void Configure(Animator targetAnimator, HumanAnimationLibrary animationLibrary)
        {
            animator = targetAnimator;
            library = animationLibrary;
            ApplyConfiguration();
        }

        void Awake()
        {
            if (!animator) animator = GetComponentInChildren<Animator>(true);
            ApplyConfiguration();
        }

        void ApplyConfiguration()
        {
            if (!animator) return;
            if (library && library.controller && animator.runtimeAnimatorController != library.controller)
                animator.runtimeAnimatorController = library.controller;
            // Reconfiguring root motion can reinitialize an Animator. A clip change
            // must not repeatedly reset its native configuration or IK state.
            if (animator.applyRootMotion != applyRootMotion) animator.applyRootMotion = applyRootMotion;
            if (animator.stabilizeFeet != stabilizeFeet) animator.stabilizeFeet = stabilizeFeet;
            animator.speed = paused ? 0f : playbackSpeed;
        }

        /// <summary>Checks model and references without mutating or starting playback.</summary>
        public bool ValidateSetup(out string error)
        {
            if (!animator) { error = "Assign this model's Animator (a child Animator is supported)."; return false; }
            if (!library) { error = "Assign a HumanAnimationLibrary asset."; return false; }
            if (!library.controller) { error = "The library has no saved Animator Controller."; return false; }
            if (!animator.avatar) { error = "The model has no Avatar. Import its rig as Humanoid and create an Avatar from this model."; return false; }
            if (!animator.avatar.isValid || !animator.avatar.isHuman)
            { error = "The model must use its own valid Humanoid Avatar. Check Rig > Configure in its importer."; return false; }
            error = "";
            return true;
        }

        /// <summary>Play a catalog ID using its saved controller state; no implicit fallback clips.</summary>
        public bool Play(string id, float normalizedTime = 0f)
        {
            if (!Application.isPlaying) return Fail("Enter Play mode to use the playback controls.");
            if (!ValidateSetup(out var error)) return Fail(error);
            if (!animator.enabled || !animator.gameObject.activeInHierarchy)
                return Fail("The selected model's Animator must be enabled and active.");
            var motion = library.Find(id);
            if (motion == null) return Fail($"Motion ID '{id}' is not in the assigned library.");
            if (!motion.clip)
                return Fail($"{motion.Label}: the {motion.source} source clip has not been imported. No replacement is played.");
            if (motion.clip.legacy || !motion.clip.humanMotion)
                return Fail($"{motion.Label}: a non-Legacy Humanoid clip is required for retargeting.");
            if (float.IsNaN(normalizedTime) || float.IsInfinity(normalizedTime))
                return Fail("Playback position must be a finite number.");
            ApplyConfiguration();
            if (!animator.isInitialized) { animator.Rebind(); animator.Update(0f); }
            var hash = Animator.StringToHash(motion.StatePath);
            if (!animator.HasState(0, hash))
                return Fail($"Saved Animator state '{motion.StatePath}' is missing. Rebuild the library controller in the Editor.");
            paused = false;
            animator.speed = playbackSpeed;
            normalizedTime = Mathf.Clamp01(normalizedTime);
            if (string.IsNullOrEmpty(currentMotionId) || motion.blendIn <= 0f)
                animator.Play(hash, 0, normalizedTime);
            else
                animator.CrossFadeInFixedTime(hash, motion.blendIn, 0, normalizedTime * motion.clip.length);
            // Let Unity evaluate the requested state once in its normal animation
            // phase. Extra zero-time evaluations also run IK callbacks and can
            // re-evaluate an interrupted walk/idle blend before navigation settles.
            currentMotionId = motion.id;
            LastError = "";
            return true;
        }

        public void Pause()
        {
            paused = true;
            if (animator) animator.speed = 0f;
        }

        public void Resume()
        {
            paused = false;
            if (animator) animator.speed = playbackSpeed;
        }

        /// <summary>Zero pauses. Positive values set speed and resume; reverse playback is unsupported.</summary>
        public void SetPlaybackSpeed(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 4f)
            { Fail("Playback speed must be a finite value from 0 to 4."); return; }
            if (value == 0f) { Pause(); return; }
            playbackSpeed = Mathf.Max(.05f, value);
            Resume();
            LastError = "";
        }

        /// <summary>Rewind the currently requested motion while preserving pause state.</summary>
        public void Rewind()
        {
            if (!Application.isPlaying || !animator || string.IsNullOrEmpty(currentMotionId)) return;
            var motion = library ? library.Find(currentMotionId) : null;
            if (motion == null || !motion.clip) { Fail("The current motion is no longer available."); return; }
            animator.Play(Animator.StringToHash(motion.StatePath), 0, 0f);
            animator.Update(0f);
            LastError = "";
        }

        bool Fail(string message)
        {
            LastError = message;
            Debug.LogWarning($"[HumanAnimation] {name}: {message}", this);
            return false;
        }
    }
}
