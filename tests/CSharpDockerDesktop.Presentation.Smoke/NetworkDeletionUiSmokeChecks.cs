using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using CSharpDockerDesktop.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

/// <summary>Exercises real Uno pages, dialog controls and recycling with a synthetic Application boundary.</summary>
internal static class NetworkDeletionUiSmokeChecks
{
    public static async Task Run(Frame frame, FixtureBrowser browser, TextBlock evidence)
    {
        var results = new List<string>(); string stage = "start"; int initialCommands = browser.NetworkCommands;
        void Pass(string text) { results.Add("PASS " + text); evidence.Text = $"Network UI checks: {results.Count} passed. {text}"; }
        void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); }
        async Task<ListView> Page(int scenario, int selected = 0)
        {
            stage = $"navigate scenario {scenario}"; browser.NetworkScenario = 0; browser.RefreshNetworks(); browser.NetworkDeletionScenario = scenario;
            frame.Navigate(typeof(NetworksPage), browser);
            ListView? list = null;
            await Until(() => frame.Content is NetworksPage { IsLoaded: true } && (list = Find<ListView>(frame, x => x.Name == "NetworkList"))?.Items.Count == 20000);
            list!.SelectedItem = list.Items[selected]; list.ScrollIntoView(list.SelectedItem); await Task.Delay(100);
            return list;
        }
        async Task<ContentDialog> OpenDelete()
        {
            stage = "open deletion dialog"; var button = Find<Button>(frame, x => x.Name == "DeleteButton")!;
            button.Focus(FocusState.Keyboard); Invoke(button);
            await Until(() => Dialog(frame) is not null); await Task.Delay(150); return Dialog(frame)!;
        }
        async Task Cancel(ContentDialog dialog)
        {
            stage = "cancel deletion dialog"; Invoke(Find<Button>(dialog, x => x.Content?.ToString() == "Cancel")!);
            Check(browser.NetworkDeletionPreparation?.Phase is not (NetworkPreparationPhase.Loading or NetworkPreparationPhase.Completed), "Cancel must fence preparation synchronously before dialog-close animation completes");
            await Until(() => Dialog(frame) is null); await Task.Delay(100);
        }
        async Task Confirm(ContentDialog dialog)
        {
            await Until(() => dialog.IsPrimaryButtonEnabled);
            Invoke(Find<Button>(dialog, x => x.Content?.ToString() == "Delete network")!);
            await Until(() => Dialog(frame) is null);
        }
        try
        {
            for (int builtIn = 2; builtIn <= 4; ++builtIn)
            {
                await Page(0, builtIn);
                Check(!Find<Button>(frame, x => x.Name == "DeleteButton")!.IsEnabled, "Built-in network enabled Delete");
            }
            Pass("bridge, host and none disable Delete before preparation");
            await Page(5); var dialog = await OpenDelete();
            Check(!dialog.IsPrimaryButtonEnabled && dialog.DefaultButton == ContentDialogButton.Close, "Loading must disable Delete; Cancel must be default");
            await Cancel(dialog); Check(browser.NetworkCommands == initialCommands, "Loading cancellation dispatched a command");
            Pass("loading cancellation, nonblocking async preparation cleanup");

            await Page(1); dialog = await OpenDelete(); await Until(() => browser.NetworkDeletionPreparation!.Phase == NetworkPreparationPhase.Completed); await Task.Delay(150);
            var attachments = Find<ListView>(dialog, x => AutomationProperties.GetAutomationId(x) == "NetworkPreparationAttachments")!;
            Check(attachments.Items.Count == 2 && ((NetworkAttachmentRow)attachments.Items[1]).Text.Contains("full-endpoint-1") && !dialog.IsPrimaryButtonEnabled, "Attached endpoints must be visible and block deletion");
            Check(browser.NetworkDeletionPreparation!.Target.Id == "Opaque-NETWORK-é ", "Exact captured name changed");
            var cancel = Find<Button>(dialog, x => x.Content?.ToString() == "Cancel")!;
            Check(dialog.DefaultButton == ContentDialogButton.Close && Within(FocusManager.GetFocusedElement(frame.XamlRoot!) as DependencyObject, cancel), "Cancel must retain keyboard focus when evidence completes");
            await Cancel(dialog);
            Check(Find<Button>(frame, x => x.Name == "DeleteButton") == FocusManager.GetFocusedElement(frame.XamlRoot!), "Cancel should restore invoking control focus");
            Pass("blocked endpoint attachments, exact ID, default Cancel and focus restoration");

            await Page(3); dialog = await OpenDelete(); await Until(() => browser.NetworkDeletionPreparation!.Phase == NetworkPreparationPhase.Completed);
            await Task.Delay(100); Check(!dialog.IsPrimaryButtonEnabled, "Unavailable evidence enabled Delete"); await Cancel(dialog); Pass("unavailable evidence cannot enable deletion");

            await Page(4); dialog = await OpenDelete(); await Until(() => browser.NetworkDeletionPreparation!.Phase == NetworkPreparationPhase.Failed);
            Check(!dialog.IsPrimaryButtonEnabled, "Failed precheck enabled Delete"); await Cancel(dialog); Pass("classified failed precheck");

            var list = await Page(0); dialog = await OpenDelete(); await Until(() => dialog.IsPrimaryButtonEnabled);
            list.SelectedItem = list.Items[1]; await Until(() => Dialog(frame) is null);
            Check(browser.NetworkCommands == initialCommands, "Selection retargeted confirmation"); Pass("selection change dismisses captured confirmation without mutation");

            await Page(7); dialog = await OpenDelete(); await Until(() => dialog.IsPrimaryButtonEnabled);
            await Until(() => browser.NetworkDeletionPreparation!.Phase == NetworkPreparationPhase.Invalidated); await Task.Delay(100);
            Check(!dialog.IsPrimaryButtonEnabled, "Event-invalidated evidence enabled Delete"); await Cancel(dialog); Pass("event invalidation disables completed confirmation");

            await Page(8); dialog = await OpenDelete(); await Until(() => Dialog(frame) is null);
            Check(browser.NetworkCommands == initialCommands, "Generation change dispatched a command"); Pass("connection generation replacement dismisses stale confirmation");

            await Page(5); dialog = await OpenDelete(); frame.Content = new TextBlock { Text = "Navigation during slow network precheck" };
            await Until(() => Dialog(frame) is null); Check(browser.NetworkCommands == initialCommands, "Navigation dispatched a command"); Pass("navigation during non-cooperative slow preparation");

            await Page(9); dialog = await OpenDelete(); await Confirm(dialog); await Until(() => browser.NetworkOperation?.Pending == false);
            Invoke(Find<Button>(frame, x => x.Name == "OperationDetailsButton")!);
            await Until(() => Find<ListView>(frame, x => x.Name == "OperationAttachmentList")?.Items.Count == 2);
            Check(Find<TextBlock>(frame, x => x.Name == "OperationStatus")!.Text.Contains("fresh explicit precheck and confirmation"), "Conflict lacks fresh authorization guidance");
            Pass("attachment conflict, refreshed attachment/endpoint evidence and fresh-precheck recovery");

            await Page(10); dialog = await OpenDelete(); await Confirm(dialog); int calls = browser.NetworkCommands;
            frame.Content = new TextBlock { Text = "Operation continues under session ownership" };
            await Until(() => browser.NetworkOperation?.Pending == false); await Page(10, 1);
            Check(Find<TextBlock>(frame, x => x.Name == "OperationStatus")!.Text.Contains("Unresolved") && browser.NetworkCommands == calls, "Reopening replayed uncertain mutation or hid evidence");
            Invoke(Find<Button>(frame, x => x.Name == "OperationDetailsButton")!);
            await Until(() => Find<TextBlock>(frame, x => x.Name == "OperationDetailsText")?.Text?.Contains("PRIVATE lost response") == true);
            Pass("lost response, no replay, navigation-independent completion and retained deliberate reveal");

            await Page(6); dialog = await OpenDelete(); await Until(() => Find<ListView>(dialog, x => AutomationProperties.GetAutomationId(x) == "NetworkPreparationAttachments")?.Items.Count == 30000); await Task.Delay(200);
            attachments = Find<ListView>(dialog, x => AutomationProperties.GetAutomationId(x) == "NetworkPreparationAttachments")!;
            int realized = Descendants<ListViewItem>(attachments).Count();
            Check(attachments.Items.Count == 30000 && realized is > 0 and < 128, "Attachment realization must be bounded");
            attachments.ScrollIntoView(attachments.Items[29999]); await Task.Delay(200);
            Check(((NetworkAttachmentRow)attachments.Items[29999]).Id == "full-container-29999", "Last attachment is hidden");
            await Cancel(dialog); Pass($"bounded attachment rendering ({realized}/30000 realized), final attachment reachable");

            await Page(11, 0); dialog = await OpenDelete(); await Until(() => dialog.IsPrimaryButtonEnabled);
            var delete = Find<Button>(dialog, x => x.Content?.ToString() == "Delete network")!; calls = browser.NetworkCommands;
            Invoke(delete); Invoke(delete); await Until(() => Dialog(frame) is null); await Until(() => browser.NetworkOperation?.Pending == false);
            Check(browser.NetworkCommands == calls + 1 && browser.LastNetworkRequest!.Target.Id == "Opaque-NETWORK-é "
                && Find<TextBlock>(frame, x => x.Name == "OperationStatus")!.Text.Contains("Observed present"), "Duplicate submission, exact whitespace/Unicode name or presence explanation failed");
            Pass("single submission, exact whitespace/Unicode target and acknowledged-but-present presence explanation");

            list = await Page(1); int networkRealized = Descendants<ListViewItem>(list).Count();
            Check(networkRealized is > 0 and < 128, $"Existing network table virtualization regressed: {networkRealized} realized, height {list.ActualHeight}");
            Invoke(Find<Button>(frame, x => x.Name == "OpenButton")!);
            await Until(() => Find<TextBlock>(frame, x => x.Name == "OverviewText")?.Text?.Contains("Full endpoint identity") == true);
            Invoke(Find<Button>(frame, x => x.Name == "InspectTab")!);
            await Until(() => Find<ListView>(frame, x => x.Name == "InspectList")?.Items.Count > 0);
            Invoke(Find<Button>(frame, x => x.Name == "DetailDeleteButton")!); await Until(() => Dialog(frame) is not null); dialog = Dialog(frame)!;
            await Until(() => browser.NetworkDeletionPreparation!.Phase == NetworkPreparationPhase.Completed); await Cancel(dialog);
            Check(Find<ListView>(frame, x => x.Name == "InspectList")?.Items.Count > 0, "Cancel lost existing Inspect");
            Pass($"existing browsing ({networkRealized}/20000 realized), Overview/Inspect and detail Delete coexist");
            await Page(0, 1); dialog = await OpenDelete(); await Confirm(dialog);
            await Until(() => browser.NetworkOperation?.Pending == false && Find<ListView>(frame, x => x.Name == "NetworkList")?.Items.Count == 19999);
            await Task.Delay(200);
            Check(Within(FocusManager.GetFocusedElement(frame.XamlRoot!) as DependencyObject, Find<ListView>(frame, x => x.Name == "NetworkList")!), $"Observed target disappearance must restore browser row focus after asynchronous filtering; focused {(FocusManager.GetFocusedElement(frame.XamlRoot!) as FrameworkElement)?.Name}/{FocusManager.GetFocusedElement(frame.XamlRoot!)?.GetType().Name}");
            Pass("observed absence and browser focus after asynchronous target removal");
            results.Add($"Completed {results.Count} synthetic Uno/macOS UI groups; real Windows/Docker validation unavailable.");
        }
        catch (Exception error) { results.Add($"FAIL {stage}: {error}; preparation {browser.NetworkPrepares}/{browser.NetworkDeletionPreparation?.Phase}/{browser.NetworkDeletionPreparation?.Failure?.Kind}"); }
        evidence.Text = string.Join("\n", results);
        await File.WriteAllLinesAsync("/private/tmp/csharpdocker-network-deletion-ui-smoke.txt", results);
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!condition()) await Task.Delay(20, timeout.Token); }
    private static ContentDialog? Dialog(Frame frame) => VisualTreeHelper.GetOpenPopupsForXamlRoot(frame.XamlRoot!).SelectMany(popup => popup.Child is { } child ? Descendants<ContentDialog>(child) : []).FirstOrDefault();
    private static T? Find<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        // Frame retains outgoing page visuals during navigation transitions; only its
        // current content owns actions. Never activate a detached page's control.
        if (root is Frame { Content: DependencyObject current }) root = current;
        var found = Descendants<T>(root).FirstOrDefault(predicate);
        if (root is Page page && found is FrameworkElement { Name.Length: > 0 } named
            && page.FindName(named.Name) is T canonical && predicate(canonical)) return canonical;
        return found;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static bool Within(DependencyObject? child, DependencyObject parent)
    { for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current)) if (ReferenceEquals(current, parent)) return true; return false; }
}
