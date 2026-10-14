using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

/// <summary>Bounded coordination defaults, independently tunable from the transport buffer budget.</summary>
public sealed record ContainerReconciliationOptions
{
    public TimeSpan BatchInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan ReplayOverlap { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RecoveryCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumRecoveryAttempts { get; init; } = 6;
    public int ObservationBatchSize { get; init; } = 128;

    internal void Validate()
    {
        if (BatchInterval <= TimeSpan.Zero || BatchInterval.TotalMilliseconds > int.MaxValue
            || ReplayOverlap < TimeSpan.Zero || ReplayOverlap.TotalMilliseconds > int.MaxValue
            || RetryDelay <= TimeSpan.Zero || RetryDelay.TotalMilliseconds > int.MaxValue
            || RecoveryCooldown <= TimeSpan.Zero || RecoveryCooldown.TotalMilliseconds > int.MaxValue
            || MaximumRecoveryAttempts < 0 || ObservationBatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(ContainerReconciliationOptions));
    }
}

public sealed partial class DesktopSession
{
    // Retain only a high-water timestamp and Engine identity, never event metadata/history.
    private EngineEventCursor? eventCursor;
    private EngineEndpoint? cursorEndpoint;
    private string? cursorEngineId;

    private bool CurrentEventsLocked(ContainerReads reads)
        => !disposed && ReferenceEquals(containerReads, reads) && !reads.Lifetime.IsCancellationRequested;

    private void RequestEventSnapshot(ContainerReads reads, bool initial)
    {
        lock (gate)
        {
            if (!CurrentEventsLocked(reads)) return;
            InvalidatePreparationLocked(VolumePreparationFailureKind.ObservedChange);
            if (initial) reads.InitialObservationPending = false;
            if (initial) reads.Refresh = true;
            else reads.EventRefresh = true;
            // A reopened stream may have missed any selected resource's changes.
            InvalidateEventDetailLocked(reads, null);
            reads.Signal.Writer.TryWrite(0);
            RequestImageEventSnapshotLocked(reads, initial);
            RequestVolumeEventSnapshotLocked(reads, initial);
            RequestNetworkEventSnapshotLocked(reads, initial);
        }
        NotifyContainerBrowsers();
    }

