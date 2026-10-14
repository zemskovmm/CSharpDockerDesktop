using System.Collections.Concurrent;
using System.Text.Json;
using CSharpDockerDesktop.Presentation;

internal static class InspectChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static async Task StructureAndSearch()
    {
        var queue = new ConcurrentQueue<Action>();
        bool Post(Action action) { queue.Enqueue(action); return true; }
        using var view = new JsonInspectView(Post);
        async Task Settle()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            do
            {
                while (queue.TryDequeue(out var action)) action();
                if (!view.Busy && queue.IsEmpty) return;
                await Task.Delay(1, timeout.Token);
            } while (true);
        }
        async Task<string> Copy(bool path)
        { string? value = null; view.Copy(path, text => value = text); await Settle(); return value ?? throw new InvalidOperationException("Copy not completed"); }
        const string raw = """{"escaped\"/\\~":{"values":[null,{},[],12345678901234567890.1234567890123456789012345],"Env":"TOKEN=secret\nline","dup":1,"dup":2},"tail":true}""";
        view.Load(raw); await Settle();
        Check(view.Rows.Count == 3 && view.Selected?.NodeId == 0, "Initially only root properties are realized in the source");
        Check(await Copy(false) == raw, "Root copy preserves complete raw response");
        view.Select(1); Check(await Copy(true) == "$[" + JsonSerializer.Serialize("escaped\"/\\~") + ",0]", "Names escaped as JSON in unambiguous path");
        view.SetExpanded(true); await Settle();
        Check(view.Rows.Count == 7, "Object expansion includes duplicates in order");
        var duplicates = view.Rows.Where(row => row.Text.Contains("dup")).ToArray();
        view.Select(duplicates[1].NodeId); Check((await Copy(true)).EndsWith("[\"dup\",1]"), "Duplicate occurrence is unambiguous");
        Check(await Copy(false) == "2", "Copy identifies selected duplicate value");
        view.Search("TOKEN=secret\nline"); await Settle();
        Check(view.Rows.Count == 3, "Nested scalar search decodes escaped values and includes ancestors");
        view.Select(view.Rows[^1].NodeId); Check(await Copy(false) == "\"TOKEN=secret\\nline\"", "Sensitive string copies the exact JSON token");
        view.Search("123456"); await Settle(); view.Select(view.Rows[^1].NodeId);
        Check(await Copy(false) == "12345678901234567890.1234567890123456789012345", "No numeric precision loss");
        Check((await Copy(true)).EndsWith("[\"values\",0][3]"), "Array ordering and index retained");
        view.Search(""); await Settle(); var array = view.Rows.First(row => row.Text.Contains("\"values\"")); view.Select(array.NodeId);
        view.SetExpanded(true); await Settle();
        var leaves = view.Rows.Where(row => row.Text.Contains("[0]:") || row.Text.Contains("[1]:") || row.Text.Contains("[2]:")).ToArray();
        foreach (var (row, expected) in leaves.Zip(new[] { "null", "{}", "[]" }))
        { view.Select(row.NodeId); Check(await Copy(false) == expected, "Null and empty collections retain distinct exact values"); }
        view.Select(array.NodeId); view.SetExpanded(false); await Settle();
        Check(!view.Rows.Any(row => row.Text.Contains("[3]:")), "Collapse removes descendants");
        view.Search("escaped\"/\\~"); await Settle(); Check(view.Rows.Count == 2, "Property search uses decoded names");
        view.Load("{\"items\":[" + string.Join(",", Enumerable.Range(0, 50000).Select(i => $"\"item-{i}\"")) + "]}"); await Settle();
        for (int i = 0; i < 100; ++i) view.Search($"item-{i}");
        view.Search("item-49999"); await Settle();
        Check(view.Rows.Count == 3 && view.Rows[^1].Text.Contains("item-49999"), "Only latest large search publishes");
        view.Select(1); view.SetExpanded(false); await Settle(); Check(view.Rows.Count == 2, "Search ancestors can be collapsed");
        view.SetExpanded(true); await Settle(); Check(view.Rows.Count == 3, "Search ancestor expands again");
        view.Select(view.Rows[^1].NodeId); bool copied = false; view.Copy(false, _ => copied = true); view.Load("{\"replacement\":false}"); await Settle();
        Check(!copied && view.Rows.Any(row => row.Text.Contains("replacement")), "Superseded copy and search never publish");
        view.Load("{\"PRIVATE secret\":broken}"); await Settle();
        Check(view.Rows.Count == 0 && !view.CanCopy && view.Message.Contains("could not") && !view.Message.Contains("PRIVATE"), "Parser failure is visible and excludes private exception content");
        view.Load("{\"unfinished\":[" + string.Join(",", Enumerable.Range(0, 50000)) + "]}"); view.Dispose(); await Task.Delay(20);
        while (queue.TryDequeue(out var action)) action();
        Check(view.Rows.Count == 0 && !view.Busy, "Navigation releases retained work and content");
    }
}
