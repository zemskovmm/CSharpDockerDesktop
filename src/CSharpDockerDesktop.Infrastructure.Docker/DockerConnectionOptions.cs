namespace CSharpDockerDesktop.Infrastructure.Docker;

/// <summary>Short connection requests only. These are bounded defaults, not live-stream lifetimes.</summary>
public sealed record DockerConnectionOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PipeConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int MaximumResponseBytes { get; init; } = 1024 * 1024;

    internal void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (PipeConnectTimeout <= TimeSpan.Zero || PipeConnectTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(PipeConnectTimeout));
        if (MaximumResponseBytes < 64 || MaximumResponseBytes > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
    }
}
