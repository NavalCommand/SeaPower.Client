using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Google.Protobuf;
using NavalCommand.Gateway.Wire;

namespace NavalCommand.SeaPower.Client;
// Engine thread owns facade, handles and ledger. I/O sees immutable byte arrays only.
// No simulator references cross queues. Serialization is bounded to 128 units/tracks.
public sealed class Provider
{
    private const int MaximumOrderBytes = 65_536;
    private const int MaximumQueuedCommands = 64;
    private const int MaximumRetainedResults = 128;
    private const int MaximumSnapshotItems = 128;
    private const int MaximumStocksPerFacility = 12;
    private const int MaximumCommandsPerTick = 8;
    private const int MaximumSnapshotBytes = 1_048_576;
    private const int MaximumRequestIdCharacters = 128;
    private const ulong MaximumObservationAgeVersions = 20;
    private const double MinimumObservedDisplacementMeters = 5;
    private const double MaximumWaypointDistanceMeters = 185_200;
    private const double MaximumLatitudeDegrees = 90;
    private const double MaximumLongitudeDegrees = 180;
    readonly IGameFacade gameFacade;
    readonly int ownerThreadId;
    readonly string observerId;
    readonly Dictionary<string, string> entityHandles = new();
    readonly Dictionary<string, OrderResult> requestLedger = new();
    readonly Dictionary<string, (string Key, double LatitudeDegrees, double LongitudeDegrees, bool AwaitingActivation)> pendingMovement = new();
    readonly Dictionary<string, string> pendingLaunches = new();
    readonly ConcurrentQueue<byte[]> commandQueue = new();
    readonly ConcurrentQueue<byte[]> snapshotQueue = new();
    string sessionId = "";
    ulong observationVersion;
    int queuedCommandCount;
    public Provider(IGameFacade game, string observer)
    {
        gameFacade = game;
        observerId = observer;
        ownerThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    void AssertEngineThread()
    {
        if (ownerThreadId != Thread.CurrentThread.ManagedThreadId)
        {
            throw new InvalidOperationException("engine_thread_required");
        }
    }

    public bool Enqueue(byte[] immutableBytes)
    {
        if (immutableBytes == null || immutableBytes.Length > MaximumOrderBytes)
        {
            return false;
        }

        if (Interlocked.Increment(ref queuedCommandCount) > MaximumQueuedCommands)
        {
            Interlocked.Decrement(ref queuedCommandCount);
            return false;
        }

        commandQueue.Enqueue((byte[])immutableBytes.Clone());
        return true;
    }

    public bool TryRead(out byte[] bytes) => snapshotQueue.TryDequeue(out bytes);
    // Separate outgoing channels preserve canonical payload types without extra schema.
    public readonly ConcurrentQueue<byte[]> Results = new();
    void PublishResult(OrderResult orderResult)
    {
        requestLedger[orderResult.RequestId] = orderResult;
        if (Results.Count < MaximumRetainedResults)
        {
            Results.Enqueue(orderResult.ToByteArray());
        }
        else
        {
            throw new InvalidOperationException("status_backpressure");
        }
    }

    public void Invalidate()
    {
        AssertEngineThread();
        sessionId = "";
        observationVersion = 0;
        entityHandles.Clear();
        requestLedger.Clear();
        pendingMovement.Clear();
        pendingLaunches.Clear();
        gameFacade.Reset();
        while (commandQueue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref queuedCommandCount);
        }

        while (snapshotQueue.TryDequeue(out _))
        {
        }

        while (Results.TryDequeue(out _))
        {
        }

        var snapshot = new WorldSnapshot
        {
            Session = new Session
            {
                SchemaVersion = 1,
                Ready = false
            }
        };
        snapshotQueue.Enqueue(snapshot.ToByteArray());
    }

