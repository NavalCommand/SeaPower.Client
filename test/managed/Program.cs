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
        Check(view.Instances.Count == 1 && view.Instances[0].Subject.IdentityCase == Subject.IdentityOneofCase.OwnUnitId, "wrong_side_filter");
        Check(view.Instances[0].Equipment.Coverage == CollectionCoverage.CoverageUnknown, "uncaptured_equipment_unknown");
        Check(view.Instances[0].Kinematics.SpeedMps.StateCase == DoubleKnowledge.StateOneofCase.Known
            && view.Instances[0].Kinematics.SpeedMps.Known == 0, "known_zero_motion");
        Check(view.Instances[0].Navigation.PlannedRoute.StateCase == RouteKnowledge.StateOneofCase.Known
            && view.Instances[0].Navigation.PlannedRoute.Known.Points.Count == 0, "inspected_empty_route");
        game.Tracks = new[] { new TrackSample { Key = "contact", LatitudeDeg = 1, LongitudeDeg = 2 } };
        var mixed = WorldSnapshot.Parser.ParseFrom(provider.Tick());
        var contact = mixed.Instances.Single(unit => unit.Subject.IdentityCase == Subject.IdentityOneofCase.TrackId);
        Check(mixed.Instances.Count == 2 && contact.Kinematics.Position.StateCase == PositionKnowledge.StateOneofCase.Estimated
            && contact.Kinematics.Position.Estimated.Value.Coordinates.LatitudeDeg == 1, "observer_contact_estimate");
        Check(contact.OrderEligibility == null && contact.Display == null
            && contact.Equipment.Coverage == CollectionCoverage.CoverageUnknown, "contact_no_hidden_identity_or_orders");
        game.Tracks = Array.Empty<TrackSample>();
        var waypointRequest = new OrderRequest
        {
            SessionId = view.Session.SessionId,
            RequestId = "first",
            Subject = new Subject { OwnUnitId = view.Instances.First(unit => unit.Subject.IdentityCase == Subject.IdentityOneofCase.OwnUnitId).Subject.OwnUnitId },
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
        var contactOrder = waypointRequest.Clone();
        contactOrder.RequestId = "tagged-contact";
        contactOrder.Subject = new Subject { TrackId = waypointRequest.Subject.OwnUnitId };
        Check(Send(contactOrder).Reason == "own_unit_subject_required" && game.Mutations == 1, "track_tag_cannot_authorize_own_unit");
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
        wrong.Subject.OwnUnitId = "enemy";
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
        MovementTests.Run();
        LaunchTests.Run();
        LaunchLifecycleTests.Run();
    }
}
