using System.Diagnostics;
using System.Text.Json;
using StationFlow.TrainStation.DecisionLab;

int checks = 0;
void Check(bool value, string message) { ++checks; if (!value) throw new Exception(message); }
void Reject<T>(Action action, string message) where T : Exception { bool rejected=false; try { action(); } catch(T) { rejected=true; } Check(rejected,message); }
Check(args.Length == 1 && Path.IsPathRooted(args[0]), "absolute fixture directory");
string fixture=Path.GetFullPath(args[0]); Directory.CreateDirectory(fixture);
var options=new JsonSerializerOptions { IncludeFields=true };
string Serialize(object value,bool pretty) => JsonSerializer.Serialize(value,value.GetType(),options);

var original=new Record { ordinal=17, stamp=new Stamp { syncSampleId=938, realElapsedSeconds=1.234567890123, paused=true }, needs=new Need { thirst=.73f }, ids=new[]{"voyageur-\u00e9\u6f22", "B"}, seats=new[]{7,8}, ignored=new UnityEngine.Object() };
object boxed=original;
var frozen=(Record)StationDecisionCaptureSnapshot.Copy(boxed,out long frozenBytes);
original.stamp.syncSampleId=44;original.needs.thirst=0;original.ids[0]="changed";original.seats[0]=100;
Check(frozen.ordinal==17 && frozen.stamp.syncSampleId==938,"object-typed concrete DTO retains all fields and exact stamp");
Check(frozen.needs.thirst==.73f && frozen.ids[0]=="voyageur-\u00e9\u6f22" && frozen.seats[0]==7,"all mutable nested DTOs and arrays detached");
Check(frozen.ignored==null && frozenBytes>128,"NonSerialized engine handles never cross worker boundary");
Reject<ArgumentException>(()=>StationDecisionCaptureSnapshot.Copy(new HasEngine(),out _),"engine object field rejected");
Reject<ArgumentException>(()=>StationDecisionCaptureSnapshot.Copy(new HasList(),out _),"unsupported List container rejected, never silently empty");
Reject<ArgumentException>(()=>StationDecisionCaptureSnapshot.Copy(new HasObject { value=new UnityEngine.Object() },out _),"object/polymorphic engine payload rejected");
Reject<ArgumentException>(()=>StationDecisionCaptureSnapshot.Copy(new HasPrivateSerializedField(),out _),"private Unity serialized fields rejected");
Reject<ArgumentException>(()=>StationDecisionCaptureSnapshot.Copy(new HasSerializeReference(),out _),"Unity SerializeReference fields rejected");
Reject<ArgumentException>(()=>new StationDecisionDataWriter("relative","run",Serialize),"relative capture root rejected");
Reject<ArgumentException>(()=>new StationDecisionDataWriter(fixture," ",Serialize),"empty run ID rejected");
Reject<ArgumentOutOfRangeException>(()=>new StationDecisionDataWriter(fixture,"run",Serialize,maxPendingCommands:0),"zero queue bound rejected");

