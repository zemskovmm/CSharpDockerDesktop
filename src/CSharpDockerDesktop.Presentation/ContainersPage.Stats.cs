using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace CSharpDockerDesktop.Presentation;

public sealed partial class ContainersPage
{
    private IReadOnlyList<StatsChart>? displayedCharts;
    private static readonly IReadOnlyList<StatsChart> emptyCharts = StatsDisplay.Charts(Array.Empty<CSharpDockerDesktop.Application.ContainerStatsHistoryEntry>());
    private sealed record ChartElements(TextBlock Range, TextBlock Time, Path Plot);
    private readonly List<ChartElements> chartElements = [];
    private bool statsFocusQueued;
    private void OnStatsTab(object sender, RoutedEventArgs args)
    {
        view?.ShowStats();
        if (statsFocusQueued) return;
        statsFocusQueued = true; int lifetime = pageEpoch;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            statsFocusQueued = false;
            if (lifetime != pageEpoch || view?.ShowingStats != true) return;
            StatsPanel.UpdateLayout();
            StatsReopen.Focus(FocusState.Programmatic);
        })) statsFocusQueued = false;
    }
    private void OnStatsReopen(object sender, RoutedEventArgs args) => view?.ShowStats();
    private void OnStatsDaemon(object sender, RoutedEventArgs args)
    { if (view is not null) view.Stats.ShowDaemonContent(!view.Stats.ShowingDaemonContent); }
    private void ClearStats()
    {
        StatsCharts.Children.Clear(); chartElements.Clear(); displayedCharts = null;
        StatsValues.Text = StatsStatus.Text = StatsRetention.Text = StatsDaemonText.Text = "";
        StatsDaemonText.Visibility = StatsDaemonButton.Visibility = Visibility.Collapsed;
    }
    private void RenderStats()
    {
        var stats = view!.Stats;
        stats.ValidateForRender();
        if (!view.ShowingStats) { ClearStats(); return; }
        StatsStatus.Text = stats.Message;
        StatsValues.Text = stats.Values;
        StatsRetention.Text = stats.RetentionMessage;
        StatsDaemonButton.Visibility = stats.HasDaemonContent ? Visibility.Visible : Visibility.Collapsed;
        StatsDaemonButton.Content = stats.ShowingDaemonContent ? "Hide original daemon content" : "Show original daemon content (may be sensitive)";
        StatsDaemonText.Visibility = stats.ShowingDaemonContent ? Visibility.Visible : Visibility.Collapsed;
        StatsDaemonText.Text = stats.DaemonContent;
        if (ReferenceEquals(displayedCharts, stats.Charts)) return;
        displayedCharts = stats.Charts;
        if (chartElements.Count == 0)
            foreach (var chart in emptyCharts)
            {
                var panel = new StackPanel { Spacing = 3 };
                panel.Children.Add(new TextBlock { Text = chart.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                var range = new TextBlock { TextWrapping = TextWrapping.Wrap };
                panel.Children.Add(range);
                var canvas = new Canvas { Width = 640, Height = 100, Background = new SolidColorBrush(Colors.Transparent) };
                var path = new Path { Stroke = (Brush)Resources["StatsChartBrush"], StrokeThickness = 2 };
                AutomationProperties.SetName(canvas, chart.Title + " historical chart. Values, scale and time labels are provided as text.");
                canvas.Children.Add(path);
                panel.Children.Add(new Viewbox { Height = 100, Stretch = Stretch.Fill, Child = canvas, HorizontalAlignment = HorizontalAlignment.Stretch });
                var time = new TextBlock { TextWrapping = TextWrapping.Wrap };
                panel.Children.Add(time); StatsCharts.Children.Add(panel); chartElements.Add(new(range, time, path));
            }
        var charts = stats.Charts.Count == 6 ? stats.Charts : emptyCharts;
        for (int i = 0; i < charts.Count; ++i)
        {
            var chart = charts[i]; var elements = chartElements[i];
            elements.Range.Text = chart.RangeLabel;
            // Six paths regardless of viewing duration. Null values and continuity/provenance changes
            // create separate figures, never zero-valued vertices or a line across known gaps.
            var geometry = new PathGeometry(); PathFigure? figure = null;
            var start = chart.Start ?? DateTimeOffset.UnixEpoch; var end = chart.End ?? start;
            double seconds = Math.Max(1, (end - start).TotalSeconds);
            foreach (var point in chart.Points)
            {
                double x = 2 + Math.Clamp((point.Time - start).TotalSeconds / seconds, 0, 1) * 636;
                double y = 96 - point.Value / chart.Maximum * 92;
                var position = new Point(x, y);
                if (point.StartsSegment || figure is null)
                {
                    figure = new PathFigure { StartPoint = new(x - 1, y), IsClosed = false, IsFilled = false };
                    geometry.Figures.Add(figure);
                    // A singleton/zero sample remains a visible mark.
                    figure.Segments.Add(new LineSegment { Point = new(x + 1, y) });
                }
                else figure.Segments.Add(new LineSegment { Point = position });
            }
            // Reuse all chart controls and their height while a replacement read is pending.
            // Clearing obsolete geometry must not collapse the scrolled history viewport.
            elements.Plot.Data = geometry;
            elements.Time.Text = chart.Start is { } first && chart.End is { } last
                ? $"{first.UtcDateTime:HH:mm:ss} UTC → {last.UtcDateTime:HH:mm:ss} UTC" : "No retained observations.";
        }
    }
}
