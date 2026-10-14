namespace CSharpDockerDesktop.Domain;

/// <summary>A null collection is unavailable. Any warning makes an available listing incomplete.</summary>
public enum VolumeListCompleteness { Unavailable, Incomplete, Complete }

/// <summary>Name is the complete daemon identity. Null metadata is unavailable; empty values are valid.</summary>
public sealed record VolumeSummary(string Name, string? Driver, string? Scope, string? Mountpoint,
    IReadOnlyDictionary<string, string>? Labels);

/// <summary>Warnings describe problems fetching volumes, even for empty/null Volumes. Their content is private daemon data.</summary>
public sealed record VolumeListing(IReadOnlyList<VolumeSummary>? Volumes, IReadOnlyList<string>? Warnings)
{
    public bool HasWarnings => Warnings is { Count: > 0 };
}

/// <summary>RawJson retains the exact actual UTF-8 response, including unknown and sensitive fields.</summary>
public sealed record VolumeInspection(string Name, string? Driver, string? Scope, string? Mountpoint,
    IReadOnlyDictionary<string, string>? Labels, string RawJson);
