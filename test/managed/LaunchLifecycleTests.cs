using System;
using System.Linq;
using Google.Protobuf;
using NavalCommand.Gateway.Wire;
using NavalCommand.SeaPower.Client;

static class LaunchLifecycleTests
{
    static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new Exception(label);
        }
        Console.WriteLine("PASS " + label);
    }

    public static void Run()
    {
        VerifyCreationOutcomes();
        using var removedImmediately = new LaunchLifecycle(0, () => { });
        removedImmediately.RememberAcceptance(new LaunchProgress { Stage = Stage.NativeTaskCreated, Reason = "verified" });
        var gapAfterAcceptance = removedImmediately.Observe(new LaunchLifecycle.Observation(), 1);
        Check(gapAfterAcceptance.Stage == Stage.NativeTaskCreated && gapAfterAcceptance.TrackingActive,
            "first_poll_spawn_gap_preserves_known_acceptance");
        int releases = 0;
        var lifecycle = new LaunchLifecycle(0, () => releases++);
        var pending = new LaunchLifecycle.Observation { TaskPending = true };
        Check(lifecycle.Observe(pending, 1).Stage == Stage.Preparing, "lifecycle_preparing");
        var transition = lifecycle.Observe(new LaunchLifecycle.Observation(), 2);
        Check(transition.Stage == Stage.Preparing && transition.TrackingActive && releases == 0, "missing_task_keeps_tracking");
        transition = lifecycle.Observe(new LaunchLifecycle.Observation { SpawnCaptured = true }, 3);
        Check(transition.Stage == Stage.Preparing && transition.SpawnedKey == "" && transition.TrackingActive,
            "callback_before_registration_no_invented_id");
        var registered = new LaunchLifecycle.Observation { SpawnCaptured = true, SpawnedKey = "own-spawn" };
        Check(lifecycle.Observe(registered, 4).Stage == Stage.AircraftSpawned, "registered_grounded_spawn");
        registered.Airborne = true;
        var airborne = lifecycle.Observe(registered, 5);
        Check(airborne.Stage == Stage.Airborne && !airborne.TrackingActive && releases == 1, "native_airborne_finalizes_once");
        lifecycle.Dispose();
        Check(lifecycle.Observe(pending, 6).Stage == Stage.Airborne && releases == 1, "terminal_status_stable");

        var uncertain = new LaunchLifecycle(0, () => releases++);
        var waiting = uncertain.Observe(new LaunchLifecycle.Observation(), 1);
        Check(waiting.Stage == Stage.ExecutionUnknown && waiting.TrackingActive, "uncertain_acceptance_keeps_owner");
        Check(uncertain.Observe(pending, 2).Stage == Stage.Preparing, "uncertain_acceptance_recovers_progress");
        Check(uncertain.ReadFailure(3, "InvalidOperationException").TrackingActive, "native_read_exception_keeps_owner");
        Check(uncertain.Observe(pending, 4).TrackingActive, "read_exception_recovers");
        uncertain.Dispose();
        uncertain.Dispose();
        Check(releases == 2, "uncertain_reset_releases_once");

        var cancelled = new LaunchLifecycle(0, () => releases++);
        Check(!cancelled.Observe(new LaunchLifecycle.Observation { TaskCancelled = true }, 1).TrackingActive,
            "cancel_before_spawn_terminal");
        var destroyed = new LaunchLifecycle(0, () => releases++);
        Check(!destroyed.Observe(new LaunchLifecycle.Observation { FacilityUnavailable = true }, 1).TrackingActive,
            "destroyed_facility_before_spawn_terminal");
        var destroyedSpawn = new LaunchLifecycle(0, () => releases++);
        Check(!destroyedSpawn.Observe(new LaunchLifecycle.Observation { SpawnCaptured = true, SpawnUnavailable = true }, 1).TrackingActive,
            "destroyed_spawn_terminal");
        var detached = new LaunchLifecycle(0, () => releases++);
        var captured = new LaunchLifecycle.Observation { SpawnCaptured = true, SpawnedKey = "spawn", FacilityUnavailable = true, TaskCancelled = true };
        Check(detached.Observe(captured, 1).Stage == Stage.AircraftSpawned, "spawn_survives_pending_cleanup_and_facility_loss");
        detached.Dispose();

        int failureReleases = 0;
        for (int attempt = 0; attempt < 256; attempt++)
        {
            using var failed = new LaunchLifecycle(0, () => failureReleases++);
            failed.Observe(new LaunchLifecycle.Observation(), 1);
            var final = failed.Observe(new LaunchLifecycle.Observation(), 1 + LaunchLifecycle.VisibilityGraceSeconds);
            if (final.TrackingActive || final.Stage != Stage.ExecutionUnknown)
            {
                throw new Exception("missing_spawn_timeout_" + attempt);
            }
        }
        Check(failureReleases == 256, "repeated_failures_release_all_subscriptions");
        var longPending = new LaunchLifecycle(0, () => releases++);
        Check(!longPending.Observe(pending, LaunchLifecycle.MaximumTrackingSeconds).TrackingActive,
            "pending_tracking_has_absolute_bound");
        var registrationTimeout = new LaunchLifecycle(0, () => releases++);
        var unregisteredSpawn = new LaunchLifecycle.Observation { SpawnCaptured = true };
        registrationTimeout.Observe(unregisteredSpawn, 1);
        Check(!registrationTimeout.Observe(unregisteredSpawn, 1 + LaunchLifecycle.VisibilityGraceSeconds).TrackingActive,
            "captured_spawn_registration_wait_is_bounded");
        var groundedTimeout = new LaunchLifecycle(0, () => releases++);
        var grounded = new LaunchLifecycle.Observation { SpawnCaptured = true, SpawnedKey = "grounded" };
        groundedTimeout.Observe(grounded, 1);
        Check(groundedTimeout.Observe(grounded, LaunchLifecycle.MaximumTrackingSeconds).Stage == Stage.ExecutionUnknown,
            "grounded_spawn_never_invents_airborne_on_timeout");
        VerifyProviderOwnership();
        VerifyPublicationOwnership();
    }

    static void VerifyCreationOutcomes()
    {
        object nativeTask = new();
        int subscriptions = 0;
        int unsubscriptions = 0;
        LaunchLifecycle owner = null;
        void Register(object task, LaunchProgress progress)
        {
            Check(task == nativeTask, "creation_correlates_native_task");
            subscriptions++;
            owner = new LaunchLifecycle(0, () => unsubscriptions++);
            progress.Token = "native-task-token";
            progress.TrackingActive = true;
        }
        var unverified = LaunchLifecycle.Start(() => nativeTask, () => null, Register, _ => false);
        Check(unverified.Stage == Stage.ExecutionUnknown && unverified.TrackingActive && unverified.Token != "",
            "failed_reservation_verification_retains_token");
        owner.Dispose();
        var beforeCreation = LaunchLifecycle.Start<object>(() => throw new InvalidOperationException(), () => null, Register, _ => true);
        Check(beforeCreation.Stage == Stage.ExecutionUnknown && !beforeCreation.TrackingActive && subscriptions == 1,
            "creation_exception_without_task_has_no_subscription");
        var afterInsertion = LaunchLifecycle.Start<object>(() => throw new InvalidOperationException(), () => nativeTask, Register, _ => true);
        Check(afterInsertion.TrackingActive && afterInsertion.Stage == Stage.ExecutionUnknown,
            "exception_after_insertion_recovers_unique_task");
        owner.Dispose();
        var afterRegistration = LaunchLifecycle.Start(() => nativeTask, () => null, Register, _ => throw new InvalidOperationException());
        Check(afterRegistration.TrackingActive && afterRegistration.Stage == Stage.ExecutionUnknown && subscriptions == 3,
            "exception_after_registration_keeps_existing_owner");
        owner.Dispose();
        var ambiguous = LaunchLifecycle.Start<object>(() => throw new InvalidOperationException(), () => null, Register, _ => true);
        Check(!ambiguous.TrackingActive && subscriptions == 3, "uncorrelated_creation_never_fabricates_record");
        var refused = LaunchLifecycle.Start<object>(() => null, () => null, Register, _ => true);
        Check(refused.Stage == Stage.ExecutionUnknown && !refused.TrackingActive, "null_native_outcome_not_no_mutation_claim");
        Check(subscriptions == unsubscriptions, "all_creation_outcome_subscriptions_released");
    }

    static void VerifyProviderOwnership()
    {
        var game = new FakeGameFacade
        {
            LaunchAcceptance = Stage.ExecutionUnknown,
            LaunchStage = Stage.ExecutionUnknown,
            KeepUnknownLaunchTracking = true
        };
        game.Own.LaunchSupported = true;
        game.Own.LaunchRestriction = "";
        var provider = new Provider(game, "own");
        var initial = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot;
        var request = new OrderRequest
        {
            SessionId = initial.SessionId,
            RequestId = "uncertain",
            Observer = initial.Observer.Clone(),
            Observation = initial.Units[0].Identity.Clone(),
            BasedOnVersion = initial.Version,
            LaunchAircraft = new LaunchAircraft
            {
                Selection = new AircraftSelection
                {
                    Type = new UnitTypeId { Value = "class" }, Squadron = "squadron",
                    Loadout = new LoadoutId { Value = "loadout" }
                },
                Count = 1
            }
        };
        OrderResult Send(OrderRequest command)
        {
            provider.Enqueue(command.ToByteArray());
            provider.Tick();
            provider.Results.TryDequeue(out var bytes);
            var receipt = OrderResult.Parser.ParseFrom(bytes);
            while (provider.Results.TryDequeue(out _))
            {
            }
            return receipt;
        }
        Check(Send(request).Stage == Stage.ExecutionUnknown && game.LaunchPolls == 1, "uncertain_token_polled");
        Check(Send(request).Stage == Stage.ExecutionUnknown && game.LaunchMutations == 1, "uncertain_duplicate_no_reservation");
        game.KeepUnknownLaunchTracking = false;
        game.LaunchStage = Stage.ExecutionUnknown;
        provider.Tick();
        while (provider.Results.TryDequeue(out _))
        {
        }
        int terminalPollCount = game.LaunchPolls;
        provider.Tick();
        Check(game.LaunchPolls == terminalPollCount, "provider_stops_polling_terminal_unknown");
        Check(Send(request).Stage == Stage.ExecutionUnknown && game.LaunchMutations == 1, "terminal_duplicate_no_retry");
        request.RequestId = "uncertain-recovery";
        request.BasedOnVersion = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot.Version;
        game.LaunchStage = Stage.Preparing;
        Send(request);
        game.LaunchStage = Stage.AircraftSpawned;
        var view = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot;
        provider.Results.TryDequeue(out var spawnedBytes);
        var spawned = OrderResult.Parser.ParseFrom(spawnedBytes);
        Check(spawned.SpawnedObservationId != null && view.Units.Any(unit => unit.Identity.Equals(spawned.SpawnedObservationId)),
            "uncertain_recovers_spawned_observation_handle");
        provider.Invalidate();
        Check(game.LaunchResets == 1, "session_reset_calls_facade_cleanup");
        provider.Tick();
        Check(Send(request).Reason == "stale_session" && game.LaunchMutations == 2, "reset_blocks_previous_launch");
        var invalidHandle = request.Clone();
        invalidHandle.SessionId = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot.SessionId;
        invalidHandle.RequestId = "old-handle";
        Check(Send(invalidHandle).Reason == "wrong_side_or_invalid_entity", "reset_blocks_previous_handle");
        var fresh = request.Clone();
        var current = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot;
        fresh.SessionId = current.SessionId;
        fresh.Observation = current.Units[0].Identity.Clone();
        fresh.BasedOnVersion = current.Version;
        fresh.RequestId = "active";
        game.LaunchStage = Stage.ExecutionUnknown;
        game.KeepUnknownLaunchTracking = true;
        Send(fresh);
        provider.Invalidate();
        Check(game.LaunchResets == 2, "reset_uncertain_launch_cleans_up");
        current = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot;
        fresh.SessionId = current.SessionId;
        fresh.Observation = current.Units[0].Identity.Clone();
        fresh.BasedOnVersion = current.Version;
        game.LaunchAcceptance = Stage.NativeTaskCreated;
        game.LaunchStage = Stage.Preparing;
        Send(fresh);
        provider.Invalidate();
        Check(game.LaunchResets == 3, "reset_active_launch_cleans_up");
    }

    static void VerifyPublicationOwnership()
    {
        var game = new FakeGameFacade { LaunchAcceptance = Stage.ExecutionUnknown };
        game.Own.LaunchSupported = true;
        game.Own.LaunchRestriction = "";
        var provider = new Provider(game, "own");
        var snapshot = ProviderObservation.Parser.ParseFrom(provider.Tick()).Snapshot;
        var request = new OrderRequest
        {
            SessionId = snapshot.SessionId,
            RequestId = "publication-failure",
            Observer = snapshot.Observer.Clone(),
            Observation = snapshot.Units[0].Identity.Clone(),
            BasedOnVersion = snapshot.Version,
            LaunchAircraft = new LaunchAircraft
            {
                Selection = new AircraftSelection
                {
                    Type = new UnitTypeId { Value = "class" }, Squadron = "squadron",
                    Loadout = new LoadoutId { Value = "loadout" }
                },
                Count = 1
            }
        };
        void FillStatuses()
        {
            const int RetainedStatusCapacity = 128;
            for (int index = 0; index < RetainedStatusCapacity; index++)
            {
                provider.Results.Enqueue(Array.Empty<byte>());
            }
        }
        void DrainStatuses()
        {
            while (provider.Results.TryDequeue(out _))
            {
            }
        }
        void ExpectPublicationFailure()
        {
            bool failed = false;
            try
            {
                provider.Tick();
            }
            catch (InvalidOperationException exception) when (exception.Message == "status_backpressure")
            {
                failed = true;
            }
            Check(failed, "launch_status_backpressure_visible");
            DrainStatuses();
        }
        FillStatuses();
        provider.Enqueue(request.ToByteArray());
        ExpectPublicationFailure();
        provider.Tick();
        Check(game.LaunchPolls == 1 && game.LaunchMutations == 1, "uncertain_owner_survives_publication_exception");
        DrainStatuses();
        game.LaunchStage = Stage.Airborne;
        game.Spawned.Airborne = true;
        FillStatuses();
        ExpectPublicationFailure();
        int terminalPolls = game.LaunchPolls;
        provider.Tick();
        Check(game.LaunchPolls == terminalPolls, "terminal_owner_removed_before_publication_exception");
        provider.Enqueue(request.ToByteArray());
        provider.Tick();
        provider.Results.TryDequeue(out var retainedBytes);
        Check(OrderResult.Parser.ParseFrom(retainedBytes).Stage == Stage.Airborne && game.LaunchMutations == 1,
            "terminal_receipt_retained_after_publication_exception");
    }
}
