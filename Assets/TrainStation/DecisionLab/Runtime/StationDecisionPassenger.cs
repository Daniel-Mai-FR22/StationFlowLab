using System;
using System.Collections.Generic;
using System.Linq;
using StationFlow.TrainStation.RailOperations;
using StationFlow.TrainStation.Social;
using TMPro;
using UnityEngine;
using UnityEngine.AI;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Activity intentions above the original FIFO executor and native NavMesh locomotion.</summary>
    [DefaultExecutionOrder(180)]
    public sealed class StationDecisionPassenger : MonoBehaviour
    {
        public StationDecisionWorld world;
        public StationActorMotion motion;
        public StationPassenger passenger;
        public string passengerId, serviceId;
        public float departureOffsetSeconds;
        public DecisionNeeds needs = new DecisionNeeds();
        [Range(0f, 1f)] public float familiarity = .5f;
        [Min(0f)] public float safetyMarginSeconds = 12f;
        [Min(0f)] public float startDelaySeconds;
        [Min(.1f)] public float walkSpeed = 1.15f, fastWalkSpeed = 1.65f;
        public bool platformKnownInitially;
        public TMP_Text intentLabel;
        public DecisionPolicyParameters policyParameters = new DecisionPolicyParameters();

        public DecisionActivity CurrentActivity { get; private set; } = DecisionActivity.Display;
        public string ExecutionState { get; private set; } = "Ready";
        public string CurrentTargetId => target ? target.poiId : "";
        public float BudgetSeconds { get; private set; }
        public bool Finished { get; private set; }
        public bool Boarded { get; private set; }
        public int DecisionCount { get; private set; }
        public List<string> ActivityHistory = new List<string>();
        public string LastDecisionReason { get; private set; } = "Initial station entry";
        public bool HasCheckedDisplay { get; private set; }
        public int CompletedActivities { get; private set; }
        public int CompletedOptionalCount => completedOptional.Count;
        public string NavigationIssue { get; private set; } = "";
        public bool BoardingViolation { get; private set; }
        public bool PlatformKnown { get; private set; }
        public DecisionTripObservation KnownTrip { get; private set; }
        public StationSeat ReservedSeat => seatVisit != null ? seatVisit.Seat : null;
        public string CurrentSeatId => ReservedSeat ? ReservedSeat.seatId : "";
        public DecisionSeatedActivity SeatedActivity => seatVisit != null && !seatVisit.Finished && target ? target.seatedActivity : DecisionSeatedActivity.None;
        public bool IsSeated => seatVisit != null && !seatVisit.Finished && seatVisit.State == "Seated";
        public float SeatedSecondsRemaining => seatVisit != null && world ? seatVisit.SecondsRemaining(world.ElapsedSeconds) : 0f;
        public float ToiletSecondsRemaining => world && CurrentActivity == DecisionActivity.Toilet && ExecutionState == "WCUsing" ?
            Mathf.Max(0f, endsAt - world.ElapsedSeconds) : 0f;
        public DecisionArrivalPlan ArrivalPlan { get; private set; }
        public string JourneyId => ArrivalPlan != null ? ArrivalPlan.journeyId : passengerId;
        public string ShortJourneyLabel => ArrivalPlan == null ? passengerId : (string.IsNullOrEmpty(ArrivalPlan.trainNumber) ? "Voyageur" : ArrivalPlan.trainNumber) +
            " / " + (IsAlighter ? "A" : "V") + JourneyId.Substring(JourneyId.LastIndexOf('/') + 1);
        public bool HasEntered { get; private set; }
        public DecisionArrivalProfile ArrivalProfile => ArrivalPlan != null ? ArrivalPlan.arrivalProfile : DecisionArrivalProfile.Ordinary;
        public string GroupId => ArrivalPlan != null ? ArrivalPlan.groupId : "";
        public int RepresentedPassengers => ArrivalPlan != null ? ArrivalPlan.representedPassengers : 1;
        public bool IsAlighter => ArrivalPlan != null && ArrivalPlan.purpose == DecisionJourneyPurpose.Alighting;
        public bool IsAlightingServiceVisitor => IsAlighter && ArrivalPlan.alightingServiceVisitor;
        public int AlightingServiceVisitsCompleted { get; private set; }
        public int AlightingServiceVisitsRemaining => IsAlightingServiceVisitor ?
            Mathf.Max(0, ArrivalPlan.alightingServiceVisitLimit - AlightingServiceVisitsCompleted) : 0;
        public float AlightingStopoverDeadlineSeconds { get; private set; }
        public StationDecisionTravelGroup TravelGroup => arrivalController ? arrivalController.GetGroup(GroupId) : null;
        public DecisionMobilityProfile MobilityProfile => ArrivalPlan != null ? ArrivalPlan.mobilityProfile : null;
        public bool UsingGroupPace => !string.IsNullOrEmpty(GroupId) && TravelGroup != null && !TravelGroup.IsSeparated;
        public StationDecisionPlatformGate PlatformGate => cachedPlatformGate;
        public bool HasPlatformAccess => !PlatformGate || IsAlighter || PlatformGate.HasPassed(JourneyId);
        public StationDecisionCoachQueue CoachQueue => cachedCoachQueue;
        public StationDecisionToiletQueue ToiletQueue => target && target.toiletQueue ? target.toiletQueue : cachedToiletQueue;
        public bool BoardingQueueValidated { get; private set; }
        public Vector3 AssignedBoardingPosition
        {
            get
            {
                var platform = world ? world.GetPlatform(ArrivalPlan != null ? ArrivalPlan.trackNumber : KnownTrip.trackNumber) : null;
                if (ArrivalPlan != null && world && world.TryGetCoachAnchor(ArrivalPlan.trackNumber, ArrivalPlan.coachNumber, out var anchor))
                {
                    uint h = unchecked((uint)StableHash(JourneyId));
                    float angle = (h % 360) * Mathf.Deg2Rad;
                    return anchor.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * .3f;
                }
                return platform ? platform.DestinationFor(this) : transform.position;
            }
        }

        StationDecisionPoi target;
        readonly HashSet<DecisionActivity> completedOptional = new HashSet<DecisionActivity>();
        readonly HashSet<DecisionActivity> completedAlightingServices = new HashSet<DecisionActivity>();
        readonly Dictionary<string, float> rejectedUntil = new Dictionary<string, float>();
        readonly DecisionFailureBudget noFeasibleBudget = new DecisionFailureBudget();
        System.Random choiceRandom, durationRandom;
        float stateStarted, endsAt, nextDecisionAt, nextBudgetCheck, lastObservationAt = -100f;
        float lastProgressAt, previousRemaining = float.PositiveInfinity;
        float sampledDwell, platformEta;
        int displayVisits;
        bool interruption, servicing, serviceResultPending, serviceSuccess, suppressServiceCallback;
        bool counterServiceCompleted, pendingSeatInterruption;
        string pendingSeatReason;
        float sampledSeatedDwell;
        StationDecisionSeatVisit seatVisit;
        Vector3 destination;
        StationDecisionArrivalController arrivalController;
        bool started, initializeOnNextUpdate, cohesionHolding, boardingRejected;
        StationDecisionPlatformGate cachedPlatformGate;
        StationDecisionCoachQueue cachedCoachQueue;
        StationDecisionToiletQueue cachedToiletQueue;
        bool pendingToiletInterruption;
        string pendingToiletReason;
        bool controlledQueueNavigation;
        float previousQueueStoppingDistance;

        void Start()
        {
            started = true;
            InitializeJourney();
        }

        void OnEnable() { if (started && ArrivalPlan != null) initializeOnNextUpdate = true; }

        /// <summary>Bind a new immutable identity only while the actor is inactive; active journeys are never teleported or overwritten.</summary>
        public void PrepareJourney(DecisionArrivalPlan plan, StationDecisionArrivalController controller)
        {
            if (gameObject.activeInHierarchy || plan == null || !controller) throw new InvalidOperationException("PrepareJourney requires an inactive pool actor and valid plan/controller.");
            if (!passenger) passenger = GetComponent<StationPassenger>();
            if (!motion) motion = GetComponent<StationActorMotion>();
            if (passenger) passenger.ExternalServiceCompleted -= ServiceCompleted;
            if (seatVisit != null) seatVisit.ReleaseOnDisable();
            ReleaseTarget();
            arrivalController = controller; ArrivalPlan = plan; world = controller.world;
            cachedPlatformGate = StationDecisionPlatformGate.For(world, plan.trackNumber);
            cachedCoachQueue = StationDecisionCoachQueue.For(world, plan.trackNumber, plan.coachNumber);
            cachedToiletQueue = StationDecisionToiletQueue.For(world);
            BoardingQueueValidated = false;
            passengerId = plan.journeyId; serviceId = plan.serviceId; departureOffsetSeconds = plan.publishedDepartureSeconds;
            needs = new DecisionNeeds { thirst = plan.needs.thirst, toilet = plan.needs.toilet, fatigue = plan.needs.fatigue,
                shopping = plan.needs.shopping, information = plan.needs.information };
            walkSpeed = plan.walkSpeed; fastWalkSpeed = plan.fastWalkSpeed; familiarity = plan.familiarity; safetyMarginSeconds = plan.safetyMarginSeconds;
            policyParameters = new DecisionPolicyParameters(); platformKnownInitially = IsAlighter;
            startDelaySeconds = 0f; HasEntered = Finished = Boarded = BoardingViolation = HasCheckedDisplay = PlatformKnown = false;
            CurrentActivity = IsAlighter ? DecisionActivity.Exit : DecisionActivity.Display; ExecutionState = "Scheduled";
            DecisionCount = CompletedActivities = displayVisits = 0; BudgetSeconds = 0f;
            ActivityHistory.Clear(); completedOptional.Clear(); completedAlightingServices.Clear(); rejectedUntil.Clear();
            AlightingServiceVisitsCompleted = 0; AlightingStopoverDeadlineSeconds = 0f;
            if (IsAlighter && controller.scenario.alightingStopovers != null)
            {
                var stopover = controller.scenario.alightingStopovers;
                policyParameters.alightingInfoWeight = stopover.infoPreference;
                policyParameters.alightingShopWeight = stopover.shopPreference;
                policyParameters.alightingToiletWeight = stopover.toiletPreference;
                policyParameters.alightingExitWeight = stopover.exitPreference;
            }
            noFeasibleBudget.Reset();
            stateStarted = endsAt = nextDecisionAt = nextBudgetCheck = lastProgressAt = sampledDwell = sampledSeatedDwell = platformEta = 0f;
            lastObservationAt = -100f; previousRemaining = float.PositiveInfinity;
            interruption = servicing = serviceResultPending = serviceSuccess = suppressServiceCallback = counterServiceCompleted = pendingSeatInterruption = false;
            cohesionHolding = boardingRejected = false; pendingSeatReason = null; seatVisit = null; destination = default;
            pendingToiletInterruption = false; pendingToiletReason = null;
            KnownTrip = default; NavigationIssue = ""; LastDecisionReason = IsAlighter ? "Virtual alighting; walk to station exit" : "Scheduled station entry";
            choiceRandom = durationRandom = null; initializeOnNextUpdate = started;
            if (passenger) { passenger.externalActivityControl = true; passenger.automaticRequests = false; }
            if (motion && motion.agent && PlatformGate) motion.agent.areaMask = StationDecisionPlatformGate.DefaultAreaMask(motion.agent.areaMask);
        }

        void InitializeJourney()
        {
            if (HasEntered) { initializeOnNextUpdate = false; return; }
            if (!passenger) passenger = GetComponent<StationPassenger>();
            if (!motion) motion = GetComponent<StationActorMotion>();
            if (!world || !motion || !passenger || !passenger.externalActivityControl)
            { Finish(false, "Invalid experiment component references or external control opt-in."); return; }
            if (!cachedToiletQueue) cachedToiletQueue = StationDecisionToiletQueue.For(world);
            int hash = StableHash(passengerId);
            choiceRandom = new System.Random(unchecked(world.seed ^ hash));
            durationRandom = new System.Random(unchecked(world.seed ^ hash ^ 7919));
            passenger.ExternalServiceCompleted -= ServiceCompleted;
            passenger.ExternalServiceCompleted += ServiceCompleted;
            var actual = world.ObserveTrip(serviceId);
            KnownTrip = new DecisionTripObservation
            { serviceId = serviceId, trackNumber = platformKnownInitially ? actual.trackNumber : 0,
                departureSeconds = departureOffsetSeconds, cutoffSeconds = departureOffsetSeconds -
                    (actual.departureSeconds - actual.cutoffSeconds), status = DecisionTrainStatus.Scheduled };
            PlatformKnown = platformKnownInitially;
            nextDecisionAt = ArrivalPlan != null ? world.ElapsedSeconds : startDelaySeconds;
            RefreshMobilityPace();
            motion.SetTravelSpeed(walkSpeed);
            if (IsAlighter)
            {
                KnownTrip = actual; PlatformKnown = HasCheckedDisplay = true;
                AlightingStopoverDeadlineSeconds = world.ElapsedSeconds + ArrivalPlan.alightingStopoverSeconds;
                LastDecisionReason = IsAlightingServiceVisitor ? "Alighting; seeded station services within an independent budget" :
                    "Alighting; direct station exit";
            }
            HasEntered = true; initializeOnNextUpdate = false; ExecutionState = "Ready";
            if (arrivalController) arrivalController.MarkEntered(this);
        }

        /// <summary>Apply a discrete solo/party profile change; native acceleration, pathfinding and avoidance remain untouched.</summary>
        public void RefreshMobilityPace()
        {
            var profile = MobilityProfile;
            if (profile == null) return; // Original laboratory actors retain their authored speed settings.
            float previousWalk = walkSpeed, previousFast = fastWalkSpeed;
            walkSpeed = UsingGroupPace ? ArrivalPlan.walkSpeed : profile.preferredWalkSpeed;
            fastWalkSpeed = UsingGroupPace ? ArrivalPlan.fastWalkSpeed : profile.preferredFastWalkSpeed;
            // The original FIFO executor owns its temporary queue-advance cap until the counter visit ends.
            if (!motion || ExecutionState == "Service") return;
            float requested = motion.RequestedTravelSpeed;
            if (Mathf.Abs(requested - previousFast) < .001f) motion.SetTravelSpeed(fastWalkSpeed);
            else if (Mathf.Abs(requested - previousWalk) < .001f) motion.SetTravelSpeed(walkSpeed);
        }

        void Update()
        {
            if (initializeOnNextUpdate) InitializeJourney();
            if (Finished || !world || world.Paused || !world.rail || !world.rail.IsReady || choiceRandom == null) return;
            // A native link is between baked floors: route sampling there can fail,
            // and ResetPath would complete the link immediately. Let Unity finish
            // its traversal before estimating budgets or changing the activity.
            if (motion && motion.agent && motion.agent.enabled && motion.agent.isOnOffMeshLink)
            {
                lastProgressAt = world.ElapsedSeconds;
                return;
            }
            ConsumeServiceResult();
            if (Finished) return;
            float now = world.ElapsedSeconds;
            if (now >= nextBudgetCheck)
            {
                nextBudgetCheck = now + .75f;
                bool seatedExit = seatVisit != null && !seatVisit.Finished && seatVisit.RequiresStanding;
                Vector3 departureOrigin = seatedExit ? seatVisit.ExitPosition : transform.position;
                if (IsAlighter)
                {
                    float exitEta = EstimateExitTravel(departureOrigin, walkSpeed);
                    if (seatedExit) exitEta += Mathf.Max(.1f, motion.Duration(motion.standUpId));
                    BudgetSeconds = Finite(exitEta) ? AlightingStopoverDeadlineSeconds - now - exitEta - safetyMarginSeconds : -99999f;
                    if (ExecutionState != "Ready" && ExecutionState != "Regrouping" && ExecutionState != "WCClearing" &&
                        StationDecisionPolicy.IsAlightingStationService(CurrentActivity) && BudgetSeconds < 2f)
                        Interrupt("Alighting station-service budget exhausted; leave through a station exit");
                }
                else
                {
                    var platform = world.GetPlatform(PlatformKnown ? KnownTrip.trackNumber : world.ObserveTrip(serviceId).trackNumber);
                    platformEta = platform ? EstimatePlatformAccess(departureOrigin, walkSpeed) : 9999f;
                    if (seatedExit) platformEta += Mathf.Max(.1f, motion.Duration(motion.standUpId));
                    BudgetSeconds = Finite(platformEta) ? KnownTrip.cutoffSeconds - now - platformEta - safetyMarginSeconds : -99999f;
                    // Optional activities may be abandoned when actual travel/queues consume their reserve.
                    if (ExecutionState != "Ready" && CurrentActivity != DecisionActivity.Platform && CurrentActivity != DecisionActivity.Exit &&
                        CurrentActivity != DecisionActivity.Info && CurrentActivity != DecisionActivity.Display &&
                        ExecutionState != "WCClearing" && BudgetSeconds < 2f)
                        Interrupt("Activity budget exhausted");
                }
            }
            if (target && CurrentActivity == DecisionActivity.Toilet && ToiletQueue)
            { UpdateToiletVisit(now); return; }
            if (seatVisit != null && !seatVisit.Finished) { UpdateSeatVisit(now); return; }
            if (target && CurrentActivity == DecisionActivity.Platform && PlatformGate &&
                (!PlatformGate.HasPassed(JourneyId) || ExecutionState.StartsWith("Gate", StringComparison.Ordinal)))
            { UpdatePlatformAccess(now); return; }
            if (target && CurrentActivity == DecisionActivity.Platform && HasPlatformAccess && CoachQueue)
            { UpdateCoachQueue(now); return; }
            if (ExecutionState == "Ready" || ExecutionState == "Regrouping")
            {
                if (now >= nextDecisionAt) Decide(LastDecisionReason);
                return;
            }
            if (ExecutionState == "Service") return;
            if (ExecutionState == "WaitingForGroup" && cohesionHolding) ExecutionState = "Walking";
            if (ExecutionState == "Walking" || ExecutionState == "ClearingCounter")
            {
                if (ExecutionState == "Walking" && TravelGroup != null && TravelGroup.HoldForCohesion(this))
                {
                    if (motion.agent && motion.agent.enabled && motion.agent.isOnNavMesh) motion.agent.isStopped = true;
                    motion.PlayIdle(); cohesionHolding = true; ExecutionState = "WaitingForGroup"; lastProgressAt = now; return;
                }
                if (cohesionHolding)
                {
                    if (motion.agent && motion.agent.enabled && motion.agent.isOnNavMesh) motion.agent.isStopped = false;
                    motion.PlayWalk(); cohesionHolding = false; lastProgressAt = now;
                }
                if (motion.NavigationFailed) { FailWalking(motion.NavigationIssue); return; }
                if (motion.HasArrived)
                {
                    motion.StopNavigation();
                    motion.PlayIdle();
                    if (ExecutionState == "ClearingCounter") { ContinueAfterCounter(); return; }
                    ExecutionState = CurrentActivity == DecisionActivity.Platform ? "WaitingForTrain" : "Using";
                    stateStarted = now;
                    endsAt = now + sampledDwell;
                    if (CurrentActivity == DecisionActivity.Platform) TryBoard();
                    return;
                }
                var agent = motion.agent;
                if (agent && agent.enabled && agent.isOnNavMesh && !agent.pathPending && agent.hasPath)
                {
                    if (StationDecisionNavigationProgress.TryGetRemainingDistance(motion, out float remaining) &&
                        remaining < previousRemaining - .2f)
                    { previousRemaining = remaining; lastProgressAt = now; }
                    if (now - lastProgressAt > 25f)
                    { FailWalking("Native navigation made no progress for 25 seconds."); return; }
                }
                // A slow mobility profile may legitimately need more than four minutes.
                // Keep the native-path progress and activity-budget checks above.
            }
            else if (ExecutionState == "Using")
            {
                if (target && target.facingTarget) motion.Face(target.facingTarget.position);
                if (now >= endsAt) CompleteActivity();
            }
            else if (ExecutionState == "WaitingForTrain") TryBoard();
        }

        void LateUpdate()
        {
            if (!intentLabel) return;
            intentLabel.text = ShortJourneyLabel + "\n" + (Finished ? ExecutionState : CurrentActivity.ToString());
            Camera camera = Camera.main;
            if (!camera || !camera.isActiveAndEnabled)
                foreach (var candidate in Camera.allCameras) if (candidate.isActiveAndEnabled) { camera = candidate; break; }
            if (camera) intentLabel.transform.rotation = camera.transform.rotation;
        }

        void Decide(string trigger)
        {
            var options = BuildOptions();
            var known = KnownTrip;
            var status = known.status;
            if (boardingRejected) status = DecisionTrainStatus.Closed;
            else if (!IsAlighter && status != DecisionTrainStatus.Cancelled && world.ElapsedSeconds >= known.cutoffSeconds)
                status = world.ElapsedSeconds >= known.departureSeconds ? DecisionTrainStatus.Departed : DecisionTrainStatus.Closed;
            var context = new DecisionContext
            { previousActivity = CurrentActivity, trainStatus = status, nowSeconds = world.ElapsedSeconds,
                boardingCutoffSeconds = known.cutoffSeconds, platformKnown = PlatformKnown, needs = needs,
                familiarity = familiarity, safetyMarginSeconds = safetyMarginSeconds, displayVisits = displayVisits,
                receivedDisruption = interruption, requireInitialDisplay = !HasCheckedDisplay && !IsAlighter,
                alightingJourney = IsAlighter, stationStopoverDeadlineSeconds = AlightingStopoverDeadlineSeconds,
                stationServicesRemaining = AlightingServiceVisitsRemaining };
            var evaluations = StationDecisionPolicy.Evaluate(context, options, policyParameters);
            if (TravelGroup != null && !TravelGroup.CoordinateDecision(this, evaluations))
            {
                ExecutionState = "Regrouping"; motion.PlayIdle(); nextDecisionAt = world.ElapsedSeconds + .25f; return;
            }
            var chosen = StationDecisionPolicy.Choose(evaluations, choiceRandom);
            ++DecisionCount;
            world.RecordDecision(this, context, evaluations, chosen, trigger);
            interruption = false;
            if (chosen == null)
            {
                bool awaitingDatedCutoff = !IsAlighter && ArrivalPlan != null && world.ElapsedSeconds < known.cutoffSeconds &&
                    evaluations.Any(e => e.option != null && e.option.reachable);
                bool awaitingCapacity = DecisionFailureBudget.HasTemporaryCapacityRecovery(context, evaluations);
                if (noFeasibleBudget.RecordNoChoice(awaitingDatedCutoff || awaitingCapacity))
                { Finish(false, "No feasible action after consecutive route or unrecoverable destination failures."); return; }
                LastDecisionReason = awaitingDatedCutoff ? "No feasible action; waiting for information, capacity or assigned cutoff" :
                    awaitingCapacity ? "No feasible action; waiting for a reachable destination to have capacity" :
                    "No feasible action; retry after route or destination recovery";
                nextDecisionAt = world.ElapsedSeconds + 3f;
                return;
            }
            StationDecisionPoi selected = null;
            foreach (var poi in world.pois) if (poi && poi.poiId == chosen.option.poiId) { selected = poi; break; }
            if (!selected || !selected.TryClaim(this))
            {
                LastDecisionReason = "Destination became full before commitment"; nextDecisionAt = world.ElapsedSeconds + 1f; return;
            }
            noFeasibleBudget.Reset();
            target = selected;
            TravelGroup?.Committed(this, selected.poiId);
            counterServiceCompleted = pendingSeatInterruption = false;
            seatVisit = null;
            CurrentActivity = selected.activity;
            ActivityHistory.Add(CurrentActivity.ToString());
            LastDecisionReason = chosen.reason;
            sampledDwell = target.service ? target.service.conversationDuration :
                world.IsDaily && CurrentActivity == DecisionActivity.Toilet ?
                    StationDecisionPolicy.SampleBoundedDuration(durationRandom, target.meanDurationSeconds, target.durationSpreadSeconds, 120f, 240f) :
                    StationDecisionPolicy.SamplePositiveDuration(durationRandom, target.meanDurationSeconds, target.durationSpreadSeconds);
            sampledSeatedDwell = target.HasSeatedActivity ?
                StationDecisionPolicy.SamplePositiveDuration(durationRandom, target.seatedMeanDurationSeconds, target.seatedDurationSpreadSeconds) : 0f;
            float normalSlack = KnownTrip.cutoffSeconds - world.ElapsedSeconds - platformEta - safetyMarginSeconds;
            motion.SetTravelSpeed(CurrentActivity == DecisionActivity.Platform && normalSlack < 25f ? fastWalkSpeed : walkSpeed);
            if (target.service)
            {
                servicing = true;
                if (!passenger.TryVisitService(target.service))
                { servicing = false; FailActivity("Original FIFO executor rejected the service visit."); return; }
                ExecutionState = "Service";
                stateStarted = world.ElapsedSeconds;
            }
            else
            {
                if (CurrentActivity == DecisionActivity.Toilet && ToiletQueue)
                { StartGateMove(ToiletQueue.PositionFor(this), "WCApproach"); return; }
                if (target.HasSeatedActivity)
                {
                    if (!TryBeginSeating(out string reason)) FailActivity(reason);
                    return;
                }
                if (CurrentActivity == DecisionActivity.Platform && PlatformGate && !HasPlatformAccess)
                { StartGateMove(PlatformGate.GetApproachPosition(this), "GateApproach"); return; }
                if (CurrentActivity == DecisionActivity.Platform && CoachQueue)
                { StartGateMove(CoachQueue.PositionFor(this), "CoachApproach"); return; }
                destination = CurrentActivity == DecisionActivity.Platform ? AssignedBoardingPosition : target.DestinationFor(this);
                if (!motion.TryMoveTo(destination)) { FailActivity(motion.NavigationIssue); return; }
                motion.PlayWalk();
                ExecutionState = "Walking";
                stateStarted = lastProgressAt = world.ElapsedSeconds;
                previousRemaining = float.PositiveInfinity;
            }
        }

        List<DecisionOption> BuildOptions()
        {
            var result = new List<DecisionOption>();
            var platform = IsAlighter ? null : world.GetPlatform(PlatformKnown ? KnownTrip.trackNumber : world.ObserveTrip(serviceId).trackNumber);
            float directEta = platform ? EstimatePlatformAccess(transform.position, walkSpeed) : 9999f;
            bool hurry = !IsAlighter && KnownTrip.cutoffSeconds - world.ElapsedSeconds - directEta - safetyMarginSeconds < 25f;
            foreach (var poi in world.pois)
            {
                if (IsAlighter && poi && poi.activity != DecisionActivity.Exit &&
                    (!IsAlightingServiceVisitor || AlightingServiceVisitsRemaining == 0 || !StationDecisionPolicy.IsAlightingStationService(poi.activity))) continue;
                if (!poi || (poi.activity == DecisionActivity.Platform && poi != platform)) continue;
                var position = poi.activity == DecisionActivity.Platform ? AssignedBoardingPosition : poi.DestinationFor(this);
                float eta = EstimateTravel(transform.position, position,
                    poi.activity == DecisionActivity.Platform && hurry ? fastWalkSpeed : walkSpeed);
                Vector3 afterCounter = position;
                if (poi.service && poi.service.queueSlots.Length > 0 && poi.service.queueSlots[0]) afterCounter = poi.service.queueSlots[0].position;
                if (poi.service && poi.departurePoint)
                {
                    eta += EstimateTravel(afterCounter, poi.departurePoint.position, walkSpeed);
                    afterCounter = poi.departurePoint.position;
                }
                var toiletQueue = poi.activity == DecisionActivity.Toilet ? poi.toiletQueue : null;
                if (toiletQueue && toiletQueue.exitPoint && toiletQueue.usePoints.Length > 0 && toiletQueue.usePoints[0])
                {
                    var usePosition = toiletQueue.usePoints[0].position;
                    eta += EstimateTravel(position, usePosition, walkSpeed) + EstimateTravel(usePosition, toiletQueue.exitPoint.position, walkSpeed);
                    afterCounter = toiletQueue.exitPoint.position;
                }
                float queueWait = poi.service ? Mathf.Max(poi.service.QueueCount, poi.ServiceDemandCount) * poi.service.conversationDuration :
                    toiletQueue ? toiletQueue.EstimatedWaitSeconds(this) :
                    poi.activity == DecisionActivity.Platform ?
                        (PlatformGate && !HasPlatformAccess ? PlatformGate.EstimatedWaitSeconds(this, EstimateGateTravel(transform.position, hurry ? fastWalkSpeed : walkSpeed)) : 0f) +
                        (CoachQueue ? CoachQueue.EstimatedWaitSeconds(this) : 0f) : 0f;
                float dwell = poi.activity == DecisionActivity.Platform || poi.activity == DecisionActivity.Exit ? 0f :
                    poi.service ? poi.service.conversationDuration : poi.meanDurationSeconds;
                float beforeOnward = eta + queueWait + dwell;
                float onward = poi.activity == DecisionActivity.Platform || poi.activity == DecisionActivity.Exit ? 0f :
                    IsAlighter ? EstimateExitTravel(afterCounter, walkSpeed) :
                    platform ? EstimatePlatformAccess(afterCounter, walkSpeed, beforeOnward) : 9999f;
                float seatTravel = 0f, seatedOnward = 0f;
                bool hasSeat = poi.HasSeatedActivity && FindSeat(poi, poi.service ? afterCounter : transform.position,
                    out _, out seatTravel, out seatedOnward, poi.service ? beforeOnward : 0f, poi.seatedMeanDurationSeconds);
                if (hasSeat) { onward = seatedOnward; if (!poi.service) eta = 0f; }
                bool optional = IsOptional(poi.activity);
                bool cooling = rejectedUntil.TryGetValue(poi.poiId, out var until) && world.ElapsedSeconds < until;
                bool redundantDisplay = poi.activity == DecisionActivity.Display && HasCheckedDisplay &&
                    world.ElapsedSeconds - lastObservationAt < 18f && !interruption;
                bool repeatedAlightingService = IsAlighter && completedAlightingServices.Contains(poi.activity);
                result.Add(new DecisionOption
                { activity = poi.activity, poiId = poi.poiId, travelSeconds = Finite(eta) ? eta : 99999,
                    toPlatformSeconds = IsAlighter ? 0f : Finite(onward) ? onward : 99999,
                    toExitSeconds = IsAlighter ? Finite(onward) ? onward : 99999 : 0f,
                    queueSeconds = queueWait,
                    durationSeconds = hasSeat && !poi.service ? 0f : dwell,
                    seatTravelSeconds = hasSeat ? seatTravel : 0f,
                    seatingTransitionSeconds = hasSeat ? SeatTransitionDuration : 0f,
                    seatedDurationSeconds = hasSeat ? poi.seatedMeanDurationSeconds : 0f,
                    reachable = Finite(eta) && (poi.activity == DecisionActivity.Exit || Finite(onward)),
                    available = poi.Available && !cooling && !redundantDisplay && !repeatedAlightingService && !(optional && completedOptional.Contains(poi.activity)) &&
                        (!poi.HasSeatedActivity || poi.service || hasSeat),
                    temporarilyFull = poi.TemporarilyFull && !cooling && !redundantDisplay && !repeatedAlightingService && !(optional && completedOptional.Contains(poi.activity)) &&
                        (!poi.HasSeatedActivity || poi.service || hasSeat),
                    preference = poi.activity == DecisionActivity.Info && lastObservationAt > 0f && needs.information < .15f ? .2f : 1f });
            }
            return result;
        }

        float SeatTransitionDuration => Mathf.Max(.1f, motion.Duration(motion.sitDownId)) + Mathf.Max(.1f, motion.Duration(motion.standUpId));

        bool FindSeat(StationDecisionPoi poi, Vector3 from, out StationSeat selected, out float travel, out float onward,
            float beforeSeatSeconds = 0f, float seatedDurationSeconds = 0f)
        {
            selected = null; travel = onward = 0f;
            if (!poi || !poi.HasSeatedActivity) return false;
            var platform = world.GetPlatform(PlatformKnown ? KnownTrip.trackNumber : world.ObserveTrip(serviceId).trackNumber);
            if (!platform && !IsAlighter) return false;
            float best = float.PositiveInfinity;
            foreach (var seat in poi.seats)
            {
                if (!StationDecisionSeatVisit.IsUsable(seat, passenger, motion)) continue;
                float candidateTravel = EstimateTravel(from, seat.ApproachPosition, walkSpeed);
                float candidateOnward = IsAlighter ? EstimateExitTravel(seat.ApproachPosition, walkSpeed) :
                    EstimatePlatformAccess(seat.ApproachPosition, walkSpeed,
                        beforeSeatSeconds + candidateTravel + SeatTransitionDuration + seatedDurationSeconds);
                if (!Finite(candidateTravel) || !Finite(candidateOnward) || candidateTravel + candidateOnward >= best) continue;
                selected = seat; travel = candidateTravel; onward = candidateOnward; best = travel + onward;
            }
            return selected;
        }

        float EstimateTravel(Vector3 from, Vector3 to, float speed)
        {
            int mask = motion && motion.agent ? motion.agent.areaMask : NavMesh.AllAreas;
            if (PlatformGate && !IsAlighter) mask = StationDecisionPlatformGate.PlanningAreaMask(mask);
            if (!NavMesh.SamplePosition(from, out var start, .8f, mask) || !NavMesh.SamplePosition(to, out var end, .8f, mask)) return float.PositiveInfinity;
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(start.position, end.position, mask, path) || path.status != NavMeshPathStatus.PathComplete) return float.PositiveInfinity;
            float length = 0f;
            for (int i = 1; i < path.corners.Length; ++i) length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            return length / Mathf.Max(.1f, speed) * Mathf.Max(1f, world.congestionAllowance);
        }

        float EstimateGateTravel(Vector3 from, float speed) => PlatformGate && PlatformGate.hallCrossingPoint ?
            EstimateTravel(from, PlatformGate.hallCrossingPoint.position, speed) : 0f;

        float EstimateExitTravel(Vector3 from, float speed)
        {
            float best = float.PositiveInfinity;
            foreach (var poi in world.pois)
                if (poi && poi.isActiveAndEnabled && poi.activity == DecisionActivity.Exit)
                    best = Mathf.Min(best, EstimateTravel(from, poi.DestinationFor(this), speed));
            return best;
        }

        float EstimatePlatformAccess(Vector3 from, float speed, float secondsUntilDeparture = 0f) => EstimateTravel(from, AssignedBoardingPosition, speed) +
            (PlatformGate && !HasPlatformAccess ? PlatformGate.EstimatedWaitSeconds(this,
                secondsUntilDeparture + EstimateGateTravel(from, speed)) : 0f) +
            (CoachQueue ? CoachQueue.EstimatedWaitSeconds(this) : 0f);

        void StartGateMove(Vector3 position, string state)
        {
            destination = position;
            if (motion.agent)
            {
                if (!controlledQueueNavigation) previousQueueStoppingDistance = motion.agent.stoppingDistance;
                controlledQueueNavigation = true;
                motion.agent.stoppingDistance = .06f;
            }
            motion.SetTravelSpeed(BudgetSeconds < 25f ? fastWalkSpeed : walkSpeed);
            if (!motion.TryMoveTo(destination)) { FailActivity("Platform control route unavailable: " + motion.NavigationIssue); return; }
            motion.PlayWalk(); cohesionHolding = false; ExecutionState = state;
            stateStarted = lastProgressAt = world.ElapsedSeconds; previousRemaining = float.PositiveInfinity;
        }

        void UpdatePlatformAccess(float now)
        {
            var gate = PlatformGate;
            var actual = world.ObserveTrip(serviceId);
            if (!gate.CanProceed(this) && (actual.status == DecisionTrainStatus.Cancelled || now >= actual.cutoffSeconds))
            { KnownTrip = actual; PlatformKnown = true; Interrupt("Platform access closed for assigned train"); return; }
            if (gate.HasPassed(JourneyId))
            {
                if (actual.status == DecisionTrainStatus.Cancelled || now >= actual.cutoffSeconds)
                { KnownTrip = actual; PlatformKnown = true; Interrupt("Train closed after controlled passage"); return; }
                if (CoachQueue) StartGateMove(CoachQueue.PositionFor(this), "CoachApproach");
                else StartGateMove(AssignedBoardingPosition, "Walking");
                return;
            }
            bool crossing = gate.CanProceed(this);
            if (!crossing && !gate.Contains(JourneyId)) gate.TryJoin(this, out _);
            Vector3 next = crossing ? gate.CrossingPosition(this) : gate.Contains(JourneyId) ? gate.QueuePosition(this) : gate.GetApproachPosition(this);
            string state = crossing ? "GateCrossing" : gate.Contains(JourneyId) ? "GateQueue" : "GateApproach";
            if ((destination-next).sqrMagnitude > .04f || ExecutionState == "Walking" || ExecutionState == "Ready" || ExecutionState == "Regrouping")
            { StartGateMove(next, state); return; }
            if (motion.NavigationFailed) { FailActivity("Platform control navigation: " + motion.NavigationIssue); return; }
            if (Vector3.Distance(transform.position, next) <= .12f)
            {
                motion.StopNavigation(); motion.PlayIdle(); ExecutionState = crossing ? "GateCrossing" : gate.Contains(JourneyId) ? "GateQueue" : "GateWaiting";
                return;
            }
            if (motion.agent && motion.agent.enabled && motion.agent.isOnNavMesh && !motion.agent.pathPending && (!motion.IsNavigatingToRequestedDestination || motion.agent.isStopped))
            { StartGateMove(next, state); return; }
            ExecutionState = state;
            if (StationDecisionNavigationProgress.TryGetRemainingDistance(motion, out float remaining) && remaining < previousRemaining-.2f)
            { previousRemaining = remaining; lastProgressAt = now; }
            if (now-lastProgressAt > 25f) FailActivity("Platform control route made no progress for 25 seconds.");
        }

        void UpdateCoachQueue(float now)
        {
            var actual = world.ObserveTrip(serviceId);
            if (actual.status == DecisionTrainStatus.Cancelled || now >= actual.cutoffSeconds)
            { KnownTrip = actual; PlatformKnown = true; Interrupt("Assigned coach boarding closed"); return; }
            var queue = CoachQueue;
            if (!queue.Contains(JourneyId)) queue.TryJoin(this);
            Vector3 next = queue.PositionFor(this);
            if ((destination - next).sqrMagnitude > .04f || ExecutionState == "Walking" || ExecutionState.StartsWith("Gate", StringComparison.Ordinal))
            { StartGateMove(next, "CoachApproach"); return; }
            if (motion.NavigationFailed) { FailActivity("Coach queue navigation: " + motion.NavigationIssue); return; }
            if (Vector3.Distance(transform.position, next) <= .12f)
            {
                motion.StopNavigation(); motion.PlayIdle(); ExecutionState = "CoachQueue";
                if (queue.CanBoard(this)) TryBoard();
                return;
            }
            if (motion.agent && motion.agent.enabled && motion.agent.isOnNavMesh && !motion.agent.pathPending && (!motion.IsNavigatingToRequestedDestination || motion.agent.isStopped))
            { StartGateMove(next, "CoachApproach"); return; }
            ExecutionState = "CoachApproach";
            if (StationDecisionNavigationProgress.TryGetRemainingDistance(motion, out float remaining) && remaining < previousRemaining - .2f)
            { previousRemaining = remaining; lastProgressAt = now; }
            if (now - lastProgressAt > 25f) FailActivity("Coach queue route made no progress for 25 seconds.");
        }

        void UpdateToiletVisit(float now)
        {
            var queue = ToiletQueue;
            if (ExecutionState == "WCClearing")
            {
                Vector3 exit = queue.exitPoint ? queue.exitPoint.position : target.Position;
                if (Vector3.Distance(transform.position, exit) <= .12f)
                {
                    motion.StopNavigation(); motion.PlayIdle(); queue.Leave(this);
                    if (pendingToiletInterruption)
                    {
                        string reason = pendingToiletReason;
                        pendingToiletInterruption = false; pendingToiletReason = null;
                        FinishInterruption(reason);
                    }
                    else CompleteActivity();
                    return;
                }
                UpdateToiletNavigation(exit, "WCClearing", now);
                return;
            }
            if (ExecutionState == "WCUsing")
            {
                if (now >= endsAt)
                {
                    queue.CompleteUse(this);
                    if (!queue.IsReleasing(JourneyId)) return;
                    ExecutionState = "WCClearing";
                    StartGateMove(queue.exitPoint ? queue.exitPoint.position : target.Position, "WCClearing");
                }
                return;
            }
            if (queue.IsUsing(JourneyId) && queue.TryGetUsePosition(this, out var usePosition))
            {
                if (Vector3.Distance(transform.position, usePosition) <= .12f)
                {
                    Vector3 exit = queue.exitPoint ? queue.exitPoint.position : usePosition;
                    float exitTravel = EstimateTravel(usePosition, exit, walkSpeed);
                    float required = sampledDwell + exitTravel +
                        (IsAlighter ? EstimateExitTravel(exit, walkSpeed) :
                            EstimatePlatformAccess(exit, walkSpeed, sampledDwell + exitTravel)) + safetyMarginSeconds;
                    var actual = world.ObserveTrip(serviceId);
                    if (IsAlighter ? AlightingStopoverDeadlineSeconds - now < required :
                        actual.status == DecisionTrainStatus.Cancelled || KnownTrip.cutoffSeconds - now < required)
                    { Interrupt(IsAlighter ? "Insufficient independent alighting budget for the WC visit and station exit." :
                        "Insufficient remaining budget for the WC visit and platform access."); return; }
                    motion.StopNavigation(); motion.PlayIdle();
                    stateStarted = now; endsAt = now + sampledDwell; ExecutionState = "WCUsing";
                    if (!queue.MarkUseStarted(this, sampledDwell))
                    { FailActivity("WC use place was not valid at the physical activity start."); return; }
                    return;
                }
                UpdateToiletNavigation(usePosition, "WCToUse", now);
                return;
            }
            if (!queue.Contains(JourneyId)) queue.TryJoin(this, out _);
            if (queue.TryBeginUse(this, out usePosition))
            { StartGateMove(usePosition, "WCToUse"); return; }
            Vector3 next = queue.PositionFor(this);
            if (Vector3.Distance(transform.position, next) <= .12f)
            {
                motion.StopNavigation(); motion.PlayIdle();
                ExecutionState = queue.Contains(JourneyId) ? "WCQueue" : "WCApproach";
                return;
            }
            UpdateToiletNavigation(next, queue.Contains(JourneyId) ? "WCQueue" : "WCApproach", now);
        }

        void UpdateToiletNavigation(Vector3 next, string state, float now)
        {
            if ((destination - next).sqrMagnitude > .04f ||
                motion.agent && motion.agent.enabled && motion.agent.isOnNavMesh && !motion.agent.pathPending && (!motion.IsNavigatingToRequestedDestination || motion.agent.isStopped))
            { StartGateMove(next, state); return; }
            if (motion.NavigationFailed) { FailActivity("WC navigation: " + motion.NavigationIssue); return; }
            ExecutionState = state;
            if (StationDecisionNavigationProgress.TryGetRemainingDistance(motion, out float remaining) && remaining < previousRemaining - .2f)
            { previousRemaining = remaining; lastProgressAt = now; }
            if (now - lastProgressAt > 25f) FailActivity("WC route made no progress for 25 seconds.");
        }

        bool TryBeginSeating(out string reason)
        {
            reason = "No available reachable seat with valid sit/stand anchors.";
            if (!target || !FindSeat(target, transform.position, out var seat, out float travel, out float onward,
                0f, sampledSeatedDwell)) return false;
            float required = travel + SeatTransitionDuration + sampledSeatedDwell + onward + safetyMarginSeconds;
            if (IsAlighter ? AlightingStopoverDeadlineSeconds - world.ElapsedSeconds < required :
                !PlatformKnown || KnownTrip.status == DecisionTrainStatus.Cancelled || KnownTrip.cutoffSeconds - world.ElapsedSeconds < required)
            { reason = IsAlighter ? "Insufficient independent alighting budget for the seated stay, standing and station exit." :
                "Insufficient remaining budget for the sampled seated stay, standing and platform access."; return false; }
            motion.SetTravelSpeed(walkSpeed);
            if (!StationDecisionSeatVisit.TryStart(seat, passenger, motion, world.ElapsedSeconds, sampledSeatedDwell, out seatVisit))
            { reason = "Seat was claimed or its route failed before reservation."; return false; }
            ExecutionState = seatVisit.State;
            world.LogEvent("seat_reserved", serviceId, target.poiId + "; seat=" + seat.seatId + "; activity=" + target.seatedActivity +
                "; sampled seated dwell=" + sampledSeatedDwell.ToString("0.00"), passengerId);
            return true;
        }

        void UpdateSeatVisit(float now)
        {
            string beforeState = seatVisit.State, seatId = CurrentSeatId;
            seatVisit.Tick(now);
            ExecutionState = seatVisit.State;
            if (ExecutionState != beforeState)
                world.LogEvent("seat_state_changed", serviceId, "seat=" + seatId + "; " + beforeState + " -> " + ExecutionState, passengerId);
            if (!seatVisit.Finished) return;
            bool completed = seatVisit.Completed;
            string reason = seatVisit.Reason;
            world.LogEvent("seat_released", serviceId, "seat=" + seatId + "; completed=" + completed + "; " + reason, passengerId);
            seatVisit = null;
            if (pendingSeatInterruption)
            {
                string interruptionReason = pendingSeatReason;
                pendingSeatInterruption = false;
                FinishInterruption(interruptionReason);
            }
            else if (completed) CompleteActivity();
            else if (counterServiceCompleted) CompleteCounterWithoutSeat(reason);
            else FailActivity(string.IsNullOrEmpty(reason) ? "Seat visit ended before completion." : reason);
        }

        void ContinueAfterCounter()
        {
            if (!target) { FinishInterruption("Service destination became unavailable."); return; }
            if (!target.HasSeatedActivity) { CompleteActivity(); return; }
            if (!TryBeginSeating(out string reason)) CompleteCounterWithoutSeat(reason);
        }

        void CompleteCounterWithoutSeat(string reason)
        {
            world.LogEvent("seated_activity_skipped", serviceId, CurrentActivity + "; " + reason, passengerId);
            motion.StopNavigation(); motion.PlayIdle();
            CompleteActivity();
        }

        void FailWalking(string reason)
        {
            if (ExecutionState == "ClearingCounter" && counterServiceCompleted) CompleteCounterWithoutSeat("Counter clearance route failed: " + reason);
            else FailActivity(reason);
        }

        void CompleteActivity()
        {
            ++CompletedActivities;
            if (IsAlighter && StationDecisionPolicy.IsAlightingStationService(CurrentActivity) && completedAlightingServices.Add(CurrentActivity))
                ++AlightingServiceVisitsCompleted;
            if (CurrentActivity == DecisionActivity.Display || CurrentActivity == DecisionActivity.Info)
            {
                KnownTrip = world.ObserveTrip(serviceId);
                PlatformKnown = true;
                needs.information = .02f;
                lastObservationAt = world.ElapsedSeconds;
                if (CurrentActivity == DecisionActivity.Display) { HasCheckedDisplay = true; ++displayVisits; }
            }
            else if (IsOptional(CurrentActivity))
            {
                completedOptional.Add(CurrentActivity);
                if (CurrentActivity == DecisionActivity.Cafe) needs.thirst = .05f;
                else if (CurrentActivity == DecisionActivity.Toilet) needs.toilet = .05f;
                else if (CurrentActivity == DecisionActivity.Shop) needs.shopping = .05f;
                else if (CurrentActivity == DecisionActivity.Rest) needs.fatigue = .05f;
                needs.information = Mathf.Clamp01(needs.information + .25f);
            }
            world.LogEvent("activity_completed", serviceId, CurrentActivity + "; sampled dwell=" + sampledDwell.ToString("0.00") +
                "; planned seated dwell=" + sampledSeatedDwell.ToString("0.00"), passengerId);
            if (CurrentActivity == DecisionActivity.Exit)
            { ReleaseTarget(); Finish(false, IsAlighter ? "Exited after alighting and optional station services" : "Exited after replanning", true); return; }
            ReleaseTarget();
            ExecutionState = "Ready";
            LastDecisionReason = "Completed " + CurrentActivity;
            nextDecisionAt = world.ElapsedSeconds + .3f;
        }

        public void ReceiveAnnouncement(DecisionTripObservation observation, string reason)
        {
            if (Finished || (ArrivalPlan != null && !HasEntered) || IsAlighter) return;
            // A FIFO completion happened before the announcement in this frame; retain its observed outcome.
            ConsumeServiceResult();
            if (Finished) return;
            KnownTrip = observation;
            PlatformKnown = true;
            needs.information = Mathf.Max(needs.information, .5f);
            interruption = true;
            world.LogEvent("announcement_received", serviceId, reason, passengerId);
            if (CurrentActivity == DecisionActivity.Platform && PlatformGate && PlatformGate.CanProceed(this)) return;
            // A platform commitment waits for its delayed train; other intentions are reevaluated.
            if (CurrentActivity == DecisionActivity.Platform && observation.status != DecisionTrainStatus.Cancelled) return;
            Interrupt("Announcement: " + reason);
        }

        void TryBoard()
        {
            if (IsAlighter)
            { BoardingViolation = true; FailActivity("Alighting journeys cannot board another train; leave through a station exit."); return; }
            if (PlatformGate && !HasPlatformAccess) { BoardingViolation = true; Finish(false, "Boarding requires a validated platform passage."); return; }
            var actual = world.ObserveTrip(serviceId);
            float anchorDistance = Vector3.Distance(transform.position, destination);
            var gate = DecisionPlatformGate.Evaluate(actual.status, target && target.trackNumber == actual.trackNumber,
                actual.train && actual.train.State == StationTrainState.Boarding,
                actual.train && actual.train.DoorOpenFraction > .95f, world.ElapsedSeconds < actual.cutoffSeconds, anchorDistance);
            if (gate == DecisionPlatformGateAction.Replan)
            { KnownTrip = actual; PlatformKnown = true; Interrupt("Original train unavailable at platform"); return; }
            if (gate == DecisionPlatformGateAction.Reapproach)
            {
                // Native avoidance may displace even a stopped agent while it waits for the train.
                // Reuse the existing Walking arrival/failure/progress checks; retain the same claimed anchor.
                motion.SetTravelSpeed(BudgetSeconds < 25f ? fastWalkSpeed : walkSpeed);
                if (!motion.TryMoveTo(destination)) { FailActivity("Platform reapproach failed: " + motion.NavigationIssue); return; }
                motion.PlayWalk(); cohesionHolding = false; ExecutionState = "Walking";
                stateStarted = lastProgressAt = world.ElapsedSeconds; previousRemaining = float.PositiveInfinity;
                world.LogEvent("platform_reapproach", serviceId, "Waiting displacement=" + anchorDistance.ToString("0.000") + "; native return to assigned anchor", passengerId);
                return;
            }
            if (gate == DecisionPlatformGateAction.Wait) return;
            if (gate == DecisionPlatformGateAction.Reject) { BoardingViolation = true; Finish(false, "Rejected invalid virtual boarding gate."); return; }
            if (arrivalController && !world.TryReserveBoarding(this, out string capacityReason))
            { boardingRejected = true; Interrupt("Boarding reservation refused: " + capacityReason); return; }
            BoardingQueueValidated = CoachQueue && CoachQueue.CanBoard(this);
            KnownTrip = actual;
            ++CompletedActivities;
            CurrentActivity = DecisionActivity.Board;
            ActivityHistory.Add("Board");
            ReleaseTarget();
            Finish(true, "Virtual boarding at safe platform anchor before door closure");
        }

        void ServiceCompleted(bool success)
        {
            if (!servicing || suppressServiceCallback) return;
            servicing = false;
            serviceSuccess = success;
            serviceResultPending = true;
        }

        void ConsumeServiceResult()
        {
            if (!serviceResultPending) return;
            serviceResultPending = false;
            if (serviceSuccess)
            {
                counterServiceCompleted = true;
                world.LogEvent("counter_service_completed", serviceId, CurrentActivity.ToString(), passengerId);
                if (target && target.departurePoint && Vector3.Distance(transform.position, target.departurePoint.position) > .25f)
                {
                    destination = target.departurePoint.position;
                    motion.SetTravelSpeed(walkSpeed);
                    if (!motion.TryMoveTo(destination)) { CompleteCounterWithoutSeat("Counter clearance route unavailable: " + motion.NavigationIssue); return; }
                    motion.PlayWalk(); ExecutionState = "ClearingCounter";
                    stateStarted = lastProgressAt = world.ElapsedSeconds; previousRemaining = float.PositiveInfinity;
                    world.LogEvent("counter_clearance_started", serviceId, target.poiId, passengerId);
                }
                else ContinueAfterCounter();
            }
            else FailActivity("Service unavailable or FIFO visit timed out.");
        }

        void Interrupt(string reason)
        {
            if (servicing)
            { suppressServiceCallback = true; passenger.CancelExternalService(); suppressServiceCallback = false; servicing = false; }
            serviceResultPending = false;
            if (target && CurrentActivity == DecisionActivity.Toilet && ToiletQueue &&
                (ToiletQueue.IsUsing(JourneyId) || ExecutionState == "WCClearing"))
            {
                if (!pendingToiletInterruption) world.LogEvent("activity_interrupted", serviceId, CurrentActivity + ": " + reason, passengerId);
                pendingToiletInterruption = true; pendingToiletReason = reason;
                ToiletQueue.CompleteUse(this, false);
                if (ExecutionState != "WCClearing")
                {
                    ExecutionState = "WCClearing";
                    StartGateMove(ToiletQueue.exitPoint ? ToiletQueue.exitPoint.position : target.Position, "WCClearing");
                }
                return;
            }
            if (seatVisit != null && !seatVisit.Finished)
            {
                if (!pendingSeatInterruption) world.LogEvent("activity_interrupted", serviceId, CurrentActivity + ": " + reason, passengerId);
                pendingSeatInterruption = true; pendingSeatReason = reason;
                seatVisit.RequestEnd(reason);
                return;
            }
            world.LogEvent("activity_interrupted", serviceId, CurrentActivity + ": " + reason, passengerId);
            FinishInterruption(reason);
        }

        void FinishInterruption(string reason)
        {
            cohesionHolding = false;
            motion.StopNavigation(); motion.PlayIdle();
            ReleaseTarget();
            ExecutionState = "Ready";
            LastDecisionReason = reason;
            nextDecisionAt = world.ElapsedSeconds + .3f;
        }

        void FailActivity(string reason)
        {
            NavigationIssue = reason;
            if (target) rejectedUntil[target.poiId] = world.ElapsedSeconds + 15f;
            Interrupt(reason);
        }

        void ReleaseTarget()
        {
            if (PlatformGate) PlatformGate.Leave(this);
            if (CoachQueue) CoachQueue.Leave(this);
            if (ToiletQueue && (target && target.activity == DecisionActivity.Toilet ||
                ToiletQueue.Contains(JourneyId) || ToiletQueue.IsUsing(JourneyId))) ToiletQueue.Leave(this);
            if (target) target.Release(this);
            if (controlledQueueNavigation && motion && motion.agent) motion.agent.stoppingDistance = previousQueueStoppingDistance;
            controlledQueueNavigation = false;
            target = null; counterServiceCompleted = false;
        }

        void Finish(bool boarded, string reason, bool exited = false)
        {
            if (seatVisit != null) { seatVisit.ReleaseOnDisable(); seatVisit = null; }
            ReleaseTarget();
            Finished = true; Boarded = boarded; ExecutionState = boarded ? "Boarded" : exited ? "Exited" : "Failed";
            LastDecisionReason = reason;
            if (motion) { motion.StopNavigation(); motion.PlayIdle(); }
            if (world) world.LogEvent(boarded ? "boarded" : "journey_ended", serviceId, reason, passengerId);
            if (arrivalController) arrivalController.CompleteJourney(this, boarded, exited, reason);
            // End the visible trajectory at a validated safe anchor, without crossing tracks or faking cabin traversal.
            if (boarded || exited || arrivalController) gameObject.SetActive(false);
        }

        void OnDisable()
        {
            if (passenger) passenger.ExternalServiceCompleted -= ServiceCompleted;
            if (seatVisit != null) { seatVisit.ReleaseOnDisable(); seatVisit = null; }
            ReleaseTarget();
        }

        static bool IsOptional(DecisionActivity activity) => activity == DecisionActivity.Cafe || activity == DecisionActivity.Toilet || activity == DecisionActivity.Shop || activity == DecisionActivity.Rest;
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static int StableHash(string value)
        { uint hash = 2166136261u; foreach (char letter in value ?? "") hash = unchecked((hash ^ letter) * 16777619u); return unchecked((int)hash); }
    }
}
