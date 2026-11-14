using StationFlow.HumanAnimation;
using UnityEngine;
using UnityEngine.AI;

namespace StationFlow.TrainStation.Social
{
    /// <summary>Authored destinations and animation presentation; NavMeshAgent owns travel and local avoidance.</summary>
    [DisallowMultipleComponent]
    public sealed class StationActorMotion : MonoBehaviour
    {
        public const string DefaultWalkId = "mixamo_male_standard_walking_ef7424170f";
        public const string AdaptiveWalkId = "station_adaptive_walk";
        public const string StrideParameter = "StationWalkStride";
        public const float MinimumStepCadence = .65f;
        static readonly int StrideParameterHash = Animator.StringToHash(StrideParameter);
        // Measured from the saved Mixamo walk RootT advance and the XBot Avatar scale.
        const float DefaultWalkReferenceSpeed = 1.091139078f;
        const float DefaultWalkReferenceHumanScale = 1.05003643f;
        public HumanAnimationPlayer animationPlayer;
        public NavMeshAgent agent;
        [Header("Saved library motion IDs")]
        public string idleId = "mixamo_neutral_idle_bcadd5d2cf";
        public string walkId = DefaultWalkId;
        public string sitIdleId = "mixamo_sitting_idle_a1128588e0";
        public string sitDownId = "mixamo_sit_down_a11f1ca01a";
        public string standUpId = "mixamo_stand_up_29c4bb683a";
        public string talkId = "mixamo_talking_2584574f06";
        public string sweepId;
        [Header("Walk calibration and movement response")]
        [Min(.05f), Tooltip("Measured forward travel in metres per second for the walk clip at 1x on the reference Avatar.")]
        public float walkReferenceSpeed = DefaultWalkReferenceSpeed;
        [Min(.01f), Tooltip("Animator.humanScale of the Avatar used to measure walkReferenceSpeed.")]
        public float walkReferenceHumanScale = DefaultWalkReferenceHumanScale;
        [Min(.05f), Tooltip("Additional stride multiplier for a model with a different gait or proportion. One uses the calibrated Humanoid scale.")]
        public float walkStrideScale = 1f;
        [Min(0f), Tooltip("Time constant in seconds for smoothing measured navigation speed. Keep this short to follow acceleration.")]
        public float walkSpeedSmoothing = .10f;
        [Min(.01f)] public float walkStartSpeed = .10f;
        [Min(0f)] public float walkStopSpeed = .045f;
        [Min(0f)] public float walkStopDelay = .15f;
        [Min(.1f)] public float navMeshSnapDistance = 1.5f;
        [Tooltip("Shorten the stride at low speed instead of slowing the whole walk cycle to a crawl. Requires the saved adaptive-walk state.")]
        public bool enhancedLocomotion = true;

        // Retained only for old serialized assets and editor reports. These settings never control movement.
        [HideInInspector] public bool directionalLocomotion;
        [HideInInspector] public bool enableStationaryDetours;
        [HideInInspector] public string leftStrafeId = "mixamo_left_strafe_walking_c95be7c053";
        [HideInInspector] public string rightStrafeId = "mixamo_right_strafe_walking_1918c30aab";
        [HideInInspector] public string leftTurnId = "mixamo_left_turn_90_c4c159e68f";
        [HideInInspector] public string rightTurnId = "mixamo_right_turn_90_e0abf15f6a";
        [HideInInspector] public float leftStrafeReferenceSpeed;
        [HideInInspector] public float rightStrafeReferenceSpeed;
        [HideInInspector] public float leftTurnDegreesPerSecond;
        [HideInInspector] public float rightTurnDegreesPerSecond;
        [HideInInspector] public float strafeEnterAngle = 60f;
        [HideInInspector] public float strafeExitAngle = 38f;
        [HideInInspector] public float directionalPoseHold = .32f;
        [HideInInspector] public float turnBeforeTravelAngle = 115f;

        [SerializeField] string currentMotion;
        [SerializeField] string navigationIssue;
        [SerializeField] bool locomotionActive;
        [SerializeField] bool isWalkPose;
        [SerializeField] float actualPlanarSpeed;
        [SerializeField] float appliedWalkPlaybackSpeed;
        [SerializeField] float locomotionStrideWeight;
        HumanAnimationLibrary checkedStrideLibrary;
        RuntimeAnimatorController checkedStrideController;
        bool hasCheckedStrideSupport, strideSupported;
        bool hasDestination;
        bool repeatNonLooping;
        float repeatAt;
        float smoothedWalkSpeed;
        float stoppedSince = -1f;
        Vector3 lastDestination;
        Vector3 lastResolvedDestination;
        int destinationRequestedFrame = -1;

