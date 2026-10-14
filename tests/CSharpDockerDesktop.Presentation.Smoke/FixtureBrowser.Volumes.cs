using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal sealed partial class FixtureBrowser : IVolumeBrowser
{
    private readonly object volumeGate = new();
    private CancellationTokenSource? volumeInspection;
    private int volumeRevision;
    public int VolumeCancellations;
    public int VolumeScenario;
    private static readonly IReadOnlyList<VolumeSummary> volumeRows = Enumerable.Range(0, 20000).Select(i => new VolumeSummary(
        i == 0 ? "Volume-Exact-Name" : i == 1 ? "volume-exact-name" : $"volume-{i:D5}",
        i % 3 == 0 ? null : "local", i % 5 == 0 ? "" : "local", i % 4 == 0 ? null : $"/fixture/volumes/{i}/_data",
        i % 6 == 0 ? null : i % 6 == 1 ? new Dictionary<string, string>() : new Dictionary<string, string> { ["fixture"] = "label-" + i, ["empty"] = "" })).ToArray();
    private VolumeBrowsingSnapshot volumeSnapshot = new(1, ResourceReadState.Ready, volumeRows,
        LatestListing: new(volumeRows, []), Completeness: VolumeListCompleteness.Complete);
    public VolumeBrowsingSnapshot VolumeSnapshot => Volatile.Read(ref volumeSnapshot);
    public event EventHandler? VolumesChanged;
    public void RefreshVolumes()
    {
        // Models accepted Application evidence, including retained rows and original warning replacement.
        lock (volumeGate)
        {
            var listing = VolumeScenario switch
            {
                1 => new VolumeListing([], []),
                2 => new VolumeListing([], ["  fixture PRIVATE warning\n  whitespace  ", "", "  fixture PRIVATE warning\n  whitespace  "]),
                3 => new VolumeListing(null, ["fixture null-list warning"]),
                4 => new VolumeListing(null, null),
                _ => new VolumeListing(volumeRows, [])
            };
            bool failed = VolumeScenario == 5;
            bool complete = !failed && listing.Volumes is not null && !listing.HasWarnings;
            var rows = complete ? listing.Volumes! : volumeSnapshot.Volumes;
            bool disappeared = complete && volumeSnapshot.SelectedVolumeName is { } name && !rows.Any(r => r.Name == name);
            if (disappeared) CancelVolume();
            volumeSnapshot = volumeSnapshot with
            {
                State = failed ? ResourceReadState.Failed : listing.Volumes is null ? ResourceReadState.Unavailable : ResourceReadState.Ready,
                Volumes = rows, LatestListing = failed ? volumeSnapshot.LatestListing : listing,
                Completeness = failed || listing.Volumes is null ? VolumeListCompleteness.Unavailable : complete ? VolumeListCompleteness.Complete : VolumeListCompleteness.Incomplete,
                HasRetainedRows = !complete && rows.Count > 0,
                Failure = failed ? new(EngineResourceFailureKind.AccessDenied, "Synthetic list access denied", 403, "  original PRIVATE daemon message  ") : null,
                SelectedVolumeName = disappeared ? null : volumeSnapshot.SelectedVolumeName,
                Inspection = disappeared ? null : volumeSnapshot.Inspection,
                InspectionState = disappeared ? ResourceReadState.NotRequested : volumeSnapshot.InspectionState
            };
        }
        VolumesChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SelectVolume(string? name)
    {
        lock (volumeGate) if (StringComparer.Ordinal.Equals(volumeSnapshot.SelectedVolumeName, name)) return;
        RequestVolume(name);
    }
    private void RequestVolume(string? name)
    {
        CancellationToken token; int revision; long generation;
        lock (volumeGate)
        {
            CancelVolume();
            volumeSnapshot = volumeSnapshot with { SelectedVolumeName = name, Inspection = null, InspectionState = ResourceReadState.Loading, InspectionFailure = null };
            volumeInspection = new(); token = volumeInspection.Token; revision = volumeRevision; generation = volumeSnapshot.ConnectionGeneration;
        }
        VolumesChanged?.Invoke(this, EventArgs.Empty);
        if (name is not null) _ = CompleteVolume(name, token, revision, generation);
    }
    private async Task CompleteVolume(string name, CancellationToken token, int revision, long generation)
    {
        try
        {
            await Task.Delay(2500, token).ConfigureAwait(false);
            string raw = "{\"Name\":" + System.Text.Json.JsonSerializer.Serialize(name) + ",\"FixtureRevision\":" + revision + ",\"Driver\":\"local\",\"Scope\":\"\",\"Mountpoint\":null,\"Labels\":{\"TOKEN\":\"fixture-sensitive\",\"empty\":\"\"},\"Unknown\":{\"Empty\":[],\"n\":12345678901234567890.123456789,\"n\":null},\"LargeArray\":[" + string.Join(",", Enumerable.Range(0, 50000).Select(i => "{\"index\":" + i + ",\"value\":\"fixture-" + i + "\"}")) + "]}";
            lock (volumeGate)
            {
                if (token.IsCancellationRequested || revision != volumeRevision || generation != volumeSnapshot.ConnectionGeneration || volumeSnapshot.SelectedVolumeName != name) return;
                volumeSnapshot = volumeSnapshot with { InspectionState = ResourceReadState.Ready,
                    Inspection = new(name, "local", "", null, new Dictionary<string, string> { ["TOKEN"] = "fixture-sensitive", ["empty"] = "" }, raw) };
            }
            VolumesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void RefreshVolumeInspection() => RequestVolume(VolumeSnapshot.SelectedVolumeName);
    public void ClearVolumeInspection()
    {
        lock (volumeGate) { CancelVolume(); volumeSnapshot = volumeSnapshot with { InspectionState = ResourceReadState.NotRequested, Inspection = null, InspectionFailure = null }; }
        VolumesChanged?.Invoke(this, EventArgs.Empty);
    }
    public void InvalidateSelectedVolume()
    { if (VolumeSnapshot.SelectedVolumeName is { } name && VolumeSnapshot.InspectionState != ResourceReadState.NotRequested) RequestVolume(name); }
    private void DisconnectVolumes()
    {
        lock (volumeGate) { CancelVolume(); volumeSnapshot = new(volumeSnapshot.ConnectionGeneration + 1, ResourceReadState.Unavailable, []); }
        VolumesChanged?.Invoke(this, EventArgs.Empty);
    }
    private void CancelVolume()
    {
        ++volumeRevision;
        if (volumeInspection is not { } pending) return;
        ++VolumeCancellations; pending.Cancel(); pending.Dispose(); volumeInspection = null;
    }
}
