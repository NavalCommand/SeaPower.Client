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
        game.Own.LaunchSupported = true;
        game.Own.LaunchRestriction = "";
        var provider = new Provider(game, "own");
        var initial = WorldSnapshot.Parser.ParseFrom(provider.Tick());
        var request = new OrderRequest
        {
            SessionId = initial.Session.SessionId,
            RequestId = "launch",
            EntityId = initial.OwnUnits[0].Id,
            BasedOnVersion = initial.ObservationVersion,
            FacilityLaunch = new FacilityLaunch { ClassKey = "class", Squadron = "squadron", Loadout = "loadout", Count = 1 }
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
        invalid.FacilityLaunch.Count = 2;
        Check(Send(invalid).Reason == "invalid_arguments", "launch_bounded_count");
        game.ResourcesAvailable = false;
        invalid = request.Clone();
        invalid.RequestId = "resources";
        Check(Send(invalid).Reason == "resources_unavailable" && game.LaunchMutations == 0, "launch_resources_no_side_effect");
        game.ResourcesAvailable = true;
        game.Own.LaunchRestriction = "facility_inoperable";
        invalid.RequestId = "damage";
        Check(Send(invalid).Reason == "facility_inoperable" && game.LaunchMutations == 0, "launch_operability_recheck");
        game.Own.LaunchRestriction = "";
        Check(Send(request).Stage == Stage.NativeTaskCreated && game.LaunchMutations == 1, "launch_native_acceptance");
        Check(Send(request).Stage == Stage.Preparing && game.LaunchMutations == 1, "launch_duplicate_reservation");
        game.LaunchStage = Stage.AircraftSpawned;
        var spawnedView = WorldSnapshot.Parser.ParseFrom(provider.Tick());
        provider.Results.TryDequeue(out var spawnedBytes);
        var spawned = OrderResult.Parser.ParseFrom(spawnedBytes);
        Check(spawned.Stage == Stage.AircraftSpawned && spawnedView.OwnUnits.Any(unit => unit.Id == spawned.SpawnedEntityId
            && unit.HasAirborne && !unit.Airborne), "launch_spawn_regular_grounded_unit");
        game.LaunchStage = Stage.Airborne;
        game.Spawned.Grounded = false;
        game.Spawned.Airborne = true;
        provider.Tick();
        provider.Results.TryDequeue(out var airborneBytes);
        Check(OrderResult.Parser.ParseFrom(airborneBytes).Stage == Stage.Airborne, "launch_airborne_distinct");
    }
}
