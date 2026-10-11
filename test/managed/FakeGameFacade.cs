using System;
using System.Collections.Generic;
using System.Linq;
using NavalCommand.SeaPower.Client;
using NavalCommand.Gateway.Wire;

sealed class FakeGameFacade : IGameFacade
{
    public bool Ready => true;
    public double OperationalSeconds => 42;

    public int Mutations;
    public Action BeforeRouteMutation;
    public int LaunchMutations;
    public bool ResourcesAvailable = true;
    public Stage LaunchStage = Stage.Preparing;
    public UnitSample Spawned = new() { Key = "spawn", Own = true, Supported = true, Controllable = true, Grounded = true, Airborne = false };
    public UnitSample Own = new()
    {
        Key = "own",
        Own = true,
        Supported = true,
        Controllable = true,
        RouteInspected = true,
        RouteRestriction = "",
        LatitudeDeg = 61.96,
        LongitudeDeg = -10.37
    };
    public IReadOnlyList<UnitSample> CaptureUnits() => new[]
    {
        Own,
        new UnitSample
        {
            Key = "hidden",
            Own = false
        },
        LaunchStage == Stage.AircraftSpawned || LaunchStage == Stage.Airborne ? Spawned : new UnitSample()
    };
    public IReadOnlyList<TrackSample> Tracks = Array.Empty<TrackSample>();
    public IReadOnlyList<TrackSample> CaptureTracks() => Tracks;
    public UnitSample Resolve(string key) => key == "own" ? Own : key == "spawn" ? Spawned : null;
    public bool AppendWaypoint(string key, double latitudeDegrees, double longitudeDegrees, string label)
    {
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
    public LaunchProgress LaunchFacility(string key, FacilityLaunch request)
    {
        string reason = !Own.LaunchSupported ? "unsupported_facility"
            : request.ClassKey != "class" ? "unsupported_aircraft"
            : request.Squadron != "squadron" ? "invalid_squadron"
            : request.Loadout != "loadout" ? "invalid_loadout"
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
            SpawnedKey = LaunchStage == Stage.AircraftSpawned || LaunchStage == Stage.Airborne ? "spawn" : "",
            TrackingActive = LaunchStage != Stage.Airborne && (LaunchStage != Stage.ExecutionUnknown || KeepUnknownLaunchTracking)
        };
    }

    public RouteMutation UpdateRoute(string key, double latitudeDeg, double longitudeDeg, string label, RouteMode mode)
    {
        BeforeRouteMutation?.Invoke();
        var result = new RouteMutation();
        result.Before.AddRange(Own.Route.Select(point => point.Clone()));
        var eligibility = Provider.RouteEligibility(Own, mode);
        if (eligibility.Availability != Availability.Executable)
        {
            result.Reason = eligibility.Reason;
            return result;
        }
        ++Mutations;
        if (mode == RouteMode.ReplaceRoute)
        {
            Own.Route.Clear();
        }
        Own.Route.Add(new RoutePoint { LatitudeDeg = latitudeDeg, LongitudeDeg = longitudeDeg, Label = label });
        Own.SelectedTask = true;
        result.After.AddRange(Own.Route.Select(point => point.Clone()));
        result.Mutated = true;
        result.Reason = "fake_route_sequence_verified";
        return result;
    }
}
