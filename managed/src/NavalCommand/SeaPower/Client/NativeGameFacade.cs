using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SeaPower;
using NavalCommand.Gateway.Wire;
using NodeCanvas.BehaviourTrees;

namespace NavalCommand.SeaPower.Client;

public sealed partial class NativeGameFacade : IGameFacade
{
    private const double MetersPerNauticalMile = 1852.0;
    private const double SecondsPerHour = 3600.0;
    private const int MaximumUnitSamplesWithOverflow = 129;
    private const int MaximumTrackSamples = 129;
    private const int MaximumStockSamples = 12;
    // This inspected game build uses this lower bound for side-local foreign contact keys.
    private const int FirstForeignContactId = 7001;
    private const int WaypointTelegraphSetting = 2;
    private const int PreserveTelegraphSetting = -2;
    readonly Dictionary<ObjectBase, string> unitKeys = new();
    readonly Dictionary<string, ObjectBase> unitsByKey = new();
    readonly Dictionary<Vehicle, string> contactKeys = new();
    Taskforce sourceTaskforce;
    GameMain sourceGame;
    string sourceSessionKey = "";
    string sourceTaskforceKey = "";

    public string SessionKey
    {
        get
        {
            AssertEngineThread();
            var currentGame = Singleton<GameMain>.Instance;
            if (!ReferenceEquals(sourceGame, currentGame) || !ReferenceEquals(sourceTaskforce, Globals._playerTaskforce))
            {
                sourceGame = currentGame;
                sourceTaskforce = Globals._playerTaskforce;
                sourceSessionKey = Guid.NewGuid().ToString("N");
                sourceTaskforceKey = sourceTaskforce == null ? "" : Guid.NewGuid().ToString("N");
            }
            return sourceSessionKey;
        }
    }
    void AssertEngineThread()
    {
        if (Globals.mainThread != Thread.CurrentThread)
        {
            throw new InvalidOperationException("engine_thread_required");
        }
    }

    public bool Ready
    {
        get
        {
            AssertEngineThread();
            return Singleton<GameMain>.InstanceExists() && Singleton<GameMain>.Instance._initialized && !Singleton<GameMain>.Instance._closing
                && Globals._playerTaskforce != null && Singleton<SceneCreator>.Instance.IsLoadingDone && !Singleton<LoadScreen>.Instance.cameraObject.enabled;
        }
    }

    public double OperationalSeconds
    {
        get
        {
            AssertEngineThread();
            return GameTime.missionElapsedTime;
        }
    }

    public void Reset()
    {
        AssertEngineThread();
        unitKeys.Clear();
        unitsByKey.Clear();
        contactKeys.Clear();
        ClearLaunches();
    }

