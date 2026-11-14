using System;
using System.Collections.Generic;

namespace StationFlow.SyntheticTraffic
{
    public sealed partial class TrafficSimulation
    {
        /// <summary>True means a lifecycle change here would be visible. Null is an unobserved perimeter.</summary>
        public Func<float, float, float, bool> VisibilityGuard { get; set; }
        public int CompletedPedestrianTrips { get; private set; }
        public int SpawnedPedestrians { get; private set; }
        public int DespawnedPedestrians { get; private set; }
        public int SpawnedVehicles { get; private set; }
        public int DespawnedVehicles { get; private set; }
        public int PlannedCornerTrips { get; private set; }
        public int PlannedCrossingTrips { get; private set; }
        public const float PortalDistance = 108f;

        struct Point
        {
            public float X, Z;
            public Point(float x, float z) { X = x; Z = z; }
        }
        sealed class Edge
        {
            public int To; public bool Crosswalk;
            public Edge(int to, bool crosswalk) { To = to; Crosswalk = crosswalk; }
        }
        sealed class NavNode
        {
            public Point Position;
            public readonly List<Edge> Edges = new List<Edge>();
            public NavNode(float x, float z) { Position = new Point(x, z); }
        }
        sealed class Walker
        {
            public SimAgent Agent;
            public Journey Journey;
            public List<int> Path;
            public int Segment = 1;
            public bool Committed, EnteredRoad, CrossingCounted, TripCounted, Exiting;
            public float WishX, WishZ, AvoidanceAt, ExitDistance = PortalDistance;
            public float CruiseSpeed, LateralOffset, QueueJitter, LastProgressAt;
            public int QueueTicket;
            public Point PreviousPosition;
        }
        sealed class Journey
        {
            public int Id, Source, Destination;
            public readonly List<Walker> Members = new List<Walker>();
            public float ArrivalAt;
        }
        struct Obstacle
        {
            public float X, Z, Radius;
            public Obstacle(float x, float z, float radius) { X = x; Z = z; Radius = radius; }
        }
        readonly List<NavNode> sidewalk = new List<NavNode>();
        readonly List<Walker> walkers = new List<Walker>();
        readonly List<Journey> journeys = new List<Journey>();
        readonly List<Obstacle> obstacles = new List<Obstacle>();
        readonly List<Walker> neighbours = new List<Walker>();
        int nextGroupId = 1;
        int nextQueueTicket = 1;
        static readonly float[] avoidanceAngles = { 0, 15, -15, 30, -30, 45, -45, 60, -60, 75, -75, 90, -90, 105, -105, 120, -120, 135, -135, 150, -150, 165, -165, 180 };
        static readonly float[] avoidanceSpeeds = { 1f, .72f, .38f, 0f };

        /// <summary>Add a circular furniture footprint in metres before advancing simulation.</summary>
        public void AddPedestrianObstacle(float x, float z, float radius)
        {
            if (radius <= 0 || float.IsNaN(x) || float.IsNaN(z) || float.IsNaN(radius) ||
                float.IsInfinity(x) || float.IsInfinity(z) || float.IsInfinity(radius))
                throw new ArgumentOutOfRangeException(nameof(radius));
            obstacles.Add(new Obstacle(x, z, radius));
        }

        void InitializeCrowd(int count, int vehicleCount)
        {
            BuildSidewalkGraph();
            int person = 0, trip = 0;
            while (person < count)
            {
                // A mixture of independent travellers, pairs and trios, not identical queues.
                int size = trip % 5 == 1 || trip % 5 == 3 ? 2 : trip % 5 == 4 ? 3 : 1;
                size = Math.Min(size, count - person);
                var journey = new Journey { Source = trip % 8 };
                journeys.Add(journey);
                for (int slot = 0; slot < size; ++slot)
                {
                    var a = new SimAgent
                    {
                        Id = vehicleCount + person + 1, EntityId = vehicleCount + person + 1,
                        Kind = AgentKind.Pedestrian, Variant = NextInt(12),
                        Length = PedestrianRadius * 2, Width = PedestrianRadius * 2,
                        GroupSlot = slot, GroupSize = size, IsGroupLeader = size > 1 && slot == 0,
                        ReactionDelay = .25f + NextFloat() * .7f,
                        Active = true, State = "Walking"
                    };
                    var w = new Walker { Agent = a, Journey = journey, QueueJitter = NextFloat() };
                    journey.Members.Add(w); walkers.Add(w); pedestrians.Add(a); Agents.Add(a);
                    person++;
                }
                PlanJourney(journey, true);
                // The initial scene is a populated simulation snapshot. Subsequent births
                // all occur at hidden portals; nobody is reset in the camera's view.
                float initialDistance = 26f + (trip / 8) * 17f + NextFloat() * 5f;
                ActivateJourney(journey, initialDistance, true);
                trip++;
            }
        }

