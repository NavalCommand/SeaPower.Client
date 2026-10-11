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
        game.Own.OnboardCount = 0;
        var inventory = new AircraftInventorySample { ClassKey = "class", Squadron = "squadron", Count = 0 };
        inventory.Loadouts.Add("loadout");
        inventory.Loadouts.Add("alternate");
        inventory.LaunchOptions.Add(new AircraftInventorySample.LaunchOptionSample
        {
            Loadout = "loadout", Availability = Availability.Blocked, Reason = "inventory_unavailable"
        });
        game.Own.AircraftInventory.Add(inventory);
        var provider = new Provider(game, "own");
        var observed = ProviderObservation.Parser.ParseFrom(provider.Tick());
        var initial = observed.Snapshot;
        var airOperations = initial.Units.Single().AirOperations;
        var inventoryEntry = airOperations.Inventory.Single();
        Check(initial.Units.Count == 1 && inventoryEntry.Type.Value == "class"
            && inventoryEntry.Squadron == "squadron" && inventoryEntry.Loadouts.Count == 2
            && inventoryEntry.Loadouts.Any(loadout => loadout.Value == "loadout"),
            "reported_air_inventory_attached_to_platform_observation");
        Check(inventoryEntry.HasUnreservedCount && inventoryEntry.UnreservedCount == 0
            && airOperations.HasOnboardCount && airOperations.OnboardCount == 0 && !airOperations.HasReservedCount,
            "exact_air_inventory_zero_and_unknown_lifecycle_counts");
        var launchAvailability = observed.OrderAvailability.Single().Eligibility.Items.Single(row => row.Kind == OrderKind.LaunchAircraft);
        Check(launchAvailability.Availability == Availability.Blocked && launchAvailability.Reason == "inventory_unavailable"
            && launchAvailability.LaunchOption.Selection.Type.Equals(inventoryEntry.Type)
            && launchAvailability.LaunchOption.Selection.Squadron == inventoryEntry.Squadron
            && inventoryEntry.Loadouts.Any(loadout => loadout.Equals(launchAvailability.LaunchOption.Selection.Loadout))
            && observed.OrderAvailability[0].Observation.Equals(initial.Units[0].Identity),
            "launch_selectors_separate_from_single_grouped_stock_row");
        Check(AircraftInventoryEntry.Descriptor.FindFieldByName("selection") == null
            && AircraftInventoryEntry.Descriptor.FindFieldByName("loadouts") != null
            && LaunchAircraft.Descriptor.FindFieldByName("unit_type_id") == null
            && UnitObservation.Descriptor.FindFieldByName("order_eligibility") == null
            && ObserverSnapshot.Descriptor.FindFieldByName("facilities") == null
            && OrderResult.Descriptor.FindFieldByName("spawned_unit_id") == null,
            "no_truth_identity_or_duplicate_facility_eligibility");
        inventory.Count = null;
        game.Own.Truncated = true;
        observed = ProviderObservation.Parser.ParseFrom(provider.Tick());
        inventoryEntry = observed.Snapshot.Units[0].AirOperations.Inventory[0];
        Check(!inventoryEntry.HasUnreservedCount && observed.Snapshot.Truncated,
            "unreported_inventory_count_remains_unknown");
        game.Own.AircraftInventory.Clear();
        observed = ProviderObservation.Parser.ParseFrom(provider.Tick());
        Check(observed.Snapshot.Units[0].AirOperations.Inventory.Count == 0
            && observed.OrderAvailability[0].Eligibility.Items.Single(row => row.Kind == OrderKind.LaunchAircraft).Availability == Availability.Unknown,
            "empty_reported_list_does_not_claim_exhaustive_inventory");
        game.Own.Truncated = false;
        var noLoadout = new AircraftInventorySample
        {
            ClassKey = "unselected-class",
            Squadron = "unselected-squadron",
            Count = 1
        };
        game.Own.AircraftInventory.Add(noLoadout);
        observed = ProviderObservation.Parser.ParseFrom(provider.Tick());
        var noLoadoutEntry = observed.Snapshot.Units[0].AirOperations.Inventory.Single();
        Check(noLoadoutEntry.Type.Value == "unselected-class" && noLoadoutEntry.Squadron == "unselected-squadron"
            && noLoadoutEntry.UnreservedCount == 1 && noLoadoutEntry.Loadouts.Count == 0,
            "grouped_native_stock_and_empty_reported_loadouts");
        game.Own.AircraftInventory.Clear();
        inventory.Count = 1;
        inventory.LaunchOptions[0].Availability = Availability.Executable;
        inventory.LaunchOptions[0].Reason = "";
        game.Own.AircraftInventory.Add(inventory);
        game.Own.Truncated = false;
        initial = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot;
        var request = new OrderRequest
        {
            SessionId = initial.SessionId,
            RequestId = "launch",
            Observer = initial.Observer.Clone(),
            Observation = initial.Units[0].Identity.Clone(),
            BasedOnVersion = initial.Version,
            LaunchAircraft = new LaunchAircraft
            {
                Selection = new AircraftSelection
                {
                    Type = new UnitTypeId { Value = "class" },
                    Squadron = "squadron",
                    Loadout = new LoadoutId { Value = "loadout" }
                },
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
        invalid.LaunchAircraft.Count = 2;
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
        var spawnedUnit = spawnedObservation.Snapshot.Units.Single(unit => unit.Identity.Equals(spawned.SpawnedObservationId));
        Check(spawned.Stage == Stage.AircraftSpawned && spawned.Observer.Equals(spawnedObservation.Snapshot.Observer)
            && spawned.SpawnedObservationId != null
            && spawnedUnit.Aircraft.HasAirborne && !spawnedUnit.Aircraft.Airborne,
            "launch_spawn_observation_handle_grounded");
        Check(spawnedObservation.OrderAvailability.Single(row => row.Observation.Equals(spawned.SpawnedObservationId))
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
