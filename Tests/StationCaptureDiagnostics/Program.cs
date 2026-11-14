using System.Text.Json;
using StationFlow.TrainStation.DecisionLab;
int checks = 0;
void Require(bool value, string message) { ++checks; if (!value) throw new Exception(message); }
void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Require(rejected, message); }
JsonElement Metric(JsonElement root, string name) => root.GetProperty("metrics").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);
Reject(() => new StationDecisionCaptureDiagnostics("bad", 0), "zero reservoir rejected");
Reject(() => new StationDecisionCaptureDiagnostics("bad", 65537), "oversized reservoir rejected");
var collector = new StationDecisionCaptureDiagnostics("quote\" slash\\ newline\n contrôle\u0001", 16);
collector.SetMetadata("escaped\"key", "French é\nvalue");
for (int i = 1; i <= 10; ++i) collector.Record("HALL-01", "known", "ms", i);
collector.Increment("HALL-01", "drops", 3);
using (var json = JsonDocument.Parse(collector.SnapshotJson()))
{
 var root = json.RootElement; var m = Metric(root,"known");
 Require(root.GetProperty("label").GetString() == "quote\" slash\\ newline\n contrôle\u0001", "label JSON escaping");
 Require(root.GetProperty("metadata").GetProperty("escaped\"key").GetString() == "French é\nvalue", "metadata JSON escaping");
 Require(m.GetProperty("count").GetInt64() == 10, "count");
 Require(m.GetProperty("total").GetDouble() == 55, "total");
 Require(m.GetProperty("min").GetDouble() == 1 && m.GetProperty("max").GetDouble() == 10, "extrema");
 Require(m.GetProperty("mean").GetDouble() == 5.5, "mean");
 Require(m.GetProperty("median").GetDouble() == 5.5, "even median");
 Require(m.GetProperty("p95").GetDouble() == 10, "nearest rank p95");
 Require(m.GetProperty("sampleCount").GetInt32() == 10 && !m.GetProperty("approximateQuantiles").GetBoolean(), "complete quantiles");
 Require(root.GetProperty("counters")[0].GetProperty("count").GetInt64() == 3, "counter");
}
Reject(() => collector.Record("", "bad", "ms", double.NaN), "NaN rejected");
Reject(() => collector.Record("", "bad", "ms", double.PositiveInfinity), "infinity rejected");
Parallel.For(0, 8, thread => { for (int i=0; i<1000; ++i) { collector.Record("HALL-02", "parallel", "ms", 1); collector.Increment("HALL-02", "parallel_count"); } });
using (var json = JsonDocument.Parse(collector.SnapshotJson()))
{
 var root=json.RootElement; var m=Metric(root,"parallel");
 Require(m.GetProperty("count").GetInt64()==8000 && m.GetProperty("total").GetDouble()==8000,"concurrent counts and totals");
 Require(m.GetProperty("sampleCount").GetInt32()==16 && m.GetProperty("approximateQuantiles").GetBoolean(),"bounded reservoir");
 Require(m.GetProperty("median").GetDouble()==1 && m.GetProperty("p95").GetDouble()==1,"reservoir quantiles");
 Require(root.GetProperty("counters").EnumerateArray().Single(c=>c.GetProperty("name").GetString()=="parallel_count").GetProperty("count").GetInt64()==8000,"concurrent counter");
}
Require(args.Length==1 && Path.IsPathRooted(args[0]),"absolute output arg");
string output=Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
collector.SetMetadata("captureDirectory",Path.Combine(output,"capture"));
Reject(()=>collector.FlushSnapshot("relative.json"),"relative path rejected");
Reject(()=>collector.FlushSnapshot(Path.Combine(output,"capture","diagnostic.json")),"dataset pollution rejected");
string report=Path.Combine(output,"collector-fixture.json");collector.FlushSnapshot(report);
using(var json=JsonDocument.Parse(File.ReadAllText(report))) Require(json.RootElement.GetProperty("schema").GetString()=="stationflow.capture_diagnostics.v1","written report valid");
Require(!File.Exists(Path.Combine(output,"capture","diagnostic.json")),"no dataset file created");
Console.WriteLine($"PASS: {checks} diagnostic collector checks, exact quantiles, bounded sampling, concurrency and report isolation.");
