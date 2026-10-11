using Google.Protobuf;
using NavalCommand.Gateway.Wire;
using NavalCommand.SeaPower.Client;
using System;
using System.Linq;
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
        var observation = ProviderObservation.Parser.ParseFrom(provider.Tick());
        var view = observation.Snapshot;
        var ownId = view.Instances.Single().UnitId;
        Check(view.Session.SchemaVersion == 3 && ownId.Value != game.Own.Key, "own_opaque_unit_identity");
        Check(view.Instances[0].Equipment.Coverage == CollectionCoverage.CoverageUnknown, "uncaptured_equipment_unknown");
        Check(view.Instances[0].Kinematics.SpeedMps.StateCase == DoubleKnowledge.StateOneofCase.Known
            && view.Instances[0].Kinematics.SpeedMps.Known == 0, "known_zero_motion");
        Check(view.Instances[0].Navigation.PlannedRoute.StateCase == RouteKnowledge.StateOneofCase.Known
            && view.Instances[0].Navigation.PlannedRoute.Known.Points.Count == 0, "inspected_empty_route");
        Check(observation.OrderAvailability.Single().UnitId.Equals(ownId)
            && observation.OrderAvailability[0].BasedOnVersion == view.ObservationVersion
            && observation.OrderAvailability[0].Eligibility.Items.Any(row => row.Kind == OrderKind.ReplaceRoute), "separate_typed_order_overlay");
        var membership = view.Memberships.Items.Single();
        Check(membership.UnitId.Equals(ownId) && membership.TaskForce.Known != game.Own.TaskForceKey
            && membership.Faction == null && view.Memberships.Coverage == CollectionCoverage.CoveragePartial, "world_membership_observer_safe");
        // Matching native text across independent sources must not imply identity or authority.
        game.Tracks = new[] { new TrackSample { Key = game.Own.Key, LatitudeDeg = 1, LongitudeDeg = 2 } };
        var mixed = ProviderObservation.Parser.ParseFrom(provider.Tick());
        var contact = mixed.Snapshot.Instances.Single(unit => !unit.UnitId.Equals(ownId));
        Check(mixed.Snapshot.Instances.Count == 2 && contact.UnitId.Value != game.Own.Key
            && contact.Kinematics.Position.StateCase == PositionKnowledge.StateOneofCase.Estimated
            && contact.Kinematics.Position.Estimated.Value.Coordinates.LatitudeDeg == 1, "observer_contact_same_identity_type");
        Check(contact.Display == null && contact.Equipment.Coverage == CollectionCoverage.CoverageUnknown
            && mixed.OrderAvailability.All(row => !row.UnitId.Equals(contact.UnitId)), "contact_no_hidden_identity_or_authority");
        Check(contact.Observation.MeasuredAtSeconds == null && contact.Kinematics.Position.Estimated.Source == "",
            "cached_measurement_epoch_and_sensor_identity_not_invented");
        var stable = ProviderObservation.Parser.ParseFrom(provider.Tick());
        Check(stable.Snapshot.Instances[0].UnitId.Equals(ownId)
            && stable.Snapshot.Instances[1].UnitId.Equals(contact.UnitId), "own_and_contact_handles_stable");
        Check(stable.Snapshot.Memberships.Items.Single().TaskForce.Equals(membership.TaskForce), "taskforce_handle_stable");
        game.Tracks[0].PositionAvailable = false;
        Check(ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot.Instances.Count == 1,
            "contact_coordinate_gap_does_not_fabricate_position");
        game.Tracks[0].PositionAvailable = true;
        Check(ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot.Instances[1].UnitId.Equals(contact.UnitId),
            "same_contact_record_retains_handle_across_coordinate_gap");
        var waypointRequest = new OrderRequest
        {
            SessionId = view.Session.SessionId,
            RequestId = "first",
            UnitId = ownId.Clone(),
            BasedOnVersion = view.ObservationVersion,
            Waypoint = new Waypoint { LatitudeDeg = 62, LongitudeDeg = -10.37 }
        };
        OrderResult Send(OrderRequest request)
        {
            provider.Enqueue(request.ToByteArray());
            provider.Tick();
            provider.Results.TryDequeue(out var result);
            return OrderResult.Parser.ParseFrom(result);
        }
        var enemyOrder = waypointRequest.Clone();
        enemyOrder.RequestId = "enemy";
        enemyOrder.UnitId = contact.UnitId.Clone();
        Check(Send(enemyOrder).Reason == "wrong_side_or_invalid_entity" && game.Mutations == 0, "enemy_unit_id_never_authorizes");
        game.Tracks = Array.Empty<TrackSample>();
        provider.Tick();
        game.Tracks = new[] { new TrackSample { Key = game.Own.Key, LatitudeDeg = 3, LongitudeDeg = 4 } };
        var replacementContact = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot.Instances[1];
        Check(!replacementContact.UnitId.Equals(contact.UnitId), "retired_contact_key_reuse_new_identity");
        enemyOrder.RequestId = "retired-enemy-handle";
        Check(Send(enemyOrder).Reason == "wrong_side_or_invalid_entity" && game.Mutations == 0,
            "retired_enemy_identity_never_matches_control_map");
        game.Tracks = Array.Empty<TrackSample>();
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
        var invalid = waypointRequest.Clone();
        invalid.RequestId = "invalid";
        invalid.Waypoint.LatitudeDeg = double.NaN;
        Check(Send(invalid).Reason == "invalid_arguments", "finite_arguments");
        invalid = waypointRequest.Clone();
        invalid.RequestId = "no-payload";
        invalid.ClearPayload();
        Check(Send(invalid).Reason == "invalid_arguments", "one_typed_payload_required");
        game.BeforeResolve = () => game.Own.Own = false;
        blocked.RequestId = "side-race";
        Check(Send(blocked).Reason == "wrong_side_or_invalid_entity" && game.Mutations == 1, "current_side_rechecked_after_capture");
        game.BeforeResolve = null;
        game.Own.Own = true;
        game.BeforeWaypointMutation = () => game.Own.Own = false;
        blocked.RequestId = "native-side-race";
        Check(Send(blocked).Reason == "native_dispatch_refused" && game.Mutations == 1, "native_adjacent_ownership_rechecked");
        game.BeforeWaypointMutation = null;
        // Ownership lost removes the handle even if a later object reuses the native key.
        provider.Tick();
        game.Own = new UnitSample
        {
            Key = "own", Own = true, Supported = true, Controllable = true,
            LatitudeDeg = 61.96, LongitudeDeg = -10.37, RouteInspected = true, RouteRestriction = ""
        };
        var reused = ProviderObservation.Parser.ParseFrom(provider.Tick());
        Check(!reused.Snapshot.Instances[0].UnitId.Equals(ownId), "reused_native_key_new_public_identity");
        blocked.RequestId = "reused-old-handle";
        Check(Send(blocked).Reason == "wrong_side_or_invalid_entity" && game.Mutations == 1, "stale_reused_handle_rejected");
        game.Own.RouteInspected = false;
        game.Own.Route.Add(new RoutePoint { Label = "partial-uninspected" });
        game.Own.HeadingRadians = 0;
        var unknown = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot.Instances[0];
        Check(unknown.Navigation.PlannedRoute == null, "uninspected_route_unknown_even_with_partial_capture");
        Check(unknown.Kinematics.HeadingRad.StateCase == DoubleKnowledge.StateOneofCase.Known
            && unknown.Kinematics.HeadingRad.Known == 0 && unknown.Kinematics.CourseOverGroundRad == null,
            "source_heading_zero_not_course");
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
        var beforeChange = reused.Snapshot.Session.SessionId;
        game.SourceSessionKey = "new-native-game";
        var afterChange = ProviderObservation.Parser.ParseFrom(provider.Tick());
        Check(afterChange.Snapshot.Session.SessionId != beforeChange && game.LaunchResets == 1,
            "continuous_ready_source_session_invalidates");
        Check(Send(waypointRequest).Reason == "stale_session" && game.Mutations == 1, "session_change_rejects_old_order");
        provider.Invalidate();
        var invalidated = ProviderObservation.Parser.ParseFrom(provider.TryRead(out var invalidatedBytes) ? invalidatedBytes : throw new Exception("missing_invalidation"));
        Check(!invalidated.Snapshot.Session.Ready && invalidated.OrderAvailability.Count == 0, "invalidation_clears_authority");
        provider.Tick();
        Check(Send(waypointRequest).Reason == "stale_session" && game.Mutations == 1, "reload_invalidation");
        Check(!provider.Enqueue(new byte[65537]), "message_bound");
        MovementTests.Run();
        LaunchTests.Run();
        LaunchLifecycleTests.Run();
    }
}