        void BuildSidewalkGraph()
        {
            for (int corner = 0; corner < 4; ++corner)
            {
                int sx = corner % 2 == 0 ? -1 : 1, sz = corner < 2 ? -1 : 1;
                int n = sidewalk.Count;
                sidewalk.Add(new NavNode(sx * 11.2f, sz * 11.2f)); // corner plaza
                sidewalk.Add(new NavNode(sx * 8.45f, sz * 10f)); // east/west kerb
                sidewalk.Add(new NavNode(sx * 10f, sz * 8.45f)); // north/south kerb
                sidewalk.Add(new NavNode(sx * 22f, sz * 11.2f));
                sidewalk.Add(new NavNode(sx * 11.2f, sz * 22f));
                sidewalk.Add(new NavNode(sx * PortalDistance, sz * 11.2f));
                sidewalk.Add(new NavNode(sx * 11.2f, sz * PortalDistance));
                Link(n, n + 1); Link(n, n + 2); Link(n, n + 3); Link(n, n + 4);
                Link(n + 3, n + 5); Link(n + 4, n + 6);
            }
            Link(1, 8, true); Link(15, 22, true);
            Link(2, 16, true); Link(9, 23, true);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    for (int j = 0; j < 4; j++)
                    {
                        AddPedestrianObstacle(sx * 8.5f, sz * (21 + 17 * j), .24f);
                        AddPedestrianObstacle(sx * (21 + 17 * j), sz * 8.5f, .24f);
                        AddPedestrianObstacle(sx * 13.4f, sz * (26 + 16 * j), .48f);
                        AddPedestrianObstacle(sx * (26 + 16 * j), sz * 13.4f, .48f);
                    }
                    for (int n = 0; n < 4; n += 3) AddPedestrianObstacle(sx * (8.2f + n * 1.15f), sz * 8.1f, .12f);
                    AddPedestrianObstacle(sx * 7.8f, sz * 12.8f, .22f);
                    AddPedestrianObstacle(sx * 12.8f, sz * 7.8f, .22f);
                }
        }

        void Link(int a, int b, bool crosswalk = false)
        {
            sidewalk[a].Edges.Add(new Edge(b, crosswalk));
            sidewalk[b].Edges.Add(new Edge(a, crosswalk));
        }
        int PortalNode(int portal) => (portal / 2) * 7 + 5 + portal % 2;
        bool CrossingEdge(int from, int to)
        {
            foreach (var edge in sidewalk[from].Edges) if (edge.To == to) return edge.Crosswalk;
            return false;
        }

        // A* over a connected, signal-aware sidewalk/crosswalk graph. Signals affect
        // entry permission, never disconnect a destination or cause a visible U-turn.
        List<int> FindWalkingRoute(int source, int destination)
        {
            int start = PortalNode(source), goal = PortalNode(destination), n = sidewalk.Count;
            var cost = new float[n]; var previous = new int[n]; var closed = new bool[n];
            for (int i = 0; i < n; i++) { cost[i] = float.PositiveInfinity; previous[i] = -1; }
            cost[start] = 0;
            while (true)
            {
                int best = -1; float score = float.PositiveInfinity;
                for (int i = 0; i < n; i++)
                {
                    if (closed[i]) continue;
                    float f = cost[i] + Distance(sidewalk[i].Position, sidewalk[goal].Position);
                    if (f < score) { best = i; score = f; }
                }
                if (best < 0 || best == goal) break;
                closed[best] = true;
                foreach (var edge in sidewalk[best].Edges)
                {
                    float candidate = cost[best] + Distance(sidewalk[best].Position, sidewalk[edge.To].Position) + (edge.Crosswalk ? 4f : 0f);
                    if (candidate < cost[edge.To]) { cost[edge.To] = candidate; previous[edge.To] = best; }
                }
            }
            var path = new List<int>();
            for (int node = goal; node >= 0; node = previous[node]) path.Add(node);
            path.Reverse();
            return path;
        }

        void PlanJourney(Journey journey, bool initial)
        {
            if (!initial) journey.Source = NextInt(8);
            // Corner visitors share the pavement with waiting crossers; other journeys
            // cross one or two arms and keep going towards another distant district.
            int choice = NextInt(10);
            if (choice < 4) journey.Destination = journey.Source ^ 1;
            else if (choice < 8)
            {
                int corner = journey.Source / 2;
                int neighbour = corner ^ (NextInt(2) == 0 ? 1 : 2);
                journey.Destination = neighbour * 2 + NextInt(2);
            }
            else journey.Destination = ((journey.Source / 2) ^ 3) * 2 + NextInt(2);
            var route = FindWalkingRoute(journey.Source, journey.Destination);
            int crossings = 0;
            for (int p = 1; p < route.Count; p++) if (CrossingEdge(route[p - 1], route[p])) crossings++;
            if (crossings == 0) PlannedCornerTrips++; else PlannedCrossingTrips++;
            journey.Id = journey.Members.Count == 1 ? 0 : nextGroupId++;
            float groupSpeed = 1.12f + NextFloat() * .42f;
            foreach (var w in journey.Members)
            {
                var a = w.Agent;
                a.Route = journey.Source; a.ArrivalPortal = journey.Source; a.DeparturePortal = journey.Destination;
                a.GroupId = journey.Id; a.DesiredSpeed = groupSpeed;
                a.NavigationIntent = crossings == 0 ? "Corner" : crossings == 1 ? "OneCrossing" : "TwoCrossings";
                a.IsSidewalkOnly = crossings == 0;
                w.Path = route; w.Segment = 1; w.Committed = w.EnteredRoad = w.Exiting = w.TripCounted = w.CrossingCounted = false;
                w.CruiseSpeed = groupSpeed * (.985f + NextFloat() * .03f);
                w.LateralOffset = .18f + (a.GroupSlot == 1 ? .88f : a.GroupSlot == 2 ? -.88f : 0f);
                w.AvoidanceAt = 0; w.ExitDistance = PortalDistance;
                w.QueueTicket = 0;
            }
        }

