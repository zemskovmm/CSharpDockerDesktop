using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

/// <summary>Actual Uno visual-tree checks with a synthetic read-only Application boundary.</summary>
internal static class NetworkUiSmokeChecks
{
    public static async Task Run(Frame frame, FixtureBrowser browser, TextBlock evidence)
    {
        var results = new List<string>(); string stage = "start";
        void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }
        void Pass(string text) { results.Add("PASS " + text); evidence.Text = $"Network UI checks: {results.Count} passed. {text}"; }
        T Control<T>(string name) where T : FrameworkElement => (T)((Page)frame.Content).FindName(name);
        async Task Open()
        {
            var list = Control<ListView>("NetworkList"); list.SelectedItem = list.Items[0]; list.ScrollIntoView(list.SelectedItem);
            Invoke(Control<Button>("OpenButton"));
            await Until(() => Control<TextBlock>("OverviewText").Text.Contains("Full endpoint identity"));
        }
        try
        {
            browser.NetworkScenario = 0; browser.RefreshNetworks(); frame.Navigate(typeof(NetworksPage), browser);
            await Until(() => frame.Content is NetworksPage { IsLoaded: true } && Control<ListView>("NetworkList").Items.Count == 20000);
            await Task.Delay(200); stage = "large list";
            var list = Control<ListView>("NetworkList"); int realized = Descendants<ListViewItem>(list).Count();
            Check(realized is > 0 and < 128, "Network table must recycle a bounded number of rows");
            Check(Control<Button>("DeleteButton") is not null, "Network Delete entry point missing");
            Pass($"virtualized table {realized}/20000 realized; network Delete entry point available");
            stage = "loaded search"; Control<TextBox>("SearchBox").Text = "network-19999";
            await Until(() => Control<ListView>("NetworkList").Items.Count == 1);
            Check(((NetworkRow)Control<ListView>("NetworkList").Items[0]).Id == "network-full-opaque-19999", "Search selected wrong opaque identity");
            Control<TextBox>("SearchBox").Text = ""; await Until(() => Control<ListView>("NetworkList").Items.Count == 20000);
            Pass("loaded metadata search and restored 20,000 rows");
            stage = "slow read navigation"; list = Control<ListView>("NetworkList"); list.SelectedItem = list.Items[0]; Invoke(Control<Button>("OpenButton"));
            Check(Control<TextBlock>("DetailStatus").Text.Contains("Loading"), "Inspection should remain asynchronous");
            frame.Content = new TextBlock { Text = "Navigated during slow network inspection" }; await Task.Delay(2800);
            Check(browser.NetworkSnapshot.Inspection is null && browser.NetworkSnapshot.InspectionState == ResourceReadState.NotRequested, "Navigation revived raw data");
            Pass("navigation during slow read cancels inspection and fences late completion");
            frame.Navigate(typeof(NetworksPage), browser); await Until(() => Control<ListView>("NetworkList").Items.Count == 20000);
            stage = "Overview"; await Open();
            string overview = Control<TextBlock>("OverviewText").Text;
            Check(overview.Contains("Full identity: Opaque-NETWORK-é ") && overview.Contains("Internal: false") && overview.Contains("Name: empty string")
                && overview.Contains("Scope: unavailable") && overview.Contains("10.1.0.53") && overview.Contains("Attachments · 20000")
                && overview.Contains("Overview preview is bounded") && overview.Length < NetworkDisplay.OverviewLimit + 200, "Overview metadata/evidence/budget incorrect");
            Invoke(Control<Button>("DetailCopyButton")); Check(Control<TextBlock>("DetailStatus").Text.Contains("Full network ID copied"), "Deliberate identity copy feedback");
            Pass("exact opaque identity, nullable/empty/false metadata, nested IPAM/attachment preview and deliberate ID copy feedback");
            stage = "Inspect"; Invoke(Control<Button>("InspectTab")); await Until(() => Control<ListView>("InspectList").Items.Count > 0);
            Control<TextBox>("InspectSearch").Text = "fixture-49999";
            await Until(() => Control<ListView>("InspectList").Items.Count > 0 && Control<ListView>("InspectList").Items.Cast<JsonInspectRow>().Any(r => r.Text.Contains("fixture-49999")));
            list = Control<ListView>("InspectList"); list.SelectedItem = list.Items[list.Items.Count - 1];
            Invoke(Control<Button>("CopyValueButton")); await Until(() => Control<TextBlock>("InspectStatus").Text.Contains("copied"));
            Invoke(Control<Button>("CopyPathButton")); await Until(() => Control<TextBlock>("InspectStatus").Text.Contains("copied"));
            Control<TextBox>("InspectSearch").Text = "index";
            await Until(() => Control<ListView>("InspectList").Items.Count >= 100000); await Task.Delay(200);
            int nodes = Control<ListView>("InspectList").Items.Count; realized = Descendants<ListViewItem>(Control<ListView>("InspectList")).Count();
            Check(realized is > 0 and < 128, "Inspect realization must remain bounded"); Pass($"virtualized Inspect {realized}/{nodes} realized, collapsed-content search to last value, deliberate path/value copy feedback");
            stage = "external invalidation"; browser.InvalidateSelectedNetwork();
            await Until(() => Control<ListView>("InspectList").Items.Count == 0);
            Check(!Control<Button>("CopyValueButton").IsEnabled, "Invalidation kept stale value-copy enabled");
            Invoke(Control<Button>("CloseDetailButton")); await Task.Delay(2800);
            Check(browser.NetworkSnapshot.Inspection is null && Control<ListView>("NetworkList").Items.Count == 20000, "Close revived obsolete details");
            Pass("external invalidation clears Inspect/copy; closing cancels replacement and restores browser");
            stage = "failed retained and original messages"; browser.NetworkScenario = 2; browser.RefreshNetworks();
            await Until(() => Control<TextBlock>("ListStatus").Text.Contains("HTTP 403"));
            Check(Control<ListView>("NetworkList").Items.Count == 20000 && !Control<TextBlock>("ListStatus").Text.Contains("PRIVATE"), "Retained state or safe diagnostic incorrect");
            Invoke(Control<Button>("EvidenceButton")); await Until(() => Control<TextBlock>("EvidenceText").Text.Contains("  PRIVATE list daemon message\n  "));
            Pass("safe failed-refresh diagnostic, retained rows and deliberate exact original-message reveal");
            stage = "authoritative empty"; browser.NetworkScenario = 1; browser.RefreshNetworks();
            await Until(() => Control<TextBlock>("ListStatus").Text.Contains("No networks")); Check(Control<ListView>("NetworkList").Items.Count == 0 && Control<TextBlock>("EvidenceText").Text == "", "Empty or replaced reveal incorrect");
            Pass("authoritative empty distinct from failure; replaced originals hidden");
            stage = "disconnected"; browser.NetworkScenario = 3; browser.RefreshNetworks();
            await Until(() => Control<TextBlock>("ListStatus").Text.Contains("unavailable"));
            Check(!Control<Button>("RefreshButton").IsEnabled && !Control<Button>("OpenButton").IsEnabled, "Unavailable browser admitted stale work");
            Pass("unavailable/disconnected state disables obsolete commands");
            results.Add($"Completed {results.Count} synthetic Uno/macOS UI groups. Native keyboard, actual clipboard contents, Windows/Docker and performance targets require separate validation.");
        }
        catch (Exception error) { results.Add($"FAIL {stage}: {error}"); }
        evidence.Text = string.Join("\n", results); await File.WriteAllLinesAsync("/private/tmp/csharpdocker-network-ui-smoke.txt", results);
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (!condition()) await Task.Delay(20, timeout.Token); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
