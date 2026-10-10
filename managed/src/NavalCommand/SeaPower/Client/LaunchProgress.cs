using NavalCommand.Gateway.Wire;

namespace NavalCommand.SeaPower.Client;

// Copied values used only by the engine-thread provider. Token is adapter-local.
public sealed class LaunchProgress
{
    public string Token = "";
    public Stage Stage = Stage.Rejected;
    public string Reason = "native_dispatch_refused";
    public string SpawnedKey = "";
    // Adapter-local ownership, not a wire stage: unknown acceptance can still
    // have a live native operation that must be polled and eventually released.
    public bool TrackingActive;
}
