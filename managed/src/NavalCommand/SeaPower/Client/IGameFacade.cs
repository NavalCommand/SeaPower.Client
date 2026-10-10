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

    IReadOnlyList<UnitSample> CaptureUnits();
    IReadOnlyList<TrackSample> CaptureTracks();
    UnitSample Resolve(string key);
    bool AppendWaypoint(string key, double latitudeDeg, double longitudeDeg, string label);
    void Reset();
}