        /// <summary>The live NavMeshAgent speed setting, including changes made in the Inspector.</summary>
        public float RequestedTravelSpeed
        {
            get
            {
                if (!agent) agent = GetComponent<NavMeshAgent>();
                return agent ? agent.speed : 0f;
            }
        }

        /// <summary>Set a discrete activity speed profile; acceleration and avoidance remain native.</summary>
        public void SetTravelSpeed(float metresPerSecond)
        {
            if (float.IsNaN(metresPerSecond) || float.IsInfinity(metresPerSecond)) return;
            if (!agent) agent = GetComponent<NavMeshAgent>();
            if (agent) agent.speed = Mathf.Max(0f, metresPerSecond);
        }

        // Read-only compatibility for reports produced before custom locomotion was retired.
        public float CrowdSpeedLimit => 1f;
        public float CurrentCrowdSpeedFactor => 1f;
        public bool IsTurningInPlace => false;
        public bool IsAvoidingQueue => false;
        public int QueueDetourCount => 0;
        public bool IsAvoidingStationaryCrowd => false;
        public int StationaryDetourCount => 0;
        public void ApplyCrowdSpeedLimit(float factor) { }

        public string CurrentMotion => currentMotion;
        public string NavigationIssue => navigationIssue;
        public bool LocomotionActive => locomotionActive;
        public bool IsWalkPose => isWalkPose;
        public float ActualPlanarSpeed => actualPlanarSpeed;
        /// <summary>Zero during locomotion idle; otherwise the actual walk playback multiplier.</summary>
        public float AppliedWalkPlaybackSpeed => appliedWalkPlaybackSpeed;
        /// <summary>Actual short-stride mode, including the saved-controller availability check.</summary>
        public bool EnhancedLocomotionPresentation => enhancedLocomotion && SupportsShortStride();
        /// <summary>Zero outside walking; one is the calibrated full stride, below one shortens its excursion.</summary>
        public float LocomotionStrideWeight => locomotionStrideWeight;
        /// <summary>Saved Animator parameter, including the outgoing walk during a crossfade to another activity.</summary>
        public float AnimatorStrideWeight => SupportsShortStride() ? animationPlayer.TargetAnimator.GetFloat(StrideParameterHash) : 0f;
        /// <summary>Nominal kinematic speed represented by cadence and stride, useful for presentation diagnostics.</summary>
        public float PresentedLocomotionSpeed => appliedWalkPlaybackSpeed * locomotionStrideWeight * EffectiveWalkReferenceSpeed;

        /// <summary>Diagnostic A/B switch only; never changes navigation state, path, speed, or root position.</summary>
        public void ConfigureLocomotionPresentation(bool enhanced)
        {
            enhancedLocomotion = enhanced;
            hasCheckedStrideSupport = false;
            if (locomotionActive && animationPlayer && Time.deltaTime > 0f) SetLocomotionPose(isWalkPose);
        }

        bool SupportsShortStride()
        {
            var library = animationPlayer ? animationPlayer.Library : null;
            var animator = animationPlayer ? animationPlayer.TargetAnimator : null;
            var controller = animator ? animator.runtimeAnimatorController : null;
            // The Player installs its controller before its first positive-delta evaluation.
            // A missing native state at that moment is not a permanent lack of support.
            if (!library || !animator || !controller || !animator.isInitialized) return false;
            if (!hasCheckedStrideSupport || library != checkedStrideLibrary || controller != checkedStrideController)
            {
                checkedStrideLibrary = library;
                checkedStrideController = controller;
                var motion = library ? library.Find(AdaptiveWalkId) : null;
                strideSupported = walkId == DefaultWalkId && animator && controller && motion != null && motion.clip &&
                    motion.blendParameter == StrideParameter && animator.HasState(0, Animator.StringToHash(motion.StatePath));
                hasCheckedStrideSupport = true;
            }
            return strideSupported;
        }

