using System.Collections.Generic;

namespace NavalCommand.SeaPower.Client;

public sealed class UnitSample
{
    public string Key = "";
    public string Name = "";
    public bool Own;
    public bool Supported;
    public bool Controllable;
    public bool Protected;
    public bool Grounded;
    public bool Formation;
    public bool SelectedTask;
    public double LatitudeDeg;
    public double LongitudeDeg;
    public double SpeedMetersPerSecond;
    public readonly List<StockSample> Stocks = new();
    public int PendingOperations;
}
