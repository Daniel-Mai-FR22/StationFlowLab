using System;
using System.Collections.Generic;
using System.Linq;
using StationFlow.SyntheticTraffic;

internal static class Program
{
    private static int assertions;
    private struct Snapshot
    {
        public int Id;
        public bool Active;
        public float X, Z, Speed, Heading, VX, VZ;
        public Snapshot(SimAgent a)
        { Id = a.Id; Active = a.Active; X = a.X; Z = a.Z; Speed = a.Speed; Heading = a.HeadingDegrees; VX = a.VelocityX; VZ = a.VelocityZ; }
    }

    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void Main(string[] args)
    {
        int failed = 0, ran = 0;
        void Case(string name, Action action)
        {
            if (args.Length > 0 && !args.Contains(name)) return;
            ran++;
            try { action(); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
        }
        Case("geometry", InvalidInputAndGeometry);
        Case("motion", JourneySafetyAndMotion);
        Case("busy-motion", () => JourneySafetyAndMotion(96, 24));
        Case("crowd", CrowdBehaviourAndEncounterCoverage);
        Case("cohesion", SignalSplitGroupCohesion);
        Case("obstacles", StaticObstacleAvoidance);
        Case("replay", DeterministicReplay);
        Case("lifecycle", OffscreenLifetimeContinuity);
        Case("visible", FullyVisibleActorsAreNotRecycled);
        Case("wide", WideObservationCoverage);
        Case("seeds", SeedDensityAndTimestepCoverage);
        if (args.Contains("busy")) Case("busy", () => SeedDensityAndTimestepCoverage(true));
        if (ran == 0) { Console.Error.WriteLine("Unknown case. Choose geometry, motion, crowd, cohesion, obstacles, replay, lifecycle, visible, wide, seeds, busy, busy-motion."); failed++; }
        Console.WriteLine((failed == 0 ? "PASS: " : "FAIL: ") + assertions + " invariant assertions, " + ran + " cases, " + failed + " failures.");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }

    private static void InvalidInputAndGeometry()
    {
        var sim = new TrafficSimulation();
        foreach (float dt in new[] { -1f, float.NaN, float.PositiveInfinity, 11f })
        {
            bool rejected = false;
            try { sim.Step(dt); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "Invalid timestep accepted");
        }
        foreach (var population in new[] { (-1, 1), (1, -1), (161, 1), (1, 161) })
        {
            bool rejected = false;
            try { new TrafficSimulation(42, population.Item1, population.Item2); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "Invalid population accepted");
        }
        sim.Step(0f);
        Assert(sim.Tick == 0 && sim.Time == 0, "Zero timestep must be a no-op");
        Assert(sim.Agents.Count == 36, "Default pool size");
        var a = new SimAgent { Id = 1, Kind = AgentKind.Vehicle, Route = 0, X = 0, Z = 0, Length = 4.5f, Width = 1.8f };
        var b = new SimAgent { Id = 2, Kind = AgentKind.Pedestrian, X = .8f, Z = 1f };
        Assert(TrafficSimulation.BodiesOverlap(a, b), "Vehicle-pedestrian contact missed");
        b.X = 1.5f;
        Assert(!TrafficSimulation.BodiesOverlap(a, b), "Separated pedestrian overlaps a car");
        var c = new SimAgent { Kind = AgentKind.Vehicle, Route = 2, HeadingDegrees = 90, X = 0, Z = 0, Length = 4.5f, Width = 1.8f };
        Assert(TrafficSimulation.BodiesOverlap(a, c), "Orthogonal vehicle contact missed");
        c.Z = 5f;
        Assert(!TrafficSimulation.BodiesOverlap(a, c), "Separated vehicles overlap");
        a.Kind = AgentKind.Pedestrian; b.X = .2f; b.Z = 0;
        Assert(TrafficSimulation.BodiesOverlap(a, b), "Pedestrian contact missed");
        a.Kind = AgentKind.Vehicle; a.HeadingDegrees = 45;
        c.HeadingDegrees = 45; c.X = 1.65f; c.Z = -1.65f;
        Assert(!TrafficSimulation.BodiesOverlap(a, c), "Parallel rotated cars with overlapping AABBs reported as contact");
        c.X = 1.1f; c.Z = -1.1f;
        Assert(TrafficSimulation.BodiesOverlap(a, c), "Rotated car side contact missed");
        b.X = 1.4f; b.Z = 1.4f;
        Assert(TrafficSimulation.BodiesOverlap(a, b), "Pedestrian at diagonal bonnet contact missed");
        b.X = 1.4f; b.Z = -1.4f;
        Assert(!TrafficSimulation.BodiesOverlap(a, b), "Pedestrian outside diagonal car overlaps");
    }

    private static void JourneySafetyAndMotion() => JourneySafetyAndMotion(40, 20);

    private static void JourneySafetyAndMotion(int pedestrianCount, int vehicleCount)
    {
        var sim = new TrafficSimulation(42, pedestrianCount, vehicleCount);
        var phases = new HashSet<SignalPhase>();
        var previous = new Dictionary<int, Snapshot>();
        bool carWaited = false, pedestrianWaited = false, brakes = false, following = false;
        var turningArms = new HashSet<int>();
        float maxPedAcceleration = 0, maxPedTurnRate = 0, maxCarTangentError = 0;
        const float dt = 1f / 60f;
        for (int frame = 0; frame < 600 * 60; frame++)
        {
            previous.Clear();
            foreach (var a in sim.Agents) previous[a.EntityId] = new Snapshot(a);
            sim.Step(dt); phases.Add(sim.Phase);
            foreach (var a in sim.Agents)
            {
                if (!a.Active) continue;
                Assert(float.IsFinite(a.X) && float.IsFinite(a.Z) && float.IsFinite(a.Speed) && float.IsFinite(a.HeadingDegrees), "Nonfinite motion state");
                Assert(a.Speed >= 0 && a.Speed < (a.Kind == AgentKind.Vehicle ? 12f : 2.5f), "Implausible speed " + a.Id + ": " + a.Speed);
                Assert(a.HeadingDegrees >= 0 && a.HeadingDegrees < 360, "Heading outside [0,360)");
                var old = previous[a.EntityId];
                if (old.Active && old.Id == a.Id)
                {
                    float distance = Distance(a.X, a.Z, old.X, old.Z);
                    Assert(distance < (a.Kind == AgentKind.Vehicle ? 12f : 2.5f) * dt + .002f,
                        "Same tracking ID teleported: id=" + a.Id + " distance=" + distance + " t=" + sim.Time);
                    if (a.Kind == AgentKind.Vehicle)
                    {
                        float acceleration = (a.Speed - old.Speed) / dt;
                        Assert(acceleration <= 1.92f && acceleration >= -3.62f, "Vehicle acceleration outside comfortable bounds: " + acceleration);
                        float oldFront = TravelCoordinate(a.Route, old.X, old.Z) + a.Length * .5f;
                        float newFront = TravelCoordinate(a.Route, a.X, a.Z) + a.Length * .5f;
                        if (a.Progress < TrafficSimulation.TurnStart && oldFront <= TrafficSimulation.StopLine && newFront > TrafficSimulation.StopLine + .001f)
                            Assert(sim.IsVehicleGreen(a.Route), "Independent vehicle stop-line audit: entered on red");
                    }
                    else
                    {
                        float acceleration = Distance(a.VelocityX, a.VelocityZ, old.VX, old.VZ) / dt;
                        maxPedAcceleration = Math.Max(maxPedAcceleration, acceleration);
                        if (acceleration >= 5.1f)
                        {
                            string nearby = string.Join("; ", sim.Agents.Where(b => b.Active && b.Id != a.Id && Distance(a.X, a.Z, b.X, b.Z) < 2f)
                                .Select(b => "id=" + b.Id + " pos=" + b.X + "," + b.Z + " velocity=" + b.VelocityX + "," + b.VelocityZ + " group=" + b.GroupId + " state=" + b.State));
                            Assert(false, "Pedestrian velocity changed abruptly: id=" + a.Id + " acceleration=" + acceleration + " t=" + sim.Time +
                                " oldPos=" + old.X + "," + old.Z + " newPos=" + a.X + "," + a.Z + " oldVelocity=" + old.VX + "," + old.VZ + " newVelocity=" + a.VelocityX + "," + a.VelocityZ + " state=" + a.State + " target=" + a.NavigationTargetX + "," + a.NavigationTargetZ + " nearby: " + nearby);
                        }
                        else Assert(true, "Smooth pedestrian acceleration");
                        if (a.Speed > .35f && old.Speed > .35f)
                        {
                            float turnRate = AngleDistance(a.HeadingDegrees, old.Heading) / dt;
                            maxPedTurnRate = Math.Max(maxPedTurnRate, turnRate);
                            Assert(turnRate < 301f, "Moving pedestrian snapped heading: " + turnRate);
                        }
                        if (!InRoad(old.X, old.Z) && InRoad(a.X, a.Z))
                            Assert(sim.IsPedestrianWalk, "Independent pedestrian curb audit: entered road on red at " + a.X + "," + a.Z);
                    }
                }
                if (a.Kind == AgentKind.Vehicle)
                {
                    carWaited |= a.State == "Waiting" && a.Speed < .05f;
                    brakes |= a.BrakeLights && a.Speed > .1f;
                    following |= a.State == "Following";
                    if (a.TurnIntent == 0 || a.Progress < TrafficSimulation.TurnStart)
                    {
                        float lateral = a.Route < 2 ? a.X : a.Z;
                        Assert(Math.Abs(Math.Abs(lateral) - 3.2f) < .001f, "Approaching vehicle left the right-hand lane");
                    }
                    if (a.State == "Turning")
                    {
                        turningArms.Add(a.Route);
                        Assert(a.Speed <= 4.201f && a.IndicatorRight, "Right turn corner speed/indicator invalid");
                        if (old.Active && old.Id == a.Id && a.Speed > .2f)
                        {
                            float tangent = (float)(Math.Atan2(a.X - old.X, a.Z - old.Z) * 180 / Math.PI);
                            float error = AngleDistance(tangent, a.HeadingDegrees);
                            maxCarTangentError = Math.Max(maxCarTangentError, error);
                            Assert(error < 3f, "Car heading is not tangent to right turn");
                        }
                    }
                }
                else
                {
                    pedestrianWaited |= a.State == "Waiting" && a.Speed < .1f;
                    if (InRoad(a.X, a.Z))
                    {
                        Assert(OnCrosswalk(a.X, a.Z), "Pedestrian left the pavement away from a crossing: " + a.X + "," + a.Z);
                        Assert(sim.Phase == SignalPhase.PedestrianWalk || sim.Phase == SignalPhase.PedestrianClearance, "Pedestrian remained on roadway during vehicle phase");
                    }
                }
            }
            Assert(sim.Collisions == 0 && sim.TotalRedLightViolations == 0,
                "Contact/red violation at t=" + sim.Time + ": contacts=" + sim.Collisions + ", red=" + sim.TotalRedLightViolations);
        }
        Assert(phases.Count == Enum.GetValues(typeof(SignalPhase)).Length, "A signal phase was never reached");
        Assert(carWaited && pedestrianWaited && brakes && following, "Waiting/braking/following behavior not exercised");
        Assert(turningArms.Count == 4 && sim.CompletedRightTurns >= 20, "Right-turn throughput did not cover four incoming arms");
        Assert(sim.CompletedVehicleTrips >= 80 && sim.CompletedPedestrianTrips >= 40 && sim.CompletedPedestrianCrossings >= 30,
            "Journeys deadlocked: cars=" + sim.CompletedVehicleTrips + ", pedestrian trips=" + sim.CompletedPedestrianTrips + ", crossings=" + sim.CompletedPedestrianCrossings);
        Assert(sim.CompletedCrossings == sim.CompletedPedestrianCrossings + sim.CompletedVehicleTrips, "Crossing metric accounting differs");
        Console.WriteLine("600 s motion/safety density=" + pedestrianCount + "/" + vehicleCount + ": vehicles=" + sim.CompletedVehicleTrips + ", pedestrian trips=" + sim.CompletedPedestrianTrips + ", crossings=" + sim.CompletedPedestrianCrossings +
            ", right turns=" + sim.CompletedRightTurns + ", contacts=" + sim.Collisions + ", red=" + sim.TotalRedLightViolations + ", maximum pedestrian acceleration=" + maxPedAcceleration.ToString("F2") +
            " m/s2, turn rate=" + maxPedTurnRate.ToString("F1") + " deg/s, car tangent error=" + maxCarTangentError.ToString("F2") + " deg.");
    }

    private sealed class Encounter
    {
        public int A, B;
        public float StartedAt, AxisX, AxisZ, InitialDelta;
        public bool Passed;
    }

    private static void CrowdBehaviourAndEncounterCoverage()
    {
        var sim = new TrafficSimulation(2026, 56, 20);
        var intents = new HashSet<string>();
        var sourcePortals = new HashSet<int>();
        var sinkPortals = new HashSet<int>();
        var pairs = new Dictionary<long, Encounter>();
        var pairSpacing = new List<float>();
        var cornerHeadings = new Dictionary<int, float>();
        var cornerTurnsDuringRed = new HashSet<int>();
        var individualSpeeds = new List<float>();
        bool solo = false, pair = false, trio = false, conversation = false, separatedAndRejoined = false;
        var stretchedGroups = new HashSet<int>();
        const float dt = .1f;
        for (int frame = 0; frame < 6000; frame++)
        {
            sim.Step(dt);
            var peds = sim.Agents.Where(a => a.Active && a.Kind == AgentKind.Pedestrian).ToList();
            foreach (var a in peds)
            {
                intents.Add(a.NavigationIntent); sourcePortals.Add(a.ArrivalPortal); sinkPortals.Add(a.DeparturePortal);
                solo |= a.GroupId == 0 && a.GroupSize == 1;
                pair |= a.GroupSize == 2; trio |= a.GroupSize == 3;
                conversation |= a.IsConversing && a.Speed > .4f && a.GroupSize > 1;
                Assert(a.GroupSlot >= 0 && a.GroupSlot < a.GroupSize, "Group slot outside group size");
                Assert(a.IsGroupLeader == (a.GroupSize > 1 && a.GroupSlot == 0), "Group leadership metadata inconsistent");
                if (frame % 100 == 0) individualSpeeds.Add(a.DesiredSpeed);
                if (a.NavigationIntent == "Corner" && Math.Abs(a.X) < 17 && Math.Abs(a.Z) < 17 && a.Speed > .3f && !sim.IsPedestrianWalk)
                {
                    if (!cornerHeadings.TryGetValue(a.Id, out float first)) cornerHeadings[a.Id] = a.HeadingDegrees;
                    else if (AngleDistance(a.HeadingDegrees, first) > 50) cornerTurnsDuringRed.Add(a.Id);
                }
                if (a.GroupId > 0 && a.GroupSlot == 0)
                {
                    var members = peds.Where(b => b.GroupId == a.GroupId && b.Id != a.Id).ToList();
                    if (members.Count != a.GroupSize - 1 || members.Any(b => b.Speed < .3f) || a.Speed < .3f) continue;
                    float spread = members.Max(b => Distance(a.X, a.Z, b.X, b.Z));
                    pairSpacing.Add(spread);
                    if (spread > 3) stretchedGroups.Add(a.GroupId);
                    if (spread < 2.5f && stretchedGroups.Contains(a.GroupId)) separatedAndRejoined = true;
                }
            }
            for (int i = 0; i < peds.Count; i++) for (int j = i + 1; j < peds.Count; j++)
            {
                var a = peds[i]; var b = peds[j];
                if (a.GroupId > 0 && a.GroupId == b.GroupId) continue;
                float dx = b.X - a.X, dz = b.Z - a.Z;
                float d = Distance(a.X, a.Z, b.X, b.Z);
                Assert(d >= .519f, "Independent pedestrian body-distance audit detected overlap: " + d + " t=" + sim.Time);
                long key = ((long)Math.Min(a.Id, b.Id) << 32) | (uint)Math.Max(a.Id, b.Id);
                if (pairs.TryGetValue(key, out var encounter))
                {
                    float direction = a.Id == encounter.A ? 1 : -1;
                    float delta = direction * (dx * encounter.AxisX + dz * encounter.AxisZ);
                    if (delta < -.8f && d > 1.2f && sim.Time - encounter.StartedAt < 15) encounter.Passed = true;
                }
                else if (d < 4f && d > 1f && a.Speed > .5f && b.Speed > .5f)
                {
                    float dot = (a.VelocityX * b.VelocityX + a.VelocityZ * b.VelocityZ) / (a.Speed * b.Speed);
                    float closing = dx * (b.VelocityX - a.VelocityX) + dz * (b.VelocityZ - a.VelocityZ);
                    float sideDistance = Math.Abs(dx * a.VelocityZ - dz * a.VelocityX) / a.Speed;
                    if (dot < -.5f && closing < -.5f && sideDistance < 1.7f)
                        pairs.Add(key, new Encounter { A = a.Id, B = b.Id, AxisX = a.VelocityX / a.Speed, AxisZ = a.VelocityZ / a.Speed, StartedAt = (float)sim.Time, InitialDelta = d });
                }
            }
        }
        pairSpacing.Sort();
        int passed = pairs.Values.Count(p => p.Passed);
        float p90Spacing = pairSpacing.Count == 0 ? float.PositiveInfinity : pairSpacing[(int)((pairSpacing.Count - 1) * .9f)];
        Assert(intents.Contains("Corner") && intents.Contains("OneCrossing") && intents.Contains("TwoCrossings"), "Origin/destination paths lack corner/single/double crossing choices");
        Assert(sourcePortals.Count >= 6 && sinkPortals.Count >= 6, "Journey portals do not cover the junction approaches");
        Assert(solo && pair && trio && conversation, "Solo/pair/trio/walking conversation diversity missing");
        Assert(individualSpeeds.Max() - individualSpeeds.Min() > .25f, "Everyone has effectively identical preferred speed");
        Assert(cornerTurnsDuringRed.Count >= 4, "Pedestrians did not walk around street corners during red pedestrian phases");
        Assert(pairSpacing.Count >= 500 && p90Spacing < 4.5f, "Walking companions routinely lose cohesion: p90=" + p90Spacing);
        Assert(pairs.Count >= 8 && passed >= 4, "Opposing pedestrians fail to encounter and pass: encounters=" + pairs.Count + ", passed=" + passed);
        Assert(sim.Collisions == 0 && sim.TotalRedLightViolations == 0, "Mixed pedestrian encounter run unsafe");
        Console.WriteLine("600 s crowd diversity: intents=" + string.Join(",", intents) + "; source/sink portals=" + sourcePortals.Count + "/" + sinkPortals.Count +
            "; corner turns on red=" + cornerTurnsDuringRed.Count + "; approaching opposing encounters=" + pairs.Count + ", passed=" + passed + "; group p90 spacing=" + p90Spacing.ToString("F2") +
            " m; observed regroup=" + separatedAndRejoined + "; solo/pair/trio/conversation covered.");
    }

    private static void DeterministicReplay()
    {
        var a = new TrafficSimulation(1234, 40, 20); var b = new TrafficSimulation(1234, 40, 20); var c = new TrafficSimulation(5678, 40, 20);
        Assert(a.Agents[0].DesiredSpeed != c.Agents[0].DesiredSpeed, "Seed does not vary parameters");
        foreach (var s in new[] { a, b }) s.VisibilityGuard = (x, z, radius) => x * x + z * z < (35 + radius) * (35 + radius);
        for (int i = 0; i < 6000; ++i)
        {
            float dt = i % 3 == 0 ? .05f : i % 3 == 1 ? .1f : .025f;
            a.Step(dt); b.Step(dt);
            Assert(a.Phase == b.Phase && a.Time == b.Time && a.Tick == b.Tick, "Replay clock differs");
            for (int j = 0; j < a.Agents.Count; ++j)
            {
                var p = a.Agents[j]; var q = b.Agents[j];
                Assert(p.Id == q.Id && p.EntityId == q.EntityId && p.Active == q.Active && p.X == q.X && p.Z == q.Z && p.Speed == q.Speed && p.HeadingDegrees == q.HeadingDegrees && p.State == q.State &&
                    p.VelocityX == q.VelocityX && p.VelocityZ == q.VelocityZ && p.GroupId == q.GroupId && p.IsConversing == q.IsConversing && p.LookYaw == q.LookYaw && p.ArrivalPortal == q.ArrivalPortal && p.DeparturePortal == q.DeparturePortal,
                    "Same-seed replay diverged at entity=" + p.EntityId + " t=" + a.Time);
            }
        }
        Assert(a.CompletedPedestrianTrips == b.CompletedPedestrianTrips && a.CompletedVehicleTrips == b.CompletedVehicleTrips, "Replay completion metrics differ");
        Console.WriteLine(a.Time.ToString("F1") + " s replay: bit-exact motion, navigation, social state and lifecycle under variable timestep schedule.");
    }

    private static void SignalSplitGroupCohesion()
    {
        float worstSpread = 0, longestSeparation = 0;
        int totalRejoined = 0, totalSplit = 0;
        foreach (int seed in new[] { 42, 2026 })
        {
            var sim = new TrafficSimulation(seed, 96, 24);
            var separatedAt = new Dictionary<int, float>();
            var lastSpread = new Dictionary<int, float>();
            for (int frame = 0; frame < 6000; frame++)
            {
                sim.Step(.1f);
                var groups = sim.Agents.Where(a => a.Active && a.Kind == AgentKind.Pedestrian && a.GroupId > 0).GroupBy(a => a.GroupId).ToList();
                var currentGroups = new HashSet<int>();
                foreach (var group in groups)
                {
                    currentGroups.Add(group.Key);
                    var people = group.ToArray();
                    Assert(people.Length == people[0].GroupSize, "A social group activated or disappeared only partially");
                    float spread = 0;
                    for (int i = 0; i < people.Length; i++) for (int j = i + 1; j < people.Length; j++)
                        spread = Math.Max(spread, Distance(people[i].X, people[i].Z, people[j].X, people[j].Z));
                    worstSpread = Math.Max(worstSpread, spread);
                    lastSpread[group.Key] = spread;
                    // A receiving-plaza wait plus the opposite curb queue can span 25–30m.
                    // This allowance requires an actual regrouping person in the junction;
                    // ordinary outbound walking still uses the tighter 24m ceiling.
                    bool signalSplit = people.Any(a => a.Regrouping && Math.Abs(a.X) < 16 && Math.Abs(a.Z) < 16);
                    float maximumSpread = signalSplit ? 32f : 24f;
                    if (spread >= maximumSpread)
                        Assert(false, "Companions separated: seed=" + seed + " group=" + group.Key + " spread=" + spread + " t=" + sim.Time + " phase=" + sim.Phase + "; " +
                            string.Join("; ", people.Select(a => "id=" + a.Id + " slot=" + a.GroupSlot + " pos=" + a.X + "," + a.Z + " speed=" + a.Speed + " state=" + a.State + " target=" + a.NavigationTargetX + "," + a.NavigationTargetZ)));
                    else Assert(true, "Group stays within crossing and clearance distance");
                    if (spread > 6 && !separatedAt.ContainsKey(group.Key)) { separatedAt[group.Key] = (float)sim.Time; totalSplit++; }
                    if (separatedAt.TryGetValue(group.Key, out float start))
                    {
                        float separatedFor = (float)sim.Time - start;
                        longestSeparation = Math.Max(longestSeparation, separatedFor);
                        if (separatedFor >= 110)
                            Assert(false, "Group did not reunite within a signal cycle plus walking clearance: seed=" + seed + " group=" + group.Key + " duration=" + separatedFor + " spread=" + spread + " t=" + sim.Time + "; " +
                                string.Join("; ", people.Select(a => "id=" + a.Id + " slot=" + a.GroupSlot + " pos=" + a.X + "," + a.Z + " velocity=" + a.VelocityX + "," + a.VelocityZ + " desired=" + a.DesiredSpeed + " state=" + a.State + " target=" + a.NavigationTargetX + "," + a.NavigationTargetZ)));
                        else Assert(true, "Separated companions reunite promptly");
                        if (spread < 3.5f) { separatedAt.Remove(group.Key); totalRejoined++; }
                    }
                }
                foreach (int groupId in separatedAt.Keys)
                    Assert(currentGroups.Contains(groupId), "Group finished a journey while companions remained separated: seed=" + seed + " group=" + groupId + " lastSpread=" + lastSpread[groupId] + " duration=" + ((float)sim.Time - separatedAt[groupId]) + " t=" + sim.Time);
            }
            Assert(sim.Collisions == 0 && sim.TotalRedLightViolations == 0, "Group reunion caused contact or red entry");
        }
        Console.WriteLine("2x600 s signal-split group audit: " + totalSplit + " separations >6m, " + totalRejoined + " reunions; maximum spread=" + worstSpread.ToString("F2") + "m, longest observed separation=" + longestSeparation.ToString("F1") + "s; stopped companions included.");
    }

    private static void StaticObstacleAvoidance()
    {
        var sim = new TrafficSimulation(2026, 12, 0);
        var footprints = new (float x, float z)[] { (-40, -11.2f), (40, -11.2f), (-40, 11.2f), (40, 11.2f), (-11.2f, -40), (11.2f, -40), (-11.2f, 40), (11.2f, 40) };
        foreach (var obstacle in footprints) sim.AddPedestrianObstacle(obstacle.x, obstacle.z, .5f);
        var closeTracks = new HashSet<int>();
        var clearedTracks = new HashSet<int>();
        for (int i = 0; i < 6000; i++)
        {
            sim.Step(.1f);
            foreach (var a in sim.Agents)
            {
                if (!a.Active) continue;
                foreach (var obstacle in footprints)
                {
                    float distance = Distance(a.X, a.Z, obstacle.x, obstacle.z);
                    Assert(distance >= .749f, "Pedestrian cut through a street-furniture footprint: id=" + a.Id + " distance=" + distance);
                    if (distance < 2f && a.Speed > .3f) closeTracks.Add(a.Id);
                }
                if (closeTracks.Contains(a.Id) && Math.Max(Math.Abs(a.X), Math.Abs(a.Z)) > 47) clearedTracks.Add(a.Id);
            }
        }
        Assert(closeTracks.Count >= 6 && clearedTracks.Count >= 4, "Pedestrians did not negotiate and leave added sidewalk obstacles");
        Assert(sim.CompletedPedestrianTrips >= 12 && sim.Collisions == 0, "Added sidewalk obstacles caused contact or journey deadlock");
        Console.WriteLine("600 s furniture avoidance: " + closeTracks.Count + " tracks approached added footprints; " + clearedTracks.Count + " proceeded beyond them without penetration.");
    }

    private static void OffscreenLifetimeContinuity()
    {
        var sim = new TrafficSimulation(42, 40, 20);
        bool Visible(float x, float z, float radius) => x * x + z * z < (42 + radius) * (42 + radius);
        int guardCalls = 0;
        sim.VisibilityGuard = (x, z, radius) => { guardCalls++; Assert(radius > 0, "Visibility callback needs positive body margin"); return Visible(x, z, radius); };
        var prior = sim.Agents.ToDictionary(a => a.EntityId, a => new Snapshot(a));
        var ids = new HashSet<int>(sim.Agents.Select(a => a.Id));
        int newPedestrians = 0, newVehicles = 0, disappearances = 0, incomingVisiblePeds = 0, incomingVisibleCars = 0;
        int highest = ids.Max();
        for (int frame = 0; frame < 600 * 30; frame++)
        {
            int previousHighest = highest;
            sim.Step(1f / 30f);
            var activeIds = new HashSet<int>();
            foreach (var a in sim.Agents)
            {
                var old = prior[a.EntityId];
                float radius = a.Kind == AgentKind.Pedestrian ? 1.2f : 4f;
                if (a.Active) Assert(activeIds.Add(a.Id), "Duplicate active tracking ID");
                if (old.Active && !a.Active)
                { Assert(!Visible(old.X, old.Z, radius), "Actor disappeared while visible"); disappearances++; }
                if (old.Id != a.Id)
                {
                    Assert(a.Id > previousHighest && ids.Add(a.Id), "Track ID reused or nonmonotonic");
                    Assert(!Visible(a.X, a.Z, radius), "New track appeared inside camera observation region");
                    Assert(!old.Active || !Visible(old.X, old.Z, radius), "Visible track recycled in the same frame");
                    highest = Math.Max(highest, a.Id);
                    if (a.Kind == AgentKind.Pedestrian) newPedestrians++; else newVehicles++;
                }
                if (!old.Active && a.Active) Assert(!Visible(a.X, a.Z, radius), "Pooled actor activated while visible");
                if (old.Active && a.Active && old.Id == a.Id)
                {
                    Assert(Distance(a.X, a.Z, old.X, old.Z) < (a.Kind == AgentKind.Vehicle ? 12f : 2.5f) / 30f + .002f, "Same tracking ID teleported");
                    if (!Visible(old.X, old.Z, radius) && Visible(a.X, a.Z, radius))
                    { if (a.Kind == AgentKind.Pedestrian) incomingVisiblePeds++; else incomingVisibleCars++; }
                }
                prior[a.EntityId] = new Snapshot(a);
            }
        }
        Assert(guardCalls > 100 && disappearances > 20 && newPedestrians > 20 && newVehicles > 20, "Offscreen pool lifecycle not exercised");
        Assert(incomingVisiblePeds > 20 && incomingVisibleCars > 20, "Distant agents did not gradually move into view");
        Console.WriteLine("600 s lifecycle: new pedestrian/car tracks=" + newPedestrians + "/" + newVehicles + "; disappearances=" + disappearances +
            "; continuous entries into view=" + incomingVisiblePeds + "/" + incomingVisibleCars + "; all activation/deactivation outside guarded view.");
    }

    private static void FullyVisibleActorsAreNotRecycled()
    {
        var sim = new TrafficSimulation(17, 16, 8);
        sim.VisibilityGuard = (x, z, radius) => true;
        var activeInitial = sim.Agents.Where(a => a.Active).ToDictionary(a => a.EntityId, a => a.Id);
        for (int i = 0; i < 3000; i++)
        {
            sim.Step(.1f);
            foreach (var a in sim.Agents)
                if (activeInitial.TryGetValue(a.EntityId, out int id)) Assert(a.Active && a.Id == id, "Fully observed actor was removed or replaced");
        }
        Assert(sim.CompletedPedestrianTrips > 0 && sim.CompletedVehicleTrips > 0, "Visibility guard fixture never reached journey exits");
        Assert(sim.Collisions == 0, "Visible exit queues collide while recycling is blocked");
        Console.WriteLine("300 s all-visible guard: initial actors retain identities at exits; recycling is deferred.");
    }

    private static void WideObservationCoverage()
    {
        var sim = new TrafficSimulation(42, 96, 24);
        sim.VisibilityGuard = (x, z, radius) => x * x + z * z < (150 + radius) * (150 + radius);
        double localPopulationSum = 0;
        int peakLocalPopulation = 0;
        for (int i = 0; i < 6000; i++)
        {
            sim.Step(.1f);
            int localPopulation = sim.Agents.Count(a => a.Active && a.Kind == AgentKind.Pedestrian && Math.Abs(a.X) < 25 && Math.Abs(a.Z) < 25);
            localPopulationSum += localPopulation;
            peakLocalPopulation = Math.Max(peakLocalPopulation, localPopulation);
        }
        float meanLocalPopulation = (float)(localPopulationSum / 6000);
        Assert(sim.SpawnedPedestrians > 30 && sim.SpawnedVehicles > 30, "Wide camera coverage starves new arrivals beyond the original portals");
        Assert(sim.CompletedPedestrianCrossings > 40 && sim.CompletedVehicleTrips > 60, "Wide camera coverage blocks intersection throughput");
        Assert(meanLocalPopulation > 12, "Wide camera coverage empties the observable junction: mean=" + meanLocalPopulation);
        Assert(sim.Collisions == 0 && sim.TotalRedLightViolations == 0, "Wide observation lifecycle causes contact or red violation");
        Console.WriteLine("600 s 150m observation guard: births p/v=" + sim.SpawnedPedestrians + "/" + sim.SpawnedVehicles + ", crossings/cars=" + sim.CompletedPedestrianCrossings + "/" + sim.CompletedVehicleTrips +
            "; central people mean/peak=" + meanLocalPopulation.ToString("F1") + "/" + peakLocalPopulation + "; zero contacts/red.");
    }

    private static void SeedDensityAndTimestepCoverage() => SeedDensityAndTimestepCoverage(false);

    private static void SeedDensityAndTimestepCoverage(bool busyOnly)
    {
        bool allSafe = true, allProgress = true;
        foreach (var test in new (int seed, int p, int v, float dt, int seconds)[] { (1, 40, 20, .1f, 900), (19, 40, 20, .1f, 900), (999, 40, 20, .1f, 900), (-7, 40, 20, .1f, 900),
            (31415, 1, 1, .25f, 420), (7, 7, 3, .05f, 420), (42, 64, 24, .1f, 900), (2026, 96, 24, .1f, 600) })
        {
            if (busyOnly && test.p != 96) continue;
            var sim = new TrafficSimulation(test.seed, test.p, test.v);
            var initialPedestrianIds = new HashSet<int>(sim.Agents.Where(a => a.Active && a.Kind == AgentKind.Pedestrian).Select(a => a.Id));
            int steps = (int)(test.seconds / test.dt);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            int peakCentralPeople = 0;
            double centralPeopleSum = 0;
            var previousPhase = sim.Phase;
            float phaseDuration = 0, maximumPhaseDuration = 0;
            for (int i = 0; i < steps; i++)
            {
                sim.Step(test.dt);
                phaseDuration = sim.Phase == previousPhase ? phaseDuration + test.dt : 0;
                previousPhase = sim.Phase;
                maximumPhaseDuration = Math.Max(maximumPhaseDuration, phaseDuration);
                if (test.p == 96)
                {
                    int count = sim.Agents.Count(a => a.Active && a.Kind == AgentKind.Pedestrian && Math.Abs(a.X) < 25 && Math.Abs(a.Z) < 25);
                    peakCentralPeople = Math.Max(peakCentralPeople, count); centralPeopleSum += count;
                }
            }
            elapsed.Stop();
            allSafe &= sim.Collisions == 0 && sim.TotalRedLightViolations == 0;
            int unfinishedInitialPedestrians = sim.Agents.Count(a => a.Active && a.Kind == AgentKind.Pedestrian && initialPedestrianIds.Contains(a.Id));
            allProgress &= sim.CompletedVehicleTrips >= test.v && sim.CompletedPedestrianTrips >= test.p && maximumPhaseDuration < 120 && unfinishedInitialPedestrians == 0;
            if (test.p == 96) Assert(peakCentralPeople >= 16 && centralPeopleSum / steps >= 8, "Busy scene remains visibly empty around the junction");
            Console.WriteLine(test.seconds + " s seed=" + test.seed + " density=" + test.p + "/" + test.v + " dt=" + test.dt + ": trips p/v=" + sim.CompletedPedestrianTrips + "/" + sim.CompletedVehicleTrips +
                ", crossings=" + sim.CompletedPedestrianCrossings + ", turns=" + sim.CompletedRightTurns + ", contacts/red=" + sim.Collisions + "/" + sim.TotalRedLightViolations + ", initial pedestrians unfinished=" + unfinishedInitialPedestrians + ", phase=" + sim.Phase + ", longest phase=" + maximumPhaseDuration.ToString("F1") + " s, wall=" + elapsed.Elapsed.TotalSeconds.ToString("F2") + " s" +
                (test.p == 96 ? ", central people mean/peak=" + (centralPeopleSum / steps).ToString("F1") + "/" + peakCentralPeople : ""));
        }
        var empty = new TrafficSimulation(42, 0, 0);
        for (int i = 0; i < 1500; i++) empty.Step(.1f);
        Assert(empty.Time > 149 && empty.Collisions == 0 && empty.CompletedCrossings == 0 && empty.CompletedPedestrianTrips == 0, "Empty scenario failed");
        Assert(allSafe, "At least one seed/density scenario had contacts or red-light violations; see per-scenario output");
        Assert(allProgress, "A seed/density scenario failed trip throughput, starved an initial pedestrian, or held a signal phase for 120 s; see per-scenario output");
    }

    private static bool InRoad(float x, float z) => Math.Abs(x) < 7.05f || Math.Abs(z) < 7.05f;
    private static bool OnCrosswalk(float x, float z) => Math.Abs(x) < 7.1f && Math.Abs(z) >= 8.4f && Math.Abs(z) <= 11.6f || Math.Abs(z) < 7.1f && Math.Abs(x) >= 8.4f && Math.Abs(x) <= 11.6f;
    private static float TravelCoordinate(int route, float x, float z) => route == 0 ? z : route == 1 ? -z : route == 2 ? x : -x;
    private static float Distance(float ax, float az, float bx, float bz) => (float)Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
    private static float AngleDistance(float a, float b) => Math.Abs(((a - b) % 360f + 540f) % 360f - 180f);
}
