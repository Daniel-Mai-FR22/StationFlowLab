using System.Globalization;
using System.Text;
using System.Text.Json;
using StationFlow.TrainStation.DecisionLab;
using J = StationFlow.TrainStation.DecisionLab.StationDecisionJsonObject;

static class Program
{
    static int checks;
    static string root;
    static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    static readonly string[] Names = "Hips LeftUpperLeg RightUpperLeg LeftLowerLeg RightLowerLeg LeftFoot RightFoot Spine Chest Neck Head LeftShoulder RightShoulder LeftUpperArm RightUpperArm LeftLowerArm RightLowerArm LeftHand RightHand LeftToes RightToes LeftEye RightEye Jaw LeftThumbProximal LeftThumbIntermediate LeftThumbDistal LeftIndexProximal LeftIndexIntermediate LeftIndexDistal LeftMiddleProximal LeftMiddleIntermediate LeftMiddleDistal LeftRingProximal LeftRingIntermediate LeftRingDistal LeftLittleProximal LeftLittleIntermediate LeftLittleDistal RightThumbProximal RightThumbIntermediate RightThumbDistal RightIndexProximal RightIndexIntermediate RightIndexDistal RightMiddleProximal RightMiddleIntermediate RightMiddleDistal RightRingProximal RightRingIntermediate RightRingDistal RightLittleProximal RightLittleIntermediate RightLittleDistal UpperChest".Split(' ');
    static string Serialize(object value, bool pretty) => JsonSerializer.Serialize(value, value.GetType(), Json);
    static void Check(bool pass, string why) { ++checks; if (!pass) throw new Exception(why); }
    static void Reject(Action action, string why) { bool rejected = false; try { action(); } catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException || e is InvalidOperationException || e is EndOfStreamException) { rejected = true; } Check(rejected, why); }
    static void Main(string[] args)
    {
        root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        CodecTests(); var result = Generate(root); Roundtrip(root, result); Corruption(root); Failures();
        var receipt = new { success = true, checks, fixture = root, expectedPoses = result.Count, joints = 55, encoding = StationDecisionSkeletonBinary.PoseEncoding, numericFloat32Exact = true, bindingRevisions = 4, checkpointInterval = 256, coverage = new[] { "55 Humanoid joints", "missing validity mask", "exact XYZ float32 bits", "Unicode canonical rig SHA256", "clock and pause", "binding revision", "logical ID/pool identity", "binary/text shared LRU", "256-record byte indexes", "corrupt/truncated payload rejection", "queue drain" } };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(root), "skeleton-core-tests.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }), Utf8);
        Console.WriteLine(JsonSerializer.Serialize(receipt));
    }
    static StationDecisionSkeletonDefinition Rig(bool alternate = false)
    {
        int[] parents = { -1,0,0,1,2,3,4,0,7,54,9,54,54,11,12,13,14,15,16,5,6,-1,-1,-1,17,24,25,17,27,28,17,30,31,17,33,34,17,36,37,18,39,40,18,42,43,18,45,46,18,48,49,18,51,52,8 };
        var paths = Names.Select((name, i) => i is >= 21 and <= 23 ? "" : "Rig-\u00e9\u6f22/" + name + (alternate ? "/variant" : "")).ToArray();
        var d = new StationDecisionSkeletonDefinition { jointNames = (string[])Names.Clone(), parentIndices = parents, transformPaths = paths };
        d.rigId = StationDecisionSkeletonBinary.ComputeRigId(d); return d;
    }
    static Stamp Clock(int sid) => new() { runId = "skeleton-core-fixture", syncSampleId = sid, unityFrame = 100 + sid / 2,
        realElapsedSeconds = sid * .031234567890123, utc = DateTime.UnixEpoch.AddTicks(12340000000000000L + sid * 123457).ToString("O"), simulationDate = "2026-10-20",
        simulationSeconds = (sid > 204 ? sid - 5 : sid >= 200 ? 199 : sid) * .03125, stationClockSeconds = 28800 + (sid > 204 ? sid - 5 : sid >= 200 ? 199 : sid) * .03125,
        timeScale = sid is >= 200 and <= 204 ? 0 : 1, paused = sid is >= 200 and <= 204 };
    static StationDecisionSkeletonPose Pose(int sid, int sample, int binding, float time)
    {
        var xyz = new float[165]; var mask = new byte[7];
        for (int i = 0; i < 55; ++i)
        {
            if (i is >= 21 and <= 23 || i == 19 && sid % 17 == 0) continue;
            mask[i / 8] |= (byte)(1 << (i % 8));
            xyz[i * 3] = i == 0 ? BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)) : (float)(i * .1234567 + sid * .00091);
            xyz[i * 3 + 1] = (float)(1.125 + i * .019 - sid * .0005); xyz[i * 3 + 2] = (float)(-29.33 + i * .011 + sid * .032);
        }
        return new StationDecisionSkeletonPose { syncSampleId = sid, sampleId = sample, bindingId = binding, jointCount = 55, time = time, validMask = mask, positionsWorld = xyz };
    }
    static void CodecTests()
    {
        var header = StationDecisionSkeletonBinary.Header(); Check(header.Length == 16 && Encoding.ASCII.GetString(header, 0, 8) == "SFLPOSE1", "header magic");
        Check(BitConverter.ToUInt32(header, 8) == 1 && BitConverter.ToUInt32(header, 12) == 16, "header version/dimensions");
        var pose = Pose(938, 1, 1, 1.25f); var bytes = StationDecisionSkeletonBinary.EncodeRecord(pose);
        Check(bytes.Length == 699 && BitConverter.ToInt32(bytes, 0) == 699 && BitConverter.ToInt64(bytes, 4) == 938, "little endian record layout");
        for (int i = 0; i < pose.positionsWorld.Length; ++i) Check(BitConverter.ToInt32(bytes, 39 + i * 4) == BitConverter.SingleToInt32Bits(pose.positionsWorld[i]), "float32 bytes exact " + i);
        var bad = Pose(1, 1, 1, 0); bad.positionsWorld[21 * 3] = 1; Reject(() => StationDecisionSkeletonBinary.EncodeRecord(bad), "missing joint nonzero rejected");
        bad = Pose(1, 1, 1, 0); bad.validMask[6] |= 128; Reject(() => StationDecisionSkeletonBinary.EncodeRecord(bad), "unused mask bits rejected");
        bad = Pose(1, 1, 1, 0); bad.positionsWorld[0] = float.NaN; Reject(() => StationDecisionSkeletonBinary.EncodeRecord(bad), "NaN rejected");
        bad = Pose(1, 1, 1, 0); bad.jointCount = 54; Reject(() => StationDecisionSkeletonBinary.EncodeRecord(bad), "dimension mismatch rejected");
        var d = Rig(); Check(d.rigId.Length == 68 && d.rigId == StationDecisionSkeletonBinary.ComputeRigId(d), "stable structural hash");
        var alt = Rig(true); Check(alt.rigId != d.rigId, "different transform path changes rig identity");
        d.parentIndices[0] = 7; Reject(() => StationDecisionSkeletonBinary.ComputeRigId(d), "cyclic hierarchy rejected");
    }
    sealed class Wanted { public string agent, rig; public StationDecisionSkeletonPose pose; public Stamp stamp; }
    static List<Wanted> Generate(string directory)
    {
        var wanted = new List<Wanted>(); var d = Rig(); var alt = Rig(true); string first = "voyageur-\u00e9\u6f22", second = "B", replacement = "reused-slot";
        using (var writer = new StationDecisionDataWriter(directory, "skeleton-core-fixture", Serialize))
        {
            writer.PrepareAgents(2, new[] { first, second, replacement }); writer.OpenRoot("sync.jsonl"); writer.OpenRoot("skeleton-definitions.jsonl"); writer.WriteSkeletonDefinition(d); writer.WriteSkeletonDefinition(d); writer.WriteSkeletonDefinition(alt);
            for (int sid = 1; sid <= 660; ++sid)
            {
                var stamp = Clock(sid); writer.WriteRoot("sync.jsonl", new Sync { kind = "clock", stamp = stamp });
                var active = sid <= 620 ? new[] { first, second } : new[] { replacement, second };
                writer.WriteRoot("sync.jsonl", new Sync { kind = "avatar_snapshot", stamp = stamp, activeJourneyIds = active });
                foreach (string agent in active)
                {
                    int binding = agent == first && sid >= 300 ? 2 : 1; string rig = binding == 2 ? alt.rigId : d.rigId;
                    if (sid == 1 || agent == first && sid == 300 || agent == replacement && sid == 621)
                        writer.WriteSkeletonBinding(agent, new StationDecisionSkeletonBinding { bindingId = binding, rigId = rig, actorSlotId = agent == second ? "slot-001" : "slot-000", avatarName = binding == 1 ? "XBot" : "XBot-v2", effectiveFromSyncSampleId = sid });
                    var pose = Pose(sid, sid, binding, (float)stamp.simulationSeconds); writer.WriteSkeletonPose(agent, pose);
                    wanted.Add(new Wanted { agent = agent, rig = rig, pose = pose, stamp = stamp });
                }
                if (sid % 100 == 0) writer.Flush();
            }
            writer.Complete(); Check(writer.CommandsAccepted == writer.CommandsCompleted && writer.PendingBytes == 0 && writer.PendingCommands == 0, "complete FIFO drain");
        }
        var manifest = new J { ["schema"] = J.Quote(StationDecisionCaptureNormalizer.CaptureSchema), ["storageLayout"] = J.Quote(StationDecisionCaptureNormalizer.StorageLayout), ["recordEncoding"] = J.Quote(StationDecisionCaptureNormalizer.RecordEncoding), ["runId"] = J.Quote("skeleton-core-fixture"), ["status"] = J.Quote("complete"), ["errors"] = "[]", ["syncSamples"] = "660", ["agentsDirectory"] = J.Quote("agents"), ["agentIndexFile"] = J.Quote("agents/index.json"),
            ["captureOptions"] = "{\"video\":false,\"timetable\":false,\"profiles\":false,\"trajectories\":false,\"animations\":false,\"skeletons\":true,\"decisions\":false,\"events\":false}", ["selectedCameraIds"] = "[]", ["cameraResults"] = "[]",
            ["skeletonDefinitionsFile"] = J.Quote("skeleton-definitions.jsonl"), ["skeletonPoseFileName"] = J.Quote("skeleton-poses.bin"), ["skeletonBindingsFileName"] = J.Quote("skeleton-bindings.jsonl"), ["skeletonIndexFileName"] = J.Quote("skeleton-index.jsonl"), ["skeletonPoseFormat"] = J.Quote(StationDecisionSkeletonBinary.PoseEncoding) };
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest.ToString(), Utf8); return wanted;
    }
    static Dictionary<string, string> Directories(string directory) => J.Array(J.Parse(File.ReadAllText(Path.Combine(directory, "agents/index.json")))["agents"]).Select(J.Parse).ToDictionary(x => x.Text("agentId"), x => x.Text("directory"));
    static void Roundtrip(string directory, List<Wanted> wanted)
    {
        var reader = new StationDecisionCaptureReader(directory); var paths = Directories(directory);
        using var golden = new StreamWriter(Path.Combine(Path.GetDirectoryName(directory), "skeleton-core-expected.jsonl"), false, Utf8);
        Check(File.ReadAllLines(Path.Combine(directory, "skeleton-definitions.jsonl")).Length == 2, "shared rig emitted once");
        foreach (var group in wanted.GroupBy(x => x.agent))
        {
            string path = paths[group.Key] + "/skeleton-poses.bin"; var expanded = reader.ReadLines(path).ToArray(); var expected = group.ToArray(); Check(expanded.Length == expected.Length, "every pose retained");
            for (int i = 0; i < expected.Length; ++i)
            {
                var w = expected[i]; var row = J.Parse(expanded[i]); Check(row.Text("agentId") == group.Key && row.Text("rigId") == w.rig && row.Long("bindingId") == w.pose.bindingId && row.Long("sampleId") == w.pose.sampleId, "logical and binding identity");
                Check(J.Parse(row["captureStamp"]).Long("syncSampleId") == w.pose.syncSampleId && double.Parse(row["time"], CultureInfo.InvariantCulture) == w.pose.time, "exact source clock/time");
                var xyz = J.Array(row["jointPositionsWorld"]).Select(J.Array).ToArray(); var valid = J.Array(row["valid"]);
                for (int j = 0; j < 55; ++j)
                {
                    Check((valid[j] == "true") == ((w.pose.validMask[j / 8] & (1 << (j % 8))) != 0), "validity bit roundtrip");
                    for (int axis = 0; axis < 3; ++axis) Check((float)double.Parse(xyz[j][axis], CultureInfo.InvariantCulture) == w.pose.positionsWorld[j * 3 + axis], "exact IEEE float32 position roundtrip");
                }
                golden.WriteLine(new J { ["kind"] = J.Quote("lines"), ["path"] = J.Quote(path), ["row"] = row.ToString() }.ToString());
            }
            var indexes = File.ReadAllLines(Path.Combine(directory, paths[group.Key], "skeleton-index.jsonl")); Check(indexes.Length == (expected.Length + 255) / 256, "one seek checkpoint every256observations");
            var bytes = File.ReadAllBytes(Path.Combine(directory, path)); Check(bytes.Length == 16 + expected.Length * 699, "one binary header across LRU reopens");
            for (int i = 0; i < expected.Length; ++i) for (int v = 0; v < 165; ++v)
                Check(BitConverter.ToInt32(bytes, 16 + i * 699 + 39 + v * 4) == BitConverter.SingleToInt32Bits(expected[i].pose.positionsWorld[v]), "binary exact original bits incl negativezero");
        }
    }
    static void Corruption(string directory)
    {
        var entry = Directories(directory).First(); string path = entry.Value + "/skeleton-poses.bin", full = Path.Combine(directory, path); var original = File.ReadAllBytes(full);
        void Change(Action<byte[]> edit, string why) { var bytes = (byte[])original.Clone(); edit(bytes); File.WriteAllBytes(full, bytes); try { Reject(() => new StationDecisionCaptureReader(directory).ReadLines(path).ToArray(), why); } finally { File.WriteAllBytes(full, original); } }
        Change(b => b[0] = 0, "binary magic corruption rejected"); Change(b => b[8] = 2, "unknown binary version rejected"); Change(b => b[16] = 0, "record length corruption rejected"); Change(b => b[16 + 32 + 6] |= 128, "mask padding corruption rejected");
        File.WriteAllBytes(full, original.Take(original.Length - 1).ToArray()); try { Reject(() => new StationDecisionCaptureReader(directory).ReadLines(path).ToArray(), "truncated XYZ record rejected"); } finally { File.WriteAllBytes(full, original); }
        string index = Path.Combine(directory, entry.Value, "skeleton-index.jsonl"), saved = File.ReadAllText(index); File.WriteAllText(index, saved.Replace("\"byteOffset\":16", "\"byteOffset\":17")); try { Reject(() => new StationDecisionCaptureReader(directory).ReadLines(path).ToArray(), "index byte boundary corruption rejected"); } finally { File.WriteAllText(index, saved); }
    }
    static void Failures()
    {
        string dir = Path.Combine(Path.GetDirectoryName(root), "skeleton-fail-" + Guid.NewGuid().ToString("N")); var w = new StationDecisionDataWriter(dir, "failure", Serialize, false); w.PrepareAgents(2, new[] { "A" }); var d = Rig(); w.WriteSkeletonDefinition(d);
        Reject(() => w.WriteSkeletonBinding("A", new StationDecisionSkeletonBinding { bindingId = 1, rigId = d.rigId, actorSlotId = "slot-000", avatarName = "XBot", effectiveFromSyncSampleId = 999 }), "unwritten clock reference rejected"); Reject(() => w.Complete(), "failed capture remains failed");
        dir = Path.Combine(Path.GetDirectoryName(root), "skeleton-dimension-fail-" + Guid.NewGuid().ToString("N")); w = new StationDecisionDataWriter(dir, "skeleton-core-fixture", Serialize, false); w.PrepareAgents(2, new[] { "A" }); var stamp = Clock(1); w.WriteRoot("sync.jsonl", new Sync { kind = "clock", stamp = stamp }); w.WriteSkeletonDefinition(d); w.WriteSkeletonBinding("A", new StationDecisionSkeletonBinding { bindingId = 1, rigId = d.rigId, actorSlotId = "slot-000", avatarName = "XBot", effectiveFromSyncSampleId = 1 }); var pose = Pose(1, 1, 1, (float)stamp.simulationSeconds); pose.jointCount = 54;
        Reject(() => w.WriteSkeletonPose("A", pose), "pose dimensions differ from declared rig rejected"); Reject(() => w.Complete(), "dimension failure not finalized");
    }
    [Serializable] sealed class Sync { public string kind, detail; public Stamp stamp; public string[] activeJourneyIds; }
    [Serializable] sealed class Stamp { public string runId, utc, simulationDate; public long syncSampleId; public int unityFrame; public double simulationSeconds, stationClockSeconds, realElapsedSeconds; public float timeScale; public bool paused; }
}
