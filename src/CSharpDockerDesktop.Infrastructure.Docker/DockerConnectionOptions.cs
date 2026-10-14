namespace CSharpDockerDesktop.Infrastructure.Docker;

/// <summary>Short handshake, liveness and resource requests. These are bounded defaults, not live-stream lifetimes.</summary>
public sealed record DockerConnectionOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PipeConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int MaximumResponseBytes { get; init; } = 1024 * 1024;

    public TimeSpan EventOpeningTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumEventBytes { get; init; } = 64 * 1024;
    public int EventBufferCapacity { get; init; } = 128;

    // Implementation tuning; independent of the five-second resource-read deadline.
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan GracefulCommandTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public int MaximumCommandErrorBytes { get; init; } = 64 * 1024;
    public int MaximumConcurrentCommands { get; init; } = 4;

    public TimeSpan LogOpeningTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumConcurrentLogSubscriptions { get; init; } = 4;
    public int MaximumLogTailLines { get; init; } = 10_000;
    public int LogBufferCapacity { get; init; } = 128;
    public int MaximumLogFragmentCharacters { get; init; } = 4096;
    public int MaximumLogErrorBytes { get; init; } = 64 * 1024;

    internal void Validate()
    {
        if (LogOpeningTimeout <= TimeSpan.Zero || LogOpeningTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(LogOpeningTimeout));
        if (MaximumConcurrentLogSubscriptions is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentLogSubscriptions));
        if (MaximumLogTailLines is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaximumLogTailLines));
        if (LogBufferCapacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(LogBufferCapacity));
        if (MaximumLogFragmentCharacters is < 64 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(MaximumLogFragmentCharacters));
        if (MaximumLogErrorBytes is < 64 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumLogErrorBytes));
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        if (GracefulCommandTimeout <= TimeSpan.Zero || GracefulCommandTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(GracefulCommandTimeout));
        if (MaximumCommandErrorBytes < 64 || MaximumCommandErrorBytes > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumCommandErrorBytes));
        if (MaximumConcurrentCommands < 1 || MaximumConcurrentCommands > 64)
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentCommands));
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (PipeConnectTimeout <= TimeSpan.Zero || PipeConnectTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(PipeConnectTimeout));
        if (EventOpeningTimeout <= TimeSpan.Zero || EventOpeningTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(EventOpeningTimeout));
        if (MaximumEventBytes < 64 || MaximumEventBytes > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumEventBytes));
        if (EventBufferCapacity < 1 || EventBufferCapacity > 4096)
            throw new ArgumentOutOfRangeException(nameof(EventBufferCapacity));
        if (MaximumResponseBytes < 64 || MaximumResponseBytes > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
    }
}
