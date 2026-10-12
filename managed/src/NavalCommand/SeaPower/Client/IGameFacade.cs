using NavalCommand.Gateway.Wire;

namespace NavalCommand.SeaPower.Client;

// Every call is on the owning engine thread. Returned publications contain
// copied wire values only; native object references never cross this boundary.
public interface IGameFacade
{
    bool Ready { get; }
    string SessionKey { get; }
    bool Paused { get; }
    double SelectedCompression { get; }
    bool SupportsCompression(double factor);
    void ApplyPause(bool paused);
    void SelectCompression(double factor, bool remainPaused);
    Publication Capture(string session, ulong revision, bool paused, double selectedCompression);
    void Reset();
}