        Point PortalPosition(int portal, float distance)
        {
            Point p = sidewalk[PortalNode(portal)].Position;
            if (portal % 2 == 0) p.X = Math.Sign(p.X) * distance;
            else p.Z = Math.Sign(p.Z) * distance;
            return p;
        }
        Point SpawnPosition(Walker w, float distance)
        {
            Point p = PortalPosition(w.Journey.Source, distance);
            Point target = sidewalk[w.Path[1]].Position;
            float dx = target.X - p.X, dz = target.Z - p.Z, l = Length(dx, dz);
            p.X += dz / l * w.LateralOffset; p.Z -= dx / l * w.LateralOffset;
            return p;
        }
        bool ActivateJourney(Journey journey, float distance, bool initial)
        {
            if (!initial)
            {
                foreach (var w in journey.Members)
                {
                    Point p = SpawnPosition(w, distance);
                    if (IsVisible(p.X, p.Z, 1.4f) || !SpawnSpaceClear(p.X, p.Z, 1.1f, journey)) return false;
                }
            }
            foreach (var w in journey.Members)
            {
                SimAgent a = w.Agent; Point p = SpawnPosition(w, distance);
                if (!initial) { a.Id = nextTrackId++; SpawnedPedestrians++; }
                a.X = p.X; a.Z = p.Z; a.Active = true; a.Speed = 0;
                a.VelocityX = a.VelocityZ = 0; a.LifetimeStart = (float)Time;
                a.Moving = false; a.Counted = false; a.Conversation = a.Looking = a.Regrouping = false;
                a.State = "Walking"; a.ReleaseAt = (float)Time + a.ReactionDelay;
                Point target = sidewalk[w.Path[1]].Position;
                a.HeadingDegrees = NormalizeDegrees((float)(Math.Atan2(target.X - a.X, target.Z - a.Z) * 180 / Math.PI));
                w.PreviousPosition = p; w.LastProgressAt = (float)Time;
            }
            return true;
        }
        bool IsVisible(float x, float z, float radius) => VisibilityGuard != null && VisibilityGuard(x, z, radius);

        void UpdateInactiveArrivals()
        {
            foreach (var journey in journeys)
            {
                bool anyActive = false;
                foreach (var w in journey.Members) anyActive |= w.Agent.Active;
                if (anyActive || Time < journey.ArrivalAt) continue;
                bool activated = false;
                for (float distance = PortalDistance; distance <= 268; distance += 32)
                    if (ActivateJourney(journey, distance, false)) { activated = true; break; }
                if (!activated) journey.ArrivalAt = (float)Time + .8f;
            }
            foreach (var a in orderedVehicles)
            {
                if (a.Active || Time < a.ReleaseAt) continue;
                for (float distance = PortalDistance; distance <= 268; distance += 32)
                {
                    a.Progress = -distance; PlaceVehicle(a);
                    if (IsVisible(a.X, a.Z, a.Length + 1f)) continue;
                    bool clear = true;
                    foreach (var other in orderedVehicles)
                        if (other != a && other.Active && other.Route == a.Route && Math.Abs(other.Progress - a.Progress) < 16f) { clear = false; break; }
                    if (!clear) continue;
                    a.Active = true; a.Id = nextTrackId++; a.LifetimeStart = (float)Time;
                    a.Speed = a.DesiredSpeed * .8f; a.Counted = false; a.State = "Approaching";
                    a.ReleaseAt = (float)Time + a.ReactionDelay; SpawnedVehicles++;
                    break;
                }
                if (!a.Active) a.ReleaseAt = (float)Time + .8f;
            }
        }

        void UpdateVehicleLifetimes()
        {
            foreach (var a in orderedVehicles)
            {
                if (!a.Active) continue;
                double h = a.HeadingDegrees * Math.PI / 180;
                a.VelocityX = a.Speed * (float)Math.Sin(h); a.VelocityZ = a.Speed * (float)Math.Cos(h);
                a.DeparturePortal = a.TurnIntent == 0 ? a.Route : RightExitRoute(a.Route);
                if (Math.Max(Math.Abs(a.X), Math.Abs(a.Z)) < PortalDistance || a.Progress < TurnEnd ||
                    IsVisible(a.X, a.Z, a.Length + 1f)) continue;
                a.Active = false; a.Speed = 0; a.VelocityX = a.VelocityZ = 0;
                a.State = "Inactive"; a.BrakeLights = a.IndicatorRight = false;
                a.Route = NextInt(4); a.ArrivalPortal = a.Route;
                a.TurnIntent = NextInt(3) == 0 ? 1 : 0;
                a.ReleaseAt = (float)Time + 1.8f + NextFloat() * 7f;
                DespawnedVehicles++;
            }
        }

        void OpenPedestrianSignal()
        {
            foreach (var w in walkers)
            {
                var a = w.Agent;
                if (!a.Active || w.Committed) continue;
                a.ReleaseAt = (float)Time + a.ReactionDelay + (a.GroupSlot > 0 ? .1f * a.GroupSlot : 0);
            }
        }

