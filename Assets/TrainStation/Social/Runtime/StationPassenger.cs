using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

namespace StationFlow.TrainStation.Social
{
    public enum StationPassengerState { Seated, StandingUp, WalkingToQueue, WaitingInQueue, Talking, ReturningToSeat, SittingDown, WaitingForSeat, Disabled, ApproachingQueue, StandingWithGroup, Wandering, FollowingGroup, ReturningToActivity, WaitingForRoute, ExternalControlled }
    public enum StationPassengerActivity { Seated, StandingGroup, WalkingGroup, SoloWalking }

    /// <summary>One reusable traveller: a saved daily activity, physical FIFO visits, and return to that activity.</summary>
    [DisallowMultipleComponent]
    public sealed class StationPassenger : MonoBehaviour
    {
        public StationActorMotion motion;
        public StationSocialWorld world;
        public StationInformationDesk informationDesk;
        [Header("Usual activity")]
        public StationPassengerActivity activity;
        public StationConversationMember conversationMember;
        public StationWalkingGroup walkingGroup;
        [Tooltip("Fixed scene anchor for a standing conversation member. If empty, the initial pose is remembered.")]
        public Transform homePoint;
        public Transform[] wanderPoints = Array.Empty<Transform>();
        public Vector2 wanderPause = new Vector2(2f, 7f);
        [Min(.4f), Tooltip("Solo travellers choose separate, temporarily claimed standing points around each route marker.")]
        public float wanderDestinationRadius = 1.6f;
        public StationSeat initialSeat;
        public Transform luggage;
        public Vector3 carriedLuggageOffset = new Vector3(.42f, 0f, -.25f);
        [Header("Requests and recovery")]
        public bool automaticRequests = true;
        [Tooltip("Opt-in for a separate activity planner. The passenger still executes physical FIFO visits, then hands control back to that planner.")]
        public bool externalActivityControl;
        public event Action<bool> ExternalServiceCompleted;
        // Retained for serialized scenes; the NavMeshAgent keeps its configured native priority.
        [HideInInspector] public int stationaryAvoidancePriority = 20;
        public int randomSeed = 1;
        public Vector2 requestInterval = new Vector2(12f, 35f);
        [Min(2f), Tooltip("While approaching a service, maximum time without measurable route progress; other navigation states retain their travel timeout. approachTimeout always caps the whole approach.")]
        public float navigationTimeout = 45f;
        [Min(2f), Tooltip("Maximum total time approaching the queue, including waiting outside a full queue. Moving the tail does not reset it.")]
        public float approachTimeout = 120f;
        [Min(2f)] public float queueWaitTimeout = 100f;
        [Range(.25f, 4f), Tooltip("Animation and transition duration multiplier; one preserves the source timing.")]
        public float transitionDurationScale = 1f;
        public UnityEvent requestStarted = new UnityEvent();
        public UnityEvent returnedToSeat = new UnityEvent();
        public UnityEvent returnedToActivity = new UnityEvent();
        [SerializeField] StationPassengerState currentState = StationPassengerState.Seated;
        [SerializeField] StationSeat currentSeat;
        [SerializeField] StationSeat originalSeat;
        [SerializeField] string lastIssue;
        [SerializeField] int completedConversations;
        [SerializeField] int completedShopVisits;
        [SerializeField] int completedInformationVisits;
        [SerializeField] StationInformationDesk currentService;
        [SerializeField] float nextRequestAt;
        System.Random random;
        float stateEndsAt;
        float transitionStartedAt;
        Vector3 transitionStartPosition;
        Vector3 transitionEndPosition;
        Quaternion transitionStartRotation;
        Quaternion transitionEndRotation;
        float navigationStartedAt;
        float approachStartedAt;
        NavMeshPath approachProgressPath;
        Vector3 approachProgressGoal;
        bool hasApproachProgressGoal;
        float approachLastProgressAt = -1f;
        float approachNextProgressCheck;
        float approachBestRemainingDistance = float.PositiveInfinity;
        float queueJoinedAt;
        float retryAt;
        bool initialized;
        bool goingToDesk;
        bool hasApproachDestination;
        bool waitingOutsideQueue;
        Vector3 approachDestination;
        StationSeat requestedSeat;
        Transform assignedQueueSlot;
        Vector3 assignedQueuePosition;
        Vector3 savedHomePosition;
        Quaternion savedHomeRotation;
        Vector3 activityDestination;
        bool hasActivityDestination;
        float nextActivityPathAt;
        float wanderResumeAt;
        float defaultAgentSpeed;
        int lastWanderIndex = -1;
        bool serviceNavigationConfigured, queueNavigationConfigured;
        float serviceTravelSpeed, serviceStoppingDistance;
        static readonly Dictionary<StationPassenger, Vector3> wanderDestinations = new Dictionary<StationPassenger, Vector3>();
        bool groupHoldingPosition;
        bool groupSpeedConfigured, groupProfileReturning;
        StationWalkingGroup speedProfileGroup;
        StationPassenger speedProfileLeader;
        [SerializeField] bool recoveringWalkingGroup;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetDestinationClaims() => wanderDestinations.Clear();

