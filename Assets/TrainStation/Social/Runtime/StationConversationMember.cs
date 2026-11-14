using UnityEngine;
using UnityEngine.AI;

namespace StationFlow.TrainStation.Social
{
    /// <summary>A saved standing actor whose active stopped agent remains visible to local avoidance.</summary>
    [DisallowMultipleComponent]
    public sealed class StationConversationMember : MonoBehaviour
    {
        public StationActorMotion motion;
        public StationConversationGroup group;
        public StationPassenger passenger;
        [Range(0, 99)] public int avoidancePriority = 25;
        [Min(.01f), Tooltip("Maximum allowed initial correction to the saved standing position.")]
        public float initialNavSnapDistance = .35f;
        [Min(1f)] public float turnSpeed = 120f;
        [SerializeField] bool isSpeaking;
        [SerializeField] bool navigationReady;
        [SerializeField] string navigationIssue;
        bool initialized;
        int mobileAvoidancePriority;
        bool stationaryPriorityApplied;

        public bool IsSpeaking => isSpeaking;
        public bool NavigationReady => navigationReady;
        public string NavigationIssue => navigationIssue;
        public bool IsAvailableForConversation => isActiveAndEnabled && (!passenger || passenger.CanParticipateInConversation);

        void Awake()
        {
            if (!motion) motion = GetComponent<StationActorMotion>();
            if (!passenger) passenger = GetComponent<StationPassenger>();
        }
        void Start() => InitializeIfNeeded();
        void OnEnable()
        {
            if (!initialized || !Application.isPlaying) return;
            if (IsAvailableForConversation) ResumeAtHome();
        }

        public void InitializeIfNeeded()
        {
            if (initialized || !Application.isPlaying || !isActiveAndEnabled) return;
            initialized = true;
            if (!motion) motion = GetComponent<StationActorMotion>();
            if (!IsAvailableForConversation) return;
            PrepareStationaryNavigation();
            isSpeaking = false;
            if (motion) motion.PlayIdle();
        }

        void PrepareStationaryNavigation()
        {
            navigationReady = false;
            navigationIssue = "";
            if (!motion || !motion.agent)
            { navigationIssue = "Assign the standing actor's motion component and NavMeshAgent."; return; }
            var agent = motion.agent;
            if (!agent.enabled || !agent.isOnNavMesh)
            {
                if (!NavMesh.SamplePosition(transform.position, out var hit, Mathf.Max(.01f, initialNavSnapDistance), agent.areaMask))
                { navigationIssue = "The saved standing position is outside the nearby NavMesh."; motion.StopNavigation(); return; }
                if (!motion.EnableNavigationAt(hit.position))
                { navigationIssue = motion.NavigationIssue; return; }
            }
            if (!stationaryPriorityApplied)
            {
                mobileAvoidancePriority = agent.avoidancePriority;
                stationaryPriorityApplied = true;
            }
            agent.avoidancePriority = Mathf.Clamp(avoidancePriority, 0, 99);
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            motion.StopNavigation();
            navigationReady = agent.enabled && agent.isOnNavMesh;
        }

        void Update()
        {
            if (!motion || !motion.agent) return;
            var agent = motion.agent;
            navigationReady = agent.enabled && agent.isOnNavMesh;
            if (!IsAvailableForConversation) return;
            if (navigationReady && !agent.isStopped) agent.isStopped = true;
            if (group && group.isActiveAndEnabled) motion.Face(group.GetFacingTarget(this), turnSpeed);
        }

        public void SetSpeaking(bool speaking)
        {
            if (!Application.isPlaying) return;
            InitializeIfNeeded();
            bool changed = isSpeaking != speaking;
            isSpeaking = speaking && IsAvailableForConversation;
            if (!motion || !IsAvailableForConversation) return;
            string desired = isSpeaking ? motion.talkId : motion.idleId;
            if (!changed && motion.CurrentMotion == desired) return;
            if (isSpeaking) motion.PlayTalk();
            else motion.PlayIdle();
        }

        public void SuspendForActivity()
        {
            isSpeaking = false;
            if (stationaryPriorityApplied && motion && motion.agent)
                motion.agent.avoidancePriority = mobileAvoidancePriority;
            stationaryPriorityApplied = false;
            if (group) group.NotifyMemberUnavailable(this);
        }

        public void ResumeAtHome()
        {
            if (!Application.isPlaying || !IsAvailableForConversation) return;
            InitializeIfNeeded();
            PrepareStationaryNavigation();
            SetSpeaking(false);
        }

        void OnDisable()
        {
            isSpeaking = false;
            navigationReady = false;
            if (!Application.isPlaying) return;
            if (motion && (!passenger || passenger.CanParticipateInConversation))
            {
                motion.StopNavigation();
                if (motion.isActiveAndEnabled) motion.PlayIdle();
            }
            if (group) group.NotifyMemberUnavailable(this);
        }
    }
}