        void MovePedestrians(float dt)
        {
            walkers.Sort(CompareWalkerOrder);
            foreach (var w in walkers)
            {
                var a = w.Agent;
                if (!a.Active) continue;
                Point target; bool waiting, crossing;
                GetWalkingTarget(w, out target, out waiting, out crossing);
                a.NavigationTargetX = target.X; a.NavigationTargetZ = target.Z;
                float dx = target.X - a.X, dz = target.Z - a.Z, distance = Length(dx, dz);
                float cruise = w.CruiseSpeed;
                float cohesionX = 0, cohesionZ = 0;
                if (a.GroupSize > 1 && !crossing && !waiting && !a.Regrouping)
                {
                    SimAgent leader = w.Journey.Members[0].Agent;
                    if (distance > .1f)
                    {
                        float behind = 0, ahead = 0;
                        float progress = WalkingRouteProgress(w);
                        foreach (var partner in w.Journey.Members)
                        {
                            if (partner.Agent == a || !partner.Agent.Active) continue;
                            float along = WalkingRouteProgress(partner) - progress;
                            ahead = Math.Max(ahead, along); behind = Math.Max(behind, -along);
                        }
                        // Whoever happens to be in front waits for the slowest friend.
                        // A designated leader can also fall behind during an encounter.
                        if (behind > 3.5f) cruise = 0;
                        else if (behind > 1.5f) cruise *= Clamp(1f - (behind - 1.5f) * .32f, .08f, 1f);
                        else if (ahead > 1.4f) cruise *= Clamp(1f + (ahead - 1.4f) * .10f, 1f, 1.15f);
                    }
                    if (!a.IsGroupLeader && leader.Active && distance > .1f)
                    {
                        float h = leader.HeadingDegrees * (float)Math.PI / 180f;
                        float side = a.GroupSlot == 1 ? .92f : -.92f;
                        float formationX = leader.X + (float)Math.Cos(h) * side;
                        float formationZ = leader.Z - (float)Math.Sin(h) * side;
                        // A friend who has stepped aside cannot pull the whole party
                        // into the edge of the walkable strip. Preserve clearance for
                        // the outside member while their leader returns to the route.
                        if (Math.Abs(a.X) > 14f) formationZ = Clamp(formationZ, -12.3f, 12.3f);
                        if (Math.Abs(a.Z) > 14f) formationX = Clamp(formationX, -12.3f, 12.3f);
                        float errorX = formationX - a.X, errorZ = formationZ - a.Z;
                        float along = (errorX * dx + errorZ * dz) / distance;
                        // Longitudinal spacing is controlled by speed; a lateral spring
                        // restores side-by-side formation without pulling someone backward.
                        cohesionX = Clamp((errorX - along * dx / distance) * .22f, -.3f, .3f);
                        cohesionZ = Clamp((errorZ - along * dz / distance) * .22f, -.3f, .3f);
                    }
                }
                if (waiting) cruise = Math.Min(cruise, distance * 1.9f);
                if (distance > .01f) { dx = dx / distance * cruise; dz = dz / distance * cruise; }
                else dx = dz = 0;
                if (!waiting) { dx += cohesionX; dz += cohesionZ; }
                if (Time >= w.AvoidanceAt)
                {
                    SelectVelocity(w, dx, dz, crossing, waiting && distance < .25f);
                    w.AvoidanceAt = (float)Time + .10f + (a.EntityId % 3) * .006f;
                }
                float wishX = w.WishX, wishZ = w.WishZ;
                ApplyComfortBraking(w, crossing, ref wishX, ref wishZ);
                float nextVx = a.VelocityX, nextVz = a.VelocityZ;
                float accelX = wishX - nextVx, accelZ = wishZ - nextVz;
                bool brakingEnvelope = Squared(wishX - w.WishX, wishZ - w.WishZ) > .0004f;
                float accel = Length(accelX, accelZ), maxAccel = (brakingEnvelope ? 4.5f : Length(wishX, wishZ) < a.Speed ? 2.5f : 1.7f) * dt;
                float blend = accel > maxAccel ? maxAccel / accel : 1;
                nextVx += accelX * blend; nextVz += accelZ * blend;
                float moveX = nextVx * dt, moveZ = nextVz * dt;
                float fraction = SafeFraction(w, moveX, moveZ, crossing);
                if (fraction < 1) { moveX *= fraction; moveZ *= fraction; w.AvoidanceAt = 0; }
                a.X += moveX; a.Z += moveZ;
                if (w.Committed && !w.EnteredRoad && (Math.Abs(a.X) < 7.5f || Math.Abs(a.Z) < 7.5f))
                {
                    w.EnteredRoad = true;
                    if (!IsPedestrianWalk) PedestrianRedLightViolations++;
                }
                a.VelocityX = moveX / dt; a.VelocityZ = moveZ / dt;
                a.Speed = Length(a.VelocityX, a.VelocityZ);
                if (a.Speed > .075f)
                {
                    float heading = NormalizeDegrees((float)(Math.Atan2(a.VelocityX, a.VelocityZ) * 180 / Math.PI));
                    float delta = (heading - a.HeadingDegrees + 540f) % 360f - 180f;
                    a.HeadingDegrees = NormalizeDegrees(a.HeadingDegrees + Clamp(delta, -155f * dt, 155f * dt));
                }
                a.Looking = waiting && !a.Regrouping && a.Speed < .3f;
                a.LookYaw = a.Looking ? (float)Math.Sin(Time * 1.8 + a.EntityId * 1.3) * 33f : 0;
                a.State = a.Regrouping ? "Regrouping" : waiting && a.Speed < .15f ? "Waiting" : crossing ? "Crossing" :
                    a.Speed < w.CruiseSpeed * .45f && !waiting ? "Yielding" :
                    a.GroupSize > 1 ? "WalkingTogether" : "Walking";
                UpdateConversation(w);
                if (a.Conversation && !waiting && a.Speed > .2f) a.State = "Conversing";
                if (Distance(w.PreviousPosition, new Point(a.X, a.Z)) > .35f)
                { w.PreviousPosition = new Point(a.X, a.Z); w.LastProgressAt = (float)Time; }
            }
            UpdatePedestrianLifetimes();
        }

