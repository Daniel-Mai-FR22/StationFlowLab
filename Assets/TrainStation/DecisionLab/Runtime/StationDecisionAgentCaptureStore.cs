using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StationFlow.TrainStation.DecisionLab
{
    [Serializable]
    public sealed class StationDecisionAgentsIndex
    {
        public string schema = "stationflow.agents_index.v1", runId,
            identityPolicy = StationDecisionAgentCaptureStore.IdentityPolicy;
        public StationDecisionAgentDirectory[] agents = Array.Empty<StationDecisionAgentDirectory>();
    }

    [Serializable]
    public sealed class StationDecisionAgentDirectory
    {
        public string agentId, directory;
    }

    /// <summary>Lazy storage by exact logical journey ID, independent of reused presentation slots.</summary>
    public sealed class StationDecisionAgentCaptureStore : IDisposable
    {
        public const string IdentityPolicy = "logical-agent-id; ordinal-case-sensitive; physical-slots-may-be-reused";
        static readonly HashSet<string> Streams = new HashSet<string>(new[] { "trajectories.jsonl", "animations.jsonl", "decisions.jsonl", "events.jsonl", "state-changes.jsonl", "skeleton-bindings.jsonl", "skeleton-index.jsonl" }, StringComparer.Ordinal);
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        readonly object gate = new object();
        readonly string rootDirectory, rootPrefix, runId;
        readonly int maxOpenWriters;
        readonly Dictionary<string, StationDecisionAgentDirectory> agents = new Dictionary<string, StationDecisionAgentDirectory>(StringComparer.Ordinal);
        readonly Dictionary<string, string> directoryOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, OpenWriter> writers = new Dictionary<string, OpenWriter>(StringComparer.OrdinalIgnoreCase);
        long useSequence;
        bool dirty = true, disposed;

        public StationDecisionAgentCaptureStore(string rootDirectory, string runId, int maxOpenWriters = 32)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Path.IsPathRooted(rootDirectory)) throw new ArgumentException("An absolute capture root is required.", nameof(rootDirectory));
            if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("A run ID is required.", nameof(runId));
            if (maxOpenWriters < 1) throw new ArgumentOutOfRangeException(nameof(maxOpenWriters));
            this.rootDirectory = Path.GetFullPath(rootDirectory);
            rootPrefix = this.rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            this.runId = runId; this.maxOpenWriters = maxOpenWriters;
            RejectLink(this.rootDirectory);
        }

        public int OpenWriterCount { get { lock (gate) return writers.Count; } }

        /// <summary>Registration creates index metadata only; no file or directory is created.</summary>
        public void RegisterAgent(string agentId)
        {
            lock (gate) { CheckActive(); RegisterCore(agentId); }
        }

        public string RelativeDirectory(string agentId) { lock (gate) { CheckActive(); return RegisterCore(agentId).directory; } }

        /// <summary>Unknown dynamic logical IDs are registered automatically. Existing immutable profiles may be repeated identically.</summary>
        public void WriteProfile(string agentId, string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Profile JSON is required.", nameof(json));
            lock (gate)
            {
                CheckActive(); var entry = RegisterCore(agentId);
                string path = PrepareFile(entry.directory + "/profile.json");
                string contents = json.TrimEnd('\r', '\n') + "\n";
                if (File.Exists(path))
                {
                    if (File.ReadAllText(path, Utf8) == contents) return;
                    throw new IOException("An existing agent profile cannot be replaced: " + entry.directory);
                }
                using (var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), Utf8)) output.Write(contents);
            }
        }

        /// <summary>Only enabled streams supplied by the caller are written; there are no placeholder files.</summary>
        public void WriteLine(string agentId, string streamName, string json) => WriteLineAtOffset(agentId, streamName, json);

        /// <summary>Returns the exact UTF-8 byte offset before this line, including buffered writes and platform newline bytes.</summary>
        public long WriteLineAtOffset(string agentId, string streamName, string json)
        {
            if (streamName == null || !Streams.Contains(streamName)) throw new ArgumentException("Unknown per-agent JSONL stream.", nameof(streamName));
            if (string.IsNullOrWhiteSpace(json) || json.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("JSONL requires one nonempty JSON record on a single line.", nameof(json));
            lock (gate)
            {
                CheckActive(); var entry = RegisterCore(agentId);
                string relative = entry.directory + "/" + streamName;
                if (!writers.TryGetValue(relative, out var open))
                {
                    string path = PrepareFile(relative);
                    if (writers.Count >= maxOpenWriters)
                    {
                        var oldest = writers.OrderBy(pair => pair.Value.lastUse).First();
                        writers.Remove(oldest.Key); oldest.Value.Dispose();
                    }
                    var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    open = new OpenWriter { offset = file.Length, writer = new StreamWriter(file, Utf8, 32768) };
                    writers.Add(relative, open);
                }
                open.lastUse = ++useSequence;
                long offset = open.offset;
                open.writer.WriteLine(json);
                open.offset += Utf8.GetByteCount(json) + Utf8.GetByteCount(open.writer.NewLine);
                return offset;
            }
        }

        /// <summary>Append an owned binary record with the same bounded LRU used by JSONL streams.</summary>
        public long WriteBinaryAtOffset(string agentId, string streamName, byte[] header, byte[] record)
        {
            if (streamName != "skeleton-poses.bin" || header == null || header.Length == 0 || record == null || record.Length == 0)
                throw new ArgumentException("A skeleton binary header and nonempty owned record are required.");
            lock (gate)
            {
                CheckActive(); var entry = RegisterCore(agentId); string relative = entry.directory + "/" + streamName;
                if (!writers.TryGetValue(relative, out var open))
                {
                    string path = PrepareFile(relative);
                    if (writers.Count >= maxOpenWriters)
                    { var oldest = writers.OrderBy(pair => pair.Value.lastUse).First(); writers.Remove(oldest.Key); oldest.Value.Dispose(); }
                    var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 32768);
                    try
                    {
                        if (file.Length == 0) file.Write(header, 0, header.Length);
                        else
                        {
                            if (file.Length < header.Length) throw new IOException("Truncated skeleton binary header.");
                            for (int i = 0; i < header.Length; ++i) if (file.ReadByte() != header[i]) throw new IOException("Skeleton binary header changed before LRU reopen.");
                            file.Seek(0, SeekOrigin.End);
                        }
                    }
                    catch { file.Dispose(); throw; }
                    open = new OpenWriter { binary = file, offset = file.Position }; writers.Add(relative, open);
                }
                open.lastUse = ++useSequence; long offset = open.offset;
                open.binary.Write(record, 0, record.Length); open.offset += record.Length; return offset;
            }
        }

        public void Flush()
        {
            lock (gate)
            {
                CheckActive(); Exception failure = null;
                foreach (var open in writers.Values) try { open.Flush(); } catch (Exception error) { if (failure == null) failure = error; }
                try { SaveIndexCore(); } catch (Exception error) { if (failure == null) failure = error; }
                if (failure != null) throw new IOException("Agent capture flush failed.", failure);
            }
        }

        /// <summary>Rewrites the index only after registration changes; all planned identities can be indexed before their first write.</summary>
        public void SaveIndex() { lock (gate) { CheckActive(); SaveIndexCore(); } }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return; disposed = true; Exception failure = null;
                foreach (var open in writers.Values) try { open.Dispose(); } catch (Exception error) { if (failure == null) failure = error; }
                writers.Clear();
                try { SaveIndexCore(); } catch (Exception error) { if (failure == null) failure = error; }
                if (failure != null) throw new IOException("Agent capture close failed.", failure);
            }
        }

        StationDecisionAgentDirectory RegisterCore(string agentId)
        {
            if (string.IsNullOrWhiteSpace(agentId)) throw new ArgumentException("A nonempty logical agent ID is required.", nameof(agentId));
            if (agents.TryGetValue(agentId, out var existing)) return existing;
            byte[] bytes;
            try { bytes = Utf8.GetBytes(agentId); } catch (EncoderFallbackException error) { throw new ArgumentException("Agent IDs must contain valid Unicode.", nameof(agentId), error); }
            string hash;
            using (var digest = SHA256.Create()) hash = BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            var slug = new StringBuilder(56);
            foreach (char raw in agentId)
            {
                char c = raw >= 'A' && raw <= 'Z' ? (char)(raw + ('a' - 'A')) : raw;
                if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9') slug.Append(c);
                else if (slug.Length > 0 && slug[slug.Length - 1] != '-') slug.Append('-');
                if (slug.Length == 56) break;
            }
            string readable = slug.ToString().Trim('-'); if (readable.Length == 0) readable = "id";
            string directory = "agents/agent-" + readable + "-" + hash;
            if (directoryOwners.TryGetValue(directory, out var owner) && !string.Equals(owner, agentId, StringComparison.Ordinal))
                throw new IOException("Logical agent IDs collide after safe directory conversion.");
            // Validate an existing target once without creating the registered agent's directory.
            CheckPath(Path.GetFullPath(Path.Combine(rootDirectory, directory.Replace('/', Path.DirectorySeparatorChar))));
            var entry = new StationDecisionAgentDirectory { agentId = agentId, directory = directory };
            agents.Add(agentId, entry); directoryOwners.Add(directory, agentId); dirty = true;
            return entry;
        }

        void SaveIndexCore()
        {
            if (!dirty) return;
            var index = new StationDecisionAgentsIndex { runId = runId, agents = agents.Values.OrderBy(entry => entry.agentId, StringComparer.Ordinal).ToArray() };
            var json = new StringBuilder(256 + index.agents.Length * 160);
            json.Append("{\n  \"schema\":"); Quote(json, index.schema); json.Append(",\n  \"runId\":"); Quote(json, index.runId);
            json.Append(",\n  \"identityPolicy\":"); Quote(json, index.identityPolicy); json.Append(",\n  \"agents\":[");
            for (int i = 0; i < index.agents.Length; ++i)
            {
                if (i > 0) json.Append(','); json.Append("\n    {\"agentId\":"); Quote(json, index.agents[i].agentId);
                json.Append(",\"directory\":"); Quote(json, index.agents[i].directory); json.Append('}');
            }
            json.Append("\n  ]\n}\n");
            string target = PrepareFile("agents/index.json"), temporary = PrepareFile("agents/index-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read), Utf8)) output.Write(json.ToString());
                CheckPath(target);
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
                dirty = false;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        string PrepareFile(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(rootDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            CheckPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            CheckPath(path);
            return path;
        }
        void CheckPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("An agent path escaped its capture root.");
            RejectLink(rootDirectory);
            string relative = full.Substring(rootPrefix.Length); string current = rootDirectory;
            foreach (string component in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            { current = Path.Combine(current, component); RejectLink(current); }
        }
        static void RejectLink(string path)
        {
            try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Capture storage cannot traverse a symbolic link or reparse point: " + path); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
        void CheckActive() { if (disposed) throw new ObjectDisposedException(nameof(StationDecisionAgentCaptureStore)); }
        static void Quote(StringBuilder output, string value)
        {
            output.Append('"');
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"': output.Append("\\\""); break; case '\\': output.Append("\\\\"); break;
                    case '\r': output.Append("\\r"); break; case '\n': output.Append("\\n"); break; case '\t': output.Append("\\t"); break;
                    default: if (c < 32 || c > 126) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else output.Append(c); break;
                }
            }
            output.Append('"');
        }
        sealed class OpenWriter
        {
            public StreamWriter writer; public FileStream binary; public long lastUse, offset;
            public void Flush() { if (writer != null) writer.Flush(); else binary.Flush(); }
            public void Dispose() { if (writer != null) writer.Dispose(); else binary.Dispose(); }
        }
    }
}
