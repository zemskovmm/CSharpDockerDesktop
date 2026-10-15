using System.Globalization;
using System.Text;
using System.Text.Json;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

// Exactly one physical stream. No HTTP pool, reconnect, redirects, informational-response loop or replay.
// This parser also owns read-ahead across the final header/TTY boundary.
internal sealed class DockerExecWire(Stream stream, DockerConnectionOptions options)
{
    private readonly byte[] buffer = new byte[4096];
    private int offset, available;
    internal sealed record Header(int Status, Dictionary<string, string> Fields)
    {
        internal string? Get(string name) => Fields.GetValueOrDefault(name);
    }
    internal async Task SendAsync(string method, string path, byte[]? body, bool upgrade, CancellationToken token)
    {
        var header = Encoding.ASCII.GetBytes($"{method} /{path} HTTP/1.1\r\nHost: docker\r\n" +
            (upgrade ? "Connection: Upgrade\r\nUpgrade: tcp\r\n" : "") +
            (body is null ? "" : $"Content-Type: application/json\r\nContent-Length: {body.Length}\r\n") + "\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        if (body is not null) await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    internal async Task<Header> ReadHeaderAsync(CancellationToken token)
    {
        int budget = options.MaximumExecHeaderBytes;
        string status = await LineAsync(budget, token).ConfigureAwait(false); budget -= status.Length + 2;
        var parts = status.Split(' ', 3);
        if (parts.Length < 2 || parts[0] != "HTTP/1.1" || parts[1].Length != 3 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int code) || code < 100 || code > 599) throw Bad();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string line = await LineAsync(budget, token).ConfigureAwait(false); budget -= line.Length + 2;
            if (line.Length == 0) break;
            int colon = line.IndexOf(':');
            if (colon <= 0 || !line[..colon].All(TokenCharacter) || !fields.TryAdd(line[..colon], line[(colon + 1)..].Trim(' ', '\t'))) throw Bad();
        }
        return new(code, fields);
    }
    internal static bool HasToken(string? value, string token) => value?.Split(',').Any(v => v.Trim().Equals(token, StringComparison.OrdinalIgnoreCase)) == true;
    internal static void ValidateUpgrade(Header header)
    {
        if (header.Status != 101 || !HasToken(header.Get("Connection"), "Upgrade") || HasToken(header.Get("Connection"), "close")
            || !string.Equals(header.Get("Upgrade"), "tcp", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(header.Get("Content-Type"), "application/vnd.docker.raw-stream", StringComparison.OrdinalIgnoreCase)
            || header.Get("Transfer-Encoding") is not null || header.Get("Content-Length") is not null) throw Bad();
    }
    internal async Task<(byte[] Bytes, bool Truncated)> BodyAsync(Header header, int maximum, bool requireReusable, CancellationToken token, Action<ReadOnlyMemory<byte>>? prefix = null)
    {
        var length = header.Get("Content-Length"); var encoding = header.Get("Transfer-Encoding");
        if (length is not null && encoding is not null || requireReusable && HasToken(header.Get("Connection"), "close")) throw Bad();
        using var bytes = new MemoryStream();
        async Task<bool> Copy(long count)
        {
            var chunk = new byte[Math.Min(4096, maximum + 1)];
            while (count > 0)
            {
                int read = await ReadAsync(chunk.AsMemory(0, (int)Math.Min(Math.Min(count, chunk.Length), maximum + 1L - bytes.Length)), token).ConfigureAwait(false);
                if (read == 0) throw Bad();
                int retain = (int)Math.Min(read, maximum - bytes.Length); bytes.Write(chunk, 0, retain); prefix?.Invoke(chunk.AsMemory(0, retain)); count -= read;
                if (read > retain) return false;
            }
            return true;
        }
        if (encoding is not null)
        {
            if (!encoding.Equals("chunked", StringComparison.OrdinalIgnoreCase)) throw Bad();
            int framingBudget = options.MaximumExecHeaderBytes;
            while (true)
            {
                var line = await LineAsync(framingBudget, token).ConfigureAwait(false); framingBudget -= line.Length + 2;
                // Deliberately narrow: extensions/trailers are unnecessary for the Engine JSON/error contract.
                if (!long.TryParse(line, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0) throw Bad();
                if (size == 0) { if (await LineAsync(framingBudget, token).ConfigureAwait(false) != "") throw Bad(); break; }
                if (!await Copy(size).ConfigureAwait(false)) return (bytes.ToArray(), true);
                if (await LineAsync(framingBudget, token).ConfigureAwait(false) != "") throw Bad(); framingBudget -= 2;
            }
        }
        else if (length is not null)
        {
            if (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0) throw Bad();
            if (!await Copy(count).ConfigureAwait(false)) return (bytes.ToArray(), true);
        }
        else
        {
            if (requireReusable) throw Bad();
            var chunk = new byte[Math.Min(4096, maximum + 1)];
            while (true)
            {
                int n = await ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maximum + 1L - bytes.Length)), token).ConfigureAwait(false);
                if (n == 0) break;
                int retain = (int)Math.Min(n, maximum - bytes.Length); bytes.Write(chunk, 0, retain); prefix?.Invoke(chunk.AsMemory(0, retain));
                if (retain < n) return (bytes.ToArray(), true);
            }
        }
        return (bytes.ToArray(), false);
    }
    internal async ValueTask<int> ReadAsync(Memory<byte> target, CancellationToken token)
    {
        if (offset < available)
        {
            int n = Math.Min(target.Length, available - offset); buffer.AsMemory(offset, n).CopyTo(target); offset += n; return n;
        }
        return await stream.ReadAsync(target, token).ConfigureAwait(false);
    }
    private async Task<string> LineAsync(int maximum, CancellationToken token)
    {
        var line = new StringBuilder(); bool cr = false;
        for (int n = 0; n < maximum; n++)
        {
            if (offset == available)
            {
                available = await stream.ReadAsync(buffer, token).ConfigureAwait(false); offset = 0;
                if (available == 0) throw Bad();
            }
            byte b = buffer[offset++];
            if (cr) { if (b == 10) return line.ToString(); throw Bad(); }
            if (b == 13) cr = true;
            else if (b == 9 || b is >= 32 and < 127) line.Append((char)b);
            else throw Bad();
        }
        throw Bad();
    }
    private static bool TokenCharacter(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);
    internal static ExecAttachmentProtocolException Bad() => new(ContainerExecFailureKind.MalformedResponse);
}

internal sealed class ExecAttachmentProtocolException(ContainerExecFailureKind kind) : Exception
{
    internal ContainerExecFailureKind Kind { get; } = kind;
}
