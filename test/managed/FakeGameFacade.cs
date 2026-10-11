using System;
using System.Collections.Generic;
using System.Linq;
using NavalCommand.SeaPower.Client;
using NavalCommand.Gateway.Wire;

sealed class FakeGameFacade : IGameFacade
{
    public bool IsReady = true;
    public bool Ready => IsReady;
    public string SourceSessionKey = "native-session";
    public string SessionKey => SourceSessionKey;
    public double OperationalSeconds => 42;

    public int Mutations;
    public Action BeforeResolve;
    public Action BeforeRouteMutation;
    public Action BeforeWaypointMutation;
    public Action BeforeLaunchMutation;
    public int LaunchMutations;
    public bool ResourcesAvailable = true;
    public Stage LaunchStage = Stage.Preparing;
    public UnitSample Spawned = new() { Key = "spawn", Own = true, Supported = true, Controllable = true, Grounded = true, Airborne = false };
    public UnitSample Own = new()
    {
        Key = "own",
        Name = "own-platform-name",
        TaskForceKey = "native-taskforce",
        Own = true,
        Supported = true,
        Controllable = true,
        RouteRestriction = "",
        RouteInspected = true,
        Airborne = false,
        LatitudeDeg = 61.96,
        LongitudeDeg = -10.37
    };
    public IReadOnlyList<UnitSample> CaptureUnits() => new[]
    {
        Own,
        new UnitSample { Key = "hidden", Own = false },
        LaunchStage == Stage.AircraftSpawned || LaunchStage == Stage.Airborne ? Spawned : new UnitSample()
    };
    public IReadOnlyList<TrackSample> Tracks = Array.Empty<TrackSample>();
    public IReadOnlyList<TrackSample> CaptureTracks() => Tracks;
    public UnitSample Resolve(string key)
    {
        BeforeResolve?.Invoke();
        return key == Own.Key ? Own : key == Spawned.Key ? Spawned : null;
    }
    public bool AppendWaypoint(string key, double latitudeDegrees, double longitudeDegrees, string label)
    {
        BeforeWaypointMutation?.Invoke();
        var current = Resolve(key);
        if (current == null || Provider.Eligibility(current).Availability != Availability.Executable)
        {
            return false;
        }
        ++Mutations;
        return true;
    }

    public void Reset()
    {
        LaunchResets++;
    }
    public int LaunchResets;
    public int LaunchPolls;
    public Stage LaunchAcceptance = Stage.NativeTaskCreated;
    public bool KeepUnknownLaunchTracking;
    public LaunchProgress LaunchFacility(string key, LaunchAircraft request)
    {
        BeforeLaunchMutation?.Invoke();
        var current = Resolve(key);
        string reason = current == null || !current.Own ? "wrong_side_or_invalid_entity"
            : !current.LaunchSupported ? "unsupported_facility"
            : !current.Controllable ? "uncontrollable"
            : current.LaunchRestriction != "" ? current.LaunchRestriction
            : request.Selection == null || request.Selection.Type == null || request.Selection.Type.Value != "class" ? "unsupported_aircraft"
            : request.Selection.Squadron != "squadron" ? "invalid_squadron"
            : request.Selection.Loadout == null || request.Selection.Loadout.Value != "loadout" ? "invalid_loadout"
            : !ResourcesAvailable ? "resources_unavailable" : "";
        if (reason != "")
        {
            return new LaunchProgress { Reason = reason };
        }
        ++LaunchMutations;
        return new LaunchProgress { Token = "launch", Stage = LaunchAcceptance, Reason = "fake_reservation", TrackingActive = true };
    }
    public LaunchProgress PollLaunch(string token)
    {
        LaunchPolls++;
        return new LaunchProgress
        {
            Stage = LaunchStage,
            Reason = "fake_progress",
            SpawnedKey = LaunchStage == Stage.AircraftSpawned || LaunchStage == Stage.Airborne ? Spawned.Key : "",
            TrackingActive = LaunchStage != Stage.Airborne && (LaunchStage != Stage.ExecutionUnknown || KeepUnknownLaunchTracking)
        };
    }

    public RouteMutation UpdateRoute(string key, double latitudeDeg, double longitudeDeg, string label, RouteMode mode)
    {
        BeforeRouteMutation?.Invoke();
        var result = new RouteMutation();
        var current = Resolve(key);
        if (current == null)
        {
            result.Reason = "wrong_side_or_invalid_entity";
            return result;
        }
        result.Before.AddRange(current.Route.Select(point => point.Clone()));
        var eligibility = Provider.RouteEligibility(current, mode);
        if (eligibility.Availability != Availability.Executable)
        {
            result.Reason = eligibility.Reason;
            return result;
        }
        ++Mutations;
        if (mode == RouteMode.ReplaceRoute)
        {
            current.Route.Clear();
        }
        current.Route.Add(new RoutePoint { LatitudeDeg = latitudeDeg, LongitudeDeg = longitudeDeg, Label = label });
        current.SelectedTask = true;
        result.After.AddRange(current.Route.Select(point => point.Clone()));
        result.Mutated = true;
        result.Reason = "fake_route_sequence_verified";
        return result;
    }
}
