using System;
using System.Collections.Generic;
using NavalCommand.SeaPower.Client;

sealed class FakeGameFacade : IGameFacade
{
    public bool Ready => true;
    public double OperationalSeconds => 42;

    public int Mutations;
    public UnitSample Own = new()
    {
        Key = "own",
        Own = true,
        Supported = true,
        Controllable = true,
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
        }
    };
    public IReadOnlyList<TrackSample> CaptureTracks() => Array.Empty<TrackSample>();
    public UnitSample Resolve(string key) => key == "own" ? Own : null;
    public bool AppendWaypoint(string key, double latitudeDegrees, double longitudeDegrees, string label)
    {
        ++Mutations;
        return true;
    }

    public void Reset()
    {
    }
}
