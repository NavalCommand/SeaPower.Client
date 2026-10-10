using System.Collections.Generic;

namespace NavalCommand.SeaPower.Client;

public sealed class StockSample
{
    public string ClassKey = "";
    public string Squadron = "";
    public int Count;
    public readonly List<NavalCommand.Gateway.Wire.LaunchOption> LaunchOptions = new();
}
