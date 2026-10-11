using System.Collections.Generic;

namespace NavalCommand.SeaPower.Client;

// Native capture seam. Launch eligibility is mapped only to the orders overlay.
public sealed class AircraftInventorySample
{
    public sealed class LaunchOptionSample
    {
        public string Loadout = "";
        public NavalCommand.Gateway.Wire.Availability Availability;
        public string Reason = "";
    }

    public string ClassKey = "";
    public string Squadron = "";
    public int? Count;
    public readonly List<string> Loadouts = new();
    public readonly List<LaunchOptionSample> LaunchOptions = new();
}
