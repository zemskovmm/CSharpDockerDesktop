using System.Collections.Immutable;
using System.Text;
using System.Threading.Channels;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

public sealed partial class DesktopSession : IContainerTerminal
{
    private readonly ContainerTerminalOptions terminalOptions;
    private readonly TimeProvider terminalTime;
    private TerminalRequest? terminalRequest, runningTerminal, pendingTerminal;
    private Task terminalWork = Task.CompletedTask, terminalNotifications = Task.CompletedTask;
    private readonly CancellationTokenSource terminalNotificationLifetime = new();
    private bool terminalNotifierRunning;
    private TaskCompletionSource? terminalNotificationCurrent, terminalNotificationPending;
    private bool terminalRunner;
    private long nextTerminal;
    private ContainerTerminalSnapshot terminalSnapshot = new();
    public event EventHandler? ContainerTerminalChanged;
    public ContainerTerminalSnapshot ContainerTerminal { get { lock (gate) return terminalSnapshot; } }

    public ContainerTerminalOpenResult OpenContainerTerminal(string containerId, IReadOnlyList<string> arguments, int columns, int rows)
    {
        // Bound before copying; no caller-owned collection survives admission.
        if (!TerminalDimensions(columns, rows) || !TerminalIdentity(containerId)
            || arguments is null || arguments.Count is < 1 || arguments.Count > terminalOptions.MaximumArguments)
            return new(ContainerTerminalAdmission.InvalidRequest);
        var argv = new string[arguments.Count];
        int bytes = 0;
        for (int i = 0; i < argv.Length; i++)
        {
            var arg = arguments[i];
            if (arg is null || (i == 0 && arg.Length == 0) || arg.Length > terminalOptions.MaximumArgumentBytes
                || arg.Contains('\0') || Encoding.UTF8.GetByteCount(arg) > terminalOptions.MaximumArgumentBytes)
                return new(ContainerTerminalAdmission.InvalidRequest);
            bytes += Encoding.UTF8.GetByteCount(arg);
            if (bytes > terminalOptions.MaximumCommandBytes) return new(ContainerTerminalAdmission.InvalidRequest);
            argv[i] = arg;
        }
        lock (gate)
        {
            if (disposed || containerReads is not { } reads || !CurrentEventsLocked(reads)
                || reads.Connection is not IContainerExec || reads.Reader is null)
                return new(ContainerTerminalAdmission.Unavailable);
            if (containerSnapshot.SelectedContainerId != containerId) return new(ContainerTerminalAdmission.WrongSelection);
            if (containerSnapshot.State == ResourceReadState.Ready && !containerSnapshot.Containers.Any(c => c.Id == containerId))
                return new(ContainerTerminalAdmission.NotFound);
            if (containerSnapshot.InspectionState == ResourceReadState.Ready && containerSnapshot.Inspection is { } inspected
                && inspected.Id == containerId && (inspected.State?.Running == false || inspected.State?.Paused == true))
                return new(ContainerTerminalAdmission.Ineligible);
            CloseTerminalLocked(ContainerTerminalPhase.Closed);
            var request = new TerminalRequest(++nextTerminal, containerId, argv, columns, rows, reads,
                containerSnapshot.ConnectionGeneration, terminalOptions);
            terminalRequest = pendingTerminal = request;
            terminalSnapshot = new(request.Id, request.Generation, containerId, ContainerTerminalPhase.AwaitingEligibility);
            if (containerSnapshot.InspectionState != ResourceReadState.Ready || containerSnapshot.Inspection?.State?.Running is null
                || containerSnapshot.Inspection?.State?.Paused is null || containerSnapshot.Inspection?.Id != containerId)
                RequestInspectionLocked(reads, containerId, true);
            ReconcileTerminalLocked(reads);
            if (!terminalRunner)
            {
                terminalRunner = true;
                terminalWork = Task.Run(RunTerminalQueueAsync);
            }
            reads.TerminalWorkflow = terminalWork;
            return new(ContainerTerminalAdmission.Accepted, request.Id);
        }
    }

