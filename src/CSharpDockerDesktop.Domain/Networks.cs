namespace CSharpDockerDesktop.Domain;

/// <summary>Id is complete. Null metadata is unavailable; empty strings/maps and false are available values.
/// List responses deliberately expose no attachment evidence.</summary>
public sealed record NetworkSummary(string Id, string? Name, string? Driver, string? Scope, bool? Internal,
    IReadOnlyDictionary<string, string>? Labels);

public sealed record NetworkIpam(string? Driver, IReadOnlyList<NetworkIpamConfiguration>? Config,
    IReadOnlyDictionary<string, string>? Options);

public sealed record NetworkIpamConfiguration(string? Subnet, string? IPRange, string? Gateway,
    IReadOnlyDictionary<string, string>? AuxiliaryAddresses);

/// <summary>The map key is the daemon's complete attachment/container identity, including driver-specific endpoint keys.</summary>
public sealed record NetworkAttachment(string? Name, string? EndpointId, string? MacAddress,
    string? IPv4Address, string? IPv6Address);

/// <summary>Containers are inspected observations, not proof of cluster-wide completeness or deletion safety.
/// Null collections are unavailable; empty collections are observed empty. RawJson retains exact actual response text,
/// including unknown and driver-specific data. No field is safe for automatic logging.</summary>
public sealed record NetworkInspection(string Id, string? Name, string? Driver, string? Scope, bool? Internal,
    IReadOnlyDictionary<string, string>? Labels, NetworkIpam? Ipam,
    IReadOnlyDictionary<string, NetworkAttachment>? Containers, string RawJson);
