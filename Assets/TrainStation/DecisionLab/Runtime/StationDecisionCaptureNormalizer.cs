using System;
using System.Collections.Generic;
using System.IO;
using J = StationFlow.TrainStation.DecisionLab.StationDecisionJsonObject;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Worker-owned normalized capture encoder. Pose/phase observations and numeric spellings are unchanged.</summary>
    public sealed class StationDecisionCaptureNormalizer
    {
        public const string CaptureSchema = "stationflow.station_capture.v4", StorageLayout = "per_agent.v3",
            RecordEncoding = "references-and-state-revisions.v1", Schema = "stationflow.normalization.v1";
        public const int CheckpointInterval = 256;
        public static readonly Dictionary<string, string> JourneyAliases = new Dictionary<string, string>(StringComparer.Ordinal) {
            {"journeyId","agentId"},{"serviceId","serviceId"},{"groupId","groupId"},{"entranceId","entranceId"},{"ticketId","ticketId"},
            {"representedPassengers","representedPassengers"},{"trackNumber","trackNumber"},{"groupMemberIndex","groupMemberIndex"},{"groupSize","groupSize"},
            {"coachNumber","coachNumber"},{"seatNumbers","seatNumbers"},{"plannedEntrySeconds","plannedEntrySeconds"},{"publishedDepartureSeconds","publishedDepartureSeconds"},
            {"leadSeconds","leadSeconds"},{"walkSpeed","walkSpeed"},{"fastWalkSpeed","fastWalkSpeed"},{"familiarity","familiarity"},{"safetyMarginSeconds","safetyMargin"},
            {"mobilityProfile","mobilityProfile"},{"clippedAtSimulationStart","clippedAtSimulationStart"},{"needs","needs"},{"alightingServiceVisitor","alightingServiceVisitor"},
            {"alightingServiceVisitLimit","alightingServiceVisitLimit"},{"alightingStopoverSeconds","alightingStopoverSeconds"} };
        readonly Action<string, string> appendRoot, writeRoot, writeProfile;
        readonly Func<string, string, string, long> appendAgent;
        readonly Func<string, string> agentDirectory;
        readonly Dictionary<string, Definition> definitions = new Dictionary<string, Definition>(StringComparer.Ordinal);
        readonly Dictionary<string, string> profiles = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, long> policies = new Dictionary<string, long>(StringComparer.Ordinal);
        readonly Dictionary<(string agent, string stream), State> states = new Dictionary<(string agent, string stream), State>();
        string lastRoster; long rosterId, policyId, lastClock;
        public StationDecisionCaptureNormalizer(string runId, Action<string, string> appendRoot, Action<string, string> writeRoot,
            Func<string, string, string, long> appendAgent, Action<string, string> writeProfile, Func<string, string> agentDirectory)
        {
            this.appendRoot = appendRoot; this.writeRoot = writeRoot; this.appendAgent = appendAgent;
            this.writeProfile = writeProfile; this.agentDirectory = agentDirectory;
            var aliases = new J(); foreach (var pair in JourneyAliases) aliases[pair.Key] = J.Quote(pair.Value);
            var header = new J { ["schema"] = J.Quote(Schema), ["captureSchema"] = J.Quote(CaptureSchema), ["storageLayout"] = J.Quote(StorageLayout),
                ["recordEncoding"] = J.Quote(RecordEncoding), ["runId"] = J.Quote(runId), ["clockFile"] = J.Quote("sync.jsonl"),
                ["definitionsFile"] = J.Quote("record-definitions.jsonl"), ["rostersFile"] = J.Quote("rosters.jsonl"),
                ["checkpointIndexFile"] = J.Quote("state-checkpoints.jsonl"), ["checkpointInterval"] = J.Number(CheckpointInterval),
                ["profileJourneyAliases"] = aliases.ToString() };
            writeRoot("normalization.json", header.ToString());
        }
        internal void RequireClockReference(long syncSampleId)
        { if (syncSampleId < 1 || syncSampleId > lastClock) throw new IOException("Skeleton record references an unwritten capture clock."); }
        Definition Define(string type, string defaults)
        {
            if (definitions.TryGetValue(type, out var existing)) return existing;
            if (definitions.Count >= 1024) throw new IOException("Capture record definition limit exceeded.");
            var values = J.ParseCompact(defaults);
            var definition = new Definition { id = definitions.Count + 1, defaults = values, stampField = values.Has("captureStamp") ? "captureStamp" : values.Has("stamp") ? "stamp" : "" };
            definitions.Add(type, definition);
            appendRoot("record-definitions.jsonl", new J { ["definitionId"] = J.Number(definition.id), ["type"] = J.Quote(type),
                ["stampField"] = J.Quote(definition.stampField), ["defaults"] = values.ToString() }.ToString());
            return definition;
        }
        J Reduce(J source, Definition definition, string agentId = null, bool retainClock = false)
        {
            var result = source.Clone();
            if (definition.stampField.Length > 0 && result.TryGet(definition.stampField, out var stamp) && stamp != "null")
            {
                long id = J.Parse(stamp).Long("syncSampleId"); if (id < 1 || id > lastClock && !retainClock) throw new IOException("Data references an unwritten capture clock.");
                if (!retainClock) { result.Remove(definition.stampField); result["syncSampleId"] = J.Number(id); }
            }
            if (agentId != null && result.TryGet("agentId", out var identity))
            { if (J.Unquote(identity) != agentId) throw new IOException("Cross-agent normalized record."); result.Remove("agentId"); }
            foreach (string key in source.Keys)
            {
                if (key == definition.stampField || key == "agentId" && agentId != null) continue;
                if (result.TryGet(key, out var value) && definition.defaults.TryGet(key, out var fallback) && value == fallback) result.Remove(key);
            }
            result["definitionId"] = J.Number(definition.id); return result;
        }
        void Membership(J source, J result)
        {
            if (!source.TryGet("activeJourneyIds", out var ids) || ids == "null") return;
            string kind = source.TryGet("kind", out var rawKind) && rawKind != "null" ? source.Text("kind") : "";
            if (kind != "avatar_snapshot" && kind != "trajectory_sample" && J.Array(ids).Count == 0) return;
            if (ids != lastRoster)
            { lastRoster = ids; ++rosterId; appendRoot("rosters.jsonl", new J { ["rosterId"] = J.Number(rosterId), ["agentIds"] = ids }.ToString()); }
            result.Remove("activeJourneyIds"); result["rosterId"] = J.Number(rosterId);
        }
        public void WriteRoot(string name, string json, string type, string defaults)
        {
            var original = J.ParseCompact(json); var definition = Define(type, defaults);
            bool clock = name == "sync.jsonl" && original.TryGet("kind", out var rawKind) && rawKind != "null" && original.Text("kind") == "clock";
            if (clock)
            { long id = J.Parse(original["stamp"]).Long("syncSampleId"); if (id <= lastClock) throw new IOException("Clock IDs must be strictly increasing."); lastClock = id; }
            var result = Reduce(original, definition, retainClock: clock); if (clock) result["kind"] = J.Quote("clock"); Membership(original, result);
            if (name == "scenario.json" && original.TryGet("services", out var services) && services != "null" && J.Array(services).Count > 0)
            { result.Remove("services"); result["servicesFile"] = J.Quote("timetable.json"); }
            if (name.EndsWith(".jsonl", StringComparison.Ordinal)) appendRoot(name, result.ToString()); else writeRoot(name, result.ToString());
        }
        public void WriteAgent(string agentId, string name, string json, string type, string defaults, bool profile)
        {
            var row = J.ParseCompact(json);
            if (profile)
            {
                var definition = Define(type, defaults); var result = Reduce(row, definition);
                var values = J.Parse(row["profile"]); profiles[agentId] = values.ToString();
                if (values.TryGet("policyParameters", out var policy) && policy != "null")
                {
                    if (!policies.TryGetValue(policy, out long id))
                    { if (policies.Count >= 65536) throw new IOException("Policy definition limit exceeded."); id = ++policyId; policies.Add(policy, id); appendRoot("record-definitions.jsonl", new J { ["policyId"] = J.Number(id), ["policyParameters"] = policy }.ToString()); }
                    values.Remove("policyParameters"); values["policyId"] = J.Number(id);
                }
                result["profile"] = values.ToString();
                if (row.TryGet("journey", out var journey) && journey != "null")
                {
                    var plan = J.Parse(journey); var originalProfile = J.Parse(row["profile"]);
                    foreach (var alias in JourneyAliases)
                        if (plan.TryGet(alias.Key, out var value) && originalProfile.TryGet(alias.Value, out var other) && value == other) plan.Remove(alias.Key);
                    result["journey"] = plan.ToString();
                }
                writeProfile(agentId, result.ToString()); return;
            }
            if (name == "trajectories.jsonl" || name == "animations.jsonl") { WriteDense(agentId, name, row); return; }
            var typed = Define(type, defaults); var payload = Reduce(row, typed, agentId); Membership(row, payload);
            if (row.TryGet("profile", out var actualProfile) && profiles.TryGetValue(agentId, out var canonical) && actualProfile == canonical)
            { payload.Remove("profile"); payload["profileRef"] = J.Quote("profile.json#profile"); }
            appendAgent(agentId, name, payload.ToString());
        }
        void WriteDense(string agent, string name, J row)
        {
            string stream = name == "trajectories.jsonl" ? "trajectories" : "animations"; var key = (agent, stream);
            if (!states.TryGetValue(key, out var state))
            { if (states.Count >= 262144) throw new IOException("Normalized agent state limit exceeded."); states.Add(key, state = new State()); }
            if (row.Text("agentId") != agent) throw new IOException("Cross-agent dense capture record.");
            long sid = J.Parse(row["captureStamp"]).Long("syncSampleId"); if (sid < 1 || sid > lastClock) throw new IOException("Dense record references an unwritten clock.");
            var dense = new J { ["syncSampleId"] = J.Number(sid) }; var current = new J();
            foreach (string field in row.Keys)
            {
                if (field == "agentId" || field == "captureStamp") continue;
                bool perFrame = field == "time" || field == "sampleId" || (stream == "trajectories"
                    ? field == "position" || field == "rotation" || field == "velocity" || field == "budget" || field == "seatedSecondsRemaining"
                    : field == "normalizedTime" || field == "nextNormalizedTime" || field == "transitionProgress" || field == "nativePlanarSpeed" || field == "locomotionStrideWeight");
                if (perFrame) dense[field] = row[field]; else current[field] = row[field];
            }
            ++state.observations; bool checkpoint = (state.observations - 1) % CheckpointInterval == 0;
            if (state.values != null) { if (state.values.Count != current.Count) throw new IOException("Dense state field set changed inside one schema."); foreach (string field in state.values.Keys) if (!current.Has(field)) throw new IOException("Dense state field removed without a schema change."); }
            J changed = null; foreach (string field in current.Keys)
                if (state.values == null || !state.values.TryGet(field, out var old) || current[field] != old)
                { if (changed == null) changed = new J(); changed[field] = current[field]; }
            long stateOffset = -1;
            if (checkpoint || changed != null)
            {
                ++state.id; var revision = new J { ["stream"] = J.Quote(stream), ["stateId"] = J.Number(state.id),
                    ["fromSyncSampleId"] = J.Number(sid), ["observationIndex"] = J.Number(state.observations), ["checkpoint"] = checkpoint ? "true" : "false" };
                revision[checkpoint ? "values" : "changes"] = (checkpoint ? current : changed).ToString();
                stateOffset = appendAgent(agent, "state-changes.jsonl", revision.ToString());
            }
            state.values = current; dense["stateId"] = J.Number(state.id); long denseOffset = appendAgent(agent, name, dense.ToString());
            if (checkpoint)
            {
                string path = agentDirectory(agent);
                appendRoot("state-checkpoints.jsonl", new J { ["agentId"] = J.Quote(agent), ["stream"] = J.Quote(stream),
                    ["stateId"] = J.Number(state.id), ["fromSyncSampleId"] = J.Number(sid), ["observationIndex"] = J.Number(state.observations),
                    ["path"] = J.Quote(path + "/state-changes.jsonl"), ["byteOffset"] = J.Number(stateOffset),
                    ["densePath"] = J.Quote(path + "/" + name), ["denseByteOffset"] = J.Number(denseOffset) }.ToString());
            }
        }
        sealed class State { public long id, observations; public J values; }
        sealed class Definition { public long id; public J defaults; public string stampField; }
    }
}
