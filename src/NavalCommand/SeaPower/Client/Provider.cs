using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Google.Protobuf;
using NavalCommand.Gateway.Wire;
namespace NavalCommand.SeaPower.Client;
// Engine thread owns facade, handles and ledger. I/O sees immutable byte arrays only.
// No simulator references cross queues. Serialization is bounded to 128 units/tracks.
public sealed class Provider {
 readonly IGameFacade game;
 readonly int thread;
 readonly string observer;
 readonly Dictionary<string,string> handles=new();
 readonly Dictionary<string,OrderResult> ledger=new();
 readonly Dictionary<string,(string Key,double Lat,double Lon)> movement=new();
 readonly ConcurrentQueue<byte[]> commands=new(), output=new();
 string session="";
 ulong version;
 int queued;
 public Provider(IGameFacade game,string observer){this.game=game;this.observer=observer;thread=Thread.CurrentThread.ManagedThreadId;}
 void Main(){if(thread!=Thread.CurrentThread.ManagedThreadId)throw new InvalidOperationException("engine_thread_required");}
 public bool Enqueue(byte[] immutableBytes) { if(immutableBytes==null||immutableBytes.Length>65536)return false;if(Interlocked.Increment(ref queued)>64){Interlocked.Decrement(ref queued);return false;}commands.Enqueue((byte[])immutableBytes.Clone());return true; }
 public bool TryRead(out byte[] bytes)=>output.TryDequeue(out bytes);
 // Separate outgoing channels preserve canonical payload types without extra schema.
 public readonly ConcurrentQueue<byte[]> Results=new();
 void Result(OrderResult r){ledger[r.RequestId]=r;if(Results.Count<128)Results.Enqueue(r.ToByteArray());else throw new InvalidOperationException("status_backpressure");}
 public void Invalidate(){Main();session="";version=0;handles.Clear();ledger.Clear();movement.Clear();game.Reset();while(commands.TryDequeue(out _)){Interlocked.Decrement(ref queued);}while(output.TryDequeue(out _)){}while(Results.TryDequeue(out _)){}var w=new WorldSnapshot{Session=new Session{SchemaVersion=1,Ready=false}};output.Enqueue(w.ToByteArray());}
 public byte[] Tick(){
  Main();if(!game.Ready){if(session!="")Invalidate();return null;}
  if(session=="")session=Guid.NewGuid().ToString("N");
  var units=game.CaptureUnits().Where(u=>u.Own).Take(129).ToArray();
  var current=new HashSet<string>(units.Take(128).Select(u=>u.Key));
  foreach(var key in handles.Keys.Where(k=>!current.Contains(k)).ToArray())handles.Remove(key);
  foreach(var u in units.Take(128))if(!handles.ContainsKey(u.Key))handles[u.Key]=Guid.NewGuid().ToString("N");
  // Process bounded commands before publishing a new version.
  for(int i=0;i<8&&commands.TryDequeue(out var bytes);++i){
   Interlocked.Decrement(ref queued);
   OrderRequest r;try{r=OrderRequest.Parser.ParseFrom(bytes);}catch{continue;}
   Dispatch(r);
  }
  var w=new WorldSnapshot{Session=new Session{SchemaVersion=1,SessionId=session,ObserverId=observer,Ready=true},ObservationVersion=++version,OperationalSeconds=game.OperationalSeconds,Truncated=units.Length>128};
  foreach(var u in units.Take(128)){
   var x=new Unit{Id=handles[u.Key],Name=u.Name,Position=new GeoPoint{LatitudeDeg=u.LatitudeDeg,LongitudeDeg=u.LongitudeDeg},SpeedMps=u.SpeedMetersPerSecond,SampledAtSeconds=game.OperationalSeconds};
   x.Orders.Add(Eligibility(u));w.OwnUnits.Add(x);
   if(u.Stocks.Count>0){var f=new Facility{EntityId=x.Id,PendingOperations=(uint)Math.Max(0,u.PendingOperations),SampledAtSeconds=game.OperationalSeconds};foreach(var s in u.Stocks.Take(12))f.Stocks.Add(new Stock{ClassKey=s.ClassKey,Squadron=s.Squadron,UnreservedCount=(uint)Math.Max(0,s.Count)});w.Facilities.Add(f);}
  }
  foreach(var t in game.CaptureTracks().Take(128))w.Tracks.Add(new Track{Id=t.Key,DetectedPosition=new GeoPoint{LatitudeDeg=t.LatitudeDeg,LongitudeDeg=t.LongitudeDeg},SampledAtSeconds=game.OperationalSeconds,Provenance="selected_plotting_table_detected_position_cache"});
  foreach(var p in movement.ToArray()){
   var u=game.Resolve(p.Value.Key);if(u==null||!u.Own){movement.Remove(p.Key);continue;}
   var meters=Distance(p.Value.Lat,p.Value.Lon,u.LatitudeDeg,u.LongitudeDeg);
   if(meters>=5){var r=ledger[p.Key].Clone();r.Stage=Stage.MovementObserved;r.Reason="displacement_after_native_task_creation";r.DisplacementM=meters;r.SampledAtSeconds=game.OperationalSeconds;Result(r);movement.Remove(p.Key);}
  }
  var payload=w.ToByteArray();if(payload.Length>1048576)throw new InvalidOperationException("snapshot_too_large");
  while(output.TryDequeue(out _)){}output.Enqueue(payload);return payload;
 }
 public static OrderEligibility Eligibility(UnitSample u){
  var e=new OrderEligibility{Kind="set_waypoint",Availability=Availability.Executable,ArgumentSemantics="append; latitude/longitude degrees; current altitude; native telegraph 2; isolated unit only"};
  if(!u.Supported){e.Availability=Availability.Unsupported;e.Reason="unsupported_unit_family";}
  else if(!u.Own){e.Availability=Availability.Blocked;e.Reason="wrong_side_or_invalid_entity";}
  else if(u.Protected){e.Availability=Availability.Blocked;e.Reason="protected_native_action";}
  else if(u.Grounded){e.Availability=Availability.Unknown;e.Reason="grounded_launch_sequence_unverified";}
  else if(!u.Controllable){e.Availability=Availability.Blocked;e.Reason="uncontrollable";}
  else if(u.Formation){e.Availability=Availability.Unknown;e.Reason="formation_effects_unverified";}
  else if(u.SelectedTask){e.Availability=Availability.Blocked;e.Reason="native_task_selection_active";}
  return e;
 }
 void Dispatch(OrderRequest r){
  var result=new OrderResult{SessionId=r.SessionId,RequestId=r.RequestId,Stage=Stage.Rejected,SampledAtSeconds=game.OperationalSeconds};
  void Reject(string reason){result.Reason=reason;Result(result);}
  if(r.SessionId!=session){result.Reason="stale_session";if(Results.Count<128)Results.Enqueue(result.ToByteArray());return;}
  if(ledger.TryGetValue(r.RequestId,out var prior)){if(Results.Count<128)Results.Enqueue(prior.ToByteArray());return;}
  if(ledger.Count>=128){result.Reason="ledger_full";if(Results.Count<128)Results.Enqueue(result.ToByteArray());return;}
  if(string.IsNullOrWhiteSpace(r.RequestId)||r.RequestId.Length>128){Reject("invalid_arguments");return;}
  var key=handles.FirstOrDefault(p=>p.Value==r.EntityId).Key;
  var u=key==null?null:game.Resolve(key);
  if(u==null||!u.Own){Reject("wrong_side_or_invalid_entity");return;}
  var p=r.Waypoint;
  if(p==null||!double.IsFinite(p.LatitudeDeg)||!double.IsFinite(p.LongitudeDeg)||Math.Abs(p.LatitudeDeg)>90||Math.Abs(p.LongitudeDeg)>180||Distance(u.LatitudeDeg,u.LongitudeDeg,p.LatitudeDeg,p.LongitudeDeg)>185200){Reject("invalid_arguments");return;}
  if(r.BasedOnVersion==0||r.BasedOnVersion>version||version-r.BasedOnVersion>20){Reject("stale_observation");return;}
  var eligible=Eligibility(u);
  if(eligible.Availability!=Availability.Executable){Reject(eligible.Reason);return;}
  // Facade revalidates native membership/state once more adjacent to mutation.
  if(!game.AppendWaypoint(key,p.LatitudeDeg,p.LongitudeDeg,"NC-"+r.RequestId)){Reject("native_dispatch_refused");return;}
  result.Stage=Stage.NativeTaskCreated;result.Reason="native_task_membership_verified";Result(result);
  movement[r.RequestId]=(key,u.LatitudeDeg,u.LongitudeDeg);
 }
 public static double Distance(double la,double lo,double lb,double lob){double a=la*Math.PI/180,b=lb*Math.PI/180,x=(lb-la)*Math.PI/180,y=(lob-lo)*Math.PI/180;double h=Math.Sin(x/2)*Math.Sin(x/2)+Math.Cos(a)*Math.Cos(b)*Math.Sin(y/2)*Math.Sin(y/2);return 6371000*2*Math.Atan2(Math.Sqrt(h),Math.Sqrt(Math.Max(0,1-h)));}
}