        /// <summary>Presentation-only mapping. The child clips share a cycle length so cadence stays coherent.</summary>
        public static void CalculateWalkPresentation(float speed, float referenceSpeed, bool enhanced, out float cadence, out float stride)
        {
            float ratio = Mathf.Max(0f, speed) / Mathf.Max(.05f, referenceSpeed);
            cadence = Mathf.Clamp(ratio, enhanced ? MinimumStepCadence : .05f, 4f);
            stride = enhanced ? Mathf.Clamp01(ratio / cadence) : 1f;
        }

        void ApplyWalkPresentation()
        {
            bool enhanced = EnhancedLocomotionPresentation;
            CalculateWalkPresentation(smoothedWalkSpeed, EffectiveWalkReferenceSpeed, enhanced, out float cadence, out float stride);
            if (enhanced) animationPlayer.TargetAnimator.SetFloat(StrideParameterHash, stride);
            if (Mathf.Abs(animationPlayer.PlaybackSpeed - cadence) > .001f || animationPlayer.IsPaused)
                animationPlayer.SetPlaybackSpeed(cadence);
            appliedWalkPlaybackSpeed = animationPlayer.PlaybackSpeed;
            locomotionStrideWeight = stride;
        }
        public float EffectiveWalkReferenceSpeed => EffectiveReferenceSpeed(walkReferenceSpeed);
        public float ForwardWalkReferenceSpeed => EffectiveWalkReferenceSpeed;
        /// <summary>A native path still targeting the resolved submitted destination.</summary>
        public bool IsNavigatingToRequestedDestination => hasDestination && agent && agent.enabled && agent.isOnNavMesh &&
            (agent.pathPending || agent.hasPath && (agent.destination - lastResolvedDestination).sqrMagnitude <= .0025f);

        float EffectiveReferenceSpeed(float referenceSpeed)
        {
            var animator = animationPlayer ? animationPlayer.TargetAnimator : null;
            float referenceScale = PositiveFinite(walkReferenceHumanScale, DefaultWalkReferenceHumanScale);
            float currentScale = animator && animator.isHuman ? PositiveFinite(animator.humanScale, referenceScale) : referenceScale;
            return PositiveFinite(referenceSpeed, DefaultWalkReferenceSpeed) * currentScale / referenceScale * PositiveFinite(walkStrideScale, 1f);
        }

        bool HasResolvedDestination => hasDestination && agent && agent.enabled && agent.isOnNavMesh &&
            Time.frameCount > destinationRequestedFrame && !agent.pathPending;
        public bool HasArrived => HasResolvedDestination && string.IsNullOrEmpty(navigationIssue) &&
            agent.pathStatus == NavMeshPathStatus.PathComplete &&
            agent.remainingDistance <= Mathf.Max(.15f, agent.stoppingDistance + .08f);
        public bool NavigationFailed => !string.IsNullOrEmpty(navigationIssue) ||
            (HasResolvedDestination && agent.pathStatus != NavMeshPathStatus.PathComplete);

        void Awake()
        {
            if (!animationPlayer) animationPlayer = GetComponentInChildren<HumanAnimationPlayer>();
            if (!agent) agent = GetComponent<NavMeshAgent>();
            if (agent) agent.updateUpAxis = false;
            ConfigureMovingAgent();
            if (animationPlayer && animationPlayer.TargetAnimator)
                animationPlayer.TargetAnimator.applyRootMotion = false;
        }

        void OnEnable()
        {
            StationCrowdSpace.Register(this);
        }

        void Update()
        {
            if (repeatNonLooping && Time.time >= repeatAt && animationPlayer && !animationPlayer.IsPaused)
                PlayMotion(currentMotion, true, true);
            // Presentation reads the native simulation; this loop never writes navigation or root transforms.
            actualPlanarSpeed = MeasurePlanarSpeed();
            if (!locomotionActive || !animationPlayer || Time.deltaTime <= 0f) return;
            float blend = walkSpeedSmoothing <= 0f ? 1f : 1f - Mathf.Exp(-Time.deltaTime / walkSpeedSmoothing);
            smoothedWalkSpeed = Mathf.Lerp(smoothedWalkSpeed, actualPlanarSpeed, blend);
            // Waiting is a visual pose only, entered at almost zero speed. The agent keeps resolving traffic.
            bool shortStride = EnhancedLocomotionPresentation;
            float stopThreshold = shortStride ? .025f : Mathf.Max(0f, walkStopSpeed);
            float startThreshold = shortStride ? .05f : Mathf.Max(stopThreshold + .01f, walkStartSpeed);
            if (isWalkPose)
            {
                if (actualPlanarSpeed <= stopThreshold)
                {
                    if (stoppedSince < 0f) stoppedSince = Time.time;
                    if (Time.time - stoppedSince >= Mathf.Max(0f, walkStopDelay)) SetLocomotionPose(false);
                }
                else stoppedSince = -1f;
            }
            else if (actualPlanarSpeed >= startThreshold)
            {
                stoppedSince = -1f;
                SetLocomotionPose(true);
            }
            if (locomotionActive && isWalkPose)
            {
                string expected = EnhancedLocomotionPresentation ? AdaptiveWalkId : walkId;
                if (currentMotion != expected) SetLocomotionPose(true);
                else ApplyWalkPresentation();
            }
        }