string[] modes={"main","background"};
foreach(string mode in modes)
{
    string path=Path.Combine(fixture,"Parity-"+mode);Directory.CreateDirectory(path);
    int caller=Environment.CurrentManagedThreadId;var threads=new HashSet<int>();
    var writer=new StationDecisionDataWriter(path,"run",(v,p)=>{lock(threads)threads.Add(Environment.CurrentManagedThreadId);return Serialize(v,p);},mode=="background");
    writer.OpenRoot("sync.jsonl");writer.OpenRoot("events.jsonl");writer.PrepareAgents(2,new[]{"voyageur-\u00e9\u6f22","B"});
    for(int i=0;i<3000;++i)
    {
        var record=new Record { kind="clock",ordinal=i,stamp=new Stamp {syncSampleId=10000+i,realElapsedSeconds=i*.012345678901},ids=new[]{"A","B"},seats=new[]{i},needs=new Need {thirst=i*.001f} };
        writer.WriteRoot("sync.jsonl",record,retainedBytes:1024);
        writer.WriteAgent(i%2==0?"voyageur-\u00e9\u6f22":"B","decisions.jsonl",record,retainedBytes:1024);
        if(i%100==0)writer.Flush();
    }
    writer.Complete();writer.Complete();
    Check(writer.PendingCommands==0 && writer.PendingBytes==0,"Stop drains every byte and command: "+mode);
    Check(writer.CommandsAccepted==writer.CommandsCompleted,"all accepted commands complete: "+mode);
    Check(threads.Count==1 && threads.Contains(caller)==(mode=="main"),"JSON serialization runs on expected thread: "+mode);
    string[] lines=File.ReadAllLines(Path.Combine(path,"sync.jsonl"));Check(lines.Length==3000,"stress loses no root lines: "+mode);
    for(int i=0;i<lines.Length;++i) {var record=JsonSerializer.Deserialize<Record>(lines[i],options);Check(record.ordinal==i && record.stamp.syncSampleId==10000+i && record.stamp.realElapsedSeconds==i*.012345678901,"FIFO and original high precision stamps: "+mode);}
    File.WriteAllText(Path.Combine(path,"manifest.json"),JsonSerializer.Serialize(new {schema=StationDecisionCaptureNormalizer.CaptureSchema,storageLayout=StationDecisionCaptureNormalizer.StorageLayout,recordEncoding=StationDecisionCaptureNormalizer.RecordEncoding,runId="run",captureOptions=new{skeletons=false},skeletonDefinitionsFile="",skeletonPoseFileName="",skeletonBindingsFileName="",skeletonIndexFileName="",skeletonPoseFormat=""}),new System.Text.UTF8Encoding(false));
    var logicalReader=new StationDecisionCaptureReader(path);
    using var index=JsonDocument.Parse(File.ReadAllText(Path.Combine(path,"agents","index.json")));
    foreach(var agent in index.RootElement.GetProperty("agents").EnumerateArray())
    {
        string id=agent.GetProperty("agentId").GetString();string sub=agent.GetProperty("directory").GetString();
        string[] individual=logicalReader.ReadLines(sub+"/decisions.jsonl").ToArray();Check(individual.Length==1500,"per-agent layout loses no records");
        for(int i=0;i<individual.Length;++i)Check(JsonSerializer.Deserialize<Record>(individual[i],options).ordinal==i*2+(id=="B"?1:0),"per-agent FIFO survives LRU reopen");
    }
    Reject<ObjectDisposedException>(()=>writer.WriteRoot("sync.jsonl",frozen),"writes after Stop rejected");
}
string[] mainLines=File.ReadAllLines(Path.Combine(fixture,"Parity-main","sync.jsonl")), backgroundLines=File.ReadAllLines(Path.Combine(fixture,"Parity-background","sync.jsonl"));
Check(mainLines.SequenceEqual(backgroundLines),"main/background produce byte-identical root JSON");

// Batching changes scheduling only: common clocks, definitions and agent records remain byte-identical.
foreach(string mode in new[]{"unbatched", "batch-main", "batch-background"})
{
    string path=Path.Combine(fixture,"BatchParity-"+mode);Directory.CreateDirectory(path);
    var writer=new StationDecisionDataWriter(path,"run",Serialize,mode!="batch-main");
    writer.OpenRoot("sync.jsonl");writer.PrepareAgents(2,new[]{"A","B"});
    for(int i=0;i<150;++i)
    {
        using var batch=mode=="unbatched"?null:writer.BeginBatch();
        Check(writer.HasActiveBatch==(batch!=null),"producer scope visible during batch assembly");
        var record=new Record {kind="clock",ordinal=i,stamp=new Stamp {syncSampleId=10000+i,realElapsedSeconds=i*.03125},ids=new[]{"A","B"}};
        writer.WriteRoot("sync.jsonl",record,retainedBytes:1024);
        writer.WriteAgent("A","decisions.jsonl",record,retainedBytes:512);
        writer.WriteAgent("B","decisions.jsonl",record,retainedBytes:512);
        if(batch!=null)
        {
            Check(batch.RetainedBytes==StationDecisionDataWriter.BatchOverheadBytes+2048+3*StationDecisionDataWriter.BatchRecordOverheadBytes,"retained group memory includes all DTOs and action overhead");
            batch.Commit();Check(!writer.HasActiveBatch,"commit ends producer scope");
        }
        if(i%11==0)writer.Flush();
    }
    writer.Complete();
    Check(writer.RecordsAccepted==450 && writer.RecordsCompleted==450,"logical record metrics independent of grouping");
    Check(writer.CommandsAccepted==writer.CommandsCompleted && writer.PendingBytes==0 && writer.PendingCommands==0,"batch final accounting drains");
    if(mode!="unbatched")Check(writer.CommandsAccepted<200,"one FIFO command per three-record snapshot");
}
string parityRoot=Path.Combine(fixture,"BatchParity-unbatched");
foreach(string name in Directory.GetFiles(parityRoot,"*",SearchOption.AllDirectories).Select(file=>Path.GetRelativePath(parityRoot,file)))
    foreach(string mode in new[]{"batch-main","batch-background"})
        Check(File.ReadAllBytes(Path.Combine(parityRoot,name)).SequenceEqual(File.ReadAllBytes(Path.Combine(fixture,"BatchParity-"+mode,name))),"batched main/background byte parity: "+name);

