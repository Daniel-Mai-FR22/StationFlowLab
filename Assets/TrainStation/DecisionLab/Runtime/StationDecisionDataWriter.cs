using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>A FIFO worker owns all common and per-agent data files. Admission never waits for capacity.
    /// Queued records must be detached managed DTOs whose ownership is transferred to this writer.</summary>
    public sealed class StationDecisionDataWriter : IDisposable
    {
        public const int DefaultMaxPendingCommands = 32768;
        public const long DefaultMaxPendingBytes = 64L * 1024 * 1024;
        public const long BatchOverheadBytes = 256, BatchRecordOverheadBytes = 64;
        readonly object gate = new object();
        readonly Queue<Command> commands = new Queue<Command>();
        readonly Dictionary<string, StreamWriter> roots = new Dictionary<string, StreamWriter>(StringComparer.Ordinal);
        readonly string directory, runId;
        readonly Func<object, bool, string> serialize;
        readonly StationDecisionCaptureDiagnostics diagnostics;
        readonly int maxCommands;
        readonly long maxBytes;
        readonly Thread worker;
        Thread closingWorker;
        StationDecisionAgentCaptureStore agents;
        StationDecisionCaptureNormalizer normalizer;
        readonly Dictionary<string, string> skeletonDefinitions = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, int> skeletonJointCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, SkeletonProgress> skeletonProgress = new Dictionary<string, SkeletonProgress>(StringComparer.Ordinal);
        readonly byte[] skeletonHeader = StationDecisionSkeletonBinary.Header();
        readonly Dictionary<Type, string> defaultRecords = new Dictionary<Type, string>();
        Exception failure;
        bool accepting = true, completed, completionStarted, completionReported;
        long pendingBytes, accepted, written, highWaterBytes, recordsAccepted, recordsWritten;
        Batch activeBatch;
        bool flushPending;
        int pendingCommands, highWaterCommands;
        public bool Background { get; }
        public string Mode => Background ? "background" : "main";
        public int MaxPendingCommands => maxCommands;
        public long MaxPendingBytes => maxBytes;
        public long CommandsAccepted { get { lock (gate) return accepted; } }
        public long CommandsCompleted { get { lock (gate) return written; } }
        public long RecordsAccepted { get { lock (gate) return recordsAccepted; } }
        public long RecordsCompleted { get { lock (gate) return recordsWritten; } }
        public bool HasActiveBatch { get { lock (gate) return activeBatch != null; } }
        public int PendingCommands { get { lock (gate) return pendingCommands; } }
        public long PendingBytes { get { lock (gate) return pendingBytes; } }
        public int HighWaterCommands { get { lock (gate) return highWaterCommands; } }
        public long HighWaterBytes { get { lock (gate) return highWaterBytes; } }
        public Exception Failure { get { lock (gate) return failure; } }
        /// <summary>True only after accepted commands and owned file closure have finished, including failures.</summary>
        public bool IsCompletionFinished { get { lock (gate) return completed; } }

        public StationDecisionDataWriter(string directory, string runId, Func<object, bool, string> serialize,
            bool background = true, StationDecisionCaptureDiagnostics diagnostics = null,
            int maxPendingCommands = DefaultMaxPendingCommands, long maxPendingBytes = DefaultMaxPendingBytes)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new ArgumentException("An absolute capture directory is required.", nameof(directory));
            if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("A run ID is required.", nameof(runId));
            if (maxPendingCommands < 1 || maxPendingBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxPendingCommands));
            this.directory = Path.GetFullPath(directory); this.runId = runId;
            this.serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
            this.diagnostics = diagnostics; maxCommands = maxPendingCommands; maxBytes = maxPendingBytes; Background = background;
            if (background) { worker = new Thread(Run) { IsBackground = true, Name = "Station capture data" }; worker.Start(); }
        }
        public void ThrowIfFailed()
        { lock (gate) { if (failure != null) throw new IOException("Capture data writer failed; this session is incomplete.", failure); } }

        void EnsureNormalizer()
        {
            if (normalizer != null) return;
            Root("record-definitions.jsonl"); Root("rosters.jsonl"); Root("state-checkpoints.jsonl");
            normalizer = new StationDecisionCaptureNormalizer(runId, (name, json) => Root(name).WriteLine(json),
                (name, json) => File.WriteAllText(PathFor(name), json, new UTF8Encoding(false, true)),
                (id, name, json) => agents.WriteLineAtOffset(id, name, json),
                (id, json) => agents.WriteProfile(id, json), id => agents.RelativeDirectory(id));
        }
        string DefaultRecord(object record)
        {
            Type type = record.GetType();
            if (!defaultRecords.TryGetValue(type, out var json))
            { json = Serialize(Activator.CreateInstance(type, true), false); defaultRecords.Add(type, json); }
            return json;
        }
        public void OpenRoot(string name) => Enqueue(() => { EnsureNormalizer(); Root(name); }, 256);
        public void PrepareAgents(int maxOpenWriters, string[] agentIds)
        {
            var ids = (string[])(agentIds ?? Array.Empty<string>()).Clone();
            long bytes = 256; foreach (string id in ids) bytes += 24 + (id?.Length ?? 0) * 2L;
            Enqueue(() => { EnsureNormalizer(); if (agents != null) throw new InvalidOperationException("Agent storage is already prepared.");
                agents = new StationDecisionAgentCaptureStore(directory, runId, maxOpenWriters);
                foreach (string id in ids) agents.RegisterAgent(id); agents.SaveIndex(); }, bytes);
        }
        public void WriteRoot(string name, object ownedRecord, bool pretty = false, long retainedBytes = 4096)
        {
            CheckRecord(ownedRecord);
            Enqueue(() => { EnsureNormalizer(); string json = Serialize(ownedRecord, false);
                normalizer.WriteRoot(name, json, ownedRecord.GetType().FullName, DefaultRecord(ownedRecord)); }, retainedBytes, record: true);
        }
        public void WriteAgent(string id, string name, object ownedRecord, bool profile = false, long retainedBytes = 4096)
        {
            CheckRecord(ownedRecord);
            Enqueue(() => { if (agents == null) throw new InvalidOperationException("Agent storage is not prepared.");
                EnsureNormalizer(); string json = Serialize(ownedRecord, false);
                normalizer.WriteAgent(id, name, json, ownedRecord.GetType().FullName, DefaultRecord(ownedRecord), profile); }, retainedBytes, record: true);
        }
        public void WriteSkeletonDefinition(StationDecisionSkeletonDefinition ownedDefinition)
        {
            CheckRecord(ownedDefinition);
            Enqueue(() => {
                EnsureNormalizer(); string id = StationDecisionSkeletonBinary.ComputeRigId(ownedDefinition);
                if (id != ownedDefinition.rigId) throw new IOException("Skeleton definition ID differs from its canonical structure.");
                string json = Serialize(ownedDefinition, false);
                if (skeletonDefinitions.TryGetValue(id, out var prior)) { if (prior != json) throw new IOException("A skeleton definition cannot be replaced."); return; }
                skeletonDefinitions.Add(id, json); skeletonJointCounts.Add(id, ownedDefinition.jointNames.Length); Root("skeleton-definitions.jsonl").WriteLine(json);
            }, 1024 + (ownedDefinition.jointNames?.Length ?? 0) * 512L, record: true);
        }
        public void WriteSkeletonBinding(string agentId, StationDecisionSkeletonBinding ownedBinding)
        {
            CheckRecord(ownedBinding);
            Enqueue(() => {
                EnsureNormalizer(); normalizer.RequireClockReference(ownedBinding.effectiveFromSyncSampleId);
                if (agents == null || !skeletonDefinitions.ContainsKey(ownedBinding.rigId)) throw new IOException("Skeleton binding references an unavailable rig.");
                if (!skeletonProgress.TryGetValue(agentId, out var progress)) { progress = new SkeletonProgress(); skeletonProgress.Add(agentId, progress); }
                if (ownedBinding.bindingId != progress.binding + 1 || ownedBinding.effectiveFromSyncSampleId <= progress.lastSync || progress.binding > 0 && !progress.bindingUsed) throw new IOException("Skeleton binding sequence is invalid.");
                progress.binding = ownedBinding.bindingId; progress.effectiveFrom = ownedBinding.effectiveFromSyncSampleId;
                progress.jointCount = skeletonJointCounts[ownedBinding.rigId]; progress.bindingUsed = false;
                agents.WriteLine(agentId, "skeleton-bindings.jsonl", Serialize(ownedBinding, false));
            }, 1024, record: true);
        }
        public void WriteSkeletonPose(string agentId, StationDecisionSkeletonPose ownedPose)
        {
            CheckRecord(ownedPose);
            Enqueue(() => {
                EnsureNormalizer(); normalizer.RequireClockReference(ownedPose.syncSampleId);
                if (agents == null || !skeletonProgress.TryGetValue(agentId, out var progress) || ownedPose.bindingId != progress.binding || ownedPose.jointCount != progress.jointCount || ownedPose.syncSampleId <= progress.lastSync || ownedPose.syncSampleId < progress.effectiveFrom || !progress.bindingUsed && ownedPose.syncSampleId != progress.effectiveFrom)
                    throw new IOException("Skeleton pose references a missing, stale or future binding/clock.");
                var encoded = StationDecisionSkeletonBinary.EncodeRecord(ownedPose);
                long offset = agents.WriteBinaryAtOffset(agentId, "skeleton-poses.bin", skeletonHeader, encoded);
                ++progress.observations; progress.lastSync = ownedPose.syncSampleId; progress.bindingUsed = true;
                if ((progress.observations - 1) % StationDecisionSkeletonBinary.CheckpointInterval == 0)
                    agents.WriteLine(agentId, "skeleton-index.jsonl", Serialize(new StationDecisionSkeletonIndex { observationIndex = progress.observations,
                        syncSampleId = ownedPose.syncSampleId, byteOffset = offset, bindingId = ownedPose.bindingId }, false));
            }, 256 + (ownedPose.positionsWorld?.Length ?? 0) * 4L + (ownedPose.validMask?.Length ?? 0), record: true);
        }
        sealed class SkeletonProgress { public int binding, jointCount; public long observations, lastSync, effectiveFrom; public bool bindingUsed; }
        public void Flush()
        {
            lock (gate) { RequireAccepting(); if (flushPending) return; flushPending = true; }
            try
            {
                Enqueue(() => {
                    try { foreach (var output in roots.Values) output.Flush(); agents?.Flush(); }
                    finally { lock (gate) flushPending = false; }
                }, 128, batchable: false);
            }
            catch { lock (gate) flushPending = false; throw; }
        }
        /// <summary>Collect a producer's ordered writes into one FIFO command. Assembly also counts against queue bounds.</summary>
        public Batch BeginBatch()
        {
            lock (gate)
            {
                RequireAccepting();
                if (activeBatch != null) throw new InvalidOperationException("A capture data batch is already active.");
                RequireCapacity(1, BatchOverheadBytes);
                activeBatch = new Batch(this, Thread.CurrentThread.ManagedThreadId);
                ++pendingCommands; pendingBytes += BatchOverheadBytes; UpdateHighWater();
                return activeBatch;
            }
        }
        /// <summary>Atomically reserve a whole optional snapshot before any producer state changes.
        /// Capacity pressure is nonfatal. A successful reservation commits without another capacity check.
        /// Keep headroom for mandatory samples, lifecycle records and already-admitted camera diagnostics.</summary>
        public bool TryBeginReservedBatch(long byteBudget, long essentialHeadroomBytes, out Batch batch, int essentialHeadroomCommands = 0)
        {
            if (byteBudget < BatchOverheadBytes) throw new ArgumentOutOfRangeException(nameof(byteBudget));
            if (essentialHeadroomBytes < 0 || essentialHeadroomBytes > maxBytes) throw new ArgumentOutOfRangeException(nameof(essentialHeadroomBytes));
            if (essentialHeadroomCommands < 0 || essentialHeadroomCommands > maxCommands) throw new ArgumentOutOfRangeException(nameof(essentialHeadroomCommands));
            lock (gate)
            {
                batch = null; RequireAccepting();
                if (activeBatch != null) throw new InvalidOperationException("A capture data batch is already active.");
                long availableBytes = maxBytes - pendingBytes;
                int availableCommands = maxCommands - pendingCommands;
                if (byteBudget > availableBytes || essentialHeadroomBytes > availableBytes - byteBudget ||
                    availableCommands < 1 || essentialHeadroomCommands > availableCommands - 1) return false;
                batch = activeBatch = new Batch(this, Thread.CurrentThread.ManagedThreadId, byteBudget);
                ++pendingCommands; pendingBytes += byteBudget; UpdateHighWater();
                return true;
            }
        }
        public sealed class Batch : IDisposable
        {
            internal readonly StationDecisionDataWriter owner;
            internal readonly int threadId;
            internal List<Command> items = new List<Command>();
            internal long bytes = BatchOverheadBytes, accountedBytes, records;
            internal readonly long reservedBytes;
            internal bool finished;
            internal Batch(StationDecisionDataWriter owner, int threadId, long reservation = 0)
            { this.owner = owner; this.threadId = threadId; reservedBytes = reservation; accountedBytes = reservation == 0 ? BatchOverheadBytes : reservation; }
            public long RetainedBytes { get { lock (owner.gate) return bytes; } }
            public long ReservedBytes => reservedBytes;
            public void Commit() => owner.CommitBatch(this);
            public void Dispose() => owner.CancelBatch(this);
        }
        void CommitBatch(Batch batch)
        {
            Command command;
            lock (gate)
            {
                RequireAccepting(); RequireBatchOwner(batch);
                if (batch.items.Count == 0) { ReleaseBatch(batch); return; }
                command = new Command { children = batch.items, bytes = batch.bytes, records = batch.records, enqueued = Stopwatch.GetTimestamp() };
                pendingBytes -= batch.accountedBytes - batch.bytes; batch.accountedBytes = batch.bytes;
                batch.items = null; batch.finished = true; activeBatch = null;
                ++accepted; recordsAccepted += command.records;
                if (Background) { commands.Enqueue(command); Monitor.PulseAll(gate); return; }
            }
            Execute(command); ThrowIfFailed();
        }
        void RequireBatchOwner(Batch batch)
        {
            if (batch.finished || activeBatch != batch) throw new ObjectDisposedException(nameof(Batch));
            if (Thread.CurrentThread.ManagedThreadId != batch.threadId) throw new InvalidOperationException("A capture batch belongs to its creating thread.");
        }
        void CancelBatch(Batch batch)
        {
            lock (gate)
            {
                if (batch.finished) return;
                RequireBatchOwner(batch);
                if (batch.items.Count != 0) SetFailure(new IOException("A capture data batch was abandoned before commit; this session is incomplete."));
                else ReleaseBatch(batch);
            }
        }
        void ReleaseBatch(Batch batch)
        {
            --pendingCommands; pendingBytes -= batch.accountedBytes;
            batch.items.Clear(); batch.items = null; batch.finished = true; activeBatch = null; Monitor.PulseAll(gate);
        }

        /// <summary>Startup-only readiness fence, before the caller resumes simulation or admits video frames.
        /// It intentionally preserves the existing synchronous Start preparation. Never call from a live capture tick.</summary>
        public void CompleteInitialization()
        {
            if (HasActiveBatch) throw new InvalidOperationException("Complete initialization before starting a capture batch.");
            var fence = new InitializationFence();
            Enqueue(() =>
            {
                foreach (var output in roots.Values) output.Flush(); agents?.Flush();
                lock (fence) { fence.done = true; Monitor.PulseAll(fence); }
            }, 128);
            lock (fence)
            {
                while (!fence.done)
                {
                    ThrowIfFailed();
                    if (worker != null && !worker.IsAlive) throw new IOException("Capture data worker stopped before initialization completed.");
                    Monitor.Wait(fence, 50);
                }
            }
            ThrowIfFailed();
        }
        sealed class InitializationFence { public bool done; }
        void CheckRecord(object record)
        { if (record == null) throw new ArgumentNullException(nameof(record)); StationDecisionCaptureSnapshot.ValidateType(record.GetType()); }
        string Serialize(object record, bool pretty)
        {
            long started = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
            try { return serialize(record, pretty); }
            finally { diagnostics?.Elapsed("", "data_json_serialize_" + Mode + "_wall", started); }
        }
        string PathFor(string name)
        {
            if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name || name == "." || name == "..")
                throw new ArgumentException("Only root file names are accepted.", nameof(name));
            return Path.Combine(directory, name);
        }
        StreamWriter Root(string name)
        {
            if (!name.EndsWith(".jsonl", StringComparison.Ordinal)) throw new ArgumentException("A JSONL root stream is required.", nameof(name));
            if (!roots.TryGetValue(name, out var output))
            {
                Directory.CreateDirectory(directory);
                output = new StreamWriter(new FileStream(PathFor(name), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false, true), 32768);
                roots.Add(name, output);
            }
            return output;
        }
        void RequireAccepting()
        {
            if (failure != null) throw new IOException("Capture data writer failed; this session is incomplete.", failure);
            if (!accepting) throw new ObjectDisposedException(nameof(StationDecisionDataWriter));
        }
        void RequireCapacity(int commandCount, long retainedBytes)
        {
            if (commandCount > maxCommands - pendingCommands || retainedBytes > maxBytes - pendingBytes)
            {
                var error = new IOException("Capture data queue capacity exceeded (commands=" + pendingCommands + ", retainedBytes=" + pendingBytes + "). No complete session can be produced.");
                SetFailure(error); throw new IOException(error.Message, error);
            }
        }
        void UpdateHighWater()
        { highWaterCommands = Math.Max(highWaterCommands, pendingCommands); highWaterBytes = Math.Max(highWaterBytes, pendingBytes); }
        void Enqueue(Action action, long retainedBytes, bool record = false, bool batchable = true)
        {
            if (retainedBytes < 1) throw new ArgumentOutOfRangeException(nameof(retainedBytes));
            var command = new Command { action = action, bytes = retainedBytes, records = record ? 1 : 0, enqueued = Stopwatch.GetTimestamp() };
            lock (gate)
            {
                RequireAccepting();
                if (batchable && activeBatch != null && activeBatch.threadId == Thread.CurrentThread.ManagedThreadId)
                {
                    long additionalBytes = checked(retainedBytes + BatchRecordOverheadBytes);
                    if (activeBatch.reservedBytes != 0)
                    {
                        if (additionalBytes > activeBatch.reservedBytes - activeBatch.bytes)
                        {
                            var error = new IOException("Capture data batch exceeded its reserved byte budget; this session is incomplete.");
                            SetFailure(error); throw new IOException(error.Message, error);
                        }
                    }
                    else
                    {
                        RequireCapacity(0, additionalBytes);
                        activeBatch.accountedBytes += additionalBytes; pendingBytes += additionalBytes;
                    }
                    activeBatch.items.Add(command); activeBatch.bytes += additionalBytes; activeBatch.records += command.records;
                    UpdateHighWater(); return;
                }
                // Count the command executing on the worker and the producer batch as well as waiting commands.
                RequireCapacity(1, retainedBytes);
                ++accepted; recordsAccepted += command.records; ++pendingCommands; pendingBytes += retainedBytes; UpdateHighWater();
                if (Background) { commands.Enqueue(command); Monitor.PulseAll(gate); return; }
            }
            Execute(command); ThrowIfFailed();
        }
        void Execute(Command command)
        {
            long started = diagnostics != null ? Stopwatch.GetTimestamp() : 0;
            try
            {
                diagnostics?.Record("", "data_queue_wait_wall", "ms", (Stopwatch.GetTimestamp() - command.enqueued) * 1000d / Stopwatch.Frequency);
                if (command.children == null)
                {
                    command.action(); lock (gate) { ++written; recordsWritten += command.records; }
                }
                else
                {
                    bool succeeded = true; long completedRecords = 0;
                    foreach (var child in command.children)
                    {
                        try { child.action(); completedRecords += child.records; }
                        catch (Exception error) { succeeded = false; SetFailure(error); }
                        finally { child.action = null; }
                    }
                    lock (gate) { recordsWritten += completedRecords; if (succeeded) ++written; }
                }
            }
            catch (Exception error) { SetFailure(error); }
            finally
            {
                // Do not let the idle worker's last stack-local command keep a full snapshot alive
                // after its retained-byte accounting has been released.
                command.action = null; command.children?.Clear(); command.children = null;
                lock (gate) { --pendingCommands; pendingBytes -= command.bytes; Monitor.PulseAll(gate); }
                diagnostics?.Elapsed("", "data_command_" + Mode + "_wall", started);
            }
        }
        void Run()
        {
            try
            {
                while (true)
                {
                    Command next;
                    lock (gate)
                    {
                        while (commands.Count == 0 && accepting) Monitor.Wait(gate);
                        if (commands.Count == 0) break;
                        // Drain accepted commands even after admission overload. A failed I/O command is explicit in Failure.
                        next = commands.Dequeue();
                    }
                    Execute(next);
                }
            }
            catch (Exception error) { SetFailure(error); }
            finally { CloseAndSignalCompletion(); }
        }
        void SetFailure(Exception error)
        {
            lock (gate)
            {
                if (failure == null) failure = error; accepting = false;
                if (activeBatch != null) ReleaseBatch(activeBatch);
                Monitor.PulseAll(gate);
            }
        }
        void CloseOwnedFiles()
        {
            foreach (var progress in skeletonProgress.Values) if (!progress.bindingUsed) SetFailure(new IOException("An unreferenced skeleton binding remains."));
            foreach (var output in roots.Values) try { output.Dispose(); } catch (Exception error) { SetFailure(error); }
            roots.Clear(); normalizer = null; defaultRecords.Clear(); skeletonDefinitions.Clear(); skeletonJointCounts.Clear(); skeletonProgress.Clear(); try { agents?.Dispose(); } catch (Exception error) { SetFailure(error); } finally { agents = null; }
        }
        /// <summary>Close admission immediately; draining and file closure never block this caller.</summary>
        public void BeginComplete()
        {
            lock (gate)
            {
                accepting = false;
                if (activeBatch != null)
                {
                    if (activeBatch.items.Count != 0) SetFailure(new IOException("Capture completed with an uncommitted data batch; this session is incomplete."));
                    else ReleaseBatch(activeBatch);
                }
                if (completionStarted || completed) { Monitor.PulseAll(gate); return; }
                completionStarted = true;
                if (!Background)
                {
                    // Main-mode records execute synchronously, but closing their buffered files
                    // can still be slow. Keep that final I/O away from the frame loop as well.
                    closingWorker = new Thread(() =>
                    {
                        lock (gate) while (pendingCommands != 0) Monitor.Wait(gate);
                        CloseAndSignalCompletion();
                    }) { IsBackground = true, Name = "Station capture data finalization" };
                    closingWorker.Start();
                }
                Monitor.PulseAll(gate);
            }
        }
        void CloseAndSignalCompletion()
        {
            try { CloseOwnedFiles(); }
            catch (Exception error) { SetFailure(error); }
            finally { lock (gate) { completed = true; Monitor.PulseAll(gate); } }
        }
        /// <summary>Nonblocking completion poll. Errors are reported only after all accepted work is drained.</summary>
        public bool TryComplete()
        {
            BeginComplete();
            bool report;
            lock (gate)
            {
                if (!completed) return false;
                report = !completionReported; completionReported = true;
            }
            if (report)
            {
                diagnostics?.Record("", "data_queue_high_water_commands", "commands", HighWaterCommands);
                diagnostics?.Record("", "data_queue_high_water_bytes", "bytes", HighWaterBytes);
                diagnostics?.Increment("", "data_commands_accepted", CommandsAccepted);
                diagnostics?.Increment("", "data_commands_completed", CommandsCompleted);
                diagnostics?.Increment("", "data_records_accepted", RecordsAccepted);
                diagnostics?.Increment("", "data_records_completed", RecordsCompleted);
            }
            ThrowIfFailed();
            return true;
        }
        /// <summary>Terminal barrier for shutdown/tests. Live-frame finalization uses BeginComplete/TryComplete.</summary>
        public void Complete()
        {
            BeginComplete();
            lock (gate) while (!completed) Monitor.Wait(gate);
            // Completion is published after closing files; join only at a terminal barrier.
            if (Background) worker.Join(); else closingWorker?.Join();
            TryComplete();
        }
        public void Dispose() => Complete();
        internal sealed class Command { public Action action; public List<Command> children; public long bytes, enqueued, records; }
    }

    /// <summary>Copies only serialized public DTO fields. No engine objects, delegates or mutable runtime graphs cross the worker boundary.</summary>
    public static class StationDecisionCaptureSnapshot
    {
        static readonly object Gate = new object();
        static readonly Dictionary<Type, FieldInfo[]> Fields = new Dictionary<Type, FieldInfo[]>();
        static readonly HashSet<Type> Validated = new HashSet<Type>();
        public static void ValidateType(Type type) { lock (Gate) Validate(type, new HashSet<Type>()); }
        static void Validate(Type type, HashSet<Type> visiting)
        {
            if (Validated.Contains(type) || type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return;
            for (Type ancestor = type; ancestor != null; ancestor = ancestor.BaseType)
                if (ancestor.FullName == "UnityEngine.Object") throw new ArgumentException("Capture DTOs cannot contain UnityEngine.Object: " + type.FullName);
            if (!visiting.Add(type)) throw new ArgumentException("Capture DTO types must be acyclic: " + type.FullName);
            if (type.IsArray) { if (type.GetArrayRank() != 1) throw new ArgumentException("Only one-dimensional DTO arrays are supported."); Validate(type.GetElementType(), visiting); }
            else
            {
                if (type == typeof(object) || type.IsInterface || type.IsAbstract || !type.IsValueType && !type.IsSealed ||
                    typeof(System.Collections.IEnumerable).IsAssignableFrom(type) || !type.IsSerializable || typeof(Delegate).IsAssignableFrom(type))
                    throw new ArgumentException("Only concrete sealed managed DTOs, value types and arrays are supported: " + type.FullName);
                foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                    foreach (var attribute in field.GetCustomAttributesData())
                        if (attribute.AttributeType.FullName == "UnityEngine.SerializeField" || attribute.AttributeType.FullName == "UnityEngine.SerializeReference")
                            throw new ArgumentException("Capture DTOs must expose serialized fields publicly: " + type.FullName);
                foreach (var field in SerializableFields(type)) Validate(field.FieldType, visiting);
            }
            visiting.Remove(type); Validated.Add(type);
        }
        static FieldInfo[] SerializableFields(Type type)
        {
            if (!Fields.TryGetValue(type, out var fields))
            {
                var selected = new List<FieldInfo>();
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (!field.IsNotSerialized) selected.Add(field);
                fields = selected.ToArray(); Fields.Add(type, fields);
            }
            return fields;
        }
        public static T Copy<T>(T source, out long retainedBytes)
        {
            Type runtime = source == null ? typeof(T) : source.GetType();
            ValidateType(runtime); long bytes = 0; object clone;
            lock (Gate) clone = CopyValue(source, runtime, ref bytes);
            retainedBytes = Math.Max(128, bytes); return (T)clone;
        }
        static object CopyValue(object source, Type type, ref long bytes)
        {
            if (source == null) { bytes += 8; return null; }
            if (source.GetType() != type) throw new ArgumentException("Polymorphic capture fields are unsupported: " + type.FullName);
            if (type == typeof(string)) { bytes += 24 + ((string)source).Length * 2L; return source; }
            if (type.IsPrimitive || type.IsEnum || type == typeof(decimal)) { bytes += 16; return source; }
            if (type.IsArray)
            {
                var original = (Array)source; var clone = Array.CreateInstance(type.GetElementType(), original.Length); bytes += 32;
                for (int i = 0; i < original.Length; ++i) clone.SetValue(CopyValue(original.GetValue(i), type.GetElementType(), ref bytes), i);
                return clone;
            }
            object result = Activator.CreateInstance(type, true); bytes += 32;
            foreach (var field in SerializableFields(type)) field.SetValue(result, CopyValue(field.GetValue(source), field.FieldType, ref bytes));
            return result;
        }
    }
}