    public bool IsContainerTerminalCurrent(long sessionId, string containerId, long connectionGeneration)
    {
        lock (gate) return terminalRequest is { Active: true, Ready: true } r && r.Id == sessionId
            && r.ContainerId == containerId && r.Generation == connectionGeneration && CurrentTerminalLocked(r);
    }
    public ContainerTerminalEvidence? ReadContainerTerminalEvidence(long sessionId)
    {
        lock (gate) return terminalRequest is { } r && r.Id == sessionId
            ? new(r.Creation, r.Start, r.Inspection, r.Resize, r.Input, r.CleanupFailure) : null;
    }
    public Task CloseContainerTerminalAsync()
    {
        lock (gate)
        {
            var running = runningTerminal?.Done.Task ?? Task.CompletedTask;
            var requested = terminalRequest?.Done.Task ?? Task.CompletedTask;
            var reading = terminalRequest?.ReadWork ?? Task.CompletedTask;
            CloseTerminalLocked(ContainerTerminalPhase.Closed);
            return Task.WhenAll(running, requested, JoinTerminalReadAsync(reading), JoinTerminalNotificationsLocked());
        }
    }
    private void CloseTerminalLocked(ContainerTerminalPhase phase, ContainerExecFailureKind? failure = null)
    {
        if (terminalRequest is not { } r || !r.Active) return;
        r.Active = r.Ready = false; // Revoke before cancellation can reenter.
        if (ReferenceEquals(pendingTerminal, r)) pendingTerminal = null;
        terminalSnapshot = terminalSnapshot with { Phase = phase, LifecycleCurrent = false, Failure = failure };
        while (r.Output.Reader.TryRead(out _)) { }
        r.Output.Writer.TryComplete();
        SignalTerminalLocked(r);
        r.Lifetime.Cancel(); CancelTerminalIOLocked(r);
        if (!ReferenceEquals(runningTerminal, r))
        {
            r.Arguments = [];
            r.Done.TrySetResult();
            r.Lifetime.Dispose(); r.IO.Dispose();
        }
    }
    private static void CancelTerminalIOLocked(TerminalRequest r)
    {
        // Mark cancellation synchronously, run borrowed capability callbacks outside our lock.
        // This prevents reentrant Open/Close callbacks from being overwritten by the caller's mutation.
        if (!r.IO.IsCancellationRequested) r.IOCallbacks = r.IO.CancelAsync();
    }
    private bool CurrentTerminalLocked(TerminalRequest r) => !disposed && ReferenceEquals(terminalRequest, r)
        && r.Active && CurrentEventsLocked(r.Reads) && containerSnapshot.SelectedContainerId == r.ContainerId
        && containerSnapshot.ConnectionGeneration == r.Generation;
    private void InvalidateTerminalLifecycleLocked(ContainerReads reads, string? identity = null)
    {
        if (terminalRequest is not { Active: true } r || r.Reads != reads
            || (!string.IsNullOrEmpty(identity) && identity != r.ContainerId)) return;
        if (r.Ready) r.EligibilityChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Ready = false;
        terminalSnapshot = terminalSnapshot with { LifecycleCurrent = false };
        SignalTerminalLocked(r);
    }
    private void ReconcileTerminalLocked(ContainerReads reads)
    {
        if (terminalRequest is not { Active: true } r || r.Reads != reads) return;
        if (containerSnapshot.SelectedContainerId != r.ContainerId)
        { CloseTerminalLocked(ContainerTerminalPhase.Interrupted, ContainerExecFailureKind.NotFound); return; }
        if (!CurrentTerminalLocked(r)) return;
        if (containerSnapshot.State == ResourceReadState.Ready && !containerSnapshot.Containers.Any(c => c.Id == r.ContainerId))
        { CloseTerminalLocked(ContainerTerminalPhase.Interrupted, ContainerExecFailureKind.NotFound); return; }
        if (containerSnapshot.InspectionState is ResourceReadState.Failed or ResourceReadState.Unavailable)
        { CloseTerminalLocked(ContainerTerminalPhase.Rejected, ContainerExecFailureKind.UnexpectedResponse); return; }
        var inspection = containerSnapshot.Inspection;
        if (containerSnapshot.InspectionState != ResourceReadState.Ready || inspection?.Id != r.ContainerId) return;
        if (inspection.State?.Running == false || inspection.State?.Paused == true)
        { CloseTerminalLocked(ContainerTerminalPhase.Interrupted, ContainerExecFailureKind.Conflict); return; }
        r.Ready = inspection.State?.Running == true && inspection.State?.Paused == false;
        if (!r.Ready)
        { CloseTerminalLocked(ContainerTerminalPhase.Rejected, ContainerExecFailureKind.UnexpectedResponse); return; }
        terminalSnapshot = terminalSnapshot with { LifecycleCurrent = true };
        r.EligibilityChanged.TrySetResult();
        SignalTerminalLocked(r);
    }
    private async Task<bool> AwaitTerminalEligibilityAsync(TerminalRequest r, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task changed;
            lock (gate)
            {
                if (!CurrentTerminalLocked(r)) return false;
                if (r.Ready) return true;
                changed = r.EligibilityChanged.Task;
            }
            await changed.WaitAsync(cancellationToken.CanBeCanceled ? cancellationToken : r.Lifetime.Token).ConfigureAwait(false);
        }
    }
    private Task JoinTerminalNotificationsLocked() => Task.WhenAll(
        terminalNotificationCurrent?.Task ?? Task.CompletedTask, terminalNotificationPending?.Task ?? Task.CompletedTask);
    private void SignalTerminalLocked(TerminalRequest r)
    {
        if (disposed) return;
        terminalNotificationPending ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (terminalNotifierRunning) return;
        terminalNotifierRunning = true;
        terminalNotifications = Task.Run(NotifyTerminalAsync);
    }
    private async Task NotifyTerminalAsync()
    {
        bool finished = false;
        try
        {
            while (true)
            {
                await Task.Delay(terminalOptions.NotificationInterval, terminalTime, terminalNotificationLifetime.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (disposed) return;
                    terminalNotificationCurrent = terminalNotificationPending;
                    terminalNotificationPending = null;
                }
                try { ContainerTerminalChanged?.Invoke(this, EventArgs.Empty); }
                catch (Exception) { /* Observer errors never include payload in diagnostics. */ }
                lock (gate)
                {
                    terminalNotificationCurrent?.TrySetResult(); terminalNotificationCurrent = null;
                    if (terminalNotificationPending is not null) continue;
                    terminalNotifierRunning = false; finished = true; return;
                }
            }
        }
        catch (OperationCanceledException) when (terminalNotificationLifetime.IsCancellationRequested) { }
        finally
        {
            if (!finished) lock (gate)
            {
                terminalNotificationCurrent?.TrySetResult(); terminalNotificationCurrent = null;
                terminalNotificationPending?.TrySetResult(); terminalNotificationPending = null;
                terminalNotifierRunning = false;
            }
        }
    }
    private async Task RunTerminalQueueAsync()
    {
        while (true)
        {
            TerminalRequest r;
            lock (gate)
            {
                if (pendingTerminal is not { Active: true } pending)
                { terminalRunner = false; return; }
                r = pending; pendingTerminal = null; runningTerminal = r;
            }
            try { await RunTerminalAsync(r).ConfigureAwait(false); }
            catch (Exception)
            {
                lock (gate) r.CleanupFailure = new(ContainerExecFailureKind.TransportFailure, "Terminal cleanup could not complete normally.");
                FailTerminal(r, r.CleanupFailure);
            }
            finally
            {
                // Notification callbacks and all owned I/O finish before Done and connection release.
                lock (gate)
                {
                    runningTerminal = null; r.Arguments = [];
                    if (!r.Active) { r.Lifetime.Dispose(); r.IO.Dispose(); }
                    r.Done.TrySetResult();
                }
            }
        }
    }
    private ITimer TerminalDeadline(CancellationTokenSource source) => terminalTime.CreateTimer(
        _ => { try { source.Cancel(); } catch (Exception) { /* Cancellation still fences admission; callback text is private. */ } }, null,
        terminalOptions.OperationTimeout, Timeout.InfiniteTimeSpan);
    private async Task RunTerminalAsync(TerminalRequest r)
    {
        var capability = (IContainerExec)r.Reads.Connection;
        IContainerExecAttachment? attachment = null;
        using var establishment = CancellationTokenSource.CreateLinkedTokenSource(r.IO.Token);
        using var deadline = TerminalDeadline(establishment);
        using var timedOut = establishment.Token.Register(() =>
        {
            lock (gate)
                if (CurrentTerminalLocked(r) && !r.IO.IsCancellationRequested)
                    CloseTerminalLocked(ContainerTerminalPhase.Interrupted, ContainerExecFailureKind.Deadline);
        });
        Task inspection = Task.CompletedTask, attachmentWatch = Task.CompletedTask;
        using var polling = CancellationTokenSource.CreateLinkedTokenSource(r.IO.Token);
        try
        {
            if (!await AwaitTerminalEligibilityAsync(r).ConfigureAwait(false)) return;
            lock (gate)
            {
                if (!CurrentTerminalLocked(r) || !r.Ready) return;
                terminalSnapshot = terminalSnapshot with { Phase = ContainerTerminalPhase.Creating }; SignalTerminalLocked(r);
            }
            ContainerExecCreationResult created;
            try { created = await capability.CreateTtyExecAsync(new(r.ContainerId, r.Arguments), establishment.Token).ConfigureAwait(false); }
            catch (ContainerExecCreateCanceledException ex) { created = ex.Outcome; }
            catch (OperationCanceledException) { created = new(ContainerExecCreationDisposition.Uncertain, Failure: new(ContainerExecFailureKind.Canceled, "Exec creation canceled after admission.")); }
            catch (Exception) { created = new(ContainerExecCreationDisposition.Uncertain, Failure: new(ContainerExecFailureKind.TransportFailure, "Exec creation completion is unknown.")); }
            lock (gate)
            {
                r.Creation = LimitCreation(created); r.Arguments = [];
                if (CurrentTerminalLocked(r)) terminalSnapshot = terminalSnapshot with { Creation = created.Disposition };
            }
            if (!created.IsUsableAcknowledgement || created.AcknowledgedInstance!.ContainerId != r.ContainerId
                || !TerminalIdentity(created.AcknowledgedInstance.ExecId))
            { FailTerminal(r, created.Failure ?? new(ContainerExecFailureKind.UnexpectedResponse, "Exec creation did not acknowledge the exact container.")); return; }
            if (!await AwaitTerminalEligibilityAsync(r).ConfigureAwait(false) || establishment.IsCancellationRequested) return;
            lock (gate)
            {
                if (!CurrentTerminalLocked(r) || !r.Ready) return;
                r.Instance = created.AcknowledgedInstance;
                terminalSnapshot = terminalSnapshot with { Phase = ContainerTerminalPhase.Starting }; SignalTerminalLocked(r);
            }
            ContainerExecStartResult started;
            try { started = await capability.StartTtyExecAsync(r.Instance, establishment.Token).ConfigureAwait(false); }
            catch (ContainerExecStartCanceledException ex) { started = ex.Outcome; }
            catch (OperationCanceledException) { started = new(ContainerExecStartDisposition.Uncertain, Failure: new(ContainerExecFailureKind.Canceled, "Exec start canceled after admission.")); }
            catch (Exception) { started = new(ContainerExecStartDisposition.Uncertain, Failure: new(ContainerExecFailureKind.TransportFailure, "Exec start completion is unknown.")); }
            attachment = started.Attachment; // Own even a late or failed result.
            lock (gate)
            {
                r.Start = started with { Attachment = null, Response = LimitEvidence(started.Response), Failure = LimitFailure(started.Failure) };
                if (CurrentTerminalLocked(r)) terminalSnapshot = terminalSnapshot with { Start = started.Disposition };
                if (!CurrentTerminalLocked(r) || establishment.IsCancellationRequested) return;
            }
            deadline.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (started.Disposition != ContainerExecStartDisposition.Established || started.Failure is not null || attachment is null
                || attachment.Instance != r.Instance)
            { FailTerminal(r, started.Failure ?? new(ContainerExecFailureKind.UnexpectedResponse, "Exec start did not establish the exact acknowledged instance.")); return; }
            lock (gate)
            {
                if (!CurrentTerminalLocked(r)) return;
                r.Attachment = attachment; r.Attached = true;
                terminalSnapshot = terminalSnapshot with { Phase = ContainerTerminalPhase.Attached }; SignalTerminalLocked(r);
                var initialResize = new TerminalResize(r.Columns, r.Rows);
                r.ResizeRunner = true;
                r.ResizeWork = Task.Run(() => RunTerminalResizeAsync(r, initialResize));
            }
            attachmentWatch = WatchTerminalAttachmentAsync(r, attachment);
            inspection = ObserveTerminalProcessAsync(r, polling.Token);
            while (await attachment.Output.WaitToReadAsync(r.IO.Token).ConfigureAwait(false))
            {
                if (!await AwaitTerminalEligibilityAsync(r).ConfigureAwait(false)) return;
                if (!attachment.Output.TryRead(out var bytes)) continue;
                // Split arbitrary capability fragments to enforce our independent byte budget.
                for (int offset = 0; offset < bytes.Length; offset += terminalOptions.OutputChunkBytes)
                {
                    var copy = bytes.AsSpan(offset, Math.Min(terminalOptions.OutputChunkBytes, bytes.Length - offset)).ToArray().ToImmutableArray();
                    await r.Output.Writer.WriteAsync(copy, r.IO.Token).ConfigureAwait(false);
                }
            }
            var completion = await attachment.Completion.WaitAsync(r.IO.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (!CurrentTerminalLocked(r)) return;
                r.Attached = false; r.Eof = true;
                terminalSnapshot = terminalSnapshot with { Phase = ContainerTerminalPhase.Draining, AttachmentEnd = completion.Termination,
                    Failure = completion.Failure?.Kind }; SignalTerminalLocked(r);
                r.Output.Writer.TryComplete();
            }
            polling.Cancel();
            await inspection.ConfigureAwait(false);
            // Final bounded observation after EOF; completion/false/zero never synthesizes process termination.
            await InspectTerminalOnceAsync(r).ConfigureAwait(false);
            if (completion.Failure is { } failure) FailTerminal(r, failure);
        }
        catch (OperationCanceledException) when (r.IO.IsCancellationRequested || r.Lifetime.IsCancellationRequested || establishment.IsCancellationRequested) { }
        catch (Exception)
        { FailTerminal(r, new(ContainerExecFailureKind.TransportFailure, "The terminal capability was interrupted.")); }
        finally
        {
            lock (gate) { r.Attached = false; CancelTerminalIOLocked(r); r.Output.Writer.TryComplete(); }
            polling.Cancel();
            try { if (attachment is not null) await attachment.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                await Task.WhenAll(inspection, attachmentWatch, r.InputWork, r.ResizeWork, JoinTerminalReadAsync(r.ReadWork), r.IOCallbacks).ConfigureAwait(false);
                lock (gate) r.Attachment = null;
            }
        }
    }
    private async Task WatchTerminalAttachmentAsync(TerminalRequest r, IContainerExecAttachment attachment)
    {
        try
        {
            var completion = await attachment.Completion.WaitAsync(r.IO.Token).ConfigureAwait(false);
            if (completion.Termination == ContainerExecAttachmentTermination.EndOfStream) return;
            lock (gate)
            {
                if (!CurrentTerminalLocked(r)) return;
                terminalSnapshot = terminalSnapshot with { AttachmentEnd = completion.Termination };
                if (completion.Failure is { } failure) FailTerminal(r, failure);
                else CloseTerminalLocked(ContainerTerminalPhase.Interrupted, ContainerExecFailureKind.Canceled);
            }
        }
        catch (OperationCanceledException) when (r.IO.IsCancellationRequested) { }
        catch (Exception) { FailTerminal(r, new(ContainerExecFailureKind.TransportFailure, "Terminal attachment completion was unavailable.")); }
    }
    private void FailTerminal(TerminalRequest r, ContainerExecFailure failure)
    {
        lock (gate)
        {
            if (!CurrentTerminalLocked(r)) return;
            if (failure.Kind is ContainerExecFailureKind.TransportFailure or ContainerExecFailureKind.Deadline)
            {
                CloseTerminalLocked(ContainerTerminalPhase.Interrupted, failure.Kind);
                ResourceTransportLostLocked(r.Reads, new(EngineResourceFailureKind.TransportLoss, "Terminal connection was interrupted."), false);
            }
            else CloseTerminalLocked(ContainerTerminalPhase.Rejected, failure.Kind);
        }
    }
    private async Task ObserveTerminalProcessAsync(TerminalRequest r, CancellationToken pollingToken)
    {
        try
        {
            while (!pollingToken.IsCancellationRequested)
            {
                await InspectTerminalOnceAsync(r).ConfigureAwait(false);
                lock (gate) if (!CurrentTerminalLocked(r) || r.Eof || r.Inspection?.Value?.Running == false) return;
                await Task.Delay(terminalOptions.InspectionInterval, terminalTime, pollingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (pollingToken.IsCancellationRequested) { }
    }
    private async Task InspectTerminalOnceAsync(TerminalRequest r)
    {
        // Serialize periodic and EOF inspection; no queued repeated polls.
        if (Interlocked.CompareExchange(ref r.InspectBusy, 1, 0) != 0) return;
        try
        {
            lock (gate) if (!CurrentTerminalLocked(r) || r.Instance is null) return;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(r.IO.Token);
            using var timer = TerminalDeadline(cancel);
            ContainerExecInspectionResult result;
            try { result = await ((IContainerExec)r.Reads.Connection).InspectExecAsync(r.Instance!, cancel.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { result = new(Failure: new(ContainerExecFailureKind.Deadline, "Exec inspection was canceled or exceeded its deadline.")); }
            catch (Exception) { result = new(Failure: new(ContainerExecFailureKind.TransportFailure, "Exec inspection was unavailable.")); }
            lock (gate)
            {
                if (!CurrentTerminalLocked(r) || r.IO.IsCancellationRequested) return;
                if (cancel.IsCancellationRequested) result = new(Failure: new(ContainerExecFailureKind.Deadline, "Exec inspection exceeded its deadline."));
                if (result.IsSuccess && (result.Value!.RequestedInstance != r.Instance
                    || (result.Value.Id is { } execId && execId != r.Instance!.ExecId)
                    || (result.Value.ContainerId is { } containerId && containerId != r.ContainerId)))
                    result = new(Response: result.Response, Failure: new(ContainerExecFailureKind.UnexpectedResponse, "Exec inspection returned a different binding."));
                // Retain only process evidence, never unbounded process configuration/command fields.
                r.Inspection = result with { Value = result.Value is { } v ? v with { ProcessConfiguration = null, DetachKeys = null } : null,
                    Response = LimitEvidence(result.Response), Failure = LimitFailure(result.Failure) };
                terminalSnapshot = terminalSnapshot with { ObservedRunning = result.IsSuccess ? result.Value!.Running : null,
                    ObservedExitCode = result.IsSuccess ? result.Value!.ExitCode : null, InspectionFailure = result.Failure?.Kind };
                SignalTerminalLocked(r);
                if (result.Failure?.Kind == ContainerExecFailureKind.TransportFailure) FailTerminal(r, result.Failure);
            }
        }
        finally { Volatile.Write(ref r.InspectBusy, 0); }
    }
    public Task<ContainerTerminalOutput> ReadContainerTerminalOutputAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (terminalRequest is not { Active: true } r || r.Id != sessionId || !CurrentTerminalLocked(r))
                return Task.FromResult(new ContainerTerminalOutput(sessionId, [], false, true));
            if (!r.ReadWork.IsCompleted) throw new InvalidOperationException("Only one terminal output consumer wait may be admitted.");
            var lifetimeToken = r.Lifetime.Token;
            async Task<ContainerTerminalOutput> ReadAsync()
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
                try
                {
                    while (true)
                    {
                        if (!await AwaitTerminalEligibilityAsync(r, cancel.Token).ConfigureAwait(false)) return new ContainerTerminalOutput(sessionId, [], false, true);
                        var available = await r.Output.Reader.WaitToReadAsync(cancel.Token).ConfigureAwait(false);
                        lock (gate)
                        {
                            if (!CurrentTerminalLocked(r)) return new ContainerTerminalOutput(sessionId, [], false, true);
                            if (!r.Ready) continue;
                            if (r.Output.Reader.TryRead(out var bytes)) return new ContainerTerminalOutput(sessionId, bytes, false, false);
                            if (!available) return new ContainerTerminalOutput(sessionId, [], true, false);
                        }
                    }
                }
                catch (OperationCanceledException) when (r.Lifetime.IsCancellationRequested)
                { return new ContainerTerminalOutput(sessionId, [], false, true); }
            }
            var task = ReadAsync();
            r.ReadWork = task;
            return task;
        }
    }
    public Task<ContainerExecWriteResult> WriteContainerTerminalAsync(long sessionId, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (terminalRequest is not { Active: true, Ready: true, Attached: true } r || r.Id != sessionId
                || !CurrentTerminalLocked(r) || !r.InputWork.IsCompleted || bytes.Length is < 1 || bytes.Length > terminalOptions.MaximumInputBytes
                || cancellationToken.IsCancellationRequested)
                return Task.FromResult(new ContainerExecWriteResult(ContainerExecWriteDisposition.NotWritten,
                    new(ContainerExecFailureKind.InvalidRequest, "Terminal input is unavailable, invalid, or another input is in flight.")));
            var copy = bytes.ToArray();
            var task = Task.Run(async () =>
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, r.IO.Token);
                using var timer = TerminalDeadline(cancel);
                bool dispatched = false;
                using var canceled = cancel.Token.Register(() =>
                {
                    lock (gate)
                        if (dispatched && CurrentTerminalLocked(r))
                            CloseTerminalLocked(ContainerTerminalPhase.Interrupted,
                                cancellationToken.IsCancellationRequested ? ContainerExecFailureKind.Canceled : ContainerExecFailureKind.Deadline);
                });
                ContainerExecWriteResult result;
                try
                {
                    lock (gate)
                    {
                        if (!CurrentTerminalLocked(r) || !r.Ready || !r.Attached || cancel.IsCancellationRequested)
                        { r.Input = new(ContainerExecWriteDisposition.NotWritten); return r.Input; }
                        dispatched = true;
                    }
                    try { result = await r.Attachment!.WriteAsync(copy, cancel.Token).ConfigureAwait(false); }
                    catch (Exception) { result = new(ContainerExecWriteDisposition.Uncertain, new(ContainerExecFailureKind.TransportFailure, "Terminal input completion is unknown.")); }
                    lock (gate)
                    {
                        r.Input = result with { Failure = LimitFailure(result.Failure) };
                        // Possible-write uncertainty cannot admit more bytes onto this attachment.
                        if (result.Disposition == ContainerExecWriteDisposition.Uncertain && CurrentTerminalLocked(r))
                        {
                            if (result.Failure?.Kind == ContainerExecFailureKind.TransportFailure) FailTerminal(r, result.Failure);
                            else CloseTerminalLocked(ContainerTerminalPhase.Interrupted, result.Failure?.Kind ?? ContainerExecFailureKind.Canceled);
                        }
                    }
                    return r.Input;
                }
                finally { Array.Clear(copy); }
            });
            r.InputWork = task;
            return task;
        }
    }
    public Task<ContainerExecResizeResult> ResizeContainerTerminalAsync(long sessionId, int columns, int rows)
    {
        lock (gate)
        {
            if (terminalRequest is not { Active: true, Ready: true, Attached: true } r || r.Id != sessionId
                || !CurrentTerminalLocked(r) || !TerminalDimensions(columns, rows))
                return Task.FromResult(new ContainerExecResizeResult(ContainerExecResizeDisposition.NotDispatched, null, columns, rows,
                    Failure: new(ContainerExecFailureKind.InvalidRequest, "Terminal resize requires a current attached session and positive dimensions.")));
            if (r.ResizePending is { } previous) previous.Done.TrySetResult(new(ContainerExecResizeDisposition.NotDispatched,
                r.Instance, previous.Columns, previous.Rows, Failure: new(ContainerExecFailureKind.CapacityExceeded, "A later explicit dimension request superseded this pending request.")));
            var request = new TerminalResize(columns, rows);
            r.ResizePending = request;
            if (!r.ResizeRunner) { r.ResizeRunner = true; r.ResizeWork = Task.Run(() => RunTerminalResizeAsync(r)); }
            return request.Done.Task;
        }
    }
    private async Task RunTerminalResizeAsync(TerminalRequest r, TerminalResize? initial = null)
    {
        while (true)
        {
            TerminalResize request;
            lock (gate)
            {
                if (initial is { } first) { request = first; initial = null; }
                else
                {
                    if (r.ResizePending is not { } pending) { r.ResizeRunner = false; return; }
                    request = pending; r.ResizePending = null;
                }
            }
            ContainerExecResizeResult result;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(r.IO.Token);
            using var timer = TerminalDeadline(cancel);
            try
            {
                bool dispatch;
                lock (gate) dispatch = CurrentTerminalLocked(r) && r.Ready && r.Attached && !cancel.IsCancellationRequested;
                if (!dispatch) result = new(ContainerExecResizeDisposition.NotDispatched, r.Instance, request.Columns, request.Rows);
                else
                {
                    try { result = await ((IContainerExec)r.Reads.Connection).ResizeTtyExecAsync(r.Instance!, request.Columns, request.Rows, cancel.Token).ConfigureAwait(false); }
                    catch (ContainerExecResizeCanceledException ex) { result = ex.Outcome; }
                    catch (Exception) { result = new(ContainerExecResizeDisposition.Uncertain, r.Instance, request.Columns, request.Rows,
                        Failure: new(ContainerExecFailureKind.TransportFailure, "Resize completion is unknown.")); }
                }
                result = result with { Response = LimitEvidence(result.Response), BindingResponse = LimitEvidence(result.BindingResponse), Failure = LimitFailure(result.Failure) };
                lock (gate)
                {
                    r.Resize = result;
                    if (result.Failure?.Kind == ContainerExecFailureKind.TransportFailure) FailTerminal(r, result.Failure);
                }
                request.Done.TrySetResult(result);
            }
            catch (Exception) { request.Done.TrySetResult(new(ContainerExecResizeDisposition.Uncertain, r.Instance, request.Columns, request.Rows)); }
        }
    }
    private static async Task JoinTerminalReadAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* An individually canceled consumer wait is a normal completion. */ }
    }
    private static bool TerminalIdentity(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool TerminalDimensions(int columns, int rows) => columns is >= 1 and <= 65535 && rows is >= 1 and <= 65535;
    private string? LimitTerminalText(string? value)
    {
        if (value is null) return null;
        int bytes = 0, characters = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > terminalOptions.MaximumEvidenceBytes) break;
            bytes += rune.Utf8SequenceLength; characters += rune.Utf16SequenceLength;
        }
        return value[..characters];
    }
    private ContainerExecFailure? LimitFailure(ContainerExecFailure? f) => f is null ? null : f with
        { Description = LimitTerminalText(f.Description)! };
    private ContainerExecResponseEvidence? LimitEvidence(ContainerExecResponseEvidence? e) => e is null ? null : e with
    {
        RawBody = e.RawBody.IsDefault ? [] : e.RawBody.Take(terminalOptions.MaximumEvidenceBytes).ToImmutableArray(),
        BodyUnavailable = e.BodyUnavailable || (!e.RawBody.IsDefault && e.RawBody.Length > terminalOptions.MaximumEvidenceBytes),
        DaemonMessage = LimitTerminalText(e.DaemonMessage)
    };
    private ContainerExecCreationResult LimitCreation(ContainerExecCreationResult result) => result with
        { Response = LimitEvidence(result.Response), Failure = LimitFailure(result.Failure) };
    private sealed class TerminalResize(int columns, int rows)
    {
        public int Columns { get; } = columns;
        public int Rows { get; } = rows;
        public TaskCompletionSource<ContainerExecResizeResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class TerminalRequest(long id, string containerId, string[] arguments, int columns, int rows,
        ContainerReads reads, long generation, ContainerTerminalOptions options)
    {
        public long Id { get; } = id;
        public string ContainerId { get; } = containerId;
        public long Generation { get; } = generation;
        public string[] Arguments = arguments;
        public int Columns { get; } = columns;
        public int Rows { get; } = rows;
        public ContainerReads Reads { get; } = reads;
        public CancellationTokenSource Lifetime { get; } = new();
        public CancellationTokenSource IO { get; } = new();
                public Channel<ImmutableArray<byte>> Output { get; } = Channel.CreateBounded<ImmutableArray<byte>>(
            new BoundedChannelOptions(options.OutputChunks) { FullMode = BoundedChannelFullMode.Wait });
        public TaskCompletionSource EligibilityChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task IOCallbacks = Task.CompletedTask, InputWork = Task.CompletedTask, ResizeWork = Task.CompletedTask, ReadWork = Task.CompletedTask;
        public bool Active = true, Ready, Attached, Eof, ResizeRunner;
        public int InspectBusy;
        public ContainerExecInstance? Instance;
        public IContainerExecAttachment? Attachment;
        public TerminalResize? ResizePending;
        public ContainerExecCreationResult? Creation;
        public ContainerExecStartResult? Start;
        public ContainerExecInspectionResult? Inspection;
        public ContainerExecResizeResult? Resize;
        public ContainerExecWriteResult? Input;
        public ContainerExecFailure? CleanupFailure;
    }
}
