using UnityEngine;

namespace StationFlow.TrainStation.Social
{
    /// <summary>Attach to the model Animator. The saved tool has its origin at the brush and its shaft along local +Y.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Animator))]
    public sealed class StationCleaningTool : MonoBehaviour
    {
        public StationActorMotion motion;
        public StationCleaner cleaner;
        public Transform toolRoot;
        [Header("Shaft grip positions in metres")]
        [Min(.2f)] public float primaryGripHeight = .95f;
        [Min(.2f)] public float secondaryGripHeight = 1.2f;
        [Min(.1f)] public float brushReach = .65f;
        [Min(0f)] public float sweepWidth = .32f;
        [Min(.05f)] public float sweepCyclesPerSecond = .45f;
        [Min(0f)] public float groundOffset = .005f;
        [Range(0f, 1f)] public float handIKWeight = 1f;
        [SerializeField] float rightGripError;
        [SerializeField] float leftGripError;
        [SerializeField] float rightReachRatio;
        [SerializeField] float leftReachRatio;
        [SerializeField] float currentIKWeight;
        Animator animator;
        Transform rightHand;
        Transform leftHand;
        Transform rightUpperArm;
        Transform leftUpperArm;
        Transform rightLowerArm;
        Transform leftLowerArm;
        Vector3 rightTarget;
        Vector3 leftTarget;
        float workBlend;

        public float RightGripError => rightGripError;
        public float LeftGripError => leftGripError;
        public float RightReachRatio => rightReachRatio;
        public float LeftReachRatio => leftReachRatio;
        public float CurrentIKWeight => currentIKWeight;

        void Awake()
        {
            animator = GetComponent<Animator>();
            if (animator && animator.isHuman)
            {
                rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                rightUpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                leftUpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                rightLowerArm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                leftLowerArm = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            }
        }

        void OnAnimatorIK(int layerIndex)
        {
            if (!animator || !rightHand || !leftHand || !toolRoot || !motion || !cleaner) return;
            var working = cleaner.CurrentState == StationCleanerState.Working;
            workBlend = Mathf.MoveTowards(workBlend, working ? 1f : 0f, Time.deltaTime * 5f);
            var root = motion.transform;
            if (working || workBlend > .001f)
            {
                var sway = Mathf.Sin(Time.time * sweepCyclesPerSecond * Mathf.PI * 2f) * sweepWidth;
                var brush = root.position + root.forward * brushReach + root.right * sway;
                brush.y = root.position.y + groundOffset;
                // The animated hands can be outside a usable shaft line during a sweep.
                // A shared shoulder anchor keeps both grips in front of the body and in arm reach.
                var shoulderCentre = (rightUpperArm.position + leftUpperArm.position) * .5f;
                var guide = shoulderCentre - Vector3.up * .20f + root.forward * .18f;
                var axis = (guide - brush).normalized;
                float rightLength = Vector3.Distance(rightUpperArm.position, rightLowerArm.position) + Vector3.Distance(rightLowerArm.position, rightHand.position);
                float leftLength = Vector3.Distance(leftUpperArm.position, leftLowerArm.position) + Vector3.Distance(leftLowerArm.position, leftHand.position);
                // Fit the rigid shaft to both shoulder reach spheres while keeping the brush on the floor.
                for (var iteration = 0; iteration < 5; ++iteration)
                {
                    var primary = brush + axis * primaryGripHeight;
                    var secondary = brush + axis * secondaryGripHeight;
                    var desiredPrimary = rightUpperArm.position + Vector3.ClampMagnitude(primary - rightUpperArm.position, rightLength * .96f);
                    var desiredSecondary = leftUpperArm.position + Vector3.ClampMagnitude(secondary - leftUpperArm.position, leftLength * .96f);
                    var correction = (desiredPrimary - primary) / primaryGripHeight + (desiredSecondary - secondary) / secondaryGripHeight;
                    axis = (axis + correction * .65f).normalized;
                }
                toolRoot.SetPositionAndRotation(brush, Quaternion.FromToRotation(Vector3.up, axis));
                rightTarget = brush + axis * primaryGripHeight;
                leftTarget = brush + axis * secondaryGripHeight;
                rightReachRatio = Vector3.Distance(rightTarget, rightUpperArm.position) / Mathf.Max(.01f, rightLength);
                leftReachRatio = Vector3.Distance(leftTarget, leftUpperArm.position) / Mathf.Max(.01f, leftLength);
                currentIKWeight = handIKWeight * workBlend;
                animator.SetIKPositionWeight(AvatarIKGoal.RightHand, currentIKWeight);
                animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, currentIKWeight);
                animator.SetIKPosition(AvatarIKGoal.RightHand, rightTarget);
                animator.SetIKPosition(AvatarIKGoal.LeftHand, leftTarget);
            }
            else
            {
                animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 0f);
                animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0f);
                currentIKWeight = 0f;
                var carryAxis = (root.up + root.forward * .12f).normalized;
                toolRoot.SetPositionAndRotation(rightHand.position - carryAxis * primaryGripHeight,
                    Quaternion.FromToRotation(Vector3.up, carryAxis));
            }
            animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 0f);
            animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 0f);
        }

        void LateUpdate()
        {
            if (!rightHand || !leftHand || !cleaner || cleaner.CurrentState != StationCleanerState.Working) return;
            rightGripError = Vector3.Distance(rightHand.position, rightTarget);
            leftGripError = Vector3.Distance(leftHand.position, leftTarget);
        }
    }
}
