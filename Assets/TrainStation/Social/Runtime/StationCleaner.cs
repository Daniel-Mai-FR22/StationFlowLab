using System;
using UnityEngine;
using UnityEngine.Events;

namespace StationFlow.TrainStation.Social
{
    public enum StationCleanerState { Patrol, Working, Yielding, Disabled }

    [DisallowMultipleComponent]
    public sealed class StationCleaner : MonoBehaviour
    {
        public StationActorMotion motion;
        public StationSocialWorld world;
        public Transform[] workPoints = Array.Empty<Transform>();
        public Transform cleaningTool;
        [Min(.5f)] public float workDuration = 7f;
        [Min(.1f)] public float patrolSpeed = 1f;
        [HideInInspector] public float yieldRadius = 1.5f; // Legacy serialized data; native avoidance now handles passing travellers.
        [Min(2f)] public float routeTimeout = 45f;
        public UnityEvent workStarted = new UnityEvent();
        public UnityEvent workFinished = new UnityEvent();
        [SerializeField] StationCleanerState currentState = StationCleanerState.Disabled;
        [SerializeField] int workPointIndex;
        [SerializeField] int completedWorkCycles;
        [SerializeField] string lastIssue;
        float stateEndsAt;
        float routeStartedAt;
        bool initialized;

        public StationCleanerState CurrentState => currentState;
        public int CompletedWorkCycles => completedWorkCycles;
        public int WorkPointIndex => workPointIndex;
        public string LastIssue => lastIssue;

        void Start()
        {
            initialized = true;
            if (!motion) motion = GetComponent<StationActorMotion>();
            BeginPatrol();
        }
        void OnEnable() { if (initialized && Application.isPlaying) BeginPatrol(); }

        void BeginPatrol()
        {
            if (!motion || !motion.agent || workPoints.Length == 0) { currentState = StationCleanerState.Disabled; return; }
            motion.SetTravelSpeed(Mathf.Max(.1f, patrolSpeed));
            motion.agent.avoidancePriority = 80;
            motion.agent.obstacleAvoidanceType = UnityEngine.AI.ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            for (var attempt = 0; attempt < workPoints.Length; ++attempt)
            {
                workPointIndex %= workPoints.Length;
                var target = workPoints[workPointIndex];
                if (target && motion.TryMoveTo(target.position))
                {
                    currentState = StationCleanerState.Patrol;
                    routeStartedAt = Time.time;
                    motion.PlayWalk();
                    return;
                }
                workPointIndex = (workPointIndex + 1) % workPoints.Length;
            }
            lastIssue = "No reachable cleaning waypoint; waiting before retry.";
            currentState = StationCleanerState.Disabled;
            stateEndsAt = Time.time + 5f;
            motion.StopNavigation();
            motion.PlayIdle();
        }

        void Update()
        {
            if (!motion) return;
            switch (currentState)
            {
                case StationCleanerState.Patrol:
                    if (motion.NavigationFailed || Time.time - routeStartedAt > routeTimeout)
                    {
                        lastIssue = "Cleaning route interrupted or timed out; trying next waypoint.";
                        workPointIndex = (workPointIndex + 1) % Mathf.Max(1, workPoints.Length);
                        BeginPatrol();
                    }
                    else if (motion.HasArrived)
                    {
                        motion.StopNavigation();
                        currentState = StationCleanerState.Working;
                        stateEndsAt = Time.time + Mathf.Max(.5f, workDuration);
                        if (workPoints[workPointIndex]) transform.rotation = workPoints[workPointIndex].rotation;
                        motion.PlaySweep();
                        workStarted.Invoke();
                    }
                    break;
                case StationCleanerState.Working:
                    if (Time.time >= stateEndsAt)
                    {
                        ++completedWorkCycles;
                        workFinished.Invoke();
                        workPointIndex = (workPointIndex + 1) % workPoints.Length;
                        BeginPatrol();
                    }
                    break;
                case StationCleanerState.Yielding:
                    // A saved legacy debug state resumes the ordinary task.
                    BeginPatrol();
                    break;
                case StationCleanerState.Disabled:
                    if (workPoints.Length > 0 && Time.time >= stateEndsAt) BeginPatrol();
                    break;
            }
        }

        void OnDisable()
        {
            currentState = StationCleanerState.Disabled;
            if (motion) motion.StopNavigation();
        }
    }
}
