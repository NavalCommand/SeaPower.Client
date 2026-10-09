using System.Collections.Generic;

namespace NavalCommand.SeaPower.Client;
// Called exclusively by Provider's owning engine thread. All return values are plain data.
public interface IGameFacade
{
    bool Ready { get; }

    double OperationalSeconds { get; }

    IReadOnlyList<UnitSample> CaptureUnits();
    IReadOnlyList<TrackSample> CaptureTracks();
    UnitSample Resolve(string key);
    bool AppendWaypoint(string key, double latitudeDeg, double longitudeDeg, string label);
    void Reset();
}

public sealed class UnitSample
{
    public string Key = "", Name = "";
    public bool Own, Supported, Controllable, Protected, Grounded, Formation, SelectedTask;
    public double LatitudeDeg, LongitudeDeg, SpeedMetersPerSecond;
    public readonly List<StockSample> Stocks = new();
    public int PendingOperations;
}

public sealed class StockSample
{
    public string ClassKey = "", Squadron = "";
    public int Count;
}

public sealed class TrackSample
{
    public string Key = "";
    public double LatitudeDeg, LongitudeDeg;
}
