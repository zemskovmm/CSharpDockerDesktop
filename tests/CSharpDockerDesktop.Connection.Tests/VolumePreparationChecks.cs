using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static partial class VolumeBrowsingChecks
{
    private static DesktopSession PreparationSession(IEngineConnectionClient client, VolumeEventClock clock, TimeSpan? timeout = null)
        => EventSession(client, clock, new() { OperationTimeout = timeout ?? TimeSpan.FromSeconds(10) });
    private static VolumeDeletionTarget Target(DesktopSession session, string? name = null)
        => new(session.ContainerSnapshot.ConnectionGeneration, name ?? A);
    private static VolumeDependencies Evidence(string? name = null)
        => new(name ?? A, Array.Empty<VolumeContainerDependency>(), Array.Empty<string>());
    private static async Task ReadyPreparation(DesktopSession session, DependencyPreparationConnection engine)
    {
        session.Start(); await Load(session, engine, A, B);
        await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready && session.ImageSnapshot.State == ResourceReadState.Ready);
        await engine.Events.Barrier();
        Check(engine.DependencyCalls == 0 && session.VolumeDeletionPreparation is null, "Browsing does not acquire dependencies");
    }
    private static async Task PreparationState(DesktopSession session, Func<VolumeDeletionPreparationSnapshot, bool> predicate)
        => await Until(() => session.VolumeDeletionPreparation is { } state && predicate(state));

    public static async Task PreparationEvidence()
    {
        const string exact = "  Data/Δ\\:%?#😀  ";
        var engine = new DependencyPreparationConnection("evidence"); var clock = new VolumeEventClock();
        await using var session = PreparationSession(new ControlledClient(engine), clock);
        await ReadyPreparation(session, engine);
        session.VolumeDeletionPreparationChanged += (_, _) => throw new InvalidOperationException("private observer");
        var names = new[] { "/running", "/alias" };
        var source = new[] { new VolumeContainerDependency(new('a', 64), names, "/running", "running", true),
            new VolumeContainerDependency(new('b', 64), null, null, "exited", false),
            new VolumeContainerDependency(new('c', 64), Array.Empty<string>(), null, null, null) };
        var evidence = new VolumeDependencies(exact, source, Array.Empty<string>());
        names[0] = "mutated"; source[0] = new("wrong", null, null, null, null);
        var pending = session.PrepareVolumeDeletionAsync(Target(session, exact));
        var loading = session.VolumeDeletionPreparation!;
        var call = await engine.NextDependency(); Check(call.Name == exact && loading.Phase == VolumePreparationPhase.Loading);
        call.Succeed(evidence); var completed = await pending.WaitAsync(Limit);
        Check(completed.HasUsableEvidence && completed.Dependencies!.Containers![0].Id == new string('a', 64)
            && completed.Dependencies.Containers[0].Names![0] == "/running" && completed.Dependencies.Containers[0].Running == true
            && completed.Dependencies.Containers[1].Running == false && completed.Dependencies.Containers[1].Names is null
            && completed.Dependencies.Containers[2].Running is null && completed.Dependencies.Containers[2].Names!.Count == 0
            && session.IsVolumeDeletionPreparationCurrent(completed.Identity)
            && !session.IsVolumeDeletionPreparationCurrent(loading.Identity));
        foreach (var item in new[] { Evidence(), new VolumeDependencies(A, Array.Empty<VolumeContainerDependency>(), new[] { "full-unknown" }),
            new VolumeDependencies(A, null, Array.Empty<string>()) })
        {
            var task = session.PrepareVolumeDeletionAsync(Target(session)); (await engine.NextDependency()).Succeed(item);
            var state = await task.WaitAsync(Limit);
            Check(state.Phase == VolumePreparationPhase.Completed && state.Dependencies!.Completeness == item.Completeness
                && state.Dependencies.UnverifiedContainerIds.SequenceEqual(item.UnverifiedContainerIds)
                && state.HasUsableEvidence == (item.Completeness != VolumeDependencyCompleteness.Unavailable));
        }
        Check(completed.Phase == VolumePreparationPhase.Completed && completed.Dependencies!.VolumeName == exact,
            "Captured immutable snapshots are validated through their identity, never mutated in place");
    }

    public static async Task PreparationReplacement()
    {
        var engine = new DependencyPreparationConnection("replace", false); var clock = new VolumeEventClock();
        await using var session = PreparationSession(new ControlledClient(engine), clock);
        await ReadyPreparation(session, engine);
        var first = session.PrepareVolumeDeletionAsync(Target(session)); var active = await engine.NextDependency();
        var captured = session.VolumeDeletionPreparation!.Identity;
        var second = session.PrepareVolumeDeletionAsync(Target(session, B));
        var replacement = session.VolumeDeletionPreparation!;
        Check(active.Token.IsCancellationRequested && !second.IsCompleted && !first.IsCompleted && engine.DependencyCalls == 1);
        for (int i = 0; i < 100; i++)
        {
            var rejected = await session.PrepareVolumeDeletionAsync(Target(session, C));
            Check(rejected.Failure?.Kind == VolumePreparationFailureKind.CapacityExceeded
                && ReferenceEquals(replacement, session.VolumeDeletionPreparation));
        }
        await session.CloseVolumeDeletionPreparationAsync(captured); // Stale capture cannot cancel replacement.
        Check(!second.IsCompleted && session.VolumeDeletionPreparation!.Target.Name == B);
        active.Succeed(Evidence()); Check((await first.WaitAsync(Limit)).Failure?.Kind == VolumePreparationFailureKind.Superseded);
        var next = await engine.NextDependency(); Check(next.Name == B && engine.MaximumDependencies == 1);
        next.Succeed(Evidence(B)); var complete = await second.WaitAsync(Limit);
        Check(complete.HasUsableEvidence && !session.IsVolumeDeletionPreparationCurrent(captured));
        await session.CloseVolumeDeletionPreparationAsync(complete.Identity);
        Check(session.VolumeDeletionPreparation!.Failure?.Kind == VolumePreparationFailureKind.Closed
            && !session.IsVolumeDeletionPreparationCurrent(complete.Identity));
        using var caller = new CancellationTokenSource();
        var canceled = session.PrepareVolumeDeletionAsync(Target(session), caller.Token); var late = await engine.NextDependency();
        caller.Cancel(); await PreparationState(session, s => s.Failure?.Kind == VolumePreparationFailureKind.Canceled);
        Check(!canceled.IsCompleted && late.Token.IsCancellationRequested);
        late.Succeed(Evidence()); Check((await canceled.WaitAsync(Limit)).Phase == VolumePreparationPhase.Invalidated);
        // A single waiting replacement can be canceled without ever dispatching.
        var blocker = session.PrepareVolumeDeletionAsync(Target(session)); var blocked = await engine.NextDependency();
        using var waitingCaller = new CancellationTokenSource();
        var queued = session.PrepareVolumeDeletionAsync(Target(session, B), waitingCaller.Token);
        waitingCaller.Cancel(); Check(session.VolumeDeletionPreparation!.Failure?.Kind == VolumePreparationFailureKind.Canceled);
        int calls = engine.DependencyCalls; blocked.Succeed(Evidence()); await blocker.WaitAsync(Limit);
        Check((await queued.WaitAsync(Limit)).Failure?.Kind == VolumePreparationFailureKind.Canceled && engine.DependencyCalls == calls);
        // Closing an active request also joins ignored cancellation.
        var closingTask = session.PrepareVolumeDeletionAsync(Target(session)); var closingRead = await engine.NextDependency();
        var close = session.CloseVolumeDeletionPreparationAsync(session.VolumeDeletionPreparation!.Identity);
        Check(!close.IsCompleted && closingRead.Token.IsCancellationRequested);
        closingRead.Succeed(Evidence()); await close.WaitAsync(Limit); await closingTask;
    }

    public static async Task PreparationEvents()
    {
        var engine = new DependencyPreparationConnection("events"); var clock = new VolumeEventClock();
        await using var session = PreparationSession(new ControlledClient(engine), clock);
        await ReadyPreparation(session, engine);
        // Before acquisition: an observed change does not poison a fresh explicit request.
        await engine.Events.Emit(VolumeEvent(A)); await engine.Events.Barrier();
        var before = session.PrepareVolumeDeletionAsync(Target(session)); (await engine.NextDependency()).Succeed(Evidence());
        var captured = await before.WaitAsync(Limit); Check(captured.HasUsableEvidence);
        await engine.Events.Emit(VolumeEvent(B)); await engine.Events.Barrier();
        Check(session.IsVolumeDeletionPreparationCurrent(captured.Identity), "Exact unrelated volume does not invalidate");
        foreach (var observation in new[] { VolumeEvent(A), VolumeEvent(null), VolumeEvent("bad\nidentity"),
            new EngineEvent("container", "create", "new-unlisted-container", null, null, new(200, null)) })
        {
            var task = session.PrepareVolumeDeletionAsync(Target(session)); var call = await engine.NextDependency();
            int notifications = 0; void Count(object? _, EventArgs __) => Interlocked.Increment(ref notifications);
            session.VolumeDeletionPreparationChanged += Count;
            for (int i = 0; i < 100; i++) await engine.Events.Emit(observation);
            await engine.Events.Barrier(); session.VolumeDeletionPreparationChanged -= Count;
            Check(session.VolumeDeletionPreparation!.Failure?.Kind == VolumePreparationFailureKind.ObservedChange
                && !call.Token.IsCancellationRequested && notifications == 1,
                "Acquisition remains bounded under events without cancel/requery loops");
            call.Succeed(Evidence()); Check((await task.WaitAsync(Limit)).Phase == VolumePreparationPhase.Invalidated);
        }
        var after = session.PrepareVolumeDeletionAsync(Target(session)); (await engine.NextDependency()).Succeed(Evidence());
        captured = await after.WaitAsync(Limit);
        await engine.Events.Emit(new("container", "stop", "unrelated-but-possibly-new-mount", null, null, new(201, null)));
        await engine.Events.Barrier();
        Check(!session.IsVolumeDeletionPreparationCurrent(captured.Identity) && session.VolumeDeletionPreparation!.Dependencies is not null
            && engine.DependencyCalls == 6);
        // A broken observation stream is a gap even without connection loss; no automatic dependency reread.
        var gapTask = session.PrepareVolumeDeletionAsync(Target(session)); (await engine.NextDependency()).Succeed(Evidence());
        captured = await gapTask.WaitAsync(Limit);
        engine.Events.End(new(EngineEventFailureKind.MalformedEvent, "observation gap"));
        await PreparationState(session, s => s.Phase == VolumePreparationPhase.Invalidated);
        Check(!session.IsVolumeDeletionPreparationCurrent(captured.Identity));
    }

    public static async Task PreparationDisappearance()
    {
        var engine = new DependencyPreparationConnection("absence"); var clock = new VolumeEventClock();
        await using var session = PreparationSession(new ControlledClient(engine), clock);
        await ReadyPreparation(session, engine);
        var pending = session.PrepareVolumeDeletionAsync(Target(session)); (await engine.NextDependency()).Succeed(Evidence());
        var captured = await pending.WaitAsync(Limit);
        session.SelectVolume(B); (await engine.NextInspection()).Succeed(Detail(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(session.IsVolumeDeletionPreparationCurrent(captured.Identity) && session.VolumeDeletionPreparation!.Target.Name == A);
        foreach (var listing in new[] { new VolumeListing(null, null), new VolumeListing(Array.Empty<VolumeSummary>(), new[] { "private warning" }) })
        {
            session.RefreshVolumes(); (await engine.NextList()).Succeed(listing);
            await State(session, s => s.State != ResourceReadState.Loading);
            Check(session.IsVolumeDeletionPreparationCurrent(captured.Identity), "Incomplete listings cannot establish absence");
        }
        session.RefreshVolumes(); (await engine.NextList()).SucceedNames(B);
        await PreparationState(session, s => s.Failure?.Kind == VolumePreparationFailureKind.TargetAbsent);
        Check(!session.IsVolumeDeletionPreparationCurrent(captured.Identity));
        session.RefreshVolumes(); await Load(session, engine, A, B);
        pending = session.PrepareVolumeDeletionAsync(Target(session)); var call = await engine.NextDependency();
        session.SelectVolume(A); (await engine.NextInspection()).Fail(Failure(EngineResourceFailureKind.NotFound));
        await PreparationState(session, s => s.Failure?.Kind == VolumePreparationFailureKind.TargetAbsent);
        call.Succeed(Evidence()); Check((await pending.WaitAsync(Limit)).Phase == VolumePreparationPhase.Invalidated);
    }

    public static async Task PreparationFailuresAndRecovery()
    {
        var old = new DependencyPreparationConnection("old"); var fresh = new DependencyPreparationConnection("fresh");
        var client = new ControlledClient(old, fresh); var clock = new VolumeEventClock();
        await using var session = PreparationSession(client, clock); await ReadyPreparation(session, old);
        foreach (var kind in new[] { EngineResourceFailureKind.AccessDenied, EngineResourceFailureKind.NotFound,
            EngineResourceFailureKind.Conflict, EngineResourceFailureKind.InvalidRequest, EngineResourceFailureKind.DaemonFailure,
            EngineResourceFailureKind.MalformedData })
        {
            var task = session.PrepareVolumeDeletionAsync(Target(session)); var failure = Failure(kind);
            (await old.NextDependency()).Fail(failure); var state = await task.WaitAsync(Limit);
            Check(state.Phase == VolumePreparationPhase.Failed && state.Failure?.Kind == VolumePreparationFailureKind.AcquisitionFailed
                && ReferenceEquals(state.Failure.ResourceFailure, failure) && state.Dependencies is null && client.Calls == 1);
        }
        var malformed = session.PrepareVolumeDeletionAsync(Target(session)); (await old.NextDependency()).Succeed(Evidence(B));
        Check((await malformed.WaitAsync(Limit)).Failure?.ResourceFailure?.Kind == EngineResourceFailureKind.MalformedData);
        var unexpected = session.PrepareVolumeDeletionAsync(Target(session));
        (await old.NextDependency()).Completion.TrySetException(new InvalidOperationException("private exception"));
        Check((await unexpected.WaitAsync(Limit)).Failure?.ResourceFailure?.Description.Contains("private") == false);
        var lost = session.PrepareVolumeDeletionAsync(Target(session)); var loss = Failure(EngineResourceFailureKind.TransportLoss);
        (await old.NextDependency()).Fail(loss); var failed = await lost.WaitAsync(Limit);
        await Until(() => session.EngineState == EngineConnectionState.Lost);
        Check(failed.Failure?.ResourceFailure == loss && session.Snapshot.Failure?.DaemonMessage == loss.DaemonMessage
            && old.DisposeCount == 1 && !old.DisposedWithActiveRead);
        (await clock.Next(TimeSpan.FromSeconds(3))).Release(); await Load(session, fresh, A);
        Check(client.Calls == 2 && fresh.DependencyCalls == 0 && session.VolumeDeletionPreparation!.Phase == VolumePreparationPhase.Failed);
        var taskFresh = session.PrepareVolumeDeletionAsync(Target(session)); (await fresh.NextDependency()).Succeed(Evidence());
        Check((await taskFresh.WaitAsync(Limit)).HasUsableEvidence);
    }

    public static async Task PreparationDeadlineAndTeardown()
    {
        foreach (bool waiting in new[] { false, true })
        {
            var engine = new DependencyPreparationConnection("deadline", false); var clock = new VolumeEventClock();
            var session = PreparationSession(new ControlledClient(engine), clock, TimeSpan.FromMilliseconds(250));
            await ReadyPreparation(session, engine);
            var task = session.PrepareVolumeDeletionAsync(Target(session)); var call = await engine.NextDependency();
            Task<VolumeDeletionPreparationSnapshot>? replacement = waiting ? session.PrepareVolumeDeletionAsync(Target(session, B)) : null;
            await PreparationState(session, s => s.Failure?.Kind == VolumePreparationFailureKind.Deadline);
            Check(engine.DependencyCalls == 1 && call.Token.IsCancellationRequested && engine.DisposeCount == 0);
            var close = session.DisconnectAsync(); Check(!close.IsCompleted);
            call.Succeed(Evidence()); await task.WaitAsync(Limit);
            if (replacement is not null) Check((await replacement.WaitAsync(Limit)).Failure?.Kind == VolumePreparationFailureKind.Deadline);
            await close.WaitAsync(Limit); Check(engine.DisposeCount == 1 && !engine.DisposedWithActiveRead);
            await session.DisposeAsync();
        }
        foreach (string mode in new[] { "disconnect", "dispose", "retry", "liveness", "containers", "images", "events", "logs", "container-operation", "image-operation" })
        {
            var engine = new DependencyPreparationConnection("closing", false); var fresh = new DependencyPreparationConnection("fresh");
            var clock = new VolumeEventClock(); var client = new ControlledClient(engine, fresh);
            var session = PreparationSession(client, clock); await ReadyPreparation(session, engine);
            long generation = session.ContainerSnapshot.ConnectionGeneration;
            var task = session.PrepareVolumeDeletionAsync(Target(session)); var call = await engine.NextDependency();
            Task? closing = null;
            Task? operation = null;
            if (mode == "logs")
            {
                Check(session.OpenContainerLogs(AllCapabilitiesConnection.ContainerId));
                await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
            }
            if (mode == "container-operation")
            {
                operation = session.ExecuteContainerOperationAsync(new(new(generation, AllCapabilitiesConnection.ContainerId), ContainerCommand.Start));
                await engine.CommandStarted.Task.WaitAsync(Limit);
            }
            if (mode == "image-operation")
            {
                operation = session.ExecuteImageOperationAsync(new(new(generation, AllCapabilitiesConnection.ImageId)));
                await engine.ImageCommandStarted.Task.WaitAsync(Limit);
            }
            if (mode == "disconnect") closing = session.DisconnectAsync();
            else if (mode == "dispose") closing = session.DisposeAsync().AsTask();
            else if (mode == "retry") session.Retry();
            else if (mode == "containers") { engine.ContainerFailure = Failure(EngineResourceFailureKind.TransportLoss); session.RefreshContainers(); }
            else if (mode == "images") { engine.ImageFailure = Failure(EngineResourceFailureKind.TransportLoss); session.RefreshImages(); }
            else if (mode == "events") engine.Events.Fail();
            else if (mode == "logs") engine.Logs.Fail();
            else if (mode == "container-operation") engine.CommandCompletion.TrySetResult(new(ContainerCommand.Start, ContainerCommandDisposition.Uncertain,
                Failure: new(ContainerCommandFailureKind.TransportFailure, "lost command")));
            else if (mode == "image-operation") engine.ImageCommandCompletion.TrySetResult(new(ImageCommandDisposition.Uncertain,
                Failure: new(ImageCommandFailureKind.TransportFailure, "lost command")));
            else { engine.PingFailure = new(EngineConnectionFailureKind.TransientTransportFailure, "lost readiness");
                (await clock.Next(TimeSpan.FromSeconds(10))).Release(); }
            await PreparationState(session, s => s.Phase == VolumePreparationPhase.Invalidated);
            Check(call.Token.IsCancellationRequested && engine.DisposeCount == 0 && client.Calls == 1
                && (closing is null || !closing.IsCompleted));
            call.Succeed(Evidence()); Check((await task.WaitAsync(Limit)).Phase == VolumePreparationPhase.Invalidated);
            if (closing is not null) await closing.WaitAsync(Limit);
            if (mode == "liveness") { await Until(() => session.EngineState == EngineConnectionState.Lost);
                (await clock.Next(TimeSpan.FromSeconds(3))).Release(); }
            if (mode is "retry" or "liveness")
            {
                await Load(session, fresh, A);
                Check(session.ContainerSnapshot.ConnectionGeneration > generation && fresh.DependencyCalls == 0);
                var rejected = await session.PrepareVolumeDeletionAsync(new(generation, A));
                Check(rejected.Failure?.Kind == VolumePreparationFailureKind.StaleConnection);
            }
            if (operation is not null) await operation.WaitAsync(Limit);
            await session.DisposeAsync(); Check(engine.DisposeCount == 1 && !engine.DisposedWithActiveRead);
        }
    }

    public static async Task PreparationCoexistence()
    {
        await using (var missing = PreparationSession(new ControlledClient(new MissingConnection()), new VolumeEventClock()))
        {
            missing.Start(); await Until(() => missing.ContainerSnapshot.ConnectionGeneration > 0);
            var rejected = await missing.PrepareVolumeDeletionAsync(Target(missing));
            Check(rejected.Failure?.Kind == VolumePreparationFailureKind.CapabilityUnavailable && rejected.Dependencies is null);
        }
        var engine = new DependencyPreparationConnection("coexist"); var clock = new VolumeEventClock();
        await using var session = PreparationSession(new ControlledClient(engine), clock); await ReadyPreparation(session, engine);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check((await session.PrepareVolumeDeletionAsync(Target(session), canceled.Token)).Failure?.Kind == VolumePreparationFailureKind.Canceled);
        foreach (var name in new[] { "", ".", "..", "bad\nname", "\ud800", new string('x', 4097) })
            Check((await session.PrepareVolumeDeletionAsync(Target(session, name))).Failure?.Kind == VolumePreparationFailureKind.InvalidRequest);
        Check(engine.DependencyCalls == 0);
        Check(session.OpenContainerLogs(AllCapabilitiesConnection.ContainerId));
        await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        var task = session.PrepareVolumeDeletionAsync(Target(session)); var call = await engine.NextDependency();
        Check(engine.CommandStarted.Task.IsCompleted == false && engine.ImageCommandStarted.Task.IsCompleted == false);
        var lifecycle = session.ExecuteContainerOperationAsync(new(new(session.ContainerSnapshot.ConnectionGeneration,
            AllCapabilitiesConnection.ContainerId), ContainerCommand.Start));
        var imageCommand = session.ExecuteImageOperationAsync(new(new(session.ImageSnapshot.ConnectionGeneration, AllCapabilitiesConnection.ImageId)));
        await Task.WhenAll(engine.CommandStarted.Task, engine.ImageCommandStarted.Task).WaitAsync(Limit);
        session.RefreshContainers(); session.RefreshImages(); session.RefreshVolumes(); await Load(session, engine, A, B);
        session.SelectVolume(B); (await engine.NextInspection()).Succeed(Detail(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(!engine.EventToken.IsCancellationRequested && !engine.LogToken.IsCancellationRequested
            && session.ContainerLogs.Phase == ContainerLogsPhase.Streaming && engine.EventCalls == 1);
        call.Succeed(Evidence()); Check((await task.WaitAsync(Limit)).HasUsableEvidence);
        var close = session.CloseVolumeDeletionPreparationAsync(session.VolumeDeletionPreparation!.Identity);
        await close.WaitAsync(Limit);
        Check(engine.MaximumDependencies == 1 && !engine.CommandToken.IsCancellationRequested && !engine.ImageCommandToken.IsCancellationRequested
            && !engine.EventToken.IsCancellationRequested && !engine.LogToken.IsCancellationRequested);
        engine.CommandCompletion.TrySetResult(new(ContainerCommand.Start, ContainerCommandDisposition.NotDispatched,
            Failure: new(ContainerCommandFailureKind.InvalidRequest, "controlled finish")));
        engine.ImageCommandCompletion.TrySetResult(new(ImageCommandDisposition.NotDispatched,
            Failure: new(ImageCommandFailureKind.InvalidRequest, "controlled finish")));
        await Task.WhenAll(lifecycle, imageCommand).WaitAsync(Limit);
    }

    private sealed class DependencyPreparationConnection(string version, bool honorDependencyCancellation = true)
        : AllCapabilitiesConnection(version), IVolumeDependencyReader
    {
        private readonly Channel<DependencyCall> calls = Channel.CreateUnbounded<DependencyCall>();
        private int activeDependencies;
        public int DependencyCalls, MaximumDependencies;
        public async Task<EngineResourceResult<VolumeDependencies>> QueryVolumeDependenciesAsync(string name, CancellationToken cancellationToken = default)
        {
            int active = Interlocked.Increment(ref activeDependencies); MaximumDependencies = Math.Max(MaximumDependencies, active);
            Interlocked.Increment(ref DependencyCalls); var call = new DependencyCall(name, cancellationToken); calls.Writer.TryWrite(call);
            try { return await (honorDependencyCancellation ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task); }
            finally { Interlocked.Decrement(ref activeDependencies); }
        }
        public async Task<DependencyCall> NextDependency() => await calls.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public override ValueTask DisposeAsync()
        { DisposedWithActiveRead |= Volatile.Read(ref activeDependencies) != 0; return base.DisposeAsync(); }
    }
    private sealed class DependencyCall(string name, CancellationToken token)
    {
        public string Name { get; } = name;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<EngineResourceResult<VolumeDependencies>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(VolumeDependencies evidence) => Completion.TrySetResult(EngineResourceResult<VolumeDependencies>.Success(evidence));
        public void Fail(EngineResourceFailure failure) => Completion.TrySetResult(EngineResourceResult<VolumeDependencies>.Failed(failure));
    }
}
