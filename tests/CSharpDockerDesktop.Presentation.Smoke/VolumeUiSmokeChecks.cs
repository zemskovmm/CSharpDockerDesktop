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
internal static class VolumeUiSmokeChecks
{
    public static async Task Run(Frame frame, FixtureBrowser browser, TextBlock evidence)
    {
        var results = new List<string>(); string stage = "start"; int initialCommands = browser.VolumeCommands;
        void Pass(string text) { results.Add("PASS " + text); evidence.Text = $"Volume UI checks: {results.Count} passed. {text}"; }
        void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); }
        async Task<ListView> Page(int scenario, int selected = 0)
        {
            stage = $"navigate scenario {scenario}"; browser.VolumeScenario = 0; browser.RefreshVolumes(); browser.VolumeDeletionScenario = scenario;
            frame.Navigate(typeof(VolumesPage), browser);
            ListView? list = null;
            await Until(() => frame.Content is VolumesPage { IsLoaded: true } && (list = Find<ListView>(frame, x => x.Name == "VolumeList"))?.Items.Count == 20000);
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
            Check(browser.VolumeDeletionPreparation?.Phase is not (VolumePreparationPhase.Loading or VolumePreparationPhase.Completed), "Cancel must fence preparation synchronously before dialog-close animation completes");
            await Until(() => Dialog(frame) is null); await Task.Delay(100);
        }
        async Task Confirm(ContentDialog dialog)
        {
            await Until(() => dialog.IsPrimaryButtonEnabled);
            Invoke(Find<Button>(dialog, x => x.Content?.ToString() == "Delete volume")!);
            await Until(() => Dialog(frame) is null);
        }
        try
        {
            await Page(5); var dialog = await OpenDelete();
            Check(!dialog.IsPrimaryButtonEnabled && dialog.DefaultButton == ContentDialogButton.Close, "Loading must disable Delete; Cancel must be default");
            await Cancel(dialog); Check(browser.VolumeCommands == initialCommands, "Loading cancellation dispatched a command");
            Pass("loading cancellation, nonblocking async preparation cleanup");

            await Page(1); dialog = await OpenDelete(); await Until(() => dialog.IsPrimaryButtonEnabled);
            var dependencies = Find<ListView>(dialog, x => AutomationProperties.GetAutomationId(x) == "VolumePreparationDependencies")!;
            Check(dependencies.Items.Count == 2 && ((VolumeDependencyRow)dependencies.Items[1]).Text.Contains("exited"), "Running/stopped dependencies must both be visible");
            Check(browser.VolumeDeletionPreparation!.Target.Name == "Volume-Exact-Name", "Exact captured name changed");
            var cancel = Find<Button>(dialog, x => x.Content?.ToString() == "Cancel")!;
            Check(dialog.DefaultButton == ContentDialogButton.Close && Within(FocusManager.GetFocusedElement(frame.XamlRoot!) as DependencyObject, cancel), "Cancel must retain keyboard focus when evidence completes");
            await Cancel(dialog);
            Check(Find<Button>(frame, x => x.Name == "DeleteButton") == FocusManager.GetFocusedElement(frame.XamlRoot!), "Cancel should restore invoking control focus");
            Pass("running/stopped dependencies, exact names, default Cancel and focus restoration");

            await Page(2, 1); dialog = await OpenDelete(); await Until(() => dialog.IsPrimaryButtonEnabled);
            Check(browser.VolumeDeletionPreparation!.Target.Name == "volume-exact-name" && Find<ListView>(dialog, x => AutomationProperties.GetAutomationId(x) == "VolumePreparationDependencies")!.Items.Count == 3
                && Find<TextBlock>(dialog, x => AutomationProperties.GetAutomationId(x) == "VolumePreparationStatus")!.Text.Contains("more relationships may exist"), "Incomplete evidence must retain found/unverified relationships and exact case");
            await Cancel(dialog); Pass("incomplete found relationships, unverified candidates and uncertainty");

            await Page(3); dialog = await OpenDelete(); await Until(() => browser.VolumeDeletionPreparation!.Phase == VolumePreparationPhase.Completed);
            await Task.Delay(100); Check(!dialog.IsPrimaryButtonEnabled, "Unavailable evidence enabled Delete"); await Cancel(dialog); Pass("unavailable evidence cannot enable deletion");

            await Page(4); dialog = await OpenDelete(); await Until(() => browser.VolumeDeletionPreparation!.Phase == VolumePreparationPhase.Failed);
            Check(!dialog.IsPrimaryButtonEnabled, "Failed precheck enabled Delete"); await Cancel(dialog); Pass("classified failed precheck");

            var list = await Page(0); dialog = await OpenDelete(); await Until(() => dialog.IsPrimaryButtonEnabled);
            list.SelectedItem = list.Items[1]; await Until(() => Dialog(frame) is null);
            Check(browser.VolumeCommands == initialCommands, "Selection retargeted confirmation"); Pass("selection change dismisses captured confirmation without mutation");

            await Page(7); dialog = await OpenDelete(); await Until(() => dialog.IsPrimaryButtonEnabled);
            await Until(() => browser.VolumeDeletionPreparation!.Phase == VolumePreparationPhase.Invalidated); await Task.Delay(100);
            Check(!dialog.IsPrimaryButtonEnabled, "Event-invalidated evidence enabled Delete"); await Cancel(dialog); Pass("event invalidation disables completed confirmation");

            await Page(8); dialog = await OpenDelete(); await Until(() => Dialog(frame) is null);
            Check(browser.VolumeCommands == initialCommands, "Generation change dispatched a command"); Pass("connection generation replacement dismisses stale confirmation");

            await Page(5); dialog = await OpenDelete(); frame.Content = new TextBlock { Text = "Navigation during slow volume precheck" };
            await Until(() => Dialog(frame) is null); Check(browser.VolumeCommands == initialCommands, "Navigation dispatched a command"); Pass("navigation during non-cooperative slow preparation");

            await Page(9); dialog = await OpenDelete(); await Confirm(dialog); await Until(() => browser.VolumeOperation?.Pending == false);
            Invoke(Find<Button>(frame, x => x.Name == "OperationDetailsButton")!);
            await Until(() => Find<ListView>(frame, x => x.Name == "OperationDependencyList")?.Items.Count == 3);
            Check(Find<TextBlock>(frame, x => x.Name == "OperationStatus")!.Text.Contains("fresh explicit precheck and confirmation"), "Conflict lacks fresh authorization guidance");
            Pass("dependency conflict, refreshed running/stopped/unverified evidence and fresh-precheck recovery");

            await Page(10); dialog = await OpenDelete(); await Confirm(dialog); int calls = browser.VolumeCommands;
            frame.Content = new TextBlock { Text = "Operation continues under session ownership" };
            await Until(() => browser.VolumeOperation?.Pending == false); await Page(10, 1);
            Check(Find<TextBlock>(frame, x => x.Name == "OperationStatus")!.Text.Contains("Unresolved") && browser.VolumeCommands == calls, "Reopening replayed uncertain mutation or hid evidence");
            Invoke(Find<Button>(frame, x => x.Name == "OperationDetailsButton")!);
            await Until(() => Find<TextBlock>(frame, x => x.Name == "OperationDetailsText")?.Text?.Contains("PRIVATE lost response") == true);
            Pass("lost response, no replay, navigation-independent completion and retained deliberate reveal");

            await Page(6); dialog = await OpenDelete(); await Until(() => dialog.IsPrimaryButtonEnabled); await Task.Delay(200);
            dependencies = Find<ListView>(dialog, x => AutomationProperties.GetAutomationId(x) == "VolumePreparationDependencies")!;
            int realized = Descendants<ListViewItem>(dependencies).Count();
            Check(dependencies.Items.Count == 30000 && realized is > 0 and < 128, "Dependency realization must be bounded");
            dependencies.ScrollIntoView(dependencies.Items[29999]); await Task.Delay(200);
            Check(((VolumeDependencyRow)dependencies.Items[29999]).Id == "unverified-9999", "Last unverified relationship is hidden");
            await Cancel(dialog); Pass($"bounded dependency rendering ({realized}/30000 realized), final unverified candidate reachable");

            await Page(11, 2); dialog = await OpenDelete(); await Until(() => dialog.IsPrimaryButtonEnabled);
            var delete = Find<Button>(dialog, x => x.Content?.ToString() == "Delete volume")!; calls = browser.VolumeCommands;
            Invoke(delete); Invoke(delete); await Until(() => Dialog(frame) is null); await Until(() => browser.VolumeOperation?.Pending == false);
            Check(browser.VolumeCommands == calls + 1 && browser.LastVolumeRequest!.Target.Name == " Volume-Exact-é "
                && Find<TextBlock>(frame, x => x.Name == "OperationStatus")!.Text.Contains("recreated volume"), "Duplicate submission, exact whitespace/Unicode name or recreation explanation failed");
            Pass("single submission, exact whitespace/Unicode target and acknowledged-but-present recreation explanation");

            list = await Page(1); int volumeRealized = Descendants<ListViewItem>(list).Count();
            Check(volumeRealized is > 0 and < 128, "Existing volume table virtualization regressed");
            Invoke(Find<Button>(frame, x => x.Name == "OpenButton")!);
            await Until(() => Find<TextBlock>(frame, x => x.Name == "OverviewText")?.Text?.Contains("Mountpoint") == true);
            Invoke(Find<Button>(frame, x => x.Name == "InspectTab")!);
            await Until(() => Find<ListView>(frame, x => x.Name == "InspectList")?.Items.Count > 0);
            Invoke(Find<Button>(frame, x => x.Name == "DetailDeleteButton")!); await Until(() => Dialog(frame) is not null); dialog = Dialog(frame)!;
            await Until(() => dialog.IsPrimaryButtonEnabled); await Cancel(dialog);
            Check(Find<ListView>(frame, x => x.Name == "InspectList")?.Items.Count > 0, "Cancel lost existing Inspect");
            Pass($"existing browsing ({volumeRealized}/20000 realized), Overview/Inspect and detail Delete coexist");
            results.Add($"Completed {results.Count} synthetic Uno/macOS UI groups; real Windows/Docker validation unavailable.");
        }
        catch (Exception error) { results.Add($"FAIL {stage}: {error}; preparation {browser.VolumePrepares}/{browser.VolumeDeletionPreparation?.Phase}"); }
        evidence.Text = string.Join("\n", results);
        await File.WriteAllLinesAsync("/private/tmp/csharpdocker-volume-ui-smoke.txt", results);
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
