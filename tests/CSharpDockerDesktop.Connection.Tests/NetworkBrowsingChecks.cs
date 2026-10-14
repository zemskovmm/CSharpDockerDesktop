using System.Collections.ObjectModel;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal static partial class NetworkBrowsingChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);
    private static readonly string A = "private-network-" + new string('a', 128), B = "Private-network-B", C = "private-network-c";
    private const string Raw = "{\n  \"Name\": \"private\", \"Unknown\": 1.0000, \"Labels\": {\"SECRET\":\"private\"}\n}";
    private static NetworkSummary Row(string name) => new(name, "display-name", "bridge", "local", false,
        new Dictionary<string, string> { ["Secret"] = "private", ["secret"] = "different" });
    private static NetworkInspection Detail(string name, string raw = Raw) => new(name, "display-name", "driver", null, false, Row(name).Labels, null, null, raw);
    private static EngineResourceFailure Failure(EngineResourceFailureKind kind) => new(kind,
        "safe classified description", 503, "private original daemon message\nunchanged");
    private static void Check(bool condition, string message = "Network browsing assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Limit);
        while (!predicate()) await Task.Delay(2, deadline.Token);
    }
    private static async Task State(DesktopSession session, Func<NetworkBrowsingSnapshot, bool> predicate)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? sender, EventArgs args) { if (predicate(session.NetworkSnapshot)) reached.TrySetResult(); }
        session.NetworksChanged += Observe;
        try { Observe(null, EventArgs.Empty); await reached.Task.WaitAsync(Limit); }
        finally { session.NetworksChanged -= Observe; }
    }
    private static async Task Load(DesktopSession session, ControlledConnection connection, params string[] names)
    {
        (await connection.NextList()).Succeed(names.Select(Row).ToArray());
        await State(session, s => s.IsAuthoritative);
    }

    public static async Task ImmutableEvidenceAndExactIdentity()
    {
        var connection = new ControlledConnection("immutable"); var client = new ControlledClient(connection);
        await using var session = new DesktopSession(client, delay: new ControlledDelay());
        INetworkBrowser browser = session;
        Check(browser.NetworkSnapshot.State == ResourceReadState.Unavailable);
        browser.NetworksChanged += (_, _) => throw new InvalidOperationException("private observer data");
        session.Start(); var initial = await connection.NextList();
        Check(browser.NetworkSnapshot.State == ResourceReadState.Loading);
        var labels = new Dictionary<string, string> { ["Secret"] = "private", ["secret"] = "different" };
        var source = new[] { Row(A) with { Labels = labels }, Row(B) with { Name = null, Driver = null, Scope = null, Internal = null, Labels = null },
            Row(C) with { Name = "", Driver = "", Scope = "", Labels = new Dictionary<string, string>() } };
        initial.Succeed(source); await State(session, s => s.IsAuthoritative);
        var saved = browser.NetworkSnapshot;
        source[0] = Row("changed"); labels["Secret"] = "changed";
        Check(saved.Networks[0].Id == A && saved.Networks[0].Labels!["Secret"] == "private"
            && saved.Networks[0].Labels!["secret"] == "different" && saved.Networks[1].Name is null
            && saved.Networks[1].Internal is null && saved.Networks[1].Labels is null
            && saved.Networks[2].Name == "" && saved.Networks[2].Internal == false && saved.Networks[2].Labels!.Count == 0
            && saved.Networks is ReadOnlyCollection<NetworkSummary> && saved.Networks[0].Labels is ReadOnlyDictionary<string, string>);
        browser.SelectNetwork(A[..12]); Check(browser.NetworkSnapshot.SelectedNetworkId is null);
        browser.SelectNetwork(B.ToLowerInvariant()); Check(browser.NetworkSnapshot.SelectedNetworkId is null);
        browser.SelectNetwork("display-name"); Check(browser.NetworkSnapshot.SelectedNetworkId is null);
        browser.SelectNetwork(A); var call = await connection.NextInspection(); Check(call.Id == A);
        var options = new Dictionary<string, string> { ["Secret"] = "private" };
        var auxiliary = new Dictionary<string, string> { ["router"] = "private address" };
        var configurations = new[] { new NetworkIpamConfiguration("", null, "gateway", auxiliary) };
        var attachments = new Dictionary<string, NetworkAttachment> { ["full-container-ID"] = new("private", "endpoint", "mac", "ipv4", "ipv6") };
        call.Succeed(Detail(A) with { Labels = labels, Ipam = new("custom", configurations, options), Containers = attachments });
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        var detail = browser.NetworkSnapshot.Inspection!;
        labels["secret"] = "changed"; options["Secret"] = "changed"; auxiliary["router"] = "changed";
        configurations[0] = new(null, null, null, null); attachments.Clear();
        Check(detail.RawJson == Raw && detail.Labels!["secret"] == "different" && detail.Ipam!.Options!["Secret"] == "private"
            && detail.Ipam.Config![0].Subnet == "" && detail.Ipam.Config[0].IPRange is null
            && detail.Ipam.Config[0].AuxiliaryAddresses!["router"] == "private address"
            && detail.Containers!["full-container-ID"].EndpointId == "endpoint"
            && detail.Containers is ReadOnlyDictionary<string, NetworkAttachment>
            && detail.Ipam.Config is ReadOnlyCollection<NetworkIpamConfiguration>
            && detail.Ipam.Config[0].AuxiliaryAddresses is ReadOnlyDictionary<string, string>);
        browser.RefreshNetworks(); await Load(session, connection, B, A, A);
        Check(browser.NetworkSnapshot.Networks.Select(row => row.Id).SequenceEqual(new[] { B, A })
            && ReferenceEquals(browser.NetworkSnapshot.Inspection, detail) && saved.Networks.Count == 3 && saved.Inspection is null);
        foreach (bool empty in new[] { false, true })
        {
            browser.RefreshNetworkInspection(); (await connection.NextInspection()).Succeed(Detail(A) with
            {
                Labels = empty ? new Dictionary<string, string>() : null,
                Ipam = empty ? new("", Array.Empty<NetworkIpamConfiguration>(), new Dictionary<string, string>()) : null,
                Containers = empty ? new Dictionary<string, NetworkAttachment>() : null
            });
            await State(session, s => s.InspectionState == ResourceReadState.Ready);
            var value = browser.NetworkSnapshot.Inspection!;
            Check(empty ? value.Labels!.Count == 0 && value.Ipam!.Config!.Count == 0 && value.Containers!.Count == 0
                : value.Labels is null && value.Ipam is null && value.Containers is null);
        }
        browser.RefreshNetworkInspection(); (await connection.NextInspection()).Succeed(Detail(A) with { Ipam = new(null, null, null) });
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(browser.NetworkSnapshot.Inspection!.Ipam is { Driver: null, Config: null, Options: null }
            && client.Calls == 1 && connection.MaximumConcurrent == 1);
    }

    public static async Task EmptyUnavailableAndDisappearance()
    {
        var connection = new ControlledConnection("empty");
        await using (var session = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay()))
        {
            session.Start(); await Load(session, connection);
            Check(session.NetworkSnapshot.Networks.Count == 0 && session.NetworkSnapshot.IsAuthoritative);
            session.RefreshNetworks(); await Load(session, connection, A, B);
            session.SelectNetwork(A); (await connection.NextInspection()).Succeed(Detail(A));
            await State(session, s => s.InspectionState == ResourceReadState.Ready);
            session.RefreshNetworks(); var pending = await connection.NextList();
            Check(session.NetworkSnapshot.State == ResourceReadState.Loading && session.NetworkSnapshot.HasRetainedRows
                && session.NetworkSnapshot.Inspection!.RawJson == Raw && session.NetworkSnapshot.SelectedNetworkId == A);
            pending.Fail(Failure(EngineResourceFailureKind.AccessDenied));
            await State(session, s => s.State == ResourceReadState.Failed);
            Check(session.NetworkSnapshot.Networks.Count == 2 && !session.NetworkSnapshot.IsAuthoritative
                && session.NetworkSnapshot.Inspection!.RawJson == Raw);
            session.RefreshNetworks(); await Load(session, connection, B);
            Check(session.NetworkSnapshot.SelectedNetworkId is null && session.NetworkSnapshot.Inspection is null
                && session.NetworkSnapshot.InspectionState == ResourceReadState.NotRequested);
            session.RefreshNetworks(); await Load(session, connection);
            Check(session.NetworkSnapshot.Networks.Count == 0 && session.NetworkSnapshot.IsAuthoritative && !session.NetworkSnapshot.HasRetainedRows);
        }
        await using var missing = new DesktopSession(new ControlledClient(new MissingConnection()), delay: new ControlledDelay());
        missing.Start(); await Until(() => missing.NetworkSnapshot.ConnectionGeneration > 0);
        missing.RefreshNetworks(); missing.SelectNetwork(A); missing.RefreshNetworkInspection(); missing.ClearNetworkInspection();
        Check(missing.EngineState == EngineConnectionState.Connected && missing.NetworkSnapshot.State == ResourceReadState.Unavailable
            && missing.NetworkSnapshot.InspectionState == ResourceReadState.Unavailable && missing.NetworkSnapshot.Failure is null);
    }

    public static async Task CancellationCallbacksSeeInvalidatedState()
    {
        var connection = new ControlledConnection("callbacks", false);
        await using var session = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay());
        session.Start(); await Load(session, connection, A, B);
        session.SelectNetwork(A); var first = await connection.NextInspection();
        NetworkBrowsingSnapshot? seen = null;
        using (first.Token.Register(() => seen = session.NetworkSnapshot)) session.SelectNetwork(B);
        Check(seen is { SelectedNetworkId: not null, Inspection: null, InspectionState: ResourceReadState.Loading } && seen.SelectedNetworkId == B);
        first.Fail(Failure(EngineResourceFailureKind.TransportLoss));
        var second = await connection.NextInspection();
        seen = null;
        using (second.Token.Register(() => seen = session.NetworkSnapshot)) session.ClearNetworkInspection();
        Check(seen is { SelectedNetworkId: not null, Inspection: null, InspectionState: ResourceReadState.NotRequested } && seen.SelectedNetworkId == B);
        second.Succeed(Detail(B));
        session.RefreshNetworks(); var list = await connection.NextList();
        seen = null;
        using (list.Token.Register(() => seen = session.NetworkSnapshot)) session.RefreshNetworks();
        Check(seen is { State: ResourceReadState.Loading, Failure: null });
        list.Fail(Failure(EngineResourceFailureKind.TransportLoss)); await Load(session, connection, B);
        session.RefreshNetworkInspection(); var third = await connection.NextInspection();
        seen = null; Task closing;
        using (third.Token.Register(() => seen = session.NetworkSnapshot)) closing = session.DisconnectAsync();
        Check(seen is { State: ResourceReadState.Unavailable, SelectedNetworkId: null, Inspection: null } && seen.Networks.Count == 0);
        third.Succeed(Detail(B)); await closing.WaitAsync(Limit);
        Check(connection.DisposeCount == 1 && !connection.DisposedWithActiveRead);
    }

    public static async Task SuccessfulBrowsingCoexistsWithOwnedCapabilities()
    {
        var connection = new AllCapabilitiesConnection("coexist"); var client = new ControlledClient(connection);
        await using var session = new DesktopSession(client, delay: new ControlledDelay());
        session.Start(); await Load(session, connection, A, B);
        await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready && session.ImageSnapshot.State == ResourceReadState.Ready
            && session.VolumeSnapshot.IsAuthoritative);
        Check(session.OpenContainerLogs(AllCapabilitiesConnection.ContainerId));
        await Until(() => session.ContainerLogs.Phase == ContainerLogsPhase.Streaming);
        var lifecycle = session.ExecuteContainerOperationAsync(new(new(session.ContainerSnapshot.ConnectionGeneration,
            AllCapabilitiesConnection.ContainerId), ContainerCommand.Start));
        var imageCommand = session.ExecuteImageOperationAsync(new(new(session.ImageSnapshot.ConnectionGeneration, AllCapabilitiesConnection.ImageId)));
        await Task.WhenAll(connection.CommandStarted.Task, connection.ImageCommandStarted.Task).WaitAsync(Limit);
        session.SelectNetwork(A); (await connection.NextInspection()).Succeed(Detail(A));
        await State(session, s => s.InspectionState == ResourceReadState.Ready);
        session.RefreshNetworks(); await Load(session, connection, B, A);
        session.RefreshContainers(); session.RefreshImages(); session.RefreshVolumes();
        await Until(() => session.ContainerSnapshot.State == ResourceReadState.Ready && session.ImageSnapshot.State == ResourceReadState.Ready
            && session.VolumeSnapshot.IsAuthoritative);
        Check(!connection.CommandToken.IsCancellationRequested && !connection.ImageCommandToken.IsCancellationRequested
            && !connection.EventToken.IsCancellationRequested && !connection.LogToken.IsCancellationRequested
            && connection.EventCalls == 1 && connection.LogCalls == 1 && connection.DisposeCount == 0 && client.Calls == 1
            && session.NetworkSnapshot.SelectedNetworkId == A && session.NetworkSnapshot.Inspection!.RawJson == Raw);
        await session.DisconnectAsync(); await Task.WhenAll(lifecycle, imageCommand).WaitAsync(Limit);
        Check(connection.DisposeCount == 1 && !connection.DisposedWithActiveRead && connection.CommandCalls == 1 && connection.ImageCommandCalls == 1);
    }

    public static async Task ClassifiedFailuresAndReturnedIdentity()
    {
        foreach (var kind in Enum.GetValues<EngineResourceFailureKind>().Where(k => k != EngineResourceFailureKind.TransportLoss))
        {
            var connection = new ControlledConnection("failures"); var client = new ControlledClient(connection);
            await using var session = new DesktopSession(client, delay: new ControlledDelay());
            session.Start(); await Load(session, connection, A, B);
            session.SelectNetwork(A); (await connection.NextInspection()).Succeed(Detail(A));
            await State(session, s => s.InspectionState == ResourceReadState.Ready);
            var failure = Failure(kind);
            session.RefreshNetworks(); (await connection.NextList()).Fail(failure);
            await State(session, s => s.State == ResourceReadState.Failed);
            Check(ReferenceEquals(session.NetworkSnapshot.Failure, failure) && session.NetworkSnapshot.Networks.Count == 2
                && session.NetworkSnapshot.SelectedNetworkId == A && session.NetworkSnapshot.HasRetainedRows);
            session.RefreshNetworkInspection(); (await connection.NextInspection()).Fail(failure);
            await State(session, s => s.InspectionState == ResourceReadState.Failed);
            Check(ReferenceEquals(session.NetworkSnapshot.InspectionFailure, failure) && session.NetworkSnapshot.Inspection is null
                && client.Calls == 1 && session.EngineState == EngineConnectionState.Connected);
            Check(kind == EngineResourceFailureKind.NotFound ? session.NetworkSnapshot.SelectedNetworkId is null
                && session.NetworkSnapshot.Networks.Single().Id == B : session.NetworkSnapshot.SelectedNetworkId == A);
        }
        var mismatch = new ControlledConnection("identity");
        await using var browser = new DesktopSession(new ControlledClient(mismatch), delay: new ControlledDelay());
        browser.Start(); await Load(browser, mismatch, A, B);
        browser.SelectNetwork(A); (await mismatch.NextInspection()).Succeed(Detail(B));
        await State(browser, s => s.InspectionState == ResourceReadState.Failed);
        Check(browser.NetworkSnapshot.Inspection is null && browser.NetworkSnapshot.SelectedNetworkId == A
            && browser.NetworkSnapshot.InspectionFailure?.Kind == EngineResourceFailureKind.MalformedData);
        browser.RefreshNetworkInspection(); (await mismatch.NextInspection()).Succeed(Detail(A));
        await State(browser, s => s.InspectionState == ResourceReadState.Ready);
    }

    public static async Task SelectionRefreshFencesAndFairness()
    {
        var connection = new ControlledConnection("fair", false);
        await using var session = new DesktopSession(new ControlledClient(connection), delay: new ControlledDelay());
        var published = new List<string>();
        session.NetworksChanged += (_, _) => { if (session.NetworkSnapshot.Inspection is { } value) published.Add(value.Id); };
        session.Start(); await Load(session, connection, A, B, C);
        session.SelectNetwork(A); var oldDetail = await connection.NextInspection();
        for (int i = 0; i < 1000; ++i) session.SelectNetwork(i % 2 == 0 ? B : C);
        Check(oldDetail.Token.IsCancellationRequested && connection.InspectionCalls == 1);
        oldDetail.Fail(Failure(EngineResourceFailureKind.NotFound));
        var latest = await connection.NextInspection(); Check(latest.Id == C && session.NetworkSnapshot.Networks.Count == 3);
        session.ClearNetworkInspection(); Check(latest.Token.IsCancellationRequested && session.NetworkSnapshot.Inspection is null);
        latest.Succeed(Detail(C));
        session.RefreshNetworks(); var list = await connection.NextList();
        session.SelectNetwork(A);
        for (int i = 0; i < 1000; ++i) session.RefreshNetworks();
        Check(list.Token.IsCancellationRequested && connection.ListCalls == 2);
        list.Succeed(new[] { Row(B) });
        var detailTurn = await connection.NextInspection(); Check(detailTurn.Id == A && session.NetworkSnapshot.Networks.Count == 3);
        session.SelectNetwork(B); detailTurn.Succeed(Detail(A));
        var listTurn = await connection.NextList();
        for (int i = 0; i < 1000; ++i) session.SelectNetwork(i % 2 == 0 ? A : B);
        Check(!listTurn.Token.IsCancellationRequested);
        listTurn.Succeed(new[] { Row(A), Row(B) });
        var selected = await connection.NextInspection(); Check(selected.Id == B);
        selected.Succeed(Detail(B)); await State(session, s => s.InspectionState == ResourceReadState.Ready);
        Check(published.All(name => name == B) && connection.MaximumConcurrent == 1 && connection.ListCalls == 3 && connection.InspectionCalls == 4);
        session.RefreshNetworkInspection(); var clearing = await connection.NextInspection();
        session.ClearNetworkInspection(); clearing.Fail(Failure(EngineResourceFailureKind.TransportLoss));
        session.RefreshNetworks(); await Load(session, connection, B, A);
        Check(session.NetworkSnapshot.SelectedNetworkId == B && session.NetworkSnapshot.InspectionState == ResourceReadState.NotRequested
            && session.EngineState == EngineConnectionState.Connected);
        session.RefreshNetworkInspection(); var refreshing = await connection.NextInspection();
        session.RefreshNetworks(); refreshing.Succeed(Detail(B));
        await Load(session, connection, A);
        Check(session.NetworkSnapshot.SelectedNetworkId is null && session.NetworkSnapshot.Inspection is null
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
            var generation = session.NetworkSnapshot.ConnectionGeneration;
            session.SelectNetwork(A); var late = await old.NextInspection();
            session.Retry();
            Check(late.Token.IsCancellationRequested && old.DisposeCount == 0 && client.Calls == 1
                && session.NetworkSnapshot.State == ResourceReadState.Unavailable && session.NetworkSnapshot.Networks.Count == 0);
            if (fail) late.Fail(Failure(EngineResourceFailureKind.TransportLoss)); else late.Succeed(Detail(A));
            var initial = await fresh.NextList();
            Check(old.DisposeCount == 1 && !old.DisposedWithActiveRead && session.NetworkSnapshot.ConnectionGeneration > generation
                && session.NetworkSnapshot.Inspection is null && session.NetworkSnapshot.SelectedNetworkId is null);
            initial.Succeed(new[] { Row(B) }); await State(session, s => s.IsAuthoritative);
            Check(client.Calls == 2 && session.NetworkSnapshot.Networks.Single().Id == B && session.Snapshot.Metadata?.EngineVersion == "new");
        }
    }

    public static async Task RefreshBurstFencesLateListFailures()
    {
        var connection = new ControlledConnection("list-burst", false); var client = new ControlledClient(connection);
        await using var session = new DesktopSession(client, delay: new ControlledDelay());
        session.Start(); var initial = await connection.NextList();
        for (int i = 0; i < 1000; ++i) session.RefreshNetworks();
        Check(initial.Token.IsCancellationRequested && connection.ListCalls == 1);
        initial.Fail(Failure(EngineResourceFailureKind.TransportLoss));
        var current = await connection.NextList();
        Check(session.NetworkSnapshot.State == ResourceReadState.Loading && session.NetworkSnapshot.Failure is null && client.Calls == 1);
        current.Succeed(new[] { Row(A) }); await State(session, s => s.IsAuthoritative);
        session.RefreshNetworks(); var stale = await connection.NextList();
        session.RefreshNetworks(); stale.Fail(Failure(EngineResourceFailureKind.NotFound));
        await Load(session, connection, B);
        Check(session.NetworkSnapshot.Networks.Single().Id == B && client.Calls == 1 && connection.ListCalls == 4 && connection.MaximumConcurrent == 1);
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
                list.Succeed(new[] { Row(A), Row(B) }); await State(session, s => s.IsAuthoritative);
                session.SelectNetwork(A); detail = await connection.NextInspection();
            }
            if (!honorCancellation)
            {
                // Superseding cancellation does not disable the original bounded deadline.
                if (inspect) session.SelectNetwork(B); else session.RefreshNetworks();
            }
            await State(session, s => s.State == ResourceReadState.Unavailable);
            Check((detail?.Token ?? list.Token).IsCancellationRequested && session.NetworkSnapshot.Networks.Count == 0
                && (inspect ? session.NetworkSnapshot.InspectionFailure : session.NetworkSnapshot.Failure)?.Kind == EngineResourceFailureKind.TransportLoss);
            if (!honorCancellation)
            {
                Check(connection.DisposeCount == 0);
                if (detail is not null) detail.Succeed(Detail(A)); else list.Succeed(new[] { Row(A) });
            }
            await Until(() => session.EngineState == EngineConnectionState.Lost);
            await session.DisposeAsync();
            Check(connection.DisposeCount == 1 && !connection.DisposedWithActiveRead && session.NetworkSnapshot.Inspection is null);
        }
    }

    public static async Task NetworkLossUsesSharedRecovery()
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
                first.Succeed(new[] { Row(A) }); await State(session, s => s.IsAuthoritative);
                session.SelectNetwork(A); (await old.NextInspection()).Fail(failure);
            }
            else first.Fail(failure);
            await Until(() => session.EngineState == EngineConnectionState.Lost);
            Check(liveness.Token.IsCancellationRequested && old.DisposeCount == 1
                && ReferenceEquals(inspect ? session.NetworkSnapshot.InspectionFailure : session.NetworkSnapshot.Failure, failure)
                && session.Snapshot.Failure!.DaemonMessage == failure.DaemonMessage);
            var retry = await clock.Next(); Check(client.Calls == 1); retry.Release();
            await Load(session, fresh, B);
            Check(client.Calls == 2 && session.NetworkSnapshot.SelectedNetworkId is null && session.NetworkSnapshot.Inspection is null
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
                list.Succeed(new[] { Row(A) }); await State(session, s => s.IsAuthoritative);
                session.SelectNetwork(A); detail = await connection.NextInspection();
            }
            var first = dispose ? session.DisposeAsync().AsTask() : session.DisconnectAsync();
            var second = dispose ? session.DisposeAsync().AsTask() : session.DisconnectAsync();
            Check(!first.IsCompleted && !second.IsCompleted && connection.DisposeCount == 0
                && (detail?.Token ?? list.Token).IsCancellationRequested && session.NetworkSnapshot.Inspection is null);
            if (detail is not null) detail.Fail(Failure(EngineResourceFailureKind.NotFound)); else list.Succeed(new[] { Row(A) });
            await Task.WhenAll(first, second).WaitAsync(Limit); await session.DisposeAsync(); await session.DisposeAsync();
            session.Start(); session.RefreshNetworks(); session.SelectNetwork(A);
            Check(connection.DisposeCount == 1 && !connection.DisposedWithActiveRead && client.Calls == 1
                && session.NetworkSnapshot.State == ResourceReadState.Unavailable && session.NetworkSnapshot.Networks.Count == 0);
        }
    }

    public static async Task EverySharedOwnerInvalidatesNetworks()
    {
        foreach (string source in new[] { "containers", "images", "volumes", "events", "lifecycle", "image-command", "logs", "liveness", "disconnect", "dispose" })
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
            session.SelectNetwork(A); var late = await connection.NextInspection();
            Task? closing = null;
            switch (source)
            {
                case "containers": connection.ContainerFailure = Failure(EngineResourceFailureKind.TransportLoss); session.RefreshContainers(); break;
                case "images": connection.ImageFailure = Failure(EngineResourceFailureKind.TransportLoss); session.RefreshImages(); break;
                case "volumes": connection.VolumeFailure = Failure(EngineResourceFailureKind.TransportLoss); session.RefreshVolumes(); break;
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
            Check(late.Token.IsCancellationRequested && session.NetworkSnapshot.Inspection is null && session.NetworkSnapshot.SelectedNetworkId is null
                && session.NetworkSnapshot.SelectedNetworkId is null && session.NetworkSnapshot.Networks.Count == 0
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

    public static async Task NetworkDeadlineRecoveryAndCapabilityCoexistence()
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
        session.SelectNetwork(A); var late = await old.NextInspection();
        session.RefreshNetworks(); // Supersedes the active detail but leaves all other capabilities operating.
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
        Check(client.Calls == 2 && fresh.Logs.Opened && session.NetworkSnapshot.Networks.Single().Id == B
            && session.NetworkSnapshot.Inspection is null && session.NetworkSnapshot.SelectedNetworkId is null);
    }

    private class AllCapabilitiesConnection(string version, bool honorCancellation = true)
        : ControlledConnection(version, honorCancellation), IContainerReader, IImageReader, IEngineEventSource,
            IContainerLogSource, IContainerCommands, IImageCommands, IVolumeReader
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
        public int EventCalls, ContainerListCalls, ImageListCalls, VolumeListCalls, LogCalls, CommandCalls, ImageCommandCalls;
        public EngineResourceFailure? ContainerFailure, ImageFailure, VolumeFailure;
        private int activeCommands;
        public override ValueTask DisposeAsync()
        {
            DisposedWithActiveRead |= Volatile.Read(ref activeCommands) != 0 || !Events.Disposed || Logs.Opened && !Logs.Disposed;
            return base.DisposeAsync();
        }
        public Task<EngineResourceResult<IReadOnlyList<ContainerSummary>>> ListContainersAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref ContainerListCalls); return Task.FromResult(ContainerFailure is { } failure ? EngineResourceResult<IReadOnlyList<ContainerSummary>>.Failed(failure)
                : EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success(new[] { new ContainerSummary(ContainerId,
                    null, null, ImageId, "exited", null, null, null, null, null, null) })); }
        public Task<EngineResourceResult<ContainerInspection>> InspectContainerAsync(string identity, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<ContainerInspection>.Success(new(identity, null, null, ImageId, null,
                new("exited", false, false, false, false, 0, null, null, null, null), null, null, null, "{}")));
        public Task<EngineResourceResult<IReadOnlyList<ImageSummary>>> ListImagesAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref ImageListCalls); return Task.FromResult(ImageFailure is { } failure ? EngineResourceResult<IReadOnlyList<ImageSummary>>.Failed(failure)
                : EngineResourceResult<IReadOnlyList<ImageSummary>>.Success(new[] { new ImageSummary(ImageId, null, null, null, null) })); }
        public Task<EngineResourceResult<ImageInspection>> InspectImageAsync(string identity, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<ImageInspection>.Success(new(identity, null, null, null, null, null, null, null, null, null, null, "{}")));
        public Task<EngineResourceResult<VolumeListing>> ListVolumesAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref VolumeListCalls); return Task.FromResult(VolumeFailure is { } failure ? EngineResourceResult<VolumeListing>.Failed(failure)
            : EngineResourceResult<VolumeListing>.Success(new(new[] { new VolumeSummary("exact-volume", "local", "local", null, null) }, null))); }
        public Task<EngineResourceResult<VolumeInspection>> InspectVolumeAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(EngineResourceResult<VolumeInspection>.Success(new(name, "local", "local", null, null, "{}")));
        public Task<EngineEventSubscriptionAttempt> SubscribeEventsAsync(EngineEventCursor? since = null, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref EventCalls); EventToken = cancellationToken; return Task.FromResult(EngineEventSubscriptionAttempt.Subscribed(Events)); }
        public Task<ContainerLogSubscriptionAttempt> OpenLogsAsync(ContainerLogRequest request, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref LogCalls); LogToken = cancellationToken; Logs.Opened = true; return Task.FromResult(ContainerLogSubscriptionAttempt.Subscribed(Logs)); }
        public async Task<ContainerCommandResult> StartContainerAsync(string identity, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CommandCalls); Interlocked.Increment(ref activeCommands); CommandToken = cancellationToken; CommandStarted.TrySetResult();
            try { return await CommandCompletion.Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref activeCommands); }
        }
        public async Task<ImageCommandResult> DeleteImageAsync(string reference, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ImageCommandCalls); Interlocked.Increment(ref activeCommands); ImageCommandToken = cancellationToken; ImageCommandStarted.TrySetResult();
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
    private class ControlledConnection(string version, bool honorCancellation = true) : IEngineConnection, INetworkReader
    {
        private readonly Channel<ListCall> lists = Channel.CreateUnbounded<ListCall>();
        private readonly Channel<InspectionCall> inspections = Channel.CreateUnbounded<InspectionCall>();
        private int active;
        public int MaximumConcurrent, DisposeCount, ListCalls, InspectionCalls;
        public bool DisposedWithActiveRead;
        public EngineConnectionFailure? PingFailure;
        public virtual EngineMetadata Metadata => new MissingConnection().Metadata with { EngineVersion = version };
        public Task<EngineConnectionFailure?> CheckLivenessAsync(CancellationToken cancellationToken = default) => Task.FromResult(PingFailure);
        public virtual ValueTask DisposeAsync()
        { DisposedWithActiveRead |= Volatile.Read(ref active) != 0; Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
        public async Task<EngineResourceResult<IReadOnlyList<NetworkSummary>>> ListNetworksAsync(CancellationToken cancellationToken = default)
        {
            Enter(); Interlocked.Increment(ref ListCalls);
            try
            {
                var call = new ListCall(cancellationToken); lists.Writer.TryWrite(call);
                return await (honorCancellation ? call.Completion.Task.WaitAsync(cancellationToken) : call.Completion.Task);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public async Task<EngineResourceResult<NetworkInspection>> InspectNetworkAsync(string name, CancellationToken cancellationToken = default)
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
        public TaskCompletionSource<EngineResourceResult<IReadOnlyList<NetworkSummary>>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void SucceedNames(params string[] names) => Succeed(names.Select(Row).ToArray());
        public void Succeed(IReadOnlyList<NetworkSummary> value) => Completion.SetResult(EngineResourceResult<IReadOnlyList<NetworkSummary>>.Success(value));
        public void Fail(EngineResourceFailure value) => Completion.SetResult(EngineResourceResult<IReadOnlyList<NetworkSummary>>.Failed(value));
    }
    private sealed class InspectionCall(string name, CancellationToken token)
    {
        public string Id { get; } = name;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<EngineResourceResult<NetworkInspection>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed(NetworkInspection value) => Completion.SetResult(EngineResourceResult<NetworkInspection>.Success(value));
        public void Fail(EngineResourceFailure value) => Completion.SetResult(EngineResourceResult<NetworkInspection>.Failed(value));
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