    UnitSample SampleUnit(ObjectBase nativeUnit, string unitKey)
    {
        _ = SessionKey;
        bool isOwnUnit = nativeUnit != null && !nativeUnit.IsDestroyed && nativeUnit.UnitTaskforce.Value == Globals._playerTaskforce
            && Globals._playerTaskforce._taskforceUnits.Contains(nativeUnit);
        if (!isOwnUnit)
        {
            return null;
        }

        var state = nativeUnit.StateMachine?.CurrentState;
        var stateName = state?.GetType().Name;
        var sample = new UnitSample
        {
            Key = unitKey,
            Name = nativeUnit.name,
            Own = true,
            Supported = nativeUnit is Vessel || nativeUnit is Aircraft || nativeUnit is Helicopter,
            Controllable = nativeUnit.HasControl && nativeUnit.IsControllable && nativeUnit.AcceptsOrdersFromPlayer && nativeUnit._userRoot != null,
            Protected = state is AircraftStates.AdvancedMissileEvasion || stateName == "EvadeMissile" || stateName == "TorpedoEvasion"
                || stateName == "CounterLaunch",
            Grounded = (nativeUnit is Aircraft aircraft && !aircraft._isInFlight) || (nativeUnit is Helicopter helicopter && !helicopter._isInFlight)
                || !string.IsNullOrEmpty(nativeUnit._flightDeckState),
            Formation = nativeUnit.Formation != null,
            RouteSupported = nativeUnit is Vessel,
            SelectedTask = nativeUnit._userRoot?.SelectedTask != null,
            LatitudeDeg = nativeUnit._geoPosition.Latitude,
            LongitudeDeg = nativeUnit._geoPosition.Longitude,
            SpeedMetersPerSecond = nativeUnit._velocityInKnots * MetersPerNauticalMile / SecondsPerHour,
            // Heading is orientation, not course over ground. Reactive native value is degrees.
            HeadingRadians = nativeUnit.transform != null ? nativeUnit.Heading.Value * Math.PI / 180.0 : null
        };
        var flightDeck = nativeUnit._obp?._flightDeck;
        InspectRoute(nativeUnit, sample);
        if (nativeUnit is Aircraft sampledAircraft)
        {
            sample.Airborne = sampledAircraft._isInFlight && string.IsNullOrEmpty(nativeUnit._flightDeckState);
        }
        if (nativeUnit is Helicopter sampledHelicopter)
        {
            sample.Airborne = sampledHelicopter._isInFlight && string.IsNullOrEmpty(nativeUnit._flightDeckState);
        }
        sample.LaunchSupported = nativeUnit is LandUnit && flightDeck != null;
        sample.LaunchRestriction = FacilityRestriction(nativeUnit);
        if (flightDeck != null)
        {
            sample.HasFacility = true;
            sample.OnboardCount = (uint)Math.Max(0, flightDeck.AircraftCurrentlyOnBoard);
            sample.ReservedCount = (uint)flightDeck.FlightDeckTasks.OfType<PendingLaunchTask>().Sum(task => Math.Max(0, task.LaunchCount));
            sample.CooldownCount = (uint)flightDeck.FlightDeckTasks.OfType<CoolDownTask>().Count();
            sample.PendingOperations = flightDeck.FlightDeckTasks.Count;
            sample.Truncated = flightDeck._vehiclesOnBoard.Count() > MaximumStockSamples
                || flightDeck._vehiclesOnBoard.Sum(vehicle => vehicle.Squadrons.Count) > MaximumStockSamples
                || flightDeck._vehiclesOnBoard.Any(vehicle => vehicle.Loadouts.Count > MaximumStockSamples);
            foreach (var vehicleType in flightDeck._vehiclesOnBoard.OrderByDescending(vehicle => vehicle.Numbers > 0).Take(MaximumStockSamples))
            {
                foreach (var squadron in vehicleType.Squadrons.OrderByDescending(squadron => squadron.Numbers > 0).Take(MaximumStockSamples - sample.AircraftInventory.Count))
                {
                    var stock = new AircraftInventorySample
                    {
                        ClassKey = vehicleType._fileName,
                        Squadron = squadron._name,
                        Count = squadron.Numbers
                    };
                    stock.Loadouts.AddRange(vehicleType.Loadouts.Take(MaximumStockSamples).Select(loadout => loadout.Name));
                    if (sample.LaunchSupported)
                    {
                        foreach (var loadout in vehicleType.Loadouts.Take(MaximumStockSamples))
                        {
                            string restriction = LaunchRestriction(nativeUnit, vehicleType, squadron, loadout);
                            stock.LaunchOptions.Add(new AircraftInventorySample.LaunchOptionSample
                            {
                                Loadout = loadout.Name,
                                Availability = restriction == "" ? Availability.Executable : Availability.Blocked,
                                Reason = restriction
                            });
                        }
                    }
                    sample.AircraftInventory.Add(stock);
                }
            }
        }

        return sample;
    }

    public IReadOnlyList<UnitSample> CaptureUnits()
    {
        AssertEngineThread();
        var ownUnits = Globals._playerTaskforce._taskforceUnits.Where(nativeUnit => nativeUnit != null
            && !nativeUnit.IsDestroyed).Take(MaximumUnitSamplesWithOverflow).ToArray();
        foreach (var removedUnit in unitKeys.Keys.Where(nativeUnit => !ownUnits.Contains(nativeUnit)).ToArray())
        {
            unitsByKey.Remove(unitKeys[removedUnit]);
            unitKeys.Remove(removedUnit);
        }

        var samples = new List<UnitSample>();
        foreach (var nativeUnit in ownUnits)
        {
            if (!unitKeys.TryGetValue(nativeUnit, out var unitKey))
            {
                unitKey = Guid.NewGuid().ToString("N");
                unitKeys[nativeUnit] = unitKey;
                unitsByKey[unitKey] = nativeUnit;
            }

            var sample = SampleUnit(nativeUnit, unitKey);
            if (sample != null)
            {
                samples.Add(sample);
            }
        }

        return samples;
    }