        float MeasurePlanarSpeed()
        {
            if (!agent || !agent.enabled || !agent.isOnNavMesh) return 0f;
            var velocity = agent.velocity;
            velocity.y = 0f;
            float speed = velocity.magnitude;
            return float.IsNaN(speed) || float.IsInfinity(speed) ? 0f : speed;
        }

        static float PositiveFinite(float value, float fallback) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && value > .0001f ? value : fallback;

        void SetLocomotionPose(bool walk)
        {
            // A social state can resume walking after this component's Update.
            // Apply the measured rate now so the first walk frame and diagnostics
            // cannot retain the preceding idle rate or ExitLocomotion's zero value.
            bool shortStride = EnhancedLocomotionPresentation;
            if (animationPlayer)
            {
                if (walk) ApplyWalkPresentation();
                else animationPlayer.SetPlaybackSpeed(1f);
            }
            if (!PlayMotion(walk ? (shortStride ? AdaptiveWalkId : walkId) : idleId, true))
            {
                ExitLocomotion(true);
                return;
            }
            isWalkPose = walk;
            appliedWalkPlaybackSpeed = walk ? animationPlayer.PlaybackSpeed : 0f;
            if (!walk) locomotionStrideWeight = 0f;
        }

        void ExitLocomotion(bool preservePlaybackSpeed)
        {
            locomotionActive = false;
            isWalkPose = false;
            appliedWalkPlaybackSpeed = 0f;
            locomotionStrideWeight = 0f;
            smoothedWalkSpeed = 0f;
            stoppedSince = -1f;
            if (!preservePlaybackSpeed && animationPlayer) animationPlayer.SetPlaybackSpeed(1f);
        }

        public float Duration(string id)
        {
            var clip = animationPlayer && animationPlayer.Library ? animationPlayer.Library.Find(id)?.clip : null;
            return clip ? clip.length : 1f;
        }

        public bool Play(string id, bool repeat = false, bool restart = false)
        {
            // Passenger supplies an explicit speed immediately before these timed seat transitions.
            ExitLocomotion(id == standUpId || id == sitDownId);
            return PlayMotion(id, repeat, restart);
        }

        bool PlayMotion(string id, bool repeat = false, bool restart = false)
        {
            if (!animationPlayer || string.IsNullOrEmpty(id)) return false;
            if (!restart && currentMotion == id)
            {
                if (animationPlayer.IsPaused) animationPlayer.Resume();
                return true;
            }
            if (!animationPlayer.Play(id)) return false;
            currentMotion = id;
            var clip = animationPlayer.Library.Find(id)?.clip;
            repeatNonLooping = repeat && clip && !clip.isLooping;
            repeatAt = Time.time + Duration(id) / Mathf.Max(.05f, animationPlayer.PlaybackSpeed);
            return true;
        }

        public void PlayIdle() => Play(idleId, true);
        public void PlayWalk()
        {
            if (locomotionActive) return;
            locomotionActive = true;
            actualPlanarSpeed = MeasurePlanarSpeed();
            smoothedWalkSpeed = actualPlanarSpeed;
            stoppedSince = -1f;
            SetLocomotionPose(actualPlanarSpeed >= (EnhancedLocomotionPresentation ? .05f : Mathf.Max(walkStopSpeed + .01f, walkStartSpeed)));
        }
        public void PlaySitIdle() => Play(sitIdleId, true);
        public void PlaySitDown() => Play(sitDownId, false, true);
        public void PlayStandUp() => Play(standUpId, false, true);
        public void PlayTalk() => Play(talkId, true);
        public void PlaySweep() => Play(sweepId, true);

