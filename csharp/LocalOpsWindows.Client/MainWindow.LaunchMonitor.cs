using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace LocalOpsWindows.Client;

public partial class MainWindow
{
    private bool _launchCompact;
    private bool _compareMemory;

    private void CardWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AppsList is null) return;
        foreach (var panel in VisualChildren<ProjectCardPanel>(AppsList)) panel.MinimumCardWidth = e.NewValue;
        if (_loaded && !_demoMode)
        {
            try { ClientStorage.Write("CardWidth", e.NewValue.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            catch (Exception ex) { ShowOperationError(ex, "无法保存卡片宽度"); }
        }
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualChildren<T>(child)) yield return nested;
        }
    }

    internal static string UserLog(string text) => string.Join("\n", text.Split('\n').Where(line =>
        !Regex.IsMatch(line, @"127\.0\.0\.1\s+-.*""GET /api/(?:health|state|tasks/status|console/log|apps/[^/\s]+/logs)(?:\?[^\s]*)? HTTP/[\d.]+"" 200(?:\s|$)", RegexOptions.CultureInvariant)));

    private TextBlock MonitorText(string text, double size = 12, string brush = "TextBrush")
    {
        var block = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    private void UpdateLaunchMonitor()
    {
        if (LaunchMonitorContent is null || AppsList is null) return;
        var selected = AppsList.SelectedItems.OfType<AppSnapshot>().ToList();
        var scope = selected.Count > 0 ? selected : _state.Apps;
        var services = LaunchTelemetry.Services(_state, scope);
        var body = new StackPanel();
        var identity = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var heading = MonitorText(selected.Count switch { 0 => "项目总览", 1 => "服务详情", _ => $"已选 {selected.Count} 项" });
        heading.FontWeight = FontWeights.SemiBold;
        identity.Children.Add(heading);
        var caption = MonitorText(selected.Count == 1 ? selected[0].Name : $"{scope.Count(a => a.Running)} 运行中 · {scope.Count} 个项目", 12, "MutedTextBrush");
        caption.Margin = new Thickness(0, 7, 0, 0);
        identity.Children.Add(caption);
        var metrics = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        metrics.Children.Add(MonitorGauge("CPU 合计", services.Sum(s => s.Cpu), "AccentBrush", services.Count > 0));
        metrics.Children.Add(MonitorGauge("内存合计", services.Sum(s => s.Mem), "InfoBrush", services.Count > 0));
        var chart = new StackPanel();
        chart.Children.Add(MonitorText("资源趋势", 11, "MutedTextBrush"));
        var series = new List<LaunchSeries>();
        if (selected.Count > 1)
        {
            var modes = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var memory in new[] { false, true })
            {
                var mode = new RadioButton { Content = memory ? "内存" : "CPU", GroupName = "LaunchMetric", IsChecked = memory == _compareMemory, Margin = new Thickness(0, 4, 12, 2) };
                AutomationProperties.SetName(mode, memory ? "对比内存" : "对比 CPU");
                mode.Click += (_, _) => { _compareMemory = memory; UpdateLaunchMonitor(); };
                modes.Children.Add(mode);
            }
            chart.Children.Add(modes);
            var colors = new[] { "AccentBrush", "InfoBrush", "WarningBrush", "DangerBrush" };
            var count = selected.Count > 4 ? 3 : selected.Count;
            for (var i = 0; i < count; i++)
                series.Add(new(selected[i].Name, colors[i], LaunchTelemetry.History(_monitor, LaunchTelemetry.Services(_state, [selected[i]]), _compareMemory)));
            if (selected.Count > 4) series.Add(new($"其余 {selected.Count - 3} 项", colors[3], LaunchTelemetry.History(_monitor, LaunchTelemetry.Services(_state, selected.Skip(3)), _compareMemory)));
        }
        else
        {
            series.Add(new("CPU", "AccentBrush", LaunchTelemetry.History(_monitor, services, false)));
            series.Add(new("内存", "InfoBrush", LaunchTelemetry.History(_monitor, services, true)));
        }
        chart.Children.Add(new LaunchTrend { Series = series, Height = _launchCompact ? 38 : 72, Margin = new Thickness(0, 6, 0, 4) });
        var legend = new WrapPanel();
        foreach (var item in series)
        {
            var label = MonitorText(item.Name, 10, item.Brush);
            label.MaxWidth = 110; label.Margin = new Thickness(0, 0, 12, 4); legend.Children.Add(label);
        }
        chart.Children.Add(legend);
        if (_launchCompact)
        {
            var strip = new Grid();
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            strip.ColumnDefinitions.Add(new ColumnDefinition());
            strip.Children.Add(identity); Grid.SetColumn(metrics, 1); strip.Children.Add(metrics);
            Grid.SetColumn(chart, 2); chart.Margin = new Thickness(16, 0, 0, 0); strip.Children.Add(chart); body.Children.Add(strip);
        }
        else
        {
            body.Children.Add(identity); metrics.Margin = new Thickness(0, 18, 0, 16); body.Children.Add(metrics); body.Children.Add(chart);
            if (selected.Count == 1)
            {
                var project = selected[0];
                foreach (var value in new[] { $"{project.StatusLabel} · {project.PortLabel}", $"{project.PidLabel} · {project.UptimeLabel}", project.Notes, project.Command, project.Cwd ?? "" })
                {
                    var line = MonitorText(value, 12, "MutedTextBrush"); line.TextWrapping = TextWrapping.Wrap; line.MaxHeight = 40; line.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(line);
                }
            }
        }
        if (services.Count == 0) body.Children.Add(MonitorText("暂无监听进程采样", 11, "MutedTextBrush"));
        LaunchMonitorContent.Content = body;
    }

    private FrameworkElement MonitorGauge(string label, double value, string brush, bool available)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(MonitorText(label, 11, "MutedTextBrush"));
        var ring = new Grid { Width = _launchCompact ? 48 : 72, Height = _launchCompact ? 48 : 72, Margin = new Thickness(0, 5, 0, 0) };
        ring.Children.Add(new LaunchDonut { Value = available ? value : 0, BrushKey = brush });
        var number = MonitorText(available ? $"{value:0.0}%" : "--", _launchCompact ? 10 : 13);
        number.HorizontalAlignment = HorizontalAlignment.Center; number.VerticalAlignment = VerticalAlignment.Center; ring.Children.Add(number);
        AutomationProperties.SetName(ring, $"{label} {number.Text}"); stack.Children.Add(ring); return stack;
    }
}

