using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Google.Protobuf;
using NavalCommand.Gateway.Wire;

namespace NavalCommand.SeaPower.Client;
// Engine thread owns facade, handles and ledger. I/O sees immutable byte arrays only.
// No simulator references cross queues. Serialization is bounded per native source.
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
    readonly Dictionary<string, string> ownHandles = new();
    readonly Dictionary<string, string> contactHandles = new();
    readonly Dictionary<string, string> controlledKeysByHandle = new();
    readonly Dictionary<string, string> taskForceHandles = new();
    readonly Dictionary<string, OrderResult> requestLedger = new();
    readonly Dictionary<string, (string Key, double LatitudeDegrees, double LongitudeDegrees, bool AwaitingActivation)> pendingMovement = new();
    readonly Dictionary<string, string> pendingLaunches = new();
    readonly ConcurrentQueue<byte[]> commandQueue = new();
    readonly ConcurrentQueue<byte[]> snapshotQueue = new();
    string sessionId = "";
    string sourceSessionKey = "";
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
        sourceSessionKey = "";
        ownHandles.Clear();
        contactHandles.Clear();
        controlledKeysByHandle.Clear();
        taskForceHandles.Clear();
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
                SchemaVersion = 3,
                Ready = false
            }
        };
        snapshotQueue.Enqueue(new ProviderObservation { Snapshot = snapshot }.ToByteArray());
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

        string currentSourceSessionKey = gameFacade.SessionKey;
        if (sessionId != "" && currentSourceSessionKey != sourceSessionKey)
        {
            Invalidate();
        }
        sourceSessionKey = currentSourceSessionKey;
        if (sessionId == "")
        {
            sessionId = Guid.NewGuid().ToString("N");
        }

        var units = gameFacade.CaptureUnits().Where(unit => unit.Own).Take(MaximumSnapshotItems + 1).ToArray();
        var contacts = gameFacade.CaptureTracks().Take(MaximumSnapshotItems + 1).ToArray();
        UpdateHandles(ownHandles, units.Take(MaximumSnapshotItems).Select(unit => unit.Key));
        UpdateHandles(contactHandles, contacts.Take(MaximumSnapshotItems).Select(contact => contact.Key));
        controlledKeysByHandle.Clear();
        foreach (var handle in ownHandles)
        {
            controlledKeysByHandle.Add(handle.Value, handle.Key);
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
                SchemaVersion = 3,
                SessionId = sessionId,
                ObserverId = observerId,
                Ready = true
            },
            ObservationVersion = ++observationVersion,
            OperationalSeconds = gameFacade.OperationalSeconds,
            Truncated = units.Length > MaximumSnapshotItems || contacts.Length > MaximumSnapshotItems || units.Any(unit => unit.Truncated),
            Memberships = new ObservedMemberships { Coverage = CollectionCoverage.CoverageUnknown },
        };
        var observation = new ProviderObservation { Snapshot = snapshot };
        foreach (var unit in units.Take(MaximumSnapshotItems))
        {
            var wireUnit = new UnitInstance
            {
                UnitId = new UnitId { Value = ownHandles[unit.Key] },
                Display = new IdentityState { Name = new StringKnowledge { Known = unit.Name } },
                Kinematics = new KinematicsState
                {
                    Position = new PositionKnowledge
                    {
                        Known = new PositionObservation
                        {
                            Coordinates = new GeoPoint { LatitudeDeg = unit.LatitudeDeg, LongitudeDeg = unit.LongitudeDeg }
                        }
                    },
                    SpeedMps = new DoubleKnowledge { Known = unit.SpeedMetersPerSecond }
                },
                Observation = new ObservationMetadata { MeasuredAtSeconds = new DoubleKnowledge { Known = gameFacade.OperationalSeconds } },
                // Equipment capture is not implemented. Unknown inventory never means unarmed.
                Equipment = new ObservedEquipment { Coverage = CollectionCoverage.CoverageUnknown },
                Navigation = new NavigationState()
            };
            if (unit.HeadingRadians.HasValue)
            {
                wireUnit.Kinematics.HeadingRad = new DoubleKnowledge { Known = unit.HeadingRadians.Value };
            }
            var discovery = new OrderDiscovery
            {
                SessionId = sessionId,
                UnitId = wireUnit.UnitId.Clone(),
                BasedOnVersion = snapshot.ObservationVersion,
                Eligibility = new OrderEligibilityState
                {
                    Coverage = unit.Truncated ? CollectionCoverage.CoveragePartial : CollectionCoverage.CoverageComplete
                }
            };
            discovery.Eligibility.Items.Add(Eligibility(unit));
            discovery.Eligibility.Items.Add(RouteEligibility(unit, RouteMode.ReplaceRoute));
            discovery.Eligibility.Items.Add(RouteEligibility(unit, RouteMode.AppendWaypoint));
            if (unit.RouteInspected)
            {
                var route = new Route();
                route.Points.Add(unit.Route.Select(point => point.Clone()));
                wireUnit.Navigation.PlannedRoute = new RouteKnowledge { Known = route };
            }
            if (unit.Airborne.HasValue)
            {
                wireUnit.Aircraft = new AircraftState { Airborne = new BoolKnowledge { Known = unit.Airborne.Value } };
            }
            if (unit.LaunchSupported)
            {
                AddLaunchAvailability(discovery.Eligibility, unit);
            }
            observation.OrderAvailability.Add(discovery);
            if (unit.HasFacility)
            {
                wireUnit.AirOperations = new AirOperations
                {
                    Inventory = new AircraftInventory { Coverage = unit.InventoryCoverage },
                    PendingOperations = new UInt32Knowledge { Known = (uint)Math.Max(0, unit.PendingOperations) },
                    MeasuredAtSeconds = new DoubleKnowledge { Known = gameFacade.OperationalSeconds }
                };
                if (unit.OnboardCount.HasValue)
                {
                    wireUnit.AirOperations.OnboardCount = new UInt32Knowledge { Known = unit.OnboardCount.Value };
                }
                if (unit.ReservedCount.HasValue)
                {
                    wireUnit.AirOperations.ReservedCount = new UInt32Knowledge { Known = unit.ReservedCount.Value };
                }
                if (unit.CooldownCount.HasValue)
                {
                    wireUnit.AirOperations.CooldownCount = new UInt32Knowledge { Known = unit.CooldownCount.Value };
                }
                foreach (var inventory in unit.AircraftInventory.Take(MaximumStocksPerFacility))
                {
                    var entry = new AircraftInventoryEntry
                    {
                        UnitTypeId = new UnitTypeId { Value = inventory.ClassKey },
                        Squadron = inventory.Squadron
                    };
                    if (inventory.Count.HasValue)
                    {
                        entry.UnreservedCount = new UInt32Knowledge { Known = (uint)Math.Max(0, inventory.Count.Value) };
                    }
                    entry.Loadouts.Add(inventory.Loadouts.Select(loadout => new LoadoutId { Value = loadout }));
                    wireUnit.AirOperations.Inventory.Items.Add(entry);
                }
            }
            if (!string.IsNullOrEmpty(unit.TaskForceKey))
            {
                if (!taskForceHandles.TryGetValue(unit.TaskForceKey, out string taskForceHandle))
                {
                    taskForceHandle = Guid.NewGuid().ToString("N");
                    taskForceHandles.Add(unit.TaskForceKey, taskForceHandle);
                }
                snapshot.Memberships.Coverage = CollectionCoverage.CoveragePartial;
                snapshot.Memberships.Items.Add(new UnitMembership
                {
                    UnitId = wireUnit.UnitId.Clone(),
                    TaskForce = new StringKnowledge { Known = taskForceHandle }
                });
            }
            snapshot.Instances.Add(wireUnit);
        }

        foreach (var contact in contacts.Take(MaximumSnapshotItems).Where(contact => contact.PositionAvailable))
        {
            snapshot.Instances.Add(new UnitInstance
            {
                UnitId = new UnitId { Value = contactHandles[contact.Key] },
                Kinematics = new KinematicsState
                {
                    Position = new PositionKnowledge
                    {
                        Estimated = new PositionKnowledge.Types.Estimate
                        {
                            Value = new PositionObservation
                            {
                                Coordinates = new GeoPoint { LatitudeDeg = contact.LatitudeDeg, LongitudeDeg = contact.LongitudeDeg }
                            }
                        }
                    }
                },
                // Cached position has no verified sensor identity or measurement epoch.
                Observation = new ObservationMetadata(),
                Equipment = new ObservedEquipment { Coverage = CollectionCoverage.CoverageUnknown }
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
            if (requestLedger[pendingRequest.Key].RouteMode != RouteMode.IdleWaypoint
                && (!unit.RouteInspected || unit.Route.Count == 0 || unit.Route[0].Label != "NC-" + pendingRequest.Key))
            {
                continue;
            }
            var distanceMeters = Distance(pendingRequest.Value.LatitudeDegrees, pendingRequest.Value.LongitudeDegrees, unit.LatitudeDeg, unit.LongitudeDeg);
            if (distanceMeters >= MinimumObservedDisplacementMeters)
            {
                var orderResult = requestLedger[pendingRequest.Key].Clone();
                orderResult.Stage = Stage.MovementObserved;
                orderResult.Reason = orderResult.RouteMode == RouteMode.IdleWaypoint
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
            if (progress.SpawnedKey != "" && ownHandles.TryGetValue(progress.SpawnedKey, out var spawnedId))
            {
                result.SpawnedUnitId = new UnitId { Value = spawnedId };
            }
            // Tracking ownership must survive publication backpressure. Terminal
            // native records are already released and must never be polled again.
            if (!progress.TrackingActive)
            {
                pendingLaunches.Remove(pending.Key);
            }
            if (result.Stage != progress.Stage || result.Reason != progress.Reason
                || !Equals(result.SpawnedUnitId, requestLedger[pending.Key].SpawnedUnitId))
            {
                result.Stage = progress.Stage;
                result.Reason = progress.Reason;
                result.SampledAtSeconds = gameFacade.OperationalSeconds;
                PublishResult(result);
            }
        }
        var payload = observation.ToByteArray();
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

    static void UpdateHandles(Dictionary<string, string> handles, IEnumerable<string> sourceKeys)
    {
        var currentKeys = new HashSet<string>(sourceKeys);
        foreach (string removedKey in handles.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
        {
            handles.Remove(removedKey);
        }
        foreach (string key in currentKeys)
        {
            if (!handles.ContainsKey(key))
            {
                handles.Add(key, Guid.NewGuid().ToString("N"));
            }
        }
    }

    static void AddLaunchAvailability(OrderEligibilityState availability, UnitSample unit)
    {
        int initialCount = availability.Items.Count;
        foreach (var inventory in unit.AircraftInventory.Take(MaximumStocksPerFacility))
        {
            foreach (var option in inventory.LaunchOptions)
            {
                availability.Items.Add(new OrderEligibility
                {
                    Kind = OrderKind.LaunchAircraft,
                    LaunchOption = new AircraftLaunch
                    {
                        UnitTypeId = new UnitTypeId { Value = inventory.ClassKey },
                        Squadron = inventory.Squadron,
                        LoadoutId = new LoadoutId { Value = option.Loadout },
                        Count = 1
                    },
                    Availability = unit.LaunchRestriction == "" ? option.Availability : Availability.Blocked,
                    Reason = unit.LaunchRestriction == "" ? option.Reason : unit.LaunchRestriction
                });
            }
        }
        if (availability.Items.Count == initialCount)
        {
            bool knownEmptyInventory = unit.InventoryCoverage == CollectionCoverage.CoverageComplete
                && unit.AircraftInventory.Count == 0;
            availability.Items.Add(new OrderEligibility
            {
                Kind = OrderKind.LaunchAircraft,
                Availability = unit.LaunchRestriction != "" || knownEmptyInventory ? Availability.Blocked : Availability.Unknown,
                Reason = unit.LaunchRestriction != "" ? unit.LaunchRestriction
                    : knownEmptyInventory ? "inventory_unavailable" : "launch_options_unknown"
            });
        }
    }

    public static OrderEligibility Eligibility(UnitSample unit)
    {
        var eligibility = new OrderEligibility
        {
            Kind = OrderKind.IdleWaypoint,
            Availability = Availability.Executable
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
        eligibility.Kind = mode == RouteMode.ReplaceRoute ? OrderKind.ReplaceRoute : OrderKind.AppendRoute;
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

        if (request.UnitId == null || !controlledKeysByHandle.TryGetValue(request.UnitId.Value, out var unitKey))
        {
            Reject("wrong_side_or_invalid_entity");
            return;
        }
        var unit = gameFacade.Resolve(unitKey);
        if (unit == null || !unit.Own)
        {
            Reject("wrong_side_or_invalid_entity");
            return;
        }

        var waypoint = request.Waypoint;
        if (request.AircraftLaunch != null)
        {
            if (request.BasedOnVersion == 0 || request.BasedOnVersion > observationVersion
                || observationVersion - request.BasedOnVersion > MaximumObservationAgeVersions)
            {
                Reject("stale_observation");
                return;
            }
            var launch = request.AircraftLaunch;
            if (launch.Count != 1 || launch.UnitTypeId == null || string.IsNullOrWhiteSpace(launch.UnitTypeId.Value)
                || string.IsNullOrWhiteSpace(launch.Squadron) || launch.LoadoutId == null || string.IsNullOrWhiteSpace(launch.LoadoutId.Value)
                || launch.UnitTypeId.Value.Length > 128 || launch.Squadron.Length > 128 || launch.LoadoutId.Value.Length > 128)
            {
                Reject("invalid_arguments");
                return;
            }
            if (!unit.LaunchSupported || unit.LaunchRestriction != "")
            {
                Reject(unit.LaunchRestriction == "" ? "unsupported_facility" : unit.LaunchRestriction);
                return;
            }
            var progress = gameFacade.LaunchFacility(unitKey, launch);
            result.Stage = progress.Stage;
            result.Reason = progress.Reason;
            if (progress.TrackingActive && progress.Token != "")
            {
                pendingLaunches[request.RequestId] = progress.Token;
            }
            PublishResult(result);
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
        if (mode != RouteMode.IdleWaypoint && mode != RouteMode.ReplaceRoute && mode != RouteMode.AppendWaypoint)
        {
            Reject("unsupported_route_mode");
            return;
        }

        var eligible = mode == RouteMode.IdleWaypoint ? Eligibility(unit) : RouteEligibility(unit, mode);
        if (eligible.Availability != Availability.Executable)
        {
            Reject(eligible.Reason);
            return;
        }

        // Facade revalidates native membership/state once more adjacent to mutation.
        if (mode != RouteMode.IdleWaypoint)
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