        public StationPassengerState CurrentState => currentState;
        public StationSeat CurrentSeat => currentSeat;
        public StationSeat OriginalSeat => originalSeat;
        public int CompletedConversations => completedConversations;
        public int CompletedShopVisits => completedShopVisits;
        public int CompletedInformationVisits => completedInformationVisits;
        public float NextRequestAt => nextRequestAt;
        public StationInformationDesk CurrentService => currentService;
        public string LastIssue => lastIssue;
        public bool ReadyForConversation => currentState == StationPassengerState.WaitingInQueue;
        public bool HasInformationRequest => goingToDesk;
        public bool IsAvailableForActivity => isActiveAndEnabled && initialized && !goingToDesk &&
            (currentState == StationPassengerState.Seated || currentState == StationPassengerState.StandingWithGroup ||
             currentState == StationPassengerState.Wandering || currentState == StationPassengerState.FollowingGroup ||
             currentState == StationPassengerState.ExternalControlled ||
             (currentState == StationPassengerState.WaitingForRoute && activity == StationPassengerActivity.SoloWalking));
        public bool CanParticipateInConversation => activity == StationPassengerActivity.StandingGroup &&
            isActiveAndEnabled && currentState == StationPassengerState.StandingWithGroup;
        public bool IsFollowingWalkingGroup => activity == StationPassengerActivity.WalkingGroup &&
            isActiveAndEnabled && currentState == StationPassengerState.FollowingGroup;
        public bool IsRecoveringWalkingGroup => activity == StationPassengerActivity.WalkingGroup && isActiveAndEnabled &&
            recoveringWalkingGroup && !goingToDesk &&
            (currentState == StationPassengerState.ReturningToActivity || currentState == StationPassengerState.WaitingForRoute);
        public bool ParticipatesInWalkingCohesion => IsFollowingWalkingGroup || IsRecoveringWalkingGroup;
        public bool IsWalking => currentState == StationPassengerState.WalkingToQueue || currentState == StationPassengerState.ReturningToSeat ||
            currentState == StationPassengerState.ReturningToActivity || currentState == StationPassengerState.Wandering ||
            currentState == StationPassengerState.FollowingGroup || (currentState == StationPassengerState.ApproachingQueue && !waitingOutsideQueue);

        void Awake()
        {
            if (!motion) motion = GetComponent<StationActorMotion>();
            if (!conversationMember) conversationMember = GetComponent<StationConversationMember>();
            if (conversationMember) conversationMember.passenger = this;
            savedHomePosition = homePoint ? homePoint.position : transform.position;
            savedHomeRotation = homePoint ? homePoint.rotation : transform.rotation;
            defaultAgentSpeed = motion && motion.agent ? motion.RequestedTravelSpeed : 1.15f;
        }

        void Start()
        {
            initialized = true;
            if (!motion) motion = GetComponent<StationActorMotion>();
            if (!informationDesk && world) informationDesk = world.informationDesk;
            random = new System.Random(randomSeed);
            originalSeat = initialSeat;
            InitializeActivity();
        }

        void OnEnable() { if (initialized && Application.isPlaying) InitializeActivity(); }

        void InitializeActivity()
        {
            ReleaseWanderDestination();
            ClearApproachProgress();
            recoveringWalkingGroup = false;
            groupSpeedConfigured = false;
            if (!motion) { lastIssue = "Missing StationActorMotion."; currentState = StationPassengerState.Disabled; return; }
            currentService = null;
            goingToDesk = false;
            if (externalActivityControl)
            {
                if (!motion.EnableNavigationAt(transform.position))
                { lastIssue = motion.NavigationIssue; currentState = StationPassengerState.Disabled; return; }
                currentState = StationPassengerState.ExternalControlled;
                motion.StopNavigation();
                motion.PlayIdle();
                return;
            }
            if (activity == StationPassengerActivity.Seated) { InitializeSeat(); return; }
            if (!motion.EnableNavigationAt(transform.position))
            {
                lastIssue = motion.NavigationIssue;
                currentState = StationPassengerState.WaitingForRoute;
                retryAt = Time.time + 3f;
                ScheduleRequest();
                return;
            }
            FinishActivityReturn(false);
        }

        void InitializeSeat()
        {
            if (!motion) { lastIssue = "Missing StationActorMotion."; currentState = StationPassengerState.Disabled; return; }
            if (initialSeat && initialSeat.TryOccupy(this))
            {
                currentSeat = initialSeat;
                FinishSeating(false);
            }
            else BeginReturnToSeat();
        }

        void Update()
        {
            if (!motion) return;
            if (!externalActivityControl && automaticRequests && IsAvailableForActivity && Time.time >= nextRequestAt)
                if (!TryRequestRandomActivity()) nextRequestAt = Time.time + Sample(new Vector2(2f, 5f));
            switch (currentState)
            {
                case StationPassengerState.Seated:
                    break;
                case StationPassengerState.StandingUp:
                    UpdateSeatingTransition(false);
                    if (Time.time >= stateEndsAt) DepartSeat();
                    break;
                case StationPassengerState.WalkingToQueue:
                case StationPassengerState.WaitingInQueue:
                    UpdateQueue();
                    break;
                case StationPassengerState.ApproachingQueue:
                    UpdateApproach();
                    break;
                case StationPassengerState.Talking:
                    if (currentService) motion.Face(currentService.ReceptionistPosition);
                    else BeginReturnToSeat();
                    break;
                case StationPassengerState.ReturningToSeat:
                    if (!currentSeat || currentSeat.ReservedBy != this) { BeginReturnToSeat(); break; }
                    if (motion.NavigationFailed || Time.time - navigationStartedAt > navigationTimeout)
                    {
                        lastIssue = "Seat route interrupted or timed out; released reservation and will retry.";
                        currentSeat.Release(this);
                        currentSeat = null;
                        WaitForSeat();
                    }
                    else if (motion.HasArrived) BeginSitting();
                    break;
                case StationPassengerState.SittingDown:
                    if (!currentSeat || currentSeat.ReservedBy != this) { BeginReturnToSeat(); break; }
                    UpdateSeatingTransition(true);
                    if (Time.time >= stateEndsAt) FinishSeating(true);
                    break;
                case StationPassengerState.WaitingForSeat:
                    if (Time.time >= retryAt) BeginReturnToSeat();
                    break;
                case StationPassengerState.StandingWithGroup:
                    if (!conversationMember || !conversationMember.isActiveAndEnabled) motion.PlayIdle();
                    break;
                case StationPassengerState.Wandering:
                    UpdateWandering();
                    break;
                case StationPassengerState.FollowingGroup:
                    UpdateGroupTravel(false);
                    break;
                case StationPassengerState.ReturningToActivity:
                    UpdateActivityReturn();
                    break;
                case StationPassengerState.WaitingForRoute:
                    if (Time.time >= retryAt) BeginActivityReturn();
                    break;
            }
        }

