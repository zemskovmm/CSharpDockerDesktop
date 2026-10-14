using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerVolumeDependencies
{
    internal static async Task<EngineResourceResult<VolumeDependencies>> QueryAsync(HttpClient http, EngineApiVersion api,
        string name, DockerConnectionOptions options, ITransportContinuity? continuity, long? generation,
        SemaphoreSlim slots, CancellationToken lifetime, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        if (!DockerVolumeName.IsValid(name))
            return EngineResourceResult<VolumeDependencies>.Failed(new(EngineResourceFailureKind.InvalidRequest,
                "A non-empty bounded complete volume name without controls or invalid Unicode is required."));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime);
        deadline.CancelAfter(options.RequestTimeout);
        var admitted = false;
        try
        {
            await slots.WaitAsync(deadline.Token).ConfigureAwait(false);
            admitted = true;
            var filters = Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string, string[]> { ["volume"] = [name] }));
            var candidates = await DockerResourceReader.ReadAsync(http, api, $"containers/json?all=true&filters={filters}",
                bytes => Candidates(bytes, options.MaximumVolumeDependencyContainers), options, continuity, generation,
                deadline.Token, caller, "volume dependency").ConfigureAwait(false);
            if (!candidates.IsSuccess) return EngineResourceResult<VolumeDependencies>.Failed(candidates.Failure!);
            var related = new List<VolumeContainerDependency>();
            var unverified = new List<string>();
            long retainedInspectionCharacters = 0;
            if (candidates.Value!.Items is { } items)
            {
                // Sequential inspection bounds active requests and transient body/JSON retention independently of candidate count.
                foreach (var candidate in items)
                {
                    var inspection = await DockerResourceReader.ReadAsync(http, api,
                        $"containers/{Uri.EscapeDataString(candidate.Id)}/json", bytes => Inspect(bytes, candidate, name),
                        options, continuity, generation, deadline.Token, caller, "volume dependency").ConfigureAwait(false);
                    // An inspection error, including disappearance, cannot certify a complete or empty dependency set.
                    if (!inspection.IsSuccess) return EngineResourceResult<VolumeDependencies>.Failed(inspection.Failure!);
                    if (inspection.Value!.Related is { } dependency)
                    {
                        retainedInspectionCharacters += (dependency.Name?.Length ?? 0) + (dependency.State?.Length ?? 0);
                        if (retainedInspectionCharacters > options.MaximumResponseBytes)
                            throw Malformed("The retained dependency inspection metadata exceeds its configured size limit.");
                        related.Add(dependency);
                    }
                    if (!inspection.Value.Complete) unverified.Add(candidate.Id);
                }
            }
            var result = new VolumeDependencies(name, candidates.Value.Items is null ? null : related, unverified);
            deadline.Token.ThrowIfCancellationRequested();
            DockerResourceReader.EnsureContinuity(continuity, generation);
            return EngineResourceResult<VolumeDependencies>.Success(result);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or ResourceProtocolException)
        {
            caller.ThrowIfCancellationRequested();
            return EngineResourceResult<VolumeDependencies>.Failed(ex is ResourceProtocolException protocol ? protocol.Failure
                : new(EngineResourceFailureKind.TransportLoss, lifetime.IsCancellationRequested
                    ? "The Engine connection was disposed." : "The volume dependency query exceeded its deadline."));
        }
        finally { if (admitted) slots.Release(); }
    }

    private sealed record Candidate(string Id, IReadOnlyList<string>? Names, string? State);
    private sealed record CandidateSet(IReadOnlyList<Candidate>? Items);
    private sealed record Inspection(VolumeContainerDependency? Related, bool Complete);

    private static CandidateSet Candidates(byte[] body, int maximum)
    {
        using var json = Parse(body);
        if (json.RootElement.ValueKind == JsonValueKind.Null) return new(null);
        Kind(json.RootElement, JsonValueKind.Array);
        if (json.RootElement.GetArrayLength() > maximum) throw Malformed("The dependency candidate count exceeds its configured limit.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Candidate>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            Kind(item, JsonValueKind.Object);
            var id = Identity(item);
            if (!ids.Add(id)) throw Malformed("A dependency container identity is duplicated.");
            IReadOnlyList<string>? names = null;
            if (Field(item, "Names") is { } values)
            {
                Kind(values, JsonValueKind.Array);
                names = Array.AsReadOnly(values.EnumerateArray().Select(Text).ToArray());
            }
            result.Add(new(id, names, String(item, "State")));
        }
        return new(result.AsReadOnly());
    }

    private static Inspection Inspect(byte[] body, Candidate candidate, string volumeName)
    {
        using var json = Parse(body);
        var root = json.RootElement;
        Kind(root, JsonValueKind.Object);
        if (!string.Equals(Identity(root), candidate.Id, StringComparison.Ordinal))
            throw Malformed("The inspected dependency container identity differs from the requested full identity.");
        var name = String(root, "Name");
        var state = candidate.State;
        bool? running = null;
        if (Field(root, "State") is { } runtime)
        {
            Kind(runtime, JsonValueKind.Object);
            state = String(runtime, "Status") ?? state;
            if (Field(runtime, "Running") is { } flag)
                running = flag.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false,
                    _ => throw Malformed("A dependency runtime flag has an incompatible JSON type.") };
        }
        if (Field(root, "Mounts") is not { } mounts) return new(null, false);
        Kind(mounts, JsonValueKind.Array);
        var complete = true;
        var related = false;
        foreach (var mount in mounts.EnumerateArray())
        {
            Kind(mount, JsonValueKind.Object);
            var type = String(mount, "Type");
            var mountName = String(mount, "Name");
            if (string.IsNullOrEmpty(type)) { complete = false; continue; }
            if (!string.Equals(type, "volume", StringComparison.Ordinal))
            {
                // A future/cluster mount kind cannot certify an ordinary volume-name relationship.
                if (type is not ("bind" or "npipe" or "tmpfs" or "image")) complete = false;
                continue;
            }
            if (mountName is null) { complete = false; continue; }
            if (!DockerVolumeName.IsValid(mountName)) throw Malformed("A dependency volume mount identity is unusable.");
            related |= string.Equals(mountName, volumeName, StringComparison.Ordinal);
        }
        return new(related ? new(candidate.Id, candidate.Names, name, state, running) : null, complete);
    }

    private static JsonDocument Parse(byte[] body)
    {
        // Reject invalid UTF-8 even in additive content, without retaining raw Inspect in this result.
        try { _ = new UTF8Encoding(false, true).GetCharCount(body); }
        catch (DecoderFallbackException) { throw Malformed("The dependency response contains invalid UTF-8."); }
        return JsonDocument.Parse(body);
    }
    private static string Identity(JsonElement item)
    {
        var id = String(item, "Id");
        return DockerVolumeName.IsValid(id) ? id! : throw Malformed("A dependency container identity is missing or unusable.");
    }
    private static JsonElement? Field(JsonElement parent, string name)
    {
        JsonElement? result = null;
        var found = false;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found) throw Malformed("A selected dependency field is duplicated.");
            found = true;
            if (property.Value.ValueKind != JsonValueKind.Null) result = property.Value;
        }
        return result;
    }
    private static string? String(JsonElement parent, string name) => Field(parent, name) is { } value ? Text(value) : null;
    private static string Text(JsonElement value)
    {
        Kind(value, JsonValueKind.String);
        try
        {
            var text = value.GetString()!;
            if (text.Length > 4096) throw Malformed("A selected dependency string exceeds its safety limit.");
            return text;
        }
        catch (InvalidOperationException) { throw Malformed("A dependency string contains invalid Unicode."); }
    }
    private static void Kind(JsonElement value, JsonValueKind kind)
    { if (value.ValueKind != kind) throw Malformed("A dependency field has an incompatible JSON type."); }
    private static ResourceProtocolException Malformed(string description) => new(new(EngineResourceFailureKind.MalformedData, description));
}