    private async Task ObserveContainerEventsAsync(ContainerReads reads)
    {
        var token = reads.Lifetime.Token;
        if (reads.Connection is not IEngineEventSource source)
        {
            RequestEventSnapshot(reads, initial: true);
            return;
        }
        bool initial = true;
        int failures = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                IEngineEventSubscription? subscription = null;
                EngineEventFailure? failure;
                using var opening = CancellationTokenSource.CreateLinkedTokenSource(token);
                // The fallback snapshot must not depend on a cooperative subscription opener.
                // Ownership still joins the eventual opening result before releasing the connection.
                bool openingInitial = initial;
                using var fallback = opening.Token.Register(() =>
                {
                    if (!token.IsCancellationRequested) RequestEventSnapshot(reads, openingInitial);
                });
                opening.CancelAfter(options.OperationTimeout);
                try
                {
                    EngineEventSubscriptionAttempt attempt;
                    try { attempt = await source.SubscribeEventsAsync(ReplayCursor(reads), opening.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { attempt = EngineEventSubscriptionAttempt.Failed(new(EngineEventFailureKind.OpeningDeadline, "Event observation opening exceeded its deadline.")); }
                    // Take ownership even when a source returns successfully after cancellation.
                    subscription = attempt.Subscription;
                    token.ThrowIfCancellationRequested();
                    if (opening.IsCancellationRequested)
                        failure = new(EngineEventFailureKind.OpeningDeadline, "Event observation opening exceeded its deadline.");
                    else if (subscription is not null)
                    {
                        // Opening's token controls the established subscription too: remove only its deadline.
                        opening.CancelAfter(Timeout.InfiniteTimeSpan);
                        PublishEventFailure(reads, null, false);
                        RequestEventSnapshot(reads, initial);
                        initial = false;
                        int batch = 0;
                        await foreach (var observation in subscription.Events.ReadAllAsync(token).ConfigureAwait(false))
                        {
                            ObserveContainerEvent(reads, observation);
                            failures = 0;
                            // Yield periodically even when the channel is continually readable.
                            if (++batch == reconciliation.ObservationBatchSize) { batch = 0; await Task.Yield(); }
                        }
                        failure = await subscription.Completion.ConfigureAwait(false)
                            ?? new(EngineEventFailureKind.UnexpectedEnd, "Engine event observation ended unexpectedly.");
                    }
                    else failure = attempt.Failure!;
                }
                finally
                {
                    if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                if (failure.Kind == EngineEventFailureKind.TransportLoss)
                {
                    lock (gate)
                    {
                        if (!CurrentEventsLocked(reads)) return;
                        snapshot = snapshot with { EventFailure = failure, EventRecoveryPending = false };
                        ResourceTransportLostLocked(reads, new(EngineResourceFailureKind.TransportLoss,
                            failure.Description, failure.HttpStatusCode, failure.DaemonMessage), false);
                    }
                    Changed?.Invoke(this, EventArgs.Empty);
                    NotifyContainerBrowsers();
                    ContainerOperationChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
                // Observation failure does not disable manual browsing or navigation.
                bool retry = reconciliation.MaximumRecoveryAttempts > 0;
                PublishEventFailure(reads, failure, retry);
                RequestEventSnapshot(reads, initial);
                initial = false;
                if (!retry) return;
                bool cooldown = ++failures > reconciliation.MaximumRecoveryAttempts;
                await delay.DelayAsync(cooldown ? reconciliation.RecoveryCooldown : reconciliation.RetryDelay, token).ConfigureAwait(false);
                if (cooldown) failures = 0;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void PublishEventFailure(ContainerReads reads, EngineEventFailure? failure, bool retry)
    {
        lock (gate)
        {
            if (!CurrentEventsLocked(reads)) return;
            snapshot = snapshot with { EventFailure = failure, EventRecoveryPending = retry };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ObserveContainerEvent(ContainerReads reads, EngineEvent observation)
    {
        bool notify;
        lock (gate)
        {
            if (!CurrentEventsLocked(reads)) return;
            RememberCursorLocked(reads.Connection.Metadata, observation.Timestamp);
            if (string.Equals(observation.Type, "container", StringComparison.Ordinal))
                InvalidatePreparationLocked(VolumePreparationFailureKind.ObservedChange);
            else if (string.Equals(observation.Type, "volume", StringComparison.Ordinal))
                InvalidatePreparationLocked(VolumePreparationFailureKind.ObservedChange,
                    name: UsableVolumeEventIdentity(observation.ResourceId));
            if (string.Equals(observation.Type, "image", StringComparison.Ordinal))
                notify = ObserveImageEventLocked(reads, observation.ResourceId);
            else if (string.Equals(observation.Type, "volume", StringComparison.Ordinal))
                notify = ObserveVolumeEventLocked(reads, observation.ResourceId);
            else if (string.Equals(observation.Type, "network", StringComparison.Ordinal))
                notify = ObserveNetworkEventLocked(reads, observation.ResourceId);
            else if (string.Equals(observation.Type, "container", StringComparison.Ordinal))
            {
                ++reads.ObservationRevision;
                if (volumeReads is { } volumes && volumes.Containers == reads) ++volumes.ObservationRevision;
                reads.EventRefresh = true;
                notify = InvalidateEventDetailLocked(reads, observation.ResourceId);
                reads.Signal.Writer.TryWrite(0);
            }
            else return;
        }
        // List publication is batched; only the first invalidation clears exposed detail content.
        NotifyPreparationChanged();
        if (notify) NotifyContainerBrowsers();
    }

    private bool InvalidateEventDetailLocked(ContainerReads reads, string? identity)
    {
        if (containerSnapshot.SelectedContainerId is not { } selected
            || containerSnapshot.InspectionState == ResourceReadState.NotRequested
            || (!string.IsNullOrEmpty(identity) && identity != selected)) return false;
        reads.Inspect = true;
        bool changed = containerSnapshot.Inspection is not null || containerSnapshot.InspectionState != ResourceReadState.Loading;
        containerSnapshot = containerSnapshot with { Inspection = null,
            InspectionState = ResourceReadState.Loading, InspectionFailure = null };
        return changed;
    }

    private EngineEventCursor? ReplayCursor(ContainerReads reads)
    {
        lock (gate)
        {
            var metadata = reads.Connection.Metadata;
            if (cursorEndpoint != metadata.Endpoint || cursorEngineId != metadata.EngineId)
            {
                eventCursor = null;
                cursorEndpoint = metadata.Endpoint;
                cursorEngineId = metadata.EngineId;
            }
            if (eventCursor is not { } cursor) return null;
            long seconds = cursor.UnixSeconds - reconciliation.ReplayOverlap.Ticks / TimeSpan.TicksPerSecond;
            long nanos = cursor.Nanoseconds - (reconciliation.ReplayOverlap.Ticks % TimeSpan.TicksPerSecond) * 100;
            if (nanos < 0) { --seconds; nanos += 1_000_000_000; }
            return seconds < 0 ? new(0) : new(seconds, (int)nanos);
        }
    }

    private void RememberCursorLocked(EngineMetadata metadata, EngineEventTimestamp timestamp)
    {
        EngineEventCursor? candidate = timestamp.UnixNanoseconds is >= 0 and var nanos
            ? new(nanos / 1_000_000_000, (int)(nanos % 1_000_000_000))
            : timestamp.UnixSeconds is >= 0 and var seconds ? new(seconds) : null;
        if (candidate is not { } value) return;
        cursorEndpoint = metadata.Endpoint;
        cursorEngineId = metadata.EngineId;
        if (eventCursor is not { } previous || value.UnixSeconds > previous.UnixSeconds
            || (value.UnixSeconds == previous.UnixSeconds && value.Nanoseconds > previous.Nanoseconds)) eventCursor = value;
    }
}
