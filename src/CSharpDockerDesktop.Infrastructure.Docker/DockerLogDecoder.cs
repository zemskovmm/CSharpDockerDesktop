using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Infrastructure.Docker;

internal static class DockerLogDecoder
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex TimestampPattern = new(
        @"^(?<date>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(?<fraction>\d{1,9}))?(?<zone>Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static async Task DecodeAsync(Stream stream, bool tty, DockerConnectionOptions options,
        Func<ContainerLogOutput, Task> deliver, CancellationToken token)
    {
        var chunk = new byte[4096];
        var decoders = new[] { Utf8.GetDecoder(), Utf8.GetDecoder(), Utf8.GetDecoder() };
        long sequence = 0, boundary = 0;
        Task Send(ContainerLogOutput output) => deliver(output with { Sequence = ++sequence });
        if (tty)
        {
            BoundaryWriter? line = null;
            int count;
            while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
            {
                int offset = 0;
                while (offset < count)
                {
                    token.ThrowIfCancellationRequested();
                    line ??= new(decoders[2], ContainerLogStream.Combined, ++boundary, ContainerLogBoundary.CombinedLine,
                        options.MaximumLogFragmentCharacters, Send);
                    int newline = Array.IndexOf(chunk, (byte)'\n', offset, count - offset);
                    int end = newline < 0 ? count : newline + 1;
                    await line.AppendAsync(chunk.AsMemory(offset, end - offset), token).ConfigureAwait(false);
                    if (newline >= 0) { await line.EndAsync(token).ConfigureAwait(false); line = null; }
                    offset = end;
                }
            }
            if (line is not null) await line.EndAsync(token).ConfigureAwait(false);
        }
        else
        {
            var header = new byte[8];
            while (true)
            {
                int received = await FillAsync(stream, header, token).ConfigureAwait(false);
                if (received == 0) break;
                if (received != 8) throw Malformed("The logs stream ended inside a multiplex header.");
                if (header[0] is not (1 or 2 or 3) || header[1] != 0 || header[2] != 0 || header[3] != 0)
                    throw Malformed("The logs stream contains an invalid multiplex header.");
                uint remaining = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
                if (header[0] == 3)
                    throw new LogProtocolException(await SystemErrorAsync(stream, remaining, options.MaximumLogErrorBytes, token).ConfigureAwait(false));
                var writer = new BoundaryWriter(decoders[header[0] - 1], header[0] == 1 ? ContainerLogStream.Stdout : ContainerLogStream.Stderr,
                    ++boundary, ContainerLogBoundary.MultiplexedFrame, options.MaximumLogFragmentCharacters, Send);
                // Never allocate from the announced uint32 size. Even multi-gigabyte frames use this fixed scratch buffer.
                while (remaining > 0)
                {
                    int count = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(remaining, (uint)chunk.Length)), token).ConfigureAwait(false);
                    if (count == 0)
                    {
                        await writer.FlushPrefixAsync(token).ConfigureAwait(false);
                        throw Malformed("The logs stream ended inside a multiplex payload.");
                    }
                    await writer.AppendAsync(chunk.AsMemory(0, count), token).ConfigureAwait(false);
                    remaining -= (uint)count;
                }
                await writer.EndAsync(token).ConfigureAwait(false);
            }
        }
        // Decoder state belongs to the logical stream, rather than a read or frame; incomplete UTF-8 fails at EOF.
        foreach (var decoder in decoders) decoder.GetChars([], 0, 0, new char[2], 0, flush: true);
        token.ThrowIfCancellationRequested();
    }

    private static async Task<int> FillAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int length = 0;
        while (length < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer[length..], token).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        return length;
    }
    private static async Task<ContainerLogFailure> SystemErrorAsync(Stream stream, uint length, int maximum, CancellationToken token)
    {
        var body = new byte[(int)Math.Min(length, (uint)maximum)];
        int count = await FillAsync(stream, body, token).ConfigureAwait(false);
        if (count != body.Length) throw Malformed("The logs stream ended inside a daemon error frame.");
        string message = Encoding.UTF8.GetString(body);
        return new(DockerLogSubscription.LoggingUnavailable(message) ? ContainerLogFailureKind.LoggingUnavailable : ContainerLogFailureKind.DaemonError,
            "The Engine reported an error while streaming logs.",
            DaemonMessage: message, DaemonMessageTruncated: length > maximum);
    }
    private static LogProtocolException Malformed(string description) => new(new(ContainerLogFailureKind.MalformedOutput, description));

    private sealed class BoundaryWriter(Decoder decoder, ContainerLogStream stream, long id, ContainerLogBoundary boundary,
        int maximumCharacters, Func<ContainerLogOutput, Task> deliver)
    {
        private readonly byte[] prefix = new byte[64];
        private readonly char[] characters = new char[maximumCharacters];
        private int prefixLength, timestampPrefixCharacters;
        private bool prefixDone, started;
        private ContainerLogTimestamp? timestamp;

        internal async Task AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            int offset = 0;
            if (!prefixDone)
            {
                while (offset < bytes.Length && prefixLength < prefix.Length)
                {
                    byte value = bytes.Span[offset++]; prefix[prefixLength++] = value;
                    if (value == (byte)' ') break;
                }
                if (prefixLength == prefix.Length || prefixLength > 0 && prefix[prefixLength - 1] == (byte)' ')
                    await FlushPrefixAsync(token).ConfigureAwait(false);
            }
            if (prefixDone && offset < bytes.Length) await TextAsync(bytes[offset..], token).ConfigureAwait(false);
        }
        internal async Task FlushPrefixAsync(CancellationToken token)
        {
            if (prefixDone) return;
            prefixDone = true;
            if (prefixLength > 0 && prefix[prefixLength - 1] == (byte)' ')
            {
                // Prefix inspection is bounded ASCII metadata recognition; text is decoded strictly below and always preserved.
                var original = Encoding.ASCII.GetString(prefix, 0, prefixLength - 1);
                var match = TimestampPattern.Match(original);
                if (match.Success && DateTimeOffset.TryParseExact(match.Groups["date"].Value + match.Groups["zone"].Value,
                    ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:sszzz"], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var seconds))
                {
                    var fraction = match.Groups["fraction"].Value;
                    int nanoseconds = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(9, '0'), CultureInfo.InvariantCulture);
                    timestamp = new(original, seconds.ToUnixTimeSeconds(), nanoseconds);
                    timestampPrefixCharacters = prefixLength;
                }
            }
            await TextAsync(prefix.AsMemory(0, prefixLength), token).ConfigureAwait(false);
        }
        private async Task TextAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                decoder.Convert(bytes.Span[offset..], characters, flush: false, out int used, out int chars, out _);
                offset += used;
                if (chars > 0)
                {
                    await deliver(new(0, stream, new string(characters, 0, chars), id, boundary,
                        !started, false, timestamp, !started ? timestampPrefixCharacters : 0)).ConfigureAwait(false);
                    started = true;
                }
            }
        }
        internal async Task EndAsync(CancellationToken token)
        {
            await FlushPrefixAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await deliver(new(0, stream, "", id, boundary, !started, true, timestamp,
                !started ? timestampPrefixCharacters : 0)).ConfigureAwait(false);
            started = true;
        }
    }
}