// A blocked worker makes repeated flush requests and assembly accounting observable without scheduler races.
{
    string path=Path.Combine(fixture,"BatchFlush");Directory.CreateDirectory(path);
    using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
    var writer=new StationDecisionDataWriter(path,"run",(v,p)=>{if(((Record)v).ordinal==0){entered.Set();release.Wait();}return Serialize(v,p);},maxPendingBytes:4096);
    writer.WriteRoot("sync.jsonl",new Record {ordinal=0},retainedBytes:256);Check(entered.Wait(10000),"batch flush worker blocked");
    for(int i=0;i<50;++i)writer.Flush();
    Check(writer.CommandsAccepted==2 && writer.PendingCommands==2,"fifty flush requests coalesce into one waiting command");
    using(var batch=writer.BeginBatch())
    {
        writer.WriteRoot("sync.jsonl",new Record {ordinal=1},retainedBytes:256);
        writer.WriteRoot("sync.jsonl",new Record {ordinal=2},retainedBytes:256);
        Check(writer.PendingCommands==3 && writer.PendingBytes==1280,"unfinished producer group already counts against bounded pending memory");
        Check(writer.CommandsAccepted==2 && writer.RecordsAccepted==1,"uncommitted records not claimed accepted");
        Reject<InvalidOperationException>(()=>writer.BeginBatch(),"nested producer batch rejected");
        batch.Commit();Check(writer.CommandsAccepted==3 && writer.RecordsAccepted==3,"commit atomically accepts complete FIFO group");
    }
    release.Set();writer.Complete();
    Check(File.ReadAllLines(Path.Combine(path,"sync.jsonl")).Select(line=>JsonSerializer.Deserialize<Record>(line,options).ordinal).SequenceEqual(new[]{0,1,2}),"coalesced flush retains ordered records");
    Check(writer.RecordsCompleted==3 && writer.CommandsAccepted==writer.CommandsCompleted,"coalescing leaves exact completion accounting");
}

