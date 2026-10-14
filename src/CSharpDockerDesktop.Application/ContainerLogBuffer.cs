using System.Text;
using CSharpDockerDesktop.Domain;

namespace CSharpDockerDesktop.Application;

// Mutated only under DesktopSession's gate. No full-buffer snapshots or per-fragment string copies.
internal sealed class ContainerLogBuffer(ContainerLogsOptions options)
{
    private readonly LinkedList<Segment> retained = new();
    private readonly Segment?[] active = new Segment?[3];
    private readonly Queue<ContainerLogEntry> comparison = new();
    private List<ContainerLogEntry>? replay;
    private int replayIndex, comparisonCharacters;
    private long nextEntry, nextLine;
    public int Characters { get; private set; }
    public int Count => retained.Count;
    public long Evictions { get; private set; }
    public long Discarded { get; private set; }
    public ContainerLogsContinuity Continuity { get; private set; }
    public ContainerLogCursor? Cursor { get; private set; }
    private bool replayEvidenceLost;
    private ContainerLogCursor? comparisonFloor;

    public void Clear()
    {
        retained.Clear(); Characters = 0;
        // No delayed finalization can restore pre-clear partial text. Comparison is independent.
        foreach (var item in active)
            if (item is not null) { Discarded += item.Text.Length; replayEvidenceLost = true; }
        Array.Clear(active);
    }

    public void Interrupt()
    {
        foreach (var item in active)
            if (item is not null)
            {
                item.Partial = true;
                if (!item.Visible) Discarded += item.Text.Length;
                replayEvidenceLost = true;
            }
        Array.Clear(active);
    }

    public void BeginReplay(ContainerLogCursor since)
    {
        Interrupt();
        replay = comparison.Where(e => e.Timestamp?.Cursor is { } cursor && Compare(cursor, since) >= 0).ToList();
        replayIndex = 0;
        // Exact daemon IDs are unavailable; finite history and partial/missing evidence cannot prove no loss.
        bool incomplete = replayEvidenceLost || replay.Count == 0
            || comparisonFloor is { } floor && Compare(floor, since) >= 0;
        Continuity = incomplete ? ContainerLogsContinuity.Uncertain : ContainerLogsContinuity.BoundedOverlap;
        if (incomplete) replay = null; // Preserve content when comparison coverage cannot justify suppression.
    }
    public void Uncertain() => Continuity = ContainerLogsContinuity.Uncertain;

    public void Add(ContainerLogOutput output)
    {
        if (output.Timestamp?.Cursor is { IsValid: true } candidate && (Cursor is null || Compare(candidate, Cursor.Value) > 0)) Cursor = candidate;
        int stream = (int)output.Stream;
        var current = active[stream];
        // A changed original timestamp is observable metadata even inside an unfinished line.
        // Keep it on its own bounded segment rather than losing it or treating every frame as a line.
        if (current is not null && output.Text.Length > 0 && output.Timestamp is { } timestamp
            && timestamp != current.Timestamp)
        {
            var line = current.Line;
            Finish(current, partial: true);
            current = new(++nextEntry, line, output.Stream, timestamp) { Continued = true };
            active[stream] = current;
            if (replay is null) Show(current);
        }
        for (int index = 0; index < output.Text.Length;)
        {
            if (current is null)
            {
                current = new(++nextEntry, ++nextLine, output.Stream, output.Timestamp);
                active[stream] = current;
                if (replay is null) Show(current);
            }
            int width = char.IsHighSurrogate(output.Text[index]) && index + 1 < output.Text.Length
                && char.IsLowSurrogate(output.Text[index + 1]) ? 2 : 1;
            if (current.Text.Length + width > options.MaximumEntryCharacters)
            {
                var line = current.Line;
                Finish(current, partial: true);
                current = new(++nextEntry, line, output.Stream, current.Timestamp) { Continued = true };
                active[stream] = current;
                if (replay is null) Show(current);
            }
            current.Text.Append(output.Text.AsSpan(index, width)); index += width;
            if (current.Visible) { Characters += width; Trim(); }
            else if (current.Dropped) Discarded += width;
            if (output.Text[index - 1] == '\n')
            {
                Finish(current, partial: false); current = null; active[stream] = null;
            }
        }
        // Empty frame/end markers do not invent a blank line. A real LF does.
    }

