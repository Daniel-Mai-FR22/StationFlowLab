using System.Globalization;
using System.Text;
using System.Text.Json;
using StationFlow.TrainStation.DecisionLab;
int checks = 0;
void Check(bool value, string why) { ++checks; if (!value) throw new Exception(why); }
void Reject(Action action, string why) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, why); }
string a = "{\"worldToCamera\":[1,0,0,0],\"nearClip\":0.300000012,\"position\":{\"x\":1.123456789,\"y\":2,\"z\":3}}";
string b = a.Replace("1.123456789", "1.123456788");
var trace = new TraceWriter();
var states = new StationDecisionCameraStateWriter();
Check(states.Reference(trace, 20, a) == 1, "first state ID");
Check(trace.Flushes == 1 && trace.Lines == 1, "state flushes before returning its reference");
Check(states.Reference(trace, 21, a) == 1 && trace.Lines == 1 && trace.Flushes == 1, "static state not repeated");
Check(states.Reference(trace, 22, b) == 2, "tiny exact exported change is not epsilon-merged");
Check(states.Reference(trace, 23, a) == 3, "revisit creates immutable new revision");
Check(states.Reference(trace, 23, a) == 3, "same clock within frame is allowed");
Check(states.Reference(trace, 24, a.Replace("0.300000012", "0.5")) == 4, "projection scalar change gets revision");
Check(states.StateCount == 4 && trace.Flushes == 4, "one state per exact change");
Reject(() => states.Reference(trace, 22, a), "backwards clock rejected");
Reject(() => states.Reference(trace, 0, a), "zero clock rejected");
Reject(() => states.Reference(trace, 25, "null"), "missing object rejected");
var failing = new StationDecisionCameraStateWriter();
bool failure = false;
try { failing.Reference(new FailingWriter(), 1, a); } catch (IOException) { failure = true; }
Check(failure && failing.StateCount == 0, "failed publication never returns a state reference");
var originalCulture = CultureInfo.CurrentCulture;
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
var firstLine = StationDecisionCameraStateWriter.FrameLine(1, 0, 0, 0, 20, 1, new StationDecisionVideoRateSample(null, null, 1));
using (var json = JsonDocument.Parse(firstLine))
{
    Check(json.RootElement.GetProperty("captureIntervalRealSeconds").ValueKind == JsonValueKind.Null, "first interval null");
    Check(json.RootElement.GetProperty("captureFpsInstantaneous").ValueKind == JsonValueKind.Null, "first FPS null");
    Check(json.RootElement.GetProperty("captureFpsRolling1s").GetDouble() == 1, "first rolling FPS retained");
    Check(!json.RootElement.TryGetProperty("stamp", out _) && !json.RootElement.TryGetProperty("camera", out _), "large inline fields removed");
    Check(json.RootElement.EnumerateObject().Count() == 9, "all nine compact frame fields retained");
}
var epochOutput = new TraceWriter();
var sequence = new StationDecisionCameraStateWriter();
var expectedStates = new Dictionary<long, string>();
var rates = new StationDecisionVideoRateTracker();
long previousId = 0;
double elapsed = 7.1234567890123;
for (int i = 0; i < 3000; ++i)
{
    // Segment rotation, dropped request IDs and a clock gap do not reset real-time FPS history.
    elapsed += i == 1600 ? 3.333333333333 : .017 + (i % 9) * .001;
    long syncId = 101 + i * 2, requestId = i * 3L;
    string camera = ((i / 251) % 2 == 0) ? a : b;
    long id = sequence.Reference(epochOutput, syncId, camera);
    if (id != previousId) { expectedStates.Add(id, camera); previousId = id; }
    var rate = rates.Add(elapsed);
    int segment = i / 701 + 1, frame = i % 701;
    string line = StationDecisionCameraStateWriter.FrameLine(segment, frame, frame / 30d, requestId, syncId, id, rate);
    using var json = JsonDocument.Parse(line);
    var row = json.RootElement;
    Check(row.GetProperty("syncSampleId").GetInt64() == syncId, "exact clock reference");
    Check(expectedStates[row.GetProperty("cameraStateId").GetInt64()] == camera, "exact immutable geometry reconstruction");
    Check(row.GetProperty("segment").GetInt32() == segment && row.GetProperty("videoFrame").GetInt32() == frame, "segment frame identity");
    Check(row.GetProperty("requestId").GetInt64() == requestId, "dropped requests remain distinguishable");
    Check(row.GetProperty("videoTimeSeconds").GetDouble() == frame / 30d, "nominal playback time retained");
    Check(row.GetProperty("captureFpsRolling1s").GetDouble() == rate.rollingFps1s, "variable rolling FPS exact");
    Check(i == 0 ? row.GetProperty("captureFpsInstantaneous").ValueKind == JsonValueKind.Null : row.GetProperty("captureFpsInstantaneous").GetDouble() == rate.fpsInstantaneous, "instantaneous FPS exact");
    Check(i == 0 ? row.GetProperty("captureIntervalRealSeconds").ValueKind == JsonValueKind.Null : row.GetProperty("captureIntervalRealSeconds").GetDouble() == rate.intervalRealSeconds, "interval exact");
}
var stateRows = epochOutput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
Check(stateRows.Length == expectedStates.Count && stateRows.Length == 12, "3000 observations retain only12 change states");
long priorEffective = 0;
for (int i = 0; i < stateRows.Length; ++i)
{
    using var json = JsonDocument.Parse(stateRows[i]);
    var row = json.RootElement;
    Check(row.GetProperty("cameraStateId").GetInt64() == i + 1, "epochs sequential");
    long effective = row.GetProperty("effectiveFromSyncSampleId").GetInt64();
    Check(effective > priorEffective, "epoch effective clock ordered");
    Check(row.GetProperty("camera").GetRawText() == expectedStates[i + 1], "raw geometry decimals untouched");
    priorEffective = effective;
}
Reject(() => StationDecisionCameraStateWriter.FrameLine(0, 0, 0, 0, 1, 1, default), "invalid segment rejected");
Reject(() => StationDecisionCameraStateWriter.FrameLine(1, 0, double.NaN, 0, 1, 1, default), "invalid JSON number rejected");
CultureInfo.CurrentCulture = originalCulture;
Console.WriteLine(JsonSerializer.Serialize(new { success = true, checks, frames = 3000, cameraStates = stateRows.Length, variableFpsFieldsRetained = true, exactDecimals = true, publicationFailure = "rejected before reference", source = "StationDecisionCameraStateWriter.cs" }));
sealed class TraceWriter : StringWriter
{
    public int Flushes, Lines;
    public override void WriteLine(string value) { base.WriteLine(value); ++Lines; }
    public override void Flush() { ++Flushes; base.Flush(); }
}
sealed class FailingWriter : StringWriter { public override void Flush() => throw new IOException("intentional flush failure"); }