    public IReadOnlyList<TrackSample> CaptureTracks()
    {
        AssertEngineThread();
        var trackSamples = new List<TrackSample>();
        var contacts = Globals._playerTaskforce.PlottingTable.Vehicles
            .Where(contact => contact.Id >= FirstForeignContactId).ToArray();
        foreach (var removedContact in contactKeys.Keys.Where(contact => !contacts.Contains(contact)).ToArray())
        {
            contactKeys.Remove(removedContact);
        }
        foreach (var contact in contacts.Take(MaximumTrackSamples))
        {
            // Use the plotting record's lifetime, never its reusable numeric ID or hidden BaseObject.
            if (!contactKeys.TryGetValue(contact, out var contactKey))
            {
                contactKey = Guid.NewGuid().ToString("N");
                contactKeys[contact] = contactKey;
            }
            var detected = contact.DetectedPosition;

            trackSamples.Add(new TrackSample
            {
                Key = contactKey,
                PositionAvailable = detected.HasValue,
                LatitudeDeg = detected.HasValue ? detected.Value.Item1.Latitude : 0,
                LongitudeDeg = detected.HasValue ? detected.Value.Item1.Longitude : 0
            });
        }

        return trackSamples;
    }

    public UnitSample Resolve(string key)
    {
        AssertEngineThread();
        return unitsByKey.TryGetValue(key, out var nativeUnit) ? SampleUnit(nativeUnit, key) : null;
    }

    public bool AppendWaypoint(string key, double latitudeDeg, double longitudeDeg, string label)
    {
        string unitKey = key; // Preserve the existing named-argument contract.
        AssertEngineThread();
        if (!unitsByKey.TryGetValue(unitKey, out var nativeUnit))
        {
            return false;
        }

        var freshSample = SampleUnit(nativeUnit, unitKey);
        if (freshSample == null || Provider.Eligibility(freshSample).Availability != NavalCommand.Gateway.Wire.Availability.Executable)
        {
            return false;
        }

        var waypointTask = nativeUnit.SetWaypointTask(new WaypointData
        {
            _geoposition = new GeoPosition(latitudeDeg, longitudeDeg, nativeUnit._geoPosition._height),
            _heightState = WaypointData.WaypointHeightState.Current,
            _setTelegraph = WaypointTelegraphSetting,
            _allowFormationManeuver = false,
            wpName = label
        });
        return waypointTask != null && nativeUnit._userRoot.TaskViewModels.Any(taskViewModel => taskViewModel.Task == waypointTask);
    }

    void InspectRoute(ObjectBase nativeUnit, UnitSample sample)
    {
        sample.RouteRestriction = "navigation_context_unverified";
        var root = nativeUnit._userRoot;
        if (root == null || root.TaskViewModels.Count > 32)
        {
            sample.RouteRestriction = "route_capacity_or_missing_root";
            return;
        }
        // ClearTasks removes every graph child, including tasks absent from UI models.
        // Require an exact one-to-one graph/model correspondence before editing.
        var children = root.GetChildNodes().ToArray();
        var tasks = root.TaskViewModels.Select(view => view.Task).ToArray();
        if (children.Length != tasks.Length || children.Where((child, index) =>
            child is not ActionNode action || action.task != tasks[index]).Any())
        {
            sample.RouteRestriction = "non_navigation_graph";
            return;
        }
        foreach (var task in tasks)
        {
            if (task == null || task.GetType() != typeof(GoToWaypointTask))
            {
                sample.RouteRestriction = "non_navigation_task";
                return;
            }
            var point = task.WaypointPosition;
            sample.Route.Add(new RoutePoint
            {
                LatitudeDeg = point.Latitude,
                LongitudeDeg = point.Longitude,
                Label = ((GoToWaypointTask)task).wpName.value ?? ""
            });
        }
        sample.RouteInspected = true;
        // Scope the UI-equivalent mutation to plain surface navigation. Airborne
        // routing has UI CeaseFire and altitude effects that are not validated here.
        if (nativeUnit is not Vessel || (nativeUnit.StateMachine?.CurrentState is not VesselStates.Default
            && nativeUnit.StateMachine?.CurrentState is not VesselStates.PlayerOverride))
        {
            sample.RouteRestriction = "surface_navigation_state_required";
            return;
        }
        if (nativeUnit.CurrentOrder.Value?.OrderType != Order.Type.None || nativeUnit._patrolZone != null
            || nativeUnit._alignmentTasks.Count != 0 || nativeUnit._weaponStatus == ObjectBase.WeaponStatus.Free)
        {
            sample.RouteRestriction = "non_navigation_context";
            return;
        }
        if ((tasks.Length > 0 && !nativeUnit._isCurrentWaypointReachable) || tasks.Cast<GoToWaypointTask>().Any(task =>
            !task._isReachable || task._isAutogenerated || task._waypointsData.Count != 0
            || task._parentObject.value != null || task._toCurrentPosition.value || task._setCourse.value || task._setTurn.value
            || task._completionOrder != SeapowerUI.ViewModels.WaypointCompletionOrder.None || task._returnToFormation
            || task._setWeaponStatus.setWS || task._setEmcon || task._setAirSearchRadars.setRadars
            || task._setSurfaceSearchRadars.setRadars || task._setActiveSonars.setSonars || task._setTowedSonars.setSonars
            || task._objectsToVIDList.Count != 0))
        {
            sample.RouteRestriction = "special_navigation_task";
            return;
        }
        sample.RouteRestriction = "";
    }

