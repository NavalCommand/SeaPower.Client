using System;
using NavalCommand.Gateway.Wire;

namespace NavalCommand.SeaPower.Client;

// Engine-thread lifecycle decisions over copied native facts. Kept separate from
// game references so deterministic tests exercise the actual transition logic.
internal sealed class LaunchLifecycle : IDisposable
{
    internal const double VisibilityGraceSeconds = 30;
    internal const double MaximumTrackingSeconds = 1800;

    internal sealed class Observation
    {
        public bool TaskPending;
        public bool TaskCancelled;
        public bool FacilityUnavailable;
        public bool SpawnCaptured;
        public bool SpawnUnavailable;
        public string SpawnedKey = "";
        public bool Airborne;
    }

    readonly double startedWallSeconds;
    readonly Action unsubscribe;
    double? unobservableSinceWallSeconds;
    Stage stage = Stage.ExecutionUnknown;
    string spawnedKey = "";
    string reason = "native_launch_acceptance_unknown";
    bool disposed;

    internal LaunchLifecycle(double wallSeconds, Action unsubscribe)
    {
        startedWallSeconds = wallSeconds;
        this.unsubscribe = unsubscribe;
    }

    internal void RememberAcceptance(LaunchProgress acceptance)
    {
        stage = acceptance.Stage;
        reason = acceptance.Reason;
    }

    internal static LaunchProgress Start<TTask>(Func<TTask> createTask, Func<TTask> recoverTask,
        Action<TTask, LaunchProgress> register, Func<TTask, bool> verifyReservation) where TTask : class
    {
        var result = new LaunchProgress();
        TTask task = null;
        try
        {
            task = createTask();
            if (task == null)
            {
                // Native invocation can mutate resources even without a returned task.
                result.Stage = Stage.ExecutionUnknown;
                result.Reason = "native_launch_null_outcome";
                return result;
            }
            register(task, result);
            bool verified = verifyReservation(task);
            result.Stage = verified ? Stage.NativeTaskCreated : Stage.ExecutionUnknown;
            result.Reason = verified ? "native_launch_reservation_verified" : "native_launch_reservation_unknown";
        }
        catch (Exception exception)
        {
            result.Stage = Stage.ExecutionUnknown;
            result.Reason = "native_launch_exception:" + exception.GetType().Name;
            if (!result.TrackingActive)
            {
                try
                {
                    task ??= recoverTask();
                    if (task != null)
                    {
                        register(task, result);
                    }
                }
                catch (Exception recoveryException)
                {
                    result.Reason += ";tracking_unavailable:" + recoveryException.GetType().Name;
                }
            }
        }
        return result;
    }

    internal LaunchProgress Observe(Observation observation, double wallSeconds)
    {
        if (disposed)
        {
            return Progress();
        }
        if (observation.SpawnUnavailable)
        {
            return Finish("native_spawn_destroyed_or_no_longer_owned");
        }
        if (observation.SpawnedKey != "")
        {
            spawnedKey = observation.SpawnedKey;
            unobservableSinceWallSeconds = null;
            stage = observation.Airborne ? Stage.Airborne : Stage.AircraftSpawned;
            reason = observation.Airborne ? "native_airborne_observed" : "native_spawn_membership_observed";
            if (observation.Airborne)
            {
                Dispose();
                return Progress();
            }
        }
        else if (!observation.SpawnCaptured && (observation.TaskCancelled || observation.FacilityUnavailable))
        {
            return Finish(observation.TaskCancelled ? "native_launch_cancelled_before_spawn" : "native_launch_facility_unavailable");
        }
        else if (!observation.SpawnCaptured && observation.TaskPending)
        {
            stage = Stage.Preparing;
            reason = "native_pending_preparation_observed";
            unobservableSinceWallSeconds = null;
        }
        else
        {
            // Pending-task removal, native spawn membership and provider handle
            // registration are separate events. Keep ownership through the gap.
            unobservableSinceWallSeconds ??= wallSeconds;
            reason = observation.SpawnCaptured ? "native_spawn_registration_pending" : "native_launch_transition_pending";
            if (wallSeconds - unobservableSinceWallSeconds.Value >= VisibilityGraceSeconds)
            {
                return Finish("native_launch_visibility_timeout");
            }
        }
        if (wallSeconds - startedWallSeconds >= MaximumTrackingSeconds)
        {
            return Finish("native_launch_tracking_timeout");
        }
        return Progress();
    }

    internal LaunchProgress ReadFailure(double wallSeconds, string exceptionType)
    {
        // A failed native read is not proof of cancellation or no mutation.
        var progress = Observe(new Observation(), wallSeconds);
        if (progress.TrackingActive)
        {
            progress.Reason = "native_launch_read_pending:" + exceptionType;
        }
        return progress;
    }

    LaunchProgress Finish(string failureReason)
    {
        stage = Stage.ExecutionUnknown;
        reason = failureReason;
        Dispose();
        return Progress();
    }

    LaunchProgress Progress() => new()
    {
        Stage = stage,
        Reason = reason,
        SpawnedKey = spawnedKey,
        TrackingActive = !disposed
    };

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            unsubscribe();
        }
    }
}
