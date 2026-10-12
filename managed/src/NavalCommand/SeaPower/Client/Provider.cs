using System;
using System.Collections.Generic;
using System.Threading;
using Google.Protobuf;
using NavalCommand.Gateway.Wire;

namespace NavalCommand.SeaPower.Client;

public sealed class Provider
{
    const int MaximumPublicationBytes = 16 * 1024 * 1024;
    const int MaximumControlBytes = 65536;
    readonly IGameFacade game;
    readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
    readonly PauseOwnership ownership = new();
    string nativeSession = "";
    string session = "";
    ulong revision;
    public bool EffectivePause => ownership.Effective;
    public bool HasExternalPauseOwners => ownership.HasExternalOwners;
    public double SelectedCompression => ownership.SelectedCompression;

    public Provider(IGameFacade gameFacade)
    {
        game = gameFacade ?? throw new ArgumentNullException(nameof(gameFacade));
    }

    void AssertEngineThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != ownerThread)
        {
            throw new InvalidOperationException("engine_thread_required");
        }
    }

    public void Synchronize()
    {
        AssertEngineThread();
        if (!game.Ready)
        {
            Invalidate();
            return;
        }
        if (nativeSession != game.SessionKey)
        {
            nativeSession = game.SessionKey;
            session = Guid.NewGuid().ToString("N");
            revision = 0;
            ownership.Reset(game.Paused, game.SelectedCompression);
        }
    }

    public void ObservePlayerPause(bool paused)
    {
        AssertEngineThread();
        ownership.SetPlayer(paused);
    }

    public void ObservePlayerCompression(double factor)
    {
        AssertEngineThread();
        ownership.SelectCompression(factor);
        // Positive native compression selection is also a native resume request.
        ownership.SetPlayer(false);
    }

    static Result Infrastructure(int condition)
    {
        return new Result { Failure = new Failure { Infrastructure = new InfrastructureError { Condition = condition } } };
    }

    public ControlResult Apply(byte[] bytes)
    {
        AssertEngineThread();
        if (bytes == null || bytes.Length > MaximumControlBytes)
        {
            return new ControlResult { Result = Infrastructure(0) };
        }
        Control control;
        try
        {
            control = Control.Parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException)
        {
            return new ControlResult { Result = Infrastructure(0) };
        }
        if (control.ActionCase == Control.ActionOneofCase.Disconnected)
        {
            ownership.SetClient(control.Client, false);
            if (game.Ready)
            {
                game.ApplyPause(ownership.Effective);
            }
            return new ControlResult { Request = control.Request, Result = new Result { Success = new Empty() } };
        }
        var outcome = new ControlResult { Request = control.Request };
        if (!game.Ready || session.Length == 0 || control.Session != session)
        {
            outcome.Result = Infrastructure(4);
            return outcome;
        }
        if (control.ExpiresUnixMilliseconds <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            outcome.Result = Infrastructure(1);
            return outcome;
        }
        if (control.Client.Length == 0 || control.Request.Length == 0)
        {
            outcome.Result = Infrastructure(0);
            return outcome;
        }
        if (control.ActionCase == Control.ActionOneofCase.Pause)
        {
            ownership.SetClient(control.Client, control.Pause);
            game.ApplyPause(ownership.Effective);
        }
        else if (control.ActionCase == Control.ActionOneofCase.Compression)
        {
            if (!game.SupportsCompression(control.Compression))
            {
                outcome.Result = new Result { Failure = new Failure { Simulation = 0 } };
                return outcome;
            }
            game.SelectCompression(control.Compression, ownership.Effective);
            ownership.SelectCompression(game.SelectedCompression);
        }
        else
        {
            outcome.Result = Infrastructure(0);
            return outcome;
        }
        outcome.Result = new Result { Success = new Empty() };
        return outcome;
    }

    public byte[] Capture()
    {
        AssertEngineThread();
        var publication = session.Length == 0 ? new Publication() : game.Capture(session, ++revision, ownership.Effective, ownership.SelectedCompression);
        if (publication.CalculateSize() > MaximumPublicationBytes)
        {
            throw new InvalidOperationException("publication_resource_exhausted");
        }
        return publication.ToByteArray();
    }

    public void Invalidate()
    {
        AssertEngineThread();
        ownership.ReleaseClients();
        if (session.Length != 0 && game.Ready)
        {
            game.SelectCompression(ownership.SelectedCompression, ownership.Player);
            game.ApplyPause(ownership.Player);
        }
        nativeSession = "";
        session = "";
        revision = 0;
        game.Reset();
    }
}
