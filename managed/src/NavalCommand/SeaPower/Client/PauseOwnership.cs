using System.Collections.Generic;

namespace NavalCommand.SeaPower.Client;

// Engine-thread arbitration. Player intent is observed at native control hooks,
// not inferred from effective pause while an external owner is holding it.
public sealed class PauseOwnership
{
    readonly HashSet<string> clients = new();
    public bool Player { get; private set; }
    public bool Effective => Player || clients.Count != 0;
    public bool HasExternalOwners => clients.Count != 0;
    public double SelectedCompression { get; private set; }

    public void Reset(bool playerPaused, double selectedCompression)
    {
        clients.Clear();
        Player = playerPaused;
        SelectedCompression = selectedCompression;
    }

    public void SetPlayer(bool paused)
    {
        Player = paused;
    }

    public void SetClient(string client, bool paused)
    {
        if (paused)
        {
            clients.Add(client);
        }
        else
        {
            clients.Remove(client);
        }
    }

    public void SelectCompression(double factor)
    {
        SelectedCompression = factor;
    }

    public void ReleaseClients()
    {
        clients.Clear();
    }
}
