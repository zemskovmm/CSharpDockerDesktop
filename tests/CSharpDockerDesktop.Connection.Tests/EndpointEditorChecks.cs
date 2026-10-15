using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;
using CSharpDockerDesktop.Presentation;

internal static partial class EndpointSelectionChecks
{
    public static async Task EditorDraftAndDefault()
    {
        var client = new Client((endpoint, _, _) => Task.FromResult(EngineConnectionAttempt.Connected(new Connection(endpoint, "fixture"))));
        await using var s = new DesktopSession(client, new() { MaximumRecoveryAttempts = 0 }, new Clock());
        var editor = new ConnectionEditorView(s);
        Check(editor.Draft == EngineEndpoint.LocalWindows.Address && !editor.HasUnappliedDraft && client.Calls == 0);
        s.Start(); await Ready(s, EngineEndpoint.LocalWindows);
        editor.Draft = B.Address;
        Check(editor.HasUnappliedDraft && editor.DraftStatus.Contains("Unapplied") && client.Calls == 1 && s.Snapshot.Endpoint == EngineEndpoint.LocalWindows);
        editor.Apply(); await Ready(s, B);
        Check(!editor.HasUnappliedDraft && editor.Selection.Contains("Explicit") && editor.Details.Contains("Actual endpoint: " + B.Address));
        editor.Draft = "unix:///draft-only";
        long previous = s.ContainerSnapshot.ConnectionGeneration;
        editor.Retry(); await Observe(s, () => s.ContainerSnapshot.ConnectionGeneration > previous && s.ContainerSnapshot.State == ResourceReadState.Ready);
        Check(client.Endpoints.Last() == B && editor.HasUnappliedDraft && editor.Draft == "unix:///draft-only", "Retry must preserve the unapplied draft and connect to the selected address");
        editor.UseDefault(); await Ready(s, EngineEndpoint.LocalWindows);
        Check(editor.Draft == EngineEndpoint.LocalWindows.Address && !editor.HasUnappliedDraft && editor.Selection.Contains("Default local"));
        int calls = client.Calls;
        editor.Draft = ""; editor.Apply(); editor.Draft = new('x', ConnectionEditorView.MaximumAddressCharacters + 1); editor.Apply();
        Check(!editor.CanApply && client.Calls == calls && s.Snapshot.Endpoint == EngineEndpoint.LocalWindows);
        Check(ConnectionEditorView.Preview(new string('x', 100_000)).Length < 2100);
    }

    public static async Task EditorCleanupAndLateNotifications()
    {
        var late = Source<EngineResourceResult<IReadOnlyList<ContainerSummary>>>(); var entered = Source<bool>();
        var old = new Connection(A, "old") { DisposeRelease = Source<bool>() };
        var finalEndpoint = new EngineEndpoint("npipe://./pipe/final", EngineTransport.WindowsNamedPipe);
        var next = new Connection(finalEndpoint, "latest");
        var client = new Client((_, call, _) => Task.FromResult(EngineConnectionAttempt.Connected(call == 1 ? old : next)));
        await using var s = Session(client); var editor = new ConnectionEditorView(s); s.Start(); await Ready(s, A);
        old.List = _ => { entered.TrySetResult(true); return late.Task; };
        s.RefreshContainers(); await entered.Task.WaitAsync(Limit);
        editor.Draft = B.Address; editor.Apply();
        Check(s.Snapshot.State == EngineConnectionState.Connecting && s.Snapshot.Endpoint == B && s.ContainerSnapshot.Containers.Count == 0);
        Check(editor.Diagnostic.Contains(B.Address) && !editor.Details.Contains("old"));
        late.SetResult(EngineResourceResult<IReadOnlyList<ContainerSummary>>.Success([Row("late obsolete resource")]));
        await old.DisposeStarted.Task.WaitAsync(Limit);
        for (int i = 0; i < 100; ++i) { editor.Draft = $"npipe://./pipe/queued_{i}"; editor.Apply(); }
        editor.Draft = finalEndpoint.Address; editor.Apply(); editor.Draft = "unapplied final draft";
        editor.Retry(); editor.Refresh();
        Check(client.Calls == 1 && editor.Draft == "unapplied final draft" && editor.Snapshot.Endpoint == finalEndpoint
            && editor.Diagnostic.Contains(finalEndpoint.Address) && editor.Details.Contains("Actual endpoint: unavailable"));
        old.DisposeRelease.SetResult(true); await Ready(s, finalEndpoint);
        editor.Refresh(); // A queued old notification reads current reality rather than its old sender's content.
        Check(client.Calls == 2 && client.MaximumConcurrent == 1 && editor.Details.Contains(finalEndpoint.Address)
            && s.ContainerSnapshot.Containers.Single().Names![0] == "latest" && !editor.Details.Contains("obsolete"));
    }

