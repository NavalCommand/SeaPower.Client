using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    const int MaximumTrackedLaunches = 64;
    sealed class LaunchRecord
    {
        public PendingLaunchTask Task;
        public ObjectBase Facility;
        public ObjectBase Spawned;
        public EventHandler<ObjectBase> Handler;
        public LaunchLifecycle Lifecycle;
    }
    readonly Dictionary<string, LaunchRecord> launches = new();

    void ClearLaunches()
    {
        foreach (var record in launches.Values)
        {
            record.Lifecycle.Dispose();
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

    public LaunchProgress LaunchFacility(string key, AircraftLaunch request)
    {
        AssertEngineThread();
        var result = new LaunchProgress();
        if (request == null || request.Count != 1 || request.UnitTypeId == null || request.LoadoutId == null
            || request.UnitTypeId.Value.Length > 128 || request.Squadron.Length > 128 || request.LoadoutId.Value.Length > 128)
        {
            result.Reason = "invalid_arguments";
            return result;
        }
        if (!unitsByKey.TryGetValue(key, out var facility))
        {
            return result;
        }
        var deck = facility._obp?._flightDeck;
        var vehicle = deck?._vehiclesOnBoard.FirstOrDefault(candidate => candidate._fileName == request.UnitTypeId.Value);
        var squadron = vehicle?.Squadrons.FirstOrDefault(candidate => candidate._name == request.Squadron);
        var loadout = vehicle?.Loadouts.FirstOrDefault(candidate => candidate.Name == request.LoadoutId.Value);
        result.Reason = LaunchRestriction(facility, vehicle, squadron, loadout);
        if (result.Reason != "")
        {
            return result;
        }
        if (launches.Count >= MaximumTrackedLaunches)
        {
            result.Reason = "launch_ledger_full";
            return result;
        }
        var tasksBefore = new HashSet<PendingLaunchTask>(deck.FlightDeckTasks.Concat(deck._flightDeckTasksToAdd).OfType<PendingLaunchTask>());
        int stockBefore = vehicle.Numbers;
        int squadronBefore = squadron.Numbers;
        var progress = LaunchLifecycle.Start(
            () => deck.createLaunchTask(vehicle, loadout, squadron, squadron.Callsigns[0], new LaunchTaskParameters()),
            () =>
            {
                // A native exception can follow insertion but precede the return.
                // Recover only one new task matching this exact own launch request.
                var candidates = deck.FlightDeckTasks.Concat(deck._flightDeckTasksToAdd).OfType<PendingLaunchTask>()
                    .Where(candidate => !tasksBefore.Contains(candidate) && candidate.VehicleType == vehicle
                        && candidate._squadronIndex == vehicle.Squadrons.IndexOf(squadron)
                        && candidate._loadoutIndex == vehicle.Loadouts.IndexOf(loadout)).Distinct().Take(2).ToArray();
                return candidates.Length == 1 ? candidates[0] : null;
            },
            (task, progress) => RegisterLaunch(task, facility, progress),
            task => deck.FlightDeckTasks.Contains(task) && task.LaunchCount == 1
                && vehicle.Numbers == stockBefore - 1 && squadron.Numbers == squadronBefore - 1);
        if (progress.TrackingActive)
        {
            launches[progress.Token].Lifecycle.RememberAcceptance(progress);
        }
        return progress;
    }
    static double LaunchWallSeconds => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    void RegisterLaunch(PendingLaunchTask task, ObjectBase facility, LaunchProgress result)
    {
        var record = new LaunchRecord { Task = task, Facility = facility };
        record.Handler = (_, spawned) =>
        {
            AssertEngineThread();
            record.Spawned = spawned;
        };
        record.Lifecycle = new LaunchLifecycle(LaunchWallSeconds, () => task.Launched -= record.Handler);
        string token = Guid.NewGuid().ToString("N");
        try
        {
            launches.Add(token, record);
            task.Launched += record.Handler;
            result.Token = token;
            result.TrackingActive = true;
        }
        catch
        {
            record.Lifecycle.Dispose();
            launches.Remove(token);
            throw;
        }
    }

    public LaunchProgress PollLaunch(string token)
    {
        AssertEngineThread();
        if (!launches.TryGetValue(token, out var record))
        {
            return new LaunchProgress { Token = token, Stage = Stage.ExecutionUnknown, Reason = "launch_tracking_expired" };
        }
        LaunchProgress result;
        try
        {
            var spawned = record.Spawned;
            var observation = new LaunchLifecycle.Observation
            {
                SpawnCaptured = spawned != null,
                SpawnUnavailable = spawned != null && (spawned.IsDestroyed
                    || (spawned.UnitTaskforce.Value != null && spawned.UnitTaskforce.Value != Globals._playerTaskforce)),
                TaskCancelled = record.Task._isCancelled,
                FacilityUnavailable = record.Facility == null || record.Facility.IsDestroyed
                    || record.Facility.UnitTaskforce.Value != Globals._playerTaskforce
            };
            if (spawned != null && !observation.SpawnUnavailable && spawned.UnitTaskforce.Value == Globals._playerTaskforce
                && Globals._playerTaskforce._taskforceUnits.Contains(spawned) && unitKeys.TryGetValue(spawned, out var key))
            {
                var sample = SampleUnit(spawned, key);
                if (sample != null)
                {
                    observation.SpawnedKey = key;
                    observation.Airborne = sample.Airborne == true;
                }
            }
            var deck = record.Facility?._obp?._flightDeck;
            observation.TaskPending = deck != null && (deck.FlightDeckTasks.Contains(record.Task) || deck._flightDeckTasksToAdd.Contains(record.Task));
            result = record.Lifecycle.Observe(observation, LaunchWallSeconds);
        }
        catch (Exception exception)
        {
            result = record.Lifecycle.ReadFailure(LaunchWallSeconds, exception.GetType().Name);
        }
        result.Token = token;
        if (!result.TrackingActive)
        {
            launches.Remove(token);
        }
        return result;
    }
}
