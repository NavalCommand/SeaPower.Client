using System;
using System.Threading.Tasks;
using Google.Protobuf;
using NavalCommand.Gateway.Wire;
using NavalCommand.SeaPower.Client;

static void Check(bool passed, string description)
{
    if (!passed)
    {
        throw new InvalidOperationException(description);
    }
}

var game = new FakeGame();
var provider = new Provider(game);
provider.Synchronize();
var session = Publication.Parser.ParseFrom(provider.Capture()).Session;
int request = 0;
Control Command(string client)
{
    return new Control { Client = client, Session = session, Request = (++request).ToString(),
        ExpiresUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds() };
}
void Pause(string client, bool paused)
{
    var control = Command(client);
    control.Pause = paused;
    Check(provider.Apply(control.ToByteArray()).Result.ValueCase == Result.ValueOneofCase.Success, "pause applied");
}
void Factor(double factor)
{
    var control = Command("first");
    control.Compression = factor;
    Check(provider.Apply(control.ToByteArray()).Result.ValueCase == Result.ValueOneofCase.Success, "factor applied");
}

Pause("first", true);
Pause("second", true);
Pause("first", false);
Check(game.Paused, "second client retains pause");
provider.ObservePlayerPause(true);
Pause("second", false);
Check(game.Paused, "player retains pause");
provider.ObservePlayerPause(false);
Pause("first", false);
Check(!game.Paused, "all claims released");

Pause("first", true);
Factor(4);
Check(game.Paused && provider.SelectedCompression == 4 && game.SelectedCompression == 4, "factor selection while paused preserves claim");
provider.ObservePlayerCompression(2);
game.SelectedCompression = 2;
Check(provider.EffectivePause && provider.SelectedCompression == 2, "later native factor wins while external pause remains");
provider.Apply(new Control { Client = "first", Disconnected = new Empty() }.ToByteArray());
Check(!game.Paused && game.SelectedCompression == 2, "disconnect immediately releases claim with latest resume factor");
Pause("reconnected", false);
Check(!game.Paused, "reconnection has fresh ownership");

var unsupported = Command("first");
unsupported.Compression = 99;
var rejected = provider.Apply(unsupported.ToByteArray());
Check(rejected.Result.Failure.ErrorCase == Failure.ErrorOneofCase.Simulation && game.SelectedCompression == 2, "unsupported factor is a domain failure without mutation");
var expired = Command("first");
expired.Pause = true;
expired.ExpiresUnixMilliseconds = 1;
Check(provider.Apply(expired.ToByteArray()).Result.Failure.Infrastructure.Condition == 1 && !game.Paused, "expired queued claim never mutates game");

var old = Command("first");
old.Pause = true;
game.SessionKey = "new native mission";
provider.Synchronize();
Check(provider.Apply(old.ToByteArray()).Result.Failure.Infrastructure.Condition == 4 && !game.Paused, "old mission control rejected");
var retainedBytes = provider.Capture();
Check(Publication.Parser.ParseFrom(retainedBytes).Session != session, "mission gets a new public identity");
game.Ready = false;
provider.Synchronize();
Check(Publication.Parser.ParseFrom(provider.Capture()).Session.Length == 0, "unavailable mission invalidates publication");
Check(Publication.Parser.ParseFrom(retainedBytes).Session.Length != 0, "previous serialized publication remains immutable");

bool wrongThreadRejected = Task.Run(() =>
{
    try
    {
        provider.Capture();
        return false;
    }
    catch (InvalidOperationException exception)
    {
        return exception.Message == "engine_thread_required";
    }
}).GetAwaiter().GetResult();
Check(wrongThreadRejected, "worker cannot access game facade");
Console.WriteLine("Managed provider: ownership, selected compression, disconnect, expiry, session and thread boundary passed.");
return 0;

sealed class FakeGame : IGameFacade
{
    public bool Ready { get; set; } = true;
    public string SessionKey { get; set; } = "native mission";
    public bool Paused { get; private set; }
    public double SelectedCompression { get; set; } = 1;
    public bool SupportsCompression(double factor) => factor == 1 || factor == 2 || factor == 4;
    public void ApplyPause(bool paused) { Paused = paused; }
    public void SelectCompression(double factor, bool remainPaused)
    {
        SelectedCompression = factor;
        Paused = remainPaused;
    }
    public Publication Capture(string session, ulong revision, bool paused, double factor)
    {
        return new Publication { Session = session, Revision = revision };
    }
    public void Reset() { }
}
