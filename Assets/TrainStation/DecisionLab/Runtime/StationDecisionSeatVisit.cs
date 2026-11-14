using StationFlow.TrainStation.Social;
using UnityEngine;
using UnityEngine.AI;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Lab-only seat execution. Travel uses NavMesh; only the authored local sit/stand approach moves a detached root.</summary>
    public sealed class StationDecisionSeatVisit
    {
        public StationSeat Seat { get; private set; }
        public string State { get; private set; } = "WalkingToSeat";
        public bool Finished { get; private set; }
        public bool Completed { get; private set; }
        public string Reason { get; private set; } = "";
        public Vector3 ExitPosition => approach;
        public bool RequiresStanding => State == "SittingDown" || State == "Seated" || State == "StandingUp";
        public float SecondsRemaining(float now) => State == "Seated" ? Mathf.Max(0f, endsAt - now) : 0f;

        readonly StationPassenger passenger;
        readonly StationActorMotion motion;
        readonly Vector3 approach, seatedPosition;
        readonly Quaternion approachRotation, seatedRotation;
        readonly float dwell;
        readonly float previousStoppingDistance;
        Vector3 transitionStartPosition;
        Quaternion transitionStartRotation;
        float stateStarted, endsAt, lastProgressAt, previousRemaining = float.PositiveInfinity;
        bool requestedEnd, dwellCompleted;

        StationDecisionSeatVisit(StationSeat seat, StationPassenger owner, StationActorMotion actor, float now, float duration)
        {
            Seat = seat; passenger = owner; motion = actor; dwell = duration;
            previousStoppingDistance = actor.agent ? actor.agent.stoppingDistance : .1f;
            if (actor.agent) actor.agent.stoppingDistance = Mathf.Min(previousStoppingDistance, .05f);
            approach = seat.ApproachPosition;
            approachRotation = seat.approachPoint.rotation;
            seatedPosition = seat.seatedPose.position; seatedRotation = seat.seatedPose.rotation;
            stateStarted = lastProgressAt = now;
        }

        public static bool IsUsable(StationSeat seat, StationPassenger passenger, StationActorMotion motion)
        {
            if (!seat || !seat.isActiveAndEnabled || !seat.approachPoint || !seat.seatedPose || !passenger || !motion ||
                !motion.animationPlayer || !motion.animationPlayer.Library ||
                (seat.ReservedBy && seat.ReservedBy != passenger) || (seat.Occupant && seat.Occupant != passenger)) return false;
            // Authoring must provide a short clear approach. Never attach from the counter straight to a distant chair.
            if (Vector3.Distance(seat.ApproachPosition, seat.seatedPose.position) > 1.2f) return false;
            int mask = motion.agent ? motion.agent.areaMask : NavMesh.AllAreas;
            if (!NavMesh.SamplePosition(seat.ApproachPosition, out var hit, .15f, mask) ||
                Vector3.Distance(hit.position, seat.ApproachPosition) > .15f) return false;
            var library = motion.animationPlayer.Library;
            return library.Find(motion.sitDownId)?.clip && library.Find(motion.sitIdleId)?.clip && library.Find(motion.standUpId)?.clip;
        }

        public static bool TryStart(StationSeat seat, StationPassenger passenger, StationActorMotion motion, float now, float dwell,
            out StationDecisionSeatVisit visit)
        {
            visit = null;
            if (!IsUsable(seat, passenger, motion) || !motion.CanReach(seat.ApproachPosition) || !seat.TryReserve(passenger)) return false;
            var candidate = new StationDecisionSeatVisit(seat, passenger, motion, now, Mathf.Max(.1f, dwell));
            if (!motion.TryMoveTo(candidate.approach)) { candidate.ReleaseOnDisable(); return false; }
            motion.PlayWalk(); visit = candidate; return true;
        }

        public void RequestEnd(string reason)
        {
            requestedEnd = true; dwellCompleted = false; Reason = reason;
        }

        public void Tick(float now)
        {
            if (Finished) return;
            bool validLease = Seat && Seat.isActiveAndEnabled && Seat.ReservedBy == passenger;
            if (!validLease && !requestedEnd) RequestEnd("Seat was disabled or its reservation became unavailable.");
            if (State == "WalkingToSeat")
            {
                if (requestedEnd) { motion.StopNavigation(); motion.PlayIdle(); End(false); return; }
                if (motion.NavigationFailed) { Reason = motion.NavigationIssue; End(false); return; }
                if (motion.HasArrived && Vector3.Distance(motion.transform.position, approach) <= .12f)
                {
                    if (Vector3.Distance(motion.transform.position, seatedPosition) > 1.2f)
                    { Reason = "The reached seat approach is too far from its authored seated root."; End(false); return; }
                    motion.StopNavigation(true);
                    motion.animationPlayer.SetPlaybackSpeed(1f); motion.PlaySitDown();
                    BeginTransition("SittingDown", now, motion.Duration(motion.sitDownId));
                    return;
                }
                if (StationDecisionNavigationProgress.TryGetRemainingDistance(motion, out float remaining) &&
                    remaining < previousRemaining - .2f)
                { previousRemaining = remaining; lastProgressAt = now; }
                if (now - lastProgressAt > 25f || now - stateStarted > 120f)
                { Reason = "Seat route stalled or exceeded its travel limit."; motion.StopNavigation(); End(false); }
                return;
            }
            if (requestedEnd && State != "StandingUp") BeginStanding(now);
            if (State == "SittingDown")
            {
                UpdateTransition(now, true);
                if (now < endsAt) return;
                if (!Seat || !Seat.TryOccupy(passenger)) { RequestEnd("Seat occupation failed."); BeginStanding(now); return; }
                motion.transform.SetPositionAndRotation(seatedPosition, seatedRotation);
                motion.animationPlayer.SetPlaybackSpeed(1f); motion.PlaySitIdle();
                State = "Seated"; stateStarted = now; endsAt = now + dwell;
            }
            else if (State == "Seated")
            {
                if (now >= endsAt) { dwellCompleted = true; BeginStanding(now); }
            }
            else if (State == "StandingUp")
            {
                UpdateTransition(now, false);
                if (now < endsAt) return;
                // The root already reached this authored NavMesh point continuously during standing.
                bool attached = motion.EnableNavigationAt(approach);
                motion.StopNavigation(); motion.animationPlayer.SetPlaybackSpeed(1f); motion.PlayIdle();
                if (!attached) Reason = "Could not reattach to the seat approach NavMesh after standing.";
                End(attached && dwellCompleted && !requestedEnd);
            }
        }

        void BeginStanding(float now)
        {
            motion.StopNavigation(true);
            motion.animationPlayer.SetPlaybackSpeed(1f); motion.PlayStandUp();
            BeginTransition("StandingUp", now, motion.Duration(motion.standUpId));
        }

        void BeginTransition(string state, float now, float duration)
        {
            State = state; stateStarted = now; endsAt = now + Mathf.Max(.1f, duration);
            transitionStartPosition = motion.transform.position; transitionStartRotation = motion.transform.rotation;
        }

        void UpdateTransition(float now, bool sitting)
        {
            float progress = Mathf.InverseLerp(stateStarted, endsAt, now);
            float travel = sitting ? Mathf.InverseLerp(0f, .65f, progress) : Mathf.InverseLerp(.45f, 1f, progress);
            travel = Mathf.SmoothStep(0f, 1f, travel);
            motion.transform.SetPositionAndRotation(Vector3.Lerp(transitionStartPosition, sitting ? seatedPosition : approach, travel),
                Quaternion.Slerp(transitionStartRotation, sitting ? seatedRotation : approachRotation, travel));
        }

        void End(bool completed)
        {
            if (Seat) Seat.Release(passenger);
            if (motion.agent) motion.agent.stoppingDistance = previousStoppingDistance;
            Seat = null; Finished = true; Completed = completed;
        }

        public void ReleaseOnDisable()
        {
            if (Seat) Seat.Release(passenger);
            if (motion.agent) motion.agent.stoppingDistance = previousStoppingDistance;
            Seat = null; Finished = true; Completed = false;
        }
    }
}
