using System.Collections.Generic;

namespace NavalCommand.SeaPower.Client;

public sealed class UnitSample
{
    public string Key = "";
    public string Name = "";
    public string TaskForceKey;
    public bool Own;
    public bool Supported;
    public bool Controllable;
    public bool Protected;
    public bool Grounded;
    public bool Formation;
    public bool SelectedTask;
    public bool? Airborne;
    public bool LaunchSupported;
    public string LaunchRestriction = "unsupported_facility";
    public bool RouteInspected;
    public bool RouteSupported = true;
    public string RouteRestriction = "navigation_context_unverified";
    public readonly List<NavalCommand.Gateway.Wire.RoutePoint> Route = new();
    public double LatitudeDeg;
    public double LongitudeDeg;
    public double SpeedMetersPerSecond;
    public double? HeadingRadians;
    public readonly List<AircraftInventorySample> AircraftInventory = new();
    public NavalCommand.Gateway.Wire.CollectionCoverage InventoryCoverage;
    public int PendingOperations;
    public bool HasFacility;
    public bool Truncated;
    public uint? OnboardCount;
    public uint? ReservedCount;
    public uint? CooldownCount;
}
