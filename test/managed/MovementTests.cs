using System;
using System.Linq;
using Google.Protobuf;
using NavalCommand.Gateway.Wire;
using NavalCommand.SeaPower.Client;

static class MovementTests
{
    static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new Exception(label);
        }
        Console.WriteLine("PASS " + label);
    }

    public static void Run()
    {
        var game = new FakeGameFacade();
        var provider = new Provider(game, "observer");
        var initial = WorldSnapshot.Parser.ParseFrom(provider.Tick());
        OrderRequest Request(string id, RouteMode mode) => new()
        {
            SessionId = initial.Session.SessionId,
            Subject = new Subject { OwnUnitId = initial.Instances.First(unit => unit.Subject.IdentityCase == Subject.IdentityOneofCase.OwnUnitId).Subject.OwnUnitId },
            RequestId = id,
            BasedOnVersion = initial.ObservationVersion,
            Waypoint = new Waypoint { LatitudeDeg = 62, LongitudeDeg = -10.37, RouteMode = mode }
        };
        OrderResult Send(OrderRequest request)
        {
            provider.Enqueue(request.ToByteArray());
            provider.Tick();
            provider.Results.TryDequeue(out var bytes);
            return OrderResult.Parser.ParseFrom(bytes);
        }
        var first = Request("A", RouteMode.ReplaceRoute);
        Check(Send(first).Stage == Stage.RouteMutated, "replace_empty_route");
        Check(Send(Request("B", RouteMode.AppendWaypoint)).RouteAfter.Count == 2, "append_moving_route");
        Check(Send(Request("C", RouteMode.AppendWaypoint)).RouteAfter.Select(point => point.Label)
            .SequenceEqual(new[] { "NC-A", "NC-B", "NC-C" }), "multiple_append_sequence");
        var replacement = Request("D", RouteMode.ReplaceRoute);
        var receipt = Send(replacement);
        Check(receipt.RouteBefore.Count == 3 && receipt.RouteAfter.Count == 1 && receipt.RouteAfter[0].Label == "NC-D", "replace_multiple_waypoints");
        Check(Send(replacement).RouteBefore.Count == 3 && game.Mutations == 4, "repeat_route_idempotency");
        Check(Send(Request("legacy", RouteMode.IdleWaypoint)).Reason == "native_task_selection_active", "idle_waypoint_never_destructive");
        Check(Send(Request("unknown", (RouteMode)99)).Reason == "unsupported_route_mode", "unknown_mode_rejected");
        game.Own.Protected = true;
        Check(Send(Request("protected", RouteMode.ReplaceRoute)).Reason == "protected_native_action", "replace_protected");
        game.Own.Protected = false;
        game.Own.RouteRestriction = "non_navigation_task";
        Check(Send(Request("unrelated", RouteMode.AppendWaypoint)).Reason == "non_navigation_task", "unrelated_task_preserved");
        game.Own.RouteRestriction = "";
        var stale = Request("stale", RouteMode.ReplaceRoute);
        stale.BasedOnVersion = 0;
        Check(Send(stale).Reason == "stale_observation", "route_stale_view");
        game.Own.Own = false;
        Check(Send(Request("unauthorized", RouteMode.ReplaceRoute)).Reason == "wrong_side_or_invalid_entity", "route_wrong_side");
        game.Own.Own = true;
        initial = WorldSnapshot.Parser.ParseFrom(provider.Tick());
        Check(game.Mutations == 4 && game.Own.Route[0].Label == "NC-D", "negative_zero_mutations");
        Check(Provider.RouteEligibility(game.Own, RouteMode.AppendWaypoint).Availability == Availability.Executable, "moving_discovery");
        game.Own.Formation = true;
        Check(Send(Request("formation-route", RouteMode.ReplaceRoute)).Reason == "formation_effects_unverified", "explicit_formation_rejection");
        game.Own.Formation = false;
        game.BeforeRouteMutation = () => game.Own.Protected = true;
        Check(Send(Request("race", RouteMode.ReplaceRoute)).Reason == "protected_native_action" && game.Mutations == 4, "adjacent_mutation_revalidation");
        game.BeforeRouteMutation = null;
        game.Own.Protected = false;
        while (game.Own.Route.Count < 32)
        {
            game.Own.Route.Add(new RoutePoint { Label = "capacity" });
        }
        Check(Send(Request("capacity", RouteMode.AppendWaypoint)).Reason == "route_capacity" && game.Mutations == 4, "route_bound");
        QueuedMovement();
    }

    static void QueuedMovement()
    {
        var game = new FakeGameFacade();
        var provider = new Provider(game, "own");
        var view = WorldSnapshot.Parser.ParseFrom(provider.Tick());
        var request = new OrderRequest
        {
            SessionId = view.Session.SessionId,
            RequestId = "head",
            Subject = new Subject { OwnUnitId = view.Instances.First(unit => unit.Subject.IdentityCase == Subject.IdentityOneofCase.OwnUnitId).Subject.OwnUnitId },
            BasedOnVersion = view.ObservationVersion,
            Waypoint = new Waypoint { LatitudeDeg = 62, LongitudeDeg = -10, RouteMode = RouteMode.ReplaceRoute }
        };
        provider.Enqueue(request.ToByteArray());
        provider.Tick();
        request.RequestId = "tail";
        request.Waypoint.RouteMode = RouteMode.AppendWaypoint;
        provider.Enqueue(request.ToByteArray());
        provider.Tick();
        while (provider.Results.TryDequeue(out _))
        {
        }
        game.Own.LatitudeDeg += .001;
        provider.Tick();
        provider.Results.TryDequeue(out var bytes);
        var movement = OrderResult.Parser.ParseFrom(bytes);
        Check(movement.RequestId == "head" && movement.Stage == Stage.MovementObserved && provider.Results.IsEmpty,
            "queued_append_displacement_not_execution");
    }
}
