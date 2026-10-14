using System.Collections.ObjectModel;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static class VolumeBrowsingChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);
    private static readonly string A = "private-volume-" + new string('a', 128), B = "Private-volume-B", C = "private-volume-c";
    private const string Raw = "{\n  \"Name\": \"private\", \"Unknown\": 1.0000, \"Labels\": {\"SECRET\":\"private\"}\n}";
    private static VolumeSummary Row(string name) => new(name, "local", "local", "/private/mount",
        new Dictionary<string, string> { ["Secret"] = "private", ["secret"] = "different" });
    private static VolumeInspection Detail(string name) => new(name, "driver", null, "", Row(name).Labels, Raw);
    private static EngineResourceFailure Failure(EngineResourceFailureKind kind) => new(kind,
        "safe classified description", 503, "private original daemon message\nunchanged");
    private static void Check(bool condition, string message = "Volume browsing assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Limit);
        while (!predicate()) await Task.Delay(2, deadline.Token);
    }
    private static async Task State(DesktopSession session, Func<VolumeBrowsingSnapshot, bool> predicate)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? sender, EventArgs args) { if (predicate(session.VolumeSnapshot)) reached.TrySetResult(); }
        session.VolumesChanged += Observe;
        try { Observe(null, EventArgs.Empty); await reached.Task.WaitAsync(Limit); }
        finally { session.VolumesChanged -= Observe; }
    }
    private static async Task Load(DesktopSession session, ControlledConnection connection, params string[] names)
    {
        (await connection.NextList()).Succeed(new(names.Select(Row).ToArray(), null));
        await State(session, s => s.IsAuthoritative);
    }

    public static async Task InitialMetadataAndImmutableState()
    {
        var connection = new ControlledConnection("initial");
        var client = new ControlledClient(connection);
        await using var session = new DesktopSession(client, delay: new ControlledDelay());
        IVolumeBrowser browser = session;
        Check(browser.VolumeSnapshot.State == ResourceReadState.Unavailable);
        // An observer exception cannot orphan the owning workflow.
        browser.VolumesChanged += (_, _) => throw new InvalidOperationException("private observer content");
        session.Start();
        var first = await connection.NextList();
        Check(browser.VolumeSnapshot.State == ResourceReadState.Loading && browser.VolumeSnapshot.LatestListing is null);
        var labels = new Dictionary<string, string> { ["Secret"] = "private", ["secret"] = "different" };
        var source = new[] { Row(A) with { Labels = labels }, Row(B) with { Labels = null }, Row(C) with { Labels = new Dictionary<string, string>() } };
        first.Succeed(new(source, Array.Empty<string>()));
        await State(session, s => s.IsAuthoritative);
        var saved = browser.VolumeSnapshot;
        source[0] = Row("changed"); labels["Secret"] = "changed";
        Check(saved.Volumes[0].Name == A && saved.Volumes[0].Labels!["Secret"] == "private"
            && saved.Volumes[0].Labels!["secret"] == "different" && saved.Volumes[1].Labels is null
            && saved.Volumes[2].Labels!.Count == 0 && saved.Volumes is ReadOnlyCollection<VolumeSummary>
            && saved.Volumes[0].Labels is ReadOnlyDictionary<string, string> && saved.Warnings!.Count == 0);
        browser.SelectVolume(A[..12]); Check(browser.VolumeSnapshot.SelectedVolumeName is null);
        browser.SelectVolume(B.ToLowerInvariant()); Check(browser.VolumeSnapshot.SelectedVolumeName is null);
        browser.SelectVolume(A);
        var inspect = await connection.NextInspection(); Check(inspect.Name == A);
        var detailLabels = new Dictionary<string, string> { ["secret"] = "private" };
        inspect.Succeed(Detail(A) with { Labels = detailLabels });
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        detailLabels["secret"] = "changed";
        Check(browser.VolumeSnapshot.Inspection!.RawJson == Raw && browser.VolumeSnapshot.Inspection.Labels!["secret"] == "private"
            && browser.VolumeSnapshot.Inspection.Scope is null && browser.VolumeSnapshot.Inspection.Mountpoint == "");
        browser.RefreshVolumes(); await Load(session, connection, B, A, A);
        Check(browser.VolumeSnapshot.SelectedVolumeName == A && browser.VolumeSnapshot.Inspection!.RawJson == Raw
            && browser.VolumeSnapshot.Volumes.Select(row => row.Name).SequenceEqual(new[] { B, A }));
        Check(saved.Volumes.Count == 3 && saved.Inspection is null && client.Calls == 1 && connection.MaximumConcurrent == 1);
    }

    public static async Task PartialUnavailableWarningsAndDisappearance()
    {
        var connection = new ControlledConnection("partial");
        await using var session = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay());
        session.Start(); await Load(session, connection, A, B);
        session.SelectVolume(A); (await connection.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        foreach (bool absentCollection in new[] { false, true })
        {
            session.RefreshVolumes(); var pending = await connection.NextList();
            Check(!session.VolumeSnapshot.IsAuthoritative && session.VolumeSnapshot.HasRetainedRows && session.VolumeSnapshot.SelectedVolumeName == A);
            var warnings = new[] { "private warning\nunchanged", "", "private warning\nunchanged" };
            pending.Succeed(new(absentCollection ? null : new[] { Row(B) with { Driver = "updated" }, Row(C) }, warnings));
            await State(session, s => s.HasWarnings && s.State != ResourceReadState.Loading);
            warnings[0] = "changed";
            Check(!session.VolumeSnapshot.IsAuthoritative && session.VolumeSnapshot.HasRetainedRows
                && session.VolumeSnapshot.SelectedVolumeName == A && session.VolumeSnapshot.Inspection!.RawJson == Raw
                && session.VolumeSnapshot.Warnings!.SequenceEqual(new[] { "private warning\nunchanged", "", "private warning\nunchanged" })
                && session.VolumeSnapshot.Volumes.Any(row => row.Name == A));
            Check(absentCollection ? session.VolumeSnapshot.LatestListing!.Volumes is null
                && session.VolumeSnapshot.Completeness == VolumeListCompleteness.Unavailable
                : session.VolumeSnapshot.LatestListing!.Volumes!.Count == 2
                && session.VolumeSnapshot.Completeness == VolumeListCompleteness.Incomplete
                && session.VolumeSnapshot.Volumes.First(row => row.Name == B).Driver == "updated");
        }
        session.RefreshVolumes(); (await connection.NextList()).Succeed(new(Array.Empty<VolumeSummary>(), new[] { "problem" }));
        await State(session, s => s.State == ResourceReadState.Ready);
        Check(session.VolumeSnapshot.Volumes.Count == 3 && session.VolumeSnapshot.LatestListing!.Volumes!.Count == 0
            && !session.VolumeSnapshot.IsAuthoritative && session.VolumeSnapshot.SelectedVolumeName == A);
        session.RefreshVolumes(); (await connection.NextList()).Fail(Failure(EngineResourceFailureKind.AccessDenied));
        await State(session, s => s.State == ResourceReadState.Failed);
        Check(session.VolumeSnapshot.Warnings!.Single() == "problem" && session.VolumeSnapshot.HasRetainedRows
            && session.VolumeSnapshot.SelectedVolumeName == A && !session.VolumeSnapshot.IsAuthoritative);
        foreach (var warnings in new IReadOnlyList<string>?[] { null, Array.Empty<string>() })
        {
            session.RefreshVolumes(); (await connection.NextList()).Succeed(new(null, warnings));
            await State(session, s => s.State == ResourceReadState.Unavailable);
            Check(session.VolumeSnapshot.LatestListing!.Volumes is null && (warnings is null ? session.VolumeSnapshot.Warnings is null : session.VolumeSnapshot.Warnings!.Count == 0)
                && session.VolumeSnapshot.Volumes.Count == 3 && session.VolumeSnapshot.SelectedVolumeName == A);
        }
        session.RefreshVolumes(); (await connection.NextList()).Succeed(new(Array.Empty<VolumeSummary>(), Array.Empty<string>()));
        await State(session, s => s.IsAuthoritative);
        Check(session.VolumeSnapshot.Volumes.Count == 0 && !session.VolumeSnapshot.HasRetainedRows
            && session.VolumeSnapshot.SelectedVolumeName is null && session.VolumeSnapshot.Inspection is null);
    }

    public static async Task InitialNullEmptyAndMissingCapability()
    {
        var connection = new ControlledConnection("null");
        await using (var session = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay()))
        {
            session.Start(); (await connection.NextList()).Succeed(new(null, null));
            await State(session, s => s.LatestListing is not null);
            Check(session.VolumeSnapshot.State == ResourceReadState.Unavailable && !session.VolumeSnapshot.IsAuthoritative
                && session.VolumeSnapshot.Volumes.Count == 0 && session.VolumeSnapshot.Warnings is null);
            session.RefreshVolumes(); await Load(session, connection);
            Check(session.VolumeSnapshot.IsAuthoritative && session.VolumeSnapshot.Volumes.Count == 0);
        }
        var missing = new MissingConnection();
        await using var browser = new DesktopSession(new ControlledClient(missing), delay: new ControlledDelay());
        browser.Start(); await Until(() => browser.VolumeSnapshot.ConnectionGeneration > 0);
        browser.RefreshVolumes(); browser.SelectVolume(A); browser.RefreshVolumeInspection(); browser.ClearVolumeInspection();
        Check(browser.EngineState == EngineConnectionState.Connected && browser.VolumeSnapshot.State == ResourceReadState.Unavailable
            && browser.VolumeSnapshot.InspectionState == ResourceReadState.Unavailable && browser.VolumeSnapshot.LatestListing is null);
    }

    public static async Task ClassifiedFailuresAndReturnedIdentity()
    {
        foreach (var kind in Enum.GetValues<EngineResourceFailureKind>().Where(k => k != EngineResourceFailureKind.TransportLoss))
        {
            var connection = new ControlledConnection("failures"); var client = new ControlledClient(connection);
            await using var session = new DesktopSession(client, delay: new ControlledDelay());
            session.Start(); await Load(session, connection, A, B);
            session.SelectVolume(A); (await connection.NextInspection()).Succeed(Detail(A));
            await State(session, s => s.InspectionState == ResourceReadState.Ready);
            var failure = Failure(kind);
            session.RefreshVolumes(); (await connection.NextList()).Fail(failure);
            await State(session, s => s.State == ResourceReadState.Failed);
            Check(ReferenceEquals(session.VolumeSnapshot.Failure, failure) && session.VolumeSnapshot.Volumes.Count == 2
                && session.VolumeSnapshot.SelectedVolumeName == A && session.VolumeSnapshot.HasRetainedRows);
            session.RefreshVolumeInspection(); (await connection.NextInspection()).Fail(failure);
            await State(session, s => s.InspectionState == ResourceReadState.Failed);
            Check(ReferenceEquals(session.VolumeSnapshot.InspectionFailure, failure) && session.VolumeSnapshot.Inspection is null
                && client.Calls == 1 && session.EngineState == EngineConnectionState.Connected);
            Check(kind == EngineResourceFailureKind.NotFound ? session.VolumeSnapshot.SelectedVolumeName is null
                && session.VolumeSnapshot.Volumes.Single().Name == B : session.VolumeSnapshot.SelectedVolumeName == A);
        }
        var mismatch = new ControlledConnection("identity");
        await using var browser = new DesktopSession(new ControlledClient(mismatch), delay: new ControlledDelay());
        browser.Start(); await Load(browser, mismatch, A, B);
        browser.SelectVolume(A); (await mismatch.NextInspection()).Succeed(Detail(B));
        await State(browser, s => s.InspectionState == ResourceReadState.Failed);
        Check(browser.VolumeSnapshot.Inspection is null && browser.VolumeSnapshot.SelectedVolumeName == A
            && browser.VolumeSnapshot.InspectionFailure?.Kind == EngineResourceFailureKind.MalformedData);
        browser.RefreshVolumeInspection(); (await mismatch.NextInspection()).Succeed(Detail(A));
        await State(browser, s => s.InspectionState == ResourceReadState.Ready);
    }

    public static async Task SelectionRefreshFencesAndFairness()
    {
        var connection = new ControlledConnection("fair", false);
        await using var session = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay());
        var published = new List<string>();
        session.VolumesChanged += (_, _) => { if (session.VolumeSnapshot.Inspection is { } value) published.Add(value.Name); };
        session.Start(); await Load(session, connection, A, B, C);
        session.SelectVolume(A); var oldDetail = await connection.NextInspection();
        for (int i = 0; i < 1000; ++i) session.SelectVolume(i % 2 == 0 ? B : C);
        Check(oldDetail.Token.IsCancellationRequested && connection.InspectionCalls == 1);
        oldDetail.Fail(Failure(EngineResourceFailureKind.NotFound));
        var latest = await connection.NextInspection(); Check(latest.Name == C && session.VolumeSnapshot.Volumes.Count == 3);
        session.ClearVolumeInspection(); Check(latest.Token.IsCancellationRequested && session.VolumeSnapshot.Inspection is null);
        latest.Succeed(Detail(C));
        session.RefreshVolumes(); var list = await connection.NextList();
        session.SelectVolume(A);
        for (int i = 0; i < 1000; ++i) session.RefreshVolumes();
        Check(list.Token.IsCancellationRequested && connection.ListCalls == 2);
        list.Succeed(new(new[] { Row(B) }, null));
        var detailTurn = await connection.NextInspection(); Check(detailTurn.Name == A && session.VolumeSnapshot.Volumes.Count == 3);
        session.SelectVolume(B); detailTurn.Succeed(Detail(A));
        var listTurn = await connection.NextList();
        for (int i = 0; i < 1000; ++i) session.SelectVolume(i % 2 == 0 ? A : B);
        Check(!listTurn.Token.IsCancellationRequested);
        listTurn.Succeed(new(new[] { Row(A), Row(B) }, null));
        var selected = await connection.NextInspection(); Check(selected.Name == B);
        selected.Succeed(Detail(B)); await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(published.All(name => name == B) && connection.MaximumConcurrent == 1 && connection.ListCalls == 3 && connection.InspectionCalls == 4);
        session.RefreshVolumeInspection(); var clearing = await connection.NextInspection();
        session.ClearVolumeInspection(); clearing.Fail(Failure(EngineResourceFailureKind.TransportLoss));
        session.RefreshVolumes(); await Load(session, connection, B, A);
        Check(session.VolumeSnapshot.SelectedVolumeName == B && session.VolumeSnapshot.InspectionState == ResourceReadState.NotRequested
            && session.EngineState == EngineConnectionState.Connected);
        session.RefreshVolumeInspection(); var refreshing = await connection.NextInspection();
        session.RefreshVolumes(); refreshing.Succeed(Detail(B));
        await Load(session, connection, A);
        Check(session.VolumeSnapshot.SelectedVolumeName is null && session.VolumeSnapshot.Inspection is null
            && connection.MaximumConcurrent == 1);
    }

    public static async Task ReconnectAndJoinedReplacement()
    {
        foreach (bool fail in new[] { false, true })
        {
            var old = new ControlledConnection("old", false); var fresh = new ControlledConnection("new");
            var client = new ControlledClient(old, fresh);
            await using var session = new DesktopSession(client, delay: new ControlledDelay());
            session.Start(); await Load(session, old, A);
            var generation = session.VolumeSnapshot.ConnectionGeneration;
            session.SelectVolume(A); var late = await old.NextInspection();
            session.Retry();
            Check(late.Token.IsCancellationRequested && old.DisposeCount == 0 && client.Calls == 1
                && session.VolumeSnapshot.State == ResourceReadState.Unavailable && session.VolumeSnapshot.Volumes.Count == 0);
            if (fail) late.Fail(Failure(EngineResourceFailureKind.TransportLoss)); else late.Succeed(Detail(A));
            var initial = await fresh.NextList();
            Check(old.DisposeCount == 1 && !old.DisposedWithActiveRead && session.VolumeSnapshot.ConnectionGeneration > generation
                && session.VolumeSnapshot.Inspection is null && session.VolumeSnapshot.LatestListing is null);
            initial.Succeed(new(new[] { Row(B) }, null)); await State(session, s => s.IsAuthoritative);
            Check(client.Calls == 2 && session.VolumeSnapshot.Volumes.Single().Name == B && session.Snapshot.Metadata?.EngineVersion == "new");
        }
    }

    public static async Task RefreshBurstFencesLateListFailures()
    {
        var connection = new ControlledConnection("list-burst", false); var client = new ControlledClient(connection);
        await using var session = new DesktopSession(client, delay: new ControlledDelay());
        session.Start(); var initial = await connection.NextList();
        for (int i = 0; i < 1000; ++i) session.RefreshVolumes();
        Check(initial.Token.IsCancellationRequested && connection.ListCalls == 1);
        initial.Fail(Failure(EngineResourceFailureKind.TransportLoss));
        var current = await connection.NextList();
        Check(session.VolumeSnapshot.State == ResourceReadState.Loading && session.VolumeSnapshot.Failure is null && client.Calls == 1);
        current.Succeed(new(new[] { Row(A) }, null)); await State(session, s => s.IsAuthoritative);
        session.RefreshVolumes(); var stale = await connection.NextList();
        session.RefreshVolumes(); stale.Fail(Failure(EngineResourceFailureKind.NotFound));
        await Load(session, connection, B);
        Check(session.VolumeSnapshot.Volumes.Single().Name == B && client.Calls == 1 && connection.ListCalls == 4 && connection.MaximumConcurrent == 1);
    }

    public static async Task DeadlinesIncludingSupersededNonCooperativeReads()
    {
        foreach (bool inspect in new[] { false, true })
        foreach (bool honorCancellation in new[] { false, true })
        {
            var connection = new ControlledConnection("deadline", honorCancellation);
            var session = new DesktopSession(new ControlledClient(connection), new()
                { OperationTimeout = TimeSpan.FromMilliseconds(200), MaximumRecoveryAttempts = 0 }, new ControlledDelay());
            session.Start(); var list = await connection.NextList(); InspectionCall? detail = null;
            if (inspect)
            {
                list.Succeed(new(new[] { Row(A), Row(B) }, null)); await State(session, s => s.IsAuthoritative);
                session.SelectVolume(A); detail = await connection.NextInspection();
            }
            if (!honorCancellation)
            {
                // Superseding cancellation does not disable the original bounded deadline.
                if (inspect) session.SelectVolume(B); else session.RefreshVolumes();
            }
            await State(session, s => s.State == ResourceReadState.Unavailable);
            Check((detail?.Token ?? list.Token).IsCancellationRequested && session.VolumeSnapshot.Volumes.Count == 0
                && (inspect ? session.VolumeSnapshot.InspectionFailure : session.VolumeSnapshot.Failure)?.Kind == EngineResourceFailureKind.TransportLoss);
            if (!honorCancellation)
            {
                Check(connection.DisposeCount == 0);
                if (detail is not null) detail.Succeed(Detail(A)); else list.Succeed(new(new[] { Row(A) }, null));
            }
            await Until(() => session.EngineState == EngineConnectionState.Lost);
            await session.DisposeAsync();
            Check(connection.DisposeCount == 1 && !connection.DisposedWithActiveRead && session.VolumeSnapshot.Inspection is null);
        }
    }

    public static async Task VolumeLossUsesSharedRecovery()
    {
        foreach (bool inspect in new[] { false, true })
        {
            var old = new ControlledConnection("lost"); var fresh = new ControlledConnection("fresh");
            var client = new ControlledClient(old, fresh); var clock = new ControlledDelay();
            await using var session = new DesktopSession(client, delay: clock);
            session.Start(); var first = await old.NextList(); var liveness = await clock.Next();
            var failure = Failure(EngineResourceFailureKind.TransportLoss);
            if (inspect)
            {
                first.Succeed(new(new[] { Row(A) }, null)); await State(session, s => s.IsAuthoritative);
                session.SelectVolume(A); (await old.NextInspection()).Fail(failure);
            }
            else first.Fail(failure);
            await Until(() => session.EngineState == EngineConnectionState.Lost);
            Check(liveness.Token.IsCancellationRequested && old.DisposeCount == 1
                && ReferenceEquals(inspect ? session.VolumeSnapshot.InspectionFailure : session.VolumeSnapshot.Failure, failure)
                && session.Snapshot.Failure!.DaemonMessage == failure.DaemonMessage);
            var retry = await clock.Next(); Check(client.Calls == 1); retry.Release();
            await Load(session, fresh, B);
            Check(client.Calls == 2 && session.VolumeSnapshot.SelectedVolumeName is null && session.VolumeSnapshot.Inspection is null
                && fresh.Metadata.Api.Negotiated == new EngineApiVersion(1, 40));
        }
    }

    public static async Task RepeatedDisconnectAndDisposalJoin()
    {
        foreach (bool inspect in new[] { false, true })
        foreach (bool dispose in new[] { false, true })
        {
            var connection = new ControlledConnection("closing", false); var client = new ControlledClient(connection);
            var session = new DesktopSession(client, delay: new ControlledDelay());
            session.Start(); var list = await connection.NextList(); InspectionCall? detail = null;
            if (inspect)
            {
                list.Succeed(new(new[] { Row(A) }, null)); await State(session, s => s.IsAuthoritative);
                session.SelectVolume(A); detail = await connection.NextInspection();
            }
            var first = dispose ? session.DisposeAsync().AsTask() : session.DisconnectAsync();
            var second = dispose ? session.DisposeAsync().AsTask() : session.DisconnectAsync();
            Check(!first.IsCompleted && !second.IsCompleted && connection.DisposeCount == 0
                && (detail?.Token ?? list.Token).IsCancellationRequested && session.VolumeSnapshot.Inspection is null);
            if (detail is not null) detail.Fail(Failure(EngineResourceFailureKind.NotFound)); else list.Succeed(new(new[] { Row(A) }, null));
            await Task.WhenAll(first, second).WaitAsync(Limit); await session.DisposeAsync(); await session.DisposeAsync();
            session.Start(); session.RefreshVolumes(); session.SelectVolume(A);
            Check(connection.DisposeCount == 1 && !connection.DisposedWithActiveRead && client.Calls == 1
                && session.VolumeSnapshot.State == ResourceReadState.Unavailable && session.VolumeSnapshot.Volumes.Count == 0);
        }
    }

    public static async Task EverySharedOwnerInvalidatesVolumes()
    {
        foreach (string source in new[] { "containers", "images", "events", "lifecycle", "image-command", "logs", "liveness", "disconnect", "dispose" })
        {
            var connection = new AllCapabilitiesConnection(source, false); var clock = new ControlledDelay();
            var session = new DesktopSession(new ControlledClient(connection), new() { MaximumRecoveryAttempts = 0 }, clock);
            session.Start(); await Load(session, connection, A);
            await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready && session.ImageSnapshot.State == ResourceReadState.Ready);
            Check(session.OpenContainerLogs(AllCapabilitiesConnection.ContainerId));
            await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
            var lifecycle = session.ExecuteContainerOperationAsync(new(new(session.ContainerSnapshot.ConnectionGeneration,
                AllCapabilitiesConnection.ContainerId), ContainerCommand.Start));
            var imageCommand = session.ExecuteImageOperationAsync(new(new(session.ImageSnapshot.ConnectionGeneration, AllCapabilitiesConnection.ImageId)));
            await Task.WhenAll(connection.CommandStarted.Task, connection.ImageCommandStarted.Task).WaitAsync(Limit);
            session.SelectVolume(A); var late = await connection.NextInspection();
            Task? closing = null;
            switch (source)
            {
                case "containers": connection.ContainerFailure = Failure(EngineResourceFailureKind.TransportLoss); session.RefreshContainers(); break;
                case "images": connection.ImageFailure = Failure(EngineResourceFailureKind.TransportLoss); session.RefreshImages(); break;
                case "events": connection.Events.Fail(); break;
                case "lifecycle": connection.CommandCompletion.TrySetResult(new(ContainerCommand.Start, ContainerCommandDisposition.Uncertain,
                    Failure: new(ContainerCommandFailureKind.TransportFailure, "transport lost"))); break;
                case "image-command": connection.ImageCommandCompletion.TrySetResult(new(ImageCommandDisposition.Uncertain,
                    Failure: new(ImageCommandFailureKind.TransportFailure, "transport lost"))); break;
                case "logs": connection.Logs.Fail(); break;
                case "liveness": connection.PingFailure = new(EngineConnectionFailureKind.TransientTransportFailure, "readiness lost"); (await clock.Next()).Release(); break;
                case "disconnect": closing = session.DisconnectAsync(); break;
                case "dispose": closing = session.DisposeAsync().AsTask(); break;
            }
            try { await State(session, s => s.State == ResourceReadState.Unavailable); }
            catch (TimeoutException) { throw new InvalidOperationException($"Shared invalidation did not arrive from {source}; connection {session.EngineState}, logs {session.ContainerLogs.Phase}."); }
            Check(late.Token.IsCancellationRequested && session.VolumeSnapshot.Inspection is null && session.VolumeSnapshot.SelectedVolumeName is null
                && session.VolumeSnapshot.LatestListing is null && session.VolumeSnapshot.Volumes.Count == 0
                && connection.DisposeCount == 0 && (closing is null || !closing.IsCompleted), source);
            // Returning even a classified late error cannot revive state or start competing recovery.
            late.Fail(Failure(EngineResourceFailureKind.NotFound));
            if (closing is not null) await closing.WaitAsync(Limit);
            else await Until(() => session.EngineState == EngineConnectionState.Lost);
            await Task.WhenAll(lifecycle, imageCommand).WaitAsync(Limit);
            Check(connection.DisposeCount == 1 && !connection.DisposedWithActiveRead && connection.Events.Disposed && connection.Logs.Disposed
                && connection.CommandToken.IsCancellationRequested && connection.ImageCommandToken.IsCancellationRequested
                && connection.EventToken.IsCancellationRequested && connection.LogToken.IsCancellationRequested, source);
            await session.DisposeAsync();
        }
    }

    public static async Task VolumeDeadlineRecoveryAndCapabilityCoexistence()
    {
        var old = new AllCapabilitiesConnection("old", false); var fresh = new AllCapabilitiesConnection("fresh");
        var client = new ControlledClient(old, fresh); var clock = new ControlledDelay();
        await using var session = new DesktopSession(client, new() { OperationTimeout = TimeSpan.FromMilliseconds(500) }, clock);
        session.Start(); await Load(session, old, A);
        await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready && session.ImageSnapshot.State == ResourceReadState.Ready);
        Check(session.OpenContainerLogs(AllCapabilitiesConnection.ContainerId));
        await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        var lifecycle = session.ExecuteContainerOperationAsync(new(new(session.ContainerSnapshot.ConnectionGeneration,
            AllCapabilitiesConnection.ContainerId), ContainerCommand.Start));
        var imageCommand = session.ExecuteImageOperationAsync(new(new(session.ImageSnapshot.ConnectionGeneration, AllCapabilitiesConnection.ImageId)));
        await Task.WhenAll(old.CommandStarted.Task, old.ImageCommandStarted.Task).WaitAsync(Limit);
        var liveness = await clock.Next();
        session.SelectVolume(A); var late = await old.NextInspection();
        session.RefreshVolumes(); // Supersedes the active detail but leaves all other capabilities operating.
        Check(!old.CommandToken.IsCancellationRequested && !old.ImageCommandToken.IsCancellationRequested
            && !old.EventToken.IsCancellationRequested && !old.LogToken.IsCancellationRequested);
        await State(session, s => s.State == ResourceReadState.Unavailable);
        Check(session.ContainerSnapshot.State == ResourceReadState.Unavailable && session.ImageSnapshot.State == ResourceReadState.Unavailable
            && old.CommandToken.IsCancellationRequested && old.ImageCommandToken.IsCancellationRequested
            && old.EventToken.IsCancellationRequested && old.LogToken.IsCancellationRequested
            && late.Token.IsCancellationRequested && old.DisposeCount == 0 && client.Calls == 1);
        late.Succeed(Detail(A));
        await Until(() => session.EngineState == EngineConnectionState.Lost);
        await Task.WhenAll(lifecycle, imageCommand).WaitAsync(Limit);
        Check(liveness.Token.IsCancellationRequested && old.DisposeCount == 1 && !old.DisposedWithActiveRead);
        (await clock.Next()).Release(); await Load(session, fresh, B);
        PendingDelay resume;
        do { resume = await clock.Next(); } while (resume.Duration != TimeSpan.FromSeconds(2));
        resume.Release();
        await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready && session.ImageSnapshot.State == ResourceReadState.Ready
            && session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        Check(client.Calls == 2 && fresh.Logs.Opened && session.VolumeSnapshot.Volumes.Single().Name == B
            && session.VolumeSnapshot.Inspection is null && session.VolumeSnapshot.SelectedVolumeName is null);
    }

    private sealed class AllCapabilitiesConnection(string version, bool honorCancellation = true)
        : ControlledConnection(version, honorCancellation), IContainerReader, IImageReader, IEngineEventSource,
            IContainerLogSource, IContainerCommands, IImageCommands
    {
        public const string ContainerId = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        public const string ImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public readonly EventSubscription Events = new();
        public readonly LogSubscription Logs = new();
        public readonly TaskCompletionSource CommandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ImageCommandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<ContainerCommandResult> CommandCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<ImageCommandResult> ImageCommandCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken EventToken, LogToken, CommandToken, ImageCommandToken;
        public EngineResourceFailure? ContainerFailure, ImageFailure;
        private int activeCommands;
        public override ValueTask DisposeAsync()
        {
            DisposedWithActiveRead |= Volatile.Read(ref activeCommands) != 0 || !Events.Disposed || Logs.Opened && !Logs.Disposed;
            return base.DisposeAsync();
        }
        public Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ContainerFailure is { } failure ? EngineResourceResult<IReadOnlyList<ContainerSummary>>.Failed(failure)
                : EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(new[] { new ContainerSummary(ContainerId,
                    null, null, ImageId, "exited", null, null, null, null, null, null) }));
        public Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string identity, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<ContainerInspection>.Success(new(identity, null, null, ImageId, null,
                new("exited", false, false, false, false, 0, null, null, null, null), null, null, null, "{}")));
        public Task<EngineResourceResult<IReadOnlyList<ImageSummary>>> ListImagesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ImageFailure is { } failure ? EngineResourceResult<IReadOnlyList<ImageSummary>>.Failed(failure)
                : EngineResourceResult<IReadOnlyList<ImageSummary>>.Success(new[] { new ImageSummary(ImageId, null, null, null, null) }));
        public Task<EngineResourceResult<ImageInspection>> InspectImageAsync(string identity, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<ImageInspection>.Success(new(identity, null, null, null, null, null, null, null, null, null, null, "{}")));
        public Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
        { EventToken = cancellationToken; return Task.FromResult(EngineEventSubscriptionAttempt.Subscribed(Events)); }
        public Task<ContainerLogSubscriptionAttempt> OpenLogsAsync(ContainerLogRequest request, CancellationToken cancellationToken = default)
        { LogToken = cancellationToken; Logs.Opened = true; return Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(Logs)); }
        public async Task<ContainerCommandResult> StartContainerAsync(string identity, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref activeCommands); CommandToken = cancellationToken; CommandStarted.TrySetResult();
            try { return await CommandCompletion.Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref activeCommands); }
        }
        public async Task<ImageCommandResult> DeleteImageAsync(string reference, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref activeCommands); ImageCommandToken = cancellationToken; ImageCommandStarted.TrySetResult();
            try { return await ImageCommandCompletion.Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref activeCommands); }
        }
        public Task<ContainerCommandResult> StopContainerAsync(string identity, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<ContainerCommandResult> RestartContainerAsync(string identity, int? gracePeriodSeconds = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<ContainerCommandResult> DeleteContainerAsync(string identity, bool force = false, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private sealed class EventSubscription : IEngineEventSubscription
    {
        private readonly Channel<EngineEvent> channel = Channel.CreateBounded<EngineEvent>(1);
        private readonly TaskCompletionSource<EngineEventFailure?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public ChannelReader<EngineEvent> Events => channel.Reader;
        public Task<EngineEventFailure?> Completion => completion.Task;
        public void Fail() { completion.TrySetResult(new(EngineEventFailureKind.TransportLoss, "events lost")); channel.Writer.TryComplete(); }
        public ValueTask DisposeAsync() { Disposed = true; completion.TrySetResult(null); channel.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class LogSubscription : IContainerLogSubscription
    {
        private readonly Channel<ContainerLogOutput> channel = Channel.CreateBounded<ContainerLogOutput>(1);
        private readonly TaskCompletionSource<ContainerLogCompletion> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed, Opened;
        public string ContainerId => AllCapabilitiesConnection.ContainerId;
        public bool IsTty => false;
        public ChannelReader<ContainerLogOutput> Output => channel.Reader;
        public Task<ContainerLogCompletion> Completion => completion.Task;
        public void Fail() { completion.TrySetResult(new(ContainerLogTermination.Failed, new(ContainerLogFailureKind.TransportLoss, "logs lost"))); channel.Writer.TryComplete(); }
        public ValueTask DisposeAsync() { Disposed = true; completion.TrySetResult(new(ContainerLogTermination.Canceled)); channel.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class ControlledClient(params IEngineConnection[] connections) : IEngineConnectionClient
    {
        public int Calls;
        public Task<EngineConnectionAttempt> ConnectAsync(EngineEndpoint? endpoint = null, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(EngineConnectionAttempt.Connected(connections[Interlocked.Increment(ref Calls) - 1])); }
    }
    private class MissingConnection : IEngineConnection
    {
        public EngineMetadata Metadata { get; } = new(EngineEndpoint.LocalWindows, "missing",
            new(new(1, 40), new(1, 56), new(1, 40), new(1, 56), new(1, 40)),
            null, null, null, "same-engine", null, null, null, null, null, new(null, null, null, null, null, null, null, null, null, null));
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default) => Task.FromResult<EngineConnectionFailure?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private class ControlledConnection(string version, bool honorCancellation = true) : IEngineConnection, IVolumeReader
    {
        private readonly Channel<ListCall> lists = Channel.CreateUnbounded<ListCall>();
        private readonly Channel<InspectionCall> inspections = Channel.CreateUnbounded<InspectionCall>();
        private int active;
        public int MaximumConcurrent, DisposeCount, ListCalls, InspectionCalls;
        public bool DisposedWithActiveRead;
        public EngineConnectionFailure? PingFailure;
        public EngineMetadata Metadata => new MissingConnection().Metadata with { EngineVersion = version };
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default) => Task.FromResult(PingFailure);
        public virtual ValueTask DisposeAsync()
        { DisposedWithActiveRead |= Volatile.Read(ref active) != 0; Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
        public async Task<EngineResourceResult<VolumeListing>> ListVolumesAsync(CancellationToken cancellationToken = default)
        {
            Enter(); Interlocked.Increment(ref ListCalls);
            try
            {
                var call = new ListCall(cancellationToken); lists.Writer.TryWrite(call);
                return await (honorCancellation ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public async Task<EngineResourceResult<VolumeInspection>> InspectVolumeAsync(string name, CancellationToken cancellationToken = default)
        {
            Enter(); Interlocked.Increment(ref InspectionCalls);
            try
            {
                var call = new InspectionCall(name, cancellationToken); inspections.Writer.TryWrite(call);
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
        public TaskCompletionSource<EngineResourceResult<VolumeListing>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(VolumeListing value) => Completion.SetResult(EngineResourceResult<VolumeListing>.Success(value));
        public void Fail(EngineResourceFailure value) => Completion.SetResult(EngineResourceResult<VolumeListing>.Failed(value));
    }
    private sealed class InspectionCall(string name, CancellationToken token)
    {
        public string Name { get; } = name;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<EngineResourceResult<VolumeInspection>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(VolumeInspection value) => Completion.SetResult(EngineResourceResult<VolumeInspection>.Success(value));
        public void Fail(EngineResourceFailure value) => Completion.SetResult(EngineResourceResult<VolumeInspection>.Failed(value));
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
