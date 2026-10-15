using System.Collections.Immutable;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;
using CSharpDockerDesktop.Presentation;

internal static partial class EndpointSelectionChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly EngineEndpoint A = new("npipe://./pipe/engine_a", EngineTransport.WindowsNamedPipe);
    private static readonly EngineEndpoint B = new("npipe://./pipe/engine_b", EngineTransport.WindowsNamedPipe);
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static TaskCompletionSource<T> Source<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string reason = "Endpoint isolation assertion failed")
    { if (!value) throw new InvalidOperationException(reason); }
    private static async Task Observe(DesktopSession s, Func<bool> predicate)
    {
        var signal = Source<bool>();
        void Changed(object? _, EventArgs __) { if (predicate()) signal.TrySetResult(true); }
        s.Changed += Changed; s.ContainersChanged += Changed; s.ImagesChanged += Changed;
        s.VolumesChanged += Changed; s.NetworksChanged += Changed;
        s.ContainerOperationChanged += Changed; s.ContainerTerminalChanged += Changed;
        s.ContainerLogsChanged += Changed; s.ContainerStatsChanged += Changed;
        try { Changed(null, EventArgs.Empty); await signal.Task.WaitAsync(Limit); }
        finally
        {
            s.Changed -= Changed; s.ContainersChanged -= Changed; s.ImagesChanged -= Changed;
            s.VolumesChanged -= Changed; s.NetworksChanged -= Changed;
            s.ContainerOperationChanged -= Changed; s.ContainerTerminalChanged -= Changed;
            s.ContainerLogsChanged -= Changed; s.ContainerStatsChanged -= Changed;
        }
    }
    private static Task Ready(DesktopSession s, EngineEndpoint endpoint) => Observe(s, () => s.Snapshot.Endpoint == endpoint
        && s.Snapshot.State == EngineConnectionState.Connected && s.ContainerSnapshot.State == ResourceReadState.Ready
        && s.ImageSnapshot.State == ResourceReadState.Ready && s.VolumeSnapshot.State == ResourceReadState.Ready
        && s.NetworkSnapshot.State == ResourceReadState.Ready);
    private static DesktopSession Session(IEngineConnectionClient client, Clock? clock = null) => new(client,
        new() { LivenessInterval = TimeSpan.FromHours(1) }, clock ?? new Clock(), endpoint: A,
        operationOptions: new() { MaximumObservations = 1 });

    public static async Task CoalescedNegotiationAndShutdown()
    {
        var first = Source<CancellationToken>(); var release = Source<EngineConnectionAttempt>();
        var old = new Connection(A, "old"); var latest = new Connection(B, "latest");
        var client = new Client(async (endpoint, call, token) =>
        {
            if (call == 1) { Check(endpoint == A); first.SetResult(token); return await release.Task; }
            Check(endpoint == B && old.Disposals == 1, "Old acquisition must be joined before the latest handshake");
            return EngineConnectionAttempt.Connected(latest);
        });
        await using var s = Session(client); s.Start();
        var token = await first.Task.WaitAsync(Limit);
        for (int i = 0; i < 5000; ++i) s.SelectEndpoint(new($"npipe://./pipe/intermediate_{i}", EngineTransport.WindowsNamedPipe));
        s.SelectEndpoint(B);
        Check(token.IsCancellationRequested && client.Calls == 1 && s.Snapshot.Endpoint == B
            && s.Snapshot.State == EngineConnectionState.Connecting && s.Snapshot.Metadata is null);
        release.SetResult(EngineConnectionAttempt.Connected(old)); await Ready(s, B);
        Check(client.Calls == 2 && client.MaximumConcurrent == 1 && s.Snapshot.Metadata == latest.Metadata);
        s.SelectEndpoint(B); Check(client.Calls == 2, "Reselecting an endpoint is a no-op");
        await s.DisposeAsync(); Check(old.Disposals == 1 && latest.Disposals == 1);

        var acquired = Source<CancellationToken>(); var late = Source<EngineConnectionAttempt>();
        var connection = new Connection(A, "shutdown");
        var shuttingClient = new Client(async (_, _, t) => { acquired.SetResult(t); return await late.Task; });
        var shutting = Session(shuttingClient); shutting.Start(); var activeToken = await acquired.Task.WaitAsync(Limit);
        await Task.WhenAll(Task.Run(() => shutting.SelectEndpoint(B)), Task.Run(() => shutting.SelectEndpoint(A)));
        var disconnect = shutting.DisconnectAsync(); var disposal = shutting.DisposeAsync().AsTask();
        shutting.SelectEndpoint(B); shutting.Retry();
        Check(activeToken.IsCancellationRequested && !disconnect.IsCompleted && !disposal.IsCompleted);
        late.SetResult(EngineConnectionAttempt.Connected(connection));
        await Task.WhenAll(disconnect, disposal, shutting.DisposeAsync().AsTask()).WaitAsync(Limit);
        Check(connection.Disposals == 1 && shuttingClient.Calls == 1 && shutting.Snapshot.Metadata is null);
    }

    public static async Task RecoveryAndDisconnectFence()
    {
        var clock = new Clock(); var failed = new EngineConnectionFailure(EngineConnectionFailureKind.MissingEndpoint, "missing");
        var second = new Connection(B, "second"); var returned = new Connection(A, "fresh return");
        var client = new Client((endpoint, call, _) => Task.FromResult(call == 1 ? EngineConnectionAttempt.Failed(failed)
            : EngineConnectionAttempt.Connected(call == 2 ? second : returned)));
        await using var s = Session(client, clock); s.Start(); var recovery = await clock.Next();
        Check(s.Snapshot.Endpoint == A && s.Snapshot.AutomaticRecoveryPending);
        s.SelectEndpoint(B); Check(recovery.Token.IsCancellationRequested); recovery.Release(); await Ready(s, B);
        Check(client.Endpoints.SequenceEqual(new[] { A, B }));
        s.SelectEndpoint(A); await Ready(s, A);
        Check(second.Disposals == 1 && s.Snapshot.Metadata?.EngineVersion == "fresh return");
        await s.DisconnectAsync().WaitAsync(Limit);
        Check(returned.Disposals == 1 && s.Snapshot.Endpoint == A && s.Snapshot.State == EngineConnectionState.NotConnected);

        // Disconnect joins only work owned when called, even if a new selection arrives while cleanup is stalled.
        var first = new Connection(A, "draining") { DisposeRelease = Source<bool>() };
        var next = new Connection(B, "new");
        var routed = new Client((_, call, _) => Task.FromResult(EngineConnectionAttempt.Connected(call == 1 ? first : next)));
        await using var separate = Session(routed); separate.Start(); await Ready(separate, A);
        var closing = separate.DisconnectAsync(); await first.DisposeStarted.Task.WaitAsync(Limit);
        separate.SelectEndpoint(B); first.DisposeRelease.SetResult(true);
        await closing.WaitAsync(Limit); await Ready(separate, B);
        Check(next.Disposals == 0 && routed.Calls == 2);
    }

    public static async Task ResourceAndPreparationIsolation()
    {
        var old = new Connection(A, "old"); var next = new Connection(B, "new"); var returned = new Connection(A, "return");
        var client = new Client((_, call, _) => Task.FromResult(EngineConnectionAttempt.Connected(call switch { 1 => old, 2 => next, _ => returned })));
        await using var s = Session(client); s.Start(); await Ready(s, A);
        s.SelectContainer(Id); s.SelectImage(ImageId); s.SelectVolume("shared"); s.SelectNetwork(Id);
        await Observe(s, () => s.ContainerSnapshot.InspectionState == ResourceReadState.Ready
            && s.ImageSnapshot.InspectionState == ResourceReadState.Ready && s.VolumeSnapshot.InspectionState == ResourceReadState.Ready
            && s.NetworkSnapshot.InspectionState == ResourceReadState.Ready);
        long generation = s.ContainerSnapshot.ConnectionGeneration;
        var volume = await s.PrepareVolumeDeletionAsync(new(generation, "shared")).WaitAsync(Limit);
        var network = await s.PrepareNetworkDeletionAsync(new(generation, Id)).WaitAsync(Limit);
        Check(volume.HasUsableEvidence && network.IsPrecheckEligible);
        var late = Source<EngineResourceResult<IReadOnlyList<ContainerSummary>>>();
        var entered = Source<CancellationToken>(); old.List = token => { entered.SetResult(token); return late.Task; };
        s.RefreshContainers(); var token = await entered.Task.WaitAsync(Limit);
        var dependencies = Source<EngineResourceResult<VolumeDependencies>>(); var prepEntered = Source<CancellationToken>();
        old.Dependencies = token => { prepEntered.SetResult(token); return dependencies.Task; };
        var preparing = s.PrepareVolumeDeletionAsync(new(generation, "shared")); var prepToken = await prepEntered.Task.WaitAsync(Limit);
        s.SelectEndpoint(B);
        Check(s.Snapshot.Endpoint == B && s.Snapshot.Metadata is null && token.IsCancellationRequested && prepToken.IsCancellationRequested);
        Check(s.ContainerSnapshot.Containers.Count == 0 && s.ImageSnapshot.Images.Count == 0
            && s.VolumeSnapshot.LatestListing is null && s.NetworkSnapshot.Networks.Count == 0);
        Check(s.ContainerSnapshot.Inspection is null && s.ImageSnapshot.Inspection is null
            && s.VolumeSnapshot.Inspection is null && s.NetworkSnapshot.Inspection is null);
        Check(!s.IsVolumeDeletionPreparationCurrent(volume.Identity) && !s.IsNetworkDeletionPreparationCurrent(network.Identity)
            && !preparing.IsCompleted && client.Calls == 1 && old.Disposals == 0);
        old.Events.Items.Writer.TryWrite(new("container", "destroy", Id, null, null, new(10, null)));
        late.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success([Row("stale")]));
        dependencies.SetResult(EngineResourceResult<VolumeDependencies>.Success(new("shared", [], [])));
        Check(!(await preparing.WaitAsync(Limit)).HasUsableEvidence); await Ready(s, B);
        Check(s.ContainerSnapshot.Containers.Single().Names![0] == "new" && s.ImageSnapshot.Images.Single().RepoTags![0] == "new"
            && s.VolumeSnapshot.LatestListing!.Volumes!.Single().Driver == "new" && s.NetworkSnapshot.Networks.Single().Name == "new");
        var stale = await s.ExecuteContainerOperationAsync(new(new(generation, Id), ContainerCommand.Start));
        Check(stale.CommandOutcome?.Failure?.Kind == ContainerCommandFailureKind.StaleConnection);
        var staleVolume = await s.ExecuteVolumeOperationAsync(new(volume.Target, volume.Identity));
        var staleNetwork = await s.ExecuteNetworkOperationAsync(new(network.Target, network.Identity));
        Check(staleVolume.Rejection == VolumeOperationRejection.StaleConnection && staleNetwork.Rejection == NetworkOperationRejection.StaleConnection);
        s.SelectEndpoint(A); await Ready(s, A);
        Check(s.ContainerSnapshot.ConnectionGeneration > generation && s.ContainerSnapshot.SelectedContainerId is null
            && s.ContainerSnapshot.Containers.Single().Names![0] == "return" && client.Calls == 3 && old.Disposals == 1);
    }

    public static async Task MutationEvidenceIsolation()
    {
        foreach (var disposition in new[] { ContainerCommandDisposition.Acknowledged, ContainerCommandDisposition.DaemonResponse, ContainerCommandDisposition.Uncertain })
        {
            var old = new Connection(A, "old"); var next = new Connection(B, "new");
            var outcome = Source<ContainerCommandResult>(); var entered = Source<CancellationToken>();
            old.Command = t => { entered.SetResult(t); return outcome.Task; };
            var client = new Client((_, call, _) => Task.FromResult(EngineConnectionAttempt.Connected(call == 1 ? old : next)));
            await using var s = Session(client); s.Start(); await Ready(s, A);
            var request = new ContainerOperationRequest(new(s.ContainerSnapshot.ConnectionGeneration, Id), ContainerCommand.Start);
            var operation = s.ExecuteContainerOperationAsync(request); var token = await entered.Task.WaitAsync(Limit);
            s.SelectEndpoint(B); Check(token.IsCancellationRequested && s.ContainerOperation?.Resolution == ContainerOperationResolution.Unresolved);
            outcome.SetResult(new(ContainerCommand.Start, disposition, 409, "original private daemon content"));
            var history = await operation.WaitAsync(Limit); await Ready(s, B);
            Check(history.Endpoint == A && history.EngineId == "same-engine-id" && history.Request == request
                && history.CommandOutcome?.Disposition == disposition && history.CommandOutcome.DaemonMessage == "original private daemon content"
                && history.Resolution == ContainerOperationResolution.Unresolved && history.ObservedConnectionGeneration is null);
            Check(next.Commands == 0 && next.ContainerInspections == 0 && old.Commands == 1,
                "Overlapping IDs and Engine IDs must not reconcile or replay across endpoint boundaries");
            using var view = new ContainerBrowserView(s, action => { action(); return true; }); view.Attach();
            Check(view.OperationMessage.Contains(A.Address) && view.OperationMessage.Contains("Historical result")
                && view.OperationMessage.Contains("same-engine-id") && !view.OperationMessage.Contains("original private daemon content"));
            Check((await s.ExecuteContainerOperationAsync(request)).CommandOutcome?.Failure?.Kind == ContainerCommandFailureKind.StaleConnection);
        }
    }

    public static async Task ResourceMutationEvidenceIsolation()
    {
        foreach (var kind in new[] { ImageCommandDisposition.Acknowledged, ImageCommandDisposition.DaemonResponse, ImageCommandDisposition.Uncertain })
        {
            var old = new Connection(A, "old"); var next = new Connection(B, "new");
            var imageResult = Source<ImageCommandResult>(); var volumeResult = Source<VolumeCommandResult>();
            var networkResult = Source<NetworkCommandResult>();
            old.ImageCommand = _ => imageResult.Task; old.VolumeCommand = _ => volumeResult.Task; old.NetworkCommand = _ => networkResult.Task;
            var client = new Client((_, call, _) => Task.FromResult(EngineConnectionAttempt.Connected(call == 1 ? old : next)));
            await using var s = Session(client); s.Start(); await Ready(s, A);
            long generation = s.ContainerSnapshot.ConnectionGeneration;
            var volume = await s.PrepareVolumeDeletionAsync(new(generation, "shared")).WaitAsync(Limit);
            var network = await s.PrepareNetworkDeletionAsync(new(generation, Id)).WaitAsync(Limit);
            var imageOperation = s.ExecuteImageOperationAsync(new(new(generation, ImageId)));
            var volumeOperation = s.ExecuteVolumeOperationAsync(new(volume.Target, volume.Identity));
            var networkOperation = s.ExecuteNetworkOperationAsync(new(network.Target, network.Identity));
            await Task.WhenAll(old.ImageEntered.Task, old.VolumeEntered.Task, old.NetworkEntered.Task).WaitAsync(Limit);
            s.SelectEndpoint(B);
            imageResult.SetResult(new(kind, 409, "image original", [new(Deleted: ImageId)]));
            volumeResult.SetResult(new((VolumeCommandDisposition)(int)kind, 409, "volume original", "volume body"));
            networkResult.SetResult(new((NetworkCommandDisposition)(int)kind, 409, "network original", "network body"));
            var imageHistory = await imageOperation.WaitAsync(Limit); var volumeHistory = await volumeOperation.WaitAsync(Limit);
            var networkHistory = await networkOperation.WaitAsync(Limit); await Ready(s, B);
            Check(imageHistory.Endpoint == A && volumeHistory.Endpoint == A && networkHistory.Endpoint == A
                && imageHistory.EngineId == "same-engine-id" && volumeHistory.EngineId == "same-engine-id" && networkHistory.EngineId == "same-engine-id");
            Check(imageHistory.CommandOutcome?.Disposition == kind && imageHistory.CommandOutcome.DaemonMessage == "image original"
                && imageHistory.CommandOutcome.RemovalRecords!.Single().Deleted == ImageId && imageHistory.Resolution == ImageOperationResolution.Unresolved);
            Check(volumeHistory.CommandOutcome?.DaemonErrorBody == "volume body" && volumeHistory.Resolution == VolumeOperationResolution.Unresolved
                && networkHistory.CommandOutcome?.DaemonErrorBody == "network body" && networkHistory.Resolution == NetworkOperationResolution.Unresolved);
            using var images = new ImageBrowserView(s, action => { action(); return true; }); images.Attach();
            using var volumes = new VolumeBrowserView(s, action => { action(); return true; }); volumes.Attach();
            using var networks = new NetworkBrowserView(s, action => { action(); return true; }); networks.Attach();
            foreach (string message in new[] { images.OperationMessage, volumes.OperationMessage, networks.OperationMessage })
                Check(message.Contains(A.Address) && message.Contains("same-engine-id") && message.Contains("Historical result"));
            Check(imageHistory.ObservedConnectionGeneration is null && volumeHistory.ObservedConnectionGeneration is null
                && networkHistory.ObservedConnectionGeneration is null && next.Commands == 0 && next.ResourceCommands == 0 && old.ResourceCommands == 3);
        }
    }

    public static async Task LiveOwnershipAndCleanup()
    {
        var old = new Connection(A, "old"); var next = new Connection(B, "new");
        var client = new Client((_, call, _) => Task.FromResult(EngineConnectionAttempt.Connected(call == 1 ? old : next)));
        await using var s = Session(client); s.Start(); await Ready(s, A); s.SelectContainer(Id);
        await Observe(s, () => s.ContainerSnapshot.InspectionState == ResourceReadState.Ready);
        Check(s.OpenContainerLogs(Id) && s.OpenContainerStats(Id));
        var terminal = s.OpenContainerTerminal(Id, ["explicit-command"], 80, 24); Check(terminal.Accepted);
        await old.StatsEntered.Task.WaitAsync(Limit);
        await Observe(s, () => s.ContainerTerminal.Phase == ContainerTerminalPhase.Attached
            && s.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        old.Attachment.DisposeRelease = Source<bool>();
        s.SelectEndpoint(B); await old.Attachment.DisposeStarted.Task.WaitAsync(Limit);
        Check(!s.IsContainerTerminalCurrent(terminal.SessionId, Id, s.ContainerTerminal.ConnectionGeneration)
            && s.ContainerLogs.Phase == ContainerLogsPhase.Closed && s.ContainerStats.Phase == ContainerStatsPhase.Closed
            && client.Calls == 1 && old.Disposals == 0);
        old.Logs.Items.Writer.TryWrite(new(1, ContainerLogStream.Combined, "old secret", 1, ContainerLogBoundary.CombinedLine, true, false, null));
        old.Stats.Items.Writer.TryWrite(new(Id, "linux", null, null, null, null, null, null, null, null, null));
        old.Attachment.Items.Writer.TryWrite(ImmutableArray.Create((byte)42));
        old.Attachment.DisposeRelease.SetResult(true); await Ready(s, B);
        Check(s.ReadContainerLogs().Entries.Count == 0 && s.ReadContainerStats().Entries.Count == 0
            && (await s.ReadContainerTerminalOutputAsync(terminal.SessionId)).Revoked);
        Check(old.Logs.Disposals == 1 && old.Stats.Disposals == 1 && old.Attachment.Disposals == 1 && old.Events.Disposals == 1
            && old.Disposals == 1 && next.Creates == 0 && next.LogOpens == 0 && next.StatsOpens == 0);
    }

    public static async Task InvalidSelections()
    {
        foreach (var address in new[] { "unix:///var/run/docker.sock", "tcp://localhost:2375", "ssh://host", "npipe://remote/pipe/docker_engine", "npipe://./pipe/../invalid" })
        {
            var transport = new RejectTransport(); await using var s = new DesktopSession(new DockerEngineConnectionClient(transport));
            var endpoint = new EngineEndpoint(address, EngineTransport.WindowsNamedPipe); s.SelectEndpoint(endpoint);
            Check(s.Snapshot.Endpoint == endpoint);
            await Observe(s, () => s.Snapshot.State == EngineConnectionState.Unsupported);
            Check(s.Snapshot.Endpoint == endpoint && s.Snapshot.Failure?.Kind == EngineConnectionFailureKind.UnsupportedEndpoint
                && !s.Snapshot.AutomaticRecoveryPending && transport.Creates == 0);
            s.Retry(); await Observe(s, () => s.Snapshot.State == EngineConnectionState.Unsupported);
            Check(s.Snapshot.Endpoint == endpoint && transport.Creates == 0);
        }
    }

    private static ContainerSummary Row(string name) => new(Id, [name], null, null, "running", null, null, null, null, null, null);
    private sealed class Client(Func<EngineEndpoint, int, CancellationToken, Task<EngineConnectionAttempt>> connect) : IEngineConnectionClient
    {
        public int Calls, MaximumConcurrent; private int active;
        public List<EngineEndpoint> Endpoints { get; } = [];
        public async Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
        {
            MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref active));
            var call = Interlocked.Increment(ref Calls); Endpoints.Add(endpoint!);
            try { return await connect(endpoint!, call, cancellationToken); }
            finally { Interlocked.Decrement(ref active); }
        }
    }
    private sealed class Connection(EngineEndpoint endpoint, string label) : IEngineConnection, IContainerReader, IImageReader,
        IVolumeReader, INetworkReader, IVolumeDependencyReader, IContainerCommands, IImageCommands, IVolumeCommands, INetworkCommands, IContainerLogSource, IContainerStatsSource, IContainerExec, IEngineEventSource
    {
        public int Disposals, Commands, ContainerInspections, Creates, LogOpens, StatsOpens;
        public int ResourceCommands;
        public Func<CancellationToken, Task<ImageCommandResult>>? ImageCommand;
        public Func<CancellationToken, Task<VolumeCommandResult>>? VolumeCommand;
        public Func<CancellationToken, Task<NetworkCommandResult>>? NetworkCommand;
        public TaskCompletionSource<bool> ImageEntered { get; } = Source<bool>();
        public TaskCompletionSource<bool> VolumeEntered { get; } = Source<bool>();
        public TaskCompletionSource<bool> NetworkEntered { get; } = Source<bool>();
        public TaskCompletionSource<bool>? DisposeRelease;
        public TaskCompletionSource<bool> DisposeStarted { get; } = Source<bool>();
        public Func<CancellationToken, Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>>>? List;
        public Func<CancellationToken, Task<EngineResourceResult<VolumeDependencies>>>? Dependencies;
        public Func<CancellationToken, Task<ContainerCommandResult>>? Command;
        public EventStream Events { get; } = new(); public Logs Logs { get; } = new(); public Stats Stats { get; } = new();
        public Attachment Attachment { get; } = new();
        public TaskCompletionSource<bool> StatsEntered { get; } = Source<bool>();
        public EngineMetadata Metadata { get; } = new(endpoint, label, new(new(1, 40), new(1, 56), new(1, 40), new(1, 56), new(1, 56)),
            "linux", null, null, "same-engine-id", label, null, "linux", null, null, new(null, null, null, null, null, null, null, null, null, null));
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default) => Task.FromResult<EngineConnectionFailure?>(null);
        public async ValueTask DisposeAsync() { ++Disposals; DisposeStarted.TrySetResult(true); if (DisposeRelease is { } r) await r.Task; }
        public Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default)
            => List?.Invoke(cancellationToken) ?? Task.FromResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success([Row(label)]));
        public Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string identity, CancellationToken cancellationToken = default)
        { ++ContainerInspections; return Task.FromResult(EngineResourceResult<ContainerInspection>.Success(new(identity, label, null, null, null,
            new("running", true, false, false, false, null, null, null, null, null), null, null, null, "{}"))); }
        public Task<EngineResourceResult<IReadOnlyList<ImageSummary>>> ListImagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<IReadOnlyList<ImageSummary>>.Success([new(ImageId, [label], [], null, null)]));
        public Task<EngineResourceResult<ImageInspection>> InspectImageAsync(string reference, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<ImageInspection>.Success(new(reference, [label], [], null, null, null, null, null, null, null, null, "{}")));
        public Task<EngineResourceResult<VolumeListing>> ListVolumesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<VolumeListing>.Success(new([new("shared", label, "local", null, null)], [])));
        public Task<EngineResourceResult<VolumeInspection>> InspectVolumeAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<VolumeInspection>.Success(new(name, label, "local", null, null, "{}")));
        public Task<EngineResourceResult<IReadOnlyList<NetworkSummary>>> ListNetworksAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<IReadOnlyList<NetworkSummary>>.Success([new(Id, label, "bridge", "local", false, null)]));
        public Task<EngineResourceResult<NetworkInspection>> InspectNetworkAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<NetworkInspection>.Success(new(id, label, "bridge", "local", false, null, null, new Dictionary<string, NetworkAttachment>(), "{}")));
        public Task<EngineResourceResult<VolumeDependencies>> QueryVolumeDependenciesAsync(string name, CancellationToken cancellationToken = default)
            => Dependencies?.Invoke(cancellationToken) ?? Task.FromResult(EngineResourceResult<VolumeDependencies>.Success(new(name, [], [])));
        public Task<ImageCommandResult> DeleteImageAsync(string reference, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref ResourceCommands); ImageEntered.TrySetResult(true); return ImageCommand!(cancellationToken); }
        public Task<VolumeCommandResult> DeleteVolumeAsync(string name, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref ResourceCommands); VolumeEntered.TrySetResult(true); return VolumeCommand!(cancellationToken); }
        public Task<NetworkCommandResult> DeleteNetworkAsync(string id, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref ResourceCommands); NetworkEntered.TrySetResult(true); return NetworkCommand!(cancellationToken); }
        public Task<ContainerCommandResult> StartContainerAsync(string containerId, CancellationToken cancellationToken = default)
        { ++Commands; return Command!(cancellationToken); }
        public Task<ContainerCommandResult> StopContainerAsync(string containerId, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<ContainerCommandResult> RestartContainerAsync(string containerId, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<ContainerCommandResult> DeleteContainerAsync(string containerId, bool force = false, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineEventSubscriptionAttempt.Subscribed(Events));
        public Task<ContainerLogSubscriptionAttempt> OpenLogsAsync(ContainerLogRequest request, CancellationToken cancellationToken = default)
        { ++LogOpens; return Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(Logs)); }
        public Task<ContainerStatsSubscriptionAttempt> SubscribeStatsAsync(string containerId, CancellationToken cancellationToken = default)
        { ++StatsOpens; StatsEntered.TrySetResult(true); return Task.FromResult(ContainerStatsSubscriptionAttempt.Subscribed(Stats)); }
        public Task<ContainerExecCreationResult> CreateTtyExecAsync(ContainerExecCreateRequest request, CancellationToken cancellationToken = default)
        { ++Creates; return Task.FromResult(new ContainerExecCreationResult(ContainerExecCreationDisposition.Acknowledged, new(Id, Id))); }
        public Task<ContainerExecStartResult> StartTtyExecAsync(ContainerExecInstance instance, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContainerExecStartResult(ContainerExecStartDisposition.Established, Attachment));
        public Task<ContainerExecResizeResult> ResizeTtyExecAsync(ContainerExecInstance instance, int columns, int rows, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContainerExecResizeResult(ContainerExecResizeDisposition.Acknowledged, instance, columns, rows));
        public Task<ContainerExecInspectionResult> InspectExecAsync(ContainerExecInstance instance, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContainerExecInspectionResult(new(instance, Id, Id, null, null, null, null, null, null, null, null, null)));
    }
    private sealed class EventStream : IEngineEventSubscription
    {
        public int Disposals;
        public Channel<EngineEvent> Items { get; } = Channel.CreateBounded<EngineEvent>(8);
        public ChannelReader<EngineEvent> Events => Items.Reader;
        public Task<EngineEventFailure?> Completion => Task.FromResult<EngineEventFailure?>(null);
        public ValueTask DisposeAsync() { ++Disposals; Items.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Logs : IContainerLogSubscription
    {
        public int Disposals; public string ContainerId => Id; public bool IsTty => true;
        public Channel<ContainerLogOutput> Items { get; } = Channel.CreateBounded<ContainerLogOutput>(8);
        public ChannelReader<ContainerLogOutput> Output => Items.Reader;
        public Task<ContainerLogCompletion> Completion => Task.FromResult(new ContainerLogCompletion(ContainerLogTermination.Canceled));
        public ValueTask DisposeAsync() { ++Disposals; Items.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Stats : IContainerStatsSubscription
    {
        public int Disposals; public string ContainerId => Id;
        public Channel<ContainerStatsSample> Items { get; } = Channel.CreateBounded<ContainerStatsSample>(8);
        public ChannelReader<ContainerStatsSample> Samples => Items.Reader;
        public Task<ContainerStatsCompletion> Completion => Task.FromResult(new ContainerStatsCompletion(ContainerStatsTermination.Canceled));
        public ValueTask DisposeAsync() { ++Disposals; Items.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Attachment : IContainerExecAttachment
    {
        public int Disposals; public ContainerExecInstance Instance => new(Id, Id);
        public TaskCompletionSource<bool> DisposeStarted { get; } = Source<bool>();
        public TaskCompletionSource<bool>? DisposeRelease;
        public Channel<ImmutableArray<byte>> Items { get; } = Channel.CreateBounded<ImmutableArray<byte>>(8);
        public ChannelReader<ImmutableArray<byte>> Output => Items.Reader;
        private readonly TaskCompletionSource<ContainerExecAttachmentCompletion> completion = Source<ContainerExecAttachmentCompletion>();
        public Task<ContainerExecAttachmentCompletion> Completion => completion.Task;
        public Task<ContainerExecWriteResult> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContainerExecWriteResult(ContainerExecWriteDisposition.Written));
        public async ValueTask DisposeAsync() { ++Disposals; DisposeStarted.TrySetResult(true); if (DisposeRelease is { } r) await r.Task; Items.Writer.TryComplete(); completion.TrySetResult(new(ContainerExecAttachmentTermination.Canceled)); }
    }
    private sealed class Clock : IConnectionDelay
    {
        private readonly Channel<Wait> waits = Channel.CreateBounded<Wait>(32);
        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        { var w = new Wait(cancellationToken); await waits.Writer.WriteAsync(w, cancellationToken); await w.Done.Task.WaitAsync(cancellationToken); }
        public Task<Wait> Next() => waits.Reader.ReadAsync().AsTask().WaitAsync(Limit);
    }
    private sealed class Wait(CancellationToken token)
    {
        public CancellationToken Token => token; public TaskCompletionSource<bool> Done { get; } = Source<bool>();
        public void Release() => Done.TrySetResult(true);
    }
    private sealed class RejectTransport : IDockerTransportFactory
    {
        public int Creates;
        public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options)
        { ++Creates; throw new InvalidOperationException("Invalid endpoints must never allocate a transport"); }
    }
}
