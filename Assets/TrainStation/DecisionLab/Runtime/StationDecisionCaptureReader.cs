using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using J = StationFlow.TrainStation.DecisionLab.StationDecisionJsonObject;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Expanded logical view of the new normalized capture format. Older capture formats are intentionally unsupported.</summary>
    public sealed class StationDecisionCaptureReader
    {
        readonly string root, prefix;
        readonly Dictionary<long, Definition> definitions = new Dictionary<long, Definition>();
        readonly Dictionary<long, string> policies = new Dictionary<long, string>(), clocks = new Dictionary<long, string>(), rosters = new Dictionary<long, string>();
        readonly Dictionary<string, string> agents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly J aliases;
        readonly Dictionary<string, SkeletonRig> skeletonRigs = new Dictionary<string, SkeletonRig>(StringComparer.Ordinal);
        readonly Dictionary<string, Dictionary<int, J>> skeletonBindings = new Dictionary<string, Dictionary<int, J>>(StringComparer.OrdinalIgnoreCase);
        readonly bool skeletons;
        static readonly string[] SkeletonJointNames = "Hips LeftUpperLeg RightUpperLeg LeftLowerLeg RightLowerLeg LeftFoot RightFoot Spine Chest Neck Head LeftShoulder RightShoulder LeftUpperArm RightUpperArm LeftLowerArm RightLowerArm LeftHand RightHand LeftToes RightToes LeftEye RightEye Jaw LeftThumbProximal LeftThumbIntermediate LeftThumbDistal LeftIndexProximal LeftIndexIntermediate LeftIndexDistal LeftMiddleProximal LeftMiddleIntermediate LeftMiddleDistal LeftRingProximal LeftRingIntermediate LeftRingDistal LeftLittleProximal LeftLittleIntermediate LeftLittleDistal RightThumbProximal RightThumbIntermediate RightThumbDistal RightIndexProximal RightIndexIntermediate RightIndexDistal RightMiddleProximal RightMiddleIntermediate RightMiddleDistal RightRingProximal RightRingIntermediate RightRingDistal RightLittleProximal RightLittleIntermediate RightLittleDistal UpperChest".Split(' ');
        public StationDecisionCaptureReader(string directory)
        {
            if (!Path.IsPathRooted(directory)) throw new ArgumentException("An absolute capture directory is required.", nameof(directory));
            root = Path.GetFullPath(directory); prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var manifest = J.Parse(ReadRaw("manifest.json")); var header = J.Parse(ReadRaw("normalization.json"));
            if (manifest.Text("schema") != StationDecisionCaptureNormalizer.CaptureSchema || manifest.Text("storageLayout") != StationDecisionCaptureNormalizer.StorageLayout ||
                manifest.Text("recordEncoding") != StationDecisionCaptureNormalizer.RecordEncoding || header.Text("schema") != StationDecisionCaptureNormalizer.Schema ||
                header.Text("recordEncoding") != StationDecisionCaptureNormalizer.RecordEncoding || header.Long("checkpointInterval") != StationDecisionCaptureNormalizer.CheckpointInterval ||
                header.Text("captureSchema") != StationDecisionCaptureNormalizer.CaptureSchema || header.Text("storageLayout") != StationDecisionCaptureNormalizer.StorageLayout ||
                header.Text("clockFile") != "sync.jsonl" || header.Text("definitionsFile") != "record-definitions.jsonl" ||
                header.Text("rostersFile") != "rosters.jsonl" || header.Text("checkpointIndexFile") != "state-checkpoints.jsonl")
                throw new InvalidDataException("Only normalized station_capture.v4 / per_agent.v3 is supported.");
            if (header.Text("runId") != manifest.Text("runId")) throw new InvalidDataException("Normalization run ID differs from manifest.");
            aliases = J.Parse(header["profileJourneyAliases"]);
            foreach (string name in new[] { "record-definitions.jsonl", "rosters.jsonl", "state-checkpoints.jsonl", "sync.jsonl" })
                if (!File.Exists(PathFor(name))) throw new FileNotFoundException("Missing normalized capture metadata.", name);
            foreach (string line in RawLines("record-definitions.jsonl"))
            {
                var row = J.Parse(line);
                if (row.Has("definitionId"))
                { long id = row.Long("definitionId"); if (id < 1 || definitions.ContainsKey(id)) throw new InvalidDataException("Duplicate/invalid record definition."); definitions.Add(id, new Definition { defaults = J.Parse(row["defaults"]), stampField = row.Text("stampField") }); }
                else if (row.Has("policyId"))
                { long id = row.Long("policyId"); if (id < 1 || policies.ContainsKey(id)) throw new InvalidDataException("Duplicate/invalid policy definition."); J.Parse(row["policyParameters"]); policies.Add(id, row["policyParameters"]); }
                else throw new InvalidDataException("Unknown record definition row.");
            }
            foreach (string line in RawLines("rosters.jsonl"))
            { var row = J.Parse(line); long id = row.Long("rosterId"); if (id < 1 || rosters.ContainsKey(id)) throw new InvalidDataException("Duplicate/invalid roster."); foreach (string item in J.Array(row["agentIds"])) J.Unquote(item); rosters.Add(id, row["agentIds"]); }
            long lastClock = 0;
            foreach (string line in RawLines("sync.jsonl"))
            {
                var row = J.Parse(line); if (!row.Has("kind") || row.Text("kind") != "clock") continue;
                var stamp = J.Parse(row["stamp"]); long id = stamp.Long("syncSampleId");
                if (id <= lastClock || stamp.Text("runId") != manifest.Text("runId")) throw new InvalidDataException("Invalid canonical capture clock.");
                clocks.Add(id, row["stamp"]); lastClock = id;
            }
            if (File.Exists(PathFor("agents/index.json")))
            {
                var index = J.Parse(ReadRaw("agents/index.json")); if (index.Text("schema") != "stationflow.agents_index.v1" || index.Text("runId") != manifest.Text("runId")) throw new InvalidDataException("Invalid logical agent index.");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (string item in J.Array(index["agents"]))
                { var row = J.Parse(item); string path = row.Text("directory"), id = row.Text("agentId"); PathFor(path); if (!path.StartsWith("agents/", StringComparison.Ordinal) || path.Split('/').Length != 2 || agents.ContainsKey(path) || !ids.Add(id)) throw new InvalidDataException("Duplicate/invalid logical agent directory."); agents.Add(path, id); }
            }
            ValidateCheckpoints();
            var options = J.Parse(manifest["captureOptions"]); if (!options.TryGet("skeletons", out var enabled) || enabled != "true" && enabled != "false") throw new InvalidDataException("captureOptions.skeletons must be an explicit boolean."); skeletons = enabled == "true";
            LoadSkeletonMetadata(manifest);
        }
        public string ReadJson(string relativePath)
        {
            var value = J.Parse(ReadRaw(relativePath)); if (!value.Has("definitionId")) return value.ToString();
            value = Expand(value, relativePath);
            if (relativePath.Replace('\\', '/').EndsWith("/profile.json", StringComparison.Ordinal))
            {
                var profile = J.Parse(value["profile"]); ExpandPolicy(profile); value["profile"] = profile.ToString();
                if (value.TryGet("journey", out var journey) && journey != "null")
                { var plan = J.Parse(journey); foreach (string key in aliases.Keys) { string target = aliases.Text(key); if (!plan.Has(key) && profile.TryGet(target, out var field)) plan[key] = field; } value["journey"] = plan.ToString(); }
            }
            if (value.TryGet("servicesFile", out var servicesFile))
            { var timetable = J.Parse(ReadJson(J.Unquote(servicesFile))); value["services"] = timetable["services"]; value.Remove("servicesFile"); }
            return value.ToString();
        }
        public IEnumerable<string> ReadLines(string relativePath)
        {
            string path = relativePath.Replace('\\', '/');
            if (path.EndsWith("/skeleton-poses.bin", StringComparison.Ordinal))
            { foreach (string line in SkeletonPoses(path)) yield return line; yield break; }
            if (path.StartsWith("agents/", StringComparison.Ordinal) && (path.EndsWith("/trajectories.jsonl", StringComparison.Ordinal) || path.EndsWith("/animations.jsonl", StringComparison.Ordinal)))
            { foreach (string line in Dense(path)) yield return line; yield break; }
            if (path.StartsWith("video/", StringComparison.Ordinal) && path.EndsWith("/frames.jsonl", StringComparison.Ordinal))
            { foreach (string line in CameraFrames(path)) yield return line; yield break; }
            foreach (string line in RawLines(path))
            { var row = J.Parse(line); if (path == "skeleton-definitions.jsonl" || path.EndsWith("/skeleton-bindings.jsonl", StringComparison.Ordinal) || path.EndsWith("/skeleton-index.jsonl", StringComparison.Ordinal) || path == "record-definitions.jsonl" || path == "rosters.jsonl" || path == "state-checkpoints.jsonl" || path.EndsWith("/state-changes.jsonl", StringComparison.Ordinal) || path.EndsWith("/camera-states.jsonl", StringComparison.Ordinal)) yield return line; else yield return Expand(row, path).ToString(); }
        }
        J Expand(J row, string path)
        {
            J result = row.Clone(); string stampField = "";
            if (row.Has("definitionId"))
            {
                if (!definitions.TryGetValue(row.Long("definitionId"), out var definition)) throw new InvalidDataException("Missing record definition.");
                result = definition.defaults.Clone(); foreach (string key in row.Keys) result[key] = row[key]; result.Remove("definitionId"); stampField = definition.stampField;
            }
            if (row.Has("syncSampleId"))
            { if (stampField.Length == 0) throw new InvalidDataException("Clock reference has no declared field."); result[stampField] = Clock(row.Long("syncSampleId")); result.Remove("syncSampleId"); }
            if (row.Has("rosterId")) { if (!rosters.TryGetValue(row.Long("rosterId"), out var ids)) throw new InvalidDataException("Missing roster revision."); result["activeJourneyIds"] = ids; result.Remove("rosterId"); }
            string agent = AgentFor(path, false); if (agent != null && (result.Has("agentId") || path.EndsWith(".jsonl", StringComparison.Ordinal))) result["agentId"] = J.Quote(agent);
            if (row.Has("profileRef"))
            {
                if (agent == null || row.Text("profileRef") != "profile.json#profile") throw new InvalidDataException("Invalid profile reference.");
                string folder = path.Substring(0, path.LastIndexOf('/')); result["profile"] = J.Parse(ReadJson(folder + "/profile.json"))["profile"]; result.Remove("profileRef");
            }
            return result;
        }
        void ExpandPolicy(J profile)
        { if (!profile.Has("policyId")) return; if (!policies.TryGetValue(profile.Long("policyId"), out var policy)) throw new InvalidDataException("Missing policy definition."); profile.Remove("policyId"); profile["policyParameters"] = policy; }
        string Clock(long id) { if (!clocks.TryGetValue(id, out var stamp)) throw new InvalidDataException("Missing canonical clock sample: " + id); return stamp; }
        string AgentFor(string path, bool required)
        {
            path = path.Replace('\\', '/'); if (!path.StartsWith("agents/", StringComparison.Ordinal) || path.Split('/').Length != 3) { if (required) throw new InvalidDataException("Not an indexed per-agent stream."); return null; }
            string directory = path.Substring(0, path.LastIndexOf('/')); if (!agents.TryGetValue(directory, out var id)) throw new InvalidDataException("Unindexed per-agent directory."); return id;
        }
        IEnumerable<string> Dense(string path)
        {
            string agent = AgentFor(path, true), stream = Path.GetFileNameWithoutExtension(path), statePath = path.Substring(0, path.LastIndexOf('/')) + "/state-changes.jsonl";
            using (var revisions = Revisions(statePath, stream).GetEnumerator())
            {
                bool hasNext = revisions.MoveNext(); J next = hasNext ? revisions.Current : null, state = null; long currentId = 0, observations = 0, lastSid = 0;
                foreach (string line in RawLines(path))
                {
                    var row = J.Parse(line); long sid = row.Long("syncSampleId"), wanted = row.Long("stateId"); ++observations;
                    if (sid <= lastSid || wanted < currentId) throw new InvalidDataException("Dense observation/reference order moved backwards."); lastSid = sid;
                    while (hasNext && next.Long("stateId") <= wanted)
                    {
                        if (next.Long("stateId") != currentId + 1 || next.Long("fromSyncSampleId") != sid || next.Long("observationIndex") != observations) throw new InvalidDataException("Invalid state revision ordering.");
                        bool checkpoint = next["checkpoint"] == "true";
                        if (checkpoint != ((observations - 1) % 256 == 0)) throw new InvalidDataException("Incorrect checkpoint cadence.");
                        if (checkpoint) { var full = J.Parse(next["values"]); if (state != null) { if (full.Count != state.Count) throw new InvalidDataException("Checkpoint state field set changed."); foreach (string field in state.Keys) if (!full.Has(field)) throw new InvalidDataException("Checkpoint state field removed."); } state = full; } else { if (state == null) throw new InvalidDataException("State delta lacks an initial checkpoint."); var changes = J.Parse(next["changes"]); foreach (string key in changes.Keys) { if (!state.Has(key)) throw new InvalidDataException("State delta introduced an undeclared field."); state[key] = changes[key]; } }
                        currentId = next.Long("stateId"); hasNext = revisions.MoveNext(); next = hasNext ? revisions.Current : null;
                    }
                    if (state == null || wanted != currentId || hasNext && next.Long("fromSyncSampleId") <= sid) throw new InvalidDataException("Missing/stale dense state reference.");
                    var result = state.Clone(); foreach (string key in row.Keys) if (key != "stateId" && key != "syncSampleId") result[key] = row[key];
                    result["captureStamp"] = Clock(sid); result["agentId"] = J.Quote(agent); yield return result.ToString();
                }
                if (hasNext) throw new InvalidDataException("Unreferenced state revisions remain.");
            }
        }
        IEnumerable<J> Revisions(string path, string stream)
        { foreach (string line in RawLines(path)) { var row = J.Parse(line); if (row.Text("stream") == stream) yield return row; } }
        IEnumerable<string> CameraFrames(string path)
        {
            string states = path.Substring(0, path.LastIndexOf('/')) + "/camera-states.jsonl";
            using (var iterator = RawLines(states).GetEnumerator())
            {
                bool more = iterator.MoveNext(); J next = more ? J.Parse(iterator.Current) : null, current = null; long id = 0, lastSid = 0;
                foreach (string line in RawLines(path))
                {
                    var row = J.Parse(line); long sid = row.Long("syncSampleId"), wanted = row.Long("cameraStateId"); if (sid <= lastSid) throw new InvalidDataException("Camera capture clock order is invalid."); lastSid = sid;
                    while (more && next.Long("cameraStateId") <= wanted)
                    { if (next.Long("cameraStateId") != id + 1 || next.Long("effectiveFromSyncSampleId") > sid) throw new InvalidDataException("Invalid camera state revision."); current = next; id = next.Long("cameraStateId"); more = iterator.MoveNext(); next = more ? J.Parse(iterator.Current) : null; }
                    if (current == null || id != wanted || more && next.Long("effectiveFromSyncSampleId") <= sid) throw new InvalidDataException("Missing/stale camera state.");
                    row.Remove("syncSampleId"); row.Remove("cameraStateId"); row["stamp"] = Clock(sid); row["camera"] = current["camera"]; yield return row.ToString();
                }
                if (more) throw new InvalidDataException("Unreferenced camera states remain.");
            }
        }
        void ValidateCheckpoints()
        {
            var indexed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in RawLines("state-checkpoints.jsonl"))
            {
                var index = J.Parse(line); string identity = index.Text("agentId") + "\0" + index.Text("stream") + "\0" + index.Long("stateId");
                if (!indexed.Add(identity)) throw new InvalidDataException("Duplicate checkpoint index entry.");
                var state = J.Parse(ReadAt(index.Text("path"), index.Long("byteOffset"))); var dense = J.Parse(ReadAt(index.Text("densePath"), index.Long("denseByteOffset")));
                if (AgentFor(index.Text("path"), true) != index.Text("agentId") || AgentFor(index.Text("densePath"), true) != index.Text("agentId") ||
                    Path.GetFileName(index.Text("path")) != "state-changes.jsonl" || Path.GetFileNameWithoutExtension(index.Text("densePath")) != index.Text("stream") || state["checkpoint"] != "true" || state.Text("stream") != index.Text("stream") ||
                    state.Long("stateId") != index.Long("stateId") || dense.Long("stateId") != index.Long("stateId") ||
                    state.Long("fromSyncSampleId") != index.Long("fromSyncSampleId") || dense.Long("syncSampleId") != index.Long("fromSyncSampleId") || state.Long("observationIndex") != index.Long("observationIndex"))
                    throw new InvalidDataException("Checkpoint byte index does not address its declared state and observation.");
            }
            foreach (var agent in agents)
            {
                string path = agent.Key + "/state-changes.jsonl"; if (!File.Exists(PathFor(path))) continue;
                foreach (string line in RawLines(path))
                {
                    var state = J.Parse(line); if (state["checkpoint"] != "true") continue;
                    string identity = agent.Value + "\0" + state.Text("stream") + "\0" + state.Long("stateId");
                    if (!indexed.Remove(identity)) throw new InvalidDataException("A full state checkpoint lacks its unique byte index.");
                }
            }
            if (indexed.Count != 0) throw new InvalidDataException("Checkpoint index contains unmatched entries.");
        }
        void LoadSkeletonMetadata(J manifest)
        {
            string[] keys = { "skeletonDefinitionsFile", "skeletonPoseFileName", "skeletonBindingsFileName", "skeletonIndexFileName", "skeletonPoseFormat" };
            string[] expected = { "skeleton-definitions.jsonl", "skeleton-poses.bin", "skeleton-bindings.jsonl", "skeleton-index.jsonl", "stationflow.skeleton_pose_binary.v1" };
            for (int i = 0; i < keys.Length; i++)
                if (!manifest.TryGet(keys[i], out var value) || J.Unquote(value) != (skeletons ? expected[i] : "")) throw new InvalidDataException("Skeleton manifest declaration differs from toggle.");
            if (File.Exists(PathFor(expected[0])) != skeletons) throw new InvalidDataException("Skeleton definitions file differs from toggle.");
            if (skeletons) foreach (string line in RawLines(expected[0]))
            {
                var row = J.Parse(line); ExactFields(row, "schema", "rigId", "jointNames", "parentIndices", "transformPaths", "coordinateSystem", "units", "positionEncoding");
                if (row.Text("schema") != "stationflow.skeleton_definition.v1" || row.Text("coordinateSystem") != "UnityWorld" || row.Text("units") != "metres" || row.Text("positionEncoding") != "float32")
                    throw new InvalidDataException("Invalid skeleton definition contract.");
                var names = J.Array(row["jointNames"]); var rawParents = J.Array(row["parentIndices"]); var rawPaths = J.Array(row["transformPaths"]); int count = SkeletonJointNames.Length;
                if (names.Count != count || rawParents.Count != count || rawPaths.Count != count) throw new InvalidDataException("Invalid skeleton joint count.");
                var rig = new SkeletonRig { parents = new int[count], paths = new string[count] }; var paths = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    if (J.Unquote(names[i]) != SkeletonJointNames[i] || !int.TryParse(rawParents[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out rig.parents[i]))
                        throw new InvalidDataException("Skeleton HumanBodyBones order or parent is invalid.");
                    rig.paths[i] = J.Unquote(rawPaths[i]);
                    if (rig.parents[i] < -1 || rig.parents[i] >= count || rig.parents[i] == i || rig.paths[i].IndexOf('\0') >= 0 ||
                        rig.paths[i].Length == 0 && rig.parents[i] != -1 || rig.paths[i].Length > 0 && !paths.Add(rig.paths[i]))
                        throw new InvalidDataException("Invalid skeleton parent or Transform path.");
                }
                for (int i = 0; i < count; i++)
                {
                    var chain = new HashSet<int>(); int parent = i;
                    while (parent >= 0)
                    { if (!chain.Add(parent)) throw new InvalidDataException("Skeleton parent cycle."); if (rig.parents[parent] >= 0 && rig.paths[rig.parents[parent]].Length == 0) throw new InvalidDataException("Missing skeleton parent."); parent = rig.parents[parent]; }
                }
                string id = row.Text("rigId");
                if (id != SkeletonHash(rig) || skeletonRigs.ContainsKey(id)) throw new InvalidDataException("Skeleton rig definition hash differs or is duplicated.");
                skeletonRigs.Add(id, rig);
            }
            foreach (var agent in agents)
            {
                string folder = agent.Key;
                bool bindings = File.Exists(PathFor(folder + "/skeleton-bindings.jsonl")), poses = File.Exists(PathFor(folder + "/skeleton-poses.bin")), index = File.Exists(PathFor(folder + "/skeleton-index.jsonl"));
                if ((bindings || poses || index) && (!skeletons || !(bindings && poses && index))) throw new InvalidDataException("Disabled or incomplete skeleton stream.");
                if (!bindings) continue;
                var revisions = new Dictionary<int, J>(); long previousSync = 0;
                foreach (string line in RawLines(folder + "/skeleton-bindings.jsonl"))
                {
                    var row = J.Parse(line); ExactFields(row, "bindingId", "rigId", "actorSlotId", "avatarName", "effectiveFromSyncSampleId");
                    long id = row.Long("bindingId"), sid = row.Long("effectiveFromSyncSampleId"); Clock(sid);
                    if (id != revisions.Count + 1 || sid <= previousSync || !skeletonRigs.ContainsKey(row.Text("rigId")) || row.Text("actorSlotId").Length == 0) throw new InvalidDataException("Invalid skeleton binding sequence or identity.");
                    row.Text("avatarName"); revisions.Add((int)id, row); previousSync = sid;
                }
                if (revisions.Count == 0) throw new InvalidDataException("Skeleton stream has no bindings.");
                skeletonBindings.Add(folder, revisions);
            }
        }
        static string SkeletonHash(SkeletonRig rig)
        {
            using (var bytes = new MemoryStream())
            {
                using (var output = new BinaryWriter(bytes, new UTF8Encoding(false, true), true))
                {
                    output.Write(Encoding.UTF8.GetBytes("stationflow.skeleton_definition.v1\n")); output.Write(SkeletonJointNames.Length);
                    for (int i = 0; i < SkeletonJointNames.Length; i++) { HashString(output, SkeletonJointNames[i]); output.Write(rig.parents[i]); HashString(output, rig.paths[i]); }
                }
                using (var sha = SHA256.Create()) return "rig-" + BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        static void HashString(BinaryWriter writer, string value) { byte[] bytes = new UTF8Encoding(false, true).GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        static void ExactFields(J row, params string[] fields)
        { if (row.Count != fields.Length) throw new InvalidDataException("Unexpected skeleton metadata fields."); foreach (string field in fields) if (!row.Has(field)) throw new InvalidDataException("Missing skeleton metadata field: " + field); }
        static string FloatNumber(float value)
        {
            if (value == 0f && BitConverter.ToInt32(BitConverter.GetBytes(value), 0) < 0) return "-0.0";
            return ((double)value).ToString("R", CultureInfo.InvariantCulture);
        }
        IEnumerable<string> SkeletonPoses(string path)
        {
            string agent = AgentFor(path, true), folder = path.Substring(0, path.LastIndexOf('/'));
            if (!skeletons || !skeletonBindings.TryGetValue(folder, out var revisions)) throw new InvalidDataException("Unindexed or disabled skeleton stream.");
            long observations = 0, lastSync = 0; int lastSample = 0, activeBinding = 0; float lastTime = -1f;
            using (var checkpoints = RawLines(folder + "/skeleton-index.jsonl").GetEnumerator())
            using (var file = new FileStream(PathFor(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new BinaryReader(file, new UTF8Encoding(false, true)))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "SFLPOSE1" || reader.ReadUInt32() != 1 || reader.ReadUInt32() != 16) throw new InvalidDataException("Invalid skeleton binary header.");
                const int count = 55, maskBytes = 7, recordBytes = 32 + maskBytes + count * 12;
                while (file.Position < file.Length)
                {
                    long offset = file.Position;
                    if (reader.ReadUInt32() != recordBytes || file.Length - offset < recordBytes) throw new InvalidDataException("Invalid/truncated skeleton record byte length.");
                    long sid = reader.ReadInt64(); int sample = reader.ReadInt32(), binding = reader.ReadInt32(), jointCount = reader.ReadInt32(), masks = reader.ReadInt32(); float time = reader.ReadSingle();
                    if (jointCount != count || masks != maskBytes || sid <= lastSync || sample <= lastSample || float.IsNaN(time) || float.IsInfinity(time) || time < lastTime) throw new InvalidDataException("Invalid skeleton record sequence/count/time.");
                    string stamp = Clock(sid); var clock = J.Parse(stamp);
                    if ((float)double.Parse(clock["simulationSeconds"], CultureInfo.InvariantCulture) != time) throw new InvalidDataException("Skeleton time differs from its clock.");
                    if (!revisions.TryGetValue(binding, out var revision)) throw new InvalidDataException("Missing skeleton binding.");
                    if (binding != activeBinding) { if (binding != activeBinding + 1 || revision.Long("effectiveFromSyncSampleId") != sid) throw new InvalidDataException("Skeleton binding activation differs from first pose."); activeBinding = binding; }
                    if (revision.Long("effectiveFromSyncSampleId") > sid || revisions.TryGetValue(binding + 1, out var next) && next.Long("effectiveFromSyncSampleId") <= sid) throw new InvalidDataException("Future/stale skeleton binding.");
                    var rig = skeletonRigs[revision.Text("rigId")]; byte[] mask = reader.ReadBytes(maskBytes); if (mask.Length != maskBytes || (mask[maskBytes - 1] & 128) != 0) throw new InvalidDataException("Invalid skeleton mask unused bits.");
                    var positions = new StringBuilder("["); var valid = new StringBuilder("[");
                    for (int i = 0; i < count; i++)
                    {
                        bool present = (mask[i / 8] & (1 << (i % 8))) != 0; float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
                        if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y) || float.IsNaN(z) || float.IsInfinity(z) || present && rig.paths[i].Length == 0 || !present && (x != 0 || y != 0 || z != 0)) throw new InvalidDataException("Invalid skeleton joint coordinates/validity.");
                        if (i > 0) { positions.Append(','); valid.Append(','); } positions.Append('[').Append(FloatNumber(x)).Append(',').Append(FloatNumber(y)).Append(',').Append(FloatNumber(z)).Append(']'); valid.Append(present ? "true" : "false");
                    }
                    ++observations;
                    if ((observations - 1) % 256 == 0)
                    {
                        if (!checkpoints.MoveNext()) throw new InvalidDataException("Missing skeleton checkpoint index.");
                        var index = J.Parse(checkpoints.Current); ExactFields(index, "observationIndex", "syncSampleId", "byteOffset", "bindingId");
                        if (index.Long("observationIndex") != observations || index.Long("syncSampleId") != sid || index.Long("byteOffset") != offset || index.Long("bindingId") != binding) throw new InvalidDataException("Skeleton checkpoint index addresses a different pose.");
                    }
                    var row = new J { ["agentId"] = J.Quote(agent), ["captureStamp"] = stamp, ["sampleId"] = J.Number(sample), ["time"] = FloatNumber(time), ["bindingId"] = J.Number(binding), ["rigId"] = revision["rigId"], ["jointPositionsWorld"] = positions.Append(']').ToString(), ["valid"] = valid.Append(']').ToString() };
                    yield return row.ToString(); lastSync = sid; lastSample = sample; lastTime = time;
                }
                if (checkpoints.MoveNext() || observations == 0 || activeBinding != revisions.Count) throw new InvalidDataException("Unreferenced skeleton checkpoint or binding.");
            }
        }
        sealed class SkeletonRig { public int[] parents; public string[] paths; }
        string ReadAt(string relative, long offset)
        {
            using (var file = new FileStream(PathFor(relative), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (offset < 0 || offset >= file.Length) throw new InvalidDataException("Checkpoint byte offset is outside its file.");
                if (offset > 0) { file.Position = offset - 1; if (file.ReadByte() != '\n') throw new InvalidDataException("Checkpoint offset is not a line boundary."); }
                file.Position = offset; using (var reader = new StreamReader(file, new UTF8Encoding(false, true), false, 1024, true)) return reader.ReadLine() ?? throw new InvalidDataException("Checkpoint points to EOF.");
            }
        }
        string ReadRaw(string relative) => File.ReadAllText(PathFor(relative), new UTF8Encoding(false, true));
        IEnumerable<string> RawLines(string relative)
        { using (var reader = new StreamReader(new FileStream(PathFor(relative), FileMode.Open, FileAccess.Read, FileShare.ReadWrite), new UTF8Encoding(false, true))) { string line; while ((line = reader.ReadLine()) != null) if (!string.IsNullOrWhiteSpace(line)) yield return line; } }
        string PathFor(string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Capture paths must be relative.");
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Capture path escapes its directory.");
            string current = root; RejectAlias(current); foreach (string part in full.Substring(prefix.Length).Split(Path.DirectorySeparatorChar)) { current = Path.Combine(current, part); RejectAlias(current); }
            return full;
        }
        static void RejectAlias(string path)
        { try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Capture paths cannot follow aliases."); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { } }
        sealed class Definition { public J defaults; public string stampField; }
    }
}
