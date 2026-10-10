using NavalCommand.Gateway.Wire;

namespace NavalCommand.SeaPower.Client;

// Copied values used only by the engine-thread provider. Token is adapter-local.
public sealed class LaunchProgress
{
    public string Token = "";
    public Stage Stage = Stage.Rejected;
    public string Reason = "native_dispatch_refused";
    public string SpawnedKey = "";
}
