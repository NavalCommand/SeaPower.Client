using Google.Protobuf;
using NavalCommand.Gateway.Wire;
using NavalCommand.SeaPower.Client;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

static class Program
{
    static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new Exception(label);
        }

        Console.WriteLine("PASS " + label);
    }

    static void Main()
    {
        DistanceTests.Run();
        var game = new FakeGameFacade();
        var provider = new Provider(game: game, observer: "observer");
        var view = WorldSnapshot.Parser.ParseFrom(provider.Tick());
        Check(view.OwnUnits.Count == 1 && view.Tracks.Count == 0, "wrong_side_filter");
        var waypointRequest = new OrderRequest
        {
            SessionId = view.Session.SessionId,
            RequestId = "first",
            EntityId = view.OwnUnits[0].Id,
            BasedOnVersion = view.ObservationVersion,
            Waypoint = new()
            {
                LatitudeDeg = 62,
                LongitudeDeg = -10.37
            }
        };
        OrderResult Send(OrderRequest request)
        {
            provider.Enqueue(request.ToByteArray());
            provider.Tick();
            provider.Results.TryDequeue(out var result);
            return OrderResult.Parser.ParseFrom(result);
        }

        Check(Send(waypointRequest).Stage == Stage.NativeTaskCreated && game.Mutations == 1, "native_acceptance");
        Check(Send(waypointRequest).Stage == Stage.NativeTaskCreated && game.Mutations == 1, "duplicate_idempotency");
        game.Own.LatitudeDeg += .001;
        provider.Tick();
        provider.Results.TryDequeue(out var movementResultBytes);
        Check(OrderResult.Parser.ParseFrom(movementResultBytes).Stage == Stage.MovementObserved, "movement_distinct");
        game.Own.Protected = true;
        var blocked = waypointRequest.Clone();
        blocked.RequestId = "blocked";
        Check(Send(blocked).Reason == "protected_native_action" && game.Mutations == 1, "dispatch_revalidation_protected");
        game.Own.Protected = false;
        game.Own.Grounded = true;
        blocked.RequestId = "grounded";
        Check(Send(blocked).Reason == "grounded_launch_sequence_unverified" && game.Mutations == 1, "grounded_conservative");
        game.Own.Grounded = false;
        game.Own.Formation = true;
        blocked.RequestId = "formation";
        Check(Send(blocked).Reason == "formation_effects_unverified", "formation_guard");
        game.Own.Formation = false;
        var wrong = waypointRequest.Clone();
        wrong.RequestId = "wrong";
        wrong.EntityId = "enemy";
        Check(Send(wrong).Reason == "wrong_side_or_invalid_entity", "wrong_entity");
        var invalid = waypointRequest.Clone();
        invalid.RequestId = "invalid";
        invalid.Waypoint.LatitudeDeg = double.NaN;
        Check(Send(invalid).Reason == "invalid_arguments", "finite_arguments");
        bool wrongThread = false;
        Task.Run(() =>
        {
            try
            {
                provider.Tick();
            }
            catch (InvalidOperationException)
            {
                wrongThread = true;
            }
        }).Wait();
        Check(wrongThread, "engine_thread_only");
        var bounded = new Provider(game: game, observer: "observer");
        for (int commandIndex = 0; commandIndex < 64; ++commandIndex)
        {
            Check(bounded.Enqueue(waypointRequest.ToByteArray()), "queue_slot");
        }

        Check(!bounded.Enqueue(waypointRequest.ToByteArray()), "backpressure");
        provider.Invalidate();
        provider.Tick();
        Check(Send(waypointRequest).Reason == "stale_session" && game.Mutations == 1, "reload_invalidation");
        Check(!provider.Enqueue(new byte[65537]), "message_bound");
    }
}
