using System.Collections.Immutable;
using System.Text;
using System.Threading.Channels;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

// Research fixture only. No Engine, shell, disk transcript, or command default.
internal sealed class ControlledTerminal : IContainerTerminal
{
    private Channel<byte[]> output = NewOutput();
    private CancellationTokenSource lifetime = new();
    private long nextId;
    public ContainerTerminalSnapshot ContainerTerminal { get; private set; } = new();
    public event EventHandler? ContainerTerminalChanged;
    public int ControlCCount { get; private set; }
    public int InputBytes { get; private set; }
    public int LastColumns { get; private set; }
    public int LastRows { get; private set; }
    public int PendingChunks => output.Reader.Count;
    public bool ProducerCompleted { get; private set; }
    private static Channel<byte[]> NewOutput() => Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public ContainerTerminalOpenResult OpenContainerTerminal(string containerId, IReadOnlyList<string> arguments, int columns, int rows)
    {
        if (ContainerTerminal.Phase != ContainerTerminalPhase.Closed || arguments.Count != 0)
            return new(ContainerTerminalAdmission.InvalidRequest);
        output = NewOutput(); lifetime.Dispose(); lifetime = new(); ProducerCompleted = false;
        LastColumns = columns; LastRows = rows; InputBytes = 0; ControlCCount = 0;
        ContainerTerminal = new(++nextId, 1, containerId, ContainerTerminalPhase.Attached, true);
        ContainerTerminalChanged?.Invoke(this, EventArgs.Empty);
        return new(ContainerTerminalAdmission.Accepted, nextId);
    }
    public ContainerTerminalEvidence? ReadContainerTerminalEvidence(long sessionId) => null;
    public bool IsContainerTerminalCurrent(long sessionId, string containerId, long connectionGeneration) =>
        ContainerTerminal.Phase == ContainerTerminalPhase.Attached && ContainerTerminal.SessionId == sessionId
        && containerId == "controlled" && connectionGeneration == 1;
    public async Task<ContainerTerminalOutput> ReadContainerTerminalOutputAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        var reader = output.Reader;
        try
        {
            var bytes = await reader.ReadAsync(cancellationToken);
            return new(sessionId, ImmutableArray.CreateRange(bytes), false,
                !IsContainerTerminalCurrent(sessionId, "controlled", 1));
        }
        catch (ChannelClosedException) { return new(sessionId, [], true, true); }
    }
    public async Task<ContainerExecWriteResult> WriteContainerTerminalAsync(long sessionId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (!IsContainerTerminalCurrent(sessionId, "controlled", 1) || bytes.Length > 4096)
            return new(ContainerExecWriteDisposition.NotWritten);
        InputBytes += bytes.Length;
        ControlCCount += bytes.Span.Count((byte)3);
        using var joined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try { await output.Writer.WriteAsync(bytes.ToArray(), joined.Token); }
        catch (OperationCanceledException) { return new(ContainerExecWriteDisposition.NotWritten); }
        return new(ContainerExecWriteDisposition.Written);
    }
    public Task<ContainerExecResizeResult> ResizeContainerTerminalAsync(long sessionId, int columns, int rows)
    {
        bool current = IsContainerTerminalCurrent(sessionId, "controlled", 1);
        if (current) { LastColumns = columns; LastRows = rows; }
        return Task.FromResult(new ContainerExecResizeResult(current ? ContainerExecResizeDisposition.Acknowledged
            : ContainerExecResizeDisposition.NotDispatched, null, columns, rows));
    }
    public Task CloseContainerTerminalAsync()
    {
        ContainerTerminal = new(); lifetime.Cancel(); output.Writer.TryComplete();
        ContainerTerminalChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
    public async Task ProduceAsync(bool sustained, int lineCount = 10000)
    {
        var writer = output.Writer; var token = lifetime.Token;
        try
        {
            // Split every UTF-8 scalar and every escape sequence across byte writes.
            foreach (byte b in Encoding.UTF8.GetBytes("UTF-8: café λ 世界 🐳\r\n\x1b[31mRED\x1b[0m normal\r\n12345\x1b[3DXYZ\r\n"))
                await writer.WriteAsync([b], token);
            if (sustained)
                for (int i = 0; i < lineCount; i += 128)
                {
                    var batch = new StringBuilder();
                    for (int j = i; j < Math.Min(i + 128, lineCount); j++) batch.Append($"bounded line {j:D5}\r\n");
                    await writer.WriteAsync(Encoding.UTF8.GetBytes(batch.ToString()), token);
                }
            // Neither OSC 8, OSC 52 nor HTML-looking output may gain host authority.
            await writer.WriteAsync(Encoding.UTF8.GetBytes("\x1b]8;;https://example.invalid/terminal-probe\x1b\\inert link\x1b]8;;\x1b\\\r\n\x1b]52;c;U0hPVUxELU5PVC1DT1BZ\a<script>alert('inert')</script>\r\n"), token);
            ProducerCompleted = true;
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }
}
