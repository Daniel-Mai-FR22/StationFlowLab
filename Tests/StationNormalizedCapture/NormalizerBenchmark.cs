using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StationFlow.TrainStation.DecisionLab;
using J = StationFlow.TrainStation.DecisionLab.StationDecisionJsonObject;

// Pure normalizer replay: generated input is outside timing, callbacks keep byte offsets in memory.
// No Unity rendering, queue admission, JSON DTO serialization or disk I/O is inside the measured window.
static class NormalizerBenchmark
{
    const int Agents = 32, Frames = 320;
    const string SyncDefaults = "{\"kind\":null,\"detail\":null,\"activeJourneyIds\":[],\"stamp\":null}";
    sealed record Input(string Agent, string Stream, string Json);
    sealed class Sink
    {
        readonly Dictionary<string,long> offsets = new(StringComparer.Ordinal);
        public ulong Hash = 14695981039346656037UL;
        public long Lines, Bytes;
        void Observe(string path, string json)
        {
            foreach (char c in path) Hash = unchecked((Hash ^ c) * 1099511628211UL);
            Hash = unchecked((Hash ^ 0) * 1099511628211UL);
            foreach (char c in json) Hash = unchecked((Hash ^ c) * 1099511628211UL);
            ++Lines; Bytes += Encoding.UTF8.GetByteCount(json);
        }
        public void Root(string path, string json) => Observe(path,json);
        public long Agent(string id,string name,string json)
        {
            string path = id+"/"+name; offsets.TryGetValue(path,out long offset);
            Observe(path,json); offsets[path] = offset + Encoding.UTF8.GetByteCount(json) + 2; return offset;
        }
    }
    static Input[] Inputs()
    {
        var all = new List<Input>(Frames*(Agents*2+1));
        for (int sid=1;sid<=Frames;++sid)
        {
            string stamp = new J { ["runId"]=J.Quote("normalizer-benchmark"),["syncSampleId"]=J.Number(sid),["utc"]=J.Quote("2026-10-28T19:00:00Z"),
                ["unityFrame"]=J.Number(sid),["realElapsedSeconds"]="0."+sid,["simulationSeconds"]="28800.125",["paused"]=sid>160?"true":"false",["timeScale"]=sid>160?"0":"1" }.ToString();
            all.Add(new Input(null,"sync.jsonl",new J { ["kind"]=J.Quote("clock"),["detail"]="null",["activeJourneyIds"]="[]",["stamp"]=stamp }.ToString()));
            for (int agent=0;agent<Agents;++agent)
            {
                string id="voyageur-"+agent; string goal=sid<100?"café":"WC";
                var trajectory=new J { ["captureStamp"]=stamp,["agentId"]=J.Quote(id),["time"]="1.0000000000000002",["sampleId"]=J.Number(sid),
                    ["position"]="{\"x\":-0,\"y\":7.200e-018,\"z\":"+(sid>160?160:sid)+"}",["rotation"]="{\"x\":0,\"y\":0,\"z\":0,\"w\":1}",
                    ["velocity"]="{\"x\":0.1,\"y\":0,\"z\":0.2}",["budget"]="9.900000000000001",["seatedSecondsRemaining"]="0",["actorId"]=J.Quote("slot-"+agent),
                    ["groupId"]=J.Quote("groupe-"+(agent/3)),["effectiveWalkSpeed"]="0.85",["target"]=J.Quote(goal),["unknownNested"]="{\"unicode\":\"é中 🚆\",\"values\":[0,false,null]}" };
                var animation=new J { ["captureStamp"]=stamp,["agentId"]=J.Quote(id),["time"]="1.0000000000000002",["sampleId"]=J.Number(sid),
                    ["normalizedTime"]="0."+(sid>160?160:sid),["nextNormalizedTime"]="0",["transitionProgress"]="0",["nativePlanarSpeed"]="7.200e-018",
                    ["actorId"]=J.Quote("slot-"+agent),["stateHash"]="101",["animatorSpeed"]="0.85",["stateLength"]="1.3333333333333333",["playing"]="true" };
                all.Add(new Input(id,"trajectories.jsonl",trajectory.ToString())); all.Add(new Input(id,"animations.jsonl",animation.ToString()));
            }
        }
        return all.ToArray();
    }
    static object Measure(Input[] inputs, out ulong hash)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); long allocated=GC.GetAllocatedBytesForCurrentThread();
        var watch=Stopwatch.StartNew(); var sink=new Sink();
        var normalizer=new StationDecisionCaptureNormalizer("normalizer-benchmark",sink.Root,sink.Root,sink.Agent,(id,json)=>sink.Root(id+"/profile.json",json),id=>"agents/"+id);
        foreach (var row in inputs)
            if (row.Agent==null) normalizer.WriteRoot(row.Stream,row.Json,"SyncDto",SyncDefaults);
            else normalizer.WriteAgent(row.Agent,row.Stream,row.Json,row.Stream,"{}",false);
        watch.Stop(); long bytes=GC.GetAllocatedBytesForCurrentThread()-allocated; hash=sink.Hash;
        return new { elapsedMilliseconds=watch.Elapsed.TotalMilliseconds,allocatedBytes=bytes,records=inputs.Length,outputRecords=sink.Lines,outputBytes=sink.Bytes,outputHash=sink.Hash.ToString("x16") };
    }
    public static void Run(string receipt)
    {
        var inputs=Inputs(); Measure(inputs,out ulong expected); var results=new List<object>();
        for(int i=0;i<3;++i) { results.Add(Measure(inputs,out ulong actual)); if(actual!=expected)throw new Exception("Normalizer replay output changed between repetitions."); }
        string json=JsonSerializer.Serialize(new { schema="stationflow.normalizer_microbenchmark.v1",agents=Agents,frames=Frames,
            scope="Pure .NET normalizer replay; excludes rendering, capture queues, DTO serialization, disk I/O and fixture generation. 1 warmup + 3 runs.", results },new JsonSerializerOptions{WriteIndented=true});
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(receipt))); File.WriteAllText(receipt,json,new UTF8Encoding(false));Console.WriteLine(json);
    }
}
