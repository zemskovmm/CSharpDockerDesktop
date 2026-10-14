using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;

internal sealed partial class FixtureBrowser : IContainerLogs
{
    private readonly object logGate = new();
    private readonly Queue<ContainerLogEntry> logEntries = new();
    private CancellationTokenSource? logLifetime;
    private Task logWork = Task.CompletedTask;
    private ContainerLogsSnapshot logSnapshot = new(0, 0, ContainerLogsPhase.Closed);
    private long logSession, logEntry;
    public int LogScenario, LogOpens, LogCloses;
    public ContainerLogsSnapshot ContainerLogs { get { lock (logGate) return logSnapshot; } }
    public event EventHandler? ContainerLogsChanged;
    public bool OpenContainerLogs(string id, bool follow = true, int? tailLines = null) => OpenContainerLogs(id, out _, follow, tailLines);
    public bool OpenContainerLogs(string id, out ContainerLogFailure? failure, bool follow = true, int? tailLines = null)
    {
        failure = null;
        _ = CloseContainerLogsAsync();
        lock (logGate)
        {
            ++LogOpens; logEntries.Clear(); logEntry = 0;
            long session = ++logSession; int scenario = LogScenario;
            logSnapshot = new(session, 0, ContainerLogsPhase.Opening, id, ContainerSnapshot.ConnectionGeneration);
            var cancellation = logLifetime = new();
            logWork = Task.Run(() => ProduceLogsAsync(session, scenario, follow, cancellation));
        }
        return true;
    }
    public ContainerLogsPage ReadContainerLogs(long afterEntryId = 0, int maximumEntries = 128)
    {
        lock (logGate) return new(logSnapshot.SessionId, logSnapshot.ViewRevision,
            logEntries.Where(e => e.EntryId > afterEntryId).Take(Math.Min(maximumEntries, 128)).ToArray());
    }
    public void ClearContainerLogs()
    {
        lock (logGate) { logEntries.Clear(); logSnapshot = logSnapshot with { ViewRevision = logSnapshot.ViewRevision + 1, RetainedEntries = 0, RetainedCharacters = 0 }; }
        ContainerLogsChanged?.Invoke(this, EventArgs.Empty);
    }
    public Task CloseContainerLogsAsync()
    {
        lock (logGate)
        {
            if (logLifetime is not null) { ++LogCloses; logLifetime.Cancel(); logLifetime = null; }
            logEntries.Clear(); logSnapshot = logSnapshot with { Phase = ContainerLogsPhase.Closed, ViewRevision = logSnapshot.ViewRevision + 1, RetainedEntries = 0, RetainedCharacters = 0 };
            return logWork;
        }
    }
    private async Task ProduceLogsAsync(long session, int scenario, bool follow, CancellationTokenSource cancellation)
    {
        try
        {
            var token = cancellation.Token;
            await Task.Delay(500, token);
            lock (logGate)
            {
                if (logSnapshot.SessionId != session || token.IsCancellationRequested) return;
                logSnapshot = logSnapshot with { Phase = ContainerLogsPhase.Streaming, IsTty = scenario == 1 };
                if (scenario is 3 or 4 or 7)
                {
                    var failure = new ContainerLogFailure(scenario == 3 ? ContainerLogFailureKind.LoggingUnavailable : scenario == 4 ? ContainerLogFailureKind.AccessDenied : ContainerLogFailureKind.MalformedOutput,
                        "Synthetic classified failure", 500, "fixture-sensitive original daemon content");
                    logSnapshot = logSnapshot with { Phase = ContainerLogsPhase.Failed, Failure = failure };
                }
            }
            ContainerLogsChanged?.Invoke(this, EventArgs.Empty);
            if (scenario is 3 or 4 or 7) return;
            int batch = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lock (logGate)
                {
                    if (logSnapshot.SessionId != session || token.IsCancellationRequested) return;
                    int count = batch == 0 ? 2048 : 32;
                    for (int i = 0; i < count; ++i)
                    {
                        long id = ++logEntry;
                        var stream = scenario == 1 ? ContainerLogStream.Combined : id % 5 == 0 ? ContainerLogStream.Stderr : ContainerLogStream.Stdout;
                        string timestamp = DateTimeOffset.UtcNow.ToString("O");
                        logEntries.Enqueue(new(id, id, stream, $"{timestamp} fixture row {id}: café 終 — {(id % 17 == 0 ? "needle" : "continuous output")}\r\n",
                            new(timestamp, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0), false, false));
                        if (logEntries.Count > 2048) { logEntries.Dequeue(); logSnapshot = logSnapshot with { EvictedEntries = logSnapshot.EvictedEntries + 1 }; }
                    }
                    logSnapshot = logSnapshot with { ViewRevision = logSnapshot.ViewRevision + 1, RetainedEntries = logEntries.Count, RetainedCharacters = logEntries.Sum(e => e.Text.Length) };
                    if (!follow || scenario == 5 && batch == 30) logSnapshot = logSnapshot with { Phase = ContainerLogsPhase.Completed };
                    if (scenario == 6 && batch == 30) logSnapshot = logSnapshot with { Phase = ContainerLogsPhase.Canceled };
                    if (scenario == 2 && batch == 30) logSnapshot = logSnapshot with { Phase = ContainerLogsPhase.Recovering, RecoveryAttempt = 1, Continuity = ContainerLogsContinuity.Uncertain };
                }
                ContainerLogsChanged?.Invoke(this, EventArgs.Empty);
                if (!follow || scenario is 5 or 6 && batch == 30) return;
                if (scenario == 2 && batch == 30)
                {
                    await Task.Delay(3000, token);
                    lock (logGate) if (logSnapshot.SessionId == session && !token.IsCancellationRequested) logSnapshot = logSnapshot with { Phase = ContainerLogsPhase.Streaming, Continuity = ContainerLogsContinuity.BoundedOverlap };
                }
                ++batch; await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { lock (logGate) { if (ReferenceEquals(logLifetime, cancellation)) logLifetime = null; cancellation.Dispose(); } }
    }
}