internal static class LaunchTelemetry
{
    internal static List<ServiceSnapshot> Services(DashboardState state, IEnumerable<AppSnapshot> scope)
    {
        var apps = scope.Where(a => a.Running).ToList();
        var ids = apps.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var pids = apps.Where(a => a.Pid > 0).Select(a => a.Pid!.Value).ToHashSet();
        return state.Services.Where(s => s.Pid > 0 && (s.AppId is not null && ids.Contains(s.AppId) || pids.Contains(s.Pid)))
            .GroupBy(s => s.Pid).Select(g => g.First()).ToList();
    }

    internal static double[] History(MonitorSession monitor, IEnumerable<ServiceSnapshot> services, bool memory)
    {
        var histories = services.Select(s => monitor.History.GetValueOrDefault(MonitorSession.Key(s)))
            .Where(q => q is { Count: > 0 }).Select(q => q!.ToArray()).ToList();
        if (histories.Count == 0) return [];
        var length = histories.Min(h => h.Length);
        return Enumerable.Range(0, length).Select(i => histories.Sum(h => memory ? h[h.Length - length + i].Mem : h[h.Length - length + i].Cpu)).ToArray();
    }
}

internal sealed record LaunchSeries(string Name, string Brush, double[] Values);

internal sealed class LaunchTrend : FrameworkElement
{
    internal IReadOnlyList<LaunchSeries> Series { get; init; } = [];
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var maximum = Math.Max(1, Series.SelectMany(s => s.Values).Where(double.IsFinite).DefaultIfEmpty(1).Max() * 1.15);
        for (var i = 0; i < 3; i++) context.DrawLine(new Pen((Brush)FindResource("SoftBorderBrush"), 1), new Point(0, 4 + i * (ActualHeight - 8) / 2), new Point(ActualWidth, 4 + i * (ActualHeight - 8) / 2));
        ToolTip = $"0 - {maximum:0.#}%";
        foreach (var series in Series.Where(s => s.Values.Length > 1))
        {
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
                for (var i = 0; i < series.Values.Length; i++)
                {
                    var value = double.IsFinite(series.Values[i]) ? Math.Max(0, series.Values[i]) : 0;
                    var point = new Point(i * ActualWidth / (series.Values.Length - 1), ActualHeight - 4 - value / maximum * (ActualHeight - 8));
                    if (i == 0) drawing.BeginFigure(point, false, false); else drawing.LineTo(point, true, false);
                }
            context.DrawGeometry(null, new Pen((Brush)FindResource(series.Brush), 1.6), geometry);
        }
    }
}

internal sealed class LaunchDonut : FrameworkElement
{
    internal double Value { get; init; }
    internal string BrushKey { get; init; } = "AccentBrush";
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 4);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        context.DrawEllipse(null, new Pen((Brush)FindResource("TrackBrush"), 4), center, radius, radius);
        var ratio = double.IsFinite(Value) ? Math.Clamp(Value / 100, 0, 1) : 0;
        var pen = new Pen((Brush)FindResource(BrushKey), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (ratio >= 1) context.DrawEllipse(null, pen, center, radius, radius);
        else if (ratio > 0)
        {
            var angle = ratio * 2 * Math.PI - Math.PI / 2; var geometry = new StreamGeometry();
            using (var drawing = geometry.Open()) { drawing.BeginFigure(new Point(center.X, center.Y - radius), false, false); drawing.ArcTo(new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)), new Size(radius, radius), 0, ratio > .5, SweepDirection.Clockwise, true, false); }
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
