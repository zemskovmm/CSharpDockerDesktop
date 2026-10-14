using CSharpDockerDesktop.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace CSharpDockerDesktop.Presentation;

public sealed partial class VolumesPage : Page
{
    private VolumeBrowserView? view;
    private IReadOnlyList<VolumeRow>? displayedRows;
    private IReadOnlyList<JsonInspectRow>? displayedInspect;
    private bool rendering, restoreInspectFocus;
    public VolumesPage()
    {
        InitializeComponent();
        VolumeList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnListKeyDown), true);
        InspectList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnInspectKeyDown), true);
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnPageKeyDown), true);
        Loaded += (_, _) => { if (view is not null) { view.Changed -= OnViewChanged; view.Changed += OnViewChanged; view.Attach(); } };
        Unloaded += (_, _) => Detach();
    }
    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args); Detach();
        view = new((IVolumeBrowser)args.Parameter, action => DispatcherQueue.TryEnqueue(() => action()));
        view.Changed += OnViewChanged; view.Attach();
    }
    protected override void OnNavigatedFrom(NavigationEventArgs args) { Detach(); base.OnNavigatedFrom(args); }
    public void Detach()
    {
        if (view is not null) view.Changed -= OnViewChanged;
        view?.Dispose(); restoreInspectFocus = false;
        InspectList.ItemsSource = null; displayedInspect = null; OverviewText.Text = "";
        VolumeList.ItemsSource = null; displayedRows = null;
        InspectSearch.Text = InspectStatus.Text = DetailStatus.Text = "";
        DetailPanel.Visibility = Visibility.Collapsed;
        EvidenceText.Text = WarningStatus.Text = "";
        EvidencePanel.Visibility = Visibility.Collapsed;
    }
    private void OnViewChanged(object? sender, EventArgs args) => Render();
    private void Render()
    {
        if (view is null) return;
        rendering = true;
        try
        {
            WarningStatus.Text = view.WarningMessage;
            EvidenceButton.IsEnabled = view.HasEvidence;
            EvidenceButton.Content = view.ShowingEvidence ? "Hide original daemon evidence" : "Show daemon warnings and original messages (may be sensitive)";
            EvidencePanel.Visibility = view.ShowingEvidence ? Visibility.Visible : Visibility.Collapsed;
            EvidenceText.Text = view.PreparingEvidence ? "Preparing original evidence…" : view.Evidence;
            bool restoreVolumeFocus = !view.DetailOpen && XamlRoot is { } listRoot && IsWithin(FocusManager.GetFocusedElement(listRoot) as DependencyObject, VolumeList);
            bool rowsChanged = !ReferenceEquals(displayedRows, view.Rows);
            if (rowsChanged)
            {
                VolumeList.SelectedItem = null; VolumeList.ItemsSource = null;
                VolumeList.ItemsSource = view.Rows; displayedRows = view.Rows;
            }
            VolumeList.SelectedItem = view.Rows.FirstOrDefault(row => row.Name == view.SelectedName);
            ListStatus.Text = view.ListMessage; RefreshButton.IsEnabled = view.CanRefresh;
            OpenButton.IsEnabled = CopyButton.IsEnabled = view.CanOpen;
            bool wasOpen = DetailPanel.Visibility == Visibility.Visible;
            DetailPanel.Visibility = view.DetailOpen ? Visibility.Visible : Visibility.Collapsed;
            SearchBox.Visibility = RefreshButton.Visibility = OpenButton.Visibility = CopyButton.Visibility
                = VolumeList.Visibility = ListStatus.Visibility = view.DetailOpen ? Visibility.Collapsed : Visibility.Visible;
            DetailStatus.Text = view.DetailMessage; OverviewText.Text = view.ShowingInspect ? "" : view.Overview;
            OverviewPanel.Visibility = view.ShowingInspect ? Visibility.Collapsed : Visibility.Visible;
            InspectPanel.Visibility = view.ShowingInspect ? Visibility.Visible : Visibility.Collapsed;
            OverviewTab.IsEnabled = view.ShowingInspect; InspectTab.IsEnabled = !view.ShowingInspect;
            if (InspectSearch.Text != view.Inspect.Query) InspectSearch.Text = view.Inspect.Query;
            InspectStatus.Text = view.Inspect.Message;
            if (view.ShowingInspect && XamlRoot is { } root && IsWithin(FocusManager.GetFocusedElement(root) as DependencyObject, InspectList)) restoreInspectFocus = true;
            if (!view.ShowingInspect) restoreInspectFocus = false;
            if (!ReferenceEquals(displayedInspect, view.Inspect.Rows))
            {
                InspectList.ItemsSource = null; InspectList.ItemsSource = view.Inspect.Rows; displayedInspect = view.Inspect.Rows;
            }
            InspectList.SelectedItem = view.Inspect.Selected;
            if (restoreInspectFocus && !view.Inspect.Busy && view.Inspect.Selected is { } inspectAnchor)
            {
                InspectList.ScrollIntoView(inspectAnchor);
                InspectList.Focus(FocusState.Programmatic); restoreInspectFocus = false;
            }
            CopyPathButton.IsEnabled = CopyValueButton.IsEnabled = view.Inspect.CanCopy;
            ExpandNodeButton.IsEnabled = !view.Inspect.Busy && view.Inspect.Selected is { IsContainer: true, Expanded: false };
            CollapseNodeButton.IsEnabled = !view.Inspect.Busy && view.Inspect.Selected is { IsContainer: true, Expanded: true };
            RefreshDetailButton.IsEnabled = view.CanOpen;
            if (!wasOpen && view.DetailOpen) CloseDetailButton.Focus(FocusState.Programmatic);
            if (!view.DetailOpen && (rowsChanged || wasOpen) && (view.Rows.FirstOrDefault(row => row.Name == view.SelectedName) ?? view.Rows.FirstOrDefault()) is { } anchor)
                VolumeList.ScrollIntoView(anchor, ScrollIntoViewAlignment.Leading);
            if (!view.DetailOpen && (wasOpen || rowsChanged && restoreVolumeFocus)) FocusSelectedVolume();
        }
        finally { rendering = false; }
    }
    private void FocusSelectedVolume()
    {
        if (VolumeList.SelectedItem is { } selected)
        {
            VolumeList.ScrollIntoView(selected);
            if (VolumeList.ContainerFromItem(selected) is Control control && control.Focus(FocusState.Programmatic)) return;
        }
        VolumeList.Focus(FocusState.Programmatic);
    }
    private static bool IsWithin(DependencyObject? child, DependencyObject parent)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, parent)) return true;
        return false;
    }
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape && view?.DetailOpen == true) { view.CloseDetail(); args.Handled = true; }
        else if (args.Key == VirtualKey.F3) { if (view?.ShowingInspect == true) InspectSearch.Focus(FocusState.Programmatic); else if (view?.DetailOpen != true) SearchBox.Focus(FocusState.Programmatic); args.Handled = true; }
        else if (args.Key == VirtualKey.F5) { if (view?.DetailOpen == true) view.RefreshDetail(); else view?.Refresh(); args.Handled = true; }
    }
    private void OnEvidence(object sender, RoutedEventArgs args) => view?.ShowEvidence(!view.ShowingEvidence);
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
        if (!rendering && VolumeList.SelectedItem is VolumeRow row) view?.Select(row.Name);
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
        if (view is null) return;
        string message = view.CopyIdentity(CopyText) ? "Full volume name copied." : "Copy unavailable. Check the current selection and clipboard.";
        if (view.DetailOpen) DetailStatus.Text = message; else ListStatus.Text = message;
    }
}