    public byte[] Tick()
    {
        AssertEngineThread();
        if (!gameFacade.Ready)
        {
            if (sessionId != "")
            {
                Invalidate();
            }

            return null;
        }

        if (sessionId == "")
        {
            sessionId = Guid.NewGuid().ToString("N");
        }

        var units = gameFacade.CaptureUnits().Where(unit => unit.Own).Take(MaximumSnapshotItems + 1).ToArray();
        var currentUnitKeys = new HashSet<string>(units.Take(MaximumSnapshotItems).Select(unit => unit.Key));
        foreach (var key in entityHandles.Keys.Where(candidateUnitKey => !currentUnitKeys.Contains(candidateUnitKey)).ToArray())
        {
            entityHandles.Remove(key);
        }

        foreach (var unit in units.Take(MaximumSnapshotItems))
        {
            if (!entityHandles.ContainsKey(unit.Key))
            {
                entityHandles[unit.Key] = Guid.NewGuid().ToString("N");
            }
        }

        // Process bounded commands before publishing a new version.
        for (int commandIndex = 0; commandIndex < MaximumCommandsPerTick && commandQueue.TryDequeue(out var bytes); ++commandIndex)
        {
            Interlocked.Decrement(ref queuedCommandCount);
            OrderRequest orderResult;
            try
            {
                orderResult = OrderRequest.Parser.ParseFrom(bytes);
            }
            catch
            {
                continue;
            }

            Dispatch(orderResult);
        }

        var snapshot = new WorldSnapshot
        {
            Session = new Session
            {
                SchemaVersion = 1,
                SessionId = sessionId,
                ObserverId = observerId,
                Ready = true
            },
            ObservationVersion = ++observationVersion,
            OperationalSeconds = gameFacade.OperationalSeconds,
            Truncated = units.Length > MaximumSnapshotItems || units.Any(unit => unit.Truncated)
        };
        foreach (var unit in units.Take(MaximumSnapshotItems))
        {
            var wireUnit = new Unit
            {
                Id = entityHandles[unit.Key],
                Name = unit.Name,
                Position = new GeoPoint
                {
                    LatitudeDeg = unit.LatitudeDeg,
                    LongitudeDeg = unit.LongitudeDeg
                },
                SpeedMps = unit.SpeedMetersPerSecond,
                SampledAtSeconds = gameFacade.OperationalSeconds
            };
            wireUnit.Orders.Add(Eligibility(unit));
            wireUnit.Orders.Add(RouteEligibility(unit, RouteMode.ReplaceRoute));
            wireUnit.Orders.Add(RouteEligibility(unit, RouteMode.AppendWaypoint));
            wireUnit.RouteInspected = unit.RouteInspected;
            wireUnit.PlannedRoute.Add(unit.Route.Select(point => point.Clone()));
            if (unit.Airborne.HasValue)
            {
                wireUnit.Airborne = unit.Airborne.Value;
            }
            if (unit.LaunchSupported)
            {
                bool hasExecutableOption = unit.Stocks.Any(stock => stock.LaunchOptions.Any(option => option.Availability == Availability.Executable));
                wireUnit.Orders.Add(new OrderEligibility
                {
                    Kind = "facility_launch",
                    Availability = unit.LaunchRestriction == "" && hasExecutableOption ? Availability.Executable : Availability.Blocked,
                    Reason = unit.LaunchRestriction != "" ? unit.LaunchRestriction : hasExecutableOption ? "" : "no_executable_launch_option",
                    ArgumentSemantics = "one aircraft; own land airfield; exact stock class/squadron/loadout; native preparation and takeoff"
                });
            }
            snapshot.OwnUnits.Add(wireUnit);
            if (unit.HasFacility || unit.Stocks.Count > 0)
            {
                var facility = new Facility
                {
                    EntityId = wireUnit.Id,
                    PendingOperations = (uint)Math.Max(0, unit.PendingOperations),
                    SampledAtSeconds = gameFacade.OperationalSeconds
                };
                if (unit.OnboardCount.HasValue)
                {
                    facility.OnboardCount = unit.OnboardCount.Value;
                }
                if (unit.ReservedCount.HasValue)
                {
                    facility.ReservedCount = unit.ReservedCount.Value;
                }
                if (unit.CooldownCount.HasValue)
                {
                    facility.CooldownCount = unit.CooldownCount.Value;
                }
                foreach (var stock in unit.Stocks.Take(MaximumStocksPerFacility))
                {
                    var wireStock = new Stock
                    {
                        ClassKey = stock.ClassKey,
                        Squadron = stock.Squadron,
                        UnreservedCount = (uint)Math.Max(0, stock.Count)
                    };
                    wireStock.LaunchOptions.Add(stock.LaunchOptions);
                    facility.Stocks.Add(wireStock);
                }

                snapshot.Facilities.Add(facility);
            }
        }

        foreach (var track in gameFacade.CaptureTracks().Take(MaximumSnapshotItems))
        {
            snapshot.Tracks.Add(new Track
            {
                Id = track.Key,
                DetectedPosition = new GeoPoint
                {
                    LatitudeDeg = track.LatitudeDeg,
                    LongitudeDeg = track.LongitudeDeg
                },
                SampledAtSeconds = gameFacade.OperationalSeconds,
                Provenance = "selected_plotting_table_detected_position_cache"
            });
        }

        foreach (var pendingRequest in pendingMovement.ToArray())
        {
            var unit = gameFacade.Resolve(pendingRequest.Value.Key);
            if (unit == null || !unit.Own)
            {
                pendingMovement.Remove(pendingRequest.Key);
                continue;
            }

            if (pendingRequest.Value.AwaitingActivation)
            {
                // An appended destination is not executing while an earlier waypoint
                // is at the native route head. Reset displacement at activation.
                if (!unit.RouteInspected || unit.Route.Count == 0 || unit.Route[0].Label != "NC-" + pendingRequest.Key)
                {
                    continue;
                }
                pendingMovement[pendingRequest.Key] = (unit.Key, unit.LatitudeDeg, unit.LongitudeDeg, false);
                continue;
            }
            if (requestLedger[pendingRequest.Key].RouteMode != RouteMode.LegacyIdle
                && (!unit.RouteInspected || unit.Route.Count == 0 || unit.Route[0].Label != "NC-" + pendingRequest.Key))
            {
                continue;
            }
            var distanceMeters = Distance(pendingRequest.Value.LatitudeDegrees, pendingRequest.Value.LongitudeDegrees, unit.LatitudeDeg, unit.LongitudeDeg);
            if (distanceMeters >= MinimumObservedDisplacementMeters)
            {
                var orderResult = requestLedger[pendingRequest.Key].Clone();
                orderResult.Stage = Stage.MovementObserved;
                orderResult.Reason = orderResult.RouteMode == RouteMode.LegacyIdle
                    ? "displacement_after_native_task_creation" : "displacement_while_requested_route_head";
                orderResult.DisplacementM = distanceMeters;
                orderResult.SampledAtSeconds = gameFacade.OperationalSeconds;
                PublishResult(orderResult);
                pendingMovement.Remove(pendingRequest.Key);
            }
        }

        foreach (var pending in pendingLaunches.ToArray())
        {
            var progress = gameFacade.PollLaunch(pending.Value);
            var result = requestLedger[pending.Key].Clone();
            if (progress.SpawnedKey != "" && entityHandles.TryGetValue(progress.SpawnedKey, out var spawnedId))
            {
                result.SpawnedEntityId = spawnedId;
            }
            if (result.Stage != progress.Stage || result.SpawnedEntityId != requestLedger[pending.Key].SpawnedEntityId)
            {
                result.Stage = progress.Stage;
                result.Reason = progress.Reason;
                result.SampledAtSeconds = gameFacade.OperationalSeconds;
                PublishResult(result);
            }
            if (progress.Stage == Stage.Airborne || progress.Stage == Stage.ExecutionUnknown)
            {
                pendingLaunches.Remove(pending.Key);
            }
        }
        var payload = snapshot.ToByteArray();
        if (payload.Length > MaximumSnapshotBytes)
        {
            throw new InvalidOperationException("snapshot_too_large");
        }

        while (snapshotQueue.TryDequeue(out _))
        {
        }

        snapshotQueue.Enqueue(payload);
        return payload;
    }

