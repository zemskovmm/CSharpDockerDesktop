using System.Globalization;

namespace CSharpDockerDesktop.Domain;

public enum EngineTransport { WindowsNamedPipe, Unsupported, UnixSocket }

/// <summary>An explicit endpoint identity; discovery policy is owned by a future coordinator.</summary>
public sealed record EngineEndpoint(string Address, EngineTransport Transport)
{
    public static EngineEndpoint LocalWindows { get; } = new("npipe://./pipe/docker_engine", EngineTransport.WindowsNamedPipe);
}

public readonly record struct EngineApiVersion(int Major, int Minor) : IComparable<EngineApiVersion>
{
    public int CompareTo(EngineApiVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
    public static bool TryParse(string? value, out EngineApiVersion version)
    {
        version = default;
        if (value is null) return false;
        var parts = value.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) || major < 1) return false;
        version = new(major, minor);
        return true;
    }
}

public sealed record EngineApiCompatibility(EngineApiVersion ClientMinimum, EngineApiVersion ClientMaximum,
    EngineApiVersion DaemonMinimum, EngineApiVersion DaemonMaximum, EngineApiVersion Negotiated);

/// <summary>Null means the daemon did not supply the field, rather than false/unsupported.</summary>
public sealed record EngineCapabilities(bool? MemoryLimit, bool? SwapLimit, bool? CpuCfsPeriod,
    bool? CpuCfsQuota, bool? CpuShares, bool? CpuSet, bool? PidsLimit, bool? IPv4Forwarding,
    bool? Experimental, string? SwarmLocalNodeState);

/// <summary>Version runtime architecture and info host architecture can legitimately differ.</summary>
public sealed record EngineMetadata(EngineEndpoint Endpoint, string EngineVersion, EngineApiCompatibility Api,
    string? RuntimeOS, string? RuntimeArchitecture, string? KernelVersion, string? EngineId, string? EngineName,
    string? OperatingSystem, string? OSType, string? HostArchitecture, string? InfoServerVersion,
    EngineCapabilities Capabilities);

public enum EngineConnectionFailureKind
{
    MissingEndpoint, AccessDenied, UnsupportedEndpoint, IncompatibleApi, DaemonHttpError,
    MalformedResponse, TransientTransportFailure
}

/// <summary>DaemonMessage is user-controlled diagnostic content. It must not be automatically logged.</summary>
public sealed record EngineConnectionFailure(EngineConnectionFailureKind Kind, string Description,
    int? HttpStatusCode = null, string? DaemonMessage = null);
