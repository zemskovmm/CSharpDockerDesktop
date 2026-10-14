using System.Text;
using System.Text.Json;

namespace CSharpDockerDesktop.Presentation;

/// <summary>Reusable read-only JSON presentation. Public state and commands belong to the UI dispatcher.
/// Paths use $ followed by [JSON property name, zero-based occurrence] or [array index].
/// Explicit occurrences make even duplicate object names unambiguous. Values copy exact JSON source slices.</summary>
public sealed class JsonInspectView(Func<Action, bool> dispatch) : IDisposable
{
    private CancellationTokenSource? work;
    private long revision;
    private string? source;
    private JsonIndex? index;
    private readonly HashSet<int> expanded = [];
    private readonly HashSet<int> searchCollapsed = [];
    private readonly SemaphoreSlim gate = new(1);
    private int? selected;
    public Func<string, bool>? IsCurrentContent { get; set; }
    public event EventHandler? Changed;
    public IReadOnlyList<JsonInspectRow> Rows { get; private set; } = Array.Empty<JsonInspectRow>();
    public string Query { get; private set; } = "";
    public bool Busy { get; private set; }
    public string Message { get; private set; } = "Inspect unavailable.";
    public JsonInspectRow? Selected => Rows.FirstOrDefault(row => row.NodeId == selected);
    public bool CanCopy => !Busy && Selected is not null;

    public void Load(string? raw)
    {
        if (ReferenceEquals(source, raw)) return;
        Cancel(); source = raw; index = null; expanded.Clear(); searchCollapsed.Clear(); selected = null; Query = "";
        Rows = Array.Empty<JsonInspectRow>();
        if (raw is null) { Busy = false; Message = "Inspect unavailable."; Notify(); return; }
        expanded.Add(0);
        Start();
    }
    public void Search(string query)
    {
        if (source is null || query == Query) return;
        Query = query; searchCollapsed.Clear(); Start();
    }
    public void Select(int? node)
    {
        selected = node is { } id && Rows.Any(row => row.NodeId == id) ? node : null;
        if (Busy && source is not null) Start(); else Notify();
    }
    public void SetExpanded(bool expand)
    {
        if (Busy || Selected is not { IsContainer: true } row) return;
        if (Query.Length > 0)
        { if (expand) searchCollapsed.Remove(row.NodeId); else searchCollapsed.Add(row.NodeId); }
        else if (expand) expanded.Add(row.NodeId); else expanded.Remove(row.NodeId);
        Start();
    }
    public void Toggle() { if (Selected is { } row) SetExpanded(!row.Expanded); }

