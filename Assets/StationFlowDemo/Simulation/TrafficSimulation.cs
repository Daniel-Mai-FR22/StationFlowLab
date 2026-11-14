using System;
using System.Collections.Generic;

namespace StationFlow.SyntheticTraffic
{
    public enum AgentKind { Pedestrian, Vehicle }

    public enum SignalPhase
    {
        NorthSouthGreen,
        NorthSouthAmber,
        AllRedBeforeEastWest,
        EastWestGreen,
        EastWestAmber,
        AllRedBeforePedestrians,
        PedestrianWalk,
        PedestrianClearance,
        AllRedBeforeNorthSouth
    }

    public sealed class SimAgent
    {
        // Track identity ends when an object is recycled outside the observation area.
        public int Id;
        // Stable renderer/pool identity. This must never be exported as a tracking ID.
        public int EntityId;
        public AgentKind Kind;
        public float X, Z, HeadingDegrees, Speed;
        public string State = "Waiting";
        public int Variant;
        public bool Active = true;
        public float Length, Width;
        public int Route;
        public float DesiredSpeed;
        // Observable intent feeds lights, head animation and richer behaviour labels.
        public int TurnIntent; // 0 = straight, 1 = right; Route remains the incoming arm.
        public bool BrakeLights, IndicatorRight, Looking, IsSidewalkOnly;
        public float LookYaw, ReactionDelay;
        public int GroupId;
        public bool Conversation, IsGroupLeader, Regrouping;
        public bool IsConversing => Conversation;
        public float ConversationLookYaw, GestureBlend, VelocityX, VelocityZ;
        public int GroupSize = 1, GroupSlot, ArrivalPortal, DeparturePortal;
        public string NavigationIntent = "Vehicle";
        public float NavigationTargetX, NavigationTargetZ;
        public float LifetimeStart;
        internal float Progress, ReleaseAt;
        internal bool Moving, Counted;
    }

    /// <summary>
    /// Deterministic social pedestrian navigation and right-hand vehicle traffic. One metre
    /// is one Unity unit; the model is independent of rendering and engine time. Pedestrians
    /// plan finite trips on a sidewalk graph and steer around moving and static obstacles.
    /// Collision metrics use XZ oriented vehicle rectangles and pedestrian discs; they
    /// detect new overlapping pairs at each <= 1/60 s integration step, not mesh contacts.
    /// </summary>
    public sealed partial class TrafficSimulation
    {
        public double Time { get; private set; }
        public int Tick { get; private set; }
        public List<SimAgent> Agents { get; } = new List<SimAgent>();
        public SignalPhase Phase { get; private set; } = SignalPhase.NorthSouthGreen;
        public float PhaseRemaining { get; private set; } = 22f;
        public int RedLightViolations { get; private set; }
        public int VehicleRedLightViolations => RedLightViolations;
        public int PedestrianRedLightViolations { get; private set; }
        public int TotalRedLightViolations => RedLightViolations + PedestrianRedLightViolations;
        public int Collisions { get; private set; }
        public int CompletedCrossings { get; private set; }
        public int CompletedVehicleTrips { get; private set; }
        public int CompletedPedestrianCrossings { get; private set; }
        public int CompletedRightTurns { get; private set; }
        public int SafetyInterventions { get; private set; }
        public int Seed { get; }
        public const float StopLine = -15f;
        public const float IntersectionExit = 15f;
        public const float PedestrianRadius = 0.27f;
        public const float TurnRadius = 6f;
        public const float TurnStart = -9.2f;
        public const float TurnEnd = TurnStart + TurnRadius * (float)Math.PI * .5f;

        private uint random;
        private readonly HashSet<long> previousContacts = new HashSet<long>();
        private readonly HashSet<long> currentContacts = new HashSet<long>();
        private readonly List<SimAgent> orderedVehicles = new List<SimAgent>();
        private readonly List<SimAgent> pedestrians = new List<SimAgent>();

        private int nextTrackId;

