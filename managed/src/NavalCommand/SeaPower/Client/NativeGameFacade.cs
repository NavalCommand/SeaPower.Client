using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using SeaPower;
using NavalCommand.Gateway.Wire;
using NativeAircraft = SeaPower.Aircraft;
using Effect = NavalCommand.Gateway.Wire.Effect;

namespace NavalCommand.SeaPower.Client;

public sealed class NativeGameFacade : IGameFacade
{
    const double MetersPerSecondPerKnot = 1852.0 / 3600.0;
    const double DegreesToRadians = Math.PI / 180.0;
    const double NativeEllipseMetersPerDegree = 111120.0;
    static readonly FieldInfo ValuesField = typeof(GameTime).GetField("_timeScaleValues", BindingFlags.NonPublic | BindingFlags.Static);
    static readonly FieldInfo IndexField = typeof(GameTime).GetField("_currentTimeScaleIndex", BindingFlags.NonPublic | BindingFlags.Static);
    static readonly FieldInfo MaximumField = typeof(GameTime).GetField("_maxTimeCompression", BindingFlags.NonPublic | BindingFlags.Static);
    readonly Dictionary<Taskforce, string> factionIds = new();
    readonly Dictionary<ObjectBase, string> unitIds = new();
    readonly Dictionary<Vehicle, string> observationIds = new();
    // Capability IDs retain the real installation/launcher association internally.
    readonly Dictionary<string, BaseSystem> capabilitySystems = new();
    GameMain sourceGame;
    string nativeSession = "";
    public bool ApplyingControl { get; private set; }

    public void ObserveNativeCompression(float factor)
    {
        AssertEngineThread();
        var values = (List<float>)ValuesField.GetValue(null);
        int index = values.IndexOf(factor);
        if (index >= 0)
        {
            IndexField.SetValue(null, index);
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
                && Singleton<TaskforceManager>.InstanceExists() && Singleton<SceneCreator>.InstanceExists()
                && Singleton<SceneCreator>.Instance.IsLoadingDone;
        }
    }

    public string SessionKey
    {
        get
        {
            AssertEngineThread();
            var current = Singleton<GameMain>.Instance;
            if (!ReferenceEquals(sourceGame, current))
            {
                Reset();
                sourceGame = current;
                nativeSession = Guid.NewGuid().ToString("N");
            }
            return nativeSession;
        }
    }

    public bool Paused
    {
        get
        {
            AssertEngineThread();
            return GameTime.IsPaused();
        }
    }

    public double SelectedCompression
    {
        get
        {
            AssertEngineThread();
            if (GameTime.TimeCompression > 0.05f)
            {
                return GameTime.TimeCompression;
            }
            var values = (List<float>)ValuesField.GetValue(null);
            int index = (int)IndexField.GetValue(null);
            return values[index];
        }
    }

