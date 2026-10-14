using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerVolumeJson
{
    internal static VolumeListing List(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        RequireKind(root, JsonValueKind.Object);
        IReadOnlyList<VolumeSummary>? volumes = null;
        if (Field(root, "Volumes") is { } rows)
        {
            RequireKind(rows, JsonValueKind.Array);
            volumes = Array.AsReadOnly(rows.EnumerateArray().Select(Summary).ToArray());
        }
        IReadOnlyList<string>? warnings = null;
        if (Field(root, "Warnings") is { } items)
        {
            RequireKind(items, JsonValueKind.Array);
            warnings = Array.AsReadOnly(items.EnumerateArray().Select(item =>
            {
                RequireKind(item, JsonValueKind.String);
                return DecodedString(item);
            }).ToArray());
        }
        return new(volumes, warnings);
    }

    internal static VolumeInspection Inspect(byte[] body)
    {
        using var json = JsonDocument.Parse(body);
        var volume = Summary(json.RootElement);
        string raw;
        try { raw = new UTF8Encoding(false, true).GetString(body); }
        catch (DecoderFallbackException) { throw Malformed("The volume inspection contains invalid UTF-8."); }
        return new(volume.Name, volume.Driver, volume.Scope, volume.Mountpoint, volume.Labels, raw);
    }

    private static VolumeSummary Summary(JsonElement volume)
    {
        RequireKind(volume, JsonValueKind.Object);
        var name = String(volume, "Name");
        if (!DockerVolumeName.IsValid(name)) throw Malformed("Volume identity is missing or unusable.");
        return new(name!, String(volume, "Driver"), String(volume, "Scope"), String(volume, "Mountpoint"), Labels(volume));
    }

    private static JsonElement? Field(JsonElement parent, string name)
    {
        JsonElement? result = null;
        var found = false;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found) throw Malformed("A selected volume field is duplicated.");
            found = true;
            if (property.Value.ValueKind != JsonValueKind.Null) result = property.Value;
        }
        return result;
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
        catch (InvalidOperationException) { throw Malformed("A volume string contains invalid Unicode."); }
    }

    private static IReadOnlyDictionary<string, string>? Labels(JsonElement volume)
    {
        if (Field(volume, "Labels") is not { } labels) return null;
        RequireKind(labels, JsonValueKind.Object);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var label in labels.EnumerateObject())
        {
            RequireKind(label.Value, JsonValueKind.String);
            string name;
            try { name = label.Name; }
            catch (InvalidOperationException) { throw Malformed("A volume label name contains invalid Unicode."); }
            if (!result.TryAdd(name, DecodedString(label.Value))) throw Malformed("A volume label is duplicated.");
        }
        return new ReadOnlyDictionary<string, string>(result);
    }

    private static void RequireKind(JsonElement value, JsonValueKind kind)
    {
        if (value.ValueKind != kind) throw Malformed("A volume field has an incompatible JSON type.");
    }

    private static ResourceProtocolException Malformed(string description)
        => new(new(EngineResourceFailureKind.MalformedData, description));
}