        public TrafficSimulation(int seed = 42, int pedestrianCount = 24, int vehicleCount = 12)
        {
            if (pedestrianCount < 0 || pedestrianCount > 160) throw new ArgumentOutOfRangeException(nameof(pedestrianCount));
            if (vehicleCount < 0 || vehicleCount > 160) throw new ArgumentOutOfRangeException(nameof(vehicleCount));
            Seed = seed;
            nextTrackId = vehicleCount + pedestrianCount + 1;
            random = unchecked((uint)seed) ^ 0x9E3779B9u;
            if (random == 0) random = 1;
            for (int i = 0; i < vehicleCount; ++i)
            {
                int route = i % 4;
                var agent = new SimAgent
                {
                    Id = i + 1, EntityId = i + 1, Kind = AgentKind.Vehicle, Route = route, Variant = NextInt(6),
                    Length = 4.4f + NextFloat() * 0.35f, Width = 1.82f,
                    DesiredSpeed = 8.2f + NextFloat() * 2.3f,
                    Progress = -25f - (i / 4) * 18f,
                    State = "Approaching", HeadingDegrees = VehicleHeading(route),
                    TurnIntent = (i / 4 + route) % 3 == 0 ? 1 : 0,
                    ReactionDelay = .3f + NextFloat() * .6f, ArrivalPortal = route, DeparturePortal = route, LifetimeStart = 0
                };
                agent.ReleaseAt = agent.ReactionDelay;
                PlaceVehicle(agent);
                Agents.Add(agent);
                orderedVehicles.Add(agent);
            }
            InitializeCrowd(pedestrianCount, vehicleCount);
        }

        /// <summary>Advances a duration in seconds. Negative, nonfinite and >10-second deltas are rejected.</summary>
        public void Step(float dt)
        {
            if (float.IsNaN(dt) || float.IsInfinity(dt) || dt < 0f || dt > 10f)
                throw new ArgumentOutOfRangeException(nameof(dt));
            if (dt == 0f) return;
            int count = (int)Math.Ceiling(dt / (1f / 60f));
            float substep = dt / count;
            for (int i = 0; i < count; ++i)
            {
                UpdateInactiveArrivals();
                AdvanceSignals(substep);
                MoveVehicles(substep);
                MovePedestrians(substep);
                DetectContacts();
                Time += substep;
                Tick++;
            }
        }

        public bool IsVehicleGreen(int route)
        {
            return route < 2 ? Phase == SignalPhase.NorthSouthGreen : Phase == SignalPhase.EastWestGreen;
        }

        public bool IsVehicleAmber(int route)
        {
            return route < 2 ? Phase == SignalPhase.NorthSouthAmber : Phase == SignalPhase.EastWestAmber;
        }

        public bool IsPedestrianWalk => Phase == SignalPhase.PedestrianWalk;

        public bool ConflictAreaOccupied
        {
            get
            {
                foreach (var agent in Agents)
                {
                    if (!agent.Active) continue;
                    if (agent.Kind == AgentKind.Vehicle)
                    {
                        if (agent.Progress + agent.Length * .5f > StopLine + .0001f &&
                            agent.Progress - agent.Length * .5f < ExitProgress(agent)) return true;
                    }
                    else if (agent.Moving) return true;
                }
                return false;
            }
        }

