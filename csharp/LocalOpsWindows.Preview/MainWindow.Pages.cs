using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace LocalOpsWindows.Preview;

public partial class MainWindow
{
    private Grid? _monitorGrid, _settingsGrid;
    private FrameworkElement? _settingsRight, _trendBlock;
    private readonly StackPanel _detail = new();
    private ListBox? _servicesList;
    private string _monitorTab = "服务列表";
    private DemoProject? _selected;
    private CheckBox? _techCheck;
    private StackPanel? _logLines;
    private TextBlock? _logCount;
    private ScrollViewer? _logScroll;
    private IEnumerable<DemoProject> Services() => _projects.Where(p => p.Kind == "service" && p.Status == "运行中").Concat(_external);

    private FrameworkElement BuildMonitor()
    {
        var root = new DockPanel();
        var top = new StackPanel(); top.Children.Add(Toolbar()); top.Children.Add(Summary());
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var label in new[] { "服务列表", "新发现", "资源排行", "动态记录" })
        {
            var tab = new RadioButton { Style = (Style)FindResource("Tab"), Content = label == "新发现" ? $"新发现  {_external.Count}" : label, GroupName = "Services", IsChecked = _monitorTab == label };
            tab.Click += (_, _) => { _monitorTab = label; PageContent.Content = BuildMonitor(); UpdateResponsiveLayout(); }; tabs.Children.Add(tab);
        }
        var tabBar = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto); Put(tabBar, tabs, 0);
        if (Services().Any(p => p.Hidden)) Put(tabBar, ActionButton("eye", "恢复隐藏服务", () => { foreach (var p in Services()) p.Hidden = false; PageContent.Content = BuildMonitor(); UpdateResponsiveLayout(); }, true), 1);
        top.Children.Add(new Border { Child = tabBar, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 15) }.Resource(Border.BorderBrushProperty, "LineBrush"));
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        _monitorGrid = Columns(new GridLength(1, GridUnitType.Star), new GridLength(22), new GridLength(300));
        var listArea = new DockPanel();
        var header = Columns(new GridLength(1, GridUnitType.Star), new GridLength(88), new GridLength(66), new GridLength(68), new GridLength(32));
        header.Height = 30; header.Margin = new Thickness(12, 0, 12, 0);
        foreach (var (label, column) in new[] { ("服务 / 启动命令", 0), ("端口 / 进程", 1), ("CPU", 2), ("内存", 3) }) Put(header, Text(label, 10, "Muted"), column);
        DockPanel.SetDock(header, Dock.Top); listArea.Children.Add(header);
        _servicesList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), ItemTemplate = (DataTemplate)FindResource("ServiceRow"), ItemContainerStyle = (Style)FindResource("ServiceItem") };
        ScrollViewer.SetHorizontalScrollBarVisibility(_servicesList, ScrollBarVisibility.Disabled);
        _servicesList.SelectionChanged += (_, _) => { if (_servicesList.SelectedItem is DemoProject p) { _selected = p; RenderDetail(); } };
        listArea.Children.Add(_servicesList); Put(_monitorGrid, listArea, 0);
        if (_detail.Parent is Border previous) previous.Child = null;
        _detail.Margin = new Thickness(22, 3, 0, 0);
        var detailFrame = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = _detail }.Resource(Border.BorderBrushProperty, "LineBrush");
        Put(_monitorGrid, detailFrame, 2);
        if (_monitorTab == "动态记录")
        {
            header.Visibility = Visibility.Collapsed; listArea.Children.Remove(_servicesList);
            var events = new StackPanel(); foreach (var entry in _logs.Where(e => !e.Technical)) events.Children.Add(LogLine(entry));
            listArea.Children.Add(new ScrollViewer { Content = events, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        else RefreshServices();
        RenderDetail(); root.Children.Add(_monitorGrid); FooterText.Text = $"{Services().Count()} 个监听服务 · 外部进程仅供观察"; return root;
    }
    private void RefreshServices()
    {
        if (_servicesList is null) return;
        var items = Services().Where(p => !p.Hidden && Matches(p) && (_monitorTab != "新发现" || p.External));
        items = _monitorTab == "资源排行" ? items.OrderByDescending(p => p.Cpu) : items.OrderByDescending(p => p.Pinned);
        var values = items.ToList(); _servicesList.ItemsSource = values;
        _selected = values.FirstOrDefault(p => p.Id == _selected?.Id) ?? values.FirstOrDefault(); _servicesList.SelectedItem = _selected; RenderDetail();
    }
    private void RenderDetail()
    {
        _detail.Children.Clear(); _trendBlock = null;
        var p = _selected; if (p is null) { _detail.Children.Add(Text("选择一个服务", 11, "Muted")); return; }
        var heading = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto); Put(heading, Text("服务详情", 11, "Muted"), 0); Put(heading, Text(p.External ? "外部进程" : "已登记项目", 9, "Muted"), 1); _detail.Children.Add(heading);
        _detail.Children.Add(new TextBlock { Text = p.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 6), ToolTip = p.Name });
        _detail.Children.Add(Text($"{p.PortLabel}     ·     {p.PidLabel}     ·     运行中", 10, "Muted"));
        var metrics = Columns(new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star)); metrics.Margin = new Thickness(0, 22, 0, 18);
        for (var i = 0; i < 2; i++) { var panel = new StackPanel(); panel.Children.Add(Text(i == 0 ? "CPU" : "内存", 10, "Muted")); panel.Children.Add(Text(i == 0 ? p.CpuLabel : p.MemoryLabel, 24, i == 0 ? "Accent" : "Blue", new Thickness(0, 4, 0, 0))); Put(metrics, panel, i); }
        _detail.Children.Add(metrics);
        var trend = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var captions = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto); Put(captions, Text("资源趋势", 9, "Muted"), 0); Put(captions, Text("最近 5 分钟", 9, "Muted"), 1); trend.Children.Add(captions);
        trend.Children.Add(new TrendChart { Height = 62, Margin = new Thickness(0, 8, 0, 4), Series = [new("CPU", "Accent", PreviewHistory.Values(p, false)), new("内存", "Blue", PreviewHistory.Values(p, true))] });
        var legend = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto); Put(legend, Text("5 分钟前", 9, "Muted"), 0); Put(legend, Text("现在", 9, "Muted"), 1); trend.Children.Add(legend); _trendBlock = trend; _detail.Children.Add(trend);
        var properties = new Grid(); properties.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(61) }); properties.ColumnDefinitions.Add(new ColumnDefinition());
        var n = 0; foreach (var (label, value) in new[] { ("已运行", p.Uptime), ("命令", p.Command), ("目录", p.Directory) })
        { properties.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) }); Put(properties, Text(label, 11, "Muted"), 0, n); var text = Text(value, 10); text.ToolTip = value; if (n > 0) text.FontFamily = new FontFamily("Cascadia Code, Consolas"); Put(properties, text, 1, n++); }
        _detail.Children.Add(properties);
        var info = ActionButton("file-text", "完整信息", () => Info(p), style: "Flat"); info.HorizontalAlignment = HorizontalAlignment.Left; info.Margin = new Thickness(0, 10, 0, 13); _detail.Children.Add(info);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(ActionButton(p.Pinned ? "pin-off" : "pin", p.Pinned ? "取消置顶" : "置顶服务", () => { p.Pinned = !p.Pinned; RefreshServices(); }, true));
        actions.Children.Add(ActionButton("eye-off", "隐藏服务", () => { p.Hidden = true; PageContent.Content = BuildMonitor(); UpdateResponsiveLayout(); }, true));
        actions.Children.Add(ActionButton("copy", "复制链接", () => Copy($"http://127.0.0.1:{p.Port}/"), true));
        actions.Children.Add(ActionButton(p.External ? "plus" : "rocket", p.External ? "加入启动台" : "在启动台查看", () => { if (p.External) { _external.Remove(p); p.External = false; _projects.Add(p); } ShowPage("launch"); var search = FindChild<TextBox>(PageContent, "ProjectSearch"); if (search is not null) search.Text = p.Name; }));
        _detail.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = actions }.Resource(Border.BorderBrushProperty, "LineBrush"));
    }
    private FrameworkElement BuildLogs()
    {
        var root = new DockPanel(); var top = new StackPanel(); var toolbar = Columns(GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star));
        var names = new[] { "控制台日志" }.Concat(_projects.Select(p => p.Name)).ToArray(); var selected = _projects.FirstOrDefault(p => p.Id == _logTarget)?.Name ?? names[0];
        Put(toolbar, Choice(names, selected, value => { _logTarget = _projects.FirstOrDefault(p => p.Name == value)?.Id ?? "console"; _techCheck!.IsEnabled = _logTarget == "console"; RenderLogs(); }, "日志来源", 190), 0);
        Put(toolbar, ActionButton("refresh-cw", "刷新日志", RenderLogs, true), 1); Put(toolbar, ActionButton("copy", "复制", () => Copy(LogText())), 2);
        Put(toolbar, ActionButton("download", "导出", () => { var dialog = new SaveFileDialog { FileName = "local-ops-example.log", Filter = "Log|*.log" }; if (dialog.ShowDialog(this) == true) TrySave(dialog.FileName, LogText()); }), 3);
        var options = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _techCheck = new CheckBox { Content = "显示技术日志", IsChecked = _technical, IsEnabled = _logTarget == "console", Margin = new Thickness(12, 0, 15, 0) };
        _techCheck.Checked += (_, _) => { _technical = true; SavePreferences(); RenderLogs(); }; _techCheck.Unchecked += (_, _) => { _technical = false; SavePreferences(); RenderLogs(); }; options.Children.Add(_techCheck);
        var follow = new CheckBox { Content = "自动跟随", IsChecked = _follow }; follow.Checked += (_, _) => { _follow = true; SavePreferences(); RenderLogs(); }; follow.Unchecked += (_, _) => { _follow = false; SavePreferences(); }; options.Children.Add(follow); Put(toolbar, options, 4); top.Children.Add(toolbar);
        _logCount = Text("", 10, "Muted", new Thickness(0, 12, 0, 11)); top.Children.Add(_logCount); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        _logLines = new StackPanel(); _logScroll = new ScrollViewer { Content = _logLines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Child = _logScroll }.Resource(Border.BackgroundProperty, "SurfaceBrush").Resource(Border.BorderBrushProperty, "LineBrush"));
        RenderLogs(); FooterText.Text = selected; return root;
    }
    private IEnumerable<DemoLog> VisibleLogs() => _logs.Where(e => (_logTarget == "console" || e.Target == _logTarget) && (_logTarget != "console" || _technical || !e.Technical));
    private string LogText() => string.Join(Environment.NewLine, VisibleLogs().Select(e => $"{e.Time} {e.Level} {e.Message}"));
    private void RenderLogs()
    {
        if (_logLines is null || _logCount is null) return; _logLines.Children.Clear(); var lines = VisibleLogs().ToList(); foreach (var entry in lines) _logLines.Children.Add(LogLine(entry));
        _logCount.Text = $"{(_logTarget != "console" ? "项目原始输出" : _technical ? "全部记录" : "运行事件与异常")}                                      {lines.Count} 条记录"; if (_follow) _logScroll?.ScrollToEnd();
    }
    private UIElement LogLine(DemoLog entry)
    {
        var row = Columns(new GridLength(76), new GridLength(50), GridLength.Auto); row.Height = 26;
        Put(row, Text(entry.Time, 11, "Muted"), 0); var color = entry.Technical ? "Muted" : entry.Level == "ERROR" ? "Danger" : entry.Level == "WARN" ? "Warn" : "Accent"; Put(row, Text(entry.Level, 11, color), 1); Put(row, Text(entry.Message, 11, entry.Level == "ERROR" ? "Danger" : entry.Technical ? "Muted" : "Text"), 2);
        foreach (TextBlock block in row.Children) block.FontFamily = new FontFamily("Cascadia Code, Consolas"); return row;
    }
    private FrameworkElement BuildSettings()
    {
        _settingsGrid = Columns(new GridLength(1, GridUnitType.Star), new GridLength(36), new GridLength(1, GridUnitType.Star)); _settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); _settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var left = new StackPanel(); var right = new StackPanel(); _settingsRight = right;
        var themes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, label) in new[] { ("light", "浅色"), ("dark", "深色"), ("system", "系统") }) { var radio = new RadioButton { Content = label, Style = (Style)FindResource("Segment"), GroupName = "Theme", IsChecked = _theme == key }; radio.Click += (_, _) => ApplyTheme(key); themes.Children.Add(radio); }
        var segment = new Border { Child = themes, Padding = new Thickness(3), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) }.Resource(Border.BackgroundProperty, "RailBrush").Resource(Border.BorderBrushProperty, "LineBrush");
        left.Children.Add(Section("外观与布局", Setting("主题", "", segment), Setting("卡片宽度", "", CardSizeControl()), Setting("服务详情", "", Text("自适应", 10, "Muted"))));
        left.Children.Add(Section("启动与窗口", Setting("登录后启动", "在通知区域启动客户端", Switch(_autostart, v => _autostart = v, "登录后启动")), Setting("最小化到托盘", "关闭按钮仍然退出客户端", Switch(_tray, v => _tray = v, "最小化到托盘")), Setting("任务完成通知", "任务结束时发送桌面通知", Switch(_notification, v => _notification = v, "任务完成通知"))));
        left.Children.Add(Section("日志偏好", Setting("显示技术日志", "包含访问请求、健康检查和后台轮询", Switch(_technical, v => { _technical = v; SavePreferences(); }, "显示技术日志")), Setting("自动跟随", "跟随显示最新的日志记录", Switch(_follow, v => { _follow = v; SavePreferences(); }, "自动跟随"))));
        var version = new StackPanel();
        var versionLine = new StackPanel { Orientation = Orientation.Horizontal };
        versionLine.Children.Add(Text("v2.1.1", 21));
        versionLine.Children.Add(new Border { Child = Text("Windows x64", 9, "Muted"), Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(4), Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }.Resource(Border.BackgroundProperty, "RailBrush"));
        version.Children.Add(versionLine);
        var message = Text("上次检查：尚未检查", 10, "Muted", new Thickness(0, 8, 0, 16)); version.Children.Add(message);
        var updateActions = new StackPanel { Orientation = Orientation.Horizontal }; updateActions.Children.Add(ActionButton("refresh-cw", "检查更新", () => message.Text = "当前已是最新版本（演示结果）")); updateActions.Children.Add(ActionButton("github", "版本信息", () => OpenDialog("版本信息", Text("Local Ops Windows v2.1.1 · 界面样稿")), style: "Flat")); version.Children.Add(updateActions); right.Children.Add(Section("版本与更新", version));
        var storage = new Grid(); storage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) }); storage.ColumnDefinitions.Add(new ColumnDefinition());
        var index = 0; foreach (var (label, value) in new[] { ("后台状态", "已连接"), ("监听地址", "127.0.0.1:9600"), ("安装目录", @"D:\Apps\LocalOpsWindows"), ("项目数据", @"D:\Apps\LocalOpsWindows\data"), ("运行日志", @"D:\Apps\LocalOpsWindows\logs"), ("应用缓存", @"D:\Apps\LocalOpsWindows\cache") })
        { storage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); Put(storage, Text(label, 11, "Muted"), 0, index); var valueBlock = Text(value, 10); valueBlock.FontFamily = new FontFamily("Cascadia Code, Consolas"); valueBlock.ToolTip = value; Put(storage, valueBlock, 1, index++); }
        var maintenance = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (glyph, label) in new[] { ("refresh-cw", "重启后台"), ("square", "停止后台") }) maintenance.Children.Add(ActionButton(glyph, label, () => OpenDialog(label, Text("此操作仅用于样稿演示，不影响真实后台。"), ActionButton("check", "模拟确认", () => { HideDialog(); FooterText.Text = "模拟操作已完成"; }))));
        right.Children.Add(Section("运行与存储", storage, maintenance));
        var reset = ActionButton("refresh-cw", "恢复默认偏好", () => { CardSize = 320; _cards = true; _technical = _autostart = _notification = false; _follow = _tray = true; ApplyTheme("light"); SavePreferences(); ShowPage("settings"); }, style: "Flat");
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        right.Children.Add(Section("工作区偏好", reset));
        Put(_settingsGrid, left, 0); Put(_settingsGrid, right, 2); FooterText.Text = "偏好仅保存在当前 WPF 样稿目录中"; return new ScrollViewer { Content = _settingsGrid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private Border Section(string title, params UIElement[] rows)
    {
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SectionTitle") }); foreach (var row in rows) panel.Children.Add(row);
        return new Border { Child = panel, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 23), Margin = new Thickness(0, 0, 0, 23) }.Resource(Border.BorderBrushProperty, "LineBrush");
    }
    private Grid Setting(string title, string description, UIElement control)
    {
        var row = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto); row.MinHeight = 55; var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }; label.Children.Add(Text(title)); if (description.Length > 0) label.Children.Add(Text(description, 10, "Muted", new Thickness(0, 3, 0, 0))); Put(row, label, 0); if (control is FrameworkElement element) element.VerticalAlignment = VerticalAlignment.Center; Put(row, control, 1); return row;
    }
    private CheckBox Switch(bool value, Action<bool> update, string label)
    { var check = new CheckBox { IsChecked = value, Style = (Style)FindResource("Switch") }; AutomationProperties.SetName(check, label); check.Checked += (_, _) => update(true); check.Unchecked += (_, _) => update(false); return check; }
    private static T? FindChild<T>(DependencyObject root, string name) where T : FrameworkElement
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T element && element.Name == name) return element; var nested = FindChild<T>(child, name); if (nested is not null) return nested; } return null; }
}

