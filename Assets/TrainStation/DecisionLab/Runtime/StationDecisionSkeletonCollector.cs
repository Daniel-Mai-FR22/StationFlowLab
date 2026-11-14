using System;
using System.Collections.Generic;
using UnityEngine;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Main-thread-only numerical sampling of evaluated Humanoid joint transforms. One shared pose serves all cameras.</summary>
    public sealed class StationDecisionSkeletonCollector : IDisposable
    {
        readonly StationDecisionDataWriter writer;
        readonly Dictionary<Animator, Rig> rigs = new Dictionary<Animator, Rig>();
        readonly Dictionary<Animator, AnimatorCullingMode> culling = new Dictionary<Animator, AnimatorCullingMode>();
        readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        readonly HashSet<string> publishedRigs = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> estimatedNewRigs = new HashSet<string>(StringComparer.Ordinal);
        Rig missingRig;
        public StationDecisionSkeletonCollector(StationDecisionDataWriter writer, StationDecisionPassenger[] passengers)
        {
            this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
            foreach (var passenger in passengers ?? Array.Empty<StationDecisionPassenger>())
                if (passenger && passenger.motion && passenger.motion.animationPlayer) KeepEvaluating(passenger.motion.animationPlayer.TargetAnimator);
        }
        void KeepEvaluating(Animator animator)
        {
            if (!animator || culling.ContainsKey(animator)) return;
            culling.Add(animator, animator.cullingMode); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
        // Preparing a Transform lookup is safe before admission. This method never publishes
        // a definition/binding, advances lastSync, samples a pose, or writes a record.
        public long EstimateSnapshotBytes(StationDecisionPassenger[] passengers, bool daily)
        {
            estimatedNewRigs.Clear();
            long bytes = 0;
            for (int slot = 0; slot < passengers.Length; ++slot)
            {
                var passenger = passengers[slot];
                if (!passenger || daily && (!passenger.HasEntered || !passenger.gameObject.activeInHierarchy || passenger.Finished)) continue;
                var animator = passenger.motion && passenger.motion.animationPlayer ? passenger.motion.animationPlayer.TargetAnimator : null;
                var rig = GetRig(animator); string avatar = animator && animator.avatar ? animator.avatar.name : "";
                string actorSlot = "slot-" + slot.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
                int count = rig.bones.Length;
                if (!publishedRigs.Contains(rig.definition.rigId) && estimatedNewRigs.Add(rig.definition.rigId))
                    bytes += 1024 + count * 512L + StationDecisionDataWriter.BatchRecordOverheadBytes;
                if (!bindings.TryGetValue(passenger.JourneyId, out var binding) || binding.rigId != rig.definition.rigId || binding.avatar != avatar || binding.slot != actorSlot)
                    bytes += 1024 + StationDecisionDataWriter.BatchRecordOverheadBytes;
                bytes += 256 + count * 3L * 4 + (count + 7) / 8 + StationDecisionDataWriter.BatchRecordOverheadBytes;
            }
            return bytes;
        }

        public void Capture(StationDecisionPassenger passenger, int slot, int sampleId, StationDecisionCaptureStamp stamp, float time)
        {
            if (!passenger || stamp == null) throw new ArgumentException("A live logical passenger and capture stamp are required.");
            string id = passenger.JourneyId;
            var animator = passenger.motion && passenger.motion.animationPlayer ? passenger.motion.animationPlayer.TargetAnimator : null;
            KeepEvaluating(animator);
            var rig = GetRig(animator); string avatar = animator && animator.avatar ? animator.avatar.name : "";
            string actorSlot = "slot-" + slot.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
            if (!publishedRigs.Contains(rig.definition.rigId))
            { writer.WriteSkeletonDefinition(rig.definition); publishedRigs.Add(rig.definition.rigId); }
            if (!bindings.TryGetValue(id, out var binding)) { binding = new Binding(); bindings.Add(id, binding); }
            if (binding.lastSync == stamp.syncSampleId) return;
            if (binding.rigId != rig.definition.rigId || binding.avatar != avatar || binding.slot != actorSlot)
            {
                binding.rigId = rig.definition.rigId; binding.avatar = avatar; binding.slot = actorSlot; ++binding.revision;
                writer.WriteSkeletonBinding(id, new StationDecisionSkeletonBinding { bindingId = binding.revision, rigId = binding.rigId,
                    actorSlotId = actorSlot, avatarName = avatar, effectiveFromSyncSampleId = stamp.syncSampleId });
            }
            int count = rig.bones.Length; var positions = new float[count * 3]; var valid = new byte[(count + 7) / 8];
            bool evaluated = animator && animator.avatar && animator.avatar.isHuman && animator.avatar.isValid;
            if (evaluated) for (int i = 0; i < count; ++i)
            {
                var bone = rig.bones[i]; if (!bone) continue; var point = bone.position;
                if (!Finite(point.x) || !Finite(point.y) || !Finite(point.z)) continue;
                valid[i / 8] |= (byte)(1 << (i % 8)); positions[i * 3] = point.x; positions[i * 3 + 1] = point.y; positions[i * 3 + 2] = point.z;
            }
            writer.WriteSkeletonPose(id, new StationDecisionSkeletonPose { syncSampleId = stamp.syncSampleId, sampleId = sampleId,
                bindingId = binding.revision, jointCount = count, time = time, validMask = valid, positionsWorld = positions });
            binding.lastSync = stamp.syncSampleId;
        }
        Rig GetRig(Animator animator)
        {
            if (!animator) return missingRig ?? (missingRig = Build(null));
            var avatar = animator.avatar;
            bool human = animator.avatar && animator.avatar.isHuman && animator.avatar.isValid;
            if (rigs.TryGetValue(animator, out var rig) && rig.avatar == avatar && rig.human == human) return rig;
            rig = Build(animator); rigs[animator] = rig; return rig;
        }
        static Rig Build(Animator animator)
        {
            int count = (int)HumanBodyBones.LastBone; bool human = animator && animator.avatar && animator.avatar.isHuman && animator.avatar.isValid;
            var bones = new Transform[count]; var names = new string[count]; var paths = new string[count]; var parents = new int[count];
            var mapped = new Dictionary<Transform, int>();
            for (int i = 0; i < count; ++i)
            {
                names[i] = ((HumanBodyBones)i).ToString(); bones[i] = human ? animator.GetBoneTransform((HumanBodyBones)i) : null;
                paths[i] = bones[i] ? RelativePath(bones[i], animator.transform) : ""; parents[i] = -1;
                if (bones[i] && !mapped.ContainsKey(bones[i])) mapped.Add(bones[i], i);
            }
            for (int i = 0; i < count; ++i) if (bones[i])
                for (var parent = bones[i].parent; parent; parent = parent.parent)
                    if (mapped.TryGetValue(parent, out int found)) { parents[i] = found; break; }
            var definition = new StationDecisionSkeletonDefinition { jointNames = names, parentIndices = parents, transformPaths = paths };
            definition.rigId = StationDecisionSkeletonBinary.ComputeRigId(definition);
            return new Rig { avatar = animator ? animator.avatar : null, human = human, bones = bones, definition = definition };
        }
        static string RelativePath(Transform bone, Transform root)
        {
            var nodes = new List<string>(); var node = bone;
            while (node && node != root) { nodes.Add(node.name); node = node.parent; }
            if (node != root) throw new InvalidOperationException("Humanoid joint is not inside its Animator hierarchy.");
            nodes.Reverse(); return nodes.Count == 0 ? "." : string.Join("/", nodes);
        }
        public void Dispose()
        {
            foreach (var item in culling) if (item.Key) item.Key.cullingMode = item.Value;
            rigs.Clear(); culling.Clear(); bindings.Clear(); publishedRigs.Clear(); estimatedNewRigs.Clear(); missingRig = null;
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        sealed class Binding { public int revision; public string rigId, avatar, slot; public long lastSync; }
        sealed class Rig { public Avatar avatar; public bool human; public Transform[] bones; public StationDecisionSkeletonDefinition definition; }
    }
}