        private void AdvanceSignals(float dt)
        {
            PhaseRemaining = Math.Max(0f, PhaseRemaining - dt);
            if (PhaseRemaining > 0f) return;
            switch (Phase)
            {
                case SignalPhase.NorthSouthGreen: SetPhase(SignalPhase.NorthSouthAmber, 3f); break;
                case SignalPhase.NorthSouthAmber: SetPhase(SignalPhase.AllRedBeforeEastWest, 2f); break;
                case SignalPhase.AllRedBeforeEastWest:
                    if (!ConflictAreaOccupied) SetPhase(SignalPhase.EastWestGreen, 22f);
                    break;
                case SignalPhase.EastWestGreen: SetPhase(SignalPhase.EastWestAmber, 3f); break;
                case SignalPhase.EastWestAmber: SetPhase(SignalPhase.AllRedBeforePedestrians, 2f); break;
                case SignalPhase.AllRedBeforePedestrians:
                    if (!ConflictAreaOccupied)
                    {
                        SetPhase(SignalPhase.PedestrianWalk, 12f);
                        OpenPedestrianSignal();
                    }
                    break;
                case SignalPhase.PedestrianWalk:
                    bool pendingEntry = false;
                    foreach (var w in walkers)
                        if (w.Agent.Active && w.Committed && !w.EnteredRoad) { pendingEntry = true; break; }
                    if (!pendingEntry) SetPhase(SignalPhase.PedestrianClearance, 2f);
                    break;
                case SignalPhase.PedestrianClearance:
                    if (!ConflictAreaOccupied) SetPhase(SignalPhase.AllRedBeforeNorthSouth, 2f);
                    break;
                case SignalPhase.AllRedBeforeNorthSouth:
                    if (!ConflictAreaOccupied) SetPhase(SignalPhase.NorthSouthGreen, 22f);
                    break;
            }
        }

        private void SetPhase(SignalPhase phase, float duration)
        {
            Phase = phase;
            PhaseRemaining = duration;
            if (phase == SignalPhase.NorthSouthGreen || phase == SignalPhase.EastWestGreen)
                foreach (var vehicle in orderedVehicles)
                    if (vehicle.Speed < .2f) vehicle.ReleaseAt = (float)Time + vehicle.ReactionDelay;
        }

        private void MoveVehicles(float dt)
        {
            // Front-to-back updates ensure a following car sees its leader's latest position.
            orderedVehicles.Sort((a, b) => a.Route != b.Route ? a.Route.CompareTo(b.Route) : b.Progress.CompareTo(a.Progress));
            foreach (var agent in orderedVehicles)
            {
                if (!agent.Active) continue;
                float half = agent.Length * .5f;
                float maximum = float.PositiveInfinity;
                bool green = IsVehicleGreen(agent.Route);
                bool committed = agent.Progress + half > StopLine + .0001f;
                if (!green && !committed) maximum = StopLine - half;
                bool following = false;
                foreach (var other in orderedVehicles)
                {
                    if (other == agent || !other.Active) continue;
                    float separation = float.PositiveInfinity;
                    // One arrival lane is shared until paths have diverged. Matching paths
                    // retain arc-length following through the complete turn, avoiding shortcuts.
                    if (other.Route == agent.Route &&
                        (other.TurnIntent == agent.TurnIntent || other.Progress < TurnEnd + 7f))
                        separation = other.Progress - agent.Progress;
                    int travelLane = CurrentLane(agent);
                    if (travelLane >= 0 && travelLane == CurrentLane(other))
                        separation = Math.Min(separation, TravelCoordinate(travelLane, other.X, other.Z) -
                            TravelCoordinate(travelLane, agent.X, agent.Z));
                    if (separation <= 0 || float.IsPositiveInfinity(separation)) continue;
                    float limit = agent.Progress + separation - other.Length * .5f - half - 2.4f;
                    if (limit < maximum) { maximum = limit; following = true; }
                }
                // A 20 cm numerical stopping margin permits smooth deceleration before
                // the hard geometric safety boundary, even at finite simulation steps.
                float available = Math.Max(0f, maximum - agent.Progress - .2f);
                float desired = Math.Min(agent.DesiredSpeed, (float)Math.Sqrt(2f * 3.6f * available));
                if (agent.TurnIntent == 1 && agent.Progress < TurnEnd)
                {
                    const float cornerSpeed = 4.2f;
                    float distanceToTurn = Math.Max(0, TurnStart - agent.Progress - .3f);
                    desired = Math.Min(desired, (float)Math.Sqrt(cornerSpeed * cornerSpeed + 2f * 3.6f * distanceToTurn));
                }
                if (!committed && green && Time < agent.ReleaseAt) desired = 0;
                // Begin stopping when green countdown reaches the braking horizon. This avoids
                // a physically implausible snap when an approaching vehicle sees amber.
                if (green && !committed)
                {
                    float toStop = StopLine - half - agent.Progress;
                    float timeToLine = toStop / Math.Max(agent.Speed, 1.5f);
                    if (PhaseRemaining < timeToLine + .55f)
                        desired = Math.Min(desired, (float)Math.Sqrt(2f * 3.6f * Math.Max(0f, toStop - .2f)));
                }
                float previousSpeed = agent.Speed;
                agent.Speed = MoveTowards(agent.Speed, desired, (desired < agent.Speed ? 3.6f : 1.9f) * dt);
                float oldFront = agent.Progress + half;
                float next = agent.Progress + agent.Speed * dt;
                if (next > maximum)
                {
                    if (agent.Speed > .25f) SafetyInterventions++;
                    next = maximum;
                    agent.Speed = Math.Max(0f, (next - agent.Progress) / dt);
                }
                agent.Progress = next;
                float front = next + half;
                if (oldFront <= StopLine + .0001f && front > StopLine + .0001f && !green) RedLightViolations++;
                if (agent.Progress - half >= ExitProgress(agent) && !agent.Counted)
                {
                    agent.Counted = true;
                    CompletedVehicleTrips++;
                    CompletedCrossings++;
                    if (agent.TurnIntent == 1) CompletedRightTurns++;
                }
                agent.BrakeLights = agent.Speed < .15f || agent.Speed < previousSpeed - .002f;
                agent.IndicatorRight = agent.TurnIntent == 1 && agent.Progress > -39f && agent.Progress < TurnEnd + 2.5f;
                agent.State = agent.Speed < .08f ? "Waiting" :
                    agent.TurnIntent == 1 && agent.Progress > TurnStart && agent.Progress < TurnEnd ? "Turning" :
                    following && available < 12f ? "Following" : (committed ? "Crossing" : "Approaching");
                PlaceVehicle(agent);
            }
            UpdateVehicleLifetimes();
        }

