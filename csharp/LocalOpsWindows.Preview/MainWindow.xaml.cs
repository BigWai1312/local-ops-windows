using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LocalOpsWindows.Preview;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty CardSizeProperty = DependencyProperty.Register(nameof(CardSize), typeof(double), typeof(MainWindow), new PropertyMetadata(320d));
    public static readonly DependencyProperty ShowResourcesProperty = DependencyProperty.Register(nameof(ShowResources), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));
    public double CardSize { get => (double)GetValue(CardSizeProperty); set => SetValue(CardSizeProperty, double.IsFinite(value) ? Math.Clamp(value, 300, 480) : 320d); }
    public bool ShowResources => (bool)GetValue(ShowResourcesProperty);
    private readonly List<DemoProject> _projects = DemoData.Projects();
    private readonly List<DemoProject> _external = DemoData.External();
    private readonly List<DemoLog> _logs = DemoData.Logs();
    private readonly bool _persist;
    private string _view = "launch", _theme = "light", _query = "", _kind = "全部类型", _status = "全部状态";
    private ItemsControl? _projectItems;
    private TextBlock? _summary;
    private string _logTarget = "console";
    private bool _technical, _follow = true, _autostart, _tray = true, _notification, _cards = true;
    private const string PreferenceName = "preview-preferences.json";
    private string PreferencePath => System.IO.Path.Combine(AppContext.BaseDirectory, "data", PreferenceName);

    public MainWindow() : this(true) { }
    public MainWindow(bool persist)
    {
        _persist = persist;
        InitializeComponent();
        ReadPreferences();
        ApplyTheme(_theme, false);
        ShowPage("launch");
    }

    private void ReadPreferences()
    {
        if (!_persist) return;
        try
        {
            if (!System.IO.File.Exists(PreferencePath)) return;
            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(PreferencePath));
            var value = document.RootElement;
            _theme = value.GetProperty("theme").GetString() is "dark" ? "dark" : value.GetProperty("theme").GetString() is "system" ? "system" : "light";
            if (value.TryGetProperty("cardSize", out var size)) CardSize = size.GetDouble();
            _technical = value.GetProperty("technical").GetBoolean();
            _follow = value.GetProperty("follow").GetBoolean();
            _cards = !value.TryGetProperty("cards", out var cards) || cards.GetBoolean();
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { CardSize = 320; _theme = "light"; }
    }
    private void SavePreferences()
    {
        if (!_persist) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PreferencePath)!);
            var text = JsonSerializer.Serialize(new { theme = _theme, cardSize = CardSize, technical = _technical, follow = _follow, cards = _cards });
            System.IO.File.WriteAllText(PreferencePath + ".tmp", text);
            System.IO.File.Move(PreferencePath + ".tmp", PreferencePath, true);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { FooterText.Text = "本次偏好未能保存"; }
    }
    private void Navigate(object sender, RoutedEventArgs e) { if (sender is RadioButton { Tag: string view }) ShowPage(view); }
    internal void ShowPage(string view)
    {
        _query = ""; _view = view;
        PageTitle.Text = view switch { "launch" => "启动台", "monitor" => "服务监控", "logs" => "日志中心", _ => "设置" };
        Breadcrumb.Text = "本机工作区   /   " + PageTitle.Text;
        foreach (var nav in new[] { LaunchNav, MonitorNav, LogsNav, SettingsNav }) nav.IsChecked = (string)nav.Tag == view;
        PageContent.Content = view switch { "launch" => BuildLaunch(), "monitor" => BuildMonitor(), "logs" => BuildLogs(), _ => BuildSettings() };
        UpdateResponsiveLayout();
    }
    private void ToggleTheme(object sender, RoutedEventArgs e) => ApplyTheme(IsDark ? "light" : "dark");
    private bool IsDark => ((SolidColorBrush)Application.Current.Resources["CanvasBrush"]).Color.R < 80;
    internal void ApplyTheme(string choice, bool save = true)
    {
        _theme = choice;
        var dark = choice == "dark";
        if (choice == "system")
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        string[] names = ["Canvas", "Surface", "Rail", "Text", "Muted", "Line", "StrongLine", "Accent", "AccentSoft", "AccentFill", "Blue", "BlueSoft", "Warn", "WarnSoft", "Danger", "DangerSoft", "Hover", "Button", "Highlight"];
        string[] light = ["#F4F6F6", "#FFFFFF", "#EDF1F1", "#203236", "#64767A", "#DCE4E4", "#C3D2D1", "#08675D", "#E1F2ED", "#08796B", "#3977AC", "#EAF2FA", "#9C6513", "#FAF0DC", "#B24452", "#FBECEF", "#EEF4F3", "#FCFDFD", "#FFFFFF"];
        string[] night = ["#1C2022", "#252A2C", "#191D1F", "#E9EEEE", "#B1BDBF", "#363F41", "#4A5759", "#99E4D4", "#23423A", "#218B77", "#9FC8ED", "#263C4D", "#EBC887", "#443923", "#F1A4AD", "#472F35", "#303A3B", "#303638", "#0DFFFFFF"];
        for (var i = 0; i < names.Length; i++) Application.Current.Resources[names[i] + "Brush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString((dark ? night : light)[i]));
        ThemeIcon.Kind = dark ? "sun" : "moon";
        if (save) SavePreferences();
        if (_view == "settings" && PageContent.Content is not null) { PageContent.Content = BuildSettings(); UpdateResponsiveLayout(); }
        if (_trendBlock is StackPanel trend) foreach (var chart in trend.Children.OfType<TrendChart>()) chart.InvalidateVisual();
        if (_view == "launch") RenderLaunchMonitor();
    }
    private void Minimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void ClosePreview(object sender, RoutedEventArgs e) => Close();
    private void RootSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();
    private void UpdateResponsiveLayout()
    {
        if (Root is null) return;
        SetValue(ShowResourcesProperty, Root.ActualWidth >= 1100);
        if (_view == "launch") UpdateLaunchLayout();
        if (_monitorGrid is not null && _view == "monitor")
        {
            var show = Root.ActualWidth >= 1200 && Root.ActualHeight >= 720;
            _monitorGrid.ColumnDefinitions[1].Width = new GridLength(show ? 22 : 0);
            _monitorGrid.ColumnDefinitions[2].Width = new GridLength(show ? Root.ActualWidth >= 1700 ? 320 : 300 : 0);
            _detail.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (_trendBlock is not null) _trendBlock.Visibility = Root.ActualHeight >= 850 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_settingsGrid is not null && _view == "settings")
        {
            var narrow = Root.ActualWidth < 1060;
            _settingsGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 36);
            _settingsGrid.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(_settingsRight!, narrow ? 0 : 2); Grid.SetRow(_settingsRight!, narrow ? 1 : 0);
        }
    }
    private FrameworkElement Toolbar()
    {
        var grid = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto);
        grid.Margin = new Thickness(0, 0, 0, 13);
        var searchGrid = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        var search = new TextBox { Name = "ProjectSearch", Padding = new Thickness(34, 6, 10, 6) };
        AutomationProperties.SetName(search, "搜索项目、端口或目录");
        var placeholder = Text("搜索项目、端口或目录", 12, "Muted", new Thickness(34, 0, 0, 0));
        placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false;
        search.TextChanged += (_, _) => { _query = search.Text; placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; if (_view == "launch") RefreshProjects(); else RefreshServices(); };
        searchGrid.Children.Add(search); searchGrid.Children.Add(placeholder);
        var glyph = Icon("search", 14); glyph.HorizontalAlignment = HorizontalAlignment.Left; glyph.Margin = new Thickness(11, 0, 0, 0); glyph.IsHitTestVisible = false; searchGrid.Children.Add(glyph);
        Put(grid, searchGrid, 0);
        Put(grid, ActionButton("refresh-cw", "刷新状态", () => FooterText.Text = "示例工作区已刷新", true), 1);
        Put(grid, ActionButton("plus", "添加服务", () => EditProject(null, "service"), style: "Primary"), 2);
        Put(grid, ActionButton("plus", "添加任务", () => EditProject(null, "task")), 3);
        return grid;
    }
    private Border Summary()
    {
        _summary = Text("", 11, "Muted");
        UpdateSummary();
        return new Border { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 15), Padding = new Thickness(0, 0, 0, 14), Child = _summary }.Resource(Border.BorderBrushProperty, "LineBrush");
    }
    private void UpdateSummary()
    {
        if (_summary is null) return;
        _summary.Inlines.Clear();
        foreach (var (number, label, color) in new[] { (_projects.Count, "已登记项目", "Text"), (_projects.Count(p => p.Status == "运行中"), "正在运行", "Accent"), (Services().Count(), "监听服务", "Text"), (_projects.Count(p => p.Status is "失败" or "端口占用"), "需要关注", "Warn") })
        {
            var run = new System.Windows.Documents.Run(number.ToString()) { FontSize = 14, FontWeight = FontWeights.SemiBold };
            run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, color + "Brush");
            _summary.Inlines.Add(run); _summary.Inlines.Add(new System.Windows.Documents.Run("  " + label + "      "));
        }
    }
    private FrameworkElement BuildLaunch()
    {
        var root = new DockPanel();
        var top = new StackPanel(); top.Children.Add(Toolbar()); top.Children.Add(Summary());
        var tools = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto); tools.Margin = new Thickness(0, 0, 0, 13);
        var filters = new StackPanel { Orientation = Orientation.Horizontal };
        filters.Children.Add(Choice(["全部类型", "长期服务", "批处理任务"], _kind, value => { _kind = value; RefreshProjects(); }, "项目类型", 125));
        filters.Children.Add(Choice(["全部状态", "运行中", "未运行", "需要关注", "已完成"], _status, value => { _status = value; RefreshProjects(); }, "项目状态", 125));
        filters.Children.Add(ActionButton("download", "导出示例项目", ExportProjects, true));
        var display = new StackPanel { Orientation = Orientation.Horizontal };
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var cards in new[] { true, false })
        {
            var label = cards ? "卡片视图" : "列表视图";
            var mode = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "ProjectLayout", Content = Icon(cards ? "layout-grid" : "file-text", 14), ToolTip = label, IsChecked = _cards == cards, Height = 28 };
            AutomationProperties.SetName(mode, label);
            mode.Click += (_, _) => { _cards = cards; ApplyProjectLayout(); SavePreferences(); };
            modes.Children.Add(mode);
        }
        display.Children.Add(new Border { Background = (Brush)FindResource("RailBrush"), CornerRadius = new CornerRadius(5), Padding = new Thickness(2), Margin = new Thickness(0, 0, 16, 0), Child = modes }.Resource(Border.BackgroundProperty, "RailBrush"));
        display.Children.Add(CardSizeControl());
        Put(tools, filters, 0); Put(tools, display, 1); top.Children.Add(tools); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        _projectItems = new ItemsControl();
        _projectItems.GroupStyle.Add(new GroupStyle { HeaderTemplate = GroupHeader() });
        ApplyProjectLayout();
        _launchGrid = Columns(new GridLength(1, GridUnitType.Star), new GridLength(20), new GridLength(278));
        _launchGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _launchGrid.RowDefinitions.Add(new RowDefinition());
        _launchScroll = new ScrollViewer { Name = "LaunchProjects", Content = _projectItems, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Put(_launchGrid, _launchScroll, 0, 1);
        _launchMonitor = new Border { Name = "LaunchMonitor", BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top }.Resource(Border.BorderBrushProperty, "LineBrush");
        Put(_launchGrid, _launchMonitor, 2, 1); root.Children.Add(_launchGrid); RenderLaunchMonitor();
        RefreshProjects(); return root;
    }
    private void ApplyProjectLayout()
    {
        if (_projectItems is null) return;
        _projectItems.ItemTemplate = (DataTemplate)FindResource(_cards ? "ProjectCard" : "ProjectRow");
        var panel = new FrameworkElementFactory(_cards ? typeof(ProjectCardPanel) : typeof(StackPanel));
        if (_cards) panel.SetBinding(ProjectCardPanel.MinimumCardWidthProperty, new System.Windows.Data.Binding(nameof(CardSize)) { Source = this });
        _projectItems.ItemsPanel = new ItemsPanelTemplate(panel);
        _projectItems.GroupStyle[0].Panel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)));
    }
    private DataTemplate GroupHeader()
    {
        var template = new DataTemplate();
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); panel.SetValue(MarginProperty, new Thickness(0, 3, 0, 10));
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name")); title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); title.SetValue(TextBlock.FontSizeProperty, 12d); panel.AppendChild(title);
        var count = new FrameworkElementFactory(typeof(TextBlock)); count.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("ItemCount")); count.SetValue(TextBlock.FontSizeProperty, 10d); count.SetValue(MarginProperty, new Thickness(8, 2, 0, 0)); count.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.AppendChild(count); template.VisualTree = panel; return template;
    }
    private void RefreshProjects()
    {
        if (_projectItems is null) return;
        var values = _projects.Where(p => Matches(p) && (_kind == "全部类型" || p.Kind == (_kind == "长期服务" ? "service" : "task")) && (_status == "全部状态" || p.Status == _status || _status == "需要关注" && p.Status is "失败" or "端口占用")).ToList();
        var collection = new System.Windows.Data.ListCollectionView(values);
        collection.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(DemoProject.Kind), new KindConverter()));
        _projectItems.ItemsSource = collection; UpdateSummary(); RenderLaunchMonitor(); FooterText.Text = $"显示 {values.Count} / {_projects.Count} 个项目";
    }
    private bool Matches(DemoProject p) => $"{p.Name} {p.Port} {p.Directory} {p.Command} {p.Notes}".Contains(_query.Trim(), StringComparison.OrdinalIgnoreCase);
    private FrameworkElement CardSizeControl()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Icon("sliders-horizontal", 14)); panel.Children.Add(Text("卡片宽度", 11, "Muted", new Thickness(7, 0, 9, 0)));
        var slider = new Slider { Name = "CardSizeSlider", Minimum = 300, Maximum = 480, Value = CardSize, Width = 100, IsMoveToPointEnabled = true, SmallChange = 10, LargeChange = 30 };
        AutomationProperties.SetName(slider, "卡片宽度");
        var value = Text($"{CardSize:0}", 10, "Muted", new Thickness(8, 0, 0, 0)); value.Width = 26; value.TextAlignment = TextAlignment.Right;
        slider.ValueChanged += (_, e) => { CardSize = e.NewValue; value.Text = $"{CardSize:0}"; SavePreferences(); };
        panel.Children.Add(slider); panel.Children.Add(value); return panel;
    }
    private void ToggleProject(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DemoProject p }) Toggle(p);
    }
    private void Toggle(DemoProject p)
    {
        if (p.Status is "失败" or "端口占用") { OpenDialog("项目需要处理", Text(p.Uptime, 12), ActionButton("pencil", "编辑项目", () => EditProject(p))); return; }
        p.Status = p.Status == "运行中" ? "未运行" : "运行中"; p.Uptime = p.Status == "运行中" ? "刚刚启动" : "刚刚停止"; p.Refresh(); UpdateSummary();
        _logs.Add(new(DateTime.Now.ToString("HH:mm:ss"), "INFO", $"{p.Name}已模拟{(p.Status == "运行中" ? "启动" : "停止")}。", p.Id)); FooterText.Text = $"已模拟{(p.Status == "运行中" ? "启动" : "停止")} {p.Name}";
        if (_view == "launch") RenderLaunchMonitor();
    }
    private void ProjectLogs(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: DemoProject p }) { _logTarget = p.Id; ShowPage("logs"); } }
    private void ProjectInfo(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: DemoProject p }) Info(p); }
    private void ProjectMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DemoProject p } source) return;
        var menu = new ContextMenu();
        foreach (var (glyph, label, action) in new (string, string, Action)[] { ("file-text", "完整信息", () => Info(p)), ("pencil", "编辑项目", () => EditProject(p)), ("file-text", "查看日志", () => { _logTarget = p.Id; ShowPage("logs"); }), ("refresh-cw", "重启", () => { p.Uptime = "刚刚重启"; p.Refresh(); FooterText.Text = "已模拟重启"; }), ("trash-2", "删除项目", () => OpenDialog("删除示例项目", Text($"确定移除「{p.Name}」？", 12), ActionButton("trash-2", "删除", () => { _projects.Remove(p); HideDialog(); RefreshProjects(); }))) })
        { var item = new MenuItem { Header = label, Icon = Icon(glyph, 14) }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        source.ContextMenu = menu; menu.PlacementTarget = source; menu.IsOpen = true;
    }
    private void Info(DemoProject p)
    {
        var text = $"名称：{p.Name}\n端口：{p.PortLabel}\n状态：{p.Status}\n\n备注\n{p.Notes}\n\n启动命令\n{p.Command}\n\n工作目录\n{p.Directory}";
        var content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 220, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        OpenDialog(p.Name, content, ActionButton("copy", "复制信息", () => Copy(text)));
    }
    private void EditProject(DemoProject? p, string kind = "service")
    {
        var form = new StackPanel();
        TextBox Field(string title, string value) { form.Children.Add(Text(title, 11, margin: new Thickness(0, 10, 0, 6))); var box = new TextBox { Text = value }; AutomationProperties.SetName(box, title); form.Children.Add(box); return box; }
        var name = Field("项目名称", p?.Name ?? ""); var command = Field("启动命令", p?.Command ?? ""); var path = Field("工作目录", p?.Directory ?? @"C:\Projects\Example"); var port = Field("端口（任务可留空）", p?.Port > 0 ? p.Port.ToString() : "");
        var notes = Field("备注（可选）", p?.Notes ?? ""); notes.Height = 64; notes.AcceptsReturn = true; notes.TextWrapping = TextWrapping.Wrap; notes.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var error = Text("", 11, "Danger", new Thickness(0, 10, 0, 0)); form.Children.Add(error);
        OpenDialog(p is null ? kind == "task" ? "添加任务" : "添加服务" : "编辑项目", form, ActionButton("check", "保存", () =>
        {
            if (new[] { name, command, path }.Any(box => string.IsNullOrWhiteSpace(box.Text)) || port.Text.Length > 0 && (!int.TryParse(port.Text, out var number) || number is < 1 or > 65535)) { error.Text = "请填写名称、命令、目录，并使用有效端口。"; return; }
            var item = p ?? new DemoProject { Kind = kind, Glyph = kind == "task" ? "file-text" : "server" };
            item.Name = name.Text.Trim(); item.Command = command.Text.Trim(); item.Directory = path.Text.Trim(); item.Notes = notes.Text.Trim(); item.Port = int.TryParse(port.Text, out var parsed) ? parsed : 0; item.Refresh(); if (p is null) _projects.Add(item);
            HideDialog(); ShowPage("launch"); FooterText.Text = "已保存到本次样稿";
        }, style: "Primary"));
    }
    private void ExportProjects()
    {
        var dialog = new SaveFileDialog { FileName = "local-ops-example-projects.json", Filter = "JSON|*.json" };
        if (dialog.ShowDialog(this) == true) TrySave(dialog.FileName, JsonSerializer.Serialize(_projects, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void TrySave(string file, string text) { try { System.IO.File.WriteAllText(file, text); FooterText.Text = "已导出演示数据"; } catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { OpenDialog("导出失败", Text(error.Message)); } }
    private void Copy(string text) { try { Clipboard.SetText(text); FooterText.Text = "已复制"; } catch (System.Runtime.InteropServices.COMException) { OpenDialog("复制内容", new TextBox { Text = text, IsReadOnly = true, Height = 200, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap }); } }
    private void OpenDialog(string title, UIElement body, params Button[] actions)
    {
        DialogTitle.Text = title; DialogBody.Children.Clear(); DialogBody.Children.Add(body); DialogActions.Children.Clear(); foreach (var action in actions) DialogActions.Children.Add(action); Overlay.Visibility = Visibility.Visible; DialogSurface.MaxHeight = Math.Max(300, Root.ActualHeight - 50);
    }
    private void HideDialog() => Overlay.Visibility = Visibility.Collapsed;
    private void CloseDialog(object sender, RoutedEventArgs e) => HideDialog();
    private void OverlayMouseDown(object sender, MouseButtonEventArgs e) { if (e.OriginalSource == Overlay) HideDialog(); }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) HideDialog();
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; CommandPalette(); }
    }
    private void CommandPalette()
    {
        var body = new StackPanel(); var search = new TextBox(); var results = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; body.Children.Add(search); body.Children.Add(results);
        void Refresh() { results.Children.Clear(); foreach (var p in _projects.Where(p => p.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase))) { var button = ActionButton("server", p.Name, () => Info(p), style: "Flat"); button.HorizontalAlignment = HorizontalAlignment.Stretch; results.Children.Add(button); } }
        search.TextChanged += (_, _) => Refresh(); Refresh(); OpenDialog("命令面板", body); search.Focus();
    }
    private new LucideIcon Icon(string glyph, double size = 15) => new() { Kind = glyph, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
    private TextBlock Text(string text, double size = 12, string? color = null, Thickness? margin = null)
    { var block = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0) }; if (color is not null) block.SetResourceReference(TextBlock.ForegroundProperty, color + "Brush"); return block; }
    private Button ActionButton(string glyph, string label, Action action, bool iconOnly = false, string? style = null)
    {
        var button = new Button { Margin = new Thickness(0, 0, 8, 0), ToolTip = label }; AutomationProperties.SetName(button, label);
        if (style is not null || iconOnly) button.Style = (Style)FindResource(style ?? "IconButton");
        if (iconOnly) button.Content = Icon(glyph); else { var panel = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(Icon(glyph, 14)); panel.Children.Add(Text(label, 12, margin: new Thickness(6, 0, 0, 0))); button.Content = panel; }
        button.Click += (_, _) => action(); return button;
    }
    private static Grid Columns(params GridLength[] widths) { var grid = new Grid(); foreach (var width in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width }); return grid; }
    private static void Put(Grid grid, UIElement child, int column, int row = 0) { Grid.SetColumn(child, column); Grid.SetRow(child, row); grid.Children.Add(child); }
    private ComboBox Choice(string[] values, string selected, Action<string> change, string label, double width)
    { var combo = new ComboBox { ItemsSource = values, SelectedItem = selected, Width = width, Margin = new Thickness(0, 0, 8, 0) }; AutomationProperties.SetName(combo, label); combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string value) change(value); }; return combo; }
}

internal sealed class KindConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => (string)value == "service" ? "长期服务" : "批处理任务";
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
internal static class ResourceExtensions
{
    internal static T Resource<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement { element.SetResourceReference(property, key); return element; }
}
