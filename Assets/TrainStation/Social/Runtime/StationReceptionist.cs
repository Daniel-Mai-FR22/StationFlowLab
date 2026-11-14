using UnityEngine;

namespace StationFlow.TrainStation.Social
{
    [DisallowMultipleComponent]
    public sealed class StationReceptionist : MonoBehaviour
    {
        public StationActorMotion motion;
        public Transform interactionLookTarget;
        [SerializeField] StationPassenger currentCustomer;
        Quaternion restRotation;
        bool initialized;
        public StationPassenger CurrentCustomer => currentCustomer;

        void Start()
        {
            if (!motion) motion = GetComponent<StationActorMotion>();
            restRotation = transform.rotation;
            initialized = true;
            if (motion) { motion.StopNavigation(true); motion.PlayIdle(); }
        }

        void OnEnable()
        {
            if (!initialized || !Application.isPlaying || !motion) return;
            if (currentCustomer && currentCustomer.isActiveAndEnabled && currentCustomer.CurrentService &&
                currentCustomer.CurrentService.receptionist == this && currentCustomer.CurrentService.ActiveCustomer == currentCustomer &&
                currentCustomer.CurrentState == StationPassengerState.Talking)
                motion.PlayTalk();
            else
            {
                currentCustomer = null;
                motion.PlayIdle();
            }
        }

        void Update()
        {
            if (!motion) return;
            if (currentCustomer) motion.Face(currentCustomer.transform.position);
            else if (interactionLookTarget) motion.Face(interactionLookTarget.position);
            else transform.rotation = Quaternion.RotateTowards(transform.rotation, restRotation, 120f * Time.deltaTime);
        }

        public void BeginConversation(StationPassenger passenger)
        {
            currentCustomer = passenger;
            if (motion) motion.PlayTalk();
        }

        public void EndConversation()
        {
            currentCustomer = null;
            if (motion && isActiveAndEnabled && Application.isPlaying) motion.PlayIdle();
        }
    }
}
