using StationFlow.TrainStation.DecisionLab;
using System.Collections.Concurrent;
int checks=0;
void Check(bool yes,string message){Interlocked.Increment(ref checks);if(!yes)throw new Exception(message);}
void Reject(Action call,string message){bool rejected=false;try{call();}catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}Check(rejected,message);}
Reject(()=>new StationDecisionCaptureRing<string>(0),"zero capacity");
Reject(()=>new StationDecisionCaptureRing<string>(5),"unbounded capacity");
for(int capacity=1;capacity<=4;++capacity){var r=new StationDecisionCaptureRing<string>(capacity);var tickets=new List<StationDecisionCaptureRing<string>.Ticket>();for(int i=0;i<capacity;++i){Check(r.TryReserve(out var ticket),"reserve bounded slot");tickets.Add(ticket);}Check(!r.TryReserve(out _),"cannot exceed bound");Check(r.OwnedCount==capacity,"exact ownership");foreach(var ticket in tickets)Check(r.CancelReservation(ticket),"rollback reservation");Check(r.OwnedCount==0,"rollback freed all");Check(!r.CancelReservation(tickets[0]),"duplicate rollback harmless");}
var ring=new StationDecisionCaptureRing<string>(4);
ring.TryReserve(out var a);ring.Submit(a);ring.TryReserve(out var b);ring.Submit(b);ring.TryReserve(out var c);ring.Submit(c);ring.TryReserve(out var d);ring.Submit(d);
Check(ring.PendingCount==4,"four requests in flight");
ring.Complete(c,"c");ring.Complete(d,"d");Check(!ring.TryTakeReady(out _,out _),"later callbacks cannot pass earliest");
ring.Complete(a,"a");Check(ring.TryTakeReady(out var ta,out var va)&&va=="a","first original stamp emitted");
Check(!ring.TryReserve(out _),"writer still owns first slot");Check(!ring.TryTakeReady(out _,out _),"missing callback holds order");
ring.Complete(b,null);Check(ring.TryTakeReady(out var tc,out var vc)&&vc=="c","failed-frame tombstone advances order");
Check(ring.TryTakeReady(out var td,out var vd)&&vd=="d","remaining original order");Check(!ring.HasUnqueued,"all callbacks handled");
Check(ring.OwnedCount==3,"null completion released only its own slot");ring.Release(ta);ring.Release(tc);ring.Release(td);Check(ring.OwnedCount==0,"written slots released");
Reject(()=>ring.Complete(a,"duplicate"),"stale completion rejected");Reject(()=>ring.Release(ta),"duplicate writer release rejected");
ring.TryReserve(out var reused);Check(reused.IsValid,"reusable reservation");Reject(()=>ring.Release(reused),"cannot release unsubmitted slot as writer");ring.CancelReservation(reused);
// Slot-indexed buffers are never recycled while a pending/ready/writer lease owns them.
var pooled=new StationDecisionCaptureRing<byte[]>(1);var buffers=new[]{new byte[8]};
pooled.TryReserve(out var old);buffers[old.index][0]=17;pooled.Submit(old);pooled.Complete(old,buffers[old.index]);pooled.TryTakeReady(out var held,out var data);
Check(!pooled.TryReserve(out _)&&data[0]==17,"buffer stable until writer releases");pooled.Release(held);pooled.TryReserve(out var fresh);Check(fresh.index==old.index&&fresh.generation!=old.generation,"reuse same allocation under a new generation");buffers[fresh.index][0]=23;Reject(()=>pooled.Release(old),"old owner cannot free reused pixels");pooled.CancelReservation(fresh);
// A failed common reservation does not create holes in the committed render sequence.
var batch=Enumerable.Range(0,4).Select(_=>new StationDecisionCaptureRing<string>(2)).ToArray();
batch[2].TryReserve(out var blocked1);batch[2].Submit(blocked1);batch[2].TryReserve(out var blocked2);batch[2].Submit(blocked2);
var reservations=new StationDecisionCaptureRing<string>.Ticket[4];bool all=true;for(int i=0;i<4;++i)all&=batch[i].TryReserve(out reservations[i]);Check(!all,"peer backpressure rejects common batch");for(int i=0;i<4;++i)batch[i].CancelReservation(reservations[i]);
for(int i=0;i<4;++i)if(i!=2){Check(batch[i].OwnedCount==0,"rollback leaves other camera idle");batch[i].TryReserve(out var ticket);batch[i].Submit(ticket);batch[i].Complete(ticket,"next");Check(batch[i].TryTakeReady(out var ready,out var value)&&value=="next","rollback creates no ordering hole");batch[i].Release(ready);}
// Queue capacity two retains the other completed slots in the ring without dropping or overwriting.
var bounded=new StationDecisionCaptureRing<string>(4);var queue=new Queue<(StationDecisionCaptureRing<string>.Ticket ticket,string value)>();var leases=new StationDecisionCaptureRing<string>.Ticket[4];
for(int i=0;i<4;++i){bounded.TryReserve(out leases[i]);bounded.Submit(leases[i]);}for(int i=3;i>=0;--i)bounded.Complete(leases[i],i.ToString());
void Pump(){while(queue.Count<2&&bounded.TryTakeReady(out var ticket,out var value))queue.Enqueue((ticket,value));}
Pump();Check(queue.Count==2&&bounded.HasUnqueued&&bounded.OwnedCount==4,"ready overflow remains owned inside ring");var saved=new List<string>();while(queue.Count>0||bounded.HasUnqueued){Pump();var frame=queue.Dequeue();saved.Add(frame.value);bounded.Release(frame.ticket);}Check(string.Join(",",saved)=="0,1,2,3"&&bounded.OwnedCount==0,"bounded downstream drains in render order");
// Stop closes admission, cancels only unused reservations, then accepts all pending completions.
var closing=new StationDecisionCaptureRing<string>(3);closing.TryReserve(out var unused);closing.TryReserve(out var pending0);closing.Submit(pending0);closing.TryReserve(out var pending1);closing.Submit(pending1);closing.CloseAdmission();Check(!closing.TryReserve(out _)&&closing.OwnedCount==2,"Stop closes new admission and rolls back unused slot");closing.Complete(pending1,"last");closing.Complete(pending0,"first");Check(closing.TryTakeReady(out var firstStop,out var firstValue)&&firstValue=="first","Stop drains first");closing.Release(firstStop);Check(closing.TryTakeReady(out var lastStop,out var lastValue)&&lastValue=="last","Stop drains last");closing.Release(lastStop);Check(!closing.HasUnqueued&&closing.OwnedCount==0,"all Stop resources reclaimable");Reject(()=>closing.Submit(unused),"cannot submit canceled Stop reservation");
// Concurrent producer / writer stress validates no reuse before ordered consumption.
var concurrent=new StationDecisionCaptureRing<string>(4);var clock=System.Diagnostics.Stopwatch.StartNew();
var producer=Task.Run(()=>{for(int i=0;i<2000;++i){StationDecisionCaptureRing<string>.Ticket token;while(!concurrent.TryReserve(out token)){if(clock.Elapsed.TotalSeconds>10)throw new Exception("producer drain timeout");Thread.Yield();}concurrent.Submit(token);concurrent.Complete(token,i.ToString());}});
var consumer=Task.Run(()=>{for(int i=0;i<2000;++i){StationDecisionCaptureRing<string>.Ticket token;string value;while(!concurrent.TryTakeReady(out token,out value)){if(clock.Elapsed.TotalSeconds>10)throw new Exception("consumer drain timeout");Thread.Yield();}Check(value==i.ToString(),"concurrent original order");concurrent.Release(token);}});
Task.WaitAll(producer,consumer);Check(concurrent.OwnedCount==0&&!concurrent.HasUnqueued,"concurrent full drain");
Console.WriteLine($"PASS: {checks} capture-ring ownership, rollback, ordering, bounded backpressure, pooled-buffer reuse and Stop/concurrency checks.");
