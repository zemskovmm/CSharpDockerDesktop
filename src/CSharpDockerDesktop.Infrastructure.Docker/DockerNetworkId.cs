namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerNetworkId
{
    // The schema defines an opaque string, not a fixed hexadecimal width (swarm IDs differ).
    // Preserve the entire original identity and escape it as one component, never interpret a path.
    internal static bool IsValid(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 4096 || id is "." or "..") return false;
        for (var i = 0; i < id.Length; i++)
        {
            if (char.IsControl(id[i]) || char.IsLowSurrogate(id[i])) return false;
            if (char.IsHighSurrogate(id[i]) && (++i >= id.Length || !char.IsLowSurrogate(id[i]))) return false;
        }
        return true;
    }
}