// Overflow during assembly never accepts a partial snapshot, and never exceeds the original byte bound.
{
    string path=Path.Combine(fixture,"BatchAssemblyOverload");Directory.CreateDirectory(path);
    using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
    var writer=new StationDecisionDataWriter(path,"run",(v,p)=>{entered.Set();release.Wait();return Serialize(v,p);},maxPendingBytes:1024);
    writer.WriteRoot("sync.jsonl",new Record {ordinal=0},retainedBytes:256);Check(entered.Wait(10000),"assembly overflow worker blocked");
    using(var batch=writer.BeginBatch())
    {
        writer.WriteRoot("sync.jsonl",new Record {ordinal=1},retainedBytes:128);
        writer.WriteRoot("sync.jsonl",new Record {ordinal=2},retainedBytes:128);
        Reject<IOException>(()=>writer.WriteRoot("sync.jsonl",new Record {ordinal=3},retainedBytes:128),"oversize assembly explicitly fails instead of retaining extra queue memory");
        Check(!writer.HasActiveBatch && writer.PendingBytes==256 && writer.PendingCommands==1,"failed group releases all staged references and accounting");
    }
    release.Set();Reject<IOException>(()=>writer.Complete(),"assembly failure remains explicit at completion");
    Check(writer.HighWaterBytes<=1024 && writer.RecordsAccepted==1 && writer.RecordsCompleted==1,"no partial group accepted and bound unchanged");
    Check(File.ReadAllLines(Path.Combine(path,"sync.jsonl")).Length==1,"only earlier accepted data is drained");
}
foreach(bool background in new[]{false,true})
{
    string path=Path.Combine(fixture,"BatchFailure-"+background);Directory.CreateDirectory(path);
    var writer=new StationDecisionDataWriter(path,"run",(v,p)=>{if(((Record)v).ordinal==1)throw new IOException("batch record failure");return Serialize(v,p);},background);
    using(var empty=writer.BeginBatch()){} // Cancellation before any authoring is harmless.
    Check(!writer.HasActiveBatch && writer.PendingBytes==0,"empty scope cancels without leaking capacity");
    using(var batch=writer.BeginBatch())
    {
        for(int i=0;i<3;++i)writer.WriteRoot("sync.jsonl",new Record {ordinal=i},retainedBytes:256);
        if(background)batch.Commit();else Reject<IOException>(()=>batch.Commit(),"main batch failure reaches caller");
    }
    Reject<IOException>(()=>writer.Complete(),"partial serializer failure explicit at completion");
    Check(writer.RecordsAccepted==3 && writer.RecordsCompleted==2 && writer.CommandsCompleted==0,"logical records expose partial failed command accurately");
    Check(File.ReadAllLines(Path.Combine(path,"sync.jsonl")).Length==2,"accepted suffix still drained after failed record inside group");
    Check(writer.PendingBytes==0 && writer.PendingCommands==0,"failed group releases complete retained budget");
}
{
    string path=Path.Combine(fixture,"AbandonedBatch");Directory.CreateDirectory(path);
    var writer=new StationDecisionDataWriter(path,"run",Serialize);
    using(var batch=writer.BeginBatch())writer.WriteRoot("sync.jsonl",new Record {ordinal=1});
    Reject<IOException>(()=>writer.Complete(),"abandoned authored snapshot cannot silently become a complete session");
    Check(writer.PendingBytes==0 && writer.RecordsAccepted==0,"abandoned group releases memory without claiming accepted records");
}

