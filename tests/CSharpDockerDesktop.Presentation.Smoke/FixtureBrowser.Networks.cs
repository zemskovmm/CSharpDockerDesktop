using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal sealed partial class FixtureBrowser : INetworkBrowser
{
    private readonly object networkGate = new();
    private CancellationTokenSource? networkInspection;
    private int networkRevision;
    public int NetworkCancellations;
    public int NetworkScenario;
    private static readonly IReadOnlyList<NetworkSummary> networkRows = Enumerable.Range(0, 20000).Select(i => new NetworkSummary(
        i == 0 ? "Opaque-NETWORK-é " : i == 1 ? "opaque-NETWORK-é " : $"network-full-opaque-{i:D5}",
        i == 2 ? "bridge" : i == 3 ? "host" : i == 4 ? "none" : $"network-{i:D5}", i % 3 == 0 ? null : "bridge", i % 4 == 0 ? "" : "local", i % 5 == 0 ? null : i % 2 == 0,
        i % 3 == 0 ? null : new Dictionary<string, string> { ["fixture"] = "label-" + i, ["empty"] = "" })).ToArray();
    private NetworkBrowsingSnapshot networkSnapshot = new(1, ResourceReadState.Ready, networkRows);
    public NetworkBrowsingSnapshot NetworkSnapshot => Volatile.Read(ref networkSnapshot);
    public event EventHandler? NetworksChanged;
    public void RefreshNetworks()
    {
        lock (networkGate)
        {
            if (NetworkScenario == 3) { CancelNetwork(); networkSnapshot = new(networkSnapshot.ConnectionGeneration + 1, ResourceReadState.Unavailable, []); }
            else if (NetworkScenario == 2) networkSnapshot = networkSnapshot with { State = ResourceReadState.Failed, Failure = new(EngineResourceFailureKind.AccessDenied, "Synthetic network list denied", 403, "  PRIVATE list daemon message\n  ") };
            else
            {
                var rows = NetworkScenario == 1 ? Array.Empty<NetworkSummary>() : networkRows;
                bool gone = networkSnapshot.SelectedNetworkId is { } id && !rows.Any(r => StringComparer.Ordinal.Equals(r.Id, id));
                if (gone) CancelNetwork();
                networkSnapshot = networkSnapshot with { State = ResourceReadState.Ready, Networks = rows, Failure = null,
                    SelectedNetworkId = gone ? null : networkSnapshot.SelectedNetworkId, Inspection = gone ? null : networkSnapshot.Inspection,
                    InspectionState = gone ? ResourceReadState.NotRequested : networkSnapshot.InspectionState };
            }
        }
        if (NetworkScenario == 3) InvalidateNetworkPreparation(NetworkPreparationFailureKind.ConnectionChanged);
        NetworksChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SelectNetwork(string? id)
    {
        lock (networkGate) if (StringComparer.Ordinal.Equals(networkSnapshot.SelectedNetworkId, id)) return;
        RequestNetwork(id);
    }
    private void RequestNetwork(string? id)
    {
        CancellationToken token; int revision; long generation;
        lock (networkGate)
        {
            CancelNetwork(); networkSnapshot = networkSnapshot with { SelectedNetworkId = id, Inspection = null, InspectionState = ResourceReadState.Loading, InspectionFailure = null };
            networkInspection = new(); token = networkInspection.Token; revision = networkRevision; generation = networkSnapshot.ConnectionGeneration;
        }
        NetworksChanged?.Invoke(this, EventArgs.Empty);
        if (id is not null) _ = CompleteNetwork(id, token, revision, generation);
    }
    private async Task CompleteNetwork(string id, CancellationToken token, int revision, long generation)
    {
        try
        {
            await Task.Delay(2500, token).ConfigureAwait(false);
            var attachments = Enumerable.Range(0, 20000).ToDictionary(i => $"container-full-identity-{i:D5}", i => new NetworkAttachment(
                i == 0 ? "" : "fixture-container-" + i, "endpoint-full-identity-" + i, null, "10.1.0.2/16", i == 0 ? "" : "2001:db8::2/64"));
            token.ThrowIfCancellationRequested();
            var ipam = new NetworkIpam("default", [new("10.1.0.0/16", "", "10.1.0.1", new Dictionary<string, string> { ["dns"] = "10.1.0.53" })], null);
            string raw = System.Text.Json.JsonSerializer.Serialize(new { Id = id, Name = "", Driver = "bridge", Scope = (string?)null, Internal = false,
                IPAM = ipam, Containers = attachments, Sensitive = "fixture-sensitive", FixtureRevision = revision,
                LargeArray = Enumerable.Range(0, 50000).Select(i => new { index = i, value = "fixture-" + i }) });
            lock (networkGate)
            {
                if (token.IsCancellationRequested || revision != networkRevision || generation != networkSnapshot.ConnectionGeneration || !StringComparer.Ordinal.Equals(networkSnapshot.SelectedNetworkId, id)) return;
                networkSnapshot = networkSnapshot with { InspectionState = NetworkScenario == 4 ? ResourceReadState.Failed : ResourceReadState.Ready,
                    InspectionFailure = NetworkScenario == 4 ? new(EngineResourceFailureKind.AccessDenied, "Synthetic network inspection denied", 403, "PRIVATE inspection daemon message") : null,
                    Inspection = NetworkScenario == 4 ? null : new(id, "", "bridge", null, false, new Dictionary<string, string>(), ipam, attachments, raw) };
            }
            NetworksChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void RefreshNetworkInspection() => RequestNetwork(NetworkSnapshot.SelectedNetworkId);
    public void ClearNetworkInspection()
    {
        lock (networkGate) { CancelNetwork(); networkSnapshot = networkSnapshot with { Inspection = null, InspectionState = ResourceReadState.NotRequested, InspectionFailure = null }; }
        NetworksChanged?.Invoke(this, EventArgs.Empty);
    }
    public void InvalidateSelectedNetwork()
    { InvalidateNetworkPreparation(NetworkPreparationFailureKind.ObservedChange); if (NetworkSnapshot.SelectedNetworkId is { } id && NetworkSnapshot.InspectionState != ResourceReadState.NotRequested) RequestNetwork(id); }
    private void DisconnectNetworks()
    { NetworkScenario = 3; RefreshNetworks(); }
    private void CancelNetwork()
    {
        ++networkRevision; if (networkInspection is not { } pending) return;
        ++NetworkCancellations; pending.Cancel(); pending.Dispose(); networkInspection = null;
    }
}
