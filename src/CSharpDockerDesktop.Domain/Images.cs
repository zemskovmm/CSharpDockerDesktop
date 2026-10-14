namespace CSharpDockerDesktop.Domain;

/// <summary>Full daemon identity and repository references. Null metadata means unavailable; empty collections and zero are valid values.</summary>
public sealed record ImageSummary(string Id, IReadOnlyList<string>? RepoTags,
    IReadOnlyList<string>? RepoDigests, DateTimeOffset? Created, long? Size);

/// <summary>Config is the image configuration, never the deprecated ContainerConfig. RawJson retains all configuration fields.</summary>
public sealed record ImageConfiguration(string? User, IReadOnlyList<string>? Environment,
    IReadOnlyList<string>? Command, IReadOnlyList<string>? Entrypoint, string? WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Labels, IReadOnlyList<string>? ExposedPorts,
    IReadOnlyList<string>? Volumes, IReadOnlyList<string>? OnBuild, string? StopSignal,
    IReadOnlyList<string>? Shell, bool? ArgsEscaped, string RawJson);

/// <summary>Layers are ordered, uncompressed layer content digests (RootFS.Layers), not registry manifest digests.</summary>
public sealed record ImageRootFileSystem(string? Type, IReadOnlyList<string>? Layers);

/// <summary>RawJson is the actual UTF-8 inspect response including unknown and sensitive fields, independently retained.</summary>
public sealed record ImageInspection(string Id, IReadOnlyList<string>? RepoTags,
    IReadOnlyList<string>? RepoDigests, DateTimeOffset? Created, long? Size, string? OS,
    string? OSVersion, string? Architecture, string? Variant, ImageConfiguration? Configuration,
    ImageRootFileSystem? RootFileSystem, string RawJson);
