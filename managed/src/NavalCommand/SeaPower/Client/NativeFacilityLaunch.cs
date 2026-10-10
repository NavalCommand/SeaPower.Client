using System;
using System.Collections.Generic;
using System.Linq;
using NavalCommand.Gateway.Wire;
using SeaPower;

namespace NavalCommand.SeaPower.Client;

public sealed partial class NativeGameFacade
{
    // Adapter capability scope, not protocol identifiers or a universal catalog.
    // Other assets/loadouts require their own native safety and launch evidence.
    const string ValidatedFacilityClass = "airfield_small_1";
    const string ValidatedAircraftClass = "usaf_f-4e";
    const string ValidatedLoadout = "AirToAir";
    sealed class LaunchRecord
    {
        public PendingLaunchTask Task;
        public ObjectBase Facility;
        public ObjectBase Spawned;
        public EventHandler<ObjectBase> Handler;
    }
    readonly Dictionary<string, LaunchRecord> launches = new();

    void ClearLaunches()
    {
        foreach (var record in launches.Values)
        {
            record.Task.Launched -= record.Handler;
        }
        launches.Clear();
    }

    string FacilityRestriction(ObjectBase facility)
    {
        var deck = facility?._obp?._flightDeck;
        if (facility is not LandUnit || deck == null || facility._obp._objectIniName != ValidatedFacilityClass)
        {
            return "unsupported_facility";
        }
        if (facility.IsDestroyed || facility.UnitTaskforce.Value != Globals._playerTaskforce
            || !Globals._playerTaskforce._taskforceUnits.Contains(facility))
        {
            return "wrong_side_or_invalid_entity";
        }
        if (!facility.HasControl || !facility.AcceptsOrdersFromPlayer)
        {
            return "uncontrollable";
        }
        if (deck.Inoperable.Value)
        {
            return "facility_inoperable";
        }
        if (deck.GroundCrewCount < 1 || deck.DeckParkSlots < 1 || deck._elevators.Count == 0 || deck._launchPoints.Count == 0)
        {
            return "facility_execution_unavailable";
        }
        if (deck.FlightDeckTasks.Count != 0 || deck._flightDeckTasksToAdd.Count != 0)
        {
            return "facility_operation_active";
        }
        return "";
    }

    string LaunchRestriction(ObjectBase facility, VehicleTypeOnBoard vehicle, Squadron squadron, Loadout loadout)
    {
        string restriction = FacilityRestriction(facility);
        if (restriction != "")
        {
            return restriction;
        }
        var deck = facility._obp._flightDeck;
        if (vehicle == null || vehicle._fileName != ValidatedAircraftClass || !deck._vehiclesOnBoard.Contains(vehicle) || vehicle._objectType != ObjectBase.ObjectType.Aircraft || vehicle._type == VehicleType.VTOL
            || !deck._supportedAirVehicleTypes.Contains(vehicle._fileName))
        {
            return "unsupported_aircraft";
        }
        if (squadron == null || !vehicle.Squadrons.Contains(squadron) || squadron.Callsigns.Count == 0)
        {
            return "invalid_squadron";
        }
        if (vehicle.Numbers < 1 || squadron.Numbers < 1)
        {
            return "inventory_unavailable";
        }
        if (loadout == null || !vehicle.Loadouts.Contains(loadout) || loadout.Name == "None")
        {
            return "invalid_loadout";
        }
        if (loadout.Name != ValidatedLoadout)
        {
            return "unsupported_loadout";
        }
        if (!deck.CanLaunchVehicle(loadout, 1))
        {
            return "resources_unavailable";
        }
        // Availability does not override native runway occupancy or taxi gates:
        // a compatible static path permits preparation; native execution may wait.
        bool supportedPath = deck._elevators.Any(elevator => elevator.CheckSupport(vehicle._fileName, vehicle._unitRoles, vehicle._isLarge, vehicle._isVeryLarge, "")
            && elevator._launchPoints.Any(index => index >= 1
            && index <= deck._launchPoints.Count && deck._launchPoints[index - 1]._allowedTypes.Contains(vehicle._type)
            && deck._launchPoints[index - 1].CheckSupport(vehicle._fileName, vehicle._unitRoles, vehicle._isLarge, vehicle._isVeryLarge, "")));
        return supportedPath ? "" : "launch_layout_unsupported";
    }

