using System;
using UnityEngine;

namespace StationFlow.TrainStation.Social
{
    [DisallowMultipleComponent]
    public sealed class StationSocialWorld : MonoBehaviour
    {
        public StationSeat[] seats = Array.Empty<StationSeat>();
        public StationPassenger[] passengers = Array.Empty<StationPassenger>();
        public StationCleaner[] cleaners = Array.Empty<StationCleaner>();
        public StationConversationGroup[] conversationGroups = Array.Empty<StationConversationGroup>();
        public StationInformationDesk informationDesk;
        public StationInformationDesk[] shopDesks = Array.Empty<StationInformationDesk>();
        public StationWalkingGroup[] walkingGroups = Array.Empty<StationWalkingGroup>();
        [Header("Crowd activity")]
        [Min(1), Tooltip("Automatic trips allowed beyond each service's physical queue capacity. This does not reserve a queue position; arrival still determines order.")]
        public int extraApproachingCustomersPerService = 4;

        public bool HasAutomaticRequestCapacity(StationInformationDesk service)
        {
            if (!service || !service.CanAcceptRequests) return false;
            int limit = service.queueSlots.Length + Mathf.Max(1, extraApproachingCustomersPerService);
            int requests = 0;
            foreach (var passenger in passengers)
                if (passenger && passenger.isActiveAndEnabled && passenger.CurrentService == service && ++requests >= limit)
                    return false;
            return true;
        }

        public int ConversationGroupCount
        {
            get { int count = 0; foreach (var group in conversationGroups) if (group) count++; return count; }
        }
        public int StandingPassengerCount
        {
            get
            {
                int count = 0;
                foreach (var passenger in passengers)
                    if (passenger && passenger.activity == StationPassengerActivity.StandingGroup) count++;
                return count;
            }
        }
        public int TotalPassengerCount
        {
            get { int count = 0; foreach (var passenger in passengers) if (passenger) ++count; return count; }
        }
        public int SeatedPassengerCount => CountActivity(StationPassengerActivity.Seated);
        public int WalkingGroupPassengerCount => CountActivity(StationPassengerActivity.WalkingGroup);
        public int SoloPassengerCount => CountActivity(StationPassengerActivity.SoloWalking);
        public int WalkingGroupCount
        {
            get { int count = 0; foreach (var group in walkingGroups) if (group) ++count; return count; }
        }
        public int ShopCount
        {
            get { int count = 0; foreach (var desk in shopDesks) if (desk) ++count; return count; }
        }

        int CountActivity(StationPassengerActivity activity)
        {
            int count = 0;
            foreach (var passenger in passengers) if (passenger && passenger.activity == activity) ++count;
            return count;
        }

        public bool TryReserveSeat(StationPassenger passenger, StationSeat preferred, out StationSeat selected)
        {
            selected = null;
            if (!passenger || !passenger.motion) return false;
            if (CanUse(passenger, preferred) && preferred.TryReserve(passenger)) { selected = preferred; return true; }
            float bestDistance = float.PositiveInfinity;
            foreach (var seat in seats)
            {
                if (!CanUse(passenger, seat)) continue;
                var distance = (passenger.transform.position - seat.ApproachPosition).sqrMagnitude;
                if (distance >= bestDistance) continue;
                selected = seat;
                bestDistance = distance;
            }
            return selected && selected.TryReserve(passenger);
        }

        static bool CanUse(StationPassenger passenger, StationSeat seat) => seat && seat.isActiveAndEnabled &&
            (!seat.ReservedBy || seat.ReservedBy == passenger) && (!seat.Occupant || seat.Occupant == passenger) &&
            passenger.motion.CanReach(seat.ApproachPosition);

        [ContextMenu("Request information for all available passengers")]
        public void RequestInformationForAll()
        {
            if (!Application.isPlaying) return;
            foreach (var passenger in passengers) if (passenger) passenger.RequestInformation();
        }

        [ContextMenu("Trigger random service visits for all available passengers")]
        public void RequestRandomActivitiesForAll()
        {
            if (!Application.isPlaying) return;
            foreach (var passenger in passengers) if (passenger) passenger.TryRequestRandomActivity();
        }
    }
}
