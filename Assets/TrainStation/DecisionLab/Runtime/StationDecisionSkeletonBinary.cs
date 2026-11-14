using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace StationFlow.TrainStation.DecisionLab
{
    [Serializable] public sealed class StationDecisionSkeletonDefinition
    {
        public string schema = StationDecisionSkeletonBinary.DefinitionSchema, rigId;
        public string[] jointNames, transformPaths;
        public int[] parentIndices;
        public string coordinateSystem = "UnityWorld", units = "metres", positionEncoding = "float32";
    }
    [Serializable] public sealed class StationDecisionSkeletonBinding
    {
        public int bindingId;
        public string rigId, actorSlotId, avatarName;
        public long effectiveFromSyncSampleId;
    }
    [Serializable] public sealed class StationDecisionSkeletonPose
    {
        public long syncSampleId;
        public int sampleId, bindingId, jointCount;
        public float time;
        public byte[] validMask;
        public float[] positionsWorld;
    }
    [Serializable] public sealed class StationDecisionSkeletonIndex
    {
        public long observationIndex, syncSampleId, byteOffset;
        public int bindingId;
    }

    /// <summary>Versioned little-endian world-joint positions; no Unity objects or dependency on JSON floating-point conversion.</summary>
    public static class StationDecisionSkeletonBinary
    {
        public const string DefinitionSchema = "stationflow.skeleton_definition.v1", PoseEncoding = "stationflow.skeleton_pose_binary.v1";
        public const int HeaderBytes = 16, RecordPrefixBytes = 32, CheckpointInterval = 256, MaximumJoints = 1024;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static byte[] Header()
        {
            var bytes = new byte[HeaderBytes]; Encoding.ASCII.GetBytes("SFLPOSE1").CopyTo(bytes, 0);
            UInt(bytes, 8, 1); UInt(bytes, 12, HeaderBytes); return bytes;
        }
        public static string ComputeRigId(StationDecisionSkeletonDefinition definition)
        {
            ValidateDefinition(definition);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                writer.Write(Utf8.GetBytes(DefinitionSchema + "\n")); writer.Write(definition.jointNames.Length);
                for (int i = 0; i < definition.jointNames.Length; ++i)
                { String(writer, definition.jointNames[i]); writer.Write(definition.parentIndices[i]); String(writer, definition.transformPaths[i]); }
                writer.Flush(); using (var hash = SHA256.Create()) return "rig-" + BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        static void String(BinaryWriter writer, string value) { var bytes = Utf8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        public static void ValidateDefinition(StationDecisionSkeletonDefinition value)
        {
            if (value == null || value.schema != DefinitionSchema || value.coordinateSystem != "UnityWorld" || value.units != "metres" || value.positionEncoding != "float32") throw new InvalidDataException("Invalid skeleton definition convention.");
            int count = value.jointNames?.Length ?? 0;
            if (count < 1 || count > MaximumJoints || value.parentIndices?.Length != count || value.transformPaths?.Length != count) throw new InvalidDataException("Invalid skeleton definition dimensions.");
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; ++i)
            {
                if (string.IsNullOrEmpty(value.jointNames[i]) || !names.Add(value.jointNames[i]) || value.transformPaths[i] == null) throw new InvalidDataException("Invalid/duplicate skeleton joint name or path.");
                int parent = value.parentIndices[i]; if (parent < -1 || parent >= count || parent == i) throw new InvalidDataException("Invalid skeleton parent.");
                int steps = 0; while (parent >= 0) { if (parent >= count || ++steps > count) throw new InvalidDataException("Cyclic skeleton parent graph."); parent = value.parentIndices[parent]; }
            }
        }
        public static byte[] EncodeRecord(StationDecisionSkeletonPose pose)
        {
            ValidatePose(pose);
            int maskBytes = pose.validMask.Length; var bytes = new byte[checked(RecordPrefixBytes + maskBytes + pose.jointCount * 12)];
            UInt(bytes, 0, bytes.Length); ULong(bytes, 4, pose.syncSampleId); UInt(bytes, 12, pose.sampleId); UInt(bytes, 16, pose.bindingId);
            UInt(bytes, 20, pose.jointCount); UInt(bytes, 24, maskBytes); Float(bytes, 28, pose.time);
            Buffer.BlockCopy(pose.validMask, 0, bytes, RecordPrefixBytes, maskBytes);
            int offset = RecordPrefixBytes + maskBytes;
            Buffer.BlockCopy(pose.positionsWorld, 0, bytes, offset, pose.positionsWorld.Length * 4);
            if (!BitConverter.IsLittleEndian) for (int i = offset; i < bytes.Length; i += 4) Array.Reverse(bytes, i, 4);
            return bytes;
        }
        public static void ValidatePose(StationDecisionSkeletonPose pose)
        {
            if (pose == null || pose.syncSampleId < 1 || pose.sampleId < 1 || pose.bindingId < 1 || pose.jointCount < 1 || pose.jointCount > MaximumJoints || !Finite(pose.time)) throw new InvalidDataException("Invalid skeleton pose header.");
            int count = pose.jointCount, maskBytes = (count + 7) / 8;
            if (pose.validMask?.Length != maskBytes || pose.positionsWorld?.Length != count * 3) throw new InvalidDataException("Invalid skeleton pose dimensions.");
            if (count % 8 != 0 && (pose.validMask[maskBytes - 1] & ~((1 << (count % 8)) - 1)) != 0) throw new InvalidDataException("Nonzero unused skeleton validity bits.");
            for (int i = 0; i < count; ++i)
                for (int axis = 0; axis < 3; ++axis)
                { float v = pose.positionsWorld[i * 3 + axis]; if (!Finite(v) || (pose.validMask[i / 8] & (1 << (i % 8))) == 0 && v != 0f) throw new InvalidDataException("Skeleton coordinates must be finite; missing joint coordinates must be zero."); }
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static void Float(byte[] bytes, int offset, float value) { var bits = BitConverter.GetBytes(value); if (!BitConverter.IsLittleEndian) Array.Reverse(bits); Buffer.BlockCopy(bits, 0, bytes, offset, 4); }
        static void UInt(byte[] bytes, int offset, int value) { for (int i = 0; i < 4; ++i) bytes[offset + i] = (byte)((uint)value >> (i * 8)); }
        static void ULong(byte[] bytes, int offset, long value) { for (int i = 0; i < 8; ++i) bytes[offset + i] = (byte)((ulong)value >> (i * 8)); }
    }
}
