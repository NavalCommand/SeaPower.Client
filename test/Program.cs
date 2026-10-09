using Google.Protobuf;
using NavalCommand.Gateway.Wire;
using NavalCommand.SeaPower.Client;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
sealed class Fake : IGameFacade {
 public bool Ready=>true;public double OperationalSeconds=>42;public int Mutations;
 public UnitSample Own=new(){Key="own",Own=true,Supported=true,Controllable=true,LatitudeDeg=61.96,LongitudeDeg=-10.37};
 public IReadOnlyList<UnitSample> CaptureUnits()=>new[]{Own,new UnitSample{Key="hidden",Own=false}};
 public IReadOnlyList<TrackSample> CaptureTracks()=>Array.Empty<TrackSample>();
 public UnitSample Resolve(string key)=>key=="own"?Own:null;
 public bool AppendWaypoint(string k,double la,double lo,string label){++Mutations;return true;}
 public void Reset(){}
}
static class Program {
 static void Check(bool b,string label){if(!b)throw new Exception(label);Console.WriteLine("PASS "+label);}
 static void Main(){
  var game=new Fake();var provider=new Provider(game,"observer");var view=WorldSnapshot.Parser.ParseFrom(provider.Tick());
  Check(view.OwnUnits.Count==1&&view.Tracks.Count==0,"wrong_side_filter");
  var r=new OrderRequest{SessionId=view.Session.SessionId,RequestId="first",EntityId=view.OwnUnits[0].Id,BasedOnVersion=view.ObservationVersion,Waypoint=new(){LatitudeDeg=62,LongitudeDeg=-10.37}};
  OrderResult Send(OrderRequest q){provider.Enqueue(q.ToByteArray());provider.Tick();provider.Results.TryDequeue(out var result);return OrderResult.Parser.ParseFrom(result);}
  Check(Send(r).Stage==Stage.NativeTaskCreated&&game.Mutations==1,"native_acceptance");
  Check(Send(r).Stage==Stage.NativeTaskCreated&&game.Mutations==1,"duplicate_idempotency");
  game.Own.LatitudeDeg+=.001;provider.Tick();provider.Results.TryDequeue(out var moved);Check(OrderResult.Parser.ParseFrom(moved).Stage==Stage.MovementObserved,"movement_distinct");
  game.Own.Protected=true;var blocked=r.Clone();blocked.RequestId="blocked";Check(Send(blocked).Reason=="protected_native_action"&&game.Mutations==1,"dispatch_revalidation_protected");
  game.Own.Protected=false;game.Own.Grounded=true;blocked.RequestId="grounded";Check(Send(blocked).Reason=="grounded_launch_sequence_unverified"&&game.Mutations==1,"grounded_conservative");
  game.Own.Grounded=false;game.Own.Formation=true;blocked.RequestId="formation";Check(Send(blocked).Reason=="formation_effects_unverified","formation_guard");
  game.Own.Formation=false;var wrong=r.Clone();wrong.RequestId="wrong";wrong.EntityId="enemy";Check(Send(wrong).Reason=="wrong_side_or_invalid_entity","wrong_entity");
  var invalid=r.Clone();invalid.RequestId="invalid";invalid.Waypoint.LatitudeDeg=double.NaN;Check(Send(invalid).Reason=="invalid_arguments","finite_arguments");
  bool wrongThread=false;Task.Run(()=>{try{provider.Tick();}catch(InvalidOperationException){wrongThread=true;}}).Wait();Check(wrongThread,"engine_thread_only");
  var bounded=new Provider(game,"observer");for(int i=0;i<64;++i)Check(bounded.Enqueue(r.ToByteArray()),"queue_slot");Check(!bounded.Enqueue(r.ToByteArray()),"backpressure");
  provider.Invalidate();provider.Tick();Check(Send(r).Reason=="stale_session"&&game.Mutations==1,"reload_invalidation");
  Check(!provider.Enqueue(new byte[65537]),"message_bound");
 }
}

