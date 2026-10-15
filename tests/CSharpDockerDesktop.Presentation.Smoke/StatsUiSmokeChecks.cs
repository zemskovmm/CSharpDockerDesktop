using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;

/// <summary>Real Uno controls/geometry with a synthetic Application capability. Native keyboard and
/// screen reader validation, Docker correctness and performance targets remain separate evidence.</summary>
internal static class StatsUiSmokeChecks
{
    public static async Task Run(Frame frame, FixtureBrowser browser, TextBlock evidence)
    {
        var results = new List<string>(); string stage = "start";
        void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); }
        void Pass(string text) { results.Add("PASS " + text); evidence.Text = $"Stats UI: {results.Count} passed. {text}"; }
        T Control<T>(string name) where T : FrameworkElement => (T)((Page)frame.Content).FindName(name);
        async Task Open()
        {
            browser.RefreshContainers(); frame.Navigate(typeof(ContainersPage), browser);
            await Until(() => frame.Content is ContainersPage { IsLoaded: true } && Control<ListView>("ContainerList").Items.Count == 20000);
            var list = Control<ListView>("ContainerList"); list.SelectedItem = list.Items[0];
            Invoke(Control<Button>("OpenButton")); await Until(() => Control<Border>("DetailPanel").Visibility == Visibility.Visible);
        }
        async Task Charts()
        { await Until(() => Control<StackPanel>("StatsCharts").Children.Count == 6 && Control<TextBlock>("StatsValues").Text.Contains("CPU: ")
            && !Control<TextBlock>("StatsValues").Text.Contains("no current measurement")
            && Descendants<Path>(Control<StackPanel>("StatsCharts")).All(p => p.Data is PathGeometry { Figures.Count: > 0 })); }
        try
        {
            browser.StatsScenario = 0; int opens = browser.StatsOpens; await Open();
            Check(browser.StatsOpens == opens && Control<Button>("StatsTab").IsTabStop, "Overview must not acquire Stats and Stats must be keyboard reachable");
            var tab = Control<Button>("StatsTab"); Check(tab.Focus(FocusState.Keyboard), "Stats tab cannot take keyboard focus"); Invoke(tab); await Charts();
            Check(browser.ContainerStats.ContainerId == browser.ContainerSnapshot.SelectedContainerId, "Wrong full identity");
            Check(FocusManager.GetFocusedElement(frame.XamlRoot!) == Control<Button>("StatsReopen"), "Stats tab must transfer focus to its content");
            Check(Control<TextBlock>("StatsStatus").Text.Contains("Live") && Control<TextBlock>("StatsValues").Text.Contains("activity"), "Current state/units not recognizable");
            Pass("explicit Stats tab opens exact selected identity; real control focus and textual current measurements");
            stage = "bounded geometry";
            var paths = Descendants<Path>(Control<StackPanel>("StatsCharts")).ToArray();
            Check(paths.Length == 6 && paths.All(p => p.Data is PathGeometry { Figures.Count: > 1 }), "Charts must have separate continuity figures");
            int vertices = paths.Sum(p => ((PathGeometry)p.Data!).Figures.Sum(f => f.Segments.Count));
            Check(vertices <= 3600 && Control<TextBlock>("StatsRetention").Text.Contains("historical"), "Chart render budget or historical label missing");
            Pass($"six actual chart paths, explicit gap figures, {vertices} bounded vertices and historical/time/scale labels");
            stage = "tabs"; int closes = browser.StatsCloses;
            Invoke(Control<Button>("InspectTab")); Check(browser.StatsCloses == closes + 1 && Control<StackPanel>("StatsCharts").Children.Count == 0, "Inspect must close/clear Stats");
            await Until(() => Control<ListView>("InspectList").Items.Count > 0);
            Invoke(Control<Button>("LogsTab")); await Until(() => browser.LogOpens > 0);
            Invoke(Control<Button>("StatsTab")); await Charts(); Check(browser.LogCloses > 0, "Stats must release Logs ownership");
            Invoke(Control<Button>("OverviewTab")); Check(Control<StackPanel>("StatsCharts").Children.Count == 0, "Overview must clear Stats");
            Invoke(Control<Button>("StatsTab")); await Charts(); Pass("Inspect, Logs, Overview and Stats switching preserves cleanup and reopening");
            stage = "Windows provenance"; browser.StatsScenario = 1; Invoke(Control<Button>("StatsReopen")); await Charts();
            string values = Control<TextBlock>("StatsValues").Text;
            Check(values.Contains("Windows private working set") && values.Contains("unsupported platform") && values.Contains("Memory commit"), "Windows semantics not visible");
            Pass("Windows working set/commit provenance and unavailable percentage in actual controls");
            stage = "states";
            string[] phases = ["lifecycle", "Inactive", "stalled", "Recovering", "completed", "failed"];
            for (int i = 0; i < phases.Length; ++i)
            {
                browser.StatsScenario = i + 2; Invoke(Control<Button>("StatsReopen"));
                string expected = phases[i]; await Until(() => Control<TextBlock>("StatsStatus").Text.Contains(expected, StringComparison.OrdinalIgnoreCase));
                Check(Control<TextBlock>("StatsValues").Text.Contains("no current measurement"), "Invalid phase retained live values");
            }
            Check(!Control<TextBlock>("StatsStatus").Text.Contains("PRIVATE") && Control<TextBlock>("StatsDaemonText").Text == "", "Original daemon content leaked into status");
            Invoke(Control<Button>("StatsDaemonButton")); Check(Control<TextBlock>("StatsDaemonText").Text == "PRIVATE Stats fixture daemon content", "Explicit reveal lost original");
            Pass("awaiting lifecycle, inactive, stalled, recovery, completion and failure states; explicit original-content reveal");
            stage = "sustained/navigation"; browser.StatsScenario = 0; Invoke(Control<Button>("StatsReopen")); await Charts();
            long revision = browser.ContainerStats.Revision;
            var retainedPaths = Descendants<Path>(Control<StackPanel>("StatsCharts")).ToArray(); await Task.Delay(3200);
            Check(browser.ContainerStats.Revision > revision && Control<StackPanel>("StatsCharts").Children.Count == 6, "Sustained samples did not progress");
            Check(retainedPaths.SequenceEqual(Descendants<Path>(Control<StackPanel>("StatsCharts"))), "Sustained updates must reuse bounded chart controls");
            browser.StatsReadDelay = 1500; int reads = browser.StatsReads; Invoke(Control<Button>("StatsReopen"));
            await Until(() => Volatile.Read(ref browser.StatsReads) > reads);
            var page = (ContainersPage)frame.Content; var other = new TextBlock { Text = "Navigation stays responsive during a noncooperative Stats history read." };
            frame.Content = other;
            Check(ReferenceEquals(frame.Content, other), "Slow history read blocked navigation");
            await page.StatsCleanup; browser.StatsReadDelay = 0;
            Check(browser.ContainerStats.Phase == ContainerStatsPhase.Closed, "Departure did not join session closure");
            Pass("sustained real control updates remain bounded; departure during noncooperative history reading is immediate and cleanup joins");
            stage = "disconnect"; await Open(); Invoke(Control<Button>("StatsTab")); await Charts(); browser.Disconnect();
            await Until(() => Control<Border>("DetailPanel").Visibility == Visibility.Collapsed);
            Check(Control<StackPanel>("StatsCharts").Children.Count == 0 && Control<TextBlock>("StatsDaemonText").Text == "", "Disconnect left obsolete Stats visuals");
            Pass("disconnect clears current values, charts and revealed original content");
            browser.StatsScenario = 0; await Open(); Invoke(Control<Button>("StatsTab")); await Charts();
            results.Add($"Completed {results.Count} synthetic Application / actual Uno macOS UI groups. Native keyboard, assistive technology, Windows/Docker and workstation performance require separate validation.");
        }
        catch (Exception error) { browser.StatsReadDelay = 0; results.Add($"FAIL {stage}: {error}"); }
        evidence.Text = string.Join("\n", results); await File.WriteAllLinesAsync("/private/tmp/csharpdocker-stats-ui-smoke.txt", results);
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
    private static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (!predicate()) await Task.Delay(20, timeout.Token); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
