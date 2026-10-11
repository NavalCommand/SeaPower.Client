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
        var own = view.Units.Single();
        var ownId = own.Identity;
        Check(view.Ready && view.SchemaVersion == 4 && view.Observer.Value == "observer" && ownId.Value != game.Own.Key,
            "schema4_own_observation_handle_is_opaque");
        Check(ObserverSnapshot.Descriptor.FindFieldByName("simulation_snapshot") == null
            && ObserverSnapshot.Descriptor.FindFieldByName("unit_id") == null
            && UnitObservation.Descriptor.FindFieldByName("unit_id") == null
            && OrderRequest.Descriptor.FindFieldByName("unit_id") == null
            && OrderResult.Descriptor.FindFieldByName("spawned_unit_id") == null, "ordinary_wire_has_no_truth_identity");
        Check(own.Name != null && own.Name.StateCase == StringKnowledge.StateOneofCase.Known
            && own.Name.Known == game.Own.Name, "source_backed_own_name_preserved");
        Check(own.Kinematics.SpeedMps.StateCase == DoubleKnowledge.StateOneofCase.Known
            && own.Kinematics.SpeedMps.Known == 0, "known_zero_motion");
        Check(own.Navigation.PlannedRoute.StateCase == RouteKnowledge.StateOneofCase.Known
            && own.Navigation.PlannedRoute.Known.Points.Count == 0, "inspected_empty_route");
        Check(observation.OrderAvailability.Single().Observation.Equals(ownId)
            && observation.OrderAvailability[0].Observer.Equals(view.Observer)
            && observation.OrderAvailability[0].BasedOnVersion == view.Version
            && observation.OrderAvailability[0].Eligibility.Items.Any(row => row.Kind == OrderKind.ReplaceRoute),
            "observer_scoped_typed_order_overlay");
        var membership = view.Memberships.Items.Single();
        Check(membership.Observation.Equals(ownId) && game.Own.TaskForceKey != game.SourceSessionKey
            && membership.TaskForce.Known.Value != game.Own.TaskForceKey
            && membership.Faction == null && view.Memberships.Coverage == CollectionCoverage.CoveragePartial,
            "observer_membership_uses_distinct_taskforce_handle");
        var secondProvider = new Provider(game, "other-observer");
        var otherView = ProviderObservation.Parser.ParseFrom(secondProvider.Tick()).Snapshot;
        Check(otherView.Observer.Value != view.Observer.Value
            && otherView.Units.Single().Identity.Value != ownId.Value, "independent_observer_handles");

        // Plotting records are independent observations, including ambiguous/fused/false records.
        game.Tracks = new[]
        {
            new TrackSample { Key = game.Own.Key, LatitudeDeg = 1, LongitudeDeg = 2 },
            new TrackSample { Key = "false-or-fused", LatitudeDeg = 1, LongitudeDeg = 2 }
        };
        var mixed = ProviderObservation.Parser.ParseFrom(provider.Tick());
        var contacts = mixed.Snapshot.Units.Where(unit => !unit.Identity.Equals(ownId)).ToArray();
        Check(mixed.Snapshot.Units.Count == 3 && contacts.Length == 2
            && contacts[0].Identity.Value != game.Own.Key && !contacts[0].Identity.Equals(contacts[1].Identity)
            && contacts.All(contact => contact.Kinematics.Position.StateCase == PositionKnowledge.StateOneofCase.Estimated
                && contact.Kinematics.Position.Estimated.Value.Coordinates.LatitudeDeg == 1),
            "false_fused_and_duplicate_observations_without_truth_mapping");
        Check(contacts.All(contact => (contact.Name == null || contact.Name.StateCase == StringKnowledge.StateOneofCase.None)
                && contact.Type == null && contact.Equipment.Coverage == CollectionCoverage.CoverageUnknown)
            && mixed.OrderAvailability.All(row => contacts.All(contact => !row.Observation.Equals(contact.Identity))),
            "contacts_have_unknown_names_no_hidden_classification_or_authority");
        Check(contacts.All(contact => contact.Evidence?.MeasuredAtSeconds == null
                || contact.Evidence.MeasuredAtSeconds.StateCase == DoubleKnowledge.StateOneofCase.None),
            "plotting_contacts_have_no_invented_measurement_time");
        var stable = ProviderObservation.Parser.ParseFrom(provider.Tick());
        Check(stable.Snapshot.Units[0].Identity.Equals(ownId)
            && stable.Snapshot.Units[1].Identity.Equals(contacts[0].Identity)
            && stable.Snapshot.Units[2].Identity.Equals(contacts[1].Identity), "own_and_contact_handles_stable");
        Check(stable.Snapshot.Memberships.Items.Single().TaskForce.Equals(membership.TaskForce), "taskforce_handle_stable");
        game.Tracks[0].PositionAvailable = false;
        var gap = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot;
        Check(gap.Units.Count == 3 && gap.Units[1].Identity.Equals(contacts[0].Identity)
            && gap.Units[1].Kinematics.Position == null, "contact_gap_retains_identity_without_fabricating_position");
        game.Tracks[0].PositionAvailable = true;
        Check(ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot.Units[1].Identity.Equals(contacts[0].Identity),
            "contact_record_retains_handle_after_coordinate_gap");

        var request = new OrderRequest
        {
            SessionId = view.SessionId,
            Observer = view.Observer.Clone(),
            RequestId = "first",
            Observation = ownId.Clone(),
            BasedOnVersion = view.Version,
            Waypoint = new Waypoint { LatitudeDeg = 62, LongitudeDeg = -10.37 }
        };
        OrderResult Send(OrderRequest command)
        {
            provider.Enqueue(command.ToByteArray());
            provider.Tick();
            provider.Results.TryDequeue(out var result);
            return OrderResult.Parser.ParseFrom(result);
        }
        var enemyOrder = request.Clone();
        enemyOrder.RequestId = "false-contact";
        enemyOrder.Observation = contacts[1].Identity.Clone();
        Check(Send(enemyOrder).Reason == "wrong_side_or_invalid_entity" && game.Mutations == 0,
            "false_contact_never_authorizes");
        var wrongObserver = request.Clone();
        wrongObserver.RequestId = "wrong-observer";
        wrongObserver.Observer = new ObserverId { Value = "other-observer" };
        Check(Send(wrongObserver).Reason == "wrong_observer" && game.Mutations == 0,
            "request_observer_scope_required");
        game.Tracks = Array.Empty<TrackSample>();
        provider.Tick();
        game.Tracks = new[] { new TrackSample { Key = game.Own.Key, LatitudeDeg = 3, LongitudeDeg = 4 } };
        var replacementContact = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot.Units[1];
        Check(!replacementContact.Identity.Equals(contacts[0].Identity), "retired_contact_record_gets_fresh_handle");
        enemyOrder.RequestId = "retired-contact";
        enemyOrder.Observation = contacts[0].Identity.Clone();
        Check(Send(enemyOrder).Reason == "wrong_side_or_invalid_entity" && game.Mutations == 0,
            "retired_observation_never_matches_control_map");
        game.Tracks = Array.Empty<TrackSample>();
        Check(Send(request).Stage == Stage.NativeTaskCreated && game.Mutations == 1, "native_acceptance");
        Check(Send(request).Stage == Stage.NativeTaskCreated && game.Mutations == 1, "duplicate_idempotency");
        game.Own.LatitudeDeg += .001;
        provider.Tick();
        provider.Results.TryDequeue(out var movementResultBytes);
        Check(OrderResult.Parser.ParseFrom(movementResultBytes).Stage == Stage.MovementObserved, "movement_distinct");
        game.Own.Protected = true;
        var blocked = request.Clone();
        blocked.RequestId = "protected";
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
        var invalid = request.Clone();
        invalid.RequestId = "invalid";
        invalid.Waypoint.LatitudeDeg = double.NaN;
        Check(Send(invalid).Reason == "invalid_arguments", "finite_arguments");
        invalid = request.Clone();
        invalid.RequestId = "no-payload";
        invalid.ClearPayload();
        Check(Send(invalid).Reason == "invalid_arguments", "one_typed_payload_required");
        game.BeforeResolve = () => game.Own.Own = false;
        blocked.RequestId = "side-race";
        Check(Send(blocked).Reason == "wrong_side_or_invalid_entity" && game.Mutations == 1, "current_control_rechecked_after_capture");
        game.BeforeResolve = null;
        game.Own.Own = true;
        game.BeforeWaypointMutation = () => game.Own.Own = false;
        blocked.RequestId = "native-side-race";
        Check(Send(blocked).Reason == "native_dispatch_refused" && game.Mutations == 1, "native_adjacent_ownership_rechecked");
        game.BeforeWaypointMutation = null;

        provider.Tick();
        game.Own = new UnitSample
        {
            Key = "own", TaskForceKey = "native-taskforce", Own = true, Supported = true, Controllable = true,
            LatitudeDeg = 61.96, LongitudeDeg = -10.37, RouteInspected = true, RouteRestriction = ""
        };
        var reused = ProviderObservation.Parser.ParseFrom(provider.Tick());
        Check(!reused.Snapshot.Units[0].Identity.Equals(ownId), "reused_native_key_gets_new_observation_id");
        blocked.RequestId = "reused-old-handle";
        Check(Send(blocked).Reason == "wrong_side_or_invalid_entity" && game.Mutations == 1, "stale_reused_handle_rejected");
        game.Own.RouteInspected = false;
        game.Own.Route.Add(new RoutePoint { Label = "partial-uninspected" });
        game.Own.HeadingRadians = 0;
        var unknown = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot.Units[0];
        Check(unknown.Navigation == null, "uninspected_route_unknown_even_with_partial_capture");
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
            Check(bounded.Enqueue(request.ToByteArray()), "queue_slot");
        }
        Check(!bounded.Enqueue(request.ToByteArray()), "backpressure");
        var beforeChange = reused.Snapshot.SessionId;
        game.SourceSessionKey = "new-native-game";
        var afterChange = ProviderObservation.Parser.ParseFrom(provider.Tick());
        Check(afterChange.Snapshot.SessionId != beforeChange && game.LaunchResets == 1
            && !afterChange.Snapshot.Memberships.Items.Single().TaskForce.Equals(membership.TaskForce),
            "continuous_ready_session_and_relationship_handles_reset");
        Check(Send(request).Reason == "stale_session" && game.Mutations == 1, "session_change_rejects_old_order");
        provider.Invalidate();
        var invalidated = ProviderObservation.Parser.ParseFrom(provider.TryRead(out var invalidatedBytes) ? invalidatedBytes : throw new Exception("missing_invalidation"));
        Check(!invalidated.Snapshot.Ready && invalidated.Snapshot.SchemaVersion == 4 && invalidated.OrderAvailability.Count == 0, "invalidation_clears_authority");
        provider.Tick();
        Check(Send(request).Reason == "stale_session" && game.Mutations == 1, "reload_invalidation");
        Check(!provider.Enqueue(new byte[65537]), "message_bound");
        MovementTests.Run();
        LaunchTests.Run();
        LaunchLifecycleTests.Run();
    }
}
