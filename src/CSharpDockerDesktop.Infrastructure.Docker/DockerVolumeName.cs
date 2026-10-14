namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerVolumeName
{
    // The read API accepts a name or ID, including plugin/cluster names. Do not impose
    // the local driver's creation grammar, normalize, trim, or interpret image references.
    internal static bool IsValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 4096 || name is "." or "..") return false;
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsControl(name[i]) || char.IsLowSurrogate(name[i])) return false;
            if (char.IsHighSurrogate(name[i]) && (++i >= name.Length || !char.IsLowSurrogate(name[i]))) return false;
        }
        return true;
    }
}