    public RouteMutation UpdateRoute(string key, double latitudeDeg, double longitudeDeg, string label, RouteMode mode)
    {
        AssertEngineThread();
        var receipt = new RouteMutation();
        if (mode != RouteMode.ReplaceRoute && mode != RouteMode.AppendWaypoint)
        {
            receipt.Reason = "unsupported_route_mode";
            return receipt;
        }
        if (!unitsByKey.TryGetValue(key, out var nativeUnit))
        {
            return receipt;
        }
        var fresh = SampleUnit(nativeUnit, key);
        if (fresh == null)
        {
            return receipt;
        }
        receipt.Before.AddRange(fresh.Route);
        var eligibility = Provider.RouteEligibility(fresh, mode);
        if (eligibility.Availability != Availability.Executable)
        {
            receipt.Reason = eligibility.Reason;
            receipt.After.AddRange(fresh.Route);
            return receipt;
        }
        var root = nativeUnit._userRoot;
        var previousTasks = root.TaskViewModels.Select(view => view.Task).ToArray();
        try
        {
            if (mode == RouteMode.ReplaceRoute)
            {
                // Proven to contain only ordinary navigation tasks immediately above.
                nativeUnit.RemoveWaypoints();
            }
            else
            {
                // Native insertion is after selection. Select the tail explicitly so
                // a GUI selection cannot cause an external append in the middle.
                for (int index = 0; index < root.TaskViewModels.Count; ++index)
                {
                    root.TaskViewModels[index].Selected = index == root.TaskViewModels.Count - 1;
                }
            }
            var created = nativeUnit.SetWaypointTask(new WaypointData
            {
                _geoposition = new GeoPosition(latitudeDeg, longitudeDeg, nativeUnit._geoPosition._height),
                _heightState = WaypointData.WaypointHeightState.Current,
                _setTelegraph = PreserveTelegraphSetting,
                _allowFormationManeuver = false,
                wpName = label
            });
            var afterTasks = root.TaskViewModels.Select(view => view.Task).ToArray();
            var sampled = SampleUnit(nativeUnit, key);
            if (sampled != null)
            {
                receipt.After.AddRange(sampled.Route);
            }
            bool sequenceVerified = mode == RouteMode.ReplaceRoute
                ? afterTasks.Length == 1 && afterTasks[0] == created
                : afterTasks.Length == previousTasks.Length + 1 && afterTasks.Last() == created
                    && afterTasks.Take(previousTasks.Length).SequenceEqual(previousTasks);
            receipt.Mutated = created != null && sequenceVerified && sampled?.RouteInspected == true;
            receipt.OutcomeUnknown = !receipt.Mutated;
            receipt.Reason = receipt.Mutated ? "native_route_sequence_verified" : "native_route_verification_failed";
        }
        catch (Exception exception)
        {
            // Native mutation is not transactional. Never report rejection/no effect
            // after invocation or attempt to reconstruct unrelated native state.
            receipt.OutcomeUnknown = true;
            receipt.Reason = "native_route_exception:" + exception.GetType().Name;
        }
        return receipt;
    }
}
