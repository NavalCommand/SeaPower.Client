using System.Collections.Generic;
using NavalCommand.Gateway.Wire;

namespace NavalCommand.SeaPower.Client;

// Engine-thread receipt: copied route values, never native objects.
public sealed class RouteMutation
{
    public bool Mutated;
    public bool OutcomeUnknown;
    public string Reason = "native_dispatch_refused";
    public readonly List<RoutePoint> Before = new();
    public readonly List<RoutePoint> After = new();
}
