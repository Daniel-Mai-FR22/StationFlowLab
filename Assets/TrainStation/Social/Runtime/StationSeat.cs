using UnityEngine;

namespace StationFlow.TrainStation.Social
{
    /// <summary>A seat has one owner from reservation until departure. All changes are atomic on Unity's main thread.</summary>
    [DisallowMultipleComponent]
    public sealed class StationSeat : MonoBehaviour
    {
        public string seatId;
        [Tooltip("Actor root pose when seated; not the position of the seat cushion.")]
        public Transform seatedPose;
        [Tooltip("A reachable standing position in front of the bench.")]
        public Transform approachPoint;
        public Transform luggagePoint;
        [SerializeField] StationPassenger reservedBy;
        [SerializeField] StationPassenger occupant;

        public StationPassenger ReservedBy => reservedBy;
        public StationPassenger Occupant => occupant;
        public bool IsAvailable => isActiveAndEnabled && !reservedBy && !occupant;
        public Vector3 ApproachPosition => approachPoint ? approachPoint.position : transform.position;

        public bool TryReserve(StationPassenger passenger)
        {
            if (!passenger || !isActiveAndEnabled || (reservedBy && reservedBy != passenger) ||
                (occupant && occupant != passenger)) return false;
            reservedBy = passenger;
            return true;
        }

        public bool TryOccupy(StationPassenger passenger)
        {
            if (!TryReserve(passenger)) return false;
            occupant = passenger;
            return true;
        }

        public void Release(StationPassenger passenger)
        {
            if (occupant == passenger) occupant = null;
            if (reservedBy == passenger) reservedBy = null;
        }

        void OnDisable()
        {
            var owner = reservedBy ? reservedBy : occupant;
            occupant = null;
            reservedBy = null;
            if (Application.isPlaying && owner) owner.NotifySeatUnavailable(this);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = occupant ? new Color(1f, .6f, .1f) : reservedBy ? Color.yellow : Color.cyan;
            var pose = seatedPose ? seatedPose : transform;
            Gizmos.DrawWireCube(pose.position + Vector3.up * .45f, new Vector3(.55f, .9f, .55f));
            if (approachPoint) { Gizmos.DrawLine(pose.position, approachPoint.position); Gizmos.DrawWireSphere(approachPoint.position, .2f); }
        }
    }
}