        [ContextMenu("Request information now (Play mode)")]
        public void RequestInformation() => TryRequestInformation();

        public bool TryRequestInformation() => TryVisitService(informationDesk ? informationDesk : world ? world.informationDesk : null);

        public bool TryRequestRandomActivity()
        {
            if (!IsAvailableForActivity) return false;
            if (random == null) random = new System.Random(randomSeed);
            int shops = world && world.shopDesks != null ? world.shopDesks.Length : 0;
            int count = shops + 1;
            int first = random.Next(count);
            for (int offset = 0; offset < count; ++offset)
            {
                int index = (first + offset) % count;
                var service = index == 0 ? (informationDesk ? informationDesk : world ? world.informationDesk : null) : world.shopDesks[index - 1];
                // Limit simultaneous automatic trips to a small service area. This is only
                // demand control: queue order is still established by physical arrival.
                if (world && !world.HasAutomaticRequestCapacity(service)) continue;
                if (TryVisitService(service)) return true;
            }
            return false;
        }

        public bool TryVisitService(StationInformationDesk service)
        {
            if (!Application.isPlaying || !IsAvailableForActivity || !motion ||
                (activity == StationPassengerActivity.Seated && !currentSeat) ||
                !service || !service.CanAcceptRequests || !service.TryGetApproachPosition(out var destination, this) ||
                !motion.CanReach(destination)) return false;
            currentService = service;
            ReleaseWanderDestination();
            groupHoldingPosition = false;
            recoveringWalkingGroup = false;
            goingToDesk = true;
            if (currentSeat) originalSeat = currentSeat;
            queueJoinedAt = 0f;
            requestedSeat = null;
            assignedQueueSlot = null;
            hasApproachDestination = false;
            waitingOutsideQueue = false;
            ResetApproachProgress();
            lastIssue = "";
            if (activity == StationPassengerActivity.Seated) BeginStanding();
            else
            {
                currentState = StationPassengerState.ApproachingQueue;
                if (conversationMember) conversationMember.SuspendForActivity();
                if (!externalActivityControl) RestoreAgentSpeed();
                CarryLuggage();
                approachStartedAt = navigationStartedAt = Time.time;
                UpdateApproach();
            }
            requestStarted.Invoke();
            return true;
        }

        /// <summary>Scenario hook using the same departure, reservation, navigation and seating transitions.</summary>
        public bool TryRequestSeat(StationSeat preferred)
        {
            if (!Application.isPlaying || !isActiveAndEnabled || currentState != StationPassengerState.Seated ||
                !preferred || preferred == currentSeat || !motion || !motion.CanReach(preferred.ApproachPosition) ||
                !preferred.TryReserve(this)) return false;
            goingToDesk = false;
            requestedSeat = preferred;
            BeginStanding();
            return true;
        }

        void BeginStanding()
        {
            motion.StopNavigation(true);
            currentState = StationPassengerState.StandingUp;
            SetTransitionSpeed();
            motion.PlayStandUp();
            stateEndsAt = Time.time + motion.Duration(motion.standUpId) * Mathf.Clamp(transitionDurationScale, .25f, 4f);
            var approach = currentSeat && currentSeat.approachPoint ? currentSeat.approachPoint : transform;
            BeginSeatingTransition(approach.position, approach.rotation);
        }

        void DepartSeat()
        {
            var departed = currentSeat;
            var approach = departed ? departed.ApproachPosition : transform.position;
            if (!motion.EnableNavigationAt(approach))
            {
                lastIssue = motion.NavigationIssue;
                goingToDesk = false;
                if (currentService) currentService.Cancel(this);
                if (requestedSeat) { requestedSeat.Release(this); requestedSeat = null; }
                if (departed && departed.TryOccupy(this)) { currentSeat = departed; FinishSeating(false); }
                else WaitForSeat();
                return;
            }
            if (departed) departed.Release(this);
            currentSeat = null;
            ResetAnimationSpeed();
            CarryLuggage();
            if (goingToDesk && currentService && currentService.CanAcceptRequests)
            {
                currentState = StationPassengerState.ApproachingQueue;
                approachStartedAt = Time.time;
                navigationStartedAt = Time.time;
                ResetApproachProgress();
                UpdateApproach();
            }
            else BeginReturnToSeat();
        }