    public bool PublishUnfinished()
    {
        if (replay is null || !active.Any(e => e is { Visible: false } && e.Text.Length > 0)) return false;
        // An unfinished replay line cannot establish identity. Release it at the next publication batch.
        replay = null; Uncertain();
        foreach (var segment in active)
            if (segment is { Visible: false, Dropped: false } && segment.Text.Length > 0) Show(segment);
        return true;
    }

    public void Complete()
    {
        foreach (var segment in active) if (segment is not null) Finish(segment, partial: true);
        Array.Clear(active);
        if (replay is not null) { replay = null; Uncertain(); }
    }

    private void Finish(Segment segment, bool partial)
    {
        segment.Partial = partial;
        if (partial || segment.Timestamp?.Cursor is null) replayEvidenceLost = true;
        var entry = segment.Snapshot();
        bool duplicate = false;
        if (replay is { } expected)
        {
            // Match only a bounded ordered replay subsequence. Consume occurrences, not a global text set.
            if (replayIndex == 0)
            {
                replayIndex = expected.FindIndex(e => Equivalent(e, entry));
                if (replayIndex > 0) Uncertain();
            }
            if (replayIndex >= 0 && replayIndex < expected.Count && Equivalent(expected[replayIndex], entry))
            {
                duplicate = true;
                if (++replayIndex == expected.Count) replay = null;
            }
            else { replay = null; Uncertain(); }
        }
        if (!duplicate)
        {
            if (!segment.Visible && !segment.Dropped) Show(segment);
            comparison.Enqueue(entry); comparisonCharacters += entry.Text.Length;
            while (comparison.Count > options.MaximumReplayEntries || comparisonCharacters > options.MaximumReplayCharacters)
            {
                var removed = comparison.Dequeue(); comparisonCharacters -= removed.Text.Length;
                if (removed.Timestamp?.Cursor is { } floor)
                {
                    if (comparisonFloor is null || Compare(floor, comparisonFloor.Value) > 0) comparisonFloor = floor;
                }
                else replayEvidenceLost = true;
            }
        }
        Trim();
    }

    private static bool Equivalent(ContainerLogEntry first, ContainerLogEntry second)
        => first.Stream == second.Stream && first.Timestamp?.Cursor is { } cursor && second.Timestamp?.Cursor == cursor
            && first.Text == second.Text && first.Partial == second.Partial && first.Continued == second.Continued;

    private void Show(Segment entry)
    {
        var following = retained.Last;
        while (following is not null && following.Value.Id > entry.Id) following = following.Previous;
        if (following is null) retained.AddFirst(entry); else retained.AddAfter(following, entry);
        entry.Visible = true; Characters += entry.Text.Length; Trim();
    }
    private void Trim()
    {
        while (retained.Count > options.MaximumEntries || Characters > options.MaximumCharacters)
        {
            var oldest = retained.First!.Value;
            retained.RemoveFirst(); oldest.Visible = false; Characters -= oldest.Text.Length; ++Evictions;
            // An evicted unfinished entry cannot reappear or continue accumulating invisibly.
            int stream = (int)oldest.Stream;
            if (ReferenceEquals(active[stream], oldest))
            {
                Discarded += oldest.Text.Length; oldest.Dropped = true; replayEvidenceLost = true;
            }
        }
    }

    public IReadOnlyList<ContainerLogEntry> Read(long after, int count)
        => Array.AsReadOnly(retained.Where(e => e.Id > after).Take(count).Select(e => e.Snapshot()).ToArray());
    public static int Compare(ContainerLogCursor first, ContainerLogCursor second)
        => first.UnixSeconds != second.UnixSeconds ? first.UnixSeconds.CompareTo(second.UnixSeconds) : first.Nanoseconds.CompareTo(second.Nanoseconds);
    private sealed class Segment(long id, long line, ContainerLogStream stream, ContainerLogTimestamp? timestamp)
    {
        public long Id = id, Line = line;
        public ContainerLogStream Stream = stream;
        public ContainerLogTimestamp? Timestamp = timestamp;
        public StringBuilder Text { get; } = new();
        public bool Visible, Continued, Dropped, Partial = true;
        public ContainerLogEntry Snapshot() => new(Id, Line, Stream, Text.ToString(), Timestamp, Continued, Partial);
    }
}