    public static OrderEligibility Eligibility(UnitSample u)
    {
        // Retain the public parameter name for named-argument compatibility.
        var unit = u;
        var eligibility = new OrderEligibility
        {
            Kind = "set_waypoint",
            Availability = Availability.Executable,
            ArgumentSemantics = "append; latitude/longitude degrees; current altitude; native telegraph 2; isolated unit only"
        };
        if (!unit.Supported)
        {
            eligibility.Availability = Availability.Unsupported;
            eligibility.Reason = "unsupported_unit_family";
        }
        else if (!unit.Own)
        {
            eligibility.Availability = Availability.Blocked;
            eligibility.Reason = "wrong_side_or_invalid_entity";
        }
        else if (unit.Protected)
        {
            eligibility.Availability = Availability.Blocked;
            eligibility.Reason = "protected_native_action";
        }
        else if (unit.Grounded)
        {
            eligibility.Availability = Availability.Unknown;
            eligibility.Reason = "grounded_launch_sequence_unverified";
        }
        else if (!unit.Controllable)
        {
            eligibility.Availability = Availability.Blocked;
            eligibility.Reason = "uncontrollable";
        }
        else if (unit.Formation)
        {
            eligibility.Availability = Availability.Unknown;
            eligibility.Reason = "formation_effects_unverified";
        }
        else if (unit.SelectedTask)
        {
            eligibility.Availability = Availability.Blocked;
            eligibility.Reason = "native_task_selection_active";
        }

        return eligibility;
    }