        void UpdateApproach()
        {
            bool wasWaitingOutsideQueue = waitingOutsideQueue;
            if (!currentService || !currentService.CanAcceptRequests ||
                !currentService.TryGetApproachPosition(out var destination, this))
            { BeginReturnToSeat(); return; }
            ConfigureServiceNavigation(false);
            if (Time.time - approachStartedAt > approachTimeout)
            {
                lastIssue = "Approaching the queue timed out; returning to the usual activity.";
                BeginReturnToSeat();
                return;
            }
            if (currentService.TryJoin(this))
            {
                if (!isActiveAndEnabled || currentState != StationPassengerState.ApproachingQueue || !currentService.Contains(this)) return;
                queueJoinedAt = Time.time;
                ClearApproachProgress();
                waitingOutsideQueue = false;
                assignedQueueSlot = null;
                currentState = StationPassengerState.WalkingToQueue;
                UpdateQueue();
                return;
            }
            // queueJoined may have run a user-authored cancellation or disable callback.
            if (!isActiveAndEnabled || currentState != StationPassengerState.ApproachingQueue) return;
            if (!hasApproachDestination || (approachDestination - destination).sqrMagnitude > .01f)
            {
                approachDestination = destination;
                hasApproachDestination = true;
                waitingOutsideQueue = false;
                navigationStartedAt = Time.time;
                if (!motion.TryMoveTo(destination)) { CancelFailedQueueRoute(); return; }
                motion.PlayWalk();
            }
            if (motion.NavigationFailed)
            { CancelFailedQueueRoute(); return; }
            UpdateApproachProgress(wasWaitingOutsideQueue);
            if (!waitingOutsideQueue && Time.time - approachLastProgressAt > Mathf.Max(2f, navigationTimeout))
            {
                CancelFailedQueueRoute("No measurable route progress toward the service within the navigation timeout.");
                return;
            }
            if (!waitingOutsideQueue && motion.HasArrived)
            {
                motion.StopNavigation();
                waitingOutsideQueue = true;
                motion.PlayIdle();
            }
            if (waitingOutsideQueue) motion.Face(currentService.ReceptionistPosition);
        }

        void ResetApproachProgress()
        {
            ClearApproachProgress();
            approachLastProgressAt = Time.time;
            // Observe a fixed service point, not the moving tail or an
            // intermediate detour waypoint. Neither can manufacture progress.
            if (!currentService || currentService.queueSlots == null || currentService.queueSlots.Length == 0 ||
                !currentService.queueSlots[0]) return;
            approachProgressGoal = currentService.queueSlots[0].position;
            hasApproachProgressGoal = true;
        }

        void ClearApproachProgress()
        {
            hasApproachProgressGoal = false;
            approachLastProgressAt = -1f;
            approachNextProgressCheck = 0f;
            approachBestRemainingDistance = float.PositiveInfinity;
            // Keep the allocated path for the next visit; only the observation resets.
        }

        void UpdateApproachProgress(bool wasWaitingOutsideQueue)
        {
            if (approachLastProgressAt < 0f) ResetApproachProgress();
            // Queue-capacity waiting is intentionally excluded from the stall
            // budget. The absolute approachStartedAt deadline is never moved.
            if (wasWaitingOutsideQueue) approachLastProgressAt = Mathf.Min(Time.time, approachLastProgressAt + Time.deltaTime);
            if (waitingOutsideQueue || !hasApproachProgressGoal || Time.time < approachNextProgressCheck) return;
            approachNextProgressCheck = Time.time + .65f;
            var agent = motion ? motion.agent : null;
            if (!agent || !agent.enabled || !agent.isOnNavMesh) return;
            if (approachProgressPath == null) approachProgressPath = new NavMeshPath();
            if (!agent.CalculatePath(approachProgressGoal, approachProgressPath) ||
                approachProgressPath.status != NavMeshPathStatus.PathComplete) return;
            var corners = approachProgressPath.corners;
            float remaining = 0f;
            for (int i = 1; i < corners.Length; ++i) remaining += Vector3.Distance(corners[i - 1], corners[i]);
            if (float.IsPositiveInfinity(approachBestRemainingDistance))
                approachBestRemainingDistance = remaining;
            else if (approachBestRemainingDistance - remaining >= .25f)
            {
                approachBestRemainingDistance = remaining;
                approachLastProgressAt = Time.time;
            }
        }

        void UpdateQueue()
        {
            if (!currentService || !currentService.isActiveAndEnabled || !currentService.TryGetQueueSlot(this, out var slot))
            { BeginReturnToSeat(); return; }
            ConfigureServiceNavigation(true);
            if (Time.time - queueJoinedAt > queueWaitTimeout)
            {
                lastIssue = "Queue wait timed out; request cancelled and the usual activity resumed.";
                currentService.Cancel(this);
                return;
            }
            if (assignedQueueSlot != slot || (assignedQueuePosition - slot.position).sqrMagnitude > .01f)
            {
                assignedQueueSlot = slot;
                assignedQueuePosition = slot.position;
                navigationStartedAt = Time.time;
                currentState = StationPassengerState.WalkingToQueue;
                if (!motion.TryMoveTo(slot.position)) { CancelFailedQueueRoute(); return; }
                motion.PlayWalk();
            }
            if (currentState == StationPassengerState.WalkingToQueue)
            {
                if (motion.NavigationFailed || Time.time - navigationStartedAt > navigationTimeout)
                { CancelFailedQueueRoute(); return; }
                // General walking accepts a 15 cm margin. A compact queue needs
                // a tighter stop so the next physical tail position really is free.
                if (motion.HasArrived && motion.agent.remainingDistance <= Mathf.Max(.07f, currentService.queueStoppingDistance + .025f))
                {
                    motion.StopNavigation();
                    currentState = StationPassengerState.WaitingInQueue;
                    motion.PlayIdle();
                }
            }
            if (currentState == StationPassengerState.WaitingInQueue)
            {
                int index = currentService.GetQueueIndex(this);
                var ahead = index > 0 ? currentService.QueuePassengers[index - 1] : null;
                motion.Face(ahead ? ahead.transform.position : currentService.ReceptionistPosition);
            }
        }

