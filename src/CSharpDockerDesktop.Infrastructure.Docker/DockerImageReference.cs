using System.Text.RegularExpressions;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerImageReference
{
    // Docker distribution/reference grammar. ASCII tag characters deliberately avoid .NET's Unicode \w.
    private const string Component = "[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*";
    private const string DomainComponent = "(?:[a-zA-Z0-9]|[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9])";
    private const string Domain = "(?:" + DomainComponent + "(?:\\." + DomainComponent + ")*|\\[[a-fA-F0-9:]+\\])(?::[0-9]+)?";
    private const string Digest = "[A-Za-z][A-Za-z0-9]*(?:[-_+.][A-Za-z][A-Za-z0-9]*)*:[a-fA-F0-9]{32,}";
    private static readonly Regex Named = new("\\A(?<name>(?:" + Domain + "/)?" + Component + "(?:/" + Component
        + ")*)(?::[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127})?(?:@" + Digest + ")?\\z", RegexOptions.NonBacktracking);
    private static readonly Regex BareDigest = new("\\A" + Digest + "\\z", RegexOptions.NonBacktracking);

    internal static bool IsValid(string? reference)
    {
        if (string.IsNullOrEmpty(reference) || reference.Length > 4096) return false;
        if (BareDigest.IsMatch(reference)) return true;
        var match = Named.Match(reference);
        // Semantic existence, algorithm support and normalization belong to the daemon.
        return match.Success && match.Groups["name"].Length <= 255;
    }
}
