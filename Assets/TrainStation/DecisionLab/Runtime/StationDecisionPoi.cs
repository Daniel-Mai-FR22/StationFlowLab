using System.Collections.Generic;
using StationFlow.TrainStation.Social;
using UnityEngine;
using UnityEngine.AI;

namespace StationFlow.TrainStation.DecisionLab
{
    public enum DecisionSeatedActivity { None, Coffee, Reading, Rest }

    /// <summary>A semantic activity location over the station's existing, safe floor navigation.</summary>
    public sealed class StationDecisionPoi : MonoBehaviour
    {
        public string poiId;
        public DecisionActivity activity;
        public StationInformationDesk service;
        public StationDecisionToiletQueue toiletQueue;
        public Transform interactionPoint;
        [Tooltip("A clear departure point distinct from the service queue; reached using the NavMesh after service.")]
        public Transform departurePoint;
        [Tooltip("Optional exclusive standing positions, for example a clear departure-board viewing area.")]
        public Transform[] standingPoints = System.Array.Empty<Transform>();
        [Tooltip("Optional target to face while stationary during the activity.")]
        public Transform facingTarget;
        public StationSeat[] seats = System.Array.Empty<StationSeat>();
        public DecisionSeatedActivity seatedActivity;
        [Min(.1f)] public float seatedMeanDurationSeconds = 24f;
        [Min(0f)] public float seatedDurationSpreadSeconds = 6f;
        public int trackNumber;
        [Min(1)] public int capacity = 8;
        [Min(.1f)] public float meanDurationSeconds = 10f;
        [Min(0f)] public float durationSpreadSeconds = 3f;
        [Min(0f)] public float spreadRadius = 1.8f;
        readonly HashSet<StationDecisionPassenger> occupants = new HashSet<StationDecisionPassenger>();
        readonly Dictionary<StationDecisionPassenger, int> standingOwners = new Dictionary<StationDecisionPassenger, int>();

        public Vector3 Position => interactionPoint ? interactionPoint.position : transform.position;
        public int Occupancy => toiletQueue ? toiletQueue.ActiveUseCount : occupants.Count;
        public int ServiceDemandCount
        {
            get { int count = 0; foreach (var traveller in occupants) if (traveller && traveller.ExecutionState == "Service") ++count; return count; }
        }
        public bool HasSeatedActivity => seatedActivity != DecisionSeatedActivity.None && seats != null && seats.Length > 0;
        public bool Available => isActiveAndEnabled && (toiletQueue ? toiletQueue.CanAcceptRequests :
            occupants.Count < capacity && (!service || service.CanAcceptRequests));
        public bool TemporarilyFull => isActiveAndEnabled && !toiletQueue && capacity > 0 && occupants.Count >= capacity &&
            (!service || service.CanAcceptRequests);

        public bool TryClaim(StationDecisionPassenger traveller)
        {
            // WC capacity is leased only when the FIFO head reaches an actual use point.
            if (toiletQueue) return Available;
            if (occupants.Contains(traveller)) return true;
            if (!Available) return false;
            if (standingPoints != null && standingPoints.Length > 0)
            {
                int index = FreeStandingIndex(traveller);
                if (index < 0) return false;
                standingOwners[traveller] = index;
            }
            return occupants.Add(traveller);
        }
        public void Release(StationDecisionPassenger traveller)
        { occupants.Remove(traveller); standingOwners.Remove(traveller); }

        int FreeStandingIndex(StationDecisionPassenger traveller)
        {
            uint hash = 2166136261u;
            foreach (char value in traveller.passengerId ?? "") hash = unchecked((hash ^ value) * 16777619u);
            for (int offset = 0; offset < standingPoints.Length; ++offset)
            {
                int index = (int)((hash + (uint)offset) % (uint)standingPoints.Length);
                if (!standingPoints[index] || !standingPoints[index].gameObject.activeInHierarchy) continue;
                bool owned = false;
                foreach (var owner in standingOwners) if (owner.Value == index) { owned = true; break; }
                if (!owned) return index;
            }
            return -1;
        }

        public Vector3 DestinationFor(StationDecisionPassenger traveller)
        {
            if (toiletQueue) return toiletQueue.PositionFor(traveller);
            if (service && service.TryGetApproachPosition(out var approach, traveller.passenger)) return approach;
            if (standingPoints != null && standingPoints.Length > 0)
            {
                int index = standingOwners.TryGetValue(traveller, out var reserved) ? reserved : FreeStandingIndex(traveller);
                return index >= 0 ? standingPoints[index].position : Position;
            }
            if (spreadRadius <= 0f) return Position;
            // Stable spatial slots keep waiting travellers from choosing the same standing point.
            uint hash = 2166136261u;
            foreach (char value in traveller.passengerId ?? "") hash = unchecked((hash ^ value) * 16777619u);
            float angle = (hash % 360u) * Mathf.Deg2Rad;
            float radius = spreadRadius * (.35f + ((hash >> 9) % 100u) / 155f);
            var point = Position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            int mask = traveller.motion && traveller.motion.agent ? traveller.motion.agent.areaMask : NavMesh.AllAreas;
            return NavMesh.SamplePosition(point, out var hit, .75f, mask) && Mathf.Abs(hit.position.y - Position.y) < .2f ? hit.position : Position;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = activity == DecisionActivity.Platform ? Color.cyan : Color.yellow;
            Gizmos.DrawWireSphere(Position + Vector3.up * .1f, Mathf.Max(.25f, spreadRadius));
        }
    }
}
