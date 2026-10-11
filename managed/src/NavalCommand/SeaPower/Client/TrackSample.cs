namespace NavalCommand.SeaPower.Client;

public sealed class TrackSample
{
    public string Key = "";
    // Presence retains record identity across a temporary gap in cached coordinates.
    public bool PositionAvailable = true;
    public double LatitudeDeg;
    public double LongitudeDeg;
}