    public bool SupportsCompression(double factor)
    {
        AssertEngineThread();
        if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0)
        {
            return false;
        }
        var values = (List<float>)ValuesField.GetValue(null);
        float maximum = (float)MaximumField.GetValue(null);
        return factor <= maximum && values.Contains((float)factor);
    }

    public void ApplyPause(bool paused)
    {
        AssertEngineThread();
        if (GameTime.IsPaused() == paused)
        {
            return;
        }
        ApplyingControl = true;
        try
        {
            if (paused)
            {
                GameTime.Pause();
            }
            else
            {
                GameTime.UnPause();
            }
        }
        finally
        {
            ApplyingControl = false;
        }
    }

    public void SelectCompression(double factor, bool remainPaused)
    {
        AssertEngineThread();
        var values = (List<float>)ValuesField.GetValue(null);
        int index = values.IndexOf((float)factor);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }
        ApplyingControl = true;
        try
        {
            if (remainPaused)
            {
                // Selecting the resume factor must not invoke the positive setter:
                // it would unpause and complete pending flight-deck preparations.
                IndexField.SetValue(null, index);
            }
            else
            {
                GameTime.SetTimeCompression((float)factor);
            }
        }
        finally
        {
            ApplyingControl = false;
        }
    }

    string FactionId(Taskforce faction)
    {
        if (!factionIds.TryGetValue(faction, out var id))
        {
            var name = faction._nameInMissionFile;
            id = string.IsNullOrWhiteSpace(name) ? "faction-" + Guid.NewGuid().ToString("N") : name;
            if (factionIds.Values.Contains(id))
            {
                id += "-" + Guid.NewGuid().ToString("N");
            }
            factionIds.Add(faction, id);
        }
        return id;
    }

    string UnitId(ObjectBase unit)
    {
        if (!unitIds.TryGetValue(unit, out var id))
        {
            id = "unit-" + Guid.NewGuid().ToString("N");
            unitIds.Add(unit, id);
        }
        return id;
    }

    static Result Unavailable()
    {
        return new Result { Failure = new Failure { Infrastructure = new InfrastructureError { Condition = 4 } } };
    }

    Result EquipmentDefinition(string capability)
    {
        var system = capabilitySystems[capability];
        if (system is not SensorSystem)
        {
            // Weapon burst costs, target inputs and shared launcher constraints
            // require a complete source mapping before publishing definitions.
            return Unavailable();
        }
        int mode;
        switch (system._systemType)
        {
            case BaseSystem.SystemType.SensorSearchRadar:
            case BaseSystem.SystemType.SensorFCRadar:
                mode = 0;
                break;
            case BaseSystem.SystemType.SensorSonar:
                mode = 1;
                break;
            case BaseSystem.SystemType.SensorESM:
                mode = 2;
                break;
            case BaseSystem.SystemType.SensorVisual:
            case BaseSystem.SystemType.SensorPeriscope:
            case BaseSystem.SystemType.SensorInfrared:
                mode = 3;
                break;
            case BaseSystem.SystemType.SensorECM:
                var jammer = new EquipmentType { Id = capability };
                jammer.Effects.Add(new Effect { Jamming = new Empty() });
                jammer.Operations.Add(new EquipmentOperation { AcceptedTargets = 1, Invocation = 1 });
                return new Result { EquipmentType = jammer };
            case BaseSystem.SystemType.TowedDecoy:
                var decoy = new EquipmentType { Id = capability };
                decoy.Effects.Add(new Effect { Deception = new Empty() });
                decoy.Operations.Add(new EquipmentOperation { AcceptedTargets = 1, Invocation = 1 });
                return new Result { EquipmentType = decoy };
            default:
                return Unavailable();
        }
        var definition = new EquipmentType { Id = capability };
        definition.Effects.Add(new Effect { Sensing = new Effect.Types.Sensing { Mode = mode } });
        definition.Operations.Add(new EquipmentOperation { AcceptedTargets = 1, Invocation = 1 });
        return new Result { EquipmentType = definition };
    }

    static void Add(ScopedPublication view, Query.Types.Method method, Result value, string id = "", string second = "")
    {
        view.Entries.Add(new Entry { Query = new Query { Method = method, Id = id, SecondId = second }, Result = value });
    }

    static Strings Ids(IEnumerable<string> ids)
    {
        var value = new Strings();
        value.Values.Add(ids);
        return value;
    }

    static Position Position(GeoPosition point)
    {
        return new Position { LatitudeDegrees = point.Latitude, LongitudeDegrees = point.Longitude, ElevationMeters = point._height };
    }

    EquipmentInstance EquipmentState(BaseSystem system, string capability)
    {
        capabilitySystems[capability] = system;
        return new EquipmentInstance
        {
            Type = capability,
            OperationalState = system.IsDestroyed || system.Inoperable.Value ? 2 : system.IsOn.Value ? 1 : 0,
            Damage = system.IsDestroyed ? 2 : system.CurrentDamageType == DamageValues.DamageType.No ? 0 : 1
        };
    }

    void CaptureEquipment(ObjectBase unit, Unit value)
    {
        // Towed decoys are reusable sensor-derived systems attached to a cable;
        // their ammunition descriptor is not an expendable inventory.
        var sensors = unit._obp._sensorSystems
            .Concat(unit._obp._systems.OfType<SensorSystem>())
            .Concat(unit._obp._towedSystems.Select(system => system._towedDecoy).Where(decoy => decoy != null))
            .Distinct();
        foreach (var sensor in sensors)
        {
            string capability = value.Type + "/" + sensor._sectionName + "/" + sensor._systemName;
            value.Equipment.Add(EquipmentState(sensor, capability));
        }
        foreach (var snorkel in unit._obp._systems.Concat(unit._obp._miscSystems).OfType<SnorkelMastSystem>().Distinct())
        {
            string capability = value.Type + "/" + snorkel._sectionName + "/" + snorkel._systemName;
            value.Equipment.Add(EquipmentState(snorkel, capability));
        }
        foreach (var weapon in unit._obp._weaponSystems.Concat(unit._obp._systems.OfType<WeaponSystem>()).Distinct())
        {
            foreach (string ammunition in weapon._associatedAmmunitionNames.Concat(weapon._loadedAmmunitionCount.Keys).Distinct())
            {
                string capability = value.Type + "/" + weapon._sectionName + "/" + ammunition;
                var equipment = EquipmentState(weapon, capability);
                int readyRounds = weapon.getLoadedAmmoCount(ammunition);
                // Magazine-fed guns use a shared ready-round counter. The
                // ammunition-specific magazine count is the actual inventory;
                // readiness still comes from the native per-capability query.
                int count = weapon is WeaponSystemGun && weapon._vwp?._associatedMagazine != null
                    ? weapon.getMagazineAmmoCount(ammunition) : readyRounds;
                if (count < 0)
                {
                    throw new InvalidOperationException("negative_native_ammunition_count");
                }
                equipment.Inventory = new EquipmentInstance.Types.ResourceInventory
                {
                    ResourceId = ammunition, Quantity = new ResourceValue { Discrete = (ulong)count }
                };
                bool engaging = weapon._executingEngageTask;
                bool selected = weapon._ammoForEngage?._ap?._ammunitionFileName == ammunition;
                bool unavailable = equipment.OperationalState == 2 || count == 0 || readyRounds == 0 || weapon._isReloading || weapon._isOffline || (engaging && !selected);
                equipment.OperationalState = unavailable ? 2 : engaging && selected ? 1 : 0;
                value.Equipment.Add(equipment);
            }
        }
    }

    Unit CaptureUnit(ObjectBase unit)
    {
        var result = new Unit
        {
            Id = UnitId(unit), Name = unit.name, Type = unit.getObjectIniName(), Faction = FactionId(unit._taskforce),
            Kinematics = new UnitKinematics
            {
                Position = Position(unit._geoPosition), SpeedMetersPerSecond = unit._velocityInKnots * MetersPerSecondPerKnot,
                HeadingRadians = unit.Heading.Value * DegreesToRadians
            }
        };
        if (unit is NativeAircraft aircraft)
        {
            result.Aircraft = new Gateway.Wire.Aircraft { Airborne = aircraft._isInFlight && string.IsNullOrEmpty(unit._flightDeckState) };
        }
        else if (unit is Helicopter helicopter)
        {
            result.Aircraft = new Gateway.Wire.Aircraft { Airborne = helicopter._isInFlight && string.IsNullOrEmpty(unit._flightDeckState) };
        }
        if (unit._obp == null)
        {
            throw new InvalidOperationException("native_unit_equipment_unavailable");
        }
        CaptureEquipment(unit, result);
        return result;
    }

    static Operations CaptureOperations(ObjectBase unit)
    {
        var result = new Operations();
        var stateName = unit.StateMachine?.CurrentState?.GetType().Name;
        if (stateName == "AdvancedMissileEvasion" || stateName == "EvadeMissile" || stateName == "TorpedoEvasion")
        {
            result.Evading = new Evasion();
        }
        if (unit._userRoot != null)
        {
            var tasks = unit._userRoot.TaskViewModels.Select(view => view.Task).ToArray();
            // Only publish a complete route when every native task is navigation.
            // A mixed/unknown task graph does not mean an empty route.
            if (tasks.All(task => task is GoToWaypointTask))
            {
                result.Navigation = new Navigation();
                foreach (var task in tasks.Cast<GoToWaypointTask>())
                {
                    result.Navigation.Route.Add(new Gateway.Wire.Waypoint { Target = Position(task.WaypointPosition), Label = task.wpName.value ?? "" });
                }
                if (tasks.Length > 0 && result.ActivityCase == Operations.ActivityOneofCase.None)
                {
                    result.MovingToWaypoint = Position(tasks[0].WaypointPosition);
                }
            }
        }
        var deck = unit._obp?._flightDeck;
        if (deck != null)
        {
            result.AirOperations = new AirOperations
            {
                Inventory = new AirOperations.Types.Inventory(),
                OnboardCount = checked((uint)deck.AircraftCurrentlyOnBoard),
                PendingOperations = checked((uint)deck.FlightDeckTasks.Count),
                ReservedCount = checked((uint)deck.FlightDeckTasks.OfType<PendingLaunchTask>().Sum(task => task.LaunchCount)),
                CooldownCount = checked((uint)deck.FlightDeckTasks.OfType<CoolDownTask>().Count())
            };
            foreach (var vehicle in deck._vehiclesOnBoard)
            {
                foreach (var squadron in vehicle.Squadrons)
                {
                    var inventory = new AircraftInventory
                    {
                        Type = vehicle._fileName, Squadron = squadron._name,
                        Loadouts = Ids(vehicle.Loadouts.Select(loadout => loadout.Name))
                    };
                    result.AirOperations.Inventory.Entries.Add(inventory);
                }
            }
        }
        return result;
    }

    Observation CaptureObservation(Vehicle track, Taskforce observer)
    {
        if (!observationIds.TryGetValue(track, out var id))
        {
            id = "observation-" + Guid.NewGuid().ToString("N");
            observationIds.Add(track, id);
        }
        var partial = new PartialPosition();
        var result = new Observation
        {
            Id = id, Name = "Track " + track.Id, ObserverFaction = FactionId(observer),
            Kinematics = new ObservationKinematics { Position = new ObservedPosition { Partial = partial } }
        };
        // Whitelist observer estimates only. Never serialize Object/BaseObject
        // names, faction, position, velocity or class from this privileged wrapper.
        var estimate = track.PositionEstimate;
        if (estimate.HasValue)
        {
            partial.LatitudeDegrees = estimate.Value.Item1.Latitude;
            partial.LongitudeDegrees = estimate.Value.Item1.Longitude;
            var ellipse = track.ErrorEllipse;
            if (ellipse != null)
            {
                partial.ErrorEllipse = new ErrorEllipse
                {
                    MajorAxisMeters = ellipse.SemiMajorAxis * NativeEllipseMetersPerDegree,
                    MinorAxisMeters = ellipse.SemiMinorAxis * NativeEllipseMetersPerDegree,
                    OrientationRadians = ellipse.Heading * DegreesToRadians
                };
            }
        }
        if (track.Side.HasValue && track.Side.Value.Value != null)
        {
            result.ObservedFaction = FactionId(track.Side.Value.Value);
        }
        if (track.Class.HasValue && !string.IsNullOrEmpty(track.Class.Value.Value))
        {
            result.Type = track.Class.Value.Value;
        }
        return result;
    }

    public Publication Capture(string session, ulong revision, bool paused, double selectedCompression)
    {
        AssertEngineThread();
        var factions = Singleton<TaskforceManager>.Instance._taskForces.Where(faction => faction != null).ToArray();
        var units = factions.SelectMany(faction => faction._taskforceUnits).Where(unit => unit != null && !unit.IsDestroyed).Distinct().ToArray();
        var publication = new Publication { Session = session, Revision = revision, CapturedAtSeconds = GameTime.missionElapsedTime };
        capabilitySystems.Clear();
        var unitValues = units.ToDictionary(unit => unit, CaptureUnit);
        var liveTracks = new HashSet<Vehicle>();
        foreach (var scope in new Taskforce[] { null }.Concat(factions))
        {
            var view = new ScopedPublication { Scope = scope == null ? new Scope { Omniscient = new Empty() } : new Scope { Faction = FactionId(scope) } };
            var visible = units.Where(unit => scope == null || unit._taskforce == scope).ToArray();
            Add(view, Query.Types.Method.UnitIds, new Result { Ids = Ids(visible.Select(UnitId)) });
            foreach (var unit in visible)
            {
                Add(view, Query.Types.Method.Unit, new Result { Unit = unitValues[unit] }, UnitId(unit));
                Add(view, Query.Types.Method.Operations, new Result { Operations = CaptureOperations(unit) }, UnitId(unit));
            }
            var observations = new List<Observation>();
            if (scope != null && scope.PlottingTable != null)
            {
                foreach (var track in scope.PlottingTable.Vehicles)
                {
                    // The hidden pointer is used solely to exclude the faction's
                    // own authoritative units, never to manufacture observations.
                    if (track.Object != null && track.Object._taskforce == scope)
                    {
                        continue;
                    }
                    liveTracks.Add(track);
                    observations.Add(CaptureObservation(track, scope));
                }
            }
            Add(view, Query.Types.Method.ObservationIds, scope != null && scope.PlottingTable == null ? Unavailable() : new Result { Ids = Ids(observations.Select(track => track.Id)) });
            foreach (var observation in observations)
            {
                Add(view, Query.Types.Method.Observation, new Result { Observation = observation }, observation.Id);
            }
            Add(view, Query.Types.Method.FactionIds, new Result { Ids = Ids(factions.Select(FactionId)) });
            foreach (var first in factions)
            {
                foreach (var second in factions)
                {
                    var native = first.RelationshipTo(second);
                    int alignment = native == RelationsState.Friendly ? 0 : native == RelationsState.Neutral ? 1 : 2;
                    Add(view, Query.Types.Method.FactionAlignment, new Result { Alignment = alignment }, FactionId(first), FactionId(second));
                }
            }
            Add(view, Query.Types.Method.Time, new Result { TimeSeconds = GameTime.missionElapsedTime });
            Add(view, Query.Types.Method.Paused, new Result { Boolean = paused });
            Add(view, Query.Types.Method.CanPause, new Result { Boolean = true });
            Add(view, Query.Types.Method.CanUnpause, new Result { Boolean = true });
            Add(view, Query.Types.Method.Compression, new Result { Compression = selectedCompression });
            var visibleTypes = visible.Select(unit => unitValues[unit].Type).Distinct().ToArray();
            Add(view, Query.Types.Method.UnitTypeIds, new Result { Ids = Ids(visibleTypes) });
            foreach (string type in visibleTypes)
            {
                // Complete native loadout definitions are not yet mapped.
                Add(view, Query.Types.Method.UnitType, Unavailable(), type);
            }
            var equipment = visible.SelectMany(unit => unitValues[unit].Equipment).ToArray();
            var equipmentTypes = equipment.Select(instance => instance.Type).Distinct().ToArray();
            Add(view, Query.Types.Method.EquipmentTypeIds, new Result { Ids = Ids(equipmentTypes) });
            foreach (string capability in equipmentTypes)
            {
                Add(view, Query.Types.Method.EquipmentType, EquipmentDefinition(capability), capability);
            }
            var resources = equipment.Where(instance => instance.Inventory != null).Select(instance => instance.Inventory.ResourceId).Distinct().ToArray();
            Add(view, Query.Types.Method.ResourceIds, new Result { Ids = Ids(resources) });
            foreach (string resource in resources)
            {
                var definitions = new ResourceDefinitions();
                definitions.Values.Add(new ResourceDefinition { Id = resource, Representation = 0, Unit = 0, Accounting = 0 });
                Add(view, Query.Types.Method.ResourceDefinitions, new Result { Resources = definitions }, resource);
            }
            publication.Views.Add(view);
        }
        foreach (var expired in observationIds.Keys.Where(track => !liveTracks.Contains(track)).ToArray())
        {
            observationIds.Remove(expired);
        }
        foreach (var expired in unitIds.Keys.Where(unit => !units.Contains(unit)).ToArray())
        {
            unitIds.Remove(expired);
        }
        return publication;
    }

    public void Reset()
    {
        AssertEngineThread();
        factionIds.Clear();
        unitIds.Clear();
        observationIds.Clear();
        capabilitySystems.Clear();
        sourceGame = null;
        nativeSession = "";
    }
}