        private void DetectContacts()
        {
            currentContacts.Clear();
            for (int i = 0; i < Agents.Count; ++i)
            {
                var a = Agents[i];
                if (!a.Active) continue;
                for (int j = i + 1; j < Agents.Count; ++j)
                {
                    var b = Agents[j];
                    if (!b.Active || Math.Abs(a.X - b.X) > 6 || Math.Abs(a.Z - b.Z) > 6) continue;
                    if (!BodiesOverlap(a, b)) continue;
                    long key = ((long)a.Id << 32) | (uint)b.Id;
                    currentContacts.Add(key);
                    if (!previousContacts.Contains(key)) Collisions++;
                }
            }
            previousContacts.Clear();
            foreach (long key in currentContacts) previousContacts.Add(key);
        }

        public static bool BodiesOverlap(SimAgent a, SimAgent b)
        {
            if (a.Kind == AgentKind.Pedestrian && b.Kind == AgentKind.Pedestrian)
            {
                float dx = a.X - b.X, dz = a.Z - b.Z;
                return dx * dx + dz * dz < (2f * PedestrianRadius - .01f) * (2f * PedestrianRadius - .01f);
            }
            if (a.Kind == AgentKind.Vehicle && b.Kind == AgentKind.Vehicle)
            {
                double ah = a.HeadingDegrees * Math.PI / 180.0;
                double bh = b.HeadingDegrees * Math.PI / 180.0;
                float afx = (float)Math.Sin(ah), afz = (float)Math.Cos(ah);
                float bfx = (float)Math.Sin(bh), bfz = (float)Math.Cos(bh);
                return !Separates(a, b, afx, afz, afx, afz, bfx, bfz) &&
                    !Separates(a, b, afz, -afx, afx, afz, bfx, bfz) &&
                    !Separates(a, b, bfx, bfz, afx, afz, bfx, bfz) &&
                    !Separates(a, b, bfz, -bfx, afx, afz, bfx, bfz);
            }
            var vehicle = a.Kind == AgentKind.Vehicle ? a : b;
            var pedestrian = a.Kind == AgentKind.Pedestrian ? a : b;
            double heading = vehicle.HeadingDegrees * Math.PI / 180.0;
            float fx = (float)Math.Sin(heading), fz = (float)Math.Cos(heading);
            float deltaX = pedestrian.X - vehicle.X, deltaZ = pedestrian.Z - vehicle.Z;
            float nearestX = Math.Max(0f, Math.Abs(deltaX * fz - deltaZ * fx) - vehicle.Width * .5f);
            float nearestZ = Math.Max(0f, Math.Abs(deltaX * fx + deltaZ * fz) - vehicle.Length * .5f);
            float radius = PedestrianRadius - .01f;
            return nearestX * nearestX + nearestZ * nearestZ < radius * radius;
        }

