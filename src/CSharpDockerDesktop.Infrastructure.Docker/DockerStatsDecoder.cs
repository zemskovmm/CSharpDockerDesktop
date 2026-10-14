using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static partial class DockerStatsDecoder
{
    // A bounded object framer handles arbitrary fragmentation and consecutive JSON values without
    // retaining a stream history. System.Text.Json remains authoritative for syntax/depth validation.
    internal static async Task DecodeAsync(Stream stream, string identity, string? osType, DockerConnectionOptions options,
        Func<ContainerStatsSample, ValueTask> publish, CancellationToken token)
    {
        var read = new byte[4096]; var sample = new byte[options.MaximumStatsSampleBytes];
        int length = 0, depth = 0; bool quoted = false, escaped = false;
        while (true)
        {
            int count = await stream.ReadAsync(read, token).ConfigureAwait(false);
            if (count == 0)
            {
                if (length != 0) throw Malformed("The Engine ended a truncated Stats sample.");
                return;
            }
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                byte b = read[i];
                if (length == 0)
                {
                    if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
                    if (b != '{') throw Malformed("Stats must contain consecutive JSON objects.");
                }
                if (length == sample.Length) throw Malformed("A Stats sample exceeds the configured size limit.");
                sample[length++] = b;
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (b == '\\') escaped = true;
                    else if (b == '"') quoted = false;
                }
                else if (b == '"') quoted = true;
                else if (b is (byte)'{' or (byte)'[')
                {
                    if (++depth > 64) throw Malformed("A Stats sample exceeds the JSON depth limit.");
                }
                else if (b is (byte)'}' or (byte)']') depth--;
                if (depth == 0)
                {
                    var result = Parse(sample.AsMemory(0, length), identity, osType, options.MaximumStatsCollectionEntries);
                    await publish(result).ConfigureAwait(false);
                    length = 0;
                }
            }
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ContainerStatsSample Parse(ReadOnlyMemory<byte> bytes, string identity, string? osType, int maximumEntries)
    {
        _ = StrictUtf8.GetCharCount(bytes.Span);
        using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
        try
        {
            var root = Object(json.RootElement); int budget = maximumEntries;
            var id = Text(root, "id");
            if (id is not null && id != identity) throw Malformed("Stats returned a different container identity.");
            var reportedOS = Text(root, "os_type");
            if (reportedOS is not null && osType is not null && reportedOS != osType)
                throw Malformed("Stats returned a different container platform.");
            return new(identity, reportedOS ?? osType, Timestamp(root, "read"), Timestamp(root, "preread"),
                Cpu(Field(root, "cpu_stats"), ref budget), Cpu(Field(root, "precpu_stats"), ref budget), UInt32(root, "num_procs"),
                Memory(Field(root, "memory_stats"), ref budget), Networks(Field(root, "networks"), ref budget),
                BlockIo(Field(root, "blkio_stats"), ref budget), Storage(Field(root, "storage_stats")));
        }
        catch (InvalidOperationException)
        { throw Malformed("The Engine returned an invalid Stats JSON value or Unicode string."); }
    }

    private static ContainerCpuCounters? Cpu(JsonElement value, ref int budget)
    {
        if (Absent(value)) return null;
        value = Object(value); var usage = Object(Field(value, "cpu_usage"));
        ImmutableArray<ulong?>? perCpu = null;
        var array = Field(usage, "percpu_usage");
        if (!Absent(array))
        {
            if (array.ValueKind != JsonValueKind.Array) throw Malformed("Invalid Stats CPU collection.");
            Take(ref budget, array.GetArrayLength());
            perCpu = array.EnumerateArray().Select(Number).ToImmutableArray();
        }
        return new(Number(Field(usage, "total_usage")), Number(Field(value, "system_cpu_usage")), UInt32(value, "online_cpus"),
            perCpu, Number(Field(usage, "usage_in_kernelmode")), Number(Field(usage, "usage_in_usermode")));
    }
    private static ContainerMemoryCounters? Memory(JsonElement value, ref int budget)
    {
        if (Absent(value)) return null;
        value = Object(value); var stats = Field(value, "stats");
        ImmutableDictionary<string, ulong?>? map = null;
        if (!Absent(stats))
        {
            stats = Object(stats); var builder = ImmutableDictionary.CreateBuilder<string, ulong?>(StringComparer.Ordinal);
            foreach (var field in stats.EnumerateObject())
            {
                Take(ref budget, 1);
                if (builder.ContainsKey(field.Name)) throw Malformed("Duplicate Stats memory counter.");
                builder.Add(field.Name, Number(field.Value));
            }
            map = builder.ToImmutable();
        }
        return new(Number(Field(value, "usage")), Number(Field(value, "limit")), Number(Field(value, "max_usage")),
            Number(Field(value, "failcnt")), map, Number(Field(value, "commitbytes")), Number(Field(value, "commitpeakbytes")),
            Number(Field(value, "privateworkingset")));
    }
    private static ImmutableDictionary<string, ContainerNetworkCounters?>? Networks(JsonElement value, ref int budget)
    {
        if (Absent(value)) return null;
        value = Object(value); var map = ImmutableDictionary.CreateBuilder<string, ContainerNetworkCounters?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            Take(ref budget, 1);
            if (map.ContainsKey(property.Name)) throw Malformed("Duplicate Stats network interface.");
            var entry = property.Value;
            if (Absent(entry)) { map.Add(property.Name, null); continue; }
            entry = Object(entry);
            map.Add(property.Name, new(Number(Field(entry, "rx_bytes")), Number(Field(entry, "tx_bytes")),
                Number(Field(entry, "rx_packets")), Number(Field(entry, "tx_packets")), Number(Field(entry, "rx_errors")),
                Number(Field(entry, "tx_errors")), Number(Field(entry, "rx_dropped")), Number(Field(entry, "tx_dropped")),
                Text(entry, "endpoint_id"), Text(entry, "instance_id")));
        }
        return map.ToImmutable();
    }
    private static ImmutableArray<ContainerBlockIoCounter>? BlockIo(JsonElement value, ref int budget)
    {
        if (Absent(value)) return null;
        var entries = Field(Object(value), "io_service_bytes_recursive");
        if (Absent(entries)) return null;
        if (entries.ValueKind != JsonValueKind.Array) throw Malformed("Invalid Stats Block I/O collection.");
        Take(ref budget, entries.GetArrayLength());
        var result = ImmutableArray.CreateBuilder<ContainerBlockIoCounter>(entries.GetArrayLength());
        foreach (var entry in entries.EnumerateArray())
        {
            var item = Object(entry);
            if (Absent(item)) throw Malformed("Invalid Stats Block I/O entry.");
            result.Add(new(Number(Field(item, "major")), Number(Field(item, "minor")), Text(item, "op"), Number(Field(item, "value"))));
        }
        return result.MoveToImmutable();
    }
    private static ContainerStorageCounters? Storage(JsonElement value)
    {
        if (Absent(value)) return null;
        value = Object(value);
        return new(Number(Field(value, "read_size_bytes")), Number(Field(value, "write_size_bytes")),
            Number(Field(value, "read_count_normalized")), Number(Field(value, "write_count_normalized")));
    }
    private static bool Absent(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
    private static JsonElement Object(JsonElement value)
    {
        if (!Absent(value) && value.ValueKind != JsonValueKind.Object) throw Malformed("Invalid Stats object field.");
        return value;
    }
    private static JsonElement Field(JsonElement value, string name)
        => !Absent(value) && value.TryGetProperty(name, out var field) ? field : default;
    private static ulong? Number(JsonElement value)
    {
        if (Absent(value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var number))
            throw Malformed("Invalid Stats unsigned counter.");
        return number;
    }
    private static uint? UInt32(JsonElement value, string name)
    {
        var number = Number(Field(value, name));
        if (number > uint.MaxValue) throw Malformed("Invalid Stats CPU count.");
        return (uint?)number;
    }
    private static string? Text(JsonElement value, string name)
    {
        var field = Field(value, name);
        if (Absent(field)) return null;
        if (field.ValueKind != JsonValueKind.String) throw Malformed("Invalid Stats text field.");
        return field.GetString();
    }
    private static void Take(ref int budget, int count)
    {
        if (count > budget) throw Malformed("Stats retained collections exceed the configured entry limit.");
        budget -= count;
    }
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex StampPattern();
    private static ContainerStatsTimestamp? Timestamp(JsonElement value, string name)
    {
        var text = Text(value, name);
        if (text is null || text.Length == 0) return null;
        if (text.Length > 40) throw Malformed("Invalid Stats timestamp.");
        var match = StampPattern().Match(text);
        if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups[1].Value + (match.Groups[3].Value == "Z" ? "+00:00" : match.Groups[3].Value),
            "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Malformed("Invalid Stats timestamp.");
        int nanos = match.Groups[2].Success ? int.Parse(match.Groups[2].Value.PadRight(9, '0'), CultureInfo.InvariantCulture) : 0;
        return new(text, date.ToUnixTimeSeconds(), nanos);
    }
    private static StatsProtocolException Malformed(string description) => new(new(ContainerStatsFailureKind.MalformedSample, description));
}