    public LaunchProgress LaunchFacility(string key, FacilityLaunch request)
    {
        AssertEngineThread();
        var result = new LaunchProgress();
        if (request == null || request.Count != 1 || request.ClassKey.Length > 128 || request.Squadron.Length > 128 || request.Loadout.Length > 128)
        {
            result.Reason = "invalid_arguments";
            return result;
        }
        if (!unitsByKey.TryGetValue(key, out var facility))
        {
            return result;
        }
        var deck = facility._obp?._flightDeck;
        var vehicle = deck?._vehiclesOnBoard.FirstOrDefault(candidate => candidate._fileName == request.ClassKey);
        var squadron = vehicle?.Squadrons.FirstOrDefault(candidate => candidate._name == request.Squadron);
        var loadout = vehicle?.Loadouts.FirstOrDefault(candidate => candidate.Name == request.Loadout);
        result.Reason = LaunchRestriction(facility, vehicle, squadron, loadout);
        if (result.Reason != "")
        {
            return result;
        }
        if (launches.Count >= 64)
        {
            result.Reason = "launch_ledger_full";
            return result;
        }
        try
        {
            int stockBefore = vehicle.Numbers;
            int squadronBefore = squadron.Numbers;
            var task = deck.createLaunchTask(vehicle, loadout, squadron, squadron.Callsigns[0], new LaunchTaskParameters());
            if (task == null)
            {
                result.Reason = "native_launch_refused";
                return result;
            }
            var record = new LaunchRecord { Task = task, Facility = facility };
            record.Handler = (_, spawned) =>
            {
                AssertEngineThread();
                record.Spawned = spawned;
            };
            task.Launched += record.Handler;
            result.Token = Guid.NewGuid().ToString("N");
            launches.Add(result.Token, record);
            bool reservationVerified = deck.FlightDeckTasks.Contains(task) && task.LaunchCount == 1
                && vehicle.Numbers == stockBefore - 1 && squadron.Numbers == squadronBefore - 1;
            result.Stage = reservationVerified ? Stage.NativeTaskCreated : Stage.ExecutionUnknown;
            result.Reason = reservationVerified ? "native_launch_reservation_verified" : "native_launch_reservation_unknown";
        }
        catch (Exception exception)
        {
            result.Stage = Stage.ExecutionUnknown;
            result.Reason = "native_launch_exception:" + exception.GetType().Name;
        }
        return result;
    }

    public LaunchProgress PollLaunch(string token)
    {
        AssertEngineThread();
        var result = new LaunchProgress { Token = token, Stage = Stage.ExecutionUnknown, Reason = "launch_lost" };
        if (!launches.TryGetValue(token, out var record))
        {
            return result;
        }
        var spawned = record.Spawned;
        if (spawned != null && !spawned.IsDestroyed && spawned.UnitTaskforce.Value == Globals._playerTaskforce
            && Globals._playerTaskforce._taskforceUnits.Contains(spawned))
        {
            // CaptureUnits registers every normal own object, including grounded spawns.
            if (unitKeys.TryGetValue(spawned, out var key))
            {
                result.SpawnedKey = key;
                var sample = SampleUnit(spawned, key);
                result.Stage = sample?.Airborne == true ? Stage.Airborne : Stage.AircraftSpawned;
                result.Reason = sample?.Airborne == true ? "native_airborne_observed" : "native_spawn_membership_observed";
            }
        }
        else if (record.Facility != null && !record.Facility.IsDestroyed && !record.Task._isCancelled
            && record.Facility._obp._flightDeck.FlightDeckTasks.Contains(record.Task))
        {
            result.Stage = Stage.Preparing;
            result.Reason = "native_pending_preparation_observed";
        }
        if (result.Stage == Stage.Airborne || result.Stage == Stage.ExecutionUnknown)
        {
            record.Task.Launched -= record.Handler;
            launches.Remove(token);
        }
        return result;
    }
}