        void ConfigureServiceNavigation(bool joined)
        {
            if (!motion || !motion.agent || !currentService) return;
            if (!serviceNavigationConfigured)
            {
                serviceTravelSpeed = motion.RequestedTravelSpeed;
                serviceStoppingDistance = motion.agent.stoppingDistance;
                serviceNavigationConfigured = true;
                motion.agent.stoppingDistance = Mathf.Min(serviceStoppingDistance, currentService.queueStoppingDistance);
            }
            if (joined && !queueNavigationConfigured)
            {
                // Set the queue's activity profile once. Native acceleration,
                // braking and avoidance own every movement toward its slot.
                motion.SetTravelSpeed(Mathf.Min(serviceTravelSpeed, currentService.queueAdvanceSpeed));
                queueNavigationConfigured = true;
            }
        }

        void RestoreServiceNavigation()
        {
            if (serviceNavigationConfigured && motion && motion.agent)
            {
                motion.agent.stoppingDistance = serviceStoppingDistance;
                motion.SetTravelSpeed(serviceTravelSpeed);
            }
            serviceNavigationConfigured = queueNavigationConfigured = false;
        }

        void CancelFailedQueueRoute(string issue = null)
        {
            lastIssue = issue ?? "Queue position is unreachable or the walk timed out.";
            if (currentService && currentService.Contains(this)) currentService.Cancel(this);
            else BeginReturnToSeat();
        }

        public void NotifyConversationStarted()
        {
            if (currentState != StationPassengerState.WaitingInQueue) return;
            currentState = StationPassengerState.Talking;
            motion.StopNavigation();
            motion.PlayTalk();
        }

        public void NotifyConversationEnded()
        {
            if (currentState != StationPassengerState.Talking) return;
            ++completedConversations;
            if (currentService && currentService.serviceKind == StationServiceKind.Shop) ++completedShopVisits;
            else ++completedInformationVisits;
            if (externalActivityControl) { FinishExternalService(true); return; }
            BeginReturnToSeat();
        }

        public void NotifyDeskUnavailable()
        {
            if (!isActiveAndEnabled || currentState == StationPassengerState.Disabled) return;
            goingToDesk = false;
            if (currentState == StationPassengerState.ApproachingQueue || currentState == StationPassengerState.WalkingToQueue || currentState == StationPassengerState.WaitingInQueue ||
                currentState == StationPassengerState.Talking) BeginReturnToSeat();
        }

        void BeginReturnToSeat()
        {
            if (externalActivityControl) { FinishExternalService(false); return; }
            ClearApproachProgress();
            RestoreServiceNavigation();
            if (activity != StationPassengerActivity.Seated) { BeginActivityReturn(); return; }
            goingToDesk = false;
            assignedQueueSlot = null;
            hasApproachDestination = false;
            waitingOutsideQueue = false;
            // Cancel first with a non-queue state to make cancellation callbacks idempotent.
            currentState = StationPassengerState.WaitingForSeat;
            if (currentService && currentService.Contains(this)) currentService.Cancel(this);
            currentService = null;
            if (currentSeat) currentSeat.Release(this);
            currentSeat = null;
            ResetAnimationSpeed();
            motion.StopNavigation();
            if (requestedSeat && requestedSeat.ReservedBy == this && motion.CanReach(requestedSeat.ApproachPosition))
                currentSeat = requestedSeat;
            else
            {
                if (requestedSeat) requestedSeat.Release(this);
                if (world) world.TryReserveSeat(this, originalSeat, out currentSeat);
            }
            requestedSeat = null;
            if (!currentSeat) { WaitForSeat(); return; }
            CarryLuggage();
            if (!motion.TryMoveTo(currentSeat.ApproachPosition))
            {
                currentSeat.Release(this);
                currentSeat = null;
                WaitForSeat();
                return;
            }
            currentState = StationPassengerState.ReturningToSeat;
            navigationStartedAt = Time.time;
            motion.PlayWalk();
        }

        /// <summary>Release a physical service visit before an external planner selects its next activity.</summary>
        public void CancelExternalService()
        {
            if (externalActivityControl && (goingToDesk || currentService)) FinishExternalService(false);
        }

        void FinishExternalService(bool completed)
        {
            bool hadRequest = goingToDesk || currentService;
            var service = currentService;
            currentState = StationPassengerState.ExternalControlled;
            goingToDesk = false;
            currentService = null;
            assignedQueueSlot = null;
            hasApproachDestination = false;
            waitingOutsideQueue = false;
            ClearApproachProgress();
            RestoreServiceNavigation();
            if (service) service.Cancel(this);
            if (motion) { motion.StopNavigation(); motion.PlayIdle(); }
            if (hadRequest) ExternalServiceCompleted?.Invoke(completed);
        }

        void WaitForSeat()
        {
            currentState = StationPassengerState.WaitingForSeat;
            retryAt = Time.time + 2f;
            if (motion) { motion.StopNavigation(); motion.PlayIdle(); }
        }

        void BeginSitting()
        {
            if (!currentSeat || !currentSeat.TryReserve(this)) { BeginReturnToSeat(); return; }
            motion.StopNavigation(true);
            currentState = StationPassengerState.SittingDown;
            PlaceLuggageBesideSeat();
            SetTransitionSpeed();
            motion.PlaySitDown();
            stateEndsAt = Time.time + motion.Duration(motion.sitDownId) * Mathf.Clamp(transitionDurationScale, .25f, 4f);
            var seatedPose = currentSeat.seatedPose ? currentSeat.seatedPose : currentSeat.transform;
            BeginSeatingTransition(seatedPose.position, seatedPose.rotation);
        }

