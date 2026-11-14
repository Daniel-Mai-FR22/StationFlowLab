using UnityEngine;

namespace StationFlow.TrainStation
{
    /// <summary>Authored placement and coverage of a fixed camera; no runtime aiming or movement.</summary>
    [DisallowMultipleComponent]
    public sealed class StationSurveillanceCamera : MonoBehaviour
    {
        public string cameraId;
        public string displayName;
        [TextArea(2, 4)] public string purpose;
        public Camera cameraComponent;
        [Tooltip("Physical attachment to the station wall, beam or canopy.")]
        public Transform mountAnchor;
        [Tooltip("Saved inspection target; moving it does not rotate the fixed camera at runtime.")]
        public Transform aimTarget;
        [Tooltip("Saved points intended to be visible for this camera's observation purpose.")]
        public Transform[] coveragePoints;

        void OnDrawGizmosSelected()
        {
            if (!cameraComponent) return;
            Gizmos.color = new Color(.2f, .85f, 1f, .65f);
            if (mountAnchor) Gizmos.DrawLine(mountAnchor.position, cameraComponent.transform.position);
            if (aimTarget)
            {
                Gizmos.DrawLine(cameraComponent.transform.position, aimTarget.position);
                Gizmos.DrawWireSphere(aimTarget.position, .3f);
            }
            if (coveragePoints != null)
                foreach (Transform point in coveragePoints)
                    if (point)
                    {
                        Gizmos.DrawLine(cameraComponent.transform.position, point.position);
                        Gizmos.DrawWireSphere(point.position, .18f);
                    }
        }
    }
}
