using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SeaPower;

namespace NavalCommand.SeaPower.Client;

public sealed class NativeGameFacade : IGameFacade
{
    private const double MetersPerNauticalMile = 1852.0;
    private const double SecondsPerHour = 3600.0;
    private const int MaximumUnitSamplesWithOverflow = 129;
    private const int MaximumTrackSamples = 128;
    private const int MaximumStockSamples = 12;
    // This inspected game build uses this lower bound for side-local foreign contact keys.
    private const int FirstForeignContactId = 7001;
    private const int WaypointTelegraphSetting = 2;
    readonly Dictionary<ObjectBase, string> unitKeys = new();
    readonly Dictionary<string, ObjectBase> unitsByKey = new();
    readonly Dictionary<int, string> trackHandles = new();
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
        trackHandles.Clear();
    }

    UnitSample SampleUnit(ObjectBase nativeUnit, string unitKey)
    {
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
            SelectedTask = nativeUnit._userRoot?.SelectedTask != null,
            LatitudeDeg = nativeUnit._geoPosition.Latitude,
            LongitudeDeg = nativeUnit._geoPosition.Longitude,
            SpeedMetersPerSecond = nativeUnit._velocityInKnots * MetersPerNauticalMile / SecondsPerHour
        };
        var flightDeck = nativeUnit._obp?._flightDeck;
        if (flightDeck != null)
        {
            sample.PendingOperations = flightDeck.FlightDeckTasks.Count;
            foreach (var vehicleType in flightDeck._vehiclesOnBoard.Where(vehicleType => vehicleType.Numbers > 0).Take(MaximumStockSamples))
            {
                foreach (var squadron in vehicleType.Squadrons.Take(MaximumStockSamples - sample.Stocks.Count))
                {
                    sample.Stocks.Add(new StockSample
                    {
                        ClassKey = vehicleType._fileName,
                        Squadron = squadron._name,
                        Count = squadron.Numbers
                    });
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
        foreach (var contact in Globals._playerTaskforce.PlottingTable.Vehicles.Where(contact => contact.Id >= FirstForeignContactId).Take(MaximumTrackSamples))
        {
            var detected = contact.DetectedPosition;
            if (!detected.HasValue)
            {
                continue;
            }

            if (!trackHandles.TryGetValue(contact.Id, out var trackId))
            {
                trackId = Guid.NewGuid().ToString("N");
                trackHandles[contact.Id] = trackId;
            }

            trackSamples.Add(new TrackSample
            {
                Key = trackId,
                LatitudeDeg = detected.Value.Item1.Latitude,
                LongitudeDeg = detected.Value.Item1.Longitude
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
}
