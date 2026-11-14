using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>A small party coordinates intentions and waits briefly; FIFO and chair ownership remain per actor.</summary>
    public sealed class StationDecisionTravelGroup
    {
        readonly StationDecisionArrivalController controller;
        readonly DecisionJourneyRecord[] records;
        readonly HashSet<string> committed = new HashSet<string>();
        readonly Dictionary<string, float> waits = new Dictionary<string, float>();
        readonly string id;
        string intentPoi;
        public bool IsSeparated { get; private set; }
        public string IntentPoi => intentPoi ?? "";

        public StationDecisionTravelGroup(StationDecisionArrivalController controller, string id, DecisionJourneyRecord[] records)
        { this.controller = controller; this.id = id; this.records = records; }
        IEnumerable<StationDecisionPassenger> Members => records.Where(r => r.status == DecisionJourneyStatus.Active && r.livePassenger &&
            r.livePassenger.JourneyId == r.plan.journeyId).Select(r => r.livePassenger);

        public bool CoordinateDecision(StationDecisionPassenger passenger, List<DecisionEvaluation> evaluations)
        {
            if (IsSeparated) return true;
            if (!HasExpectedCompanions) { Separate("No remaining companions; resume individual travel."); return true; }
            if (Tight(passenger)) { Separate("Departure reserve requires individual travel."); return true; }
            if (records.Any(r => r.status == DecisionJourneyStatus.Scheduled || r.status == DecisionJourneyStatus.WaitingForEntry))
                return WaitOrSeparate(passenger, "Waiting for companions to enter.");
            if (!string.IsNullOrEmpty(intentPoi) && committed.Contains(passenger.JourneyId))
            {
                if (Members.All(p => p.ExecutionState == "Ready" || p.ExecutionState == "Regrouping"))
                { intentPoi = null; committed.Clear(); waits.Clear(); }
                else return WaitOrSeparate(passenger, "Regrouping after individual services.");
            }
            if (string.IsNullOrEmpty(intentPoi))
            {
                // The first ready party member selects a feasible shared intention through its ordinary policy.
                return true;
            }
            var shared = evaluations.FirstOrDefault(e => e.option != null && e.option.poiId == intentPoi && e.probability > 0d);
            if (shared == null) { Separate("Shared activity is not feasible for every companion."); return true; }
            foreach (var value in evaluations)
            {
                value.probability = value == shared ? 1d : 0d;
                if (value != shared) { value.score = 0d; value.reason += " Party continues its shared feasible intention."; }
            }
            shared.reason += " Shared party intention."; waits.Remove(passenger.JourneyId); return true;
        }

        public void Committed(StationDecisionPassenger passenger, string poi)
        {
            if (IsSeparated) return;
            if (string.IsNullOrEmpty(intentPoi)) intentPoi = poi;
            if (poi != intentPoi) { Separate("Party selected different feasible destinations."); return; }
            committed.Add(passenger.JourneyId); waits.Remove(passenger.JourneyId);
        }

        public bool HoldForCohesion(StationDecisionPassenger passenger)
        {
            if (!IsSeparated && !HasExpectedCompanions) Separate("No remaining companions; resume individual travel.");
            if (IsSeparated || Tight(passenger)) { if (!IsSeparated && Tight(passenger)) Separate("Tight boarding reserve ends regrouping."); return false; }
            var members = Members.OrderBy(p => p.ArrivalPlan.groupMemberIndex).ToArray();
            if (members.Length < 2) return false;
            var leader = members[0];
            float distance = controller.scenario.groupCohesionDistance;
            if (!StationDecisionNavigationProgress.TryGetRemainingDistance(leader.motion, out float leaderRemaining)) return false;
            bool far = false;
            if (passenger == leader)
            {
                foreach (var member in members.Skip(1))
                    if (member.CurrentTargetId == leader.CurrentTargetId && (member.transform.position - leader.transform.position).sqrMagnitude > distance * distance &&
                        StationDecisionNavigationProgress.TryGetRemainingDistance(member.motion, out float memberRemaining) && memberRemaining > leaderRemaining + distance * .5f)
                    { far = true; break; }
            }
            else
                far = passenger.CurrentTargetId == leader.CurrentTargetId && (passenger.transform.position - leader.transform.position).sqrMagnitude > distance * distance &&
                    StationDecisionNavigationProgress.TryGetRemainingDistance(passenger.motion, out float followerRemaining) && followerRemaining + distance * .5f < leaderRemaining;
            if (!far) { waits.Remove(passenger.JourneyId); return false; }
            return !WaitOrSeparate(passenger, "Walking companions are outside the cohesion distance.");
        }

        bool WaitOrSeparate(StationDecisionPassenger passenger, string reason)
        {
            float now = controller.world.ElapsedSeconds;
            if (!waits.TryGetValue(passenger.JourneyId, out float began)) { waits[passenger.JourneyId] = now; return false; }
            if (now - began < controller.scenario.maximumRegroupSeconds) return false;
            Separate(reason + " Bounded regroup time expired."); return true;
        }
        bool Tight(StationDecisionPassenger passenger) => !passenger.IsAlighter && passenger.BudgetSeconds < 35f;
        bool HasExpectedCompanions => records.Count(r => r.status == DecisionJourneyStatus.Active ||
            r.status == DecisionJourneyStatus.Scheduled || r.status == DecisionJourneyStatus.WaitingForEntry) > 1;
        void Separate(string reason)
        {
            if (IsSeparated) return; IsSeparated = true; waits.Clear();
            foreach (var member in Members) member.RefreshMobilityPace();
            var activeMember = Members.FirstOrDefault();
            controller.world.LogEvent("group_separated", records[0].plan.serviceId, id + "; " + reason,
                activeMember ? activeMember.JourneyId : null);
        }
        public void MemberFinished(string journeyId)
        {
            committed.Remove(journeyId); waits.Remove(journeyId);
            if (!HasExpectedCompanions) Separate("No remaining companions; resume individual travel.");
        }
    }
}