// Reserve before authoring: denial is a nonfatal, side-effect-free admission decision.
{
    string path=Path.Combine(fixture,"ReservedSkeleton");Directory.CreateDirectory(path);
    using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
    var writer=new StationDecisionDataWriter(path,"run",(v,p)=>{if(v is Record row && row.ordinal==0){entered.Set();release.Wait();}return Serialize(v,p);},maxPendingCommands:8,maxPendingBytes:8192);
    writer.PrepareAgents(2,new[]{"A"});writer.CompleteInitialization();
    writer.WriteRoot("sync.jsonl",new Record {kind="clock",ordinal=0,stamp=new Stamp {syncSampleId=1}},retainedBytes:256);
    Check(entered.Wait(10000),"reserved skeleton worker blocked before clock processing");
    bool authored=false;
    if(writer.TryBeginReservedBatch(8192,2048,out var denied,2)){authored=true;denied.Dispose();}
    Check(!authored && denied==null && writer.Failure==null && !writer.HasActiveBatch,"capacity denial cannot mutate clock or skeleton producer caches");
    Check(writer.PendingBytes==256 && writer.PendingCommands==1,"denial leaves pending accounting unchanged");
    Check(writer.MaxPendingBytes==8192 && writer.MaxPendingCommands==8,"configured queue limits exposed read-only");
    Check(writer.TryBeginReservedBatch(4096,2048,out var batch,2),"whole clock and skeleton snapshot admitted atomically");
    using(batch)
    {
        Check(batch.ReservedBytes==4096 && batch.RetainedBytes==256 && writer.PendingBytes==4352,"full budget held before authoring any data");
        writer.WriteRoot("sync.jsonl",new Record {kind="clock",ordinal=1,stamp=new Stamp {syncSampleId=2}},retainedBytes:256);
        var rig=new StationDecisionSkeletonDefinition {jointNames=new[]{"Hips"},transformPaths=new[]{"Armature/Hips"},parentIndices=new[]{-1}};
        rig.rigId=StationDecisionSkeletonBinary.ComputeRigId(rig);
        writer.WriteSkeletonDefinition(rig);
        writer.WriteSkeletonBinding("A",new StationDecisionSkeletonBinding {bindingId=1,rigId=rig.rigId,actorSlotId="slot-A",avatarName="test",effectiveFromSyncSampleId=2});
        writer.WriteSkeletonPose("A",new StationDecisionSkeletonPose {syncSampleId=2,sampleId=1,bindingId=1,jointCount=1,validMask=new byte[]{1},positionsWorld=new float[]{1,2,3}});
        Check(batch.RetainedBytes==3597 && writer.PendingBytes==4352,"actual snapshot remains within fixed held budget");
        // An independent mandatory producer consumes protected capacity while the optional scope is still open.
        Task.Run(()=>writer.WriteRoot("events.jsonl",new Record {ordinal=99},retainedBytes:2048)).GetAwaiter().GetResult();
        Check(writer.PendingBytes==6400 && writer.PendingCommands==3,"mandatory records can use headroom while optional bytes are reserved");
        batch.Commit();
        Check(writer.PendingBytes==5901 && writer.CommandsAccepted==5,"commit releases only unused slack and needs no capacity retry");
    }
    Check(!writer.TryBeginReservedBatch(4096,2048,out var stillDenied,2) && stillDenied==null && writer.Failure==null,"subsequent pressure rejects only optional capture");
    release.Set();Check(SpinWait.SpinUntil(()=>writer.PendingCommands==0,10000),"reservation and mandatory records drain normally");
    Check(writer.TryBeginReservedBatch(4096,2048,out var reopened,2),"optional admission reopens automatically after pressure drains");
    reopened.Dispose();writer.Complete();
    Check(writer.HighWaterBytes<=8192 && writer.PendingBytes==0,"reservation never raises queue memory bounds");
    Check(writer.RecordsAccepted==6 && writer.RecordsCompleted==6,"all admitted clock, event and skeleton records complete");
    Check(File.ReadAllLines(Path.Combine(path,"sync.jsonl")).Length==2 && File.ReadAllLines(Path.Combine(path,"skeleton-definitions.jsonl")).Length==1,"denied admission writes no partial clock or rig definition");
    string[] poses=Directory.GetFiles(path,"skeleton-poses.bin",SearchOption.AllDirectories),bindings=Directory.GetFiles(path,"skeleton-bindings.jsonl",SearchOption.AllDirectories);
    Check(poses.Length==1 && new FileInfo(poses[0]).Length==61 && bindings.Length==1 && File.ReadAllLines(bindings[0]).Length==1,"committed pose retains complete rig, binding and clock dependencies");
}
{
    string path=Path.Combine(fixture,"ReservedCommandHeadroom");Directory.CreateDirectory(path);
    using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
    var writer=new StationDecisionDataWriter(path,"run",(v,p)=>{entered.Set();release.Wait();return Serialize(v,p);},maxPendingCommands:4,maxPendingBytes:4096);
    writer.WriteRoot("sync.jsonl",new Record(),retainedBytes:256);Check(entered.Wait(10000),"command headroom worker blocked");
    Check(!writer.TryBeginReservedBatch(512,1024,out _,3) && writer.Failure==null,"command headroom rejects before hard command limit");
    Check(writer.TryBeginReservedBatch(512,1024,out var batch,2),"reservation retains two mandatory command slots");
    using(batch)
    {
        Task.Run(()=>{writer.WriteRoot("sync.jsonl",new Record {ordinal=1},retainedBytes:256);writer.WriteRoot("sync.jsonl",new Record {ordinal=2},retainedBytes:256);}).GetAwaiter().GetResult();
        Check(writer.PendingCommands==4 && writer.PendingBytes==1280,"mandatory writes consume protected command headroom");
    }
    release.Set();writer.Complete();Check(writer.RecordsCompleted==3 && writer.PendingBytes==0,"empty reservation cancels without dropping mandatory writes");
}
foreach(bool background in new[]{false,true})
{
    string path=Path.Combine(fixture,"ReservedUnderestimate-"+background);Directory.CreateDirectory(path);
    var writer=new StationDecisionDataWriter(path,"run",Serialize,background,maxPendingBytes:2048);
    Check(writer.TryBeginReservedBatch(1024,512,out var batch),"underestimate fixture reservation admitted");
    using(batch)
    {
        writer.WriteRoot("sync.jsonl",new Record {kind="clock",stamp=new Stamp {syncSampleId=1}},retainedBytes:256);
        Reject<IOException>(()=>writer.WriteRoot("sync.jsonl",new Record {ordinal=1},retainedBytes:512),"underestimated snapshot fails explicitly before any partial commit");
    }
    Reject<IOException>(()=>writer.Complete(),"budget underestimation remains visible at finalization");
    Check(writer.PendingBytes==0 && writer.PendingCommands==0 && writer.RecordsAccepted==0 && writer.HighWaterBytes==1024,"underestimate releases entire reservation without growing bounds");
    Check(!File.Exists(Path.Combine(path,"sync.jsonl")),"uncommitted clock reference never reaches output after underestimation");
}
foreach(bool background in new[]{false,true})
    foreach(bool authored in new[]{false,true})
    {
        string path=Path.Combine(fixture,"ReservedCompletion-"+background+"-"+authored);Directory.CreateDirectory(path);
        var writer=new StationDecisionDataWriter(path,"run",Serialize,background);
        Check(writer.TryBeginReservedBatch(2048,1024,out var batch),"completion fixture reservation admitted");
        if(authored)writer.WriteRoot("sync.jsonl",new Record(),retainedBytes:256);
        writer.BeginComplete();
        Check(SpinWait.SpinUntil(()=>writer.IsCompletionFinished,10000),"completion cannot hang on an uncommitted reservation");
        if(authored)Reject<IOException>(()=>writer.TryComplete(),"completion with authored uncommitted state is explicitly incomplete");
        else Check(writer.TryComplete(),"empty reservation cancels during normal completion");
        batch.Dispose();Check(writer.PendingBytes==0 && writer.PendingCommands==0 && !writer.HasActiveBatch,"completion releases reserved accounting and references");
    }
{
    string path=Path.Combine(fixture,"ReservedValidation");Directory.CreateDirectory(path);
    using var writer=new StationDecisionDataWriter(path,"run",Serialize);
    Reject<ArgumentOutOfRangeException>(()=>writer.TryBeginReservedBatch(0,0,out _),"too-small reserve budget rejected");
    Reject<ArgumentOutOfRangeException>(()=>writer.TryBeginReservedBatch(512,-1,out _),"negative byte headroom rejected");
    Reject<ArgumentOutOfRangeException>(()=>writer.TryBeginReservedBatch(512,0,out _,-1),"negative command headroom rejected");
    Check(!writer.TryBeginReservedBatch(long.MaxValue,0,out _) && writer.Failure==null,"oversized optional budget returns false without arithmetic overflow");
}

