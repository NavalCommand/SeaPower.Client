using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SeaPower;

namespace NavalCommand.SeaPower.Client;
public sealed class NativeGameFacade : IGameFacade
{
    readonly Dictionary<ObjectBase, string> keys = new();
    readonly Dictionary<string, ObjectBase> objects = new();
    readonly Dictionary<int, string> tracks = new();
    void Main()
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
            Main();
            return Singleton<GameMain>.InstanceExists() && Singleton<GameMain>.Instance._initialized && !Singleton<GameMain>.Instance._closing && Globals._playerTaskforce != null && Singleton<SceneCreator>.Instance.IsLoadingDone && !Singleton<LoadScreen>.Instance.cameraObject.enabled;
        }
    }

    public double OperationalSeconds
    {
        get
        {
            Main();
            return GameTime.missionElapsedTime;
        }
    }

    public void Reset()
    {
        Main();
        keys.Clear();
        objects.Clear();
        tracks.Clear();
    }

    UnitSample Sample(ObjectBase u, string key)
    {
        bool own = u != null && !u.IsDestroyed && u.UnitTaskforce.Value == Globals._playerTaskforce && Globals._playerTaskforce._taskforceUnits.Contains(u);
        if (!own)
        {
            return null;
        }

        var state = u.StateMachine?.CurrentState;
        var stateName = state?.GetType().Name;
        var s = new UnitSample
        {
            Key = key,
            Name = u.name,
            Own = true,
            Supported = u is Vessel || u is Aircraft || u is Helicopter,
            Controllable = u.HasControl && u.IsControllable && u.AcceptsOrdersFromPlayer && u._userRoot != null,
            Protected = state is AircraftStates.AdvancedMissileEvasion || stateName == "EvadeMissile" || stateName == "TorpedoEvasion" || stateName == "CounterLaunch",
            Grounded = (u is Aircraft a && !a._isInFlight) || (u is Helicopter h && !h._isInFlight) || !string.IsNullOrEmpty(u._flightDeckState),
            Formation = u.Formation != null,
            SelectedTask = u._userRoot?.SelectedTask != null,
            LatitudeDeg = u._geoPosition.Latitude,
            LongitudeDeg = u._geoPosition.Longitude,
            SpeedMetersPerSecond = u._velocityInKnots * 1852.0 / 3600.0
        };
        var d = u._obp?._flightDeck;
        if (d != null)
        {
            s.PendingOperations = d.FlightDeckTasks.Count;
            foreach (var v in d._vehiclesOnBoard.Where(v => v.Numbers > 0).Take(12))
            {
                foreach (var q in v.Squadrons.Take(12 - s.Stocks.Count))
                {
                    s.Stocks.Add(new StockSample { ClassKey = v._fileName, Squadron = q._name, Count = q.Numbers });
                }
            }
        }

        return s;
    }

    public IReadOnlyList<UnitSample> CaptureUnits()
    {
        Main();
        var own = Globals._playerTaskforce._taskforceUnits.Where(u => u != null && !u.IsDestroyed).Take(129).ToArray();
        foreach (var old in keys.Keys.Where(u => !own.Contains(u)).ToArray())
        {
            objects.Remove(keys[old]);
            keys.Remove(old);
        }

        var samples = new List<UnitSample>();
        foreach (var u in own)
        {
            if (!keys.TryGetValue(u, out var key))
            {
                key = Guid.NewGuid().ToString("N");
                keys[u] = key;
                objects[key] = u;
            }

            var s = Sample(u, key);
            if (s != null)
            {
                samples.Add(s);
            }
        }

        return samples;
    }

    public IReadOnlyList<TrackSample> CaptureTracks()
    {
        Main();
        var output = new List<TrackSample>();
        foreach (var v in Globals._playerTaskforce.PlottingTable.Vehicles.Where(v => v.Id >= 7001).Take(128))
        {
            var detected = v.DetectedPosition;
            if (!detected.HasValue)
            {
                continue;
            }

            if (!tracks.TryGetValue(v.Id, out var id))
            {
                id = Guid.NewGuid().ToString("N");
                tracks[v.Id] = id;
            }

            output.Add(new TrackSample { Key = id, LatitudeDeg = detected.Value.Item1.Latitude, LongitudeDeg = detected.Value.Item1.Longitude });
        }

        return output;
    }

    public UnitSample Resolve(string key)
    {
        Main();
        return objects.TryGetValue(key, out var u) ? Sample(u, key) : null;
    }

    public bool AppendWaypoint(string key, double latitudeDeg, double longitudeDeg, string label)
    {
        Main();
        if (!objects.TryGetValue(key, out var u))
        {
            return false;
        }

        var fresh = Sample(u, key);
        if (fresh == null || Provider.Eligibility(fresh).Availability != NavalCommand.Gateway.Wire.Availability.Executable)
        {
            return false;
        }

        var task = u.SetWaypointTask(new WaypointData { _geoposition = new GeoPosition(latitudeDeg, longitudeDeg, u._geoPosition._height), _heightState = WaypointData.WaypointHeightState.Current, _setTelegraph = 2, _allowFormationManeuver = false, wpName = label });
        return task != null && u._userRoot.TaskViewModels.Any(v => v.Task == task);
    }
}
