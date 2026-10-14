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

    internal void Validate()
    {
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
