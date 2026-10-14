using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal sealed partial class FixtureBrowser : IImageBrowser
{
    private readonly object imageGate = new();
    private CancellationTokenSource? imageInspection;
    private int imageRevision;
    public int ImageCancellations;
    private ImageBrowsingSnapshot imageSnapshot = new(1, ResourceReadState.Ready,
        Enumerable.Range(0, 20000).Select(i => new ImageSummary("sha256:" + i.ToString("x64"),
            i % 3 == 0 ? null : [$"example/image-{i}:latest", $"example/image-{i}:other"],
            i % 3 == 0 ? null : [$"example/image-{i}@sha256:fixture-digest"],
            i % 5 == 0 ? null : DateTimeOffset.UnixEpoch.AddHours(i), i % 4 == 0 ? null : i * 1024L)).ToArray());
    public ImageBrowsingSnapshot ImageSnapshot => Volatile.Read(ref imageSnapshot);
    public event EventHandler? ImagesChanged;
    public void RefreshImages()
    {
        lock (imageGate) imageSnapshot = imageSnapshot with { State = ResourceReadState.Ready };
        ImagesChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SelectImage(string? id)
    {
        CancellationToken token;
        int revision;
        long generation;
        lock (imageGate)
        {
            CancelImage();
            imageSnapshot = imageSnapshot with { SelectedImageId = id, Inspection = null, InspectionState = ResourceReadState.Loading };
            imageInspection = new(); token = imageInspection.Token; revision = imageRevision; generation = imageSnapshot.ConnectionGeneration;
        }
        ImagesChanged?.Invoke(this, EventArgs.Empty);
        if (id is not null) _ = CompleteImage(id, token, revision, generation);
    }
    private async Task CompleteImage(string id, CancellationToken token, int revision, long generation)
    {
        try
        {
            await Task.Delay(2500, token).ConfigureAwait(false);
            string raw = "{\"FixtureRevision\":" + revision + ",\"Image\":" + InspectFixture(id) + "}";
            lock (imageGate)
            {
                if (token.IsCancellationRequested || revision != imageRevision || generation != imageSnapshot.ConnectionGeneration || imageSnapshot.SelectedImageId != id) return;
                imageSnapshot = imageSnapshot with { InspectionState = ResourceReadState.Ready,
                    Inspection = new(id, imageSnapshot.Images.Single(row => row.Id == id).RepoTags, [], DateTimeOffset.UnixEpoch, 12345678,
                        "linux", null, "arm64", "v8", new(null, ["TOKEN=fixture-sensitive"], ["fixture", "run"], [], "/app", null, ["80/tcp"], [], null, null, null, false, "{}"),
                        new("layers", ["sha256:ordered-first", "sha256:ordered-second"]), raw) };
            }
            ImagesChanged?.Invoke(this, EventArgs.Empty); // Intentionally off the UI thread.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void RefreshImageInspection() => SelectImage(ImageSnapshot.SelectedImageId);
    public void ClearImageInspection()
    {
        lock (imageGate) { CancelImage(); imageSnapshot = imageSnapshot with { InspectionState = ResourceReadState.NotRequested, Inspection = null }; }
        ImagesChanged?.Invoke(this, EventArgs.Empty);
    }
    public void InvalidateSelectedImage()
    {
        // Simulates the Application snapshot resulting from an external selected-image event.
        // It clears exposed Inspect immediately, then starts an authoritative delayed replacement.
        if (ImageSnapshot.SelectedImageId is { } id && ImageSnapshot.InspectionState != ResourceReadState.NotRequested) SelectImage(id);
    }
    private void DisconnectImages()
    {
        lock (imageGate) { CancelImage(); imageSnapshot = new(imageSnapshot.ConnectionGeneration + 1, ResourceReadState.Unavailable, []); }
        ImagesChanged?.Invoke(this, EventArgs.Empty);
    }
    private void CancelImage()
    {
        ++imageRevision;
        if (imageInspection is not { } pending) return;
        ++ImageCancellations; pending.Cancel(); pending.Dispose(); imageInspection = null;
    }
}
