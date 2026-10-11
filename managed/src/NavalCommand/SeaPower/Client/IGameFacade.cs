using System.Collections.Generic;

namespace NavalCommand.SeaPower.Client;
// Called exclusively by Provider's owning engine thread. All return values are plain data.
public interface IGameFacade
{
    bool Ready
    {
        get;
    }

    double OperationalSeconds
    {
        get;
    }

    // Changes when native game or observer ownership context changes, even if ready.
    string SessionKey
    {
        get;
    }

    IReadOnlyList<UnitSample> CaptureUnits();
    IReadOnlyList<TrackSample> CaptureTracks();
    UnitSample Resolve(string key);
    bool AppendWaypoint(string key, double latitudeDeg, double longitudeDeg, string label);
    RouteMutation UpdateRoute(string key, double latitudeDeg, double longitudeDeg, string label, NavalCommand.Gateway.Wire.RouteMode mode);
    LaunchProgress LaunchFacility(string key, NavalCommand.Gateway.Wire.LaunchAircraft request);
    LaunchProgress PollLaunch(string token);
    void Reset();
}
