using System.Collections.Immutable;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static class TerminalSessionChecks
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string E = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string Secret = "private-token-太陽";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static void Check(bool value, string message = "Terminal session assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static TaskCompletionSource<T> Source<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> predicate)
    { using var cancel = new CancellationTokenSource(Limit); while (!predicate()) await Task.Delay(1, cancel.Token); }
    private static DesktopSession Session(Connection c, ContainerTerminalOptions? options = null, ManualTime? time = null, Connection? next = null)
        => new(new Client(c, next ?? c), new() { LivenessInterval = TimeSpan.FromHours(1), RetryDelay = TimeSpan.FromMilliseconds(1) },
            reconciliation: new() { BatchInterval = TimeSpan.FromMilliseconds(1) }, terminalOptions: options, terminalTimeProvider: time);
    private static async Task Ready(DesktopSession s)
    { s.Start(); await Until(() => s.ContainerSnapshot.State == ResourceReadState.Ready); s.SelectContainer(A); await Until(() => s.ContainerSnapshot.InspectionState == ResourceReadState.Ready); }
    private static long Open(DesktopSession s)
    { var result = s.OpenContainerTerminal(A, ["command", Secret], 91, 37); Check(result.Accepted); return result.SessionId; }
    private static async Task Attached(DesktopSession s) => await Until(() => s.ContainerTerminal.Phase == ContainerTerminalPhase.Attached);

    public static async Task AdmissionAndExactBinding()
    {
        var c = new Connection(); await using var s = Session(c);
        Check(s.OpenContainerTerminal(A, ["command"], 80, 24).Admission == ContainerTerminalAdmission.Unavailable);
        s.Start(); await Until(() => s.ContainerSnapshot.State == ResourceReadState.Ready);
        Check(s.OpenContainerTerminal(A, ["command"], 80, 24).Admission == ContainerTerminalAdmission.WrongSelection);
        var inspect = Source<EngineResourceResult<ContainerInspection>>(); c.Inspect = (_, _) => inspect.Task;
        s.SelectContainer(A); var id = Open(s);
        await Task.Delay(15); Check(c.Creates == 0 && s.ContainerTerminal.Phase == ContainerTerminalPhase.AwaitingEligibility);
        inspect.SetResult(EngineResourceResult<ContainerInspection>.Success(Inspection(A, State())));
        await Attached(s); await Until(() => c.Resizes == 1);
        Check(c.Creates == 1 && c.Starts == 1 && c.Started == new ContainerExecInstance(A, E));
        Check(c.Created!.Arguments.SequenceEqual(new[] { "command", Secret }));
        Check(c.Dimensions.SequenceEqual(new[] { (91, 37) }));
        Check(s.ContainerTerminal.Creation == ContainerExecCreationDisposition.Acknowledged
            && s.ContainerTerminal.Start == ContainerExecStartDisposition.Established);
        Check(s.IsContainerTerminalCurrent(id, A, s.ContainerTerminal.ConnectionGeneration)
            && !s.IsContainerTerminalCurrent(id, B, s.ContainerTerminal.ConnectionGeneration)
            && !s.IsContainerTerminalCurrent(id + 1, A, s.ContainerTerminal.ConnectionGeneration));
        Check(!s.OpenContainerTerminal(A, [], 80, 24).Accepted && !s.OpenContainerTerminal(A, ["x"], 0, 24).Accepted
            && !s.OpenContainerTerminal(A, [new string('x', 5000)], 80, 24).Accepted);
    }
    public static async Task LifecycleAuthorityAndAdmission()
    {
        foreach (var state in new[] { State(false, false), State(true, true), State(null, false), State(true, null) })
        {
            var c = new Connection { Runtime = state }; await using var s = Session(c); await Ready(s);
            var opening = s.OpenContainerTerminal(A, ["command"], 80, 24);
            if (state.Running == false || state.Paused == true) Check(opening.Admission == ContainerTerminalAdmission.Ineligible);
            else { Check(opening.Accepted); await Until(() => s.ContainerTerminal.Phase == ContainerTerminalPhase.Rejected); }
            Check(c.Creates == 0, "Running list labels cannot establish eligibility");
        }
        foreach (int action in new[] { 0, 1, 2, 3 })
        {
            var c = new Connection(); await using var s = Session(c); await Ready(s); var id = Open(s); await Attached(s);
            var evidence = Source<EngineResourceResult<ContainerInspection>>(); c.Inspect = (_, _) => evidence.Task;
            await c.Emit("pause"); await Until(() => !s.ContainerTerminal.LifecycleCurrent);
            Check((await s.WriteContainerTerminalAsync(id, new byte[] { 3 })).Disposition == ContainerExecWriteDisposition.NotWritten);
            Check(!s.IsContainerTerminalCurrent(id, A, s.ContainerTerminal.ConnectionGeneration));
            evidence.SetResult(action switch
            {
                0 => EngineResourceResult<ContainerInspection>.Success(Inspection(A, State(false, false))),
                1 => EngineResourceResult<ContainerInspection>.Success(Inspection(A, State(true, true))),
                2 => EngineResourceResult<ContainerInspection>.Failed(new(EngineResourceFailureKind.NotFound, "gone")),
                _ => EngineResourceResult<ContainerInspection>.Success(Inspection(A, State()))
            });
            if (action == 3) { await Until(() => s.ContainerTerminal.LifecycleCurrent); Check(c.Creates == 1 && !c.Stream.Disposed); }
            else { await Until(() => c.Stream.Disposed); Check((await s.ReadContainerTerminalOutputAsync(id)).Revoked); }
        }
    }
    public static async Task UncertainEstablishmentAndNoReplay()
    {
        foreach (int stage in new[] { 0, 1, 2, 3 })
        {
            var c = new Connection();
            if (stage == 0) c.Create = (_, _) => Task.FromResult(new ContainerExecCreationResult(ContainerExecCreationDisposition.Uncertain,
                Failure: new(ContainerExecFailureKind.DaemonFailure, "creation unknown")));
            if (stage == 1) c.Start = (_, _) => Task.FromResult(new ContainerExecStartResult(ContainerExecStartDisposition.Uncertain,
                Failure: new(ContainerExecFailureKind.DaemonFailure, "start unknown")));
            if (stage == 2) c.Create = (_, token) => throw new ContainerExecCreateCanceledException(new(ContainerExecCreationDisposition.Acknowledged,
                new(A, E), Failure: new(ContainerExecFailureKind.Canceled, "canceled")), token);
            if (stage == 3) c.Start = (_, token) => throw new ContainerExecStartCanceledException(new(ContainerExecStartDisposition.Uncertain,
                Failure: new(ContainerExecFailureKind.Canceled, "canceled")), token);
            await using var s = Session(c); await Ready(s); var id = Open(s);
            await Until(() => s.ContainerTerminal.Phase == ContainerTerminalPhase.Rejected);
            s.RefreshContainers(); await Task.Delay(20);
            Check(c.Creates == 1 && c.Starts == (stage is 1 or 3 ? 1 : 0));
            Check(s.ReadContainerTerminalEvidence(id)!.Creation is not null);
        }
    }
    public static async Task ReplacementCloseAndLateAttachments()
    {
        foreach (bool duringCreate in new[] { true, false })
        {
            var c = new Connection(); var creating = Source<ContainerExecCreationResult>(); var starting = Source<ContainerExecStartResult>();
            if (duringCreate) c.Create = (_, _) => creating.Task; else c.Start = (_, _) => starting.Task;
            var s = Session(c); await Ready(s); var first = Open(s); await Until(() => duringCreate ? c.Creates == 1 : c.Starts == 1);
            for (int i = 0; i < 1000; i++) Open(s);
            Check(c.Creates == 1 && c.Starts == (duringCreate ? 0 : 1));
            var closing = s.CloseContainerTerminalAsync(); var again = s.CloseContainerTerminalAsync();
            Check(!closing.IsCompleted && !again.IsCompleted && !s.IsContainerTerminalCurrent(first, A, s.ContainerTerminal.ConnectionGeneration));
            if (duringCreate) creating.SetResult(new(ContainerExecCreationDisposition.Acknowledged, new(A, E)));
            else starting.SetResult(new(ContainerExecStartDisposition.Established, c.Stream));
            await Task.WhenAll(closing, again).WaitAsync(Limit);
            Check(c.Creates == 1 && c.Starts == (duringCreate ? 0 : 1));
            if (!duringCreate) Check(c.Stream.Disposed);
            await s.DisposeAsync(); Check(c.Disposed);
        }
        // A single latest replacement starts after late attachment disposal joins.
        var connection = new Connection(); var late = Source<ContainerExecStartResult>();
        var obsolete = connection.Stream; obsolete.DisposeRelease = Source<bool>(); connection.Start = (_, _) => late.Task;
        var session = Session(connection); await Ready(session); Open(session); await Until(() => connection.Starts == 1);
        var latest = Open(session); connection.Start = (_, _) => Task.FromResult(new ContainerExecStartResult(ContainerExecStartDisposition.Established, new Attachment()));
        late.SetResult(new(ContainerExecStartDisposition.Established, obsolete)); await Until(() => obsolete.Disposed);
        Check(connection.Starts == 1); obsolete.DisposeRelease.SetResult(true);
        await Attached(session); Check(connection.Starts == 2 && session.ContainerTerminal.SessionId == latest);
        await session.DisposeAsync();
    }
    public static async Task BackpressureRawOrderingAndEofDrain()
    {
        var c = new Connection(); await using var s = Session(c, new() { OutputChunks = 2, OutputChunkBytes = 2 });
        await Ready(s); var id = Open(s); await Attached(s);
        var bytes = new byte[] { 0xff, 0, 27, 91, 0xc3, 0x28, 3, 9 };
        var producer = Task.Run(async () => { for (int i = 0; i < 100; i++) await c.Stream.OutputWriter.WriteAsync(bytes.ToImmutableArray()); c.Stream.End(); });
        await Task.Delay(20); Check(!producer.IsCompleted, "Lossless bounded output must backpressure a slow consumer");
        var received = new List<byte>();
        while (true)
        {
            var output = await s.ReadContainerTerminalOutputAsync(id).WaitAsync(Limit);
            Check(!output.Revoked && output.Bytes.Length <= 2);
            if (output.Completed) break;
            received.AddRange(output.Bytes);
        }
        await producer.WaitAsync(Limit); await Until(() => c.Stream.Disposed);
        Check(received.SequenceEqual(Enumerable.Range(0, 100).SelectMany(_ => bytes)));
        Check(s.ContainerTerminal.Phase == ContainerTerminalPhase.Draining && s.ContainerTerminal.ObservedRunning is null
            && s.ContainerTerminal.ObservedExitCode is null);
        // Previously admitted bytes drain even after owned attachment disposal, until explicit revocation.
        var c2 = new Connection(); await using var s2 = Session(c2); await Ready(s2); var id2 = Open(s2); await Attached(s2);
        await c2.Stream.OutputWriter.WriteAsync(bytes.ToImmutableArray()); c2.Stream.End(); await Until(() => c2.Stream.Disposed);
        Check((await s2.ReadContainerTerminalOutputAsync(id2)).Bytes.SequenceEqual(bytes));
        await s2.CloseContainerTerminalAsync(); Check((await s2.ReadContainerTerminalOutputAsync(id2)).Revoked);
    }
    public static async Task InputUncertaintyControlByteAndClose()
    {
        var c = new Connection(); await using var s = Session(c); await Ready(s); var id = Open(s); await Attached(s);
        Check((await s.WriteContainerTerminalAsync(id, new byte[] { 3 })).Disposition == ContainerExecWriteDisposition.Written);
        Check(c.Stream.Inputs.Single().SequenceEqual(new byte[] { 3 }) && c.Commands == 0);
        var write = Source<ContainerExecWriteResult>(); c.Stream.Write = (_, _) => write.Task;
        var pending = s.WriteContainerTerminalAsync(id, new byte[] { 1, 2 }); await Until(() => c.Stream.Inputs.Count == 2);
        Check((await s.WriteContainerTerminalAsync(id, new byte[] { 7 })).Disposition == ContainerExecWriteDisposition.NotWritten);
        var close = s.CloseContainerTerminalAsync(); Check(!close.IsCompleted);
        write.SetResult(new(ContainerExecWriteDisposition.Uncertain, new(ContainerExecFailureKind.Canceled, Secret)));
        Check((await pending).Disposition == ContainerExecWriteDisposition.Uncertain); await close.WaitAsync(Limit);
        Check(c.Stream.Inputs.Count == 2 && c.Commands == 0 && s.ReadContainerTerminalEvidence(id)!.Input!.Disposition == ContainerExecWriteDisposition.Uncertain);
        var second = new Connection(); await using var session = Session(second); await Ready(session); var id2 = Open(session); await Attached(session);
        second.Stream.Write = (_, _) => Task.FromResult(new ContainerExecWriteResult(ContainerExecWriteDisposition.Uncertain));
        await session.WriteContainerTerminalAsync(id2, new byte[] { 2 }); await Until(() => second.Stream.Disposed);
        Check((await session.WriteContainerTerminalAsync(id2, new byte[] { 2 })).Disposition == ContainerExecWriteDisposition.NotWritten);
    }
    public static async Task ResizeCoalescingAndHistoricalResults()
    {
        var c = new Connection(); var first = Source<ContainerExecResizeResult>(); c.Resize = (_, _, _, _) => first.Task;
        await using var s = Session(c); await Ready(s); var id = Open(s); await Attached(s); await Until(() => c.Resizes == 1);
        var superseded = s.ResizeContainerTerminalAsync(id, 100, 40); var latest = s.ResizeContainerTerminalAsync(id, 120, 50);
        Check((await superseded).Disposition == ContainerExecResizeDisposition.NotDispatched && c.Resizes == 1);
        c.Resize = (instance, cols, rows, _) => Task.FromResult(new ContainerExecResizeResult(ContainerExecResizeDisposition.Acknowledged, instance, cols, rows));
        first.SetResult(new(ContainerExecResizeDisposition.Uncertain, new(A, E), 91, 37));
        Check((await latest.WaitAsync(Limit)).Disposition == ContainerExecResizeDisposition.Acknowledged);
        Check(c.Dimensions.SequenceEqual(new[] { (91, 37), (120, 50) }));
        Check((await s.ResizeContainerTerminalAsync(id, 0, 5)).Disposition == ContainerExecResizeDisposition.NotDispatched);
        var blocked = Source<ContainerExecResizeResult>(); c.Resize = (_, _, _, _) => blocked.Task;
        var dispatched = s.ResizeContainerTerminalAsync(id, 130, 60); await Until(() => c.Resizes == 3);
        var pending = s.ResizeContainerTerminalAsync(id, 140, 70); var closing = s.CloseContainerTerminalAsync(); Check(!closing.IsCompleted);
        blocked.SetResult(new(ContainerExecResizeDisposition.Uncertain, new(A, E), 130, 60));
        Check((await dispatched).Disposition == ContainerExecResizeDisposition.Uncertain
            && (await pending).Disposition == ContainerExecResizeDisposition.NotDispatched);
        await closing.WaitAsync(Limit); Check(c.Resizes == 3);
    }
    public static async Task ProcessEvidenceAndInspectionJoin()
    {
        var c = new Connection(); var inspect = Source<ContainerExecInspectionResult>(); c.ExecInspect = (_, _) => inspect.Task;
        var s = Session(c); await Ready(s); var id = Open(s); await Attached(s); await Until(() => c.ExecInspections == 1);
        Check(s.ContainerTerminal.ObservedRunning is null && s.ContainerTerminal.ObservedExitCode is null);
        var closing = s.CloseContainerTerminalAsync(); Check(!closing.IsCompleted);
        inspect.SetResult(new(Value: Process(false, 0))); await closing.WaitAsync(Limit);
        Check(s.ContainerTerminal.ObservedRunning is null, "Late inspection after revocation must not publish process evidence");
        await s.DisposeAsync();
        var c2 = new Connection { ExecInspect = (_, _) => Task.FromResult(new ContainerExecInspectionResult(Value: Process(true, 0))) };
        await using var s2 = Session(c2); await Ready(s2); Open(s2); await Attached(s2); await Until(() => s2.ContainerTerminal.ObservedRunning == true);
        Check(s2.ContainerTerminal.ObservedExitCode == 0, "Observed zero while running is evidence, never inferred success");
        c2.ExecInspect = (_, _) => Task.FromResult(new ContainerExecInspectionResult(Value: Process(false, 127)));
        c2.Stream.End(); await Until(() => c2.Stream.Disposed);
        Check(s2.ContainerTerminal.ObservedRunning == false && s2.ContainerTerminal.ObservedExitCode == 127);
        int count = c2.ExecInspections; await Task.Delay(15); Check(c2.ExecInspections == count);
    }
    public static async Task DisconnectReconnectAndNavigation()
    {
        foreach (int action in new[] { 0, 1, 2 })
        {
            var c = new Connection(); var next = new Connection(); var late = Source<ContainerExecStartResult>(); c.Start = (_, _) => late.Task;
            var s = Session(c, next: next); await Ready(s); var id = Open(s); await Until(() => c.Starts == 1);
            Task closing = action switch { 0 => s.DisconnectAsync(), 1 => s.DisposeAsync().AsTask(), _ => Task.CompletedTask };
            if (action == 2) s.SelectContainer(B);
            Check(!s.IsContainerTerminalCurrent(id, A, s.ContainerTerminal.ConnectionGeneration));
            late.SetResult(new(ContainerExecStartDisposition.Established, c.Stream));
            await closing.WaitAsync(Limit); await Until(() => c.Stream.Disposed);
            if (action == 0)
            {
                s.Start(); await Until(() => s.ContainerSnapshot.State == ResourceReadState.Ready);
                Check(next.Creates == 0 && next.Starts == 0 && s.ContainerTerminal.Phase == ContainerTerminalPhase.Interrupted);
            }
            await s.DisposeAsync();
        }
        // Transport loss delegates to the existing recovery owner; recovery never reopens exec.
        var failed = new Connection(); var recovered = new Connection();
        await using var session = Session(failed, next: recovered); await Ready(session); Open(session); await Attached(session);
        failed.Stream.End(new(ContainerExecAttachmentTermination.Failed, new(ContainerExecFailureKind.TransportFailure, Secret)));
        await Until(() => recovered.Disposed == false && failed.Disposed && session.ContainerSnapshot.State == ResourceReadState.Ready);
        Check(recovered.Creates == 0 && recovered.Starts == 0 && session.ContainerTerminal.Phase == ContainerTerminalPhase.Interrupted);
    }
    public static async Task DeadlineLateSuccessAndJoinedDisposal()
    {
        foreach (bool create in new[] { true, false })
        {
            var c = new Connection(); var time = new ManualTime(); var creating = Source<ContainerExecCreationResult>(); var starting = Source<ContainerExecStartResult>();
            if (create) c.Create = (_, _) => creating.Task; else c.Start = (_, _) => starting.Task;
            var s = Session(c, new() { OperationTimeout = TimeSpan.FromSeconds(10) }, time); await Ready(s); var id = Open(s);
            await Until(() => create ? c.Creates == 1 : c.Starts == 1); time.Advance(TimeSpan.FromSeconds(11));
            await Until(() => s.ContainerTerminal.Phase == ContainerTerminalPhase.Interrupted);
            var disposal = s.DisposeAsync().AsTask(); Check(!disposal.IsCompleted && !c.Disposed);
            if (create) creating.SetResult(new(ContainerExecCreationDisposition.Acknowledged, new(A, E)));
            else { c.Stream.DisposeRelease = Source<bool>(); starting.SetResult(new(ContainerExecStartDisposition.Established, c.Stream)); await Until(() => c.Stream.Disposed); Check(!disposal.IsCompleted); c.Stream.DisposeRelease.SetResult(true); }
            await disposal.WaitAsync(Limit); Check(c.Disposed && c.Creates == 1 && c.Starts == (create ? 0 : 1));
            Check(s.ReadContainerTerminalEvidence(id)!.Creation!.Disposition == ContainerExecCreationDisposition.Acknowledged);
        }
    }
    public static async Task PrivacyBudgetsNotificationsAndCoexistence()
    {
        var c = new Connection(); var time = new ManualTime();
        c.Create = (_, _) => Task.FromResult(new ContainerExecCreationResult(ContainerExecCreationDisposition.Acknowledged, new(A, E),
            new(201, Enumerable.Repeat((byte)42, 100).ToImmutableArray(), false, Secret)));
        var s = Session(c, new() { MaximumEvidenceBytes = 8, OutputChunks = 2, OutputChunkBytes = 4 }, time);
        await Ready(s); Check(s.OpenContainerLogs(A)); Check(s.OpenContainerStats(A)); var id = Open(s); await Attached(s);
        await Until(() => c.LogsOpened && c.StatsOpened);
        int notices = 0; s.ContainerTerminalChanged += (_, _) => Interlocked.Increment(ref notices);
        for (int i = 0; i < 100; i++)
        {
            await c.Stream.OutputWriter.WriteAsync(new byte[] { 255, 27, 3 }.ToImmutableArray());
            await s.ReadContainerTerminalOutputAsync(id);
        }
        Check(notices == 0, "Raw fragments cannot schedule observer work");
        time.Advance(TimeSpan.FromSeconds(1)); await Until(() => notices == 1);
        var evidence = s.ReadContainerTerminalEvidence(id)!;
        Check(evidence.Creation!.Response!.RawBody.Length == 8 && evidence.Creation.Response.BodyUnavailable);
        Check(!evidence.ToString().Contains(Secret) && !s.ContainerTerminal.ToString().Contains(Secret)
            && !new ContainerExecWriteResult(ContainerExecWriteDisposition.Uncertain, new(ContainerExecFailureKind.DaemonFailure, Secret)).ToString().Contains(Secret)
            && !new ContainerTerminalOutput(id, System.Text.Encoding.UTF8.GetBytes(Secret).ToImmutableArray(), false, false).ToString().Contains(Secret));
        // Avoid a fake-time notification wait during the explicit close assertion.
        var closing = s.CloseContainerTerminalAsync();
        for (int i = 0; i < 100 && !closing.IsCompleted; ++i) { time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(1); }
        await closing.WaitAsync(Limit);
        Check(!c.LogsDisposed && !c.StatsDisposed && c.Commands == 0); await s.DisposeAsync(); Check(c.LogsDisposed && c.StatsDisposed);
    }

    public static async Task LifecycleBroadcastAndReentrantCancellation()
    {
        var c = new Connection(); await using var s = Session(c); await Ready(s); var id = Open(s); await Attached(s);
        var check = Source<EngineResourceResult<ContainerInspection>>(); c.Inspect = (_, _) => check.Task;
        await c.Emit("unpause"); await Until(() => !s.ContainerTerminal.LifecycleCurrent);
        using var cancelRead = new CancellationTokenSource();
        var canceledRead = s.ReadContainerTerminalOutputAsync(id, cancelRead.Token);
        cancelRead.Cancel();
        try { await canceledRead; throw new InvalidOperationException("Read cancellation must be observable"); }
        catch (OperationCanceledException) { }
        var output = s.ReadContainerTerminalOutputAsync(id);
        await c.Stream.OutputWriter.WriteAsync(new byte[] { 1, 2, 3 }.ToImmutableArray());
        check.SetResult(EngineResourceResult<ContainerInspection>.Success(Inspection(A, State())));
        Check((await output.WaitAsync(Limit)).Bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "All lifecycle waiters must wake on authoritative eligibility");
        await s.CloseContainerTerminalAsync();
        var other = new Connection(); var session = Session(other); await Ready(session);
        CancellationTokenRegistration callback = default;
        long replacement = 0;
        other.Start = (_, token) =>
        {
            callback = token.Register(() => replacement = Open(session));
            return Task.FromResult(new ContainerExecStartResult(ContainerExecStartDisposition.Established, other.Stream));
        };
        Open(session); await Attached(session);
        other.Start = (_, _) => Task.FromResult(new ContainerExecStartResult(ContainerExecStartDisposition.Established, new Attachment()));
        await session.CloseContainerTerminalAsync().WaitAsync(Limit);
        await Until(() => replacement > 0 && session.ContainerTerminal.Phase == ContainerTerminalPhase.Attached);
        Check(other.Creates == 2 && session.ContainerTerminal.SessionId == replacement);
        callback.Dispose(); await session.DisposeAsync();
    }

    public static async Task DiagnosticsCallerCancellationAndNotificationJoin()
    {
        var c = new Connection();
        c.Start = (_, _) => Task.FromResult(new ContainerExecStartResult(ContainerExecStartDisposition.DaemonRejected,
            Response: new(500, System.Text.Encoding.UTF8.GetBytes(Secret).ToImmutableArray(), false, "command not found: " + Secret),
            Failure: new(ContainerExecFailureKind.DaemonFailure, "command not found: " + Secret)));
        await using (var s = Session(c))
        {
            await Ready(s); var id = Open(s); await Until(() => s.ContainerTerminal.Phase == ContainerTerminalPhase.Rejected);
            var evidence = s.ReadContainerTerminalEvidence(id)!;
            Check(evidence.Start!.Failure!.Description.Contains("command not found") && c.ExecInspections == 0
                && s.ContainerTerminal.Start == ContainerExecStartDisposition.DaemonRejected
                && !s.ContainerTerminal.ToString().Contains(Secret) && !evidence.Start.ToString().Contains(Secret));
        }
        var writing = new Connection(); var delayed = Source<ContainerExecWriteResult>(); writing.Stream.Write = (_, _) => delayed.Task;
        var session = Session(writing); await Ready(session); var sid = Open(session); await Attached(session);
        using var cancel = new CancellationTokenSource(); var input = session.WriteContainerTerminalAsync(sid, new byte[] { 3 }, cancel.Token);
        await Until(() => writing.Stream.Inputs.Count == 1); cancel.Cancel();
        Check(!session.IsContainerTerminalCurrent(sid, A, session.ContainerTerminal.ConnectionGeneration));
        var close = session.CloseContainerTerminalAsync(); Check(!close.IsCompleted);
        delayed.SetResult(new(ContainerExecWriteDisposition.Uncertain)); await input; await close.WaitAsync(Limit); await session.DisposeAsync();

        var observing = new Connection(); var observedSession = Session(observing); await Ready(observedSession);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        observedSession.ContainerTerminalChanged += (_, _) => { entered.Set(); release.Wait(Limit); };
        Open(observedSession); await Attached(observedSession); await Until(() => entered.IsSet);
        var closing = observedSession.CloseContainerTerminalAsync();
        try { Check(!closing.IsCompleted, "Close must join an in-progress observer callback"); }
        finally { release.Set(); }
        await closing.WaitAsync(Limit); await observedSession.DisposeAsync();
    }

    public static async Task BlockedOutputFailureAndAdmissionClose()
    {
        foreach (bool failure in new[] { false, true })
        {
            var c = new Connection(); await using var s = Session(c, new() { OutputChunks = 1, OutputChunkBytes = 1 });
            await Ready(s); var id = Open(s); await Attached(s);
            await c.Stream.OutputWriter.WriteAsync(new byte[] { 1, 2, 3, 4 }.ToImmutableArray());
            c.Stream.End(new(failure ? ContainerExecAttachmentTermination.Failed : ContainerExecAttachmentTermination.Canceled,
                failure ? new(ContainerExecFailureKind.DaemonFailure, Secret) : null));
            await Until(() => c.Stream.Disposed);
            Check((await s.ReadContainerTerminalOutputAsync(id)).Revoked, "Canceled/failed completion revokes a backpressured producer without waiting for a consumer");
        }
        var pending = new Connection(); var inspect = Source<EngineResourceResult<ContainerInspection>>();
        pending.Inspect = (_, _) => inspect.Task;
        var session = Session(pending); session.Start(); await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready);
        session.SelectContainer(A); var sid = Open(session);
        await session.CloseContainerTerminalAsync().WaitAsync(Limit);
        Check(pending.Creates == 0 && pending.Starts == 0 && (await session.ReadContainerTerminalOutputAsync(sid)).Revoked);
        inspect.SetResult(EngineResourceResult<ContainerInspection>.Success(Inspection(A, State()))); await session.DisposeAsync();
    }

    private static ContainerRuntimeState State(bool? running = true, bool? paused = false) => new("running", running, paused, false, false, null, null, null, null, null);
    private static ContainerInspection Inspection(string id, ContainerRuntimeState state) => new(id, null, null, null, null, state, null, null, null, "{}");
    private static ContainerExecInspection Process(bool? running = null, long? exit = null) => new(new(A, E), E, A, running, exit, null, null, null, null, null, null, null);
    private sealed class Client(params Connection[] connections) : IEngineConnectionClient
    {
        private int calls;
        public Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineConnectionAttempt.Connected(connections[Math.Min(Interlocked.Increment(ref calls) - 1, connections.Length - 1)]));
    }
    private sealed class Connection : IEngineConnection, IContainerReader, IContainerExec, IEngineEventSource, IContainerCommands, IContainerLogSource, IContainerStatsSource
    {
        public bool Disposed, LogsOpened, StatsOpened, LogsDisposed, StatsDisposed;
        public int Creates, Starts, Resizes, ExecInspections, Commands;
        public ContainerRuntimeState Runtime = State();
        public ContainerExecCreateRequest? Created;
        public ContainerExecInstance? Started;
        public List<(int, int)> Dimensions = [];
        public Attachment Stream = new();
        private EventStream? events;
        public Func<string, CancellationToken, Task<EngineResourceResult<ContainerInspection>>>? Inspect;
        public Func<ContainerExecCreateRequest, CancellationToken, Task<ContainerExecCreationResult>>? Create;
        public Func<ContainerExecInstance, CancellationToken, Task<ContainerExecStartResult>>? Start;
        public Func<ContainerExecInstance, int, int, CancellationToken, Task<ContainerExecResizeResult>>? Resize;
        public Func<ContainerExecInstance, CancellationToken, Task<ContainerExecInspectionResult>>? ExecInspect;
        public EngineMetadata Metadata => new(EngineEndpoint.LocalWindows, "fixture", new(new(1, 40), new(1, 56), new(1, 40), new(1, 56), new(1, 56)),
            "linux", "x64", null, "same", null, null, "linux", null, null, new(null, null, null, null, null, null, null, null, null, null));
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default) => Task.FromResult<EngineConnectionFailure?>(null);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(new[] { A, B }.Select(id => new ContainerSummary(id, null, null, null, "running", null, null, null, null, null, null)).ToArray()));
        public Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string containerId, CancellationToken cancellationToken = default)
            => Inspect?.Invoke(containerId, cancellationToken) ?? Task.FromResult(EngineResourceResult<ContainerInspection>.Success(Inspection(containerId, Runtime)));
        public Task<ContainerExecCreationResult> CreateTtyExecAsync(ContainerExecCreateRequest request, CancellationToken cancellationToken = default)
        { Created = request; Interlocked.Increment(ref Creates); return Create?.Invoke(request, cancellationToken) ?? Task.FromResult(new ContainerExecCreationResult(ContainerExecCreationDisposition.Acknowledged, new(A, E))); }
        public Task<ContainerExecStartResult> StartTtyExecAsync(ContainerExecInstance instance, CancellationToken cancellationToken = default)
        { Started = instance; Interlocked.Increment(ref Starts); return Start?.Invoke(instance, cancellationToken) ?? Task.FromResult(new ContainerExecStartResult(ContainerExecStartDisposition.Established, Stream)); }
        public Task<ContainerExecResizeResult> ResizeTtyExecAsync(ContainerExecInstance instance, int columns, int rows, CancellationToken cancellationToken = default)
        { Dimensions.Add((columns, rows)); Interlocked.Increment(ref Resizes); return Resize?.Invoke(instance, columns, rows, cancellationToken) ?? Task.FromResult(new ContainerExecResizeResult(ContainerExecResizeDisposition.Acknowledged, instance, columns, rows)); }
        public Task<ContainerExecInspectionResult> InspectExecAsync(ContainerExecInstance instance, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref ExecInspections); return ExecInspect?.Invoke(instance, cancellationToken) ?? Task.FromResult(new ContainerExecInspectionResult(Value: Process())); }
        public Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
        { events = new(cancellationToken); return Task.FromResult(EngineEventSubscriptionAttempt.Subscribed(events)); }
        public Task Emit(string action) => events!.Channel.Writer.WriteAsync(new("container", action, A, null, null, new(1, 1_000_000_001))).AsTask();
        public Task<ContainerCommandResult> StartContainerAsync(string containerId, CancellationToken cancellationToken = default) { ++Commands; throw new InvalidOperationException(); }
        public Task<ContainerCommandResult> StopContainerAsync(string containerId, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default) { ++Commands; throw new InvalidOperationException(); }
        public Task<ContainerCommandResult> RestartContainerAsync(string containerId, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default) { ++Commands; throw new InvalidOperationException(); }
        public Task<ContainerCommandResult> DeleteContainerAsync(string containerId, bool force = false, CancellationToken cancellationToken = default) { ++Commands; throw new InvalidOperationException(); }
        public Task<ContainerLogSubscriptionAttempt> OpenLogsAsync(ContainerLogRequest request, CancellationToken cancellationToken = default)
        { LogsOpened = true; return Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(new Logs(this))); }
        public Task<ContainerStatsSubscriptionAttempt> SubscribeStatsAsync(string containerId, CancellationToken cancellationToken = default)
        { StatsOpened = true; return Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(new Stats(this))); }
    }
    private sealed class Attachment : IContainerExecAttachment
    {
        private readonly Channel<ImmutableArray<byte>> output = Channel.CreateBounded<ImmutableArray<byte>>(1);
        private readonly TaskCompletionSource<ContainerExecAttachmentCompletion> completion = Source<ContainerExecAttachmentCompletion>();
        public bool Disposed;
        public TaskCompletionSource<bool>? DisposeRelease;
        public List<byte[]> Inputs = [];
        public Func<ReadOnlyMemory<byte>, CancellationToken, Task<ContainerExecWriteResult>>? Write;
        public ContainerExecInstance Instance => new(A, E);
        public ChannelReader<ImmutableArray<byte>> Output => output.Reader;
        public ChannelWriter<ImmutableArray<byte>> OutputWriter => output.Writer;
        public Task<ContainerExecAttachmentCompletion> Completion => completion.Task;
        public Task<ContainerExecWriteResult> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        { Inputs.Add(bytes.ToArray()); return Write?.Invoke(bytes, cancellationToken) ?? Task.FromResult(new ContainerExecWriteResult(ContainerExecWriteDisposition.Written)); }
        public void End(ContainerExecAttachmentCompletion? result = null) { output.Writer.TryComplete(); completion.TrySetResult(result ?? new(ContainerExecAttachmentTermination.EndOfStream)); }
        public async ValueTask DisposeAsync() { Disposed = true; End(new(ContainerExecAttachmentTermination.Canceled)); if (DisposeRelease is { } release) await release.Task; }
    }
    private sealed class EventStream : IEngineEventSubscription
    {
        public Channel<EngineEvent> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<EngineEvent>(8);
        private readonly CancellationTokenRegistration registration;
        public EventStream(CancellationToken token) => registration = token.Register(() => Channel.Writer.TryComplete());
        public ChannelReader<EngineEvent> Events => Channel.Reader;
        public Task<EngineEventFailure?> Completion => Task.FromResult<EngineEventFailure?>(null);
        public ValueTask DisposeAsync() { registration.Dispose(); Channel.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Logs(Connection c) : IContainerLogSubscription
    {
        public string ContainerId => A;
        public bool IsTty => true;
        public ChannelReader<ContainerLogOutput> Output { get; } = Channel.CreateBounded<ContainerLogOutput>(1).Reader;
        public Task<ContainerLogCompletion> Completion => Task.FromResult(new ContainerLogCompletion(ContainerLogTermination.Canceled));
        public ValueTask DisposeAsync() { c.LogsDisposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Stats(Connection c) : IContainerStatsSubscription
    {
        public string ContainerId => A;
        public ChannelReader<ContainerStatsSample> Samples { get; } = Channel.CreateBounded<ContainerStatsSample>(1).Reader;
        public Task<ContainerStatsCompletion> Completion => Task.FromResult(new ContainerStatsCompletion(ContainerStatsTermination.Canceled));
        public ValueTask DisposeAsync() { c.StatsDisposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class ManualTime : TimeProvider
    {
        private readonly object gate = new();
        private long ticks;
        private readonly HashSet<Timer> timers = [];
        public int ActiveTimers { get { lock (gate) return timers.Count(t => t.Due != long.MaxValue); } }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate) { var timer = new Timer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        }
        public void Advance(TimeSpan duration)
        {
            List<Timer> due;
            lock (gate)
            {
                ticks += duration.Ticks;
                due = timers.Where(timer => timer.Due <= ticks).ToList();
                foreach (var timer in due) timer.Due = timer.Period > TimeSpan.Zero ? ticks + timer.Period.Ticks : long.MaxValue;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        public void ElapseWithoutTimers(TimeSpan duration) { lock (gate) ticks += duration.Ticks; }
        private sealed class Timer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public long Due = long.MaxValue;
            public TimeSpan Period;
            public TimerCallback Callback => callback;
            public object? State => state;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (owner.gate) { Due = dueTime < TimeSpan.Zero ? long.MaxValue : owner.ticks + dueTime.Ticks; Period = period; return true; } }
            public void Dispose() { lock (owner.gate) { Due = long.MaxValue; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
