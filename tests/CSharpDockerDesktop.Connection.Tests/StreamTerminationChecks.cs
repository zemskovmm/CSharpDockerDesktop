using System.Net;
using System.Text;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Infrastructure.Docker;

// Gates live entirely in the synthetic transport. No sleeps, retries or production test hooks.
internal static class StreamTerminationChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task ReaderCrossesCleanup(bool logs)
    {
        foreach (bool wait in new[] { false, true })
        {
            using var fixture = new TerminalTransport(logs);
            await using var owner = await Connect(fixture);
            var subscription = await Open(owner, logs);
            await using (subscription)
            {
                await fixture.Stream.EndReached.Task.WaitAsync(Deadline); // All records/fragments queued before EOF.
                fixture.ReaderCheck.Arm();
                var reading = Task.Run(async () => wait ? await subscription.Wait() : subscription.Read());
                try
                {
                    await fixture.ReaderCheck.Entered.Task.WaitAsync(Deadline);
                    fixture.Stream.AllowEnd.TrySetResult();
                    await subscription.Finished.WaitAsync(Deadline);
                    Check(fixture.Stream.Disposed && fixture.Handlers[1].Disposed, "Producer must close stream and dedicated pool before completing");
                }
                finally { fixture.ReaderCheck.Release.Set(); }
                Check(await reading.WaitAsync(Deadline), $"Reader lost queued {(logs ? "log" : "event")} output crossing cleanup (wait={wait})");
                if (wait) Check(subscription.Read(), "Signaled output must remain readable");
                await subscription.Drain();
                subscription.AssertOutput();
                subscription.AssertCompletion(canceled: false, replaced: false);
                Check(!fixture.Handlers[0].Disposed && await owner.CheckLivenessAsync() is null,
                    "Dedicated cleanup must preserve ordinary connection");
            }
        }
    }

    internal static async Task CancellationAndReplacement(bool logs)
    {
        foreach (bool completed in new[] { false, true })
        foreach (bool wait in new[] { false, true })
        foreach (var ending in new[] { "cancel", "subscription", "owner", "owner-generation", "stream-generation" })
        {
            using var fixture = new TerminalTransport(logs);
            using var cancel = new CancellationTokenSource();
            await using var owner = await Connect(fixture);
            await using var subscription = await Open(owner, logs, cancel.Token);
            await fixture.Stream.EndReached.Task.WaitAsync(Deadline);
            Task<bool>? reading = null;
            Task? disposal = null;
            try
            {
                if (completed)
                {
                    fixture.Stream.AllowEnd.TrySetResult();
                    await subscription.Finished.WaitAsync(Deadline);
                    subscription.AssertCompletion(canceled: false, replaced: false);
                }
                else
                {
                    fixture.Stream.AllowCleanup.Reset();
                    // Pause the final admission check, after dequeue or availability, so invalidation
                    // must also reject an item already removed from the underlying channel.
                    fixture.ReaderCheck.Arm(checks: 2);
                    reading = Task.Run(async () => wait ? await subscription.Wait() : subscription.Read());
                    await fixture.ReaderCheck.Entered.Task.WaitAsync(Deadline);
                    fixture.Stream.AllowEnd.TrySetResult();
                    // Producer has detached continuity callbacks and entered response disposal.
                    await fixture.Stream.CleanupEntered.Task.WaitAsync(Deadline);
                }
                switch (ending)
                {
                    case "cancel": cancel.Cancel(); break;
                    case "subscription": disposal = subscription.DisposeAsync().AsTask(); break;
                    case "owner": disposal = owner.DisposeAsync().AsTask(); break;
                    case "owner-generation": fixture.Counters[0].Opened(); break;
                    case "stream-generation": fixture.Counters[1].Opened(); break;
                }
                fixture.ReaderCheck.Release.Set();
                reading ??= Task.Run(async () => wait ? await subscription.Wait() : subscription.Read());
                Check(!await reading.WaitAsync(Deadline),
                    $"Termination must reject obsolete delivery (logs={logs}, completed={completed}, wait={wait}, ending={ending})");
            }
            finally
            {
                fixture.ReaderCheck.Release.Set(); fixture.Stream.AllowCleanup.Set();
            }
            await subscription.Finished.WaitAsync(Deadline);
            if (disposal is not null) await disposal.WaitAsync(Deadline);
            Check(!subscription.Read() && !await subscription.Wait(), "Terminated subscription must not retain readable stale output");
            Check(fixture.Stream.Disposed && fixture.Handlers[1].Disposed, "Termination must promptly close dedicated resources");
            // Completion describes the result at producer termination; later cancellation/replacement
            // suppresses queued delivery without rewriting an already completed result.
            subscription.AssertCompletion(canceled: !completed, replaced: !completed && ending.EndsWith("generation", StringComparison.Ordinal));
        }

        // No consumer participates in invalidation here: cleanup itself must notice a replacement
        // occurring after callback detachment and classify it before completing the producer.
        foreach (int pool in new[] { 0, 1 })
        {
            using var fixture = new TerminalTransport(logs);
            await using var owner = await Connect(fixture);
            await using var subscription = await Open(owner, logs);
            await fixture.Stream.EndReached.Task.WaitAsync(Deadline);
            fixture.Stream.AllowCleanup.Reset();
            try
            {
                fixture.Stream.AllowEnd.TrySetResult();
                await fixture.Stream.CleanupEntered.Task.WaitAsync(Deadline);
                fixture.Counters[pool].Opened();
            }
            finally { fixture.Stream.AllowCleanup.Set(); }
            await subscription.Finished.WaitAsync(Deadline);
            subscription.AssertCompletion(canceled: false, replaced: true);
            Check(!subscription.Read() && !await subscription.Wait(), "Cleanup replacement must clear queued output without a participating consumer");
            Check(fixture.Stream.Disposed && fixture.Handlers[1].Disposed, "Replaced producer must release dedicated resources");
        }
    }

    private static async Task<IEngineConnection> Connect(TerminalTransport fixture)
    {
        var result = await new DockerEngineConnectionClient(fixture, new() { EventBufferCapacity = 4, LogBufferCapacity = 8 }).ConnectAsync();
        Check(result.IsConnected, "Synthetic connection must negotiate"); return result.Connection!;
    }
    private static async Task<ObservedSubscription> Open(IEngineConnection owner, bool logs, CancellationToken token = default)
    {
        if (logs)
        {
            var result = await ((IContainerLogSource)owner).OpenLogsAsync(new("fixture", 3), token);
            Check(result.IsSubscribed, "Synthetic logs must open"); return new(result.Subscription!);
        }
        var events = await ((IEngineEventSource)owner).SubscribeEventsAsync(cancellationToken: token);
        Check(events.IsSubscribed, "Synthetic events must open"); return new(events.Subscription!);
    }

    private sealed class ObservedSubscription : IAsyncDisposable
    {
        private readonly IContainerLogSubscription? logs;
        private readonly IEngineEventSubscription? events;
        private readonly List<ContainerLogOutput> output = [];
        private readonly List<EngineEvent> observations = [];
        public ObservedSubscription(IContainerLogSubscription value) => logs = value;
        public ObservedSubscription(IEngineEventSubscription value) => events = value;
        public Task Finished => logs?.Completion ?? (Task)events!.Completion;
        public ValueTask<bool> Wait() => logs is not null ? logs.Output.WaitToReadAsync() : events!.Events.WaitToReadAsync();
        public bool Read()
        {
            if (logs is not null)
            {
                if (!logs.Output.TryRead(out var item)) return false;
                output.Add(item); return true;
            }
            if (!events!.Events.TryRead(out var observation)) return false;
            observations.Add(observation); return true;
        }
        public async Task Drain()
        {
            while (await Wait().AsTask().WaitAsync(Deadline)) while (Read()) { }
        }
        public void AssertOutput()
        {
            if (logs is not null)
            {
                Check(output.Count == 6 && string.Concat(output.Select(item => item.Text)) == "first\nsecond\nthird\n"
                    && output.Select(item => item.Sequence).SequenceEqual(Enumerable.Range(1, 6).Select(value => (long)value)),
                    $"Queued logs must drain exactly once in order (fragments={output.Count}, characters={output.Sum(item => item.Text.Length)})");
            }
            else Check(observations.Select(item => item.Action).SequenceEqual(new[] { "first", "second", "third" }),
                $"Queued events must drain exactly once in order (observations={observations.Count})");
        }
        public void AssertCompletion(bool canceled, bool replaced)
        {
            if (logs is not null)
            {
                var completion = logs.Completion.GetAwaiter().GetResult();
                Check(completion.Termination == (replaced ? ContainerLogTermination.Failed : canceled ? ContainerLogTermination.Canceled : ContainerLogTermination.CleanEnd)
                    && (!replaced || completion.Failure?.Kind == ContainerLogFailureKind.TransportLoss),
                    $"Logs terminal classification must survive cleanup (termination={completion.Termination}, failure={completion.Failure?.Kind})");
            }
            else
            {
                var failure = events!.Completion.GetAwaiter().GetResult();
                Check(failure?.Kind == (replaced ? EngineEventFailureKind.TransportLoss : canceled ? null : EngineEventFailureKind.UnexpectedEnd),
                    $"Events terminal classification must survive cleanup (failure={failure?.Kind})");
            }
        }
        public ValueTask DisposeAsync() => logs is not null ? logs.DisposeAsync() : events!.DisposeAsync();
    }

    private sealed class TerminalTransport(bool logs) : IDockerTransportFactory, IDisposable
    {
        public TerminalStream Stream { get; } = new(logs
            ? [.. LogChecks.Frame(1, "first\n"), .. LogChecks.Frame(2, "second\n"), .. LogChecks.Frame(1, "third\n")]
            : Encoding.UTF8.GetBytes(string.Concat(new[] { "first", "second", "third" }
                .Select(action => $$"""{"Type":"container","Action":"{{action}}"}""" + "\n"))));
        public ReaderGate ReaderCheck { get; } = new();
        public List<TrackingHandler> Handlers { get; } = [];
        public List<PipeConnectionCounter> Counters { get; } = [];
        public HttpMessageHandler Create(EngineEndpoint endpoint, DockerConnectionOptions options)
        {
            int pool = Handlers.Count;
            var counter = new PipeConnectionCounter(); counter.Opened(); Counters.Add(counter);
            var handler = new TrackingHandler((request, _, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/logs") || path.EndsWith("/events"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Stream) });
                string body = path switch
                {
                    "/_ping" => "OK",
                    "/version" => """{"Version":"fixture","ApiVersion":"1.56","MinAPIVersion":"1.40"}""",
                    _ when path.EndsWith("/info") => """{"ID":"fixture"}""",
                    _ => """{"Id":"fixture","Config":{"Tty":false}}"""
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new TrackedContent(body) });
            });
            Handlers.Add(handler);
            return new GatedContinuityHandler(handler, counter, pool == 1 ? ReaderCheck : null);
        }
        public void Dispose()
        {
            ReaderCheck.Release.Set(); Stream.AllowEnd.TrySetResult(); Stream.AllowCleanup.Set();
            ReaderCheck.Release.Dispose(); Stream.AllowCleanup.Dispose();
        }
    }

    private sealed class ReaderGate
    {
        private int armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void Arm(int checks = 1) => Volatile.Write(ref armed, checks);
        public void Pause()
        {
            if (Volatile.Read(ref armed) <= 0 || Interlocked.Decrement(ref armed) != 0) return;
            Entered.TrySetResult();
            if (!Release.Wait(Deadline)) throw new TimeoutException("Coordinated reader was not released");
        }
    }
    private sealed class GatedContinuityHandler(HttpMessageHandler inner, PipeConnectionCounter counter, ReaderGate? reader)
        : DelegatingHandler(inner), ITransportContinuity
    {
        public long ConnectionGeneration { get { reader?.Pause(); return counter.Generation; } }
        public bool Seal(long generation) => counter.Seal(generation);
        public event Action? Reopened { add => counter.Reopened += value; remove => counter.Reopened -= value; }
    }
    private sealed class TerminalStream(byte[] bytes) : Stream
    {
        private int position;
        public TaskCompletionSource EndReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowEnd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim AllowCleanup { get; } = new(initialState: true);
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position == bytes.Length)
            {
                EndReached.TrySetResult(); await AllowEnd.Task.WaitAsync(cancellationToken); return 0;
            }
            int count = Math.Min(buffer.Length, bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer); position += count; return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CleanupEntered.TrySetResult();
                if (!AllowCleanup.Wait(Deadline)) throw new TimeoutException("Coordinated cleanup was not released");
                Disposed = true;
            }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