    public static OrderEligibility RouteEligibility(UnitSample unit, RouteMode mode)
    {
        var eligibility = Eligibility(unit);
        eligibility.Kind = mode == RouteMode.ReplaceRoute ? "replace_route" : "append_waypoint";
        eligibility.ArgumentSemantics = "explicit route mode; isolated surface navigation; latitude/longitude degrees; preserve speed; bounded route";
        if (eligibility.Reason == "native_task_selection_active")
        {
            eligibility.Availability = Availability.Executable;
            eligibility.Reason = "";
        }
        if (eligibility.Availability == Availability.Executable && !unit.RouteSupported)
        {
            eligibility.Availability = Availability.Unsupported;
            eligibility.Reason = "unsupported_route_unit_family";
        }
        if (eligibility.Availability == Availability.Executable && (!unit.RouteInspected || unit.RouteRestriction != ""))
        {
            eligibility.Availability = Availability.Blocked;
            eligibility.Reason = unit.RouteRestriction == "" ? "navigation_context_unverified" : unit.RouteRestriction;
        }
        if (eligibility.Availability == Availability.Executable && mode == RouteMode.AppendWaypoint && unit.Route.Count >= 32)
        {
            eligibility.Availability = Availability.Blocked;
            eligibility.Reason = "route_capacity";
        }
        return eligibility;
    }