// Startup profile I/O must finish before simulation/video admission. This is not a live-frame queue wait.
foreach(bool failStartup in new[]{false,true})
{
    string path=Path.Combine(fixture,"Initialization-"+failStartup);Directory.CreateDirectory(path);
    using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
    var writer=new StationDecisionDataWriter(path,"run",(v,p)=>{entered.Set();release.Wait();if(failStartup)throw new IOException("startup failure");return Serialize(v,p);});
    writer.WriteRoot("sync.jsonl",new Record {ordinal=10});Check(entered.Wait(10000),"initialization worker entered gate");
    var ready=Task.Run(()=>{try{writer.CompleteInitialization();return true;}catch(IOException){return false;}});
    Check(!ready.Wait(30),"initialization fence cannot return before accepted startup writes finish");
    release.Set();Check(ready.Wait(10000) && ready.Result==!failStartup,"initialization fence exposes success or injected failure");
    if(failStartup)Reject<IOException>(()=>writer.Complete(),"failed startup can still Stop/drain safely");
    else
    {
        using(var file=new FileStream(Path.Combine(path,"sync.jsonl"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
        using(var reader=new StreamReader(file))Check(reader.ReadToEnd().Contains("10"),"initialization fence flushes output before returning");
        writer.WriteRoot("sync.jsonl",new Record {ordinal=11});writer.Complete();
        Check(File.ReadAllLines(Path.Combine(path,"sync.jsonl")).Length==2,"live records can follow completed initialization without backlog loss");
    }
}

// Completion admission/polling must remain responsive while accepted writes are deliberately blocked.
foreach(bool failAsync in new[]{false,true})
{
    string path=Path.Combine(fixture,"AsyncCompletion-"+failAsync);Directory.CreateDirectory(path);
    using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
    var writer=new StationDecisionDataWriter(path,"run",(v,p)=>{entered.Set();release.Wait();if(failAsync)throw new IOException("async injected failure");return Serialize(v,p);});
    writer.WriteRoot("sync.jsonl",new Record {ordinal=25});Check(entered.Wait(10000),"async worker held at actual serialization");
    var watch=Stopwatch.StartNew();writer.BeginComplete();
    Check(watch.Elapsed.TotalMilliseconds<250,"BeginComplete does not wait for blocked serializer");
    Check(!writer.IsCompletionFinished && !writer.TryComplete(),"poll stays pending until accepted writes and closure finish");
    Reject<ObjectDisposedException>(()=>writer.WriteRoot("sync.jsonl",new Record()),"async completion closes admission immediately");
    release.Set();Check(SpinWait.SpinUntil(()=>writer.IsCompletionFinished,10000),"worker independently publishes completion");
    if(failAsync)Reject<IOException>(()=>writer.TryComplete(),"poll reports failure after drain without hanging");
    else Check(writer.TryComplete() && writer.TryComplete(),"completion polling is idempotent");
    Check(writer.PendingCommands==0 && writer.PendingBytes==0,"async finalization drains accepted accounting");
    using(var exclusive=new FileStream(Path.Combine(path,"record-definitions.jsonl"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        Check(exclusive.CanRead,"completion guarantees all owned file handles closed");
    if(failAsync)Reject<IOException>(()=>writer.Complete(),"terminal barrier retains original failure");else writer.Complete();
}

// Gate the serializer deterministically, exercise the in-flight bounds without sleeps or scheduler assumptions.
foreach(bool byteBound in new[]{false,true})
{
    string path=Path.Combine(fixture,"Overload-"+byteBound);Directory.CreateDirectory(path);
    using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
    var writer=new StationDecisionDataWriter(path,"run",(v,p)=>{if(((Record)v).ordinal==0){entered.Set();release.Wait();}return Serialize(v,p);},
        maxPendingCommands:byteBound?8:2,maxPendingBytes:byteBound?512:100000);
    writer.WriteRoot("sync.jsonl",new Record {ordinal=0},retainedBytes:256);
    Check(entered.Wait(10000),"worker reached serializer gate");
    writer.WriteRoot("sync.jsonl",new Record {ordinal=1},retainedBytes:256);
    var watch=Stopwatch.StartNew();Reject<IOException>(()=>writer.WriteRoot("sync.jsonl",new Record {ordinal=2},retainedBytes:256),"overload visibly fails session");
    Check(watch.Elapsed.TotalSeconds<1,"overload does not wait for worker capacity");
    Check(writer.PendingCommands==2 && writer.PendingBytes==512,"in-flight item is included in pending limits");
    var closing=Task.Run(()=>{try{writer.Complete();return false;}catch(IOException){return true;}});
    Check(!closing.IsCompleted,"Stop barrier waits for accepted work");release.Set();Check(closing.Wait(10000) && closing.Result,"Stop reports overload after draining accepted commands");
    Check(writer.CommandsAccepted==2 && writer.CommandsCompleted==2 && writer.PendingCommands==0,"overload retains and completes previously accepted commands");
    Check(File.ReadAllLines(Path.Combine(path,"sync.jsonl")).Length==2,"overload does not silently discard accepted suffix");
}

foreach(bool background in new[]{false,true})
{
    string path=Path.Combine(fixture,"Failure-"+background);Directory.CreateDirectory(path);
    var writer=new StationDecisionDataWriter(path,"run",(v,p)=>throw new IOException("injected serialization failure"),background);
    if(background) writer.WriteRoot("sync.jsonl",new Record());else Reject<IOException>(()=>writer.WriteRoot("sync.jsonl",new Record()),"main serializer error visible");
    Reject<IOException>(()=>writer.Complete(),"worker/main serializer failure reaches Stop");
    Check(writer.Failure!=null && writer.CommandsAccepted==1 && writer.CommandsCompleted==0 && writer.PendingCommands==0,"failed command accounting explicit");
    Reject<IOException>(()=>writer.Flush(),"future admissions rejected after failure");
}

string diskPath=Path.Combine(fixture,"DiskFailure");Directory.CreateDirectory(diskPath);File.WriteAllText(Path.Combine(diskPath,"events.jsonl"),"existing");
var diskWriter=new StationDecisionDataWriter(diskPath,"run",Serialize);
diskWriter.WriteRoot("events.jsonl",new Record());Reject<IOException>(()=>diskWriter.Complete(),"root file collision I/O error reaches Stop");Check(File.ReadAllText(Path.Combine(diskPath,"events.jsonl"))=="existing","existing capture file never truncated");

// Repeated independent sessions never retain a previous worker, file handle or run identity.
for(int round=0;round<12;++round)
{
    string path=Path.Combine(fixture,"Restart-"+round);Directory.CreateDirectory(path);
    using(var writer=new StationDecisionDataWriter(path,"run-"+round,Serialize)) {writer.WriteRoot("sync.jsonl",new Record {ordinal=round});writer.Flush();}
    Check(File.ReadAllLines(Path.Combine(path,"sync.jsonl")).Length==1,"rapid start/stop/restart session finalized");
}
Console.WriteLine(JsonSerializer.Serialize(new {success=true,checks,stressRecords=12000,fixture,coverage=new[]{"main/background parity","FIFO and exact stamps","per-agent LRU","UTF-8 logical IDs","detached mutable DTOs","object-typed concrete snapshots","engine/container rejection","in-flight command/byte overload","nonblocking admission","Stop drain","serializer and disk errors","12 restarts","startup-only readiness and flush fence","startup failure drains safely","nonblocking completion and failure polling","completion closes handles before publishing","batch main/background byte parity","logical record counters","bounded in-progress group accounting","coalesced flush requests","atomic batch overflow","failed grouped record suffix drains","uncommitted authored batch fails explicitly","nonfatal reserved admission rejection","reserved byte and command headroom","commit guarantee and slack release","clock rig binding pose atomic reservation","admission reopens after drain","fatal reservation underestimation","completion during reservation"}},new JsonSerializerOptions{WriteIndented=true}));

[Serializable] sealed class Need { public float thirst; }
[Serializable] sealed class Stamp { public string runId="run"; public long syncSampleId; public double realElapsedSeconds; public bool paused; }
[Serializable] sealed class Record { public string kind;public int ordinal;public Stamp stamp;public Need needs;public string[] ids;public int[] seats;[NonSerialized]public UnityEngine.Object ignored; }
[Serializable] sealed class HasEngine { public UnityEngine.Object value; }
[Serializable] sealed class HasList { public List<int> values=new(); }
[Serializable] sealed class HasObject { public object value; }
[Serializable] sealed class HasPrivateSerializedField { [UnityEngine.SerializeField] private int hidden=5; }
[Serializable] sealed class HasSerializeReference { [UnityEngine.SerializeReference] private Need hidden=new(); }
namespace UnityEngine { [Serializable] public class Object {} public class SerializeField:Attribute {} public class SerializeReference:Attribute {} }
