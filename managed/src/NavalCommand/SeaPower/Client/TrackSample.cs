namespace NavalCommand.SeaPower.Client;

public sealed class TrackSample
{
    // Plotting-record key is separate from controlled-source keys and never goes on the wire.
    public string Key = "";
    // Presence retains record identity across a temporary gap in cached coordinates.
    public bool PositionAvailable = true;
    public double LatitudeDeg;
    public double LongitudeDeg;
}