internal sealed class TrendChart : FrameworkElement
{
    internal IReadOnlyList<TrendSeries> Series { get; init; } = [];
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context); Brush B(string key) => (Brush)FindResource(key + "Brush");
        var maximum = Math.Max(1, Math.Ceiling(Series.SelectMany(s => s.Values).DefaultIfEmpty().Max() * 1.15));
        for (var i = 0; i <= 2; i++) context.DrawLine(new Pen(B("Line"), 1), new Point(0, 4 + i * (ActualHeight - 8) / 2), new Point(ActualWidth, 4 + i * (ActualHeight - 8) / 2));
        ToolTip = $"0 - {maximum:0.#}%";
        foreach (var series in Series)
        {
            var shape = new StreamGeometry();
            using (var drawing = shape.Open())
                for (var i = 0; i < series.Values.Count; i++)
                {
                    var point = new Point(i / (double)Math.Max(1, series.Values.Count - 1) * ActualWidth, ActualHeight - 4 - Math.Clamp(series.Values[i] / maximum, 0, 1) * (ActualHeight - 8));
                    if (i == 0) drawing.BeginFigure(point, false, false); else drawing.LineTo(point, true, false);
                }
            context.DrawGeometry(null, new Pen(B(series.Color), 1.6), shape);
        }
    }
}
