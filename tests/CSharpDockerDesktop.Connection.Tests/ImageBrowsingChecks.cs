using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static class ImageBrowsingChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);
    private static readonly string A = "sha256:" + new string('a', 64), B = "sha256:" + new string('b', 64), C = "sha256:" + new string('c', 64);
    private const string Raw = "{\n  \"Id\": \"full-id\", \"Config\": {\"Env\": [\"SECRET=private\"]}, \"Unknown\": true\n}";
    private static ImageSummary Row(string id) => new(id, Array.AsReadOnly(new[] { "private/image:mutable" }),
        Array.AsReadOnly(new[] { "private/image@" + id }), DateTimeOffset.UnixEpoch, 123);
    private static ImageInspection Detail(string id, string raw = Raw) => new(id, Row(id).RepoTags, Row(id).RepoDigests, DateTimeOffset.UnixEpoch, 123,
        "linux", "private-os-version", "arm64", "v8", new("private-user", Array.AsReadOnly(new[] { "SECRET=private" }),
            null, null, "/private/path", null, null, null, null, null, null, false, "{\"Env\":[\"SECRET=private\"]}"),
        new("layers", Array.AsReadOnly(new[] { "sha256:private-layer" })), raw);
    private static EngineResourceFailure Failure(EngineResourceFailureKind kind) => new(kind,
        "classified resource failure", 503, "original private daemon message\nunchanged");
    private static void Check(bool condition, string message = "Container browsing assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task State(DesktopSession session, Func<ImageBrowsingSnapshot, bool> predicate)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? sender, EventArgs args) { if (predicate(session.ImageSnapshot)) reached.TrySetResult(); }
        session.ImagesChanged += Observe;
        try { Observe(null, EventArgs.Empty); await reached.Task.WaitAsync(Limit); }
        finally { session.ImagesChanged -= Observe; }
    }
    private static async Task ConnectionState(DesktopSession session, Func<ConnectionSnapshot, bool> predicate)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? sender, EventArgs args) { if (predicate(session.Snapshot)) reached.TrySetResult(); }
        session.Changed += Observe;
        try { Observe(null, EventArgs.Empty); await reached.Task.WaitAsync(Limit); }
        finally { session.Changed -= Observe; }
    }
    private static async Task Load(DesktopSession session, ControlledConnection connection, params string[] ids)
    {
        (await connection.NextList()).Succeed(ids);
        await State(session, s => s.State == ResourceReadState.Ready);
    }

    public static async Task InitialEmptyAndReplacement()
    {
        var connection = new ControlledConnection("initial");
        await using var session = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay());
        IImageBrowser browser = session;
        Check(browser.ImageSnapshot.State == ResourceReadState.Unavailable);
        session.Start();
        var initial = await connection.NextList();
        Check(browser.ImageSnapshot.State == ResourceReadState.Loading && browser.ImageSnapshot.Images.Count == 0);
        initial.Succeed();
        await State(session, s => s.State == ResourceReadState.Ready);
        Check(browser.ImageSnapshot.Images.Count == 0 && session.EngineState == EngineConnectionState.Connected);
        browser.RefreshImages();
        await Load(session, connection, A, B);
        browser.SelectImage(A);
        var inspect = await connection.NextInspection();
        Check(inspect.Identity == A && browser.ImageSnapshot.InspectionState == ResourceReadState.Loading);
        inspect.Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(browser.ImageSnapshot.Inspection!.Id == A && browser.ImageSnapshot.Inspection.RawJson == Raw);
        browser.RefreshImages();
        await Load(session, connection, B, C, C);
        Check(browser.ImageSnapshot.Images.Select(row => row.Id).SequenceEqual(new[] { B, C }));
        Check(browser.ImageSnapshot.SelectedImageId is null && browser.ImageSnapshot.Inspection is null);
        browser.SelectImage(A); // A user selection from an older rendered snapshot is harmless.
        Check(browser.ImageSnapshot.SelectedImageId is null && connection.InspectionCalls == 1);
        browser.RefreshImages();
        await Load(session, connection, C);
        Check(browser.ImageSnapshot.Images.Single().Id == C && connection.MaximumConcurrent == 1);
    }

    public static async Task PreservedFailuresAndDisappearance()
    {
        foreach (var kind in Enum.GetValues<EngineResourceFailureKind>().Where(k => k != EngineResourceFailureKind.TransportLoss))
        {
            var connection = new ControlledConnection("errors");
            var client = new ControlledClient(connection);
            await using var session = new DesktopSession(client, delay: new ControlledDelay());
            session.Start();
            var failure = Failure(kind);
            (await connection.NextList()).Fail(failure);
            await State(session, s => s.State == ResourceReadState.Failed);
            Check(ReferenceEquals(session.ImageSnapshot.Failure, failure));
            Check(client.Calls == 1 && session.EngineState == EngineConnectionState.Connected);
            session.RefreshImages();
            await Load(session, connection, A, B);
            session.SelectImage(A);
            (await connection.NextInspection()).Succeed(Detail(A));
            await State(session, s => s.InspectionState == ResourceReadState.Ready);
            session.RefreshImageInspection();
            (await connection.NextInspection()).Fail(failure);
            await State(session, s => s.InspectionState == ResourceReadState.Failed);
            Check(ReferenceEquals(session.ImageSnapshot.InspectionFailure, failure) && session.ImageSnapshot.Inspection is null);
            Check(session.EngineState == EngineConnectionState.Connected && client.Calls == 1);
            if (kind == EngineResourceFailureKind.NotFound)
                Check(session.ImageSnapshot.SelectedImageId is null
                    && session.ImageSnapshot.Images.Select(row => row.Id).SequenceEqual(new[] { B }));
            else Check(session.ImageSnapshot.SelectedImageId == A && session.ImageSnapshot.Images.Count == 2);
        }
    }

    public static async Task SelectionRacesAndClearing()
    {
        var connection = new ControlledConnection("races", honorCancellation: false);
        await using var session = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay());
        var observed = new List<string>();
        session.ImagesChanged += (_, _) => { if (session.ImageSnapshot.Inspection is { } value) observed.Add(value.Id); };
        session.Start(); await Load(session, connection, A, B, C);
        session.SelectImage(A);
        var first = await connection.NextInspection();
        for (int index = 0; index < 200; index++) session.SelectImage(index % 2 == 0 ? B : C);
        Check(first.Token.IsCancellationRequested && connection.InspectionCalls == 1 && connection.MaximumConcurrent == 1);
        first.Succeed(Detail(A)); // Reader deliberately completes successfully after cancellation.
        var latest = await connection.NextInspection();
        Check(latest.Identity == C && !observed.Contains(A));
        session.SelectImage(null);
        Check(latest.Token.IsCancellationRequested && session.ImageSnapshot.Inspection is null);
        latest.Succeed(Detail(C));
        session.SelectImage(B);
        var retained = await connection.NextInspection();
        session.ClearImageInspection();
        Check(retained.Token.IsCancellationRequested && session.ImageSnapshot.SelectedImageId == B
            && session.ImageSnapshot.InspectionState == ResourceReadState.NotRequested);
        session.RefreshImages(); // Refresh must not reopen an explicitly cleared inspection.
        retained.Fail(Failure(EngineResourceFailureKind.NotFound)); // A superseded error cannot remove the latest row.
        await Load(session, connection, A, B, C);
        Check(session.ImageSnapshot.InspectionState == ResourceReadState.NotRequested && connection.InspectionCalls == 3);
        session.RefreshImageInspection();
        var reload = await connection.NextInspection();
        Check(reload.Identity == B && session.ImageSnapshot.Images.Any(row => row.Id == B));
        reload.Succeed(Detail(B));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(observed.All(id => id == B) && session.ImageSnapshot.Inspection!.RawJson == Raw);

        session.RefreshImageInspection();
        var vanishing = await connection.NextInspection();
        session.RefreshImages();
        Check(vanishing.Token.IsCancellationRequested);
        vanishing.Succeed(Detail(B));
        await Load(session, connection, A);
        Check(session.ImageSnapshot.SelectedImageId is null && session.ImageSnapshot.Inspection is null
            && connection.InspectionCalls == 5, "Fresh absence must cancel the pending detail reload");
    }

    public static async Task BoundedRefreshAndDeadline()
    {
        var connection = new ControlledConnection("bounded", honorCancellation: false);
        await using var session = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay());
        session.Start();
        var first = await connection.NextList();
        for (int index = 0; index < 1000; index++) session.RefreshImages();
        Check(first.Token.IsCancellationRequested && connection.ListCalls == 1);
        first.Succeed(A);
        var latest = await connection.NextList();
        Check(session.ImageSnapshot.Images.Count == 0 && connection.ListCalls == 2 && connection.MaximumConcurrent == 1);
        latest.Succeed(B);
        await State(session, s => s.State == ResourceReadState.Ready);
        Check(session.ImageSnapshot.Images.Single().Id == B);

        var timed = new ControlledConnection("deadline");
        await using var timeout = new DesktopSession(new ControlledClient(timed), new()
            { OperationTimeout = TimeSpan.FromMilliseconds(100), MaximumRecoveryAttempts = 0 }, new ControlledDelay());
        timeout.Start();
        var pending = await timed.NextList();
        await ConnectionState(timeout, s => s.State == EngineConnectionState.Lost);
        Check(pending.Token.IsCancellationRequested && timed.DisposeCount == 1
            && timeout.ImageSnapshot.Failure?.Kind == EngineResourceFailureKind.TransportLoss);
    }

    public static async Task ReconnectInvalidationAndLateReads()
    {
        var old = new ControlledConnection("old", honorCancellation: false);
        var current = new ControlledConnection("new");
        var client = new ControlledClient(old, current);
        await using var session = new DesktopSession(client, delay: new ControlledDelay());
        session.Start(); await Load(session, old, A);
        long generation = session.ImageSnapshot.ConnectionGeneration;
        session.SelectImage(A);
        var late = await old.NextInspection();
        session.Retry();
        Check(late.Token.IsCancellationRequested && session.ImageSnapshot.State == ResourceReadState.Unavailable
            && session.ImageSnapshot.Images.Count == 0 && session.ImageSnapshot.SelectedImageId is null);
        Check(old.DisposeCount == 0 && client.Calls == 1, "Retained connection must outlive the canceled reader");
        late.Succeed(Detail(A));
        var initial = await current.NextList();
        Check(old.DisposeCount == 1 && !old.DisposedWithActiveRead && session.ImageSnapshot.ConnectionGeneration > generation
            && session.ImageSnapshot.State == ResourceReadState.Loading && session.ImageSnapshot.Inspection is null);
        initial.Succeed(B);
        await State(session, s => s.State == ResourceReadState.Ready);
        Check(session.ImageSnapshot.Images.Single().Id == B && session.Snapshot.Metadata?.EngineVersion == "new");
    }

    public static async Task ResourceDetectedLossAndRecovery()
    {
        foreach (bool inspect in new[] { false, true })
        {
            var old = new ControlledConnection("before-loss");
            var fresh = new ControlledConnection("renegotiated");
            var clock = new ControlledDelay();
            var client = new ControlledClient(old, fresh);
            await using var session = new DesktopSession(client, delay: clock);
            session.Start();
            var initial = await old.NextList();
            var liveness = await clock.Next();
            long generation = session.ImageSnapshot.ConnectionGeneration;
            var failure = Failure(EngineResourceFailureKind.TransportLoss);
            if (inspect)
            {
                initial.Succeed(A);
                await State(session, s => s.State == ResourceReadState.Ready);
                session.SelectImage(A);
                (await old.NextInspection()).Fail(failure);
            }
            else initial.Fail(failure);
            await ConnectionState(session, s => s.State == EngineConnectionState.Lost);
            Check(liveness.Token.IsCancellationRequested && old.DisposeCount == 1 && session.ImageSnapshot.Images.Count == 0);
            Check(ReferenceEquals(inspect ? session.ImageSnapshot.InspectionFailure : session.ImageSnapshot.Failure, failure));
            Check(session.Snapshot.Failure?.DaemonMessage == failure.DaemonMessage);
            var retry = await clock.Next();
            session.RefreshImages(); session.SelectImage(null); // No resource reads while disconnected.
            Check(client.Calls == 1 && old.ListCalls == 1);
            retry.Release();
            var snapshot = await fresh.NextList();
            Check(session.ImageSnapshot.ConnectionGeneration > generation && session.ImageSnapshot.State == ResourceReadState.Loading);
            snapshot.Succeed(B);
            await State(session, s => s.State == ResourceReadState.Ready);
            Check(client.Calls == 2 && session.Snapshot.Metadata?.EngineVersion == "renegotiated"
                && session.ImageSnapshot.Images.Single().Id == B && fresh.Metadata.Api.Negotiated == new EngineApiVersion(1, 40));
        }
    }

    public static async Task LivenessLossAndShutdown()
    {
        var clock = new ControlledDelay();
        var connection = new ControlledConnection("liveness", honorCancellation: false)
        { PingFailure = new(EngineConnectionFailureKind.TransientTransportFailure, "readiness lost") };
        var session = new DesktopSession(new ControlledClient(connection), new() { MaximumRecoveryAttempts = 0 }, clock);
        session.Start(); await Load(session, connection, A);
        session.SelectImage(A);
        var detail = await connection.NextInspection();
        (await clock.Next()).Release();
        await State(session, s => s.State == ResourceReadState.Unavailable);
        Check(detail.Token.IsCancellationRequested && session.ImageSnapshot.Inspection is null);
        detail.Succeed(Detail(A));
        await ConnectionState(session, s => s.State == EngineConnectionState.Lost);
        Check(connection.DisposeCount == 1 && !connection.DisposedWithActiveRead);
        await session.DisposeAsync();

        foreach (bool inspect in new[] { false, true })
        {
            var closing = new ControlledConnection("shutdown", honorCancellation: false);
            var client = new ControlledClient(closing);
            var shutdown = new DesktopSession(client, delay: new ControlledDelay());
            shutdown.Start();
            var list = await closing.NextList();
            InspectionCall? inspection = null;
            if (inspect)
            {
                list.Succeed(A); await State(shutdown, s => s.State == ResourceReadState.Ready);
                shutdown.SelectImage(A); inspection = await closing.NextInspection();
            }
            var completion = shutdown.DisposeAsync().AsTask();
            Check((inspection?.Token ?? list.Token).IsCancellationRequested && !completion.IsCompleted
                && shutdown.ImageSnapshot.State == ResourceReadState.Unavailable && shutdown.ImageSnapshot.Images.Count == 0);
            if (inspection is not null) inspection.Succeed(Detail(A)); else list.Succeed(A);
            await completion.WaitAsync(Limit);
            await shutdown.DisposeAsync();
            Check(closing.DisposeCount == 1 && !closing.DisposedWithActiveRead && shutdown.ImageSnapshot.Inspection is null);
            shutdown.Start(); shutdown.RefreshImages(); shutdown.SelectImage(A);
            Check(client.Calls == 1 && closing.ListCalls == 1);
        }
    }

    public static async Task MissingCapabilityAndFairness()
    {
        var missing = new NoImagesConnection();
        await using (var session = new DesktopSession(new ControlledClient(missing), delay: new ControlledDelay()))
        {
            session.Start(); await ConnectionState(session, value => value.State == EngineConnectionState.Connected);
            await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready
                && session.ImageSnapshot.ConnectionGeneration == session.ContainerSnapshot.ConnectionGeneration);
            Check(session.ImageSnapshot.State == ResourceReadState.Unavailable && session.ImageSnapshot.Images.Count == 0);
            session.RefreshImages(); session.SelectImage(A); session.RefreshImageInspection(); session.ClearImageInspection();
            Check(session.ImageSnapshot.InspectionState == ResourceReadState.Unavailable && missing.ListCalls == 1);
        }

        var connection = new ControlledConnection("fair", honorCancellation: false);
        await using var browser = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay());
        browser.Start(); await Load(browser, connection, A, B);
        var saved = browser.ImageSnapshot;
        Check(saved.Images is System.Collections.ObjectModel.ReadOnlyCollection<ImageSummary>);
        browser.RefreshImages(); var list = await connection.NextList();
        browser.SelectImage(A);
        for (int i = 0; i < 500; ++i) browser.RefreshImages();
        list.Succeed(B);
        var detail = await connection.NextInspection();
        Check(detail.Identity == A && browser.ImageSnapshot.Images.Count == 2,
            "Pending detail must get a turn under repeated list refreshes");
        browser.SelectImage(B);
        detail.Succeed(Detail(A));
        var fairList = await connection.NextList();
        for (int i = 0; i < 500; ++i) browser.SelectImage(i % 2 == 0 ? A : B);
        Check(!fairList.Token.IsCancellationRequested, "Selection bursts must not starve list I/O");
        fairList.Succeed(A, B);
        var latest = await connection.NextInspection();
        Check(latest.Identity == B && connection.MaximumConcurrent == 1);
        latest.Succeed(Detail(B));
        await State(browser, value => value.InspectionState == ResourceReadState.Ready);
        Check(saved.Images.Count == 2 && saved.Inspection is null);
        var result = browser.ImageSnapshot.Inspection!;
        Check(result.Configuration!.Environment!.Single() == "SECRET=private" && result.Architecture == "arm64"
            && result.RootFileSystem!.Layers!.Single() == "sha256:private-layer" && result.RawJson == Raw);
    }

    public static async Task NonCooperativeDeadlineAndSharedRecovery()
    {
        var old = new AllCapabilitiesConnection("old", honorCancellation: false);
        var fresh = new AllCapabilitiesConnection("new");
        var clock = new ControlledDelay();
        var client = new ControlledClient(old, fresh);
        await using var session = new DesktopSession(client, new() { OperationTimeout = TimeSpan.FromMilliseconds(150) }, clock);
        session.Start(); await Load(session, old, A);
        await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready);
        Check(session.OpenContainerLogs(AllCapabilitiesConnection.ContainerId));
        await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        var command = session.ExecuteContainerOperationAsync(new(new(session.ContainerSnapshot.ConnectionGeneration,
            AllCapabilitiesConnection.ContainerId), ContainerCommand.Start));
        await old.CommandStarted.Task.WaitAsync(Limit);
        var liveness = await clock.Next();
        session.SelectImage(A); var late = await old.NextInspection();
        await State(session, value => value.State == ResourceReadState.Unavailable);
        Check(late.Token.IsCancellationRequested && old.CommandToken.IsCancellationRequested
            && old.EventToken.IsCancellationRequested && old.LogToken.IsCancellationRequested
            && session.ContainerSnapshot.State == ResourceReadState.Unavailable
            && session.ImageSnapshot.InspectionFailure?.Kind == EngineResourceFailureKind.TransportLoss);
        Check(old.DisposeCount == 0 && client.Calls == 1, "Recovery must join outstanding borrowed work");
        late.Succeed(Detail(A));
        await ConnectionState(session, value => value.State == EngineConnectionState.Lost);
        await command.WaitAsync(Limit);
        Check(liveness.Token.IsCancellationRequested && old.DisposeCount == 1 && old.Events.Disposed
            && old.Logs.Disposed && !old.DisposedWithActiveRead);
        (await clock.Next()).Release();
        await Load(session, fresh, B);
        // Logs applies its independent recovery delay before borrowing the replacement.
        PendingDelay resume;
        do { resume = await clock.Next(); } while (resume.Duration != TimeSpan.FromSeconds(2));
        resume.Release();
        await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready
            && session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        Check(client.Calls == 2 && session.ImageSnapshot.SelectedImageId is null
            && session.ImageSnapshot.Inspection is null && fresh.Logs.Opened);
    }

    public static async Task SharedInvalidationAndJoinedShutdown()
    {
        foreach (var mode in new[] { "container-loss", "disconnect", "dispose" })
        {
            var connection = new AllCapabilitiesConnection(mode, honorCancellation: false);
            var clock = new ControlledDelay();
            var session = new DesktopSession(new ControlledClient(connection), new() { MaximumRecoveryAttempts = 0 }, clock);
            session.Start(); await Load(session, connection, A);
            await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready);
            Check(session.OpenContainerLogs(AllCapabilitiesConnection.ContainerId));
            await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
            var command = session.ExecuteContainerOperationAsync(new(new(session.ContainerSnapshot.ConnectionGeneration,
                AllCapabilitiesConnection.ContainerId), ContainerCommand.Start));
            await connection.CommandStarted.Task.WaitAsync(Limit);
            session.SelectImage(A); var detail = await connection.NextInspection();
            Task? completion = null;
            if (mode == "container-loss")
            {
                connection.ContainerFailure = Failure(EngineResourceFailureKind.TransportLoss);
                session.RefreshContainers();
                await State(session, value => value.State == ResourceReadState.Unavailable);
            }
            else completion = mode == "disconnect" ? session.DisconnectAsync() : session.DisposeAsync().AsTask();
            Check(detail.Token.IsCancellationRequested && connection.CommandToken.IsCancellationRequested
                && connection.EventToken.IsCancellationRequested && connection.LogToken.IsCancellationRequested
                && session.ImageSnapshot.Images.Count == 0 && session.ImageSnapshot.Inspection is null
                && session.ContainerSnapshot.State == ResourceReadState.Unavailable && connection.DisposeCount == 0);
            if (completion is not null) Check(!completion.IsCompleted);
            detail.Succeed(Detail(A));
            if (completion is null) await ConnectionState(session, value => value.State == EngineConnectionState.Lost);
            else await completion.WaitAsync(Limit);
            await command.WaitAsync(Limit);
            Check(connection.DisposeCount == 1 && !connection.DisposedWithActiveRead && connection.Events.Disposed
                && connection.Logs.Disposed && session.ImageSnapshot.Inspection is null);
            await session.DisposeAsync();
        }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Limit);
        while (!predicate()) await Task.Delay(2, deadline.Token);
    }

    private sealed class NoImagesConnection : IEngineConnection, IContainerReader
    {
        public int ListCalls;
        public EngineMetadata Metadata => new ControlledConnection("no-images").Metadata;
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<EngineConnectionFailure?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default)
        { ++ListCalls; return Task.FromResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(Array.Empty<ContainerSummary>())); }
        public Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string identity, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No selection is expected");
    }

    private sealed class AllCapabilitiesConnection(string version, bool honorCancellation = true)
        : ControlledConnection(version, honorCancellation), IContainerReader, IEngineEventSource, IContainerLogSource, IContainerCommands
    {
        public const string ContainerId = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        public readonly EventsSubscription Events = new();
        public readonly LogsSubscription Logs = new();
        public readonly TaskCompletionSource CommandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken EventToken, LogToken, CommandToken;
        public EngineResourceFailure? ContainerFailure;
        private int activeCommands;
        public override EngineMetadata Metadata => base.Metadata with { EngineId = "same-engine" };
        public override ValueTask DisposeAsync()
        {
            DisposedWithActiveRead |= Volatile.Read(ref activeCommands) != 0 || !Events.Disposed || Logs.Opened && !Logs.Disposed;
            return base.DisposeAsync();
        }
        public Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ContainerFailure is { } failure ? EngineResourceResult<IReadOnlyList<ContainerSummary>>.Failed(failure)
                : EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(new[] { new ContainerSummary(ContainerId,
                    null, null, A, "exited", null, null, null, null, null, null) }));
        public Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string identity, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<ContainerInspection>.Success(new(identity, null, null, A, null,
                new("exited", false, false, false, false, 0, null, null, null, null), null, null, null, "{}")));
        public Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
        { EventToken = cancellationToken; return Task.FromResult(EngineEventSubscriptionAttempt.Subscribed(Events)); }
        public Task<ContainerLogSubscriptionAttempt> OpenLogsAsync(ContainerLogRequest request, CancellationToken cancellationToken = default)
        { LogToken = cancellationToken; Logs.Opened = true; return Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(Logs)); }
        public async Task<ContainerCommandResult> StartContainerAsync(string identity, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref activeCommands); CommandToken = cancellationToken; CommandStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
            finally { Interlocked.Decrement(ref activeCommands); }
        }
        public Task<ContainerCommandResult> StopContainerAsync(string identity, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException();
        public Task<ContainerCommandResult> RestartContainerAsync(string identity, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException();
        public Task<ContainerCommandResult> DeleteContainerAsync(string identity, bool force = false, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException();
    }

    private sealed class EventsSubscription : IEngineEventSubscription
    {
        private readonly Channel<EngineEvent> channel = Channel.CreateBounded<EngineEvent>(1);
        private readonly TaskCompletionSource<EngineEventFailure?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public ChannelReader<EngineEvent> Events => channel.Reader;
        public Task<EngineEventFailure?> Completion => completion.Task;
        public ValueTask DisposeAsync()
        { Disposed = true; channel.Writer.TryComplete(); completion.TrySetResult(null); return ValueTask.CompletedTask; }
    }

    private sealed class LogsSubscription : IContainerLogSubscription
    {
        private readonly Channel<ContainerLogOutput> channel = Channel.CreateBounded<ContainerLogOutput>(1);
        private readonly TaskCompletionSource<ContainerLogCompletion> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed, Opened;
        public string ContainerId => AllCapabilitiesConnection.ContainerId;
        public bool IsTty => false;
        public ChannelReader<ContainerLogOutput> Output => channel.Reader;
        public Task<ContainerLogCompletion> Completion => completion.Task;
        public ValueTask DisposeAsync()
        { Disposed = true; channel.Writer.TryComplete(); completion.TrySetResult(new(ContainerLogTermination.Canceled)); return ValueTask.CompletedTask; }
    }

    private sealed class ControlledClient(params IEngineConnection[] connections) : IEngineConnectionClient
    {
        public int Calls;
        public Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(EngineConnectionAttempt.Connected(connections[Interlocked.Increment(ref Calls) - 1]));
        }
    }
    private class ControlledConnection(string version, bool honorCancellation = true) : IEngineConnection, IImageReader
    {
        private readonly Channel<ListCall> lists = Channel.CreateUnbounded<ListCall>();
        private readonly Channel<InspectionCall> inspections = Channel.CreateUnbounded<InspectionCall>();
        private int active;
        public int MaximumConcurrent, DisposeCount, ListCalls, InspectionCalls;
        public bool DisposedWithActiveRead;
        public EngineConnectionFailure? PingFailure;
        public virtual EngineMetadata Metadata { get; } = new(EngineEndpoint.LocalWindows, version,
            new(new(1, 40), new(1, 56), new(1, 40), new(1, 56), new(1, 40)),
            null, null, null, null, null, null, null, null, null, new(null, null, null, null, null, null, null, null, null, null));
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(PingFailure);
        public virtual ValueTask DisposeAsync()
        { DisposedWithActiveRead |= Volatile.Read(ref active) != 0; Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
        public async Task<EngineResourceResult<IReadOnlyList<ImageSummary>>> ListImagesAsync(CancellationToken cancellationToken = default)
        {
            Enter(); Interlocked.Increment(ref ListCalls);
            try
            {
                var call = new ListCall(cancellationToken); lists.Writer.TryWrite(call);
                return await (honorCancellation ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public async Task<EngineResourceResult<ImageInspection>> InspectImageAsync(string identity, CancellationToken cancellationToken = default)
        {
            Enter(); Interlocked.Increment(ref InspectionCalls);
            try
            {
                var call = new InspectionCall(identity, cancellationToken); inspections.Writer.TryWrite(call);
                return await (honorCancellation ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        private void Enter() { Check(DisposeCount == 0); MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref active)); }
        public async Task<ListCall> NextList() => await lists.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        public async Task<InspectionCall> NextInspection() => await inspections.Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class ListCall(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<EngineResourceResult<IReadOnlyList<ImageSummary>>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(params string[] ids) => Completion.SetResult(EngineResourceResult<IReadOnlyList<ImageSummary>>.Success(ids.Select(Row).ToArray()));
        public void Fail(EngineResourceFailure failure) => Completion.SetResult(EngineResourceResult<IReadOnlyList<ImageSummary>>.Failed(failure));
    }
    private sealed class InspectionCall(string identity, CancellationToken token)
    {
        public string Identity { get; } = identity;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<EngineResourceResult<ImageInspection>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(ImageInspection value) => Completion.SetResult(EngineResourceResult<ImageInspection>.Success(value));
        public void Fail(EngineResourceFailure failure) => Completion.SetResult(EngineResourceResult<ImageInspection>.Failed(failure));
    }
    private sealed class ControlledDelay : IConnectionDelay
    {
        private readonly Channel<PendingDelay> waits = Channel.CreateUnbounded<PendingDelay>();
        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var pending = new PendingDelay(delay, cancellationToken); waits.Writer.TryWrite(pending);
            await pending.Completion.Task.WaitAsync(cancellationToken);
        }
        public async Task<PendingDelay> Next() => await waits.Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class PendingDelay(TimeSpan duration, CancellationToken token)
    {
        public TimeSpan Duration { get; } = duration;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Completion.TrySetResult();
    }
}