        int CompareWalkerOrder(Walker a, Walker b)
        {
            int aFrom = a.Path[a.Segment - 1], bFrom = b.Path[b.Segment - 1];
            int aTo = a.Path[a.Segment], bTo = b.Path[b.Segment];
            int key = (aFrom * 32 + aTo).CompareTo(bFrom * 32 + bTo);
            if (key != 0) return key;
            Point start = sidewalk[aFrom].Position, end = sidewalk[aTo].Position;
            float ax = a.Agent.X, az = a.Agent.Z, bx = b.Agent.X, bz = b.Agent.Z;
            int progress = ((bx - ax) * (end.X - start.X) + (bz - az) * (end.Z - start.Z)).CompareTo(0f);
            return progress != 0 ? progress : a.Agent.EntityId.CompareTo(b.Agent.EntityId);
        }

        float WalkingRouteProgress(Walker w)
        {
            float distance = 0;
            for (int i = 1; i < w.Segment; ++i)
                distance += Distance(sidewalk[w.Path[i - 1]].Position, sidewalk[w.Path[i]].Position);
            Point from = sidewalk[w.Path[w.Segment - 1]].Position, to = sidewalk[w.Path[w.Segment]].Position;
            float dx = to.X - from.X, dz = to.Z - from.Z, length = Length(dx, dz);
            // Signed projection allows meaningful progress beyond a source or sink.
            // Unlike a dot product against each person's local desired direction,
            // this consistently orders companions while they round a shared corner.
            return distance + ((w.Agent.X - from.X) * dx + (w.Agent.Z - from.Z) * dz) / length;
        }

        void ApplyComfortBraking(Walker w, bool crossing, ref float vx, ref float vz)
        {
            // Continuous braking envelope runs every physics step, between the slower
            // anticipatory steering decisions. It leaves space for a following person
            // to slow down instead of arriving at the hard body-contact boundary.
            var a = w.Agent;
            // Reproject the complete set: avoiding a second neighbour can otherwise
            // reintroduce a closing component towards the first one in a dense corner.
            for (int pass = 0; pass < 3; pass++)
            {
            foreach (var other in walkers)
            {
                var b = other.Agent;
                if (b == a || !b.Active) continue;
                float dx = b.X - a.X, dz = b.Z - a.Z, square = dx * dx + dz * dz;
                if (square > 4.84f || square < .00001f) continue;
                float distance = (float)Math.Sqrt(square), nx = dx / distance, nz = dz / distance;
                float closing = (vx - b.VelocityX) * nx + (vz - b.VelocityZ) * nz;
                float limit = (float)Math.Sqrt(2f * 1.6f * Math.Max(0, distance - .82f));
                if (closing <= limit) continue;
                vx -= nx * (closing - limit); vz -= nz * (closing - limit);
            }
            foreach (var obstacle in obstacles)
            {
                float dx = obstacle.X - a.X, dz = obstacle.Z - a.Z, square = dx * dx + dz * dz;
                if (square > 4 || square < .00001f) continue;
                float distance = (float)Math.Sqrt(square), nx = dx / distance, nz = dz / distance;
                float closing = vx * nx + vz * nz;
                float limit = BrakingLimit(distance - obstacle.Radius - PedestrianRadius - .15f);
                if (closing <= limit) continue;
                vx -= nx * (closing - limit); vz -= nz * (closing - limit);
            }
            if (crossing)
            {
                Point end = sidewalk[w.Path[w.Segment]].Position, start = sidewalk[w.Path[w.Segment - 1]].Position;
                if (Math.Abs(end.X - start.X) > Math.Abs(end.Z - start.Z))
                    vz = Clamp(vz, -BrakingLimit(a.Z - end.Z + .94f), BrakingLimit(end.Z + .94f - a.Z));
                else vx = Clamp(vx, -BrakingLimit(a.X - end.X + .94f), BrakingLimit(end.X + .94f - a.X));
            }
            else
            {
                float ax = Math.Abs(a.X), az = Math.Abs(a.Z);
                if (a.X > 0) vx = Math.Max(vx, -BrakingLimit(ax - 7.62f));
                else vx = Math.Min(vx, BrakingLimit(ax - 7.62f));
                if (a.Z > 0) vz = Math.Max(vz, -BrakingLimit(az - 7.62f));
                else vz = Math.Min(vz, BrakingLimit(az - 7.62f));
                if (az > 12.45f)
                {
                    float outer = az < 13.85f ? 13.85f : 12.55f;
                    if (a.X > 0) vx = Math.Min(vx, BrakingLimit(outer - ax));
                    else vx = Math.Max(vx, -BrakingLimit(outer - ax));
                }
                if (ax > 12.45f)
                {
                    float outer = ax < 13.85f ? 13.85f : 12.55f;
                    if (a.Z > 0) vz = Math.Min(vz, BrakingLimit(outer - az));
                    else vz = Math.Max(vz, -BrakingLimit(outer - az));
                }
            }
            }
        }
        static float BrakingLimit(float space) => (float)Math.Sqrt(2f * 1.6f * Math.Max(0, space));

