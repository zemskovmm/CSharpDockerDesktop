using CSharpDockerDesktop.Application;
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
    public ContainersPage()
    {
        InitializeComponent();
        // ListViewItem handles Enter for selection; also observe it to open the selected resource.
        ContainerList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnListKeyDown), true);
        Loaded += (_, _) => view?.Attach();
        Unloaded += (_, _) => Detach();
    }
    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        view = new((IContainerBrowser)args.Parameter, action => DispatcherQueue.TryEnqueue(() => action()));
        view.Changed += OnViewChanged;
        view.Attach();
    }
    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        Detach();
        base.OnNavigatedFrom(args);
    }
    public void Detach() => view?.Dispose();
    private void OnViewChanged(object? sender, EventArgs args) => Render();
    private void Render()
    {
        if (view is null) return;
        rendering = true;
        try
        {
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
            OverviewText.Text = view.Overview;
            RefreshDetailButton.IsEnabled = view.CanOpen;
            if (!wasOpen && view.DetailOpen) CloseDetailButton.Focus(FocusState.Programmatic);
            else if (wasOpen && !view.DetailOpen) ContainerList.Focus(FocusState.Programmatic);
            // Reset the virtualized viewport after a source replacement or reopening. The old scroll
            // anchor can refer to an index that no longer exists in the filtered collection.
            if (!view.DetailOpen && (rowsChanged || wasOpen) && view.Rows.FirstOrDefault() is { } anchor)
                ContainerList.ScrollIntoView(anchor, ScrollIntoViewAlignment.Leading);
        }
        finally { rendering = false; }
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
