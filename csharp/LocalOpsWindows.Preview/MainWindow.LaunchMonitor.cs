using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LocalOpsWindows.Preview;

public partial class MainWindow
{
    private Grid? _launchGrid;
    private Border? _launchMonitor;
    private ScrollViewer? _launchScroll;
    private bool _launchCompact, _memoryComparison;
    private static readonly string[] ComparisonColors = ["Accent", "Blue", "Warn", "Danger"];

    private void SelectProject(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: DemoProject p } surface) return;
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != surface; source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
            if (source is ButtonBase) return;
        SelectLaunchProject(p, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        surface.Focus(); e.Handled = true;
    }
    private void ProjectKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter) || sender != e.OriginalSource || sender is not Border { DataContext: DemoProject p }) return;
        SelectLaunchProject(p, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); e.Handled = true;
    }
    internal void SelectLaunchProject(DemoProject project, bool additive)
    {
        var selected = !project.Selected || !additive && _projects.Count(p => p.Selected) > 1;
        if (!additive) foreach (var p in _projects.Where(p => p != project)) p.Selected = false;
        project.Selected = selected; RenderLaunchMonitor();
    }
    private void ProjectSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: DemoProject p, IsChecked: bool selected }) p.Selected = selected;
        RenderLaunchMonitor();
    }
    private void UpdateLaunchLayout()
    {
        if (_launchGrid is null || _launchMonitor is null) return;
        var compact = Root.ActualWidth < 1180 || Root.ActualHeight < 680;
        _launchGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 18);
        _launchGrid.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 278);
        Grid.SetRow(_launchMonitor, compact ? 0 : 1); Grid.SetColumn(_launchMonitor, compact ? 0 : 2);
        _launchMonitor.BorderThickness = compact ? new Thickness(0, 0, 0, 1) : new Thickness(1, 0, 0, 0);
        _launchMonitor.Padding = compact ? new Thickness(0, 0, 0, 10) : new Thickness(18, 0, 0, 0);
        _launchMonitor.Margin = compact ? new Thickness(0, 0, 0, 10) : new Thickness(0);
        if (_launchCompact != compact || _launchMonitor.Child is null) { _launchCompact = compact; RenderLaunchMonitor(); }
    }
    private void RenderLaunchMonitor()
    {
        if (_view != "launch" || _launchMonitor is null) return;
        var selected = _projects.Where(p => p.Selected).ToList();
        var scope = selected.Count == 0 ? _projects : selected;
        var running = scope.Where(p => p.Status == "运行中").ToList();
        var panel = new StackPanel { Name = "LaunchMonitorBody" };
        var header = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(header, Text(selected.Count switch { 0 => "项目总览", 1 => "服务详情", _ => $"已选 {selected.Count} 项" }, 12), 0);
        var clear = ActionButton("x", "清除监控选择", () => { foreach (var p in _projects) p.Selected = false; RenderLaunchMonitor(); }, true);
        clear.Height = 24; clear.Width = 24; clear.Margin = new Thickness(0); clear.Visibility = selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed; Put(header, clear, 1);
        var caption = selected.Count == 1 ? selected[0].Name : $"{running.Count} 运行中 · {scope.Count} 个已登记项目";
        var name = Text(caption, _launchCompact ? 11 : 14, selected.Count == 1 ? null : "Muted", new Thickness(0, 6, 0, 8));
        name.Name = "LaunchMonitorTitle"; name.ToolTip = caption; name.FontWeight = selected.Count == 1 ? FontWeights.SemiBold : FontWeights.Normal;
        var identity = new StackPanel(); identity.Children.Add(header); identity.Children.Add(name);
        if (_launchCompact && selected.Count == 1)
        {
            var info = ActionButton("file-text", "完整信息", () => Info(selected[0]), true); info.Width = 24; info.Height = 24; info.Margin = new Thickness(0);
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; header.Children.Remove(clear); actions.Children.Add(info); actions.Children.Add(clear); Put(header, actions, 1);
        }
        var metrics = Columns(new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star));
        Put(metrics, Gauge("CPU 合计", running.Sum(p => p.Cpu), "Accent"), 0);
        Put(metrics, Gauge("内存合计", running.Sum(p => p.Memory), "Blue"), 1);
        var trend = new StackPanel();
        var trendHeader = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto);
        Put(trendHeader, Text(selected.Count > 1 ? "项目对比" : "资源趋势", 10, "Muted"), 0);
        if (selected.Count > 1)
        {
            var modes = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var useMemory in new[] { false, true })
            {
                var label = useMemory ? "对比内存" : "对比 CPU";
                var mode = new RadioButton { Content = useMemory ? "内存" : "CPU", Style = (Style)FindResource("Segment"), GroupName = "ComparisonMetric", IsChecked = _memoryComparison == useMemory, ToolTip = label };
                AutomationProperties.SetName(mode, label);
                mode.Click += (_, _) => { _memoryComparison = useMemory; RenderLaunchMonitor(); }; modes.Children.Add(mode);
            }
            Put(trendHeader, modes, 1);
        }
        else Put(trendHeader, Text("最近 5 分钟", 9, "Muted"), 1);
        trend.Children.Add(trendHeader);
        var series = selected.Count > 1 ? ComparisonSeries(selected, _memoryComparison) : new List<TrendSeries> { new("CPU", "Accent", PreviewHistory.Sum(scope, false)), new("内存", "Blue", PreviewHistory.Sum(scope, true)) };
        var chart = new TrendChart { Name = "LaunchTrend", Series = series, Height = _launchCompact ? 38 : 64, Margin = new Thickness(0, 5, 0, 3) };
        AutomationProperties.SetName(chart, selected.Count > 1 ? "所选项目性能对比" : "项目资源趋势");
        trend.Children.Add(chart);
        var legend = new WrapPanel();
        foreach (var item in series)
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 10, 0) };
            entry.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center }.Resource(Border.BackgroundProperty, item.Color + "Brush"));
            var label = Text(item.Name, 10, "Muted", new Thickness(4, 0, 0, 0)); label.MaxWidth = _launchCompact ? 94 : 100; label.ToolTip = item.Name; entry.Children.Add(label); legend.Children.Add(entry);
        }
        trend.Children.Add(legend);
        if (_launchCompact)
        {
            var compact = Columns(new GridLength(150), new GridLength(164), new GridLength(1, GridUnitType.Star));
            Put(compact, identity, 0); Put(compact, metrics, 1); trend.Margin = new Thickness(16, 0, 0, 0); Put(compact, trend, 2);
            panel.Children.Add(compact);
        }
        else
        {
            panel.Children.Add(identity); panel.Children.Add(metrics); trend.Margin = new Thickness(0, 14, 0, 10); panel.Children.Add(trend);
            if (selected.Count == 1)
            {
                var p = selected[0];
                var status = Text($"{p.PortLabel} · {p.Status} · {p.Uptime}", 10, "Muted", new Thickness(0, 2, 0, 6)); status.ToolTip = status.Text; panel.Children.Add(status);
                var notes = Text(p.Notes, 12); notes.TextWrapping = TextWrapping.Wrap; notes.MaxHeight = 36; notes.ToolTip = p.Notes; panel.Children.Add(notes);
                foreach (var value in new[] { p.Command, p.Directory }) { var block = Text(value, 10, "Muted", new Thickness(0, 6, 0, 0)); block.FontFamily = new FontFamily("Cascadia Code, Consolas"); block.ToolTip = value; panel.Children.Add(block); }
                var info = ActionButton("file-text", "完整信息", () => Info(p), style: "Flat"); info.HorizontalAlignment = HorizontalAlignment.Left; info.Margin = new Thickness(0, 10, 0, 0); panel.Children.Add(info);
            }
            else
            {
                foreach (var p in scope.OrderByDescending(p => p.Status == "运行中" ? _memoryComparison ? p.Memory : p.Cpu : 0).Take(3))
                {
                    var row = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto); row.Margin = new Thickness(0, 8, 0, 0);
                    var label = Text(p.Name, 11); label.Margin = new Thickness(0, 0, 10, 0); label.ToolTip = p.Name; Put(row, label, 0); Put(row, Text(_memoryComparison ? p.MemoryLabel : p.CpuLabel, 10, "Muted"), 1); panel.Children.Add(row);
                }
                if (scope.Count > 3) panel.Children.Add(Text($"其余 {scope.Count - 3} 项已计入合计", 10, "Muted", new Thickness(0, 10, 0, 0)));
            }
        }
        _launchMonitor.Child = panel;
    }
    private FrameworkElement Gauge(string label, double value, string color)
    {
        var size = _launchCompact ? 44 : 68;
        var ring = new Grid { Width = size, Height = size };
        var gauge = new DonutGauge { Value = value, ColorKey = color, ToolTip = $"{label} {value:0.0}%" }; AutomationProperties.SetName(gauge, $"{label} {value:0.0}%"); ring.Children.Add(gauge);
        ring.Children.Add(new TextBlock { Text = $"{value:0.0}%", FontSize = _launchCompact ? 10 : 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(Text(label, 10, "Muted", new Thickness(0, 0, 0, 5))); stack.Children.Add(ring); return stack;
    }
    private static List<TrendSeries> ComparisonSeries(List<DemoProject> projects, bool memory)
    {
        var result = projects.Take(projects.Count > 4 ? 3 : 4).Select((p, i) => new TrendSeries(p.Name, ComparisonColors[i], PreviewHistory.Values(p, memory))).ToList();
        if (projects.Count > 4) result.Add(new($"其余 {projects.Count - 3} 项", ComparisonColors[3], PreviewHistory.Sum(projects.Skip(3), memory)));
        return result;
    }
}

internal sealed record TrendSeries(string Name, string Color, IReadOnlyList<double> Values);

internal static class PreviewHistory
{
    // shortcut: fictional history; use sampled telemetry when integrating the real client.
    internal static double[] Values(DemoProject project, bool memory)
    {
        var value = project.Status == "运行中" ? memory ? project.Memory : project.Cpu : 0;
        var phase = project.Id.Sum(c => (int)c) % 19;
        return Enumerable.Range(0, 60).Select(i => i == 59 ? value : Math.Max(0, value * (1 + .14 * Math.Sin(i * .61 + phase) + .06 * Math.Sin(i * 1.7 + phase)))).ToArray();
    }
    internal static double[] Sum(IEnumerable<DemoProject> projects, bool memory)
    {
        var result = new double[60];
        foreach (var p in projects) { var values = Values(p, memory); for (var i = 0; i < result.Length; i++) result[i] += values[i]; }
        return result;
    }
}

internal sealed class DonutGauge : FrameworkElement
{
    internal double Value { get; init; }
    internal string ColorKey { get; init; } = "Accent";
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 4); var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var track = new Pen((Brush)FindResource("LineBrush"), 4); var ink = new Pen((Brush)FindResource(ColorKey + "Brush"), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        context.DrawEllipse(null, track, center, radius, radius);
        var ratio = double.IsFinite(Value) ? Math.Clamp(Value / 100, 0, 1) : 0;
        if (ratio <= 0) return;
        if (ratio >= 1) { context.DrawEllipse(null, ink, center, radius, radius); return; }
        var angle = ratio * Math.PI * 2 - Math.PI / 2; var shape = new StreamGeometry();
        using (var drawing = shape.Open()) { drawing.BeginFigure(new Point(center.X, center.Y - radius), false, false); drawing.ArcTo(new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)), new Size(radius, radius), 0, ratio > .5, SweepDirection.Clockwise, true, false); }
        context.DrawGeometry(null, ink, shape);
    }
}