    public static async Task EditorEvidenceOwnership()
    {
        string original = "private daemon content " + new string('x', 20000);
        var client = new Client((_, _, _) => Task.FromResult(EngineConnectionAttempt.Failed(new(EngineConnectionFailureKind.AccessDenied, "Denied", DaemonMessage: original))));
        await using var s = Session(client); var editor = new ConnectionEditorView(s); s.Start();
        await Observe(s, () => s.Snapshot.State == EngineConnectionState.Unavailable);
        editor.RevealDaemonMessage(); Check(editor.ShowingDaemonMessage && editor.DaemonMessage.StartsWith("private") && editor.DaemonMessage.Length < 16500);
        Check(!editor.Diagnostic.Contains("private") && !editor.Details.Contains("private"));
        editor.Draft = B.Address; editor.Apply();
        await Observe(s, () => s.Snapshot.Endpoint == B && s.Snapshot.State == EngineConnectionState.Unavailable);
        editor.Refresh(); Check(!editor.ShowingDaemonMessage && editor.DaemonMessage == "" && editor.HasDaemonMessage);
        editor.RevealDaemonMessage(); Check(editor.ShowingDaemonMessage);
        editor.Retry(); await Observe(s, () => client.Calls == 3 && s.Snapshot.State == EngineConnectionState.Unavailable);
        editor.Refresh(); Check(!editor.ShowingDaemonMessage && editor.DaemonMessage == "");
        editor.RevealDaemonMessage(); editor.HideDaemonMessage(); Check(editor.DaemonMessage == "");
    }

    public static async Task EditorUnsupportedInput()
    {
        var transport = new RejectTransport(); var client = new DockerEngineConnectionClient(transport);
        await using var s = new DesktopSession(client, new() { MaximumRecoveryAttempts = 0 });
        var editor = new ConnectionEditorView(s);
        foreach (string address in new[] { "unix:///var/run/docker.sock", "npipe://remote/pipe/docker_engine", "tcp://localhost:2375", "ssh://host", "npipe://./pipe/", "   " })
        {
            editor.Draft = address; editor.Apply();
            await Observe(s, () => s.Snapshot.State == EngineConnectionState.Unsupported);
            Check(s.Snapshot.Endpoint.Address == address && s.Snapshot.Failure?.Kind == EngineConnectionFailureKind.UnsupportedEndpoint
                && transport.Creates == 0 && editor.Details.Contains(address) && !editor.Diagnostic.Contains("Opening the default"));
            var previous = s.Snapshot;
            editor.Draft = A.Address; editor.Retry();
            await Observe(s, () => s.Snapshot.State == EngineConnectionState.Unsupported && !ReferenceEquals(previous, s.Snapshot));
            Check(s.Snapshot.Endpoint.Address == address && transport.Creates == 0);
        }
    }

    public static Task OperationPresentationProvenance()
    {
        var container = new ContainerOperationSnapshot(1, new(new(1, Id), ContainerCommand.Start), ContainerOperationPhase.Completed) { Endpoint = A, EngineId = "same-engine-id" };
        var image = new ImageOperationSnapshot(1, new(new(1, ImageId)), ImageOperationPhase.Completed) { Endpoint = A, EngineId = "same-engine-id" };
        var volume = new VolumeOperationSnapshot(1, new(new(1, "shared"), new(1, 1)), VolumeOperationPhase.Completed) { Endpoint = A, EngineId = "same-engine-id" };
        var network = new NetworkOperationSnapshot(1, new(new(1, Id), new(1, 1)), NetworkOperationPhase.Completed) { Endpoint = A, EngineId = "same-engine-id" };
        var original = new[] { ContainerOperationDisplay.Status(container, 2), ImageOperationDisplay.Status(image, 2), VolumeOperationDisplay.Status(volume, 2), NetworkOperationDisplay.Status(network, 2) };
        foreach (string status in original)
            Check(status.Contains(A.Address) && status.Contains("Originating Engine identity: same-engine-id") && status.Contains("Historical result"));
        foreach (string status in new[] { ContainerOperationDisplay.Status(container with { Endpoint = B }, 2), ImageOperationDisplay.Status(image with { Endpoint = B }, 2), VolumeOperationDisplay.Status(volume with { Endpoint = B }, 2), NetworkOperationDisplay.Status(network with { Endpoint = B }, 2) })
            Check(status.Contains(B.Address) && !original.Contains(status), "Overlapping resource and Engine identities must retain distinct originating endpoints");
        foreach (string status in new[] { ContainerOperationDisplay.Status(container with { Endpoint = null, EngineId = null }), ImageOperationDisplay.Status(image with { EngineId = null }), VolumeOperationDisplay.Status(volume with { EngineId = null }), NetworkOperationDisplay.Status(network with { EngineId = null }) })
            Check(status.Contains("Originating Engine identity: unavailable"));
        Check(ContainerOperationDisplay.Status(container, 1).Contains("current connection generation"));
        return Task.CompletedTask;
    }
}
