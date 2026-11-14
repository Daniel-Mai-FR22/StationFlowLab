using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StationFlow.TrainStation.Social
{
    /// <summary>Shared route and loose formation for saved actors. Each traveller owns its NavMesh path and may leave for a service.</summary>
    [DisallowMultipleComponent]
    public sealed class StationWalkingGroup : MonoBehaviour
    {
        public StationPassenger[] members = Array.Empty<StationPassenger>();
        public Transform[] waypoints = Array.Empty<Transform>();
        [Tooltip("Optional formation slots relative to the leader's travelled path. Member indices keep their side when companions leave for a service.")]
        public Vector3[] formationOffsets = Array.Empty<Vector3>();
        public int randomSeed = 7101;
        public Vector2 pauseDuration = new Vector2(2f, 5f);
        [Min(.2f)] public float leaderSpeed = .95f;
        [Min(.2f)] public float followerSpeed = 1.15f;
        [Min(.2f)] public float catchUpSpeed = 1.4f;
        [Min(.7f)] public float spacing = 1.05f;
        [Min(.8f)] public float rejoinDistance = 1.5f;
        [Min(1f), Tooltip("The leader waits at a waypoint for travelling companions within this distance; service visitors are excluded.")]
        public float cohesionDistance = 2.8f;
        [Min(2f)] public float maximumRegroupWait = 6f;
        [Header("Formation through corners and narrow passages")]
        [Min(.7f)] public float singleFileSpacing = 1.05f;
        [Min(.2f)] public float formationChangeSpeed = 1.4f;
        [Min(.3f)] public float widenAfterClearSeconds = 1.5f;
        [SerializeField] StationPassenger currentLeader;
        [SerializeField] int currentWaypointIndex;
        [SerializeField] int completedWaypoints;
        [SerializeField] bool waitingAtWaypoint;
        [SerializeField] bool waitingForCompanions;
        [SerializeField] int regroupRecoveryCount;
        [SerializeField] int regroupTargets;
        [SerializeField] bool singleFile;
        [SerializeField, Range(0f, 1f)] float formationWidth = 1f;
        readonly List<Vector3> leaderTrail = new List<Vector3>(160);
        struct RecoveryTarget { public Vector3 position; public float retryAt; public bool valid; }
        readonly Dictionary<StationPassenger, RecoveryTarget> recoveryTargets = new Dictionary<StationPassenger, RecoveryTarget>();
        StationPassenger trailLeader;
        Vector3 travelForward = Vector3.forward;
        float nextFormationCheck, clearPassageSince = -1f;
        int trailFrame = -1;
        float resumeAt;
        float cohesionWaitStartedAt;
        System.Random random;
        bool initialized;

        public StationPassenger CurrentLeader => SelectLeader();
        public int CurrentWaypointIndex => currentWaypointIndex;
        public int CompletedWaypoints => completedWaypoints;
        public bool WaitingAtWaypoint => waitingAtWaypoint;
        public bool WaitingForCompanions => waitingForCompanions;
        public int RegroupRecoveryCount => regroupRecoveryCount;
        public int RegroupTargets => regroupTargets;
        public bool SingleFile => singleFile;

        void Start() => Initialize();

        void Initialize()
        {
            if (initialized) return;
            initialized = true;
            random = new System.Random(randomSeed);
            currentWaypointIndex = waypoints != null && waypoints.Length > 0 ? random.Next(waypoints.Length) : 0;
            foreach (var member in members)
                if (member && !member.walkingGroup) member.walkingGroup = this;
        }

        StationPassenger SelectLeader()
        {
            if (currentLeader && currentLeader.walkingGroup == this && currentLeader.IsFollowingWalkingGroup) return currentLeader;
            currentLeader = null;
            waitingAtWaypoint = false;
            waitingForCompanions = false;
            foreach (var member in members)
            {
                if (!member || member.walkingGroup != this || !member.IsFollowingWalkingGroup) continue;
                currentLeader = member;
                break;
            }
            return currentLeader;
        }

        void Update()
        {
            Initialize();
            var leader = SelectLeader();
            if (!leader || !TryGetWaypoint(out var target)) return;
            UpdateTrail(leader);
            UpdateFormationWidth(leader);
            // Recovery is still group travel: a companion changing FSM state
            // must not disappear from the group merely because it is rerouting.
            UpdateCohesion(leader);
            if (waitingForCompanions) return;
            var offset = target.position - leader.transform.position;
            offset.y = 0f;
            if (!waitingAtWaypoint)
            {
                if (offset.sqrMagnitude > .65f * .65f) return;
                waitingAtWaypoint = true;
                float minimum = Mathf.Max(0f, Mathf.Min(pauseDuration.x, pauseDuration.y));
                resumeAt = Time.time + minimum + (float)random.NextDouble() * Mathf.Max(0f, Mathf.Max(pauseDuration.x, pauseDuration.y) - minimum);
                return;
            }
            if (Time.time < resumeAt) return;
            ++completedWaypoints;
            currentWaypointIndex = waypoints.Length > 0 ? (currentWaypointIndex + 1) % waypoints.Length : 0;
            waitingAtWaypoint = false;
        }

        void UpdateCohesion(StationPassenger leader)
        {
            float limit = Mathf.Max(1f, cohesionDistance);
            float resumeDistance = CohesionResumeDistance(leader, limit);
            float largestGap = 0f;
            foreach (var member in members)
            {
                if (!member || member == leader || !member.ParticipatesInWalkingCohesion) continue;
                var gap = member.transform.position - leader.transform.position;
                gap.y = 0f;
                largestGap = Mathf.Max(largestGap, gap.magnitude);
            }
            if (!waitingForCompanions)
            {
                if (largestGap <= limit) return;
                waitingForCompanions = true;
                cohesionWaitStartedAt = Time.time;
            }
            // Hysteresis avoids repeatedly stopping the leader near one threshold.
            if (largestGap <= resumeDistance) { waitingForCompanions = false; return; }
            // In dense foot traffic, avoidance can carry a blocked companion away
            // even while the leader waits. Retry the reunion path before that
            // drift exceeds twice the group's normal cohesion distance.
            if (largestGap <= limit * 2f && Time.time - cohesionWaitStartedAt < Mathf.Max(2f, maximumRegroupWait)) return;
            // Start one recovery for a blocked travelling companion. Returning
            // and WaitingForRoute recovery states remain counted above; they
            // are not reset again on every frame. Service visitors are excluded.
            foreach (var member in members)
            {
                if (!member || member == leader || !member.IsFollowingWalkingGroup) continue;
                var gap = member.transform.position - leader.transform.position;
                gap.y = 0f;
                if (gap.sqrMagnitude <= resumeDistance * resumeDistance) continue;
                member.RejoinWalkingGroup();
                ++regroupRecoveryCount;
            }
            // Keep the leader waiting until the physical gap has closed. The
            // timeout requests another route; it never grants leave-behind.
        }

        float CohesionResumeDistance(StationPassenger leader, float limit)
        {
            float extent = 0f;
            foreach (var member in members)
            {
                if (!member || member == leader || !member.ParticipatesInWalkingCohesion) continue;
                int slot = StableSlot(member);
                var offset = FormationOffset(slot);
                float behind = Mathf.Lerp(Mathf.Max(.85f, singleFileSpacing) * slot,
                    Mathf.Max(.85f, -offset.z), formationWidth);
                float side = offset.x * formationWidth;
                extent = Mathf.Max(extent, Mathf.Sqrt(behind * behind + side * side));
            }
            // Include the authored formation extent and an arrival allowance
            // when deciding whether the social group has gathered again.
            return Mathf.Min(limit - .15f, Mathf.Max(limit * .8f, extent + .4f));
        }

        bool TryGetWaypoint(out Transform waypoint)
        {
            waypoint = null;
            if (waypoints == null || waypoints.Length == 0) return false;
            currentWaypointIndex = Mathf.Clamp(currentWaypointIndex, 0, waypoints.Length - 1);
            for (int offset = 0; offset < waypoints.Length; ++offset)
            {
                int index = (currentWaypointIndex + offset) % waypoints.Length;
                if (!waypoints[index]) continue;
                currentWaypointIndex = index;
                waypoint = waypoints[index];
                return true;
            }
            return false;
        }

        public bool TryGetDestination(StationPassenger member, out Vector3 destination, out bool waiting)
        {
            Initialize();
            destination = transform.position;
            waiting = false;
            if (!member || !TryGetWaypoint(out var waypoint)) return false;
            var leader = SelectLeader();
            waiting = waitingAtWaypoint || waitingForCompanions;
            if (!leader)
            {
                // When every companion is returning from a service, nobody is
                // leader yet. Give each returner a different reunion location.
                int memberIndex = Mathf.Max(0, Array.IndexOf(members, member));
                float angle = memberIndex * 2.3999632f;
                var reunion = waypoint.position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 1.25f;
                return StationCrowdSpace.TryFindStandingPosition(reunion, member.motion, .4f, out destination);
            }
            if (leader == member)
            {
                destination = waypoint.position;
                return true;
            }
            UpdateTrail(leader);
            if (member.IsRecoveringWalkingGroup)
                return TryGetRecoveryDestination(member, leader, out destination);
            recoveryTargets.Remove(member);
            int slot = StableSlot(member);
            Vector3 localOffset = FormationOffset(slot);
            float behind = Mathf.Lerp(Mathf.Max(.85f, singleFileSpacing) * slot,
                Mathf.Max(.85f, -localOffset.z), formationWidth);
            SampleTrail(leader, behind, out var center, out var forward);
            var right = Vector3.Cross(Vector3.up, forward);
            var candidate = center + right * (localOffset.x * formationWidth);
            if (TryFormationPosition(member, leader, center, candidate, out destination)) return true;
            return TryGetTrailingDestination(member, out destination);
        }

        bool TryGetRecoveryDestination(StationPassenger member, StationPassenger leader, out Vector3 destination)
        {
            destination = member.transform.position;
            // Include the recovery arrival tolerance inside the group's resume
            // radius, otherwise a valid recovery goal could itself hold up departure.
            float maximumGap = Mathf.Max(1.1f, CohesionResumeDistance(leader, Mathf.Max(1f, cohesionDistance)) - .38f);
            if (recoveryTargets.TryGetValue(member, out var saved))
            {
                var gap = saved.position - leader.transform.position; gap.y = 0f;
                if (saved.valid && gap.sqrMagnitude >= .8f * .8f && gap.sqrMagnitude <= maximumGap * maximumGap &&
                    StationCrowdSpace.IsPositionAvailable(saved.position, member.motion, .72f))
                { destination = saved.position; return true; }
                if (Time.time < saved.retryAt) return false;
            }
            // A blocked historical slot can be occupied indefinitely by a
            // stranger. Search a bounded free reunion region, then keep that
            // destination stable while the group waits and the member walks.
            int slot = StableSlot(member);
            float preferredRadius = Mathf.Min(maximumGap, 1.1f + (slot - 1) * .7f);
            int mask = member.motion && member.motion.agent ? member.motion.agent.areaMask : NavMesh.AllAreas;
            for (int attempt = 0; attempt < 16; ++attempt)
            {
                int spoke = attempt % 8;
                float side = slot % 2 == 1 ? 1f : -1f;
                float angle = 180f + side * (spoke == 0 ? 0f : (spoke + 1) / 2 * 42f * (spoke % 2 == 1 ? 1f : -1f));
                float radius = attempt < 8 ? preferredRadius : Mathf.Min(maximumGap, preferredRadius + .55f);
                var candidate = leader.transform.position + Quaternion.AngleAxis(angle, Vector3.up) * travelForward * radius;
                if (!NavMesh.SamplePosition(candidate, out var hit, .18f, mask) ||
                    Mathf.Abs(hit.position.y - leader.transform.position.y) > .15f ||
                    !StationCrowdSpace.IsPositionAvailable(hit.position, member.motion, .72f) || !member.motion.CanReach(hit.position)) continue;
                var gap = hit.position - leader.transform.position; gap.y = 0f;
                if (gap.sqrMagnitude < .8f * .8f || gap.sqrMagnitude > maximumGap * maximumGap) continue;
                destination = hit.position;
                recoveryTargets[member] = new RecoveryTarget { position = destination, retryAt = Time.time + .8f, valid = true };
                ++regroupTargets;
                return true;
            }
            recoveryTargets[member] = new RecoveryTarget { retryAt = Time.time + .8f };
            return false;
        }

        /// <summary>Recovery aims at a free point behind the leader, never at the leader's occupied centre.</summary>
        public bool TryGetTrailingDestination(StationPassenger member, out Vector3 destination)
        {
            destination = member ? member.transform.position : transform.position;
            var leader = SelectLeader();
            if (!member || !member.motion || !leader || member == leader) return false;
            UpdateTrail(leader);
            int slot = StableSlot(member);
            float baseDistance = Mathf.Max(.85f, singleFileSpacing) * slot;
            for (int attempt = 0; attempt < 3; ++attempt)
            {
                SampleTrail(leader, Mathf.Min(baseDistance + attempt * .8f, Mathf.Max(baseDistance, cohesionDistance - .6f)),
                    out var center, out _);
                if (TryFormationPosition(member, leader, center, center, out destination))
                { ++regroupTargets; return true; }
                if (StationCrowdSpace.TryFindStandingPosition(center, member.motion, .5f, out var nearby) &&
                    TryFormationPosition(member, leader, center, nearby, out destination))
                { ++regroupTargets; return true; }
            }
            return false;
        }

        int StableSlot(StationPassenger member)
        {
            int index = Array.IndexOf(members, member);
            // The original leader takes the promoted companion's vacated slot;
            // every other companion keeps its authored index and side.
            if (index > 0) return index;
            return Mathf.Max(1, Array.IndexOf(members, currentLeader));
        }

        Vector3 FormationOffset(int slot)
        {
            if (formationOffsets != null && slot < formationOffsets.Length && formationOffsets[slot].sqrMagnitude >= .35f)
                return formationOffsets[slot];
            int rank = slot - 1;
            return new Vector3((rank % 2 == 0 ? -.85f : .85f) * spacing, 0f, -(1 + rank / 2) * spacing);
        }

        bool TryFormationPosition(StationPassenger member, StationPassenger leader, Vector3 center,
            Vector3 candidate, out Vector3 destination)
        {
            destination = candidate;
            int mask = member.motion && member.motion.agent ? member.motion.agent.areaMask : NavMesh.AllAreas;
            if (!NavMesh.SamplePosition(center, out var centerHit, .3f, mask) ||
                !NavMesh.SamplePosition(candidate, out var hit, .2f, mask) ||
                Mathf.Abs(hit.position.y - centerHit.position.y) > .15f ||
                NavMesh.Raycast(centerHit.position, hit.position, out _, mask)) return false;
            var leaderGap = hit.position - leader.transform.position; leaderGap.y = 0f;
            if (leaderGap.sqrMagnitude < .78f * .78f ||
                !StationCrowdSpace.IsPositionAvailable(hit.position, member.motion, .72f)) return false;
            destination = hit.position;
            return true;
        }

        void UpdateTrail(StationPassenger leader)
        {
            if (trailLeader != leader)
            {
                trailLeader = leader;
                leaderTrail.Clear();
                recoveryTargets.Clear();
                travelForward = Vector3.ProjectOnPlane(leader.transform.forward, Vector3.up).normalized;
                trailFrame = -1;
            }
            if (trailFrame == Time.frameCount) return;
            trailFrame = Time.frameCount;
            var position = leader.transform.position;
            var agent = leader.motion ? leader.motion.agent : null;
            if (agent && agent.enabled && agent.isOnNavMesh && !agent.isStopped)
            {
                var direction = agent.steeringTarget - position; direction.y = 0f;
                if (direction.sqrMagnitude > .04f) travelForward = direction.normalized;
            }
            if (leaderTrail.Count == 0 || (leaderTrail[leaderTrail.Count - 1] - position).sqrMagnitude >= .04f)
            {
                leaderTrail.Add(position);
                if (leaderTrail.Count > 160) leaderTrail.RemoveAt(0);
            }
            if (travelForward.sqrMagnitude < .5f) travelForward = Vector3.forward;
        }

        void SampleTrail(StationPassenger leader, float distance, out Vector3 position, out Vector3 forward)
        {
            position = leader.transform.position;
            forward = travelForward;
            for (int i = leaderTrail.Count - 1; i >= 0; --i)
            {
                var segment = position - leaderTrail[i]; segment.y = 0f;
                float length = segment.magnitude;
                if (length < .001f) continue;
                forward = segment / length;
                if (distance <= length) { position -= forward * distance; return; }
                distance -= length;
                position = leaderTrail[i];
            }
            // At startup there is no travelled history yet. Extend along the
            // actual navigation heading; tight sampling rejects walls/benches.
            position -= forward * distance;
        }

        void UpdateFormationWidth(StationPassenger leader)
        {
            if (Time.time >= nextFormationCheck)
            {
                nextFormationCheck = Time.time + .35f;
                bool passageClear = true;
                foreach (var member in members)
                {
                    if (!member || member == leader || !member.IsFollowingWalkingGroup) continue;
                    var local = FormationOffset(StableSlot(member));
                    SampleTrail(leader, Mathf.Max(.85f, -local.z), out var center, out var forward);
                    var side = center + Vector3.Cross(Vector3.up, forward) * local.x;
                    int mask = member.motion && member.motion.agent ? member.motion.agent.areaMask : NavMesh.AllAreas;
                    if (!NavMesh.SamplePosition(center, out var centerHit, .3f, mask) ||
                        !NavMesh.SamplePosition(side, out var sideHit, .15f, mask) ||
                        NavMesh.Raycast(centerHit.position, sideHit.position, out _, mask))
                    { passageClear = false; break; }
                }
                if (!passageClear) { singleFile = true; clearPassageSince = -1f; }
                else
                {
                    if (clearPassageSince < 0f) clearPassageSince = Time.time;
                    if (Time.time - clearPassageSince >= Mathf.Max(.3f, widenAfterClearSeconds)) singleFile = false;
                }
            }
            formationWidth = Mathf.MoveTowards(formationWidth, singleFile ? 0f : 1f,
                Mathf.Max(.2f, formationChangeSpeed) * Time.deltaTime);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(.85f, .5f, .1f);
            if (waypoints == null) return;
            for (int index = 0; index < waypoints.Length; ++index)
            {
                if (!waypoints[index]) continue;
                Gizmos.DrawWireSphere(waypoints[index].position, .3f);
                int next = (index + 1) % waypoints.Length;
                if (waypoints[next]) Gizmos.DrawLine(waypoints[index].position, waypoints[next].position);
            }
        }
    }
}
