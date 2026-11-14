using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StationFlow.TrainStation.DecisionLab;
int checks=0;
void Check(bool value,string message){++checks;if(!value)throw new Exception(message);}
void Reject<T>(Action action,string message) where T:Exception{bool rejected=false;try{action();}catch(T){rejected=true;}Check(rejected,message);}
Dictionary<string,string> Index(string rootPath){using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(rootPath,"agents","index.json")));Check(json.RootElement.GetProperty("schema").GetString()=="stationflow.agents_index.v1","index schema");Check(json.RootElement.GetProperty("runId").GetString()=="run-test","index run identity");Check(json.RootElement.GetProperty("identityPolicy").GetString()==StationDecisionAgentCaptureStore.IdentityPolicy,"index identity policy");return json.RootElement.GetProperty("agents").EnumerateArray().ToDictionary(x=>x.GetProperty("agentId").GetString(),x=>x.GetProperty("directory").GetString(),StringComparer.Ordinal);}
string[] ReadSharedLines(string path){using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new StreamReader(file);return reader.ReadToEnd().Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);}
Check(args.Length==1 && Path.IsPathRooted(args[0]),"absolute fixture root argument");string fixture=Path.GetFullPath(args[0]);Directory.CreateDirectory(fixture);
Reject<ArgumentException>(()=>new StationDecisionAgentCaptureStore("relative","run-test"),"relative root rejected");
Reject<ArgumentException>(()=>new StationDecisionAgentCaptureStore(fixture," "),"blank run rejected");
Reject<ArgumentOutOfRangeException>(()=>new StationDecisionAgentCaptureStore(fixture,"run-test",0),"zero writer bound rejected");
string rootPath=Path.Combine(fixture,"Routing");var store=new StationDecisionAgentCaptureStore(rootPath,"run-test",2);
string[] ids={"Journey-A","journey-a","CON","NUL","COM1","LPT9","../../outside","C:\\escape\\..","漢字/🚆","élève","e\u0301lève","quote\"slash\\\nline","   visible   ",new string('X',200),"2026-10-20/daily-20261020-013-v4/A/001"};
foreach(string id in ids)store.RegisterAgent(id);store.RegisterAgent(ids[0]);
Check(!Directory.Exists(rootPath),"registration creates no directories at all");
Reject<ArgumentException>(()=>store.RegisterAgent("  "),"blank logical ID rejected");
Reject<ArgumentException>(()=>store.RegisterAgent("bad\ud800"),"invalid Unicode rejected");
store.SaveIndex();var index=Index(rootPath);Check(index.Count==ids.Length,"duplicate registration idempotent");
Check(index.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count()==ids.Length,"all case/Unicode/reserved/traversal IDs filesystem-unique");
Check(Directory.GetDirectories(Path.Combine(rootPath,"agents")).Length==0,"index does not create any planned agent folder");
foreach(string id in ids){string rel=index[id];Check(Regex.IsMatch(rel,@"^agents/agent-[a-z0-9](?:[a-z0-9-]{0,55})-[0-9a-f]{16}$"),"safe bounded readable directory");Check(!rel.Contains("..") && !rel.Contains('\\'),"no traversal or platform separator");string expected=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant()[..16];Check(rel.EndsWith("-"+expected,StringComparison.Ordinal),"hash derives from exact original logical ID");}
Check(index[ids[0]]!=index[ids[1]],"case-only logical identities remain separate");
Check(index["2026-10-20/daily-20261020-013-v4/A/001"].StartsWith("agents/agent-2026-10-20-daily-20261020-013-v4-a-001-",StringComparison.Ordinal),"readable folder retains full voyageur number");
string indexPath=Path.Combine(rootPath,"agents","index.json");var oldDate=new DateTime(2001,2,3,0,0,0,DateTimeKind.Utc);File.SetLastWriteTimeUtc(indexPath,oldDate);store.Flush();store.SaveIndex();Check(File.GetLastWriteTimeUtc(indexPath)==oldDate,"unchanged registry not rewritten by Flush/SaveIndex");
Reject<ArgumentException>(()=>store.WriteLine("new-invalid","../escape.jsonl","{}"),"stream traversal rejected");
Reject<ArgumentException>(()=>store.WriteLine("new-invalid","events.jsonl","{}\n{}"),"multiline JSONL injection rejected");
store.WriteProfile(ids[0],"{\"agentId\":\"Journey-A\"}");store.WriteProfile(ids[0],"{\"agentId\":\"Journey-A\"}\n");
Reject<IOException>(()=>store.WriteProfile(ids[0],"{\"different\":true}"),"conflicting immutable profile rejected");
for(int pass=0;pass<3;++pass)foreach(string id in ids){store.WriteLine(id,"trajectories.jsonl",JsonSerializer.Serialize(new{agentId=id,actorSlotId="slot-003",pass}));Check(store.OpenWriterCount<=2,"append handle count bounded during LRU churn");}
store.WriteLine("dynamic","events.jsonl","{\"agentId\":\"dynamic\"}");store.Flush();index=Index(rootPath);Check(index.ContainsKey("dynamic"),"dynamic write auto-registers and Flush saves dirty index");Check(File.GetLastWriteTimeUtc(indexPath)!=oldDate,"dirty registry rewritten");
foreach(string id in ids){string path=Path.Combine(rootPath,index[id],"trajectories.jsonl");string[] lines=ReadSharedLines(path);Check(lines.Length==3,"LRU reopen appends without truncation");for(int pass=0;pass<3;++pass){using var json=JsonDocument.Parse(lines[pass]);Check(json.RootElement.GetProperty("agentId").GetString()==id && json.RootElement.GetProperty("pass").GetInt32()==pass,"logical identity/order preserved despite reused physical slot");}}
Check(!Directory.GetFiles(rootPath,"*.jsonl").Any(),"no duplicated global avatar streams");
Check(!File.Exists(Path.Combine(rootPath,index[ids[1]],"profile.json")),"disabled/unwritten profile remains absent");
Check(!File.Exists(Path.Combine(rootPath,index[ids[0]],"animations.jsonl")),"disabled/unwritten animation stream absent");
store.Dispose();Check(store.OpenWriterCount==0,"Dispose closes all append writers");store.Dispose();
Reject<ObjectDisposedException>(()=>store.WriteLine(ids[0],"events.jsonl","{}"),"writes after disposal rejected");
Check(!Directory.GetFiles(rootPath,"*.tmp",SearchOption.AllDirectories).Any(),"atomic index temporary files cleaned");
string concurrentRoot=Path.Combine(fixture,"Concurrent");using(var concurrent=new StationDecisionAgentCaptureStore(concurrentRoot,"run-test",4)){Parallel.For(0,8,t=>{for(int i=0;i<100;++i)concurrent.WriteLine("shared-logical-id","events.jsonl",JsonSerializer.Serialize(new{thread=t,index=i}));});concurrent.Flush();var concurrentIndex=Index(concurrentRoot);string[] lines=ReadSharedLines(Path.Combine(concurrentRoot,concurrentIndex["shared-logical-id"],"events.jsonl"));Check(lines.Length==800,"thread-safe append loses no lines");Check(lines.Select(line=>{using var j=JsonDocument.Parse(line);return(j.RootElement.GetProperty("thread").GetInt32(),j.RootElement.GetProperty("index").GetInt32());}).Distinct().Count()==800,"thread-safe append does not interleave or duplicate records");}
string links=Path.Combine(fixture,"Reparse"),outside=Path.Combine(fixture,"Outside");Directory.CreateDirectory(links);Directory.CreateDirectory(outside);bool linkTest=false;
if(Directory.Exists(Path.Combine(links,"agents")) && (File.GetAttributes(Path.Combine(links,"agents")) & FileAttributes.ReparsePoint)!=0)linkTest=true;
else try{Directory.CreateSymbolicLink(Path.Combine(links,"agents"),outside);linkTest=true;}catch(UnauthorizedAccessException){}catch(System.IO.IOException){}
if(linkTest){Reject<IOException>(()=>new StationDecisionAgentCaptureStore(Path.Combine(links,"agents"),"run-test"),"reparse root rejected");var linked=new StationDecisionAgentCaptureStore(links,"run-test");Reject<IOException>(()=>linked.RegisterAgent("escape"),"reparse path rejected even during registration");Reject<IOException>(()=>linked.Dispose(),"index cannot write through reparse target");Check(!Directory.EnumerateFileSystemEntries(outside).Any(),"no evidence written outside root through link");}
Console.WriteLine($"PASS: {checks} agent-store checks; lazy registration, safe exact-ID routing, immutable profiles, bounded LRU append/reopen, dirty index, concurrency and disposal.");Console.WriteLine(linkTest?"PASS: reparse containment tested.":"NOTE: symbolic-link creation unavailable; lexical traversal and path-safety checks passed.");