        void BeginSeatingTransition(Vector3 destination, Quaternion rotation)
        {
            transitionStartedAt = Time.time;
            transitionStartPosition = transform.position;
            transitionEndPosition = destination;
            transitionStartRotation = transform.rotation;
            transitionEndRotation = rotation;
        }

        void UpdateSeatingTransition(bool sitting)
        {
            float progress = Mathf.InverseLerp(transitionStartedAt, stateEndsAt, Time.time);
            // Back toward the cushion before lowering; clear the bench after rising.
            // These authored short seat approaches are the only movement while detached from navigation.
            float travel = sitting ? Mathf.InverseLerp(0f, .65f, progress) : Mathf.InverseLerp(.45f, 1f, progress);
            travel = Mathf.SmoothStep(0f, 1f, travel);
            transform.SetPositionAndRotation(Vector3.Lerp(transitionStartPosition, transitionEndPosition, travel),
                Quaternion.Slerp(transitionStartRotation, transitionEndRotation, travel));
            PlaceLuggageBesideSeat();
        }

        void FinishSeating(bool notify)
        {
            ClearApproachProgress();
            if (!currentSeat || !currentSeat.TryOccupy(this)) { BeginReturnToSeat(); return; }
            currentService = null;
            goingToDesk = false;
            motion.AlignTo(currentSeat.seatedPose ? currentSeat.seatedPose : currentSeat.transform);
            currentState = StationPassengerState.Seated;
            ResetAnimationSpeed();
            motion.PlaySitIdle();
            PlaceLuggageBesideSeat();
            ScheduleRequest();
            if (notify) returnedToSeat.Invoke();
            if (notify && isActiveAndEnabled) returnedToActivity.Invoke();
        }

        public void ScheduleNextRequest() => ScheduleRequest();

        void ScheduleRequest()
        {
            if (random == null) random = new System.Random(randomSeed);
            var minimum = Mathf.Max(1f, Mathf.Min(requestInterval.x, requestInterval.y));
            var maximum = Mathf.Max(minimum, Mathf.Max(requestInterval.x, requestInterval.y));
            nextRequestAt = Time.time + minimum + (float)random.NextDouble() * (maximum - minimum);
        }

        void CarryLuggage()
        {
            if (!luggage || !luggage.gameObject.activeSelf) return;
            luggage.SetPositionAndRotation(transform.TransformPoint(carriedLuggageOffset), transform.rotation);
            if (luggage.parent != transform) luggage.SetParent(transform, true);
        }

        void PlaceLuggageBesideSeat()
        {
            if (luggage && luggage.gameObject.activeSelf && currentSeat && currentSeat.luggagePoint)
                luggage.SetPositionAndRotation(currentSeat.luggagePoint.position, currentSeat.luggagePoint.rotation);
        }

        /// <summary>Bounded group recovery from the current position; never teleports or interrupts a service visit.</summary>
        public void RejoinWalkingGroup()
        {
            if (!Application.isPlaying || !IsFollowingWalkingGroup || !walkingGroup) return;
            recoveringWalkingGroup = true;
            BeginActivityReturn();
        }

        void BeginActivityReturn()
        {
            ReleaseWanderDestination();
            ClearApproachProgress();
            groupHoldingPosition = false;
            groupSpeedConfigured = false;
            RestoreServiceNavigation();
            goingToDesk = false;
            assignedQueueSlot = null;
            hasApproachDestination = waitingOutsideQueue = false;
            currentState = StationPassengerState.ReturningToActivity;
            var previousService = currentService;
            currentService = null;
            if (previousService && previousService.Contains(this)) previousService.Cancel(this);
            if (!isActiveAndEnabled || !motion) return;
            ResetAnimationSpeed();
            RestoreAgentSpeed();
            motion.StopNavigation();
            CarryLuggage();
            hasActivityDestination = false;
            navigationStartedAt = Time.time;
            nextActivityPathAt = 0f;
            if (activity == StationPassengerActivity.SoloWalking)
            {
                FinishActivityReturn(true);
                return;
            }
            if (activity == StationPassengerActivity.WalkingGroup && walkingGroup && walkingGroup.isActiveAndEnabled)
            {
                UpdateGroupTravel(true);
                return;
            }
            activityDestination = homePoint ? homePoint.position : savedHomePosition;
            hasActivityDestination = true;
            if (!motion.TryMoveTo(activityDestination)) { WaitForActivityRoute(); return; }
            motion.PlayWalk();
        }

        void UpdateActivityReturn()
        {
            if (activity == StationPassengerActivity.WalkingGroup && walkingGroup && walkingGroup.isActiveAndEnabled)
            { UpdateGroupTravel(true); return; }
            if (!hasActivityDestination) { BeginActivityReturn(); return; }
            if (motion.NavigationFailed || Time.time - navigationStartedAt > Mathf.Max(2f, navigationTimeout))
            { WaitForActivityRoute(); return; }
            if (!motion.HasArrived) return;
            FinishActivityReturn(true);
        }