        private static bool Separates(SimAgent a, SimAgent b, float axisX, float axisZ,
            float afx, float afz, float bfx, float bfz)
        {
            float distance = Math.Abs((b.X - a.X) * axisX + (b.Z - a.Z) * axisZ);
            float aRadius = a.Length * .5f * Math.Abs(afx * axisX + afz * axisZ) +
                a.Width * .5f * Math.Abs(afz * axisX - afx * axisZ);
            float bRadius = b.Length * .5f * Math.Abs(bfx * axisX + bfz * axisZ) +
                b.Width * .5f * Math.Abs(bfz * axisX - bfx * axisZ);
            return distance >= aRadius + bRadius - .01f;
        }

        private static void PlaceVehicle(SimAgent agent)
        {
            float x = 3.2f, z = agent.Progress;
            float relativeHeading = 0;
            if (agent.TurnIntent == 1 && agent.Progress > TurnStart)
            {
                float angle = Math.Min((float)Math.PI * .5f, (agent.Progress - TurnStart) / TurnRadius);
                x = 9.2f - TurnRadius * (float)Math.Cos(angle);
                z = -9.2f + TurnRadius * (float)Math.Sin(angle);
                if (agent.Progress > TurnEnd) x += agent.Progress - TurnEnd;
                relativeHeading = angle * 180f / (float)Math.PI;
            }
            switch (agent.Route)
            {
                case 0: agent.X = x; agent.Z = z; break;
                case 1: agent.X = -x; agent.Z = -z; break;
                case 2: agent.X = z; agent.Z = -x; break;
                default: agent.X = -z; agent.Z = x; break;
            }
            agent.HeadingDegrees = NormalizeDegrees(VehicleHeading(agent.Route) + relativeHeading);
        }

        private static float ExitProgress(SimAgent agent) => agent.TurnIntent == 1 ? TurnEnd + 5.8f : IntersectionExit;
        private static int CurrentLane(SimAgent agent) => agent.TurnIntent == 0 || agent.Progress <= TurnStart ? agent.Route :
            agent.Progress < TurnEnd ? -1 : RightExitRoute(agent.Route);
        public static int RightExitRoute(int route) => route == 0 ? 2 : route == 1 ? 3 : route == 2 ? 1 : 0;
        private static float TravelCoordinate(int route, float x, float z) => route == 0 ? z : route == 1 ? -z : route == 2 ? x : -x;

        private static float VehicleHeading(int route) => route == 0 ? 0f : route == 1 ? 180f : route == 2 ? 90f : 270f;
        private static float Length(float x, float z) => (float)Math.Sqrt(x * x + z * z);
        private static float NormalizeDegrees(float angle) => (angle % 360f + 360f) % 360f;
        private static float MoveTowards(float value, float target, float maxDelta) => value < target ? Math.Min(value + maxDelta, target) : Math.Max(value - maxDelta, target);
        private float NextFloat()
        {
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            return (random & 0xFFFFFFu) / 16777216f;
        }
        private int NextInt(int exclusiveMax) => Math.Min(exclusiveMax - 1, (int)(NextFloat() * exclusiveMax));
    }
}
