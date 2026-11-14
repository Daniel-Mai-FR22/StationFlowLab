using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Explicit benchmark-only measurements, kept outside the normal capture contract.</summary>
    public sealed class StationDecisionCaptureDiagnostics
    {
        readonly object gate = new object();
        readonly string label, createdUtc = DateTime.UtcNow.ToString("O");
        readonly int sampleLimit;
        readonly Dictionary<string, string> metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, Metric> metrics = new Dictionary<string, Metric>(StringComparer.Ordinal);
        readonly Dictionary<string, Counter> counters = new Dictionary<string, Counter>(StringComparer.Ordinal);

        public StationDecisionCaptureDiagnostics(string label, int maxSamplesPerMetric = 4096)
        {
            if (maxSamplesPerMetric < 1 || maxSamplesPerMetric > 65536) throw new ArgumentOutOfRangeException(nameof(maxSamplesPerMetric));
            this.label = label ?? ""; sampleLimit = maxSamplesPerMetric;
        }

        public void SetMetadata(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A metadata key is required.", nameof(key));
            lock (gate) metadata[key] = value ?? "";
        }

        internal void Increment(string cameraId, string name, long amount = 1)
        {
            lock (gate)
            {
                string key = (cameraId ?? "") + "\n" + name;
                if (!counters.TryGetValue(key, out var counter)) counters.Add(key, counter = new Counter { cameraId = cameraId ?? "", name = name });
                counter.count += amount;
            }
        }

        internal void Record(string cameraId, string name, string unit, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (gate)
            {
                string key = (cameraId ?? "") + "\n" + name;
                if (!metrics.TryGetValue(key, out var metric)) metrics.Add(key, metric = new Metric(cameraId ?? "", name, unit, sampleLimit));
                metric.Add(value);
            }
        }

        internal void Elapsed(string cameraId, string name, long startedAt)
            => Record(cameraId, name, "ms", (Stopwatch.GetTimestamp() - startedAt) * (1000d / Stopwatch.Frequency));

        public string SnapshotJson()
        {
            lock (gate)
            {
                var b = new StringBuilder(8192);
                b.Append("{\n  \"schema\":\"stationflow.capture_diagnostics.v1\",\n  \"label\":"); Quote(b, label);
                b.Append(",\n  \"createdUtc\":"); Quote(b, createdUtc);
                b.Append(",\n  \"snapshotUtc\":"); Quote(b, DateTime.UtcNow.ToString("O"));
                b.Append(",\n  \"sampleLimitPerMetric\":").Append(sampleLimit).Append(",\n  \"metadata\":{");
                bool comma = false;
                foreach (var pair in metadata.OrderBy(p => p.Key, StringComparer.Ordinal))
                { if (comma) b.Append(','); Quote(b, pair.Key); b.Append(':'); Quote(b, pair.Value); comma = true; }
                b.Append("},\n  \"counters\":["); comma = false;
                foreach (var c in counters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value))
                {
                    if (comma) b.Append(','); b.Append("\n    {\"cameraId\":"); Quote(b, c.cameraId); b.Append(",\"name\":"); Quote(b, c.name);
                    b.Append(",\"count\":").Append(c.count.ToString(CultureInfo.InvariantCulture)).Append('}'); comma = true;
                }
                b.Append("\n  ],\n  \"metrics\":["); comma = false;
                foreach (var m in metrics.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value))
                {
                    if (comma) b.Append(','); b.Append("\n    {\"cameraId\":"); Quote(b, m.cameraId); b.Append(",\"name\":"); Quote(b, m.name);
                    b.Append(",\"unit\":"); Quote(b, m.unit);
                    double[] sorted = m.samples.ToArray(); Array.Sort(sorted);
                    double median = sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
                    b.Append(",\"count\":").Append(m.count.ToString(CultureInfo.InvariantCulture));
                    Number(b, "total", m.total); Number(b, "min", m.minimum); Number(b, "max", m.maximum); Number(b, "mean", m.total / m.count);
                    Number(b, "median", median); Number(b, "p95", sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * .95d) - 1)]);
                    b.Append(",\"sampleCount\":").Append(sorted.Length).Append(",\"approximateQuantiles\":").Append(m.count > sorted.Length ? "true" : "false").Append('}'); comma = true;
                }
                b.Append("\n  ],\n  \"notes\":[");
                string[] notes = {
                    "All stage timings are elapsed wall time, not OS CPU execution time. SubmitRenderRequest CPU wall time may include GPU/driver waits and is not GPU execution duration; physical GPU execution timing is unavailable.",
                    "GPU readback request-to-callback latency includes GPU work, transfer, synchronization and Unity callback scheduling; its frame gap is not a GPU-only timer.",
                    "Stage timings may overlap across cameras and threads. Do not sum them as total elapsed runtime.",
                    "JPEG and I/O wall times are measured on each camera worker; I/O includes API calls and flush to OS buffers, not proof of physical disk latency.",
                    "Queue wait measures successful packet enqueue to worker dequeue; queue depth is sampled at named pipeline events.",
                    "Counts, totals, minima and maxima cover all observations. Median/p95 use a bounded uniform reservoir after the sample limit; p95 uses nearest rank.",
                    "First-frame reference readback/orientation and AVI segment initialization/finalization are measured separately from steady-state stages.",
                    "Diagnostics add stopwatch/counter overhead only when explicitly configured. Capture defaults, queue capacity and normal loss marker text are unchanged."
                };
                for (int i = 0; i < notes.Length; ++i) { if (i > 0) b.Append(','); Quote(b, notes[i]); }
                return b.Append("]\n}\n").ToString();
            }
        }

        public void FlushSnapshot(string absoluteReportPath)
        {
            if (string.IsNullOrEmpty(absoluteReportPath) || !Path.IsPathRooted(absoluteReportPath)) throw new ArgumentException("An absolute diagnostic report path is required.", nameof(absoluteReportPath));
            string target = Path.GetFullPath(absoluteReportPath);
            lock (gate)
            {
                if (metadata.TryGetValue("captureDirectory", out var capture) && !string.IsNullOrEmpty(capture))
                {
                    string directory = Path.GetFullPath(capture).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (target.Equals(directory, StringComparison.OrdinalIgnoreCase) || target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Benchmark diagnostics must remain outside the captured dataset.", nameof(absoluteReportPath));
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(target, SnapshotJson(), new UTF8Encoding(false));
        }

        static void Number(StringBuilder b, string key, double value) { b.Append(",\"").Append(key).Append("\":").Append(value.ToString("R", CultureInfo.InvariantCulture)); }
        static void Quote(StringBuilder b, string value)
        {
            b.Append('"');
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break; case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break; case '\r': b.Append("\\r"); break; case '\t': b.Append("\\t"); break;
                    default: if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else b.Append(c); break;
                }
            }
            b.Append('"');
        }
        sealed class Counter { public string cameraId, name; public long count; }
        sealed class Metric
        {
            public readonly string cameraId, name, unit;
            public readonly List<double> samples;
            readonly Random random = new Random(1729);
            readonly int capacity;
            public long count;
            public double total, minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            public Metric(string cameraId, string name, string unit, int capacity)
            { this.cameraId = cameraId; this.name = name; this.unit = unit; this.capacity = capacity; samples = new List<double>(capacity); }
            public void Add(double value)
            {
                ++count; total += value; minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value);
                if (samples.Count < capacity) samples.Add(value);
                else { long replace = (long)(random.NextDouble() * count); if (replace < capacity) samples[(int)replace] = value; }
            }
        }
    }
}