    /// <summary>Only the caller's explicit copy action may invoke this. Obsolete operations never call publish.</summary>
    public void Copy(bool path, Action<string> publish)
    {
        if (!CanCopy || source is null || index is null || selected is not { } node || IsCurrentContent?.Invoke(source) == false) return;
        string raw = source;
        var currentIndex = index; long current = revision; var token = work!.Token;
        Busy = true; Message = "Preparing selected JSON for copying…"; Notify();
        _ = Task.Run(async () =>
        {
            try
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                string text;
                try
                {
                    token.ThrowIfCancellationRequested();
                    text = path ? currentIndex.Path(node) : currentIndex.Value(node);
                    token.ThrowIfCancellationRequested();
                }
                finally { gate.Release(); }
                dispatch(() =>
                {
                    if (current != revision || token.IsCancellationRequested || IsCurrentContent?.Invoke(raw) == false) return;
                    Busy = false;
                    try { publish(text); Message = path ? "Selected JSON path copied." : "Selected JSON value copied."; }
                    catch (Exception) { Message = "Clipboard unavailable. Copy did not complete."; }
                    Notify();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception) { Fail(current, token); }
        });
    }
    private void Start()
    {
        Cancel(); var cancel = work = new(); var token = cancel.Token; long current = revision;
        string raw = source!; string query = Query; var loaded = index; var open = expanded.ToHashSet(); var collapsed = searchCollapsed.ToHashSet();
        Busy = true; Rows = Array.Empty<JsonInspectRow>(); Message = "Processing Inspect…"; Notify();
        _ = Task.Run(async () =>
        {
            try
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                JsonIndex parsed; IReadOnlyList<JsonInspectRow> rows;
                try
                {
                    parsed = loaded ?? JsonIndex.Parse(raw, token);
                    rows = parsed.Project(query, open, collapsed, token);
                }
                finally { gate.Release(); }
                dispatch(() =>
                {
                    if (current != revision || token.IsCancellationRequested || IsCurrentContent?.Invoke(raw) == false) return;
                    index = parsed; Rows = rows; Busy = false;
                    if (!Rows.Any(row => row.NodeId == selected)) selected = Rows.FirstOrDefault()?.NodeId;
                    Message = query.Length == 0 ? $"Read-only Inspect · {rows.Count} visible nodes" : $"{rows.Count} matching nodes and ancestors · search includes collapsed content";
                    Notify();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception) { Fail(current, token); }
        });
    }
    private void Fail(long current, CancellationToken token) => dispatch(() =>
    {
        if (revision != current || token.IsCancellationRequested || source is { } raw && IsCurrentContent?.Invoke(raw) == false) return;
        Busy = false; Rows = Array.Empty<JsonInspectRow>(); selected = null; index = null;
        // Parser exceptions can contain private property names. Never display/log their messages.
        Message = "Inspect could not be displayed. Refresh details to retry."; Notify();
    });
    private void Cancel() { ++revision; work?.Cancel(); work?.Dispose(); work = null; }
    public void Dispose()
    {
        Cancel(); source = null; index = null; expanded.Clear(); searchCollapsed.Clear(); selected = null; Query = "";
        Busy = false; Rows = Array.Empty<JsonInspectRow>(); Message = "Inspect unavailable.";
    }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class Node(int parent, string segment, string label, int start, int depth, bool container)
    {
        public int Parent = parent, Start = start, End, Depth = depth;
        public string Segment = segment, Label = label, Name = "", Search = "", Preview = "";
        public bool Container = container;
        public int Children;
    }
    private sealed class JsonIndex(byte[] bytes, List<Node> nodes)
    {
        public string Path(int id)
        {
            var parts = new Stack<string>();
            for (int n = id; n > 0; n = nodes[n].Parent) parts.Push(nodes[n].Segment);
            return "$" + string.Concat(parts);
        }
        public string Value(int id) => Encoding.UTF8.GetString(bytes.AsSpan(nodes[id].Start, nodes[id].End - nodes[id].Start));
        public static JsonIndex Parse(string raw, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes = Encoding.UTF8.GetBytes(raw);
            var nodes = new List<Node>();
            var stack = new Stack<(int Id, Dictionary<string, int>? Names)>();
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
            string? property = null;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.TokenType == JsonTokenType.PropertyName) { property = reader.GetString()!; continue; }
                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    int id = stack.Pop().Id; nodes[id].End = checked((int)reader.BytesConsumed);
                    nodes[id].Preview = reader.TokenType == JsonTokenType.EndObject ? $"object · {nodes[id].Children} properties" : $"array · {nodes[id].Children} items";
                    continue;
                }
                int parent = stack.Count == 0 ? -1 : stack.Peek().Id;
                string segment = "", label = "$", decodedName = "";
                if (parent >= 0)
                {
                    var frame = stack.Peek();
                    if (frame.Names is { } names)
                    {
                        string name = property!; decodedName = name; int occurrence = names.GetValueOrDefault(name); names[name] = occurrence + 1;
                        segment = $"[{JsonSerializer.Serialize(name)},{occurrence}]";
                        label = (name.Length > 240 ? JsonSerializer.Serialize(name[..240]) + "…" : JsonSerializer.Serialize(name)) + (occurrence == 0 ? "" : $" (occurrence {occurrence})"); property = null;
                    }
                    else { segment = $"[{nodes[parent].Children}]"; label = segment; }
                    ++nodes[parent].Children;
                }
                bool container = reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray;
                var node = new Node(parent, segment, label, checked((int)reader.TokenStartIndex), stack.Count, container)
                { End = checked((int)reader.BytesConsumed), Name = decodedName };
                nodes.Add(node);
                if (container) stack.Push((nodes.Count - 1, reader.TokenType == JsonTokenType.StartObject ? new() : null));
                else
                {
                    node.Search = reader.TokenType == JsonTokenType.String ? reader.GetString()! : Encoding.UTF8.GetString(reader.ValueSpan);
                    int length = Math.Min(240, node.End - node.Start);
                    node.Preview = Encoding.UTF8.GetString(bytes.AsSpan(node.Start, length)) + (node.End - node.Start > length ? "…" : "");
                }
            }
            if (nodes.Count == 0 || stack.Count != 0) throw new JsonException();
            return new(bytes, nodes);
        }
        public IReadOnlyList<JsonInspectRow> Project(string query, HashSet<int> expanded, HashSet<int> collapsed, CancellationToken token)
        {
            HashSet<int>? included = query.Length == 0 ? null : [];
            if (included is not null)
                for (int i = 0; i < nodes.Count; ++i)
                {
                    token.ThrowIfCancellationRequested();
                    var node = nodes[i];
                    if (!node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && !node.Search.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    for (int n = i; n >= 0 && included.Add(n); n = nodes[n].Parent) { token.ThrowIfCancellationRequested(); }
                }
            var visible = new bool[nodes.Count]; var rows = new List<JsonInspectRow>();
            for (int i = 0; i < nodes.Count; ++i)
            {
                token.ThrowIfCancellationRequested(); var node = nodes[i];
                visible[i] = included is not null ? included.Contains(i) && (node.Parent < 0 || visible[node.Parent] && !collapsed.Contains(node.Parent)) : node.Parent < 0 || visible[node.Parent] && expanded.Contains(node.Parent);
                if (!visible[i]) continue;
                bool open = included is not null ? !collapsed.Contains(i) : expanded.Contains(i);
                string label = new string(' ', Math.Min(24, node.Depth) * 2) + (node.Container ? open ? "▼ " : "▶ " : "  ") + node.Label + ": " + node.Preview;
                rows.Add(new(i, label, node.Container, open));
            }
            return rows.AsReadOnly();
        }
    }
}
public sealed record JsonInspectRow(int NodeId, string Text, bool IsContainer, bool Expanded)
{
    public string AccessibleSummary => Text + (IsContainer ? Expanded ? ", expanded" : ", collapsed" : "");
}