        void GetWalkingTarget(Walker w, out Point target, out bool waiting, out bool crossing)
        {
            SimAgent a = w.Agent;
            waiting = false; crossing = false;
            a.Regrouping = false;
            if (w.Exiting)
            {
                Point exit = PortalPosition(w.Journey.Destination, Math.Max(w.ExitDistance, Math.Max(Math.Abs(a.X), Math.Abs(a.Z)) + 10f));
                target = OffsetAlong(a, exit, w.LateralOffset); return;
            }
            if (!w.Committed && a.GroupSize > 1)
            {
                // If a friend missed this WALK interval, wait in the receiving plaza.
                // The stopping point is beyond the crossing exit and passing streams.
                int lastCrossing = -1;
                for (int i = w.Segment - 1; i > 0; --i)
                    if (CrossingEdge(w.Path[i - 1], w.Path[i])) { lastCrossing = i; break; }
                if (lastCrossing >= 0)
                {
                    bool friendBehind = false;
                    foreach (var friend in w.Journey.Members)
                        if (friend != w && friend.Agent.Active && friend.Segment <= lastCrossing) { friendBehind = true; break; }
                    if (friendBehind)
                    {
                        Point exit = sidewalk[w.Path[lastCrossing]].Position;
                        float sx = Math.Sign(exit.X), sz = Math.Sign(exit.Z);
                        target = new Point(sx * (a.GroupSlot == 1 ? 13.5f : a.GroupSlot == 2 ? 12.1f : 12.8f),
                            sz * (a.GroupSlot == 1 ? 12.1f : a.GroupSlot == 2 ? 13.5f : 12.8f));
                        a.Regrouping = true; waiting = true; return;
                    }
                }
            }
            int from = w.Path[w.Segment - 1], to = w.Path[w.Segment];
            Point start = sidewalk[from].Position, end = sidewalk[to].Position;
            bool crossEdge = CrossingEdge(from, to);
            float vx = end.X - start.X, vz = end.Z - start.Z, length = Length(vx, vz);
            float fraction = ((a.X - start.X) * vx + (a.Z - start.Z) * vz) / (length * length);
            float effectiveSide = crossEdge ? .52f : w.LateralOffset;
            float endDistance = Distance(new Point(a.X, a.Z), new Point(end.X + vz / length * effectiveSide, end.Z - vx / length * effectiveSide));
            if (fraction > .99f || endDistance < (crossEdge ? .32f : to % 7 == 0 ? 1.7f : .68f))
            {
                if (crossEdge && w.Committed)
                {
                    CompletedPedestrianCrossings++; CompletedCrossings++;
                    w.Committed = w.EnteredRoad = false; a.Moving = false;
                }
                if (w.Segment == w.Path.Count - 1)
                {
                    w.Exiting = true;
                    if (!w.TripCounted) { CompletedPedestrianTrips++; w.TripCounted = true; }
                    GetWalkingTarget(w, out target, out waiting, out crossing); return;
                }
                w.Segment++; w.AvoidanceAt = 0; w.QueueTicket = 0;
                GetWalkingTarget(w, out target, out waiting, out crossing); return;
            }
            if (crossEdge && !w.Committed)
            {
                if (w.QueueTicket == 0) w.QueueTicket = nextQueueTicket++;
                int ahead = 0;
                foreach (var other in walkers)
                    if (other != w && other.Agent.Active && !other.Committed && !other.Exiting && other.QueueTicket > 0 &&
                        other.QueueTicket < w.QueueTicket && other.Path[other.Segment - 1] == from && other.Path[other.Segment] == to) ahead++;
                if (IsPedestrianWalk && PhaseRemaining > 3.5f && Time >= a.ReleaseAt && ahead == 0 &&
                    Distance(new Point(a.X, a.Z), start) < 1.2f &&
                    Math.Abs((a.X - start.X) * vz / length - (a.Z - start.Z) * vx / length - .52f) < .32f)
                {
                    w.Committed = true; a.Moving = true;
                }
                else
                {
                    waiting = true;
                    target = new Point(start.X - vx / length * (.08f + ahead * .83f) + vz / length * .52f,
                        start.Z - vz / length * (.08f + ahead * .83f) - vx / length * .52f);
                    return;
                }
            }
            crossing = crossEdge && w.Committed;
            if (waiting)
            {
                float approachHeading = (float)Math.Atan2(vx, vz);
                float side = w.LateralOffset;
                // Preserve partner spacing while allowing arrivals to spread naturally.
                target = new Point(start.X + (float)Math.Cos(approachHeading) * side,
                    start.Z - (float)Math.Sin(approachHeading) * side);
                return;
            }
            // Following a point ahead of the projection avoids path-node snapping. At
            // sidewalk bends look through the next segment, rounding the corner.
            float look = Math.Max(0f, fraction * length) + 1.8f;
            Point aim = new Point(start.X + vx / length * Math.Min(look, length), start.Z + vz / length * Math.Min(look, length));
            if (!crossEdge && look > length && w.Segment + 1 < w.Path.Count && !CrossingEdge(to, w.Path[w.Segment + 1]))
            {
                Point following = sidewalk[w.Path[w.Segment + 1]].Position;
                float nx = following.X - end.X, nz = following.Z - end.Z, nl = Length(nx, nz);
                aim = new Point(end.X + nx / nl * Math.Min(look - length, nl), end.Z + nz / nl * Math.Min(look - length, nl));
            }
            // A social group compresses into the directional crossing lane. Partners
            // regain their side-by-side positions on the wider receiving pavement.
            target = new Point(aim.X + vz / length * effectiveSide, aim.Z - vx / length * effectiveSide);
            if (crossEdge)
            {
                // Keep the complete body on the marked zebra, including a trio's outside member.
                if (Math.Abs(vx) > Math.Abs(vz)) target.Z = Clamp(target.Z, end.Z - 1.0f, end.Z + 1.0f);
                else target.X = Clamp(target.X, end.X - 1.0f, end.X + 1.0f);
            }
        }