        void FinishActivityReturn(bool notify)
        {
            ReleaseWanderDestination();
            ClearApproachProgress();
            groupHoldingPosition = false;
            groupSpeedConfigured = false;
            recoveringWalkingGroup = false;
            goingToDesk = false;
            currentService = null;
            hasActivityDestination = false;
            RestoreAgentSpeed();
            motion.StopNavigation();
            CarryLuggage();
            switch (activity)
            {
                case StationPassengerActivity.StandingGroup:
                    currentState = StationPassengerState.StandingWithGroup;
                    motion.PlayIdle();
                    if (conversationMember) conversationMember.ResumeAtHome();
                    else transform.rotation = homePoint ? homePoint.rotation : savedHomeRotation;
                    break;
                case StationPassengerActivity.WalkingGroup:
                    currentState = walkingGroup && walkingGroup.isActiveAndEnabled ? StationPassengerState.FollowingGroup : StationPassengerState.Wandering;
                    nextActivityPathAt = Time.time + (Math.Abs(randomSeed % 19) / 19f) * .3f;
                    wanderResumeAt = Time.time;
                    motion.PlayIdle();
                    break;
                default:
                    currentState = StationPassengerState.Wandering;
                    wanderResumeAt = Time.time;
                    motion.PlayIdle();
                    break;
            }
            ScheduleRequest();
            if (notify) returnedToActivity.Invoke();
        }

        void WaitForActivityRoute()
        {
            ReleaseWanderDestination();
            lastIssue = string.IsNullOrEmpty(motion.NavigationIssue) ? "Activity route interrupted; retrying from the current position." : motion.NavigationIssue;
            currentState = StationPassengerState.WaitingForRoute;
            retryAt = Time.time + 3f;
            hasActivityDestination = false;
            motion.StopNavigation();
            motion.PlayIdle();
            RestoreAgentSpeed();
        }

        void UpdateWandering()
        {
            if (hasActivityDestination)
            {
                if (motion.NavigationFailed || Time.time - navigationStartedAt > Mathf.Max(2f, navigationTimeout))
                {
                    lastIssue = "Walking route interrupted; selecting another reachable waypoint.";
                    motion.StopNavigation();
                    motion.PlayIdle();
                    hasActivityDestination = false;
                    ReleaseWanderDestination();
                    wanderResumeAt = Time.time + 3f;
                }
                else if (motion.HasArrived)
                {
                    motion.StopNavigation();
                    motion.PlayIdle();
                    hasActivityDestination = false;
                    ReleaseWanderDestination();
                    wanderResumeAt = Time.time + Sample(wanderPause);
                }
                return;
            }
            if (Time.time < wanderResumeAt) return;
            var points = wanderPoints;
            if ((points == null || points.Length == 0) && walkingGroup) points = walkingGroup.waypoints;
            if (points == null || points.Length == 0) { wanderResumeAt = Time.time + 3f; return; }
            int first = random.Next(points.Length);
            for (int offset = 0; offset < points.Length; ++offset)
            {
                int index = (first + offset) % points.Length;
                if (!points[index] || (points.Length > 1 && index == lastWanderIndex)) continue;
                if (!TryChooseWanderDestination(points[index].position, out var destination)) continue;
                lastWanderIndex = index;
                activityDestination = destination;
                wanderDestinations[this] = destination;
                hasActivityDestination = true;
                navigationStartedAt = Time.time;
                lastIssue = "";
                RestoreAgentSpeed();
                motion.PlayWalk();
                return;
            }
            lastIssue = "No reachable walking waypoint; checking again in three seconds.";
            motion.StopNavigation();
            motion.PlayIdle();
            wanderResumeAt = Time.time + 3f;
        }

        void UpdateGroupTravel(bool returning)
        {
            if (!walkingGroup || !walkingGroup.isActiveAndEnabled)
            {
                FinishActivityReturn(returning);
                return;
            }
            if (Time.time < nextActivityPathAt) return;
            nextActivityPathAt = Time.time + .32f + (Math.Abs(randomSeed % 11) / 11f) * .09f;
            var leader = walkingGroup.CurrentLeader;
            ConfigureGroupSpeed(returning, leader);
            // A service visitor has rejoined when physically beside the group.
            // Its historical slot may be beyond the leader on a narrow path;
            // do not require walking through a companion to record the reunion.
            // Recovery still requires arrival at its explicit safe destination.
            if (returning && !recoveringWalkingGroup && HasPhysicallyRejoinedWalkingGroup(leader))
            {
                FinishActivityReturn(true);
                return;
            }
            if (!walkingGroup.TryGetDestination(this, out var destination, out var waiting))
            {
                WaitWithWalkingGroup();
                return;
            }
            var offset = destination - transform.position;
            offset.y = 0f;
            var leaderGap = leader ? leader.transform.position - transform.position : Vector3.zero;
            leaderGap.y = 0f;
            bool nearLeader = !leader || leaderGap.sqrMagnitude <= walkingGroup.cohesionDistance * walkingGroup.cohesionDistance;
            float returnTolerance = recoveringWalkingGroup ? Mathf.Min(.32f, walkingGroup.rejoinDistance) : walkingGroup.rejoinDistance;
            if (returning && nearLeader && offset.sqrMagnitude <= returnTolerance * returnTolerance)
            {
                FinishActivityReturn(true);
                return;
            }
            if (returning && Time.time - navigationStartedAt > Mathf.Max(navigationTimeout, 90f))
            { WaitForActivityRoute(); return; }
            if (waiting && leader == this)
            {
                WaitWithWalkingGroup();
                return;
            }
            groupHoldingPosition = false;
            if (!motion.TryMoveTo(destination))
            {
                if (!walkingGroup.TryGetTrailingDestination(this, out destination) || !motion.TryMoveTo(destination))
                {
                    WaitWithWalkingGroup();
                    return;
                }
            }
            activityDestination = destination;
            hasActivityDestination = true;
            motion.PlayWalk();
        }

