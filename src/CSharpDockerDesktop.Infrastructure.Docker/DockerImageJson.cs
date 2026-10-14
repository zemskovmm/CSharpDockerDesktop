using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerImageJson
{
    internal static IReadOnlyList<ImageSummary> List(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        RequireKind(json.RootElement, JsonValueKind.Array);
        return Array.AsReadOnly(json.RootElement.EnumerateArray().Select(image =>
        {
            RequireKind(image, JsonValueKind.Object);
            DateTimeOffset? created = null;
            if (Number(image, "Created") is { } seconds)
            {
                try { created = DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { throw Malformed("An image creation timestamp is out of range."); }
            }
            return new ImageSummary(Identity(image), Strings(image, "RepoTags"), Strings(image, "RepoDigests"),
                created, Size(image));
        }).ToArray());
    }

    internal static ImageInspection Inspect(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        var image = json.RootElement;
        RequireKind(image, JsonValueKind.Object);
        ImageConfiguration? configuration = null;
        if (Object(image, "Config") is { } config)
            configuration = new(String(config, "User"), Strings(config, "Env"), Strings(config, "Cmd"),
                Strings(config, "Entrypoint"), String(config, "WorkingDir"), Labels(config), Keys(config, "ExposedPorts"),
                Keys(config, "Volumes"), Strings(config, "OnBuild"), String(config, "StopSignal"),
                Strings(config, "Shell"), Bool(config, "ArgsEscaped"), config.GetRawText());
        ImageRootFileSystem? root = Object(image, "RootFS") is { } fs
            ? new(String(fs, "Type"), Strings(fs, "Layers")) : null;
        return new(Identity(image), Strings(image, "RepoTags"), Strings(image, "RepoDigests"), Timestamp(image),
            Size(image), String(image, "Os"), String(image, "OsVersion"), String(image, "Architecture"),
            String(image, "Variant"), configuration, root, Encoding.UTF8.GetString(body));
    }

    private static string Identity(JsonElement image)
        => String(image, "Id") is { Length: > 0 } id && !string.IsNullOrWhiteSpace(id)
            ? id : throw Malformed("Image identity is missing or empty.");
    private static JsonElement? Field(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;
    private static JsonElement? Object(JsonElement parent, string name)
    {
        var value = Field(parent, name);
        if (value is { } v) RequireKind(v, JsonValueKind.Object);
        return value;
    }
    private static string? String(JsonElement parent, string name)
    {
        if (Field(parent, name) is not { } value) return null;
        RequireKind(value, JsonValueKind.String);
        return DecodedString(value);
    }
    private static string DecodedString(JsonElement value)
    {
        try { return value.GetString()!; }
        catch (InvalidOperationException) { throw Malformed("An image string field contains invalid Unicode."); }
    }
    private static string DecodedName(JsonProperty property)
    {
        try { return property.Name; }
        catch (InvalidOperationException) { throw Malformed("An image property name contains invalid Unicode."); }
    }
    private static long? Number(JsonElement parent, string name)
    {
        if (Field(parent, name) is not { } value) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw Malformed("An image numeric field is invalid.");
        return number;
    }
    private static long? Size(JsonElement image)
    {
        var size = Number(image, "Size");
        return size is < 0 ? throw Malformed("An image size is negative.") : size;
    }
    private static bool? Bool(JsonElement parent, string name)
        => Field(parent, name) is not { } value ? null : value.ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            _ => throw Malformed("An image boolean field is invalid.")
        };
    private static IReadOnlyList<string>? Strings(JsonElement parent, string name)
    {
        if (Field(parent, name) is not { } value) return null;
        RequireKind(value, JsonValueKind.Array);
        return Array.AsReadOnly(value.EnumerateArray().Select(item =>
        {
            RequireKind(item, JsonValueKind.String);
            return DecodedString(item);
        }).ToArray());
    }
    private static IReadOnlyList<string>? Keys(JsonElement parent, string name)
    {
        if (Object(parent, name) is not { } value) return null;
        return Array.AsReadOnly(value.EnumerateObject().Select(property =>
        {
            // Older daemons can emit null values for these object-as-set fields.
            if (property.Value.ValueKind != JsonValueKind.Null) RequireKind(property.Value, JsonValueKind.Object);
            return DecodedName(property);
        }).ToArray());
    }
    private static IReadOnlyDictionary<string, string?>? Labels(JsonElement config)
    {
        if (Object(config, "Labels") is not { } labels) return null;
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var label in labels.EnumerateObject())
        {
            if (label.Value.ValueKind != JsonValueKind.Null) RequireKind(label.Value, JsonValueKind.String);
            result[DecodedName(label)] = label.Value.ValueKind == JsonValueKind.Null ? null : DecodedString(label.Value);
        }
        return new ReadOnlyDictionary<string, string?>(result);
    }
    private static DateTimeOffset? Timestamp(JsonElement image)
    {
        if (String(image, "Created") is not { } text) return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            throw Malformed("An image timestamp is invalid.");
        // Before API 1.44 an unavailable image creation time was encoded as Go zero time.
        return value == DateTimeOffset.MinValue ? null : value;
    }
    private static void RequireKind(JsonElement value, JsonValueKind kind)
    {
        if (value.ValueKind != kind) throw Malformed("An image field has an incompatible JSON type.");
    }
    private static ResourceProtocolException Malformed(string description)
        => new(new(EngineResourceFailureKind.MalformedData, description));
}