        static Point OffsetAlong(SimAgent a, Point point, float side)
        {
            float dx = point.X - a.X, dz = point.Z - a.Z, l = Length(dx, dz);
            if (l < .001f) return point;
            return new Point(point.X + dz / l * side, point.Z - dx / l * side);
        }

        void UpdateConversation(Walker w)
        {
            SimAgent a = w.Agent;
            a.Conversation = false; a.GestureBlend = 0; a.ConversationLookYaw = 0;
            if (a.GroupSize < 2 || a.Moving || a.State == "Yielding") return;
            SimAgent nearest = null; float distance = 3.4f;
            foreach (var partner in w.Journey.Members)
            {
                var b = partner.Agent;
                if (b == a || !b.Active) continue;
                float d = Length(b.X - a.X, b.Z - a.Z);
                if (d < distance) { nearest = b; distance = d; }
            }
            if (nearest == null) return;
            double phrase = Time * .63 + a.Id * 1.713;
            a.Conversation = Math.Sin(phrase) > -.55;
            float heading = (float)(Math.Atan2(nearest.X - a.X, nearest.Z - a.Z) * 180 / Math.PI);
            float yaw = (heading - a.HeadingDegrees + 540f) % 360f - 180f;
            a.ConversationLookYaw = Clamp(yaw, -65, 65);
            a.GestureBlend = a.Conversation && !a.Looking ? (float)Math.Pow(Math.Max(0, Math.Sin(phrase * 1.41 + a.Id)), 2) * .8f : 0;
        }

        void SelectVelocity(Walker w, float preferredX, float preferredZ, bool crossing, bool waiting)
        {
            SimAgent a = w.Agent;
            neighbours.Clear();
            foreach (var other in walkers)
                if (other != w && other.Agent.Active && Math.Abs(other.Agent.X - a.X) < 4.5f && Math.Abs(other.Agent.Z - a.Z) < 4.5f)
                    neighbours.Add(other);
            float desiredLength = Length(preferredX, preferredZ);
            if (desiredLength < .015f) { w.WishX = w.WishZ = 0; return; }
            float bestCost = float.PositiveInfinity, bestX = 0, bestZ = 0;
            // Velocity-obstacle sampling anticipates encounters two seconds ahead.
            // A small shared keep-right bias resolves symmetric head-on encounters.
            foreach (float speedScale in avoidanceSpeeds)
            {
                foreach (float angle in avoidanceAngles)
                {
                    if (speedScale == 0 && angle != 0) continue;
                    double r = angle * Math.PI / 180;
                    float vx = (float)(preferredX * Math.Cos(r) + preferredZ * Math.Sin(r)) * speedScale;
                    float vz = (float)(preferredZ * Math.Cos(r) - preferredX * Math.Sin(r)) * speedScale;
                    float cost = Squared(vx - preferredX, vz - preferredZ) * 1.2f +
                        Squared(vx - a.VelocityX, vz - a.VelocityZ) * .22f + (angle < 0 ? .025f : 0f);
                    // A pedestrian who cannot make forward progress is willing to step
                    // sideways/backwards to get around a corner or free another person.
                    // Waiting at a signal remains a deliberate stationary behaviour.
                    if (!waiting) cost += (1f - speedScale) * Clamp((float)Time - w.LastProgressAt - 1f, 0f, 4f) * desiredLength * desiredLength;
                    if (!Walkable(w, a.X + vx * .025f, a.Z + vz * .025f, crossing) ||
                        !Walkable(w, a.X + vx * .10f, a.Z + vz * .10f, crossing) ||
                        !Walkable(w, a.X + vx * .3f, a.Z + vz * .3f, crossing) ||
                        !Walkable(w, a.X + vx * .8f, a.Z + vz * .8f, crossing)) cost += 1000;
                    foreach (var other in neighbours)
                    {
                        var b = other.Agent;
                        float px = b.X - a.X, pz = b.Z - a.Z;
                        float rvx = vx - b.VelocityX, rvz = vz - b.VelocityZ;
                        float vv = Squared(rvx, rvz);
                        float closing = px * rvx + pz * rvz;
                        float closestAt = vv > .0001f ? Clamp(closing / vv, 0, 2.2f) : 0;
                        float separation = Length(px - rvx * closestAt, pz - rvz * closestAt);
                        float radius = b.GroupId != 0 && b.GroupId == a.GroupId ? .61f : .75f;
                        if (closing > .001f && separation < radius) cost += (radius - separation) * (2.5f - closestAt) * 40f;
                        if (separation < 1.15f) cost += (1.15f - separation) * .12f;
                    }
                    foreach (var obstacle in obstacles)
                    {
                        float px = obstacle.X - a.X, pz = obstacle.Z - a.Z;
                        if (Math.Abs(px) > 3 || Math.Abs(pz) > 3) continue;
                        float vv = vx * vx + vz * vz;
                        float at = vv > .001f ? Clamp((px * vx + pz * vz) / vv, 0, 1.4f) : 0;
                        float separation = Length(px - vx * at, pz - vz * at);
                        float radius = obstacle.Radius + PedestrianRadius + .16f;
                        if (separation < radius) cost += (radius - separation) * 90f;
                    }
                    if (cost < bestCost) { bestCost = cost; bestX = vx; bestZ = vz; }
                }
            }
            w.WishX = bestX; w.WishZ = bestZ;

        }

