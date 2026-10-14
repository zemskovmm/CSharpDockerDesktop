using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerNetworkJson
{
    internal static IReadOnlyList<NetworkSummary> List(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        RequireKind(json.RootElement, JsonValueKind.Array);
        return Array.AsReadOnly(json.RootElement.EnumerateArray().Select(Summary).ToArray());
    }

    internal static NetworkInspection Inspect(byte[] body, string? expectedId = null)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var summary = Summary(root);
        if (expectedId is not null && !string.Equals(summary.Id, expectedId, StringComparison.Ordinal))
            throw Malformed("The inspected network identity does not match the complete requested ID.");
        NetworkIpam? ipam = null;
        if (Field(root, "IPAM") is { } value)
        {
            RequireKind(value, JsonValueKind.Object);
            IReadOnlyList<NetworkIpamConfiguration>? configurations = null;
            if (Field(value, "Config") is { } config)
            {
                RequireKind(config, JsonValueKind.Array);
                configurations = Array.AsReadOnly(config.EnumerateArray().Select(item =>
                {
                    RequireKind(item, JsonValueKind.Object);
                    return new NetworkIpamConfiguration(String(item, "Subnet"), String(item, "IPRange"),
                        String(item, "Gateway"), Map(item, "AuxiliaryAddresses"));
                }).ToArray());
            }
            ipam = new(String(value, "Driver"), configurations, Map(value, "Options"));
        }
        IReadOnlyDictionary<string, NetworkAttachment>? containers = null;
        if (Field(root, "Containers") is { } attachments)
        {
            RequireKind(attachments, JsonValueKind.Object);
            var map = new Dictionary<string, NetworkAttachment>(StringComparer.Ordinal);
            foreach (var property in attachments.EnumerateObject())
            {
                var id = PropertyName(property);
                if (!DockerNetworkId.IsValid(id)) throw Malformed("A network attachment identity is missing or unusable.");
                RequireKind(property.Value, JsonValueKind.Object);
                var item = property.Value;
                var attachment = new NetworkAttachment(String(item, "Name"), String(item, "EndpointID"),
                    String(item, "MacAddress"), String(item, "IPv4Address"), String(item, "IPv6Address"));
                if (!map.TryAdd(id, attachment)) throw Malformed("A network attachment identity is duplicated.");
            }
            containers = new ReadOnlyDictionary<string, NetworkAttachment>(map);
        }
        string raw;
        try { raw = new UTF8Encoding(false, true).GetString(body); }
        catch (DecoderFallbackException) { throw Malformed("The network inspection contains invalid UTF-8."); }
        return new(summary.Id, summary.Name, summary.Driver, summary.Scope, summary.Internal, summary.Labels, ipam, containers, raw);
    }

    private static NetworkSummary Summary(JsonElement root)
    {
        RequireKind(root, JsonValueKind.Object);
        var id = String(root, "Id");
        if (!DockerNetworkId.IsValid(id)) throw Malformed("Network identity is missing or unusable.");
        bool? internalNetwork = null;
        if (Field(root, "Internal") is { } value)
            internalNetwork = value.ValueKind switch
            {
                JsonValueKind.True => true, JsonValueKind.False => false,
                _ => throw Malformed("A network field has an incompatible JSON type.")
            };
        return new(id!, String(root, "Name"), String(root, "Driver"), String(root, "Scope"), internalNetwork, Map(root, "Labels"));
    }

    private static JsonElement? Field(JsonElement parent, string name)
    {
        JsonElement? result = null;
        var found = false;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found) throw Malformed("A selected network field is duplicated.");
            found = true;
            if (property.Value.ValueKind != JsonValueKind.Null) result = property.Value;
        }
        return result;
    }

    private static string? String(JsonElement parent, string name)
    {
        if (Field(parent, name) is not { } value) return null;
        RequireKind(value, JsonValueKind.String);
        try { return value.GetString(); }
        catch (InvalidOperationException) { throw Malformed("A network string contains invalid Unicode."); }
    }

    private static string PropertyName(JsonProperty property)
    {
        try { return property.Name; }
        catch (InvalidOperationException) { throw Malformed("A network map key contains invalid Unicode."); }
    }

    private static IReadOnlyDictionary<string, string>? Map(JsonElement parent, string name)
    {
        if (Field(parent, name) is not { } value) return null;
        RequireKind(value, JsonValueKind.Object);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            RequireKind(property.Value, JsonValueKind.String);
            string text;
            try { text = property.Value.GetString()!; }
            catch (InvalidOperationException) { throw Malformed("A network map value contains invalid Unicode."); }
            if (!result.TryAdd(PropertyName(property), text)) throw Malformed("A network map key is duplicated.");
        }
        return new ReadOnlyDictionary<string, string>(result);
    }

    private static void RequireKind(JsonElement value, JsonValueKind kind)
    {
        if (value.ValueKind != kind) throw Malformed("A network field has an incompatible JSON type.");
    }

    private static ResourceProtocolException Malformed(string description)
        => new(new(EngineResourceFailureKind.MalformedData, description));
}
