using System;
using System.Linq;
using Google.Protobuf;
using NavalCommand.Gateway.Wire;
using NavalCommand.SeaPower.Client;

static class LaunchTests
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
        game.Own.HasFacility = true;
        game.Own.LaunchSupported = true;
        game.Own.LaunchRestriction = "";
        game.Own.InventoryCoverage = CollectionCoverage.CoverageComplete;
        game.Own.OnboardCount = 0;
        var inventory = new AircraftInventorySample { ClassKey = "class", Squadron = "squadron", Count = 0 };
        inventory.Loadouts.Add("loadout");
        inventory.LaunchOptions.Add(new AircraftInventorySample.LaunchOptionSample
        {
            Loadout = "loadout", Availability = Availability.Blocked, Reason = "inventory_unavailable"
        });
        game.Own.AircraftInventory.Add(inventory);
        var provider = new Provider(game, "own");
        var observed = ProviderObservation.Parser.ParseFrom(provider.Tick());
        var initial = observed.Snapshot;
        var airOperations = initial.Instances.Single().AirOperations;
        var inventoryEntry = airOperations.Inventory.Items.Single();
        Check(initial.Instances.Count == 1 && airOperations.Inventory.Coverage == CollectionCoverage.CoverageComplete
            && inventoryEntry.UnitTypeId.Value == "class" && inventoryEntry.Loadouts.Single().Value == "loadout",
            "air_operations_attached_to_regular_unit");
        Check(inventoryEntry.UnreservedCount.StateCase == UInt32Knowledge.StateOneofCase.Known
            && inventoryEntry.UnreservedCount.Known == 0 && airOperations.OnboardCount.StateCase == UInt32Knowledge.StateOneofCase.Known
            && airOperations.OnboardCount.Known == 0 && airOperations.ReservedCount == null,
            "air_inventory_known_zero_and_unknown_lifecycle_counts");
        var launchAvailability = observed.OrderAvailability.Single().Eligibility.Items.Single(row => row.Kind == OrderKind.LaunchAircraft);
        Check(launchAvailability.Availability == Availability.Blocked && launchAvailability.Reason == "inventory_unavailable"
            && launchAvailability.LaunchOption.UnitTypeId.Equals(inventoryEntry.UnitTypeId)
            && launchAvailability.LaunchOption.LoadoutId.Equals(inventoryEntry.Loadouts.Single())
            && observed.OrderAvailability[0].UnitId.Equals(initial.Instances[0].UnitId), "launch_selectors_only_in_orders_overlay");
        Check(AircraftInventoryEntry.Descriptor.FindFieldByName("launch_options") == null
            && UnitInstance.Descriptor.FindFieldByName("order_eligibility") == null
            && WorldSnapshot.Descriptor.FindFieldByName("facilities") == null, "no_duplicate_public_facility_or_eligibility");
        inventory.Count = null;
        game.Own.InventoryCoverage = CollectionCoverage.CoveragePartial;
        game.Own.Truncated = true;
        observed = ProviderObservation.Parser.ParseFrom(provider.Tick());
        Check(observed.Snapshot.Instances[0].AirOperations.Inventory.Coverage == CollectionCoverage.CoveragePartial
            && observed.Snapshot.Instances[0].AirOperations.Inventory.Items[0].UnreservedCount == null
            && observed.OrderAvailability[0].Eligibility.Coverage == CollectionCoverage.CoveragePartial
            && observed.Snapshot.Truncated, "partial_inventory_and_unknown_count_preserved");
        game.Own.AircraftInventory.Clear();
        game.Own.InventoryCoverage = CollectionCoverage.CoverageUnknown;
        observed = ProviderObservation.Parser.ParseFrom(provider.Tick());
        Check(observed.Snapshot.Instances[0].AirOperations.Inventory.Coverage == CollectionCoverage.CoverageUnknown
            && observed.Snapshot.Instances[0].AirOperations.Inventory.Items.Count == 0
            && observed.OrderAvailability[0].Eligibility.Items.Single(row => row.Kind == OrderKind.LaunchAircraft).Availability == Availability.Unknown,
            "unknown_inventory_never_claims_empty_or_executable");
        game.Own.InventoryCoverage = CollectionCoverage.CoverageComplete;
        observed = ProviderObservation.Parser.ParseFrom(provider.Tick());
        Check(observed.Snapshot.Instances[0].AirOperations.Inventory.Coverage == CollectionCoverage.CoverageComplete
            && observed.Snapshot.Instances[0].AirOperations.Inventory.Items.Count == 0
            && observed.OrderAvailability[0].Eligibility.Items.Single(row => row.Kind == OrderKind.LaunchAircraft).Availability == Availability.Blocked,
            "complete_empty_air_inventory");
        inventory.Count = 1;
        inventory.LaunchOptions[0].Availability = Availability.Executable;
        inventory.LaunchOptions[0].Reason = "";
        game.Own.AircraftInventory.Add(inventory);
        game.Own.Truncated = false;
        initial = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot;
        var request = new OrderRequest
        {
            SessionId = initial.Session.SessionId,
            RequestId = "launch",
            UnitId = initial.Instances[0].UnitId.Clone(),
            BasedOnVersion = initial.ObservationVersion,
            AircraftLaunch = new AircraftLaunch
            {
                UnitTypeId = new UnitTypeId { Value = "class" },
                Squadron = "squadron",
                LoadoutId = new LoadoutId { Value = "loadout" },
                Count = 1
            }
        };
        OrderResult Send(OrderRequest command)
        {
            provider.Enqueue(command.ToByteArray());
            provider.Tick();
            provider.Results.TryDequeue(out var bytes);
            var result = OrderResult.Parser.ParseFrom(bytes);
            while (provider.Results.TryDequeue(out _))
            {
            }
            return result;
        }
        var invalid = request.Clone();
        invalid.RequestId = "count";
        invalid.AircraftLaunch.Count = 2;
        Check(Send(invalid).Reason == "invalid_arguments", "launch_bounded_count");
        invalid = request.Clone();
        invalid.RequestId = "stale";
        invalid.BasedOnVersion = 0;
        Check(Send(invalid).Reason == "stale_observation" && game.LaunchMutations == 0, "launch_stale_observation");
        game.ResourcesAvailable = false;
        invalid = request.Clone();
        invalid.RequestId = "resources";
        Check(Send(invalid).Reason == "resources_unavailable" && game.LaunchMutations == 0, "launch_resources_no_side_effect");
        game.ResourcesAvailable = true;
        game.Own.LaunchRestriction = "facility_inoperable";
        invalid.RequestId = "damage";
        Check(Send(invalid).Reason == "facility_inoperable" && game.LaunchMutations == 0, "launch_operability_recheck");
        game.Own.LaunchRestriction = "";
        game.BeforeLaunchMutation = () => game.Own.Own = false;
        invalid.RequestId = "ownership-race";
        Check(Send(invalid).Reason == "wrong_side_or_invalid_entity" && game.LaunchMutations == 0, "launch_adjacent_ownership_recheck");
        game.BeforeLaunchMutation = null;
        game.Own.Own = true;
        Check(Send(request).Stage == Stage.NativeTaskCreated && game.LaunchMutations == 1, "launch_native_acceptance");
        Check(Send(request).Stage == Stage.Preparing && game.LaunchMutations == 1, "launch_duplicate_reservation");
        game.LaunchStage = Stage.AircraftSpawned;
        var spawnedObservation = ProviderObservation.Parser.ParseFrom(provider.Tick());
        provider.Results.TryDequeue(out var spawnedBytes);
        var spawned = OrderResult.Parser.ParseFrom(spawnedBytes);
        var spawnedUnit = spawnedObservation.Snapshot.Instances.Single(unit => unit.UnitId.Equals(spawned.SpawnedUnitId));
        Check(spawned.Stage == Stage.AircraftSpawned && spawnedUnit.Aircraft.Airborne.StateCase == BoolKnowledge.StateOneofCase.Known
            && !spawnedUnit.Aircraft.Airborne.Known, "launch_spawn_regular_grounded_unit");
        Check(spawnedObservation.OrderAvailability.Single(row => row.UnitId.Equals(spawned.SpawnedUnitId))
            .Eligibility.Items.Single(row => row.Kind == OrderKind.IdleWaypoint).Availability == Availability.Unknown,
            "grounded_spawn_orders_protected");
        game.LaunchStage = Stage.Airborne;
        game.Spawned.Grounded = false;
        game.Spawned.Airborne = true;
        provider.Tick();
        provider.Results.TryDequeue(out var airborneBytes);
        Check(OrderResult.Parser.ParseFrom(airborneBytes).Stage == Stage.Airborne, "launch_airborne_distinct");
    }
}
