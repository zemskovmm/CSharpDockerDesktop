namespace CSharpDockerDesktop.Domain;

public enum VolumeDependencyCompleteness { Unavailable, Incomplete, Complete }

/// <summary>Full daemon identity and available names/state. Metadata is private daemon content.</summary>
public sealed class VolumeContainerDependency
{
    public VolumeContainerDependency(string id, IEnumerable<string>? names, string? name, string? state, bool? running)
    {
        Id = id;
        Names = names is null ? null : Array.AsReadOnly(names.ToArray());
        Name = name;
        State = state;
        Running = running;
    }
    public string Id { get; }
    public IReadOnlyList<string>? Names { get; }
    public string? Name { get; }
    public string? State { get; }
    public bool? Running { get; }
}

/// <summary>
/// Fresh filtered-list plus per-candidate inspection evidence, not an atomic snapshot or deletion authorization.
/// Containers is null only when the candidate collection is unavailable. UnverifiedContainerIds identifies
/// candidates with missing mount evidence; only Complete with an empty collection is successful empty evidence.
/// Concurrent creation/removal/attachment and daemon deletion conflicts still require final-response handling.
/// </summary>
public sealed class VolumeDependencies
{
    public VolumeDependencies(string volumeName, IEnumerable<VolumeContainerDependency>? containers,
        IEnumerable<string> unverifiedContainerIds)
    {
        VolumeName = volumeName;
        Containers = containers is null ? null : Array.AsReadOnly(containers.ToArray());
        UnverifiedContainerIds = Array.AsReadOnly(unverifiedContainerIds.ToArray());
    }
    public string VolumeName { get; }
    public IReadOnlyList<VolumeContainerDependency>? Containers { get; }
    public IReadOnlyList<string> UnverifiedContainerIds { get; }
    public VolumeDependencyCompleteness Completeness => Containers is null ? VolumeDependencyCompleteness.Unavailable
        : UnverifiedContainerIds.Count > 0 ? VolumeDependencyCompleteness.Incomplete : VolumeDependencyCompleteness.Complete;
}