        bool Walkable(Walker w, float x, float z, bool crossing)
        {
            float ax = Math.Abs(x), az = Math.Abs(z);
            if (crossing)
            {
                Point p = sidewalk[w.Path[w.Segment]].Position;
                Point from = sidewalk[w.Path[w.Segment - 1]].Position;
                if (Math.Abs(p.X - from.X) > Math.Abs(p.Z - from.Z)) return Math.Abs(z - p.Z) <= 1.03f && ax <= 9.65f;
                return Math.Abs(x - p.X) <= 1.03f && az <= 9.65f;
            }
            return ax >= 7.52f && az >= 7.52f && (ax <= 12.65f || az <= 12.65f || ax <= 14f && az <= 14f);
        }

        float SafeFraction(Walker w, float dx, float dz, bool crossing)
        {
            SimAgent a = w.Agent;
            float fraction = 1;
            if (!Walkable(w, a.X + dx, a.Z + dz, crossing))
            {
                float lo = 0, hi = 1;
                for (int k = 0; k < 8; k++)
                {
                    float mid = (lo + hi) * .5f;
                    if (Walkable(w, a.X + dx * mid, a.Z + dz * mid, crossing)) lo = mid; else hi = mid;
                }
                fraction = lo;
            }
            foreach (var other in walkers)
            {
                var b = other.Agent;
                if (b == a || !b.Active || Math.Abs(a.X - b.X) > 1.2f || Math.Abs(a.Z - b.Z) > 1.2f) continue;
                fraction = Math.Min(fraction, CircleSafeFraction(a.X - b.X, a.Z - b.Z, dx, dz, .575f));
            }
            foreach (var obstacle in obstacles)
            {
                if (Math.Abs(a.X - obstacle.X) > 2 || Math.Abs(a.Z - obstacle.Z) > 2) continue;
                fraction = Math.Min(fraction, CircleSafeFraction(a.X - obstacle.X, a.Z - obstacle.Z, dx, dz, obstacle.Radius + PedestrianRadius + .025f));
            }
            return fraction;
        }
        static float CircleSafeFraction(float px, float pz, float dx, float dz, float radius)
        {
            float aa = dx * dx + dz * dz;
            if (aa < 1e-12f) return 1;
            float bb = 2 * (px * dx + pz * dz);
            if (bb >= 0) return 1;
            float cc = px * px + pz * pz - radius * radius;
            if (cc <= 0) return 0;
            float discriminant = bb * bb - 4 * aa * cc;
            if (discriminant < 0) return 1;
            float root = (-bb - (float)Math.Sqrt(discriminant)) / (2 * aa);
            return root > 0 && root < 1 ? Math.Max(0, root - .005f) : 1;
        }
        bool SpawnSpaceClear(float x, float z, float radius, Journey ignore)
        {
            foreach (var w in walkers)
            {
                var a = w.Agent;
                if (!a.Active || w.Journey == ignore) continue;
                if (Squared(a.X - x, a.Z - z) < radius * radius) return false;
            }
            return true;
        }

        void UpdatePedestrianLifetimes()
        {
            foreach (var journey in journeys)
            {
                bool allOutside = true, anyActive = false;
                foreach (var w in journey.Members)
                {
                    var a = w.Agent; anyActive |= a.Active;
                    if (a.Active && (!w.Exiting || Math.Max(Math.Abs(a.X), Math.Abs(a.Z)) < PortalDistance || IsVisible(a.X, a.Z, 1.4f)))
                        allOutside = false;
                }
                if (!anyActive || !allOutside) continue;
                bool together = true;
                for (int i = 0; i < journey.Members.Count && together; ++i)
                    for (int j = i + 1; j < journey.Members.Count; ++j)
                    {
                        var a = journey.Members[i].Agent; var b = journey.Members[j].Agent;
                        if (Squared(a.X - b.X, a.Z - b.Z) > 2.8f * 2.8f) { together = false; break; }
                    }
                // The frontmost friend keeps yielding beyond the portal until the
                // entire party has arrived; do not end a visibly separated group trip.
                if (!together) continue;
                foreach (var w in journey.Members)
                {
                    var a = w.Agent;
                    a.Active = false; a.Speed = 0; a.VelocityX = a.VelocityZ = 0;
                    a.Moving = false; a.State = "Inactive"; a.Conversation = a.Looking = false;
                    DespawnedPedestrians++;
                }
                journey.ArrivalAt = (float)Time + 2f + NextFloat() * 10f;
                PlanJourney(journey, false);
            }
        }
        static float Squared(float x, float z) => x * x + z * z;
        static float Distance(Point a, Point b) => Length(a.X - b.X, a.Z - b.Z);
        static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
    }
}
