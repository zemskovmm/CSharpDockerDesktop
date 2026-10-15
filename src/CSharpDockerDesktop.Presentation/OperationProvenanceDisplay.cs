using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Presentation;

public static class OperationProvenanceDisplay
{
    public static string Describe(EngineEndpoint? endpoint, string? engineId, long generation, long? currentGeneration)
        => $"Originating endpoint: {ConnectionEditorView.Preview(endpoint?.Address)}\nOriginating transport: {endpoint?.Transport.ToString() ?? "unavailable"}\nOriginating Engine identity: {ConnectionEditorView.Preview(string.IsNullOrWhiteSpace(engineId) ? null : engineId)}\n"
            + (currentGeneration is null ? "Result belongs to the originating endpoint and connection generation."
                : currentGeneration == generation ? "Result belongs to the current connection generation."
                : "Historical result from a previous connection; it does not describe the currently selected Engine.");
}
