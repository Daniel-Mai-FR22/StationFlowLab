using StationFlow.TrainStation.DecisionLab;
int checks=0;
void Check(bool condition,string message){++checks;if(!condition)throw new Exception(message);}
void Close(double actual,double expected,string message)=>Check(Math.Abs(actual-expected)<1e-10,message);
void Reject(Action action,string message){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check(rejected,message);}
var rates=new StationDecisionVideoRateTracker();Close(rates.GetFpsAt(0),0,"empty rate");
var first=rates.Add(0);Check(first.intervalRealSeconds==null && first.fpsInstantaneous==null,"first frame nullable");Close(first.rollingFps1s,1,"fixed one-second startup denominator");
var second=rates.Add(.2);Close(second.intervalRealSeconds.Value,.2,"original render interval");Close(second.fpsInstantaneous.Value,5,"instantaneous reciprocal");Close(second.rollingFps1s,2,"second-frame count");
var third=rates.Add(.7);Close(third.intervalRealSeconds.Value,.5,"variable interval");Close(third.fpsInstantaneous.Value,2,"variable instantaneous");
Close(rates.GetFpsAt(1),2,"left boundary excluded");Close(rates.GetFpsAt(.7),3,"right boundary included");
Close(rates.GetFpsAt(100),0,"old frames absent");var delayed=rates.Add(.8);Close(delayed.rollingFps1s,4,"future UI query cannot erase delayed-worker history");Close(delayed.fpsInstantaneous.Value,10,"render time independent of write time");
var gap=rates.Add(3);Close(gap.intervalRealSeconds.Value,2.2,"long gap across segment boundary");Close(gap.rollingFps1s,1,"long gap window");
Reject(()=>rates.Add(3),"duplicate render stamp rejected");Reject(()=>rates.Add(2.9),"backwards stamp rejected");Reject(()=>rates.Add(double.NaN),"NaN rejected");Reject(()=>rates.Add(double.PositiveInfinity),"infinity rejected");Reject(()=>rates.GetFpsAt(-1),"negative query rejected");
var concurrent=new StationDecisionVideoRateTracker();var readers=Enumerable.Range(0,4).Select(_=>Task.Run(()=>{for(int i=0;i<2000;++i){double value=concurrent.GetFpsAt(100);if(value<0 || double.IsNaN(value))throw new Exception("unsafe read");}})).ToArray();
for(int i=0;i<=1000;++i)concurrent.Add(i*.01);Task.WaitAll(readers);Close(concurrent.GetFpsAt(10),100,"one hundred stamps inside final one-second window");Check(concurrent.GetFrameCountAt(10)==100,"count and FPS agree");
Console.WriteLine($"PASS: {checks} real-frame-rate checks including exact window boundaries, variable timestamps, gaps, non-mutating delayed-worker queries and concurrent readers.");
