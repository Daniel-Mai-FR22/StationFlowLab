using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StationFlow.TrainStation.Social
{
    /// <summary>Shared spatial queries over saved actors. No people or destinations are spawned here.</summary>
    public static class StationCrowdSpace
    {
        const float CellSize = 2f;
        static readonly List<StationActorMotion> actors = new List<StationActorMotion>();
        static readonly Dictionary<Vector2Int, List<StationActorMotion>> cells = new Dictionary<Vector2Int, List<StationActorMotion>>();
        static readonly Stack<List<StationActorMotion>> pool = new Stack<List<StationActorMotion>>();
        static readonly List<StationActorMotion> query = new List<StationActorMotion>(32);
        static readonly Dictionary<StationActorMotion, int> registrationOrder = new Dictionary<StationActorMotion, int>();
        static int nextOrder;
        static float largestRadius = .32f;
        static int gridFrame = -1;
        public static IReadOnlyList<StationActorMotion> Actors => actors;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() { actors.Clear(); cells.Clear(); pool.Clear(); query.Clear(); registrationOrder.Clear(); nextOrder = 0; gridFrame = -1; }
        public static void Register(StationActorMotion actor)
        { if (actor && !registrationOrder.ContainsKey(actor)) { actors.Add(actor); registrationOrder.Add(actor, nextOrder++); gridFrame = -1; } }
        public static void Unregister(StationActorMotion actor)
        { actors.Remove(actor); registrationOrder.Remove(actor); gridFrame = -1; }
        public static int CompareOrder(StationActorMotion a, StationActorMotion b)
        {
            int names = string.CompareOrdinal(a.name, b.name);
            if (names != 0) return names;
            return registrationOrder[a].CompareTo(registrationOrder[b]);
        }
        static Vector2Int Cell(Vector3 p) => new Vector2Int(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));
        static void Refresh()
        {
            if (gridFrame == Time.frameCount) return;
            gridFrame = Time.frameCount;
            foreach (var bucket in cells.Values) { bucket.Clear(); pool.Push(bucket); }
            cells.Clear();
            largestRadius = .32f;
            foreach (var actor in actors)
            {
                if (!actor || !actor.isActiveAndEnabled) continue;
                if (actor.agent) largestRadius = Mathf.Max(largestRadius, actor.agent.radius);
                var key = Cell(actor.transform.position);
                if (!cells.TryGetValue(key, out var bucket))
                { bucket = pool.Count > 0 ? pool.Pop() : new List<StationActorMotion>(12); cells.Add(key, bucket); }
                bucket.Add(actor);
            }
        }
        public static void GetNeighbours(Vector3 position, float radius, List<StationActorMotion> result)
        {
            Refresh(); result.Clear();
            var low = Cell(position - new Vector3(radius, 0, radius));
            var high = Cell(position + new Vector3(radius, 0, radius));
            float squared = radius * radius;
            for (int x = low.x; x <= high.x; x++) for (int z = low.y; z <= high.y; z++)
            {
                if (!cells.TryGetValue(new Vector2Int(x, z), out var bucket)) continue;
                foreach (var actor in bucket)
                {
                    if (!actor) continue;
                    var delta = actor.transform.position - position;
                    if (Mathf.Abs(delta.y) > 1f) continue;
                    delta.y = 0;
                    if (delta.sqrMagnitude <= squared) result.Add(actor);
                }
            }
        }
        public static bool IsPositionAvailable(Vector3 position, StationActorMotion actor, float clearance = .72f)
        {
            Refresh();
            float ownRadius = actor && actor.agent ? actor.agent.radius : .32f;
            GetNeighbours(position, Mathf.Max(clearance, ownRadius + largestRadius + .06f), query);
            foreach (var other in query)
            {
                if (other == actor) continue;
                float body = actor && actor.agent && other.agent ? actor.agent.radius + other.agent.radius + .06f : .70f;
                var delta = position - other.transform.position; delta.y = 0;
                if (delta.sqrMagnitude < Mathf.Pow(Mathf.Max(clearance, body), 2)) return false;
            }
            return true;
        }
        public static bool TryFindStandingPosition(Vector3 center, StationActorMotion actor, float radius, out Vector3 position)
        {
            position = center;
            if (!actor || !actor.agent) return false;
            int mask = actor.agent.areaMask;
            // Name-derived phase keeps selection deterministic without aligning all travellers to one spoke.
            uint hash = 2166136261;
            foreach (char c in actor.name) hash = (hash ^ c) * 16777619;
            float phase = (hash % 360) * Mathf.Deg2Rad;
            for (int i = 0; i < 25; i++)
            {
                float r = i == 0 ? 0 : radius * Mathf.Sqrt(i / 24f);
                float angle = phase + i * 2.39996323f;
                var candidate = center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * r;
                if (!NavMesh.SamplePosition(candidate, out var hit, .18f, mask) || Mathf.Abs(hit.position.y - center.y) > .2f) continue;
                if (!IsPositionAvailable(hit.position, actor) || !actor.CanReach(hit.position)) continue;
                position = hit.position; return true;
            }
            return false;
        }
    }
}
