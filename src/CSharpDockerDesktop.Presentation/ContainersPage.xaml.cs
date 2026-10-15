using System.Collections.ObjectModel;
using CSharpDockerDesktop.Application;
using CSharpDockerDesktop.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace CSharpDockerDesktop.Presentation;

public sealed partial class ContainersPage : Page
{
    private ContainerBrowserView? view;
    private IReadOnlyList<ContainerRow>? displayedRows;
    private bool rendering;
    private ContentDialog? deletionDialog;
    private ContainerDeleteConfirmation? dialogConfirmation;
    private int pageEpoch;
    private IReadOnlyList<JsonInspectRow>? displayedInspect;
    private readonly ObservableCollection<ContainerLogRow> logRows = [];
    public Task StatsCleanup { get; private set; } = Task.CompletedTask;
    public Task LogsCleanup { get; private set; } = Task.CompletedTask;
    public ContainersPage()
    {
        InitializeComponent();
        LogsList.ItemsSource = logRows;
        // ListViewItem handles Enter for selection; also observe it to open the selected resource.
        ContainerList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnListKeyDown), true);
        InspectList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnInspectKeyDown), true);
        Loaded += (_, _) => { if (view is not null) { view.Changed -= OnViewChanged; view.Changed += OnViewChanged; view.Attach(); } };
        Unloaded += (_, _) => Detach();
    }
    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        Detach();
        view = new((IContainerBrowser)args.Parameter, action => DispatcherQueue.TryEnqueue(() => action()));
        view.Changed += OnViewChanged;
        view.Attach();
    }
    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        Detach();
        base.OnNavigatedFrom(args);
    }
    public void Detach()
    {
        ++pageEpoch;
        deletionDialog?.Hide();
        deletionDialog = null; dialogConfirmation = null;
        if (view is not null) view.Changed -= OnViewChanged;
        view?.Dispose();
        if (view is not null) { LogsCleanup = JoinLogsAsync(view.Logs.Cleanup); StatsCleanup = view.Stats.Cleanup; }
        ClearStats();
        logRows.Clear(); LogsDaemonText.Text = LogsSearch.Text = LogsStatus.Text = LogsRetention.Text = LogsSearchStatus.Text = "";
        LogsDaemonPanel.Visibility = Visibility.Collapsed;
        OperationStatus.Text = AdmissionStatus.Text = OperationDetailsText.Text = "";
        OperationDetailsPanel.Visibility = Visibility.Collapsed;
        InspectList.ItemsSource = null; displayedInspect = null; OverviewText.Text = "";
        ContainerList.ItemsSource = null; displayedRows = null;
    }
    private static async Task JoinLogsAsync(Task cleanup) { try { await cleanup; } catch (Exception) { /* Never report sensitive exception content. */ } }
    private void OnViewChanged(object? sender, EventArgs args) => Render();
    private void Render()
    {
        if (view is null) return;
        rendering = true;
        try
        {
            if (deletionDialog is not null && !ReferenceEquals(dialogConfirmation, view.DeleteConfirmation)) deletionDialog.Hide();
            StartButton.IsEnabled = view.CanStart;
            StopButton.IsEnabled = view.CanStop;
            RestartButton.IsEnabled = view.CanRestart;
            DeleteButton.IsEnabled = view.CanDelete;
            ForceDeleteButton.IsEnabled = view.CanForceDelete;
            ActionStatus.Text = view.ActionMessage;
            OperationStatus.Text = view.OperationMessage;
            AdmissionStatus.Text = view.AdmissionMessage;
            OperationDetailsButton.Visibility = view.HasOperationDetails ? Visibility.Visible : Visibility.Collapsed;
            OperationDetailsButton.Content = view.ShowingOperationDetails ? "Hide original daemon content" : "Show original daemon content (may be sensitive)";
            OperationDetailsPanel.Visibility = view.ShowingOperationDetails ? Visibility.Visible : Visibility.Collapsed;
            OperationDetailsText.Text = view.OperationDetails;
            bool rowsChanged = !ReferenceEquals(displayedRows, view.Rows);
            if (rowsChanged)
            {
                ContainerList.SelectedItem = null;
                ContainerList.ItemsSource = null;
                ContainerList.ItemsSource = view.Rows;
                displayedRows = view.Rows;
            }
            var selectedRow = view.Rows.FirstOrDefault(row => row.Id == view.SelectedId);
            ContainerList.SelectedItem = selectedRow;
            ListStatus.Text = view.ListMessage;
            RefreshButton.IsEnabled = view.CanRefresh;
            OpenButton.IsEnabled = view.CanOpen;
            CopyButton.IsEnabled = view.CanOpen;
            bool wasOpen = DetailPanel.Visibility == Visibility.Visible;
            DetailPanel.Visibility = view.DetailOpen ? Visibility.Visible : Visibility.Collapsed;
            // Hide the underlying browser from keyboard navigation while Overview is open.
            SearchBox.Visibility = RefreshButton.Visibility = OpenButton.Visibility = CopyButton.Visibility
                = ContainerList.Visibility = ListStatus.Visibility = view.DetailOpen ? Visibility.Collapsed : Visibility.Visible;
            DetailStatus.Text = view.DetailMessage;
            OverviewText.Text = view.ShowingInspect || view.ShowingLogs || view.ShowingStats ? "" : view.Overview;
            OverviewPanel.Visibility = view.ShowingInspect || view.ShowingLogs || view.ShowingStats ? Visibility.Collapsed : Visibility.Visible;
            InspectPanel.Visibility = view.ShowingInspect ? Visibility.Visible : Visibility.Collapsed;
            OverviewTab.IsEnabled = view.ShowingInspect || view.ShowingLogs || view.ShowingStats;
            InspectTab.IsEnabled = !view.ShowingInspect;
            LogsTab.IsEnabled = !view.ShowingLogs;
            StatsTab.IsEnabled = !view.ShowingStats;
            StatsPanel.Visibility = view.ShowingStats ? Visibility.Visible : Visibility.Collapsed;
            RenderStats();
            LogsPanel.Visibility = view.ShowingLogs ? Visibility.Visible : Visibility.Collapsed;
            RenderLogs();
            if (InspectSearch.Text != view.Inspect.Query) InspectSearch.Text = view.Inspect.Query;
            InspectStatus.Text = view.Inspect.Message;
            if (!ReferenceEquals(displayedInspect, view.Inspect.Rows))
            {
                InspectList.ItemsSource = null;
                InspectList.ItemsSource = view.Inspect.Rows; displayedInspect = view.Inspect.Rows;
            }
            InspectList.SelectedItem = view.Inspect.Selected;
            CopyPathButton.IsEnabled = CopyValueButton.IsEnabled = view.Inspect.CanCopy;
            ExpandNodeButton.IsEnabled = !view.Inspect.Busy && view.Inspect.Selected is { IsContainer: true, Expanded: false };
            CollapseNodeButton.IsEnabled = !view.Inspect.Busy && view.Inspect.Selected is { IsContainer: true, Expanded: true };
            RefreshDetailButton.IsEnabled = view.CanOpen && !view.ShowingLogs && !view.ShowingStats;
            if (!wasOpen && view.DetailOpen) CloseDetailButton.Focus(FocusState.Programmatic);
            else if (wasOpen && !view.DetailOpen) ContainerList.Focus(FocusState.Programmatic);
            // Reset the virtualized viewport after a source replacement or reopening. The old scroll
            // anchor can refer to an index that no longer exists in the filtered collection.
            if (!view.DetailOpen && (rowsChanged || wasOpen) && view.Rows.FirstOrDefault() is { } anchor)
                ContainerList.ScrollIntoView(anchor, ScrollIntoViewAlignment.Leading);
        }
        finally { rendering = false; }
    }
    private void OnStart(object sender, RoutedEventArgs args) => view?.Execute(ContainerCommand.Start);
    private void OnStop(object sender, RoutedEventArgs args) => view?.Execute(ContainerCommand.Stop);
    private void OnRestart(object sender, RoutedEventArgs args) => view?.Execute(ContainerCommand.Restart);
    private void OnDelete(object sender, RoutedEventArgs args) => ShowDelete(false);
    private void OnForceDelete(object sender, RoutedEventArgs args) => ShowDelete(true);
    private void OnOperationDetails(object sender, RoutedEventArgs args) => view?.ShowOperationDetails(!view.ShowingOperationDetails);
    private async void ShowDelete(bool force)
    {
        var owner = view;
        if (deletionDialog is not null || owner?.BeginDelete(force) is not { } confirmation) return;
        int lifetime = pageEpoch;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Confirm {confirmation.Action}",
            Content = new TextBlock { Text = confirmation.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = confirmation.Action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        deletionDialog = dialog; dialogConfirmation = confirmation;
        try
        {
            var result = await dialog.ShowAsync();
            if (lifetime == pageEpoch && ReferenceEquals(view, owner) && result == ContentDialogResult.Primary) owner.ConfirmDelete(confirmation);
            else owner.CancelDelete(confirmation);
        }
        catch (Exception) { owner.CancelDelete(confirmation); }
        finally
        {
            if (ReferenceEquals(deletionDialog, dialog)) { deletionDialog = null; dialogConfirmation = null; }
            if (lifetime == pageEpoch && ReferenceEquals(view, owner))
            {
                Render();
                var button = force ? ForceDeleteButton : DeleteButton;
                if (button.IsEnabled) button.Focus(FocusState.Programmatic);
                else if (owner.DetailOpen) CloseDetailButton.Focus(FocusState.Programmatic);
                else ContainerList.Focus(FocusState.Programmatic);
            }
        }
    }
    private void RenderLogs()
    {
        var logs = view!.Logs;
        LogsStatus.Text = logs.Message; LogsRetention.Text = logs.RetentionMessage; LogsSearchStatus.Text = logs.SearchMessage;
        if (LogsSearch.Text != logs.Query) LogsSearch.Text = logs.Query;
        LogsFilter.IsEnabled = logs.Snapshot.IsTty != true;
        LogsFilter.SelectedIndex = logs.Stream == ContainerLogStream.Stdout ? 1 : logs.Stream == ContainerLogStream.Stderr ? 2 : 0;
        LogsAutoscroll.IsChecked = logs.Autoscroll;
        LogsDaemonButton.Visibility = logs.HasDaemonContent ? Visibility.Visible : Visibility.Collapsed;
        LogsDaemonButton.Content = logs.ShowingDaemonContent ? "Hide original daemon content" : "Show original daemon content (may be sensitive)";
        LogsDaemonPanel.Visibility = logs.ShowingDaemonContent ? Visibility.Visible : Visibility.Collapsed;
        LogsDaemonText.Text = logs.DaemonContent;
        // Preserve recycled rows and scroll offset on partial-text updates. On eviction/page changes,
        // retain a visible identity when possible, otherwise move explicitly to the next retained row.
        ContainerLogRow? anchor = null;
        if (!logs.Autoscroll)
            for (int i = 0; i < logRows.Count; ++i)
                if (LogsList.ContainerFromIndex(i) is FrameworkElement item)
                {
                    var point = item.TransformToVisual(LogsList).TransformPoint(new Windows.Foundation.Point(0, 0));
                    if (point.Y >= 0 && point.Y < LogsList.ActualHeight) { anchor = logRows[i]; break; }
                }
        bool changed = false;
        for (int i = 0; i < logs.Rows.Count; ++i)
        {
            if (i >= logRows.Count) { logRows.Add(logs.Rows[i]); changed = true; }
            else if (logRows[i] != logs.Rows[i]) { logRows[i] = logs.Rows[i]; changed = true; }
        }
        while (logRows.Count > logs.Rows.Count) { logRows.RemoveAt(logRows.Count - 1); changed = true; }
        if (changed && logRows.Count > 0)
        {
            if (logs.Autoscroll) LogsList.ScrollIntoView(logRows[^1]);
            else if (anchor is not null) LogsList.ScrollIntoView(logRows.FirstOrDefault(r => r.Entry.EntryId == anchor.Entry.EntryId) ?? logRows[0], ScrollIntoViewAlignment.Leading);
        }
    }
    private void OnLogsTab(object sender, RoutedEventArgs args) { view?.ShowLogs(LogsFollow.IsChecked == true); LogsSearch.Focus(FocusState.Programmatic); }
    private void OnLogsReopen(object sender, RoutedEventArgs args) => view?.ShowLogs(LogsFollow.IsChecked == true);
    private void OnLogsSearch(object sender, TextChangedEventArgs args) { if (!rendering) view?.Logs.Search(LogsSearch.Text); }
    private void OnLogsFilter(object sender, SelectionChangedEventArgs args) { if (!rendering) view?.Logs.Filter(LogsFilter.SelectedIndex == 1 ? ContainerLogStream.Stdout : LogsFilter.SelectedIndex == 2 ? ContainerLogStream.Stderr : null); }
    private void OnLogsAutoscroll(object sender, RoutedEventArgs args) { if (!rendering) view?.Logs.SetAutoscroll(LogsAutoscroll.IsChecked == true); }
    private void OnLogsClear(object sender, RoutedEventArgs args) => view?.Logs.Clear();
    private void OnLogsOlder(object sender, RoutedEventArgs args) => view?.Logs.Older();
    private void OnLogsNewer(object sender, RoutedEventArgs args) => view?.Logs.Newer();
    private void OnLogsDaemon(object sender, RoutedEventArgs args) { if (view is not null) view.Logs.ShowDaemonContent(!view.Logs.ShowingDaemonContent); }
    private void OnOverviewTab(object sender, RoutedEventArgs args) { view?.ShowInspect(false); RefreshDetailButton.Focus(FocusState.Programmatic); }
    private void OnInspectTab(object sender, RoutedEventArgs args) { view?.ShowInspect(true); InspectSearch.Focus(FocusState.Programmatic); }
    private void OnInspectSearch(object sender, TextChangedEventArgs args) { if (!rendering) view?.Inspect.Search(InspectSearch.Text); }
    private void OnInspectSelection(object sender, SelectionChangedEventArgs args)
    { if (!rendering) view?.Inspect.Select((InspectList.SelectedItem as JsonInspectRow)?.NodeId); }
    private void OnExpandNode(object sender, RoutedEventArgs args) => view?.Inspect.SetExpanded(true);
    private void OnCollapseNode(object sender, RoutedEventArgs args) => view?.Inspect.SetExpanded(false);
    private void OnCopyPath(object sender, RoutedEventArgs args) => view?.Inspect.Copy(true, CopyText);
    private void OnCopyValue(object sender, RoutedEventArgs args) => view?.Inspect.Copy(false, CopyText);
    private static void CopyText(string text) { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); }
    private void OnInspectKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (view?.Inspect.Selected is not { IsContainer: true } || view.Inspect.Busy) return;
        if (args.Key == VirtualKey.Right) { view.Inspect.SetExpanded(true); args.Handled = true; }
        else if (args.Key == VirtualKey.Left) { view.Inspect.SetExpanded(false); args.Handled = true; }
        else if (args.Key == VirtualKey.Enter) { view.Inspect.Toggle(); args.Handled = true; }
    }
    private void OnSearch(object sender, TextChangedEventArgs args) => view?.Search(SearchBox.Text);
    private void OnRefresh(object sender, RoutedEventArgs args) => view?.Refresh();
    private void OnRefreshDetail(object sender, RoutedEventArgs args) => view?.RefreshDetail();
    private void OnOpen(object sender, RoutedEventArgs args) => view?.OpenSelected();
    private void OnCloseDetail(object sender, RoutedEventArgs args) => view?.CloseDetail();
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!rendering && ContainerList.SelectedItem is ContainerRow row) view?.Select(row.Id);
    }
    private void OnListKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && view?.CanOpen == true)
        {
            view.OpenSelected();
            args.Handled = true;
        }
    }
    private void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (view?.CanOpen == true) { view.OpenSelected(); args.Handled = true; }
    }
    private void OnCopy(object sender, RoutedEventArgs args)
    {
        if (view?.SelectedId is not { } id) return;
        try
        {
            var data = new DataPackage();
            data.SetText(id);
            Clipboard.SetContent(data);
            if (view.DetailOpen) DetailStatus.Text = "Full container ID copied.";
            else ListStatus.Text = "Full container ID copied.";
        }
        catch (Exception)
        {
            if (view.DetailOpen) DetailStatus.Text = "Clipboard unavailable. Select and copy the identity in Overview.";
            else ListStatus.Text = "Clipboard unavailable. Open Overview to select and copy the full identity.";
        }
    }
}