        /// <summary>Attach an authored spawn or seat-approach pose to the NavMesh; never used for avoidance.</summary>
        public bool EnableNavigationAt(Vector3 position)
        {
            navigationIssue = "";
            if (!agent) agent = GetComponent<NavMeshAgent>();
            if (!agent) { navigationIssue = "Missing NavMeshAgent."; return false; }
            ConfigureMovingAgent();
            if (!NavMesh.SamplePosition(position, out var hit, navMeshSnapDistance, agent.areaMask))
            { navigationIssue = "No walkable NavMesh at the authored approach."; return false; }
            if (agent.enabled && agent.isOnNavMesh)
            {
                if (!agent.Warp(hit.position))
                { navigationIssue = "Agent could not attach to the authored approach."; return false; }
            }
            else
            {
                agent.enabled = false;
                transform.position = hit.position;
                agent.enabled = true;
            }
            if (!agent.isOnNavMesh) { navigationIssue = "Agent could not attach to the baked NavMesh."; return false; }
            agent.updateUpAxis = false;
            agent.updateRotation = true;
            agent.isStopped = false;
            hasDestination = false;
            return true;
        }

        /// <summary>Read-only reachability query for selecting an activity or seat.</summary>
        public bool CanReach(Vector3 destination)
        {
            if (!agent) agent = GetComponent<NavMeshAgent>();
            if (!agent) return false;
            if (!NavMesh.SamplePosition(transform.position, out var start, navMeshSnapDistance, agent.areaMask) ||
                !NavMesh.SamplePosition(destination, out var end, navMeshSnapDistance, agent.areaMask)) return false;
            var path = new NavMeshPath();
            return NavMesh.CalculatePath(start.position, end.position, agent.areaMask, path) &&
                path.status == NavMeshPathStatus.PathComplete;
        }

        /// <summary>Submit the activity's destination; Unity owns the resulting path and local steering.</summary>
        public bool TryMoveTo(Vector3 destination)
        {
            navigationIssue = "";
            if (!agent || !agent.enabled || !agent.isOnNavMesh)
            { navigationIssue = "Native NavMeshAgent is not enabled on its baked floor."; return false; }
            ConfigureMovingAgent();
            agent.updateRotation = true;
            if (IsNavigatingToRequestedDestination && (destination - lastDestination).sqrMagnitude < .0025f && !NavigationFailed)
            {
                agent.isStopped = false;
                return true;
            }
            if (!NavMesh.SamplePosition(destination, out var hit, navMeshSnapDistance, agent.areaMask))
            { navigationIssue = "Destination is outside the baked NavMesh."; return false; }
            if (!agent.SetDestination(hit.position))
            { navigationIssue = "NavMeshAgent rejected the destination."; return false; }
            lastDestination = destination;
            lastResolvedDestination = hit.position;
            hasDestination = true;
            destinationRequestedFrame = Time.frameCount;
            agent.isStopped = false;
            return true;
        }

        public void StopNavigation(bool detach = false)
        {
            if (agent && agent.enabled && agent.isOnNavMesh)
            {
                agent.isStopped = true;
                agent.ResetPath();
            }
            hasDestination = false;
            if (detach && agent) agent.enabled = false;
        }

        void ConfigureMovingAgent()
        {
            if (!agent) return;
            agent.avoidancePriority = 50;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            agent.autoRepath = true;
        }

        /// <summary>Face a social partner only while stationary or detached; never override travel orientation.</summary>
        public void Face(Vector3 point, float degreesPerSecond = 240f)
        {
            if (agent && agent.enabled && agent.isOnNavMesh)
            {
                if (MeasurePlanarSpeed() > .025f ||
                    (!agent.isStopped && (agent.pathPending || (agent.hasPath && !HasArrived)))) return;
                agent.updateRotation = false;
            }
            var direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < .001f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction),
                Mathf.Max(0f, degreesPerSecond) * Time.deltaTime);
        }

        public void AlignTo(Transform pose)
        {
            if (!pose) return;
            StopNavigation(true);
            transform.SetPositionAndRotation(pose.position, pose.rotation);
        }

        void OnDisable()
        {
            StationCrowdSpace.Unregister(this);
            StopNavigation();
            repeatNonLooping = false;
            actualPlanarSpeed = 0f;
            ExitLocomotion(true);
        }
    }
}