    void Dispatch(OrderRequest request)
    {
        var result = new OrderResult
        {
            SessionId = request.SessionId,
            RequestId = request.RequestId,
            Stage = Stage.Rejected,
            SampledAtSeconds = gameFacade.OperationalSeconds
        };
        void Reject(string reason)
        {
            result.Reason = reason;
            PublishResult(result);
        }

        if (request.SessionId != sessionId)
        {
            result.Reason = "stale_session";
            if (Results.Count < MaximumRetainedResults)
            {
                Results.Enqueue(result.ToByteArray());
            }

            return;
        }

        if (requestLedger.TryGetValue(request.RequestId, out var priorResult))
        {
            if (Results.Count < MaximumRetainedResults)
            {
                Results.Enqueue(priorResult.ToByteArray());
            }

            return;
        }

        if (requestLedger.Count >= MaximumRetainedResults)
        {
            result.Reason = "ledger_full";
            if (Results.Count < MaximumRetainedResults)
            {
                Results.Enqueue(result.ToByteArray());
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > MaximumRequestIdCharacters)
        {
            Reject("invalid_arguments");
            return;
        }

        var unitKey = entityHandles.FirstOrDefault(entry => entry.Value == request.EntityId).Key;
        var unit = unitKey == null ? null : gameFacade.Resolve(unitKey);
        if (unit == null || !unit.Own)
        {
            Reject("wrong_side_or_invalid_entity");
            return;
        }

        var waypoint = request.Waypoint;
        if (request.FacilityLaunch != null)
        {
            if (request.BasedOnVersion == 0 || request.BasedOnVersion > observationVersion
                || observationVersion - request.BasedOnVersion > MaximumObservationAgeVersions)
            {
                Reject("stale_observation");
                return;
            }
            var launch = request.FacilityLaunch;
            if (launch.Count != 1 || string.IsNullOrWhiteSpace(launch.ClassKey) || string.IsNullOrWhiteSpace(launch.Squadron)
                || string.IsNullOrWhiteSpace(launch.Loadout) || launch.ClassKey.Length > 128 || launch.Squadron.Length > 128 || launch.Loadout.Length > 128)
            {
                Reject("invalid_arguments");
                return;
            }
            if (!unit.LaunchSupported || unit.LaunchRestriction != "")
            {
                Reject(unit.LaunchRestriction);
                return;
            }
            var progress = gameFacade.LaunchFacility(unitKey, launch);
            result.Stage = progress.Stage;
            result.Reason = progress.Reason;
            PublishResult(result);
            if (progress.Stage == Stage.NativeTaskCreated && progress.Token != "")
            {
                pendingLaunches[request.RequestId] = progress.Token;
            }
            return;
        }
        if (waypoint == null || !double.IsFinite(waypoint.LatitudeDeg) || !double.IsFinite(waypoint.LongitudeDeg)
            || Math.Abs(waypoint.LatitudeDeg) > MaximumLatitudeDegrees || Math.Abs(waypoint.LongitudeDeg) > MaximumLongitudeDegrees
            || Distance(unit.LatitudeDeg, unit.LongitudeDeg, waypoint.LatitudeDeg, waypoint.LongitudeDeg) > MaximumWaypointDistanceMeters)
        {
            Reject("invalid_arguments");
            return;
        }

        if (request.BasedOnVersion == 0 || request.BasedOnVersion > observationVersion
            || observationVersion - request.BasedOnVersion > MaximumObservationAgeVersions)
        {
            Reject("stale_observation");
            return;
        }

        var mode = waypoint.RouteMode;
        if (mode != RouteMode.LegacyIdle && mode != RouteMode.ReplaceRoute && mode != RouteMode.AppendWaypoint)
        {
            Reject("unsupported_route_mode");
            return;
        }

        var eligible = mode == RouteMode.LegacyIdle ? Eligibility(unit) : RouteEligibility(unit, mode);
        if (eligible.Availability != Availability.Executable)
        {
            Reject(eligible.Reason);
            return;
        }

        // Facade revalidates native membership/state once more adjacent to mutation.
        if (mode != RouteMode.LegacyIdle)
        {
            var mutation = gameFacade.UpdateRoute(unitKey, waypoint.LatitudeDeg, waypoint.LongitudeDeg, "NC-" + request.RequestId, mode);
            result.RouteMode = mode;
            result.RouteBefore.Add(mutation.Before);
            result.RouteAfter.Add(mutation.After);
            result.Reason = mutation.Reason;
            result.Stage = mutation.OutcomeUnknown ? Stage.ExecutionUnknown : mutation.Mutated ? Stage.RouteMutated : Stage.Rejected;
            PublishResult(result);
            if (mutation.Mutated)
            {
                // A replacement supersedes displacement attribution for the previous route.
                if (mode == RouteMode.ReplaceRoute)
                {
                    foreach (var prior in pendingMovement.Where(entry => entry.Value.Key == unitKey).Select(entry => entry.Key).ToArray())
                    {
                        pendingMovement.Remove(prior);
                    }
                }
                pendingMovement[request.RequestId] = (unitKey, unit.LatitudeDeg, unit.LongitudeDeg, true);
            }
            return;
        }

        if (!gameFacade.AppendWaypoint(unitKey, waypoint.LatitudeDeg, waypoint.LongitudeDeg, "NC-" + request.RequestId))
        {
            Reject("native_dispatch_refused");
            return;
        }

        result.Stage = Stage.NativeTaskCreated;
        result.Reason = "native_task_membership_verified";
        PublishResult(result);
        pendingMovement[request.RequestId] = (unitKey, unit.LatitudeDeg, unit.LongitudeDeg, false);
    }

    public static double Distance(double la, double lo, double lb, double lob)
    {
        // Keep parameter names for callers that use named arguments.
        double startLatitudeDegrees = la;
        double startLongitudeDegrees = lo;
        double endLatitudeDegrees = lb;
        double endLongitudeDegrees = lob;
        const double EarthRadiusMeters = 6_371_000;
        const double DegreesPerHalfTurn = 180;
        // Preserve multiplication/division order and near-antipodal handling from the
        // original Haversine routine. No newer target-runtime math APIs are required.
        double startLatitudeRadians = startLatitudeDegrees * Math.PI / DegreesPerHalfTurn;
        double endLatitudeRadians = endLatitudeDegrees * Math.PI / DegreesPerHalfTurn;
        double latitudeDifferenceRadians = (endLatitudeDegrees - startLatitudeDegrees) * Math.PI / DegreesPerHalfTurn;
        double longitudeDifferenceRadians = (endLongitudeDegrees - startLongitudeDegrees) * Math.PI / DegreesPerHalfTurn;
        double latitudeHaversineTerm = Math.Sin(latitudeDifferenceRadians / 2) * Math.Sin(latitudeDifferenceRadians / 2);
        double longitudeHaversineTerm = Math.Cos(startLatitudeRadians) * Math.Cos(endLatitudeRadians)
            * Math.Sin(longitudeDifferenceRadians / 2) * Math.Sin(longitudeDifferenceRadians / 2);
        double haversine = latitudeHaversineTerm + longitudeHaversineTerm;
        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(Math.Max(0, 1 - haversine)));
    }
}