        bool HasPhysicallyRejoinedWalkingGroup(StationPassenger leader)
        {
            if (!leader || leader == this || !leader.motion || !motion || !walkingGroup) return false;
            var ownAgent = motion.agent;
            var leaderAgent = leader.motion.agent;
            if (!ownAgent || !leaderAgent || !ownAgent.isActiveAndEnabled || !leaderAgent.isActiveAndEnabled ||
                !ownAgent.isOnNavMesh || !leaderAgent.isOnNavMesh) return false;
            var gap = leader.transform.position - transform.position;
            if (Mathf.Abs(gap.y) > .15f) return false;
            gap.y = 0f;
            float clearance = Mathf.Max(.72f, ownAgent.radius + leaderAgent.radius + .06f);
            float rejoin = walkingGroup.rejoinDistance;
            if (gap.sqrMagnitude < clearance * clearance || gap.sqrMagnitude > rejoin * rejoin ||
                !StationCrowdSpace.IsPositionAvailable(transform.position, motion, .72f)) return false;
            return !NavMesh.Raycast(ownAgent.nextPosition, leaderAgent.nextPosition, out _, ownAgent.areaMask);
        }

        void ConfigureGroupSpeed(bool returning, StationPassenger leader)
        {
            if (groupSpeedConfigured && speedProfileGroup == walkingGroup && speedProfileLeader == leader &&
                groupProfileReturning == returning) return;
            // These are fixed activity profiles, not a distance/velocity controller.
            // Inspector changes to agent.speed survive until the role changes.
            float speed = returning ? walkingGroup.catchUpSpeed : leader == this ? walkingGroup.leaderSpeed : walkingGroup.followerSpeed;
            motion.SetTravelSpeed(Mathf.Max(.1f, speed));
            speedProfileGroup = walkingGroup;
            speedProfileLeader = leader;
            groupProfileReturning = returning;
            groupSpeedConfigured = true;
        }

        void WaitWithWalkingGroup()
        {
            if (groupHoldingPosition) return;
            groupHoldingPosition = true;
            motion.StopNavigation();
            motion.PlayIdle();
        }

        bool TryChooseWanderDestination(Vector3 center, out Vector3 destination)
        {
            destination = center;
            float radius = Mathf.Max(.4f, wanderDestinationRadius);
            float phase = (float)random.NextDouble() * Mathf.PI * 2f;
            for (int attempt = 0; attempt < 12; ++attempt)
            {
                // Different seeds and radial bands distribute endpoints even
                // when many solo travellers share the same authored route.
                float angle = phase + attempt * 2.3999632f;
                float distance = radius * (.32f + .65f * ((attempt % 4) / 3f));
                var candidate = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
                if (!StationCrowdSpace.TryFindStandingPosition(candidate, motion, .25f, out var available) ||
                    !IsWanderDestinationUnclaimed(available) || !motion.TryMoveTo(available)) continue;
                destination = available;
                return true;
            }
            return false;
        }

        bool IsWanderDestinationUnclaimed(Vector3 destination)
        {
            foreach (var entry in wanderDestinations)
            {
                if (!entry.Key || entry.Key == this || !entry.Key.isActiveAndEnabled) continue;
                var gap = destination - entry.Value; gap.y = 0f;
                if (gap.sqrMagnitude < .82f * .82f) return false;
            }
            return true;
        }

        void ReleaseWanderDestination() => wanderDestinations.Remove(this);

        float Sample(Vector2 range)
        {
            if (random == null) random = new System.Random(randomSeed);
            float minimum = Mathf.Max(0f, Mathf.Min(range.x, range.y));
            return minimum + (float)random.NextDouble() * Mathf.Max(0f, Mathf.Max(range.x, range.y) - minimum);
        }

        void RestoreAgentSpeed() { if (motion && motion.agent) motion.SetTravelSpeed(Mathf.Max(.1f, defaultAgentSpeed)); }

        void SetTransitionSpeed()
        {
            if (motion.animationPlayer) motion.animationPlayer.SetPlaybackSpeed(1f / Mathf.Clamp(transitionDurationScale, .25f, 4f));
        }
        void ResetAnimationSpeed() { if (motion && motion.animationPlayer) motion.animationPlayer.SetPlaybackSpeed(1f); }

        public void NotifySeatUnavailable(StationSeat seat)
        {
            if (requestedSeat == seat) requestedSeat = null;
            if (currentSeat != seat) return;
            currentSeat = null;
            if (isActiveAndEnabled && motion)
            {
                // Only an actor physically using the bench needs the local escape point.
                // A traveller whose distant reservation was removed must reroute from their current position.
                if (currentState == StationPassengerState.Seated || currentState == StationPassengerState.SittingDown ||
                    currentState == StationPassengerState.StandingUp)
                    motion.EnableNavigationAt(seat.ApproachPosition);
                BeginReturnToSeat();
            }
        }

        void OnDisable()
        {
            ReleaseWanderDestination();
            ClearApproachProgress();
            recoveringWalkingGroup = false;
            currentState = StationPassengerState.Disabled;
            goingToDesk = false;
            if (!Application.isPlaying) return;
            RestoreServiceNavigation();
            if (currentService) currentService.Cancel(this);
            currentService = null;
            if (conversationMember) conversationMember.SuspendForActivity();
            if (currentSeat) currentSeat.Release(this);
            if (requestedSeat) requestedSeat.Release(this);
            currentSeat = null;
            requestedSeat = null;
            if (motion) motion.StopNavigation();
        }
    }
}
