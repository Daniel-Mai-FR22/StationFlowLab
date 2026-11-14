using System.Text;
using System.Text.Json;
using StationFlow.TrainStation.DecisionLab;
using J = StationFlow.TrainStation.DecisionLab.StationDecisionJsonObject;

static class Program
{
    static readonly UTF8Encoding Utf8 = new(false,true);
    static int checks;
    const string Run = "core-fixture-normalized";
    static string Root;
    static Dictionary<string,List<string>> expected = new(StringComparer.Ordinal);
    static Dictionary<string,string> expectedJson = new(StringComparer.Ordinal);
    static void Assert(bool value,string message) { ++checks; if (!value) throw new Exception(message); }
    static void Throws(Action action,string message) { ++checks; try { action(); } catch (Exception e) when(e is IOException || e is InvalidDataException || e is ArgumentException || e is FormatException || e is KeyNotFoundException || e is InvalidOperationException) { return; } throw new Exception("Expected rejection: "+message); }
    static string Canonical(string raw)
    {
        raw=J.Compact(raw);
        if(raw.StartsWith("{")) { var x=J.Parse(raw); return "{"+string.Join(",",x.Keys.OrderBy(k=>k,StringComparer.Ordinal).Select(k=>J.Quote(k)+":"+Canonical(x[k])))+"}"; }
        if(raw.StartsWith("[")) return "["+string.Join(",",J.Array(raw).Select(Canonical))+"]";
        if(raw.StartsWith("\"")) return J.Quote(J.Unquote(raw));
        return raw;
    }
    static void Eq(string wanted,string actual,string message) => Assert(Canonical(wanted)==Canonical(actual),message+"\nWANTED "+wanted+"\nACTUAL "+actual);
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--benchmark") { NormalizerBenchmark.Run(args[1]); return; }
        Root=Path.GetFullPath(args.Length>0?args[0]:Path.Combine(Path.GetTempPath(),"StationNormalizedCapture-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Root);
        ParserTests(); CompactParserTests(); GenerateFixture(); CheckFixture(); CorruptionTests(); WriterIntegration();
        string receipt=Path.Combine(Path.GetDirectoryName(Root),"core-tests.json");
        File.WriteAllText(receipt,JsonSerializer.Serialize(new{success=true,checks,fixture=Root,expectedLogicalRows=expected.Sum(p=>p.Value.Count),expectedJsonDocuments=expectedJson.Count,checkpointInterval=256,independentNumericSpelling=true,unicodeAndLruOffsets=true},new JsonSerializerOptions{WriteIndented=true}),Utf8);
        Console.WriteLine(JsonSerializer.Serialize(new{success=true,checks,fixture=Root,receipt}));
    }
    static void ParserTests()
    {
        string[] bad={"", "[]", "{\"x\":1,}","{\"x\":01}","{\"x\":1e}","{\"x\":+1}","{\"x\":.1}","{\"x\":1.}","{\"x\":NaN}","{\"x\":Infinity}","{\"x\":truefalse}","{\"x\":1,\"x\":2}","{\"nested\":{\"x\":1,\"x\":2}}","{\"x\":\"bad\\q\"}","{\"x\":\"bad\nline\"}","{\"x\":[1,]}","{\"x\":null}trailing"};
        foreach(string x in bad) { Throws(()=>J.Parse(x),"invalid JSON "+x); Throws(()=>J.ParseCompact(x),"invalid compact JSON "+x); }
        string precision=" { \"a\" : -0, \"b\": 1.0000000000000002, \"c\": 7.200e-018, \"d\" : [ true , null , \"ÃƒÂ©\\n\\u4E2D\" ] } ";
        var parsed=J.Parse(precision); Assert(parsed["a"]=="-0"&&J.Compact(parsed["b"])=="1.0000000000000002"&&J.Compact(parsed["c"])=="7.200e-018","numeric spelling retained");
        Eq(precision,parsed.ToString(),"parser preserves source scalar meanings and numeric lexemes");
        foreach(string text in new[]{"", "cafÃƒÂ© Ã¤Â¸Â­Ã¦â€“â€¡", "a\tb\nc\r\"\\",char.ConvertFromUtf32(0x1F680)}) Assert(J.Unquote(J.Quote(text))==text,"string escaping roundtrip");
        Throws(()=>J.Compact(new string('[',140)+"0"+new string(']',140)),"depth bounded");
    }
    static void CompactParserTests()
    {
        (string input,string expected)[] cases = {
            (" { } ","{}"),
            (" { \"b\" : -0 , \"a\" : [ 1.0000000000000002 , 7.200e-018 , true , null ] } ","{\"b\":-0,\"a\":[1.0000000000000002,7.200e-018,true,null]}"),
            (" { \"\\u0061\" : { \"\\u0062\" : 1.0 } } ","{\"a\":{\"\\u0062\":1.0}}"),
            (" { \"quoted\\\"key\" : \"a b\\t\\n\\r\\\\\\\" é中 🚆\" } ","{\"quoted\\\"key\":\"a b\\t\\n\\r\\\\\\\" é中 🚆\"}")
        };
        foreach(var item in cases) Assert(J.ParseCompact(item.input).ToString()==item.expected,"single-validation parser exact spelling/order");
        var random=new Random(1977);
        for(int i=0;i<500;++i)
        {
            string text="é中 🚆 "+new string(Enumerable.Range(0,12).Select(_=>(char)random.Next(0,256)).ToArray());
            string key="key\"\\\t"+i; var row=new J{[key]=J.Quote(text),["numeric"]="[-0,1.0000000000000002,7.200e-018]",["nested"]="{\"first\":[false,null],\"second\":true}"};
            string raw=row.ToString();
            Assert(J.ParseCompact(DenseInput(row,17)).ToString()==raw,"random escaped strings/formatting preserve exact bytes");
            using var document=JsonDocument.Parse(raw);Assert(document.RootElement.GetProperty(key).GetString()==text,"independent string quoting/parser check");
        }
        string[] bad = { "{\"a\":0,\"\\u0061\":1}", "{\"x\":{\"a\":0,\"\\u0061\":1}}", "{\"x\":1\u00a0}", "{\"x\":-01}", "{\"x\":0x1}", "{\"x\":1e+}" };
        foreach(string input in bad) { Throws(()=>J.ParseCompact(input),"strict compact rejection"); Throws(()=>J.Parse(input),"strict public parser rejection"); }
        string atLimit="{\"x\":"+new string('[',127)+"0"+new string(']',127)+"}";
        Assert(J.ParseCompact(atLimit).ToString()==atLimit,"same depth limit accepted");
        Throws(()=>J.ParseCompact("{\"x\":"+new string('[',128)+"0"+new string(']',128)+"}"),"same depth limit rejected");
        Assert(J.Parse("{\"x\":{ \"a\" : 1 }}")["x"]=="{ \"a\" : 1 }","public Parse still preserves raw nested whitespace");
    }
    static J Stamp(int sid) => new J { ["runId"]=J.Quote(Run),["syncSampleId"]=J.Number(sid),["unityFrame"]=J.Number(100+sid/2),["realElapsedSeconds"]="0."+sid.ToString("D8"),["simulationDate"]=J.Quote("2026-10-20"),["simulationSeconds"]="28800.000000000000002",["stationClockSeconds"]="28800.000000000000002",["timeScale"]=sid==15?"0":"1",["paused"]=sid==15?"true":"false",["utc"]=J.Quote("2026-10-20T16:00:00."+sid.ToString("D7")+"Z") };
    static void Remember(string path,string row) { if(!expected.TryGetValue(path,out var list)) expected[path]=list=new(); list.Add(row); }
    static void GenerateFixture()
    {
        var writers=new Dictionary<string,StreamWriter>(StringComparer.Ordinal);
        StreamWriter Writer(string name) { if(!writers.TryGetValue(name,out var value)) writers[name]=value=new StreamWriter(new FileStream(Path.Combine(Root,name),FileMode.CreateNew,FileAccess.Write,FileShare.Read),Utf8){NewLine="\r\n"}; return value; }
        foreach(string name in new[]{"sync.jsonl","record-definitions.jsonl","rosters.jsonl","state-checkpoints.jsonl"}) Writer(name);
        using(var store=new StationDecisionAgentCaptureStore(Root,Run,2))
        {
            var n=new StationDecisionCaptureNormalizer(Run,(name,json)=>Writer(name).WriteLine(json),(name,json)=>File.WriteAllText(Path.Combine(Root,name),json,Utf8),store.WriteLineAtOffset,store.WriteProfile,store.RelativeDirectory);
            var ids=new[]{"Journey-ÃƒÂ©","journey-ÃƒÂ©","policy-change"}; foreach(string id in ids)store.RegisterAgent(id);store.SaveIndex();
            var syncDefault=new J{["kind"]="null",["detail"]="null",["activeJourneyIds"]="[]",["stamp"]="null",["measuredFrameRates"]="[]"};
            var profileDefault=new J{["captureStamp"]="null",["profile"]="null",["journey"]="null"};
            var eventDefault=new J{["captureStamp"]="null",["agentId"]="null",["kind"]="null",["profile"]="null",["activeJourneyIds"]="[]",["detail"]="null",["value"]="0"};
            var profiles=new Dictionary<string,string>();
            for(int sid=1;sid<=520;sid++)
            {
                var stamp=Stamp(sid); var clock=syncDefault.Clone();clock["kind"]=J.Quote("clock");clock["stamp"]=stamp.ToString(); n.WriteRoot("sync.jsonl",clock.ToString(),"SyncDto",syncDefault.ToString());Remember("sync.jsonl",clock.ToString());
                if(sid==1)
                {
                    foreach(string id in ids)
                    {
                        var profile=new J();int field=0;
                        foreach(var pair in StationDecisionCaptureNormalizer.JourneyAliases)
                        {
                            string value=pair.Value=="agentId"?J.Quote(id):pair.Value=="seatNumbers"?"[12,13]":pair.Value=="needs"?"{\"wc\":0.25,\"rest\":0.1}":pair.Value.Contains("Id")||pair.Value=="mobilityProfile"?J.Quote("profile-"+pair.Value):pair.Value=="clippedAtSimulationStart"||pair.Value=="alightingServiceVisitor"?"false":J.Number(++field);
                            profile[pair.Value]=value;
                        }
                        profile["policyParameters"]=id=="policy-change"?"{\"temperature\":0.8,\"weights\":[1,2,3]}":"{\"temperature\":0.7,\"weights\":[1,2,3]}";
                        profile["ageYears"]="65"; var plan=new J();foreach(var pair in StationDecisionCaptureNormalizer.JourneyAliases)plan[pair.Key]=profile[pair.Value];plan["purpose"]=J.Quote("dÃƒÂ©part");plan["publishedArrivalSeconds"]="28900";
                        var row=profileDefault.Clone();row["captureStamp"]=stamp.ToString();row["profile"]=profile.ToString();row["journey"]=plan.ToString(); n.WriteAgent(id,"profile.json",row.ToString(),"ProfileDto",profileDefault.ToString(),true); expectedJson[store.RelativeDirectory(id)+"/profile.json"]=row.ToString();profiles[id]=profile.ToString();
                    }
                    var timetable=new J{["services"]="[{\"serviceId\":\"T1\",\"departureSeconds\":30000}]",["simulationDate"]=J.Quote("2026-10-20")};
                    var timetableDefault=new J{["services"]="[]",["simulationDate"]="null"};n.WriteRoot("timetable.json",timetable.ToString(),"TimetableDto",timetableDefault.ToString());expectedJson["timetable.json"]=timetable.ToString();
                    var scenario=new J{["services"]=timetable["services"],["description"]=J.Quote("donnÃƒÂ©es exactes")};var scenarioDefault=new J{["services"]="[]",["description"]="null"};n.WriteRoot("scenario.json",scenario.ToString(),"ScenarioDto",scenarioDefault.ToString());expectedJson["scenario.json"]=scenario.ToString();
                    var empty=new J{["services"]="[]",["description"]=J.Quote("no timetable ref")};n.WriteRoot("empty-scenario.json",empty.ToString(),"ScenarioDto",scenarioDefault.ToString());expectedJson["empty-scenario.json"]=empty.ToString();
                }
                if(sid%13==1)
                {
                    string members=sid<260?"[\"Journey-ÃƒÂ©\",\"journey-ÃƒÂ©\"]":sid<390?"[]":"[\"Journey-ÃƒÂ©\",\"journey-ÃƒÂ©\"]";
                    var marker=syncDefault.Clone();marker["kind"]=J.Quote("avatar_snapshot");marker["stamp"]=stamp.ToString();marker["activeJourneyIds"]=members;n.WriteRoot("sync.jsonl",marker.ToString(),"SyncDto",syncDefault.ToString());Remember("sync.jsonl",marker.ToString());
                    var ev=eventDefault.Clone();ev["captureStamp"]=stamp.ToString();ev["kind"]=J.Quote("trajectory_sample");ev["activeJourneyIds"]=members;n.WriteRoot("events.jsonl",ev.ToString(),"EventDto",eventDefault.ToString());Remember("events.jsonl",ev.ToString());
                }
                foreach(string id in ids.Take(2))
                {
                    string folder=store.RelativeDirectory(id); string actor=sid<300?"reused-slot-1":"reused-slot-2";
                    var trajectory=new J{["captureStamp"]=stamp.ToString(),["agentId"]=J.Quote(id),["time"]="1.0000000000000002",["sampleId"]=J.Number(sid),["position"]="{\"x\":-0,\"y\":7.200e-018,\"z\":"+sid+"}",["rotation"]="{\"x\":0,\"y\":0,\"z\":0,\"w\":1}",["velocity"]="{\"x\":0.1,\"y\":0,\"z\":0.2}",["budget"]="9.900000000000001",["seatedSecondsRemaining"]="0",["actorId"]=J.Quote(actor),["groupId"]=J.Quote(sid<40?"group-A":"group-B"),["effectiveWalkSpeed"]=sid<17?"1.2":"0.85",["target"]=J.Quote(sid<300?"cafÃƒÂ©":"WC"),["unknownNested"]="{\"unicode\":\"ÃƒÂ©Ã¤Â¸Â­\",\"values\":[0,false,null]}"};
                    n.WriteAgent(id,"trajectories.jsonl",DenseInput(trajectory,sid),"TrajectoryDto","{}",false);Remember(folder+"/trajectories.jsonl",trajectory.ToString());
                    var animation=new J{["captureStamp"]=stamp.ToString(),["agentId"]=J.Quote(id),["time"]="1.0000000000000002",["sampleId"]=J.Number(sid),["normalizedTime"]="0."+sid.ToString("D5"),["nextNormalizedTime"]="0",["transitionProgress"]="0",["nativePlanarSpeed"]="7.200e-018",["locomotionStrideWeight"]="0."+sid.ToString("D5"),["adaptiveLocomotion"]="true",["actorId"]=J.Quote(actor),["stateHash"]=sid<300?"101":"102",["animatorSpeed"]=sid<17?"1":"0.85",["stateLength"]=sid<300?"1.3333333333333333":"0.7555",["playing"]="true"};
                    n.WriteAgent(id,"animations.jsonl",DenseInput(animation,sid),"AnimationDto","{}",false);Remember(folder+"/animations.jsonl",animation.ToString());
                    if(sid==1||sid==300)
                    {
                        var ev=eventDefault.Clone();ev["captureStamp"]=stamp.ToString();ev["agentId"]=J.Quote(id);ev["kind"]=J.Quote("agent-entered");ev["profile"]=profiles[id];ev["detail"]=J.Quote("cafÃƒÂ©");n.WriteAgent(id,"events.jsonl",ev.ToString(),"EventDto",eventDefault.ToString(),false);Remember(folder+"/events.jsonl",ev.ToString());
                        var decision=new J{["captureStamp"]=stamp.ToString(),["agentId"]=J.Quote(id),["input"]="{\"weights\":[1,2.0000000000000001],\"target\":\"WC\"}",["options"]="[\"Walk\",\"Wait\"]",["selection"]="1"};
                        n.WriteAgent(id,"decisions.jsonl",decision.ToString(),"DecisionDto","{\"captureStamp\":null,\"agentId\":null,\"input\":null,\"options\":[],\"selection\":0}",false);Remember(folder+"/decisions.jsonl",decision.ToString());
                    }
                }
            }
            foreach(var output in writers.Values)output.Dispose();store.Flush();store.SaveIndex();
        }
        File.WriteAllText(Path.Combine(Root,"manifest.json"),new J{["schema"]=J.Quote(StationDecisionCaptureNormalizer.CaptureSchema),["storageLayout"]=J.Quote(StationDecisionCaptureNormalizer.StorageLayout),["recordEncoding"]=J.Quote(StationDecisionCaptureNormalizer.RecordEncoding),["runId"]=J.Quote(Run),["status"]=J.Quote("complete"),["errors"]="[]",["syncSamples"]="520",["agentsDirectory"]=J.Quote("agents"),["agentIndexFile"]=J.Quote("agents/index.json"),["skeletonDefinitionsFile"]=J.Quote(""),["skeletonPoseFileName"]=J.Quote(""),["skeletonBindingsFileName"]=J.Quote(""),["skeletonIndexFileName"]=J.Quote(""),["skeletonPoseFormat"]=J.Quote(""),["captureOptions"]="{\"video\":true,\"timetable\":true,\"profiles\":true,\"trajectories\":true,\"animations\":true,\"skeletons\":false,\"decisions\":true,\"events\":true,\"videoWidth\":320,\"videoHeight\":180,\"videoFps\":30}",["selectedCameraIds"]="[\"Hall-1\"]",["cameraResults"]="[{\"cameraId\":\"Hall-1\",\"directory\":\"video/Hall-1\",\"framesWritten\":3,\"requestsDropped\":0,\"segments\":1,\"cameraStateFile\":\"camera-states.jsonl\",\"cameraStatesWritten\":2}]"}.ToString(),Utf8);
        string video=Path.Combine(Root,"video","Hall-1");Directory.CreateDirectory(video);var cameraStates=new List<string>();var cameraFrames=new List<string>();
        int revision=0;foreach(int sid in new[]{1,250,520})
        {
            if(sid!=520) { ++revision;cameraStates.Add(new J{["cameraStateId"]=J.Number(revision),["effectiveFromSyncSampleId"]=J.Number(sid),["camera"]="{\"position\":{\"x\":"+sid+",\"y\":4,\"z\":5},\"projection\":[1.0000000000000002,-0,7.200e-018]}"}.ToString()); }
            var frame=new J{["syncSampleId"]=J.Number(sid),["cameraStateId"]=J.Number(revision),["requestId"]=J.Number(sid),["videoTime"]="0.3333333333333333",["gameFps"]="29.625",["cameraFps"]="28.75",["unityFrameFps"]="30"};cameraFrames.Add(frame.ToString());frame.Remove("syncSampleId");frame.Remove("cameraStateId");frame["stamp"]=Stamp(sid).ToString();frame["camera"]=J.Parse(cameraStates.Last())["camera"];Remember("video/Hall-1/frames.jsonl",frame.ToString());
        }
        File.WriteAllText(Path.Combine(video,"calibration.json"),"{\"cameraId\":\"Hall-1\",\"width\":320,\"height\":180}",Utf8);
        File.WriteAllLines(Path.Combine(video,"camera-states.jsonl"),cameraStates,Utf8);File.WriteAllLines(Path.Combine(video,"frames.jsonl"),cameraFrames,Utf8);
        using(var output=new StreamWriter(Path.Combine(Path.GetDirectoryName(Root),"core-fixture-expected.jsonl"),false,Utf8))
        { foreach(var pair in expected) foreach(string row in pair.Value)output.WriteLine(new J{["kind"]=J.Quote("lines"),["path"]=J.Quote(pair.Key),["row"]=row}.ToString());foreach(var pair in expectedJson)output.WriteLine(new J{["kind"]=J.Quote("json"),["path"]=J.Quote(pair.Key),["row"]=pair.Value}.ToString()); }
    }
    static string DenseInput(J row, int sid)
    {
        if (sid == 257) { var reversed = new J(); foreach (string key in row.Keys.Reverse()) reversed[key] = row[key]; row = reversed; }
        string raw = row.ToString();
        if (sid % 17 != 0) return raw;
        var text = new StringBuilder(); bool quoted = false, escaped = false;
        foreach (char c in raw)
        {
            text.Append(c);
            if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; }
            else if (c == '"') quoted = true;
            else if (c == ',' || c == ':' || c == '{' || c == '[') text.Append(" \n\t");
        }
        return text.ToString();
    }
    static void CheckFixture()
    {
        var reader=new StationDecisionCaptureReader(Root);
        foreach(var pair in expected)
        { var actual=reader.ReadLines(pair.Key).ToArray();Assert(actual.Length==pair.Value.Count,"line count "+pair.Key);for(int i=0;i<actual.Length;i++)Eq(pair.Value[i],actual[i],pair.Key+" row "+i); }
        foreach(var pair in expectedJson)Eq(pair.Value,reader.ReadJson(pair.Key),"static document "+pair.Key);
        Assert(File.ReadAllLines(Path.Combine(Root,"state-checkpoints.jsonl"),Utf8).Length==12,"2 agents Ãƒâ€” 2 streams Ãƒâ€” 3 full checkpoints");
        Assert(File.ReadAllLines(Path.Combine(Root,"rosters.jsonl"),Utf8).Length==3,"roster initial/change/return revisions");
        Assert(File.ReadAllLines(Path.Combine(Root,"record-definitions.jsonl"),Utf8).Count(x=>J.Parse(x).Has("policyId"))==2,"shared exact policy definitions");
        foreach(string profile in Directory.GetFiles(Path.Combine(Root,"agents"),"profile.json",SearchOption.AllDirectories))
        { var row=J.Parse(File.ReadAllText(profile,Utf8));Assert(!J.Parse(row["profile"]).Has("policyParameters"),"policy physically shared");Assert(J.Parse(row["journey"]).Count==2,"24 profile/plan aliases physically elided"); }
        Assert(J.Parse(File.ReadAllText(Path.Combine(Root,"scenario.json"),Utf8)).Has("servicesFile"),"nonempty timetable referenced");
        Assert(!J.Parse(File.ReadAllText(Path.Combine(Root,"empty-scenario.json"),Utf8)).Has("servicesFile"),"empty timetable has no dangling reference");
        foreach(string physical in Directory.GetFiles(Path.Combine(Root,"agents"),"events.jsonl",SearchOption.AllDirectories))Assert(File.ReadAllLines(physical,Utf8).All(x=>J.Parse(x).Has("profileRef")),"event profile referenced");
        foreach(string physical in Directory.GetFiles(Path.Combine(Root,"agents"),"animations.jsonl",SearchOption.AllDirectories))
            Assert(File.ReadAllLines(physical,Utf8).All(x=>J.Parse(x).Has("locomotionStrideWeight")),"stride is a dense per-observation sample");
        foreach(string physical in Directory.GetFiles(Path.Combine(Root,"agents"),"state-changes.jsonl",SearchOption.AllDirectories))
            Assert(File.ReadAllLines(physical,Utf8).All(x=>!x.Contains("\"locomotionStrideWeight\"")),"stride changes do not create state revisions");
        Console.WriteLine("Fixture roundtrip complete: "+expected.Sum(x=>x.Value.Count)+" exact numeric-fragment rows");
    }
    static void CorruptionTests()
    {
        void Change(string path,Func<string,string> change,Action action,string title)
        { string file=Path.Combine(Root,path),old=File.ReadAllText(file,Utf8);try{File.WriteAllText(file,change(old),Utf8);Throws(action,title);}finally{File.WriteAllText(file,old,Utf8);} }
        Change("manifest.json",x=>x.Replace("station_capture.v4","station_capture.v3"),()=>new StationDecisionCaptureReader(Root),"old format rejected");
        Change("normalization.json",x=>x.Replace("per_agent.v3","per_agent.v2"),()=>new StationDecisionCaptureReader(Root),"header layout rejected");
        Change("state-checkpoints.jsonl",x=>string.Join("\n",x.Split('\n').Skip(1)),()=>new StationDecisionCaptureReader(Root),"missing checkpoint index rejected");
        Change("record-definitions.jsonl",x=>x+x.Split('\n')[0]+"\n",()=>new StationDecisionCaptureReader(Root),"duplicate definition rejected");
        Change("state-checkpoints.jsonl",x=>x.Replace("\"byteOffset\":0", "\"byteOffset\":1"),()=>new StationDecisionCaptureReader(Root),"byte offset boundary rejected");
        var index=J.Parse(File.ReadAllText(Path.Combine(Root,"agents/index.json"),Utf8));var entries=J.Array(index["agents"]).Select(J.Parse).ToArray();string first=entries[0].Text("directory"),second=entries[1].Text("directory");
        Change("state-checkpoints.jsonl",x=>x.Replace(first+"/trajectories.jsonl",second+"/trajectories.jsonl"),()=>new StationDecisionCaptureReader(Root),"cross-agent dense index rejected");
        Change(first+"/trajectories.jsonl",x=>x.Replace("\"syncSampleId\":2,","\"syncSampleId\":999999,"),()=>new StationDecisionCaptureReader(Root).ReadLines(first+"/trajectories.jsonl").ToArray(),"unknown clock/order rejected");
        Change(first+"/trajectories.jsonl",x=>x.Replace("\"stateId\":2", "\"stateId\":1"),()=>new StationDecisionCaptureReader(Root).ReadLines(first+"/trajectories.jsonl").ToArray(),"stale state rejected");
        var reader=new StationDecisionCaptureReader(Root);Throws(()=>reader.ReadJson("../outside.json"),"path traversal rejected");
        Change("video/Hall-1/frames.jsonl",x=>x.Replace("\"cameraStateId\":2", "\"cameraStateId\":1"),()=>new StationDecisionCaptureReader(Root).ReadLines("video/Hall-1/frames.jsonl").ToArray(),"stale camera state rejected");
    }
    [Serializable] sealed class StampDto { public string runId=Run;public long syncSampleId;public string utc="2026-10-20T00:00:00Z"; }
    [Serializable] sealed class SyncDto { public string kind;public StampDto stamp;public string[] activeJourneyIds=Array.Empty<string>(); }
    [Serializable] sealed class EventDto { public StampDto captureStamp;public string agentId,kind,detail;public int value; }
    static void WriterIntegration()
    {
        foreach(bool background in new[]{false,true})
        {
            string directory=Path.Combine(Path.GetDirectoryName(Root),"writer-"+(background?"background":"main")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            var options=new JsonSerializerOptions{IncludeFields=true};using(var writer=new StationDecisionDataWriter(directory,Run,(value,pretty)=>JsonSerializer.Serialize(value,value.GetType(),options),background))
            {
                writer.PrepareAgents(2,new[]{"agent-A"});writer.OpenRoot("sync.jsonl");var stamp=new StampDto{syncSampleId=1};writer.WriteRoot("sync.jsonl",new SyncDto{kind="clock",stamp=stamp});writer.WriteAgent("agent-A","events.jsonl",new EventDto{captureStamp=stamp,agentId="agent-A",kind="entered",detail="ÃƒÂ©"});writer.CompleteInitialization();Assert(File.Exists(Path.Combine(directory,"normalization.json")),"production writer always creates normalization metadata");writer.Complete();Assert(writer.CommandsAccepted==writer.CommandsCompleted&&writer.PendingCommands==0,"normalized writer drained");
            }
            File.WriteAllText(Path.Combine(directory,"manifest.json"),new J{["schema"]=J.Quote(StationDecisionCaptureNormalizer.CaptureSchema),["storageLayout"]=J.Quote(StationDecisionCaptureNormalizer.StorageLayout),["recordEncoding"]=J.Quote(StationDecisionCaptureNormalizer.RecordEncoding),["runId"]=J.Quote(Run),["skeletonDefinitionsFile"]=J.Quote(""),["skeletonPoseFileName"]=J.Quote(""),["skeletonBindingsFileName"]=J.Quote(""),["skeletonIndexFileName"]=J.Quote(""),["skeletonPoseFormat"]=J.Quote(""),["captureOptions"]="{\"skeletons\":false}"}.ToString(),Utf8);
            var reader=new StationDecisionCaptureReader(directory);var agent=J.Parse(J.Array(J.Parse(File.ReadAllText(Path.Combine(directory,"agents/index.json"),Utf8))["agents"])[0]);var row=J.Parse(reader.ReadLines(agent.Text("directory")+"/events.jsonl").Single());Assert(row.Text("agentId")=="agent-A"&&row.Text("kind")=="entered"&&J.Parse(row["captureStamp"]).Long("syncSampleId")==1,"worker encoding integration");
        }
    }
}
