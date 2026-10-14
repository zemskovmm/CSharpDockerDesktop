using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal sealed partial class FixtureBrowser : IImageOperations
{
    private ImageOperationSnapshot? imageOperation;
    public ImageOperationSnapshot? ImageOperation => Volatile.Read(ref imageOperation);
    public int ImageCommands, ImageScenario;
    public ImageOperationRequest? LastImageRequest;
    public event EventHandler? ImageOperationChanged;
    public async Task<ImageOperationSnapshot> ExecuteImageOperationAsync(ImageOperationRequest request, CancellationToken cancellationToken = default)
    {
        int scenario;
        ImageOperationSnapshot current;
        lock (imageGate)
        {
            if (imageOperation?.Pending == true || imageSnapshot.State != ResourceReadState.Ready
                || request.Target.ConnectionGeneration != imageSnapshot.ConnectionGeneration || !imageSnapshot.Images.Any(row => row.Id == request.Target.Id))
                return new(0, request, ImageOperationPhase.Completed, new(ImageCommandDisposition.NotDispatched,
                    Failure: new(ImageCommandFailureKind.StaleConnection, "Synthetic admission rejected.")), ImageOperationResolution.NotDispatched);
            scenario = ImageScenario; LastImageRequest = request;
            current = new(++ImageCommands, request, ImageOperationPhase.CommandPending); imageOperation = current;
        }
        ImageOperationChanged?.Invoke(this, EventArgs.Empty);
        await Task.Delay(3000).ConfigureAwait(false);
        var outcome = scenario switch
        {
            1 => new ImageCommandResult(ImageCommandDisposition.DaemonResponse, 409, "fixture-sensitive image dependency conflict",
                Failure: new(ImageCommandFailureKind.Conflict, "Synthetic dependency conflict: image used by a container.")),
            2 => new ImageCommandResult(ImageCommandDisposition.Uncertain,
                Failure: new(ImageCommandFailureKind.TransportFailure, "Synthetic lost delete response.")),
            4 => new ImageCommandResult(ImageCommandDisposition.NotDispatched,
                Failure: new(ImageCommandFailureKind.Canceled, "Synthetic cancellation before dispatch.")),
            _ => new ImageCommandResult(ImageCommandDisposition.Acknowledged, 200,
                RemovalRecords: [new("fixture-sensitive repo:tag", request.Target.Id)])
        };
        current = current with { Phase = outcome.RequiresReconciliation ? ImageOperationPhase.Reconciling : ImageOperationPhase.Completed,
            CommandOutcome = outcome, Resolution = outcome.RequiresReconciliation ? null : ImageOperationResolution.NotDispatched };
        lock (imageGate) imageOperation = current;
        ImageOperationChanged?.Invoke(this, EventArgs.Empty);
        if (!outcome.RequiresReconciliation) return current;
        await Task.Delay(3000).ConfigureAwait(false);
        lock (imageGate)
        {
            bool same = request.Target.ConnectionGeneration == imageSnapshot.ConnectionGeneration;
            var resolution = !same || scenario == 2 ? ImageOperationResolution.Unresolved
                : scenario is 1 or 3 ? ImageOperationResolution.ObservedPresent : ImageOperationResolution.ObservedAbsent;
            if (resolution == ImageOperationResolution.ObservedAbsent)
            {
                if (imageSnapshot.SelectedImageId == request.Target.Id) CancelImage();
                imageSnapshot = imageSnapshot with { Images = imageSnapshot.Images.Where(row => row.Id != request.Target.Id).ToArray(),
                    SelectedImageId = imageSnapshot.SelectedImageId == request.Target.Id ? null : imageSnapshot.SelectedImageId,
                    Inspection = null, InspectionState = ResourceReadState.NotRequested };
            }
            current = current with { Phase = ImageOperationPhase.Completed, Resolution = resolution,
                ObservedExists = resolution == ImageOperationResolution.Unresolved ? null : resolution == ImageOperationResolution.ObservedPresent,
                ObservedConnectionGeneration = resolution == ImageOperationResolution.Unresolved ? null : imageSnapshot.ConnectionGeneration,
                ObservationFailure = resolution == ImageOperationResolution.Unresolved ? new(EngineResourceFailureKind.TransportLoss,
                    "Synthetic image observation unavailable.", DaemonMessage: "fixture-sensitive image observation message") : null };
            imageOperation = current;
        }
        ImagesChanged?.Invoke(this, EventArgs.Empty); ImageOperationChanged?.Invoke(this, EventArgs.Empty);
        return current;
    }
}
