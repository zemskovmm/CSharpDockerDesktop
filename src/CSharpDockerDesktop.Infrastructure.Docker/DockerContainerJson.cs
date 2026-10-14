using System.Globalization;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerContainerJson
{
    internal static IReadOnlyList<ContainerSummary> List(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        RequireKind(json.RootElement, JsonValueKind.Array);
        return Array.AsReadOnly(json.RootElement.EnumerateArray().Select(c =>
        {
            RequireKind(c, JsonValueKind.Object);
            return new ContainerSummary(Identity(c), Strings(c, "Names"), String(c, "Image"), String(c, "ImageID"),
                String(c, "State"), String(c, "Status"), UnixTime(c, "Created"), ListPorts(c), Mounts(c), Networks(c), Health(c));
        }).ToArray());
    }

    internal static ContainerInspection Inspect(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        var c = json.RootElement;
        RequireKind(c, JsonValueKind.Object);
        ContainerRuntimeState? state = null;
        if (Object(c, "State") is { } s)
        {
            ContainerHealth? health = Health(s);
            state = new(String(s, "Status"), Bool(s, "Running"), Bool(s, "Paused"), Bool(s, "Restarting"),
                Bool(s, "Dead"), Number(s, "ExitCode"), String(s, "Error"), Timestamp(s, "StartedAt"), Timestamp(s, "FinishedAt"), health);
        }
        return new(Identity(c), String(c, "Name"), Object(c, "Config") is { } config ? String(config, "Image") : null,
            String(c, "Image"), Timestamp(c, "Created"), state, InspectPorts(c), Mounts(c), Networks(c), Encoding.UTF8.GetString(body));
    }

    private static string Identity(JsonElement c) => String(c, "Id") is { Length: > 0 } id ? id : throw Malformed("Container identity is missing or empty.");
    private static ContainerHealth? Health(JsonElement c)
        => Object(c, "Health") is { } h ? new(String(h, "Status"), Number(h, "FailingStreak")) : null;

    private static JsonElement? Field(JsonElement c, string name)
        => c.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;
    private static JsonElement? Object(JsonElement c, string name)
    {
        var value = Field(c, name);
        if (value is { } v) RequireKind(v, JsonValueKind.Object);
        return value;
    }
    private static string? String(JsonElement c, string name)
    {
        if (Field(c, name) is not { } value) return null;
        RequireKind(value, JsonValueKind.String);
        return value.GetString();
    }
    private static long? Number(JsonElement c, string name)
    {
        if (Field(c, name) is not { } value) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw Malformed("A container numeric field is invalid.");
        return number;
    }
    private static int? Integer(JsonElement c, string name)
    {
        var value = Number(c, name);
        if (value is < int.MinValue or > int.MaxValue) throw Malformed("A container integer field is out of range.");
        return (int?)value;
    }
    private static bool? Bool(JsonElement c, string name)
    {
        if (Field(c, name) is not { } value) return null;
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Malformed("A container boolean field is invalid.") };
    }
    private static IReadOnlyList<T>? Items<T>(JsonElement c, string name, Func<JsonElement, T> decode)
    {
        if (Field(c, name) is not { } value) return null;
        RequireKind(value, JsonValueKind.Array);
        return Array.AsReadOnly(value.EnumerateArray().Select(decode).ToArray());
    }
    private static IReadOnlyList<string>? Strings(JsonElement c, string name)
        => Items(c, name, value => { RequireKind(value, JsonValueKind.String); return value.GetString()!; });
    private static DateTimeOffset? UnixTime(JsonElement c, string name)
    {
        if (Number(c, name) is not { } seconds) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { throw Malformed("A container creation timestamp is out of range."); }
    }
    private static DateTimeOffset? Timestamp(JsonElement c, string name)
    {
        if (String(c, name) is not { } text) return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            throw Malformed("A container timestamp is invalid.");
        // Docker's Go zero time means the container has never started/finished.
        return value == DateTimeOffset.MinValue ? null : value;
    }
    private static IReadOnlyList<ContainerPort>? ListPorts(JsonElement c)
        => Items(c, "Ports", p =>
        {
            RequireKind(p, JsonValueKind.Object);
            return new ContainerPort(Integer(p, "PrivatePort"), String(p, "Type"), String(p, "IP"), Integer(p, "PublicPort"));
        });
    private static IReadOnlyList<ContainerPort>? InspectPorts(JsonElement c)
    {
        if (Object(c, "NetworkSettings") is not { } settings || Object(settings, "Ports") is not { } ports) return null;
        var result = new List<ContainerPort>();
        foreach (var port in ports.EnumerateObject())
        {
            var key = port.Name.Split('/');
            if (key.Length != 2 || !int.TryParse(key[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number < 0 || number > 65535 || key[1].Length == 0) throw Malformed("A container port key is invalid.");
            if (port.Value.ValueKind == JsonValueKind.Null) result.Add(new(number, key[1], null, null));
            else
            {
                RequireKind(port.Value, JsonValueKind.Array);
                foreach (var binding in port.Value.EnumerateArray())
                {
                    RequireKind(binding, JsonValueKind.Object);
                    var text = String(binding, "HostPort");
                    int? hostPort = null;
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0 || n > 65535)
                            throw Malformed("A container host port is invalid.");
                        hostPort = n;
                    }
                    result.Add(new(number, key[1], String(binding, "HostIp"), hostPort));
                }
                if (port.Value.GetArrayLength() == 0) result.Add(new(number, key[1], null, null));
            }
        }
        return result.AsReadOnly();
    }
    private static IReadOnlyList<ContainerMount>? Mounts(JsonElement c)
        => Items(c, "Mounts", m =>
        {
            RequireKind(m, JsonValueKind.Object);
            return new ContainerMount(String(m, "Type"), String(m, "Name"), String(m, "Source"), String(m, "Destination"),
                String(m, "Driver"), String(m, "Mode"), Bool(m, "RW"), String(m, "Propagation"));
        });
    private static IReadOnlyList<ContainerNetwork>? Networks(JsonElement c)
    {
        if (Object(c, "NetworkSettings") is not { } settings || Object(settings, "Networks") is not { } networks) return null;
        return Array.AsReadOnly(networks.EnumerateObject().Select(n =>
        {
            if (n.Value.ValueKind == JsonValueKind.Null) return new ContainerNetwork(n.Name, null, null, null, null, null, null, null, null, null, null);
            RequireKind(n.Value, JsonValueKind.Object);
            return new ContainerNetwork(n.Name, String(n.Value, "NetworkID"), String(n.Value, "EndpointID"), String(n.Value, "IPAddress"),
                Integer(n.Value, "IPPrefixLen"), String(n.Value, "Gateway"), String(n.Value, "GlobalIPv6Address"),
                Integer(n.Value, "GlobalIPv6PrefixLen"), String(n.Value, "IPv6Gateway"), String(n.Value, "MacAddress"), Strings(n.Value, "Aliases"));
        }).ToArray());
    }
    private static void RequireKind(JsonElement value, JsonValueKind kind)
    {
        if (value.ValueKind != kind) throw Malformed("A container field has an incompatible JSON type.");
    }
    internal static ResourceProtocolException Malformed(string description) => new(new(EngineResourceFailureKind.MalformedData, description));
}
