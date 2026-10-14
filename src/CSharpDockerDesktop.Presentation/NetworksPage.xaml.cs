using CSharpDockerDesktop.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace CSharpDockerDesktop.Presentation;

public sealed partial class NetworksPage : Page
{
    private NetworkBrowserView? view;
    private IReadOnlyList<NetworkRow>? displayedRows;
    private IReadOnlyList<JsonInspectRow>? displayedInspect;
    private bool rendering, restoreInspectFocus, restoreNetworkFocus, networkFocusQueued;
    private ContentDialog? deletionDialog;
    private NetworkDeletePresentation? dialogPresentation;
    private NetworkDeleteConfirmation? dialogConfirmation;
    private TextBlock? preparationStatus, preparationOriginals, preparationTarget;
    private Button? preparationReveal;
    private ListView? attachmentList;
    private int pageEpoch;
    private long? displayedOperationId;
    public NetworksPage()
    {
        InitializeComponent();
        NetworkList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnListKeyDown), true);
        InspectList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnInspectKeyDown), true);
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnPageKeyDown), true);
        Loaded += (_, _) => { BindButtonClicks(); if (view is not null) { view.Changed -= OnViewChanged; view.Changed += OnViewChanged; view.Attach(); } };
        Unloaded += (_, _) => Detach();
    }
    private void BindButtonClicks()
    {
        // Resolve the loaded controls and rebind idempotently across Uno page reattachment.
        RefreshButton.Click -= OnRefresh;
        RefreshButton.Click += OnRefresh;
        OpenButton.Click -= OnOpen;
        OpenButton.Click += OnOpen;
        CopyButton.Click -= OnCopy;
        CopyButton.Click += OnCopy;
        DeleteButton.Click -= OnDelete;
        DeleteButton.Click += OnDelete;
        EvidenceButton.Click -= OnEvidence;
        EvidenceButton.Click += OnEvidence;
        OperationDetailsButton.Click -= OnOperationDetails;
        OperationDetailsButton.Click += OnOperationDetails;
        CopyAttachmentButton.Click -= OnCopyRefreshedAttachment;
        CopyAttachmentButton.Click += OnCopyRefreshedAttachment;
        CloseDetailButton.Click -= OnCloseDetail;
        CloseDetailButton.Click += OnCloseDetail;
        OverviewTab.Click -= OnOverviewTab;
        OverviewTab.Click += OnOverviewTab;
        InspectTab.Click -= OnInspectTab;
        InspectTab.Click += OnInspectTab;
        RefreshDetailButton.Click -= OnRefreshDetail;
        RefreshDetailButton.Click += OnRefreshDetail;
        DetailCopyButton.Click -= OnCopy;
        DetailCopyButton.Click += OnCopy;
        DetailDeleteButton.Click -= OnDelete;
        DetailDeleteButton.Click += OnDelete;
        ExpandNodeButton.Click -= OnExpandNode;
        ExpandNodeButton.Click += OnExpandNode;
        CollapseNodeButton.Click -= OnCollapseNode;
        CollapseNodeButton.Click += OnCollapseNode;
        CopyPathButton.Click -= OnCopyPath;
        CopyPathButton.Click += OnCopyPath;
        CopyValueButton.Click -= OnCopyValue;
        CopyValueButton.Click += OnCopyValue;
    }
    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args); Detach();
        view = new((INetworkBrowser)args.Parameter, action => DispatcherQueue.TryEnqueue(() => action()));
        view.Changed += OnViewChanged; view.Attach();
    }
    protected override void OnNavigatedFrom(NavigationEventArgs args) { Detach(); base.OnNavigatedFrom(args); }
    public void Detach()
    {
        ++pageEpoch;
        deletionDialog?.Hide(); deletionDialog = null; dialogPresentation = null; dialogConfirmation = null;
        if (attachmentList is not null) attachmentList.ItemsSource = null;
        if (preparationStatus is not null) preparationStatus.Text = "";
        if (preparationOriginals is not null) preparationOriginals.Text = "";
        if (preparationTarget is not null) preparationTarget.Text = "";
        attachmentList = null; preparationStatus = preparationOriginals = preparationTarget = null; preparationReveal = null;
        if (view is not null) view.Changed -= OnViewChanged;
        view?.Dispose(); restoreInspectFocus = restoreNetworkFocus = networkFocusQueued = false;
        InspectList.ItemsSource = null; displayedInspect = null; OverviewText.Text = "";
        NetworkList.ItemsSource = null; displayedRows = null;
        InspectSearch.Text = InspectStatus.Text = DetailStatus.Text = "";
        DetailPanel.Visibility = Visibility.Collapsed;
        EvidenceText.Text = "";
        EvidencePanel.Visibility = Visibility.Collapsed;
        OperationAttachmentList.ItemsSource = null; displayedOperationId = null;
        OperationStatus.Text = AdmissionStatus.Text = OperationDetailsText.Text = "";
        OperationDetailsPanel.Visibility = Visibility.Collapsed;
    }
    private void OnViewChanged(object? sender, EventArgs args) => Render();
    private void Render()
    {
        if (view is null) return;
        rendering = true;
        try
        {
            RenderDeletion();
            DeleteButton.IsEnabled = DetailDeleteButton.IsEnabled = view.CanDelete;
            OperationStatus.Text = view.OperationMessage; AdmissionStatus.Text = view.AdmissionMessage;
            OperationDetailsButton.Visibility = view.HasOperationDetails ? Visibility.Visible : Visibility.Collapsed;
            OperationDetailsButton.Content = view.ShowingOperationDetails ? "Hide original operation evidence and refreshed attachments" : "Show original operation evidence and refreshed attachments (may be sensitive)";
            OperationDetailsPanel.Visibility = view.ShowingOperationDetails ? Visibility.Visible : Visibility.Collapsed;
            OperationDetailsText.Text = view.PreparingOperationDetails ? "Preparing original operation evidence…" : view.OperationDetails;
            if (displayedOperationId != view.Operation?.OperationId || view.Operation?.RefreshedInspection is not null && OperationAttachmentList.ItemsSource is null)
            { displayedOperationId = view.Operation?.OperationId; OperationAttachmentSource.SelectedIndex = view.Operation?.RefreshedInspection is null ? 0 : 1; }
            OperationAttachmentList.ItemsSource = view.ShowingOperationDetails
                ? OperationAttachmentSource.SelectedIndex == 0 ? view.OperationPreparationAttachments : view.RefreshedAttachments : null;
            EvidenceButton.Visibility = view.HasEvidence ? Visibility.Visible : Visibility.Collapsed;
            EvidenceButton.Content = view.ShowingEvidence ? "Hide original daemon messages" : "Show original daemon messages (may be sensitive)";
            EvidencePanel.Visibility = view.ShowingEvidence ? Visibility.Visible : Visibility.Collapsed;
            EvidenceText.Text = view.PreparingEvidence ? "Preparing original evidence…" : view.Evidence;
            bool hadNetworkFocus = !view.DetailOpen && XamlRoot is { } listRoot && IsWithin(FocusManager.GetFocusedElement(listRoot) as DependencyObject, NetworkList);
            bool rowsChanged = !ReferenceEquals(displayedRows, view.Rows);
            if (rowsChanged)
            {
                NetworkList.SelectedItem = null; NetworkList.ItemsSource = null;
                NetworkList.ItemsSource = view.Rows; displayedRows = view.Rows;
            }
            NetworkList.SelectedItem = view.Rows.FirstOrDefault(row => row.Id == view.SelectedId);
            ListStatus.Text = view.ListMessage; RefreshButton.IsEnabled = view.CanRefresh;
            OpenButton.IsEnabled = CopyButton.IsEnabled = view.CanOpen;
            bool wasOpen = DetailPanel.Visibility == Visibility.Visible;
            DetailPanel.Visibility = view.DetailOpen ? Visibility.Visible : Visibility.Collapsed;
            SearchBox.Visibility = RefreshButton.Visibility = OpenButton.Visibility = CopyButton.Visibility = DeleteButton.Visibility
                = NetworkList.Visibility = ListStatus.Visibility = view.DetailOpen ? Visibility.Collapsed : Visibility.Visible;
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
            if (deletionDialog is null && restoreInspectFocus && !view.Inspect.Busy && view.Inspect.Selected is { } inspectAnchor)
            {
                InspectList.ScrollIntoView(inspectAnchor);
                InspectList.Focus(FocusState.Programmatic); restoreInspectFocus = false;
            }
            CopyPathButton.IsEnabled = CopyValueButton.IsEnabled = view.Inspect.CanCopy;
            ExpandNodeButton.IsEnabled = !view.Inspect.Busy && view.Inspect.Selected is { IsContainer: true, Expanded: false };
            CollapseNodeButton.IsEnabled = !view.Inspect.Busy && view.Inspect.Selected is { IsContainer: true, Expanded: true };
            RefreshDetailButton.IsEnabled = view.CanOpen;
            if (!wasOpen && view.DetailOpen) CloseDetailButton.Focus(FocusState.Programmatic);
            if (!view.DetailOpen && (rowsChanged || wasOpen) && (view.Rows.FirstOrDefault(row => row.Id == view.SelectedId) ?? view.Rows.FirstOrDefault()) is { } anchor)
                NetworkList.ScrollIntoView(anchor, ScrollIntoViewAlignment.Leading);
            if (!view.DetailOpen && (wasOpen || rowsChanged && hadNetworkFocus)) restoreNetworkFocus = true;
            QueueNetworkFocus();
        }
        finally { rendering = false; }
    }
    private void QueueNetworkFocus()
    {
        if (networkFocusQueued || !restoreNetworkFocus || deletionDialog is not null || view?.DetailOpen != false) return;
        int lifetime = pageEpoch; networkFocusQueued = true;
        // Inspect clearing can notify synchronously before the same update starts filtering.
        // Defer until that update has completed before deciding whether rows are ready.
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (lifetime != pageEpoch) return;
            networkFocusQueued = false;
            if (deletionDialog is null && view is { DetailOpen: false, Filtering: false } && restoreNetworkFocus)
                restoreNetworkFocus = !FocusSelectedNetwork();
        })) networkFocusQueued = false;
    }
    private bool FocusSelectedNetwork()
    {
        if ((NetworkList.SelectedItem ?? NetworkList.Items.FirstOrDefault()) is { } selected)
        {
            NetworkList.ScrollIntoView(selected);
            if (NetworkList.ContainerFromItem(selected) is Control control && control.Focus(FocusState.Programmatic)) return true;
        }
        return NetworkList.Items.Count > 0 ? NetworkList.Focus(FocusState.Programmatic)
            : RefreshButton.IsEnabled ? RefreshButton.Focus(FocusState.Programmatic) : SearchBox.Focus(FocusState.Programmatic);
    }
    private static bool IsWithin(DependencyObject? child, DependencyObject parent)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, parent)) return true;
        return false;
    }
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (deletionDialog is not null) return;
        if (args.Key == VirtualKey.Delete && view?.CanDelete == true && XamlRoot is { } root
            && FocusManager.GetFocusedElement(root) is not TextBox)
        { ShowDelete(); args.Handled = true; return; }
        if (args.Key == VirtualKey.Escape && view?.DetailOpen == true) { view.CloseDetail(); args.Handled = true; }
        else if (args.Key == VirtualKey.F3) { if (view?.DetailOpen == true) { view.ShowInspect(true); InspectSearch.Focus(FocusState.Programmatic); } else SearchBox.Focus(FocusState.Programmatic); args.Handled = true; }
        else if (args.Key == VirtualKey.F5) { if (view?.DetailOpen == true) view.RefreshDetail(); else view?.Refresh(); args.Handled = true; }
    }
    private void OnDelete(object sender, RoutedEventArgs args) => ShowDelete();
    private void OnOperationAttachmentSource(object sender, SelectionChangedEventArgs args) { if (!rendering && view is not null) Render(); }
    private void OnOperationDetails(object sender, RoutedEventArgs args) => view?.ShowOperationDetails(!view.ShowingOperationDetails);
    private async void OnCopyRefreshedAttachment(object sender, RoutedEventArgs args)
    {
        if (view is { } owner && OperationAttachmentList.SelectedItem is NetworkAttachmentRow row)
            await owner.CopyAttachmentAsync(row, true, CopyText);
    }
    private void RenderDeletion()
    {
        if (deletionDialog is not { } dialog || view is not { } owner) return;
        if (!ReferenceEquals(dialogPresentation, owner.Deletion))
        {
            dialog.IsPrimaryButtonEnabled = false; dialogConfirmation = null;
            if (attachmentList is not null) attachmentList.ItemsSource = null;
            if (preparationStatus is not null) preparationStatus.Text = "";
            if (preparationOriginals is not null) preparationOriginals.Text = "";
            if (preparationTarget is not null) preparationTarget.Text = "";
            dialog.Hide(); return;
        }
        dialogConfirmation = owner.DeleteConfirmation;
        dialog.IsPrimaryButtonEnabled = dialogConfirmation is not null;
        if (preparationStatus is not null) preparationStatus.Text = owner.PreparationMessage;
        if (attachmentList is not null) attachmentList.ItemsSource = owner.PreparationAttachments;
        if (preparationReveal is not null)
        {
            preparationReveal.Visibility = owner.HasPreparationDetails ? Visibility.Visible : Visibility.Collapsed;
            preparationReveal.Content = owner.ShowingPreparationDetails ? "Hide original precheck failure" : "Show original precheck failure (may be sensitive)";
        }
        if (preparationOriginals is not null) preparationOriginals.Text = owner.PreparationDetails;
    }
    private static bool FocusCancel(DependencyObject current, ContentDialog dialog)
    {
        if (current is Button button && button.Content?.ToString() == dialog.CloseButtonText) return button.Focus(FocusState.Programmatic);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); ++i)
            if (FocusCancel(VisualTreeHelper.GetChild(current, i), dialog)) return true;
        return false;
    }
    private async void ShowDelete()
    {
        var owner = view;
        var focus = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;
        if (deletionDialog is not null || owner?.BeginDelete() is not { } presentation) return;
        int lifetime = pageEpoch; restoreInspectFocus = false;
        var content = new Grid { RowSpacing = 8, MaxHeight = 460 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            content.RowDefinitions.Add(new() { Height = height });
        var warning = new TextBlock { Text = NetworkBrowserView.DeletionWarning, FontWeight = Microsoft.UI.Text.FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        var target = new TextBlock { Text = $"Captured network ID: “{NetworkOperationDisplay.Preview(presentation.Target.Id)}” ({presentation.Target.Id.Length} characters)\nConnection generation: {presentation.Target.ConnectionGeneration}\n\n{NetworkBrowserView.PrecheckExplanation}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var targetScroll = new ScrollViewer { MaxHeight = 135, Content = target, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(status, "NetworkPreparationStatus");
        var attachments = new ListView { Height = 175, SelectionMode = ListViewSelectionMode.Single, ItemTemplate = OperationAttachmentList.ItemTemplate, ItemsPanel = OperationAttachmentList.ItemsPanel };
        ScrollViewer.SetHorizontalScrollBarVisibility(attachments, ScrollBarVisibility.Disabled);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(attachments, "Inspected attached containers and endpoints");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(attachments, "NetworkPreparationAttachments");
        var copy = new Button { Content = "Copy selected attachment identities and metadata in full" };
        copy.Click += async (_, _) =>
        {
            if (lifetime == pageEpoch && ReferenceEquals(view, owner) && attachments.SelectedItem is NetworkAttachmentRow row)
                await owner.CopyAttachmentAsync(row, false, CopyText);
        };
        var originals = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var reveal = new Button { Visibility = Visibility.Collapsed, Content = "Show original precheck failure (may be sensitive)" };
        reveal.Click += (_, _) => { if (lifetime == pageEpoch && ReferenceEquals(view, owner)) owner.ShowPreparationDetails(!owner.ShowingPreparationDetails); };
        var precheckTools = new StackPanel { Spacing = 4 };
        var copyTarget = new Button { Content = "Copy captured network ID in full" };
        copyTarget.Click += (_, _) =>
        {
            if (lifetime == pageEpoch && ReferenceEquals(view, owner)) owner.CopyDeletionTarget(presentation, CopyText);
        };
        precheckTools.Children.Add(copyTarget); precheckTools.Children.Add(copy); precheckTools.Children.Add(reveal);
        precheckTools.Children.Add(new ScrollViewer { MaxHeight = 70, Content = originals, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        content.Children.Add(warning); content.Children.Add(targetScroll); content.Children.Add(status); content.Children.Add(attachments); content.Children.Add(precheckTools);
        Grid.SetRow(targetScroll, 1); Grid.SetRow(status, 2); Grid.SetRow(attachments, 3); Grid.SetRow(precheckTools, 4);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Delete network · fresh attachment precheck", Content = content,
            PrimaryButtonText = "Delete network", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false, MaxWidth = 760
        };
        deletionDialog = dialog; dialogPresentation = presentation; attachmentList = attachments; preparationStatus = status; preparationOriginals = originals; preparationTarget = target; preparationReveal = reveal;
        dialog.Opened += (_, _) => FocusCancel(dialog, dialog);
        // Fence at the beginning of close/cancel, before an async dialog animation
        // or a queued primary-button callback can complete.
        dialog.CloseButtonClick += (_, _) => owner.CancelDelete(presentation);
        dialog.Closing += (_, _) => owner.CancelDelete(presentation);
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var confirmation = dialogConfirmation;
            if (lifetime != pageEpoch || !ReferenceEquals(view, owner) || confirmation is null || !ReferenceEquals(confirmation, owner.DeleteConfirmation))
            { args.Cancel = true; RenderDeletion(); return; }
            owner.ConfirmDelete(presentation, confirmation);
        };
        RenderDeletion();
        try { await dialog.ShowAsync(); }
        catch (Exception) { /* Never log private dialog/daemon content. */ }
        finally
        {
            owner.CancelDelete(presentation);
            attachments.ItemsSource = null; target.Text = status.Text = originals.Text = "";
            if (ReferenceEquals(deletionDialog, dialog))
            { deletionDialog = null; dialogPresentation = null; dialogConfirmation = null; attachmentList = null; preparationStatus = preparationOriginals = preparationTarget = null; preparationReveal = null; }
            if (lifetime == pageEpoch && ReferenceEquals(view, owner))
            {
                Render();
                if (focus is { IsEnabled: true, Visibility: Visibility.Visible } && IsWithin(focus, this) && focus.Focus(FocusState.Programmatic)) { }
                else if (owner.DetailOpen) CloseDetailButton.Focus(FocusState.Programmatic);
                else { restoreNetworkFocus = true; QueueNetworkFocus(); }
            }
        }
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
        if (!rendering && NetworkList.SelectedItem is NetworkRow row) view?.Select(row.Id);
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
        string message = view.CopyIdentity(CopyText) ? "Full network ID copied." : "Copy unavailable. Check the current selection and clipboard.";
        if (view.DetailOpen) DetailStatus.Text = message; else ListStatus.Text = message;
    }
}
