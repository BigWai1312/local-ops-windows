using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace LocalOpsWindows.Client;

public partial class MainWindow : Window
{
    private readonly string[] _args;
    private readonly bool _demoMode;
    private readonly BackendSupervisor _backend;
    private readonly Func<int, ApiClient> _createApi;
    private readonly DispatcherTimer _pollTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private ApiClient? _api;
    private DashboardState _state = new();
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayAppIcon;
    private bool _quitting;
    private string _closeBehavior = "tray";
    private bool _loaded;
    private bool _pollInFlight;
    private bool _logInFlight;
    private bool _portConflictInFlight;
    private bool _suppressLogSelection;
    private bool? _compactLayout;
    private string _logTarget = "console";
    private readonly Dictionary<string, ImageSource?> _iconCache = [];
    private readonly MonitorSession _monitor = new();
    private readonly TaskCompletionTracker _taskCompletions = new();
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _notificationInFlight;
    private bool _backendStopped;
    private string _displayedLogTarget = "";
    private readonly ReleaseUpdater _updater;
    private ResourcePack _clientResources;
    private AvailableUpdate? _availableUpdate;
    private bool _updateInFlight;

    public MainWindow(string[] args, BackendSupervisor? backend = null, Func<int, ApiClient>? createApi = null)
    {
        _backend = backend ?? new BackendSupervisor();
        _createApi = createApi ?? (port => new ApiClient(port));
        InitializeComponent();
        _args = args;
        _demoMode = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        _updater = new ReleaseUpdater();
        _clientResources = ClientResources.Load(ReleaseUpdater.CurrentVersion, includeCache: !_demoMode);
        if (_demoMode && args.Contains("--compact", StringComparer.OrdinalIgnoreCase))
        {
            Width = 1080;
            Height = 720;
        }
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) =>
        {
            if (IsMonitoringVisible) await RefreshStateAsync();
        };
        _notificationTimer.Tick += async (_, _) => await CheckTaskNotificationsAsync();
        IsVisibleChanged += (_, _) => UpdatePollingVisibility();
        SourceInitialized += ApplyWindowBackdrop;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        UpdateResponsiveLayout();
        if (!_demoMode)
        {
            SetupTray();
        }
        RestoreWindowPosition();
        if (double.TryParse(ReadSetting("CardWidth"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cardWidth))
            CardWidthSlider.Value = Math.Clamp(cardWidth, 300, 480);
        var savedTheme = ReadSetting("Theme") ?? "system";
        SelectComboTag(ThemeCombo, savedTheme);
        ApplyTheme(savedTheme);
        MinimizeToTrayCheck.IsChecked = !_demoMode && !string.Equals(
            ReadSetting("MinimizeToTray"), "false", StringComparison.OrdinalIgnoreCase);
        _closeBehavior = ReadSetting("CloseBehavior") == "exit" ? "exit" : "tray";
        SelectComboTag(CloseBehaviorCombo, _closeBehavior);
        AutostartCheck.IsChecked = IsAutostartEnabled();
        AutostartCheck.IsEnabled = !_demoMode && IsInstalledClient();
        AutostartCheck.ToolTip = IsInstalledClient()
            ? "登录 Windows 后在后台启动客户端"
            : "开机启动仅适用于已安装的客户端版本";
        TaskNotifyCheck.IsChecked = ReadSetting("TaskNotifications") == "true";
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;

        if (_args.Contains("--background", StringComparer.OrdinalIgnoreCase))
        {
            Hide();
        }

        if (_demoMode)
        {
            _state = CreateDemoState();
            _monitor.Observe(_state);
            RefreshCollections();
            RefreshLogTargets(_state.Apps);
            AppCountText.Text = _state.Apps.Count.ToString();
            RunningCountText.Text = _state.Apps.Count(x => x.Running).ToString();
            ServiceCountText.Text = _state.Services.Count(x => !x.Hidden).ToString();
            WarningCountText.Text = _state.Apps.Count(x => x.PortOccupied).ToString();
            ProjectHintText.Text = $"{_state.Apps.Count} 个虚构项目";
            ActivityText.Text = BuildActivityText(_state);
            ResourceText.Text = BuildResourceText(_state);
            WatchKeywordItems.ItemsSource = _state.WatchedKeywords.ToList();
            WatchedGrid.ItemsSource = _state.Watched;
            WatchedSummaryText.Text = $"{_state.Watched.Count} 个匹配进程 · {_state.WatchedKeywords.Count} 个关键字";
            RuntimeInfoText.Text = "演示模式\n仅显示虚构数据，不连接本机后台，也不会修改真实项目。";
            LastSyncText.Text = "演示数据 · 不连接后台";
            SetBackendStatus("演示模式", "仅显示虚构数据", "AccentBrush");
            SetView(LaunchpadView, "启动台");
            if (_args.Contains("--services", StringComparer.OrdinalIgnoreCase))
            {
                SetView(ServicesView, "服务监控");
            }
            if (_args.Contains("--editor", StringComparer.OrdinalIgnoreCase))
                _ = Dispatcher.BeginInvoke(new Action(() => OpenEditor(_state.Apps.FirstOrDefault())));
            return;
        }

        if (IsMonitoringVisible) _pollTimer.Start();
        await RefreshStateAsync();
    }

    private bool IsMonitoringVisible => IsVisible && WindowState != WindowState.Minimized;

    private void UpdatePollingVisibility()
    {
        if (!_loaded || _quitting || (_demoMode && _api is null)) return;
        if (!IsMonitoringVisible)
        {
            // Task supervision and log capture belong to the backend, not UI polling.
            _pollTimer.Stop();
            _monitor.ResetBaseline();
            if (!_demoMode && !_backendStopped && TaskNotifyCheck.IsChecked == true)
                _notificationTimer.Start();
            return;
        }
        _notificationTimer.Stop();
        if (_backendStopped) return;
        if (!_pollTimer.IsEnabled)
        {
            _pollTimer.Start();
            _ = RefreshStateAsync();
        }
    }

    private async Task RefreshStateAsync()
    {
        if (_pollInFlight || _quitting || _backendStopped || (_demoMode && _api is null))
        {
            return;
        }

        _pollInFlight = true;
        try
        {
            if (_api is null)
            {
                SetBackendStatus("正在连接", "正在探测或启动本机后台…", "MutedTextBrush");
                await _backend.StartAsync(_lifetime.Token);
                if (_quitting) return;
                _api = _createApi(_backend.Port);
                SetBackendStatus("正在同步", "后台已连接，正在读取项目与服务…", "MutedTextBrush");
            }
            var next = await _api.GetStateAsync(_lifetime.Token);
            await LoadProjectIconsAsync(next);
            _taskCompletions.Observe(next.Apps);
            _state = next;
            if (_quitting || (_loaded && !IsMonitoringVisible)) return;
            _monitor.Observe(next);
            RefreshCollections();

            AppCountText.Text = next.Apps.Count.ToString();
            RunningCountText.Text = next.Apps.Count(x => x.Running).ToString();
            ServiceCountText.Text = next.Services.Count(x => !x.Hidden).ToString();
            WarningCountText.Text = next.Apps.Count(x => x.HasWarning).ToString();
            ProjectHintText.Text = $"{next.Apps.Count} 个已登记项目";
            ActivityText.Text = BuildActivityText(next);
            ResourceText.Text = BuildResourceText(next);
            RuntimeInfoText.Text =
                $"版本 v{next.Version}\n控制台端口 :{next.ConsolePort}\n" +
                $"工作目录 {next.ConsoleCwd ?? "—"}\n" +
                $"数据目录 {ClientStorage.Data}\n日志目录 {ClientStorage.Logs}";
            LastSyncText.Text = $"最后同步 {DateTime.Now:HH:mm:ss}";
            SetBackendStatus("已连接", $"本机回环 :{next.ConsolePort}", "AccentBrush");
            _pollTimer.Interval = TimeSpan.FromSeconds(5);

            if (LogsView.Visibility == Visibility.Visible)
            {
                RefreshLogTargets(next.Apps);
                if (FollowLogCheck.IsChecked == true) await RefreshLogAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _api?.Dispose();
            _api = null;
            _monitor.ResetBaseline();
            _taskCompletions.Reset();
            _pollTimer.Interval = TimeSpan.FromSeconds(5);
            SetBackendStatus("正在自动重连", ex.Message, "WarningBrush");
            LastSyncText.Text = "连接中断 · 当前显示上次同步数据";
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private void RefreshCollections()
    {
        if (LaunchpadView.Visibility == Visibility.Visible)
        {
            var selectedIds = AppsList.SelectedItems.OfType<AppSnapshot>()
                .Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            var apps = FilterApps(_state.Apps).OrderBy(app => app.Kind == "task" ? 1 : 0).ToList();
            var grouped = new ListCollectionView(apps);
            grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AppSnapshot.Kind)));
            AppsList.ItemsSource = grouped;
            foreach (var app in apps.Where(x => selectedIds.Contains(x.Id)))
            {
                AppsList.SelectedItems.Add(app);
            }
            UpdateLaunchMonitor();
        }

        if (ServicesView.Visibility != Visibility.Visible) return;
        var mode = (MonitorTabs.SelectedItem as TabItem)?.Tag as string ?? "list";
        ServiceBody.Visibility = mode == "events" ? Visibility.Collapsed : Visibility.Visible;
        EventsPanel.Visibility = mode == "events" ? Visibility.Visible : Visibility.Collapsed;
        MetricFilter.Visibility = mode == "resources" ? Visibility.Visible : Visibility.Collapsed;
        DiscoveryTab.Header = $"新发现 ({_monitor.Discoveries.Count()})";
        EventList.ItemsSource = _monitor.Events.ToList();
        var selectedService = ServicesGrid.SelectedItem as ServiceSnapshot;
        var services = FilterServices(mode == "new" ? _monitor.Discoveries : _state.Services).ToList();
        if (mode == "resources")
            services = services.GroupBy(s => s.Pid).Select(g => g.First())
                .OrderByDescending(s => SelectedTag(MetricFilter) == "mem" ? s.Mem : s.Cpu).Take(20).ToList();
        ServicesGrid.ItemsSource = services;
        ServiceFilterText.Text = $"{services.Count} 个监听服务 · " +
            $"{services.Count(x => !string.IsNullOrWhiteSpace(x.AppId))} 个已登记";
        if (selectedService is not null)
        {
            ServicesGrid.SelectedItem = services.FirstOrDefault(x =>
                x.Key == selectedService.Key && x.InstanceKey == selectedService.InstanceKey);
        }
        if (ServicesGrid.SelectedItem is null && services.Count > 0)
        {
            ServicesGrid.SelectedIndex = 0;
        }
        UpdateServiceDetailHeader();
        DismissDiscoveryButton.Visibility = mode == "new" ? Visibility.Visible : Visibility.Collapsed;
        WatchKeywordItems.ItemsSource = _state.WatchedKeywords.ToList();
        WatchedGrid.ItemsSource = _state.Watched;
        WatchedSummaryText.Text = $"{_state.Watched.Count} 个匹配进程 · {_state.WatchedKeywords.Count} 个关键字";
    }

    private IEnumerable<AppSnapshot> FilterApps(IEnumerable<AppSnapshot> apps)
    {
        var query = SearchBox.Text.Trim();
        var kind = SelectedTag(KindFilterCombo) ?? "all";
        var state = SelectedTag(StateFilterCombo) ?? "all";
        return apps.Where(app =>
            (kind == "all" || app.Kind == kind)
            && (state == "all"
                || (state == "running" && app.Running)
                || (state == "stopped" && !app.Running && !app.PortOccupied
                    && app.LastExit?.EffectiveStatus is not ("succeeded" or "failed" or "canceled" or "stopped"))
                || (state == "succeeded" && !app.Running && app.LastExit?.EffectiveStatus == "succeeded")
                || (state == "failed" && !app.Running && app.LastExit?.EffectiveStatus == "failed")
                || (state == "canceled" && !app.Running && app.LastExit?.EffectiveStatus is "canceled" or "stopped")
                || (state == "warning" && app.HasWarning))
            && (string.IsNullOrWhiteSpace(query)
                || $"{app.Name} {app.Command} {app.Notes} {app.Cwd} {app.Port} {app.Pid}"
                    .Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    private IEnumerable<ServiceSnapshot> FilterServices(IEnumerable<ServiceSnapshot> services)
    {
        var query = SearchBox.Text.Trim();
        var group = SelectedTag(ServiceGroupFilter) ?? "all";
        return services.Where(service =>
            ((group == "all" && !service.Hidden)
                || (group == "hidden" && service.Hidden)
                || (group != "hidden" && service.Group == group && !service.Hidden))
            && (string.IsNullOrWhiteSpace(query)
            || $"{service.DisplayName} {service.Command} {service.Cwd} {service.Port} {service.Pid}"
                .Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(service => service.Pinned).ThenBy(service => service.Port);
    }

    private static string BuildActivityText(DashboardState state)
    {
        var running = state.Apps.Count(x => x.Running);
        var warnings = state.Apps.Count(x => x.HasWarning);
        var watched = state.Watched.Count;
        var degraded = state.Degraded ? "后台报告存在降级项，请查看日志。" : "后台运行正常。";
        return $"当前有 {running} 个项目运行中。\n" +
               $"{state.Services.Count(x => !x.Hidden)} 个监听服务，{watched} 个关注进程。\n" +
               (warnings == 0 ? "端口状态正常。" : $"有 {warnings} 个端口需要处理。") +
               $"\n{degraded}";
    }

    private static string BuildResourceText(DashboardState state)
    {
        var ports = state.Services.Where(x => x.Port > 0).Select(x => x.Port).Distinct().Order().Take(8);
        return $"已观测端口：{(ports.Any() ? string.Join("、", ports) : "—")}\n" +
               $"关注进程：{state.Watched.Count}\n" +
               $"降级原因：{state.DegradedReasons.Count}";
    }

    private void SetupTray()
    {
        _trayAppIcon = new System.Drawing.Icon(
            Path.Combine(AppContext.BaseDirectory, "Assets", "local-ops.ico"));
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayAppIcon,
            Text = "本地运维台 Windows",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
        menu.Items.Add("隐藏窗口", null, (_, _) => Hide());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出客户端", null, (_, _) => ExitFromButton(null, null));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void RefreshLogTargets(IEnumerable<AppSnapshot> apps)
    {
        var targets = apps.ToList();
        if (LogTargetCombo.Items.Count == targets.Count + 1
            && LogTargetCombo.Items.OfType<ComboBoxItem>().Skip(1)
                .Select(item => (item.Tag?.ToString() ?? "", item.Content?.ToString() ?? ""))
                .SequenceEqual(targets.Select(app => (app.Id, $"{app.Name} · {app.KindLabel}")))) return;
        var current = _logTarget;
        _suppressLogSelection = true;
        try
        {
            LogTargetCombo.Items.Clear();
            LogTargetCombo.Items.Add(new ComboBoxItem { Content = "控制台日志", Tag = "console" });
            foreach (var app in targets)
            {
                LogTargetCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{app.Name} · {app.KindLabel}",
                    Tag = app.Id,
                });
            }

            LogTargetCombo.SelectedItem = LogTargetCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), current, StringComparison.Ordinal))
                ?? LogTargetCombo.Items[0];
        }
        finally
        {
            _suppressLogSelection = false;
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowLaunchpad(object sender, RoutedEventArgs e)
    {
        SetView(LaunchpadView, "启动台");
    }

    private void ShowServices(object sender, RoutedEventArgs e)
    {
        SetView(ServicesView, "服务监控");
    }

    private async void ShowLogs(object sender, RoutedEventArgs e)
    {
        SetView(LogsView, "日志中心");
        await RefreshLogAsync();
    }

    private void ShowSettings(object sender, RoutedEventArgs e)
    {
        SetView(SettingsView, "设置");
    }

    private void SetView(Grid visible, string title)
    {
        LaunchpadView.Visibility = visible == LaunchpadView ? Visibility.Visible : Visibility.Collapsed;
        ServicesView.Visibility = visible == ServicesView ? Visibility.Visible : Visibility.Collapsed;
        LogsView.Visibility = visible == LogsView ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = visible == SettingsView ? Visibility.Visible : Visibility.Collapsed;
        PageTitleText.Text = title;
        foreach (var (button, view) in new[]
        {
            (LaunchpadNav, LaunchpadView), (ServicesNav, ServicesView),
            (LogsNav, LogsView), (SettingsNav, SettingsView),
        })
        {
            button.SetResourceReference(BackgroundProperty,
                visible == view ? "AccentSoftBrush" : "TransparentBrush");
        }
        SearchBox.IsEnabled = visible == LaunchpadView || visible == ServicesView;
        RefreshCollections();
        if (visible == LogsView) RefreshLogTargets(_state.Apps);
    }

    private async void RefreshNow(object sender, RoutedEventArgs e)
    {
        _backendStopped = false;
        UpdatePollingVisibility();
        await RefreshStateAsync();
    }

    private async void ToggleApp(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppSnapshot app)
        {
            if (!app.Running && app.PortOccupied) await ResolvePortConflictAsync(app);
            else await RunAppActionAsync(app, app.Running ? "stop" : "start");
        }
    }

    private async Task ResolvePortConflictAsync(AppSnapshot app)
    {
        if (_demoMode || _api is null || app.Port is not int port || _portConflictInFlight) return;
        _portConflictInFlight = true;
        try
        {
            PortProcess? target = null;
            try
            {
                target = await Task.Run(() => PortProcess.Inspect(port));
                if (PortConflictDialog.Create(target, $"登记项目：{app.Name}", this).ShowDialog() != true) return;
                await Task.Run(target.Stop);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                if (MessageBox.Show(this, "Windows 拒绝了普通权限的读取或结束请求。是否为这一次操作请求管理员权限？\n管理员窗口会重新展示进程，请核对后确认。",
                    "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
                var owners = await Task.Run(() => PortProcess.ListenerPids(port));
                if (owners.Count != 1) throw new InvalidOperationException("端口占用状态已变化，请刷新后重试。");
                if (!await PortConflictDialog.ElevateAsync(target?.Pid ?? owners.Single(), port, target?.Created ?? 0)) return;
            }
            var remaining = await Task.Run(() => PortProcess.ListenerPids(port));
            MessageBox.Show(this, remaining.Count == 0
                    ? $"进程已结束，端口 {port} 已释放。现在可以从启动台启动项目。"
                    : $"原进程已结束，但端口 {port} 仍被其他进程占用或被自动拉起。请重新核对；不会继续自动结束进程。",
                "处理完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowOperationError(ex, "无法处理端口占用"); }
        finally
        {
            _portConflictInFlight = false;
            await RefreshStateAsync();
        }
    }

    private async void RestartApp(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppSnapshot app)
        {
            await RunAppActionAsync(app, "restart");
        }
    }

    private async void DiagnoseApp(object sender, RoutedEventArgs e)
    {
        if (_api is null || ((FrameworkElement)sender).DataContext is not AppSnapshot app)
        {
            return;
        }

        try
        {
            using var result = await _api.PostAsync($"api/apps/{app.Id}/diagnose", new { }, _lifetime.Token);
            var report = result?.RootElement.Deserialize<DiagnosticReport>() ?? new();
            var dialog = new DiagnosticWindow(app, report) { Owner = this, Icon = Icon };
            if (dialog.ShowDialog() == true)
            {
                if (dialog.RequestedAction == "logs")
                {
                    _logTarget = app.Id;
                    SetView(LogsView, "日志中心");
                    await RefreshLogAsync();
                }
                else OpenEditor(app, repairAction: dialog.RequestedAction);
            }
        }
        catch (Exception ex)
        {
            ShowOperationError(ex);
        }
    }

    private async Task RunAppActionAsync(AppSnapshot app, string action)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            using var result = await _api.PostAsync($"api/apps/{app.Id}/{action}", new { }, _lifetime.Token);
            await RefreshStateAsync();
        }
        catch (Exception ex)
        {
            ShowOperationError(ex);
        }
    }

    private async void DeleteApp(object sender, RoutedEventArgs e)
    {
        if (_api is null || ((FrameworkElement)sender).DataContext is not AppSnapshot app)
        {
            return;
        }

        var choice = MessageBox.Show(
            $"确定删除“{app.Name}”吗？\n该项目将停止运行，并从启动台移除。",
            "删除项目", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (choice != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            using var result = await _api.DeleteAsync($"api/apps/{app.Id}", _lifetime.Token);
            await RefreshStateAsync();
        }
        catch (Exception ex)
        {
            ShowOperationError(ex);
        }
    }

    private async void BatchStopSelected(object sender, RoutedEventArgs e)
    {
        if (_api is null)
        {
            return;
        }

        var selected = AppsList.SelectedItems.OfType<AppSnapshot>()
            .Where(x => x.Running).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("没有选中的运行中项目。", "批量停止",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show($"确定停止选中的 {selected.Count} 个项目吗？",
                "批量停止", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
        {
            return;
        }

        foreach (var app in selected)
        {
            try
            {
                using var result = await _api.PostAsync($"api/apps/{app.Id}/stop", new { }, _lifetime.Token);
            }
            catch (Exception ex)
            {
                ShowOperationError(ex, $"停止失败 · {app.Name}");
            }
        }
        await RefreshStateAsync();
    }

    private async void MoveAppUp(object sender, RoutedEventArgs e) => await MoveSelectedAppAsync(-1);

    private async void MoveAppDown(object sender, RoutedEventArgs e) => await MoveSelectedAppAsync(1);

    private async Task MoveSelectedAppAsync(int direction)
    {
        if (_api is null || AppsList.SelectedItems.OfType<AppSnapshot>().FirstOrDefault() is not { } selected)
        {
            return;
        }

        var ordered = _state.Apps.Where(x => x.Kind == selected.Kind).ToList();
        var index = ordered.FindIndex(x => x.Id == selected.Id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= ordered.Count)
        {
            return;
        }

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        try
        {
            using var result = await _api.PostAsync("api/apps/reorder",
                new { ids = ordered.Select(x => x.Id).ToArray() }, _lifetime.Token);
            await RefreshStateAsync();
        }
        catch (Exception ex)
        {
            ShowOperationError(ex);
        }
    }

    private void ExportApps(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv|JSON 文件 (*.json)|*.json",
            FileName = $"local-ops-apps-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            if (Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_state.Apps,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                var lines = new List<string> { "name,kind,running,port,pid,command,cwd" };
                lines.AddRange(_state.Apps.Select(app => string.Join(",",
                    Csv(app.Name), Csv(app.KindLabel), app.Running, app.Port?.ToString() ?? "",
                    app.Pid?.ToString() ?? "", Csv(app.Command), Csv(app.Cwd ?? ""))));
                File.WriteAllLines(dialog.FileName, lines);
            }
            UpdateStatusText.Text = "项目清单已导出";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    private async void ToggleServiceFlag(object sender, RoutedEventArgs e)
    {
        if (_api is null || ((FrameworkElement)sender).DataContext is not ServiceSnapshot service
            || ((FrameworkElement)sender).Tag is not string flag)
        {
            return;
        }

        var value = flag switch
        {
            "pinned" => !service.Pinned,
            "hidden" => !service.Hidden,
            "promoted" => !service.Promoted,
            _ => false,
        };
        try
        {
            using var result = await _api.PostAsync("api/services/flag",
                new { key = service.InstanceKey.Length > 0 ? service.InstanceKey : service.Key, flag, value },
                _lifetime.Token);
            await RefreshStateAsync();
        }
        catch (Exception ex)
        {
            ShowOperationError(ex);
        }
    }

    private void OpenService(object sender, RoutedEventArgs e)
    {
        if (_demoMode)
        {
            return;
        }
        if (((FrameworkElement)sender).DataContext is not ServiceSnapshot service || service.Port <= 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(service.OpenUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowOperationError(ex, "无法打开服务");
        }
    }

    private async void AddWatchKeyword(object sender, RoutedEventArgs e) => await ChangeWatchKeywordAsync("add");

    private async void WatchKeywordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await ChangeWatchKeywordAsync("add");
            e.Handled = true;
        }
    }

    private async void RemoveWatchKeyword(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string keyword)
        {
            WatchKeywordBox.Text = keyword;
            await ChangeWatchKeywordAsync("remove");
        }
    }

    private async Task ChangeWatchKeywordAsync(string action)
    {
        if (_api is null || string.IsNullOrWhiteSpace(WatchKeywordBox.Text))
        {
            return;
        }

        try
        {
            using var result = await _api.PostAsync("api/watch",
                new { keyword = WatchKeywordBox.Text.Trim(), action }, _lifetime.Token);
            WatchKeywordBox.Clear();
            await RefreshStateAsync();
        }
        catch (Exception ex)
        {
            ShowOperationError(ex);
        }
    }

    private async void OpenAppLog(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppSnapshot app)
        {
            _logTarget = app.Id;
            SetView(LogsView, "日志中心");
            await RefreshLogAsync();
        }
    }

    private void EditApp(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppSnapshot app)
        {
            OpenEditor(app);
        }
    }

    private void AddService(object sender, RoutedEventArgs e) => OpenEditor(null, "service");

    private void AddTask(object sender, RoutedEventArgs e) => OpenEditor(null, "task");

    private void OpenEditor(AppSnapshot? existing, string? newKind = null, string? repairAction = null)
    {
        var dialog = new ProjectEditor(_demoMode ? null : _api, existing, newKind ?? "service", _lifetime.Token)
        {
            Owner = this, Icon = Icon,
        };
        if (repairAction is not null) dialog.Loaded += (_, _) => dialog.FocusRepair(repairAction);
        dialog.ShowDialog();
        if (dialog.Saved)
        {
            _iconCache.Clear();
            _ = RefreshStateAsync();
        }
    }

    private async void RefreshLog(object sender, RoutedEventArgs e) => await RefreshLogAsync();

    private async void LogTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLogSelection)
        {
            return;
        }

        if (LogTargetCombo.SelectedItem is ComboBoxItem item && item.Tag is string target)
        {
            _logTarget = target;
            await RefreshLogAsync();
        }
    }

    private async Task RefreshLogAsync()
    {
        if (_api is null || _logInFlight)
        {
            return;
        }

        _logInFlight = true;
        var target = _logTarget;
        try
        {
            var text = await _api.GetLogAsync(target, _lifetime.Token);
            if (target == "console" && TechnicalLogCheck.IsChecked != true) text = UserLog(text);
            if (target != _logTarget || _quitting) return;
            var changedTarget = _displayedLogTarget != target;
            // Do not replace the text being selected; the next refresh will pick up newer output.
            if (!changedTarget && LogTextBox.SelectionLength > 0) return;
            var offset = LogScroll.VerticalOffset;
            if (LogTextBox.Text != text) LogTextBox.Text = text;
            _displayedLogTarget = target;
            if (FollowLogCheck.IsChecked == true) LogScroll.ScrollToEnd();
            else LogScroll.ScrollToVerticalOffset(changedTarget ? 0 : offset);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"日志读取失败：{ex.Message}";
        }
        finally
        {
            _logInFlight = false;
            if (target != _logTarget && !_quitting) _ = RefreshLogAsync();
        }
    }

    private void CopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogTextBox.Text);
            UpdateStatusText.Text = "日志已复制到剪贴板";
        }
        catch
        {
            UpdateStatusText.Text = "复制失败";
        }
    }

    private void ExportLog(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"local-ops-{_logTarget}-{DateTime.Now:yyyyMMdd-HHmmss}.log",
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                File.WriteAllText(dialog.FileName, LogTextBox.Text);
                UpdateStatusText.Text = "日志已导出";
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = $"导出失败：{ex.Message}";
            }
        }
    }

    private async void CheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_updateInFlight || _demoMode) return;
        _updateInFlight = true;
        _availableUpdate = null;
        ApplyUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查 GitHub…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            _availableUpdate = await _updater.CheckAsync(timeout.Token);
            if (_availableUpdate is null)
            {
                UpdateStatusText.Text = "尚无正式发行版本";
                return;
            }
            var install = _availableUpdate.NeedsInstaller(ReleaseUpdater.CurrentVersion);
            var resources = _availableUpdate.CanUpdateResources(ReleaseUpdater.CurrentVersion, _clientResources.Revision);
            ApplyUpdateLabel.Text = install ? "下载并安装" : "应用资源更新";
            ApplyUpdateButton.IsEnabled = install || resources;
            UpdateStatusText.Text = install ? $"可更新至 v{_availableUpdate.ClientVersion}"
                : resources ? $"资源更新 r{_availableUpdate.Resources.Revision}"
                : $"已是最新版本 v{ReleaseUpdater.CurrentVersion} · r{_clientResources.Revision}";
        }
        catch (OperationCanceledException) when (_quitting) { }
        catch (Exception ex)
        {
            _availableUpdate = null;
            UpdateStatusText.Text = $"检查失败：{ex.Message}";
        }
        finally { _updateInFlight = false; }
    }

    private async void RestartBackend(object sender, RoutedEventArgs e)
    {
        if (_api is null)
        {
            await RefreshStateAsync();
            return;
        }

        if (MessageBox.Show("重启管理后台不会停止已登记项目，是否继续？",
                "重启管理后台", MessageBoxButton.OKCancel, MessageBoxImage.Information)
            != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            using var result = await _api.PostAsync("api/console/restart", new { }, _lifetime.Token);
            UpdateStatusText.Text = "后台重启已排队，正在重新连接…";
            await Task.Delay(800, _lifetime.Token);
            _api?.Dispose();
            _api = null;
            await RefreshStateAsync();
        }
        catch (Exception ex)
        {
            ShowOperationError(ex, "后台重启失败");
        }
    }

    private async void ApplyUpdate(object sender, RoutedEventArgs e)
    {
        var update = _availableUpdate;
        if (_demoMode || _updateInFlight || update is null) return;
        var install = update.NeedsInstaller(ReleaseUpdater.CurrentVersion);
        if (install && MessageBox.Show(this,
            $"下载并安装 v{update.ClientVersion}？校验成功后客户端将退出并重新打开，已托管项目继续运行。",
            "安装更新", MessageBoxButton.OKCancel, MessageBoxImage.Information,
            MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        _updateInFlight = true;
        ApplyUpdateButton.IsEnabled = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            if (install)
            {
                UpdateStatusText.Text = "正在下载安装包…";
                var directory = Path.Combine(ClientStorage.Cache, "updates");
                var path = await _updater.DownloadInstallerAsync(update, directory,
                    new Progress<int>(percent => UpdateStatusText.Text = $"下载安装包 {percent}%"), timeout.Token);
                var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = directory };
                start.ArgumentList.Add("/VERYSILENT");
                start.ArgumentList.Add("/SUPPRESSMSGBOXES");
                start.ArgumentList.Add("/NORESTART");
                start.ArgumentList.Add($"/UPDATEPID={Environment.ProcessId}");
                start.ArgumentList.Add($"/DIR={ClientStorage.Root}");
                start.ArgumentList.Add("/LAUNCHAFTERUPDATE=1");
                start.ArgumentList.Add(IsAutostartEnabled() ? "/MERGETASKS=autostart" : "/MERGETASKS=!autostart");
                Directory.CreateDirectory(ClientStorage.Temporary);
                start.Environment["TEMP"] = ClientStorage.Temporary;
                start.Environment["TMP"] = ClientStorage.Temporary;
                using var installer = Process.Start(start)
                    ?? throw new InvalidOperationException("无法启动安装器。");
                ExitFromButton(this, e);
            }
            else if (update.CanUpdateResources(ReleaseUpdater.CurrentVersion, _clientResources.Revision))
            {
                UpdateStatusText.Text = "正在下载资源…";
                var bytes = await _updater.DownloadResourcesAsync(update.Resources, ReleaseUpdater.CurrentVersion, timeout.Token);
                _clientResources = ClientResources.Save(bytes, ReleaseUpdater.CurrentVersion, ClientResources.CachePath);
                ApplyTheme(SelectedTag(ThemeCombo) ?? "system");
                UpdateStatusText.Text = $"资源 r{_clientResources.Revision} 已生效";
            }
            _availableUpdate = null;
        }
        catch (OperationCanceledException) when (_quitting) { }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"更新失败：{ex.Message}";
            ApplyUpdateButton.IsEnabled = true;
        }
        finally { _updateInFlight = false; }
    }

    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || ThemeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string theme)
        {
            return;
        }

        try
        {
            ApplyTheme(theme);
            if (_demoMode)
            {
                return;
            }
            ClientStorage.Write("Theme", theme);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法保存主题：{ex.Message}", "设置失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyTheme(string theme)
    {
        if (theme == "system")
        {
            using var personalization = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            theme = personalization?.GetValue("AppsUseLightTheme") is int light && light == 0 ? "dark" : "light";
        }
        var colors = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
            ? new Dictionary<string, string>
            {
                ["CanvasBrush"] = "#1C1C1E", ["SurfaceBrush"] = "#2C2C2E",
                ["SurfaceRaisedBrush"] = "#3A3A3C", ["SurfaceHoverBrush"] = "#444446",
                ["GlassBrush"] = "#1C1C1E", ["GlassStrongBrush"] = "#2C2C2E",
                ["PopupBrush"] = "#3A3A3C", ["TrackBrush"] = "#525256",
                ["GlassHoverBrush"] = "#444446", ["GlassPressedBrush"] = "#242426",
                ["InsetBrush"] = "#2C2C2E", ["InputBrush"] = "#3A3A3C",
                ["SoftBorderBrush"] = "#505054", ["AccentStrongBrush"] = "#A0DECD",
                ["InfoSoftBrush"] = "#243C57",
                ["BorderBrush"] = "#646468", ["TextBrush"] = "#F5F5F7",
                ["MutedTextBrush"] = "#D0D0D6", ["DimTextBrush"] = "#B8B8C1",
                ["AccentBrush"] = "#A0DECD", ["AccentSoftBrush"] = "#233C36",
                ["AccentFillBrush"] = "#176452", ["ButtonHighlightBrush"] = "#386E6E73",
                ["DisabledTextBrush"] = "#B8B8C1", ["DisabledSurfaceBrush"] = "#323234",
                ["WarningBrush"] = "#FFD480", ["WarningSoftBrush"] = "#4C3B20",
                ["DangerBrush"] = "#FFA8AF", ["DangerSoftBrush"] = "#4D292E",
                ["InfoBrush"] = "#AFD4FF",
            }
            : new Dictionary<string, string>
            {
                ["CanvasBrush"] = "#F2F2F5", ["SurfaceBrush"] = "#FFFFFF",
                ["SurfaceRaisedBrush"] = "#FFFFFF", ["SurfaceHoverBrush"] = "#E9E9EF",
                ["GlassBrush"] = "#F2F2F5", ["GlassStrongBrush"] = "#FFFFFF",
                ["PopupBrush"] = "#FFFFFF", ["TrackBrush"] = "#D5D5DC",
                ["GlassHoverBrush"] = "#E9E9EF", ["GlassPressedBrush"] = "#DCDCE4",
                ["InsetBrush"] = "#FFFFFF", ["InputBrush"] = "#FFFFFF",
                ["SoftBorderBrush"] = "#D5D5DD", ["AccentStrongBrush"] = "#125C50",
                ["InfoSoftBrush"] = "#E0EDFF",
                ["BorderBrush"] = "#B8B8C2", ["TextBrush"] = "#202023",
                ["MutedTextBrush"] = "#50505B", ["DimTextBrush"] = "#60606C",
                ["AccentBrush"] = "#125C50", ["AccentSoftBrush"] = "#DFEEEA",
                ["AccentFillBrush"] = "#176452", ["ButtonHighlightBrush"] = "#D9FFFFFF",
                ["DisabledTextBrush"] = "#62626E", ["DisabledSurfaceBrush"] = "#E8E8EE",
                ["WarningBrush"] = "#80500A", ["WarningSoftBrush"] = "#FCEBCF",
                ["DangerBrush"] = "#A72B3B", ["DangerSoftBrush"] = "#F8DEE3",
                ["InfoBrush"] = "#075DBB",
            };

        foreach (var (key, value) in theme == "dark" ? _clientResources.Dark : _clientResources.Light)
            colors[key] = value;
        foreach (var (key, value) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            Resources[key] = brush;
            System.Windows.Application.Current.Resources[key] = brush;
        }
        UpdateNativeTheme(string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase));
    }

    private void AutostartChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || !IsInstalledClient())
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            if (AutostartCheck.IsChecked == true)
            {
                var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    throw new InvalidOperationException("无法确定客户端可执行文件路径。");
                }
                key?.SetValue("LocalOpsWindows", $"\"{executable}\" --background");
            }
            else
            {
                key?.DeleteValue("LocalOpsWindows", false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"开机启动设置失败：{ex.Message}", "设置失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_loaded)
        {
            RefreshCollections();
        }
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded)
        {
            RefreshCollections();
        }
    }

    private void OnAppSelected(object sender, SelectionChangedEventArgs e)
    {
        UpdateLaunchMonitor();
        if (AppsList.SelectedItem is AppSnapshot app)
        {
            ActivityText.Text = $"{app.Name}\n{app.StatusLabel} · {app.PortLabel}\n" +
                                $"{app.PidLabel} · {app.UptimeLabel}\n{app.Cwd ?? "未设置工作目录"}";
        }
    }

    private void OnServiceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ServicesGrid.SelectedItem is not ServiceSnapshot service)
        {
            ServiceDetailContent.DataContext = null;
            SelectedServiceName.Text = "没有匹配的服务";
            SelectedServiceMeta.Text = "调整分组或搜索条件";
            SelectedServicePath.Text = "—";
            SelectedServiceCpu.Text = "—";
            SelectedServiceMem.Text = "—";
            ServiceTrendCpu.Points = [];
            ServiceTrendMem.Points = [];
            ServiceUptimeText.Text = "";
            UpdateServiceDetailHeader();
            return;
        }

        ServiceDetailContent.DataContext = service;
        SelectedServiceName.Text = service.DisplayName;
        SelectedServiceMeta.Text = $":{service.Port} · PID {service.Pid} · {service.OriginLabel}";
        SelectedServicePath.Text = $"{service.Command ?? "未提供命令"}\n{service.Cwd ?? "未提供目录"}";
        SelectedServiceCpu.Text = $"{service.Cpu:0.0}%";
        SelectedServiceMem.Text = $"{service.Mem:0.0}%";
        _monitor.History.TryGetValue(MonitorSession.Key(service), out var samples);
        ServiceTrendCpu.Points = TrendPoints(samples?.Select(s => s.Cpu));
        ServiceTrendMem.Points = TrendPoints(samples?.Select(s => s.Mem));
        ServiceUptimeText.Text = $"运行时长 {AppSnapshot.FormatDuration(service.UptimeSec)}";
        UpdateServiceDetailHeader();
    }

    private void UpdateServiceDetailHeader()
    {
        ServiceDetailExpander.Header = _compactLayout == true
            && ServicesGrid.SelectedItem is ServiceSnapshot service
                ? $"服务详情 · {service.DisplayName}" : "服务详情";
    }

    private void ApplyWindowBackdrop(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = ((SolidColorBrush)FindResource("CanvasBrush")).Color;
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            var corners = 2;
            NativeBackdrop.DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
        }
    }

    private void UpdateNativeTheme(bool dark)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }
        var enabled = dark ? 1 : 0;
        NativeBackdrop.DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void ToggleMaximizeWindow(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private static DashboardState CreateDemoState()
    {
        return new DashboardState
        {
            Version = "2.1.0-preview",
            ConsolePort = 9600,
            ConsoleCwd = @"C:\Projects\LocalOps",
            WatchedKeywords = ["node", "worker", "python"],
            Apps =
            [
                new() { Id = "demo-api", Name = "Atlas API", Command = "dotnet run --project Atlas.Api",
                    Cwd = @"C:\Projects\Atlas", Port = 5180, Running = true, Pid = 2408, UptimeSec = 12840 },
                new() { Id = "demo-web", Name = "Studio Web", Command = "npm run dev",
                    Cwd = @"C:\Projects\Studio", Port = 5173, Running = true, Pid = 6312, UptimeSec = 3740 },
                new() { Id = "demo-cache", Name = "Cache Worker", Command = "python worker.py",
                    Cwd = @"C:\Projects\Cache", Port = 8787, Running = false },
                new() { Id = "demo-backup", Name = "每日备份", Command = "powershell -File backup.ps1",
                    Cwd = @"C:\Projects\Ops", Kind = "task", Running = false,
                    LastExit = new() { Status = "succeeded", DurationSec = 42 } },
                new() { Id = "demo-index", Name = "文档索引", Command = "python index.py",
                    Cwd = @"C:\Projects\Docs", Kind = "task", Running = false,
                    LastExit = new() { Status = "failed", DurationSec = 8 } },
                new() { Id = "demo-preview", Name = "Design Preview", Command = "npm run preview",
                    Cwd = @"C:\Projects\Design", Port = 4173, PortOccupied = true, PortOccupiedPid = 4250 },
            ],
            Services =
            [
                new() { Key = "atlas", InstanceKey = "atlas-2408", Name = "dotnet", AppName = "Atlas API",
                    Command = "dotnet run --project Atlas.Api", Cwd = @"C:\Projects\Atlas", Port = 5180,
                    Pid = 2408, Cpu = 2.8, Mem = 1.4, Group = "mine", AppId = "demo-api", Pinned = true },
                new() { Key = "studio", InstanceKey = "studio-6312", Name = "node", AppName = "Studio Web",
                    Command = "node vite --host 127.0.0.1", Cwd = @"C:\Projects\Studio", Port = 5173,
                    Pid = 6312, Cpu = 1.3, Mem = 0.8, Group = "mine", AppId = "demo-web" },
                new() { Key = "gateway", InstanceKey = "gateway-7488", Name = "Gateway",
                    Command = "gateway.exe --local", Cwd = @"C:\Projects\Gateway", Port = 8088,
                    Pid = 7488, Cpu = 0.4, Mem = 0.6, Group = "background" },
                new() { Key = "preview", InstanceKey = "preview-4250", Name = "Design Preview",
                    Command = "node preview.js", Cwd = @"C:\Projects\Design", Port = 4173,
                    Pid = 4250, Cpu = 0.1, Mem = 0.3, Group = "background" },
            ],
            Watched =
            [
                new() { Pid = 6312, Name = "node.exe", Keyword = "node", Cpu = 1.3, Mem = 0.8 },
                new() { Pid = 7804, Name = "worker.exe", Keyword = "worker", Cpu = 0.6, Mem = 0.4 },
                new() { Pid = 8516, Name = "python.exe", Keyword = "python", Cpu = 0.2, Mem = 0.3 },
            ],
        };
    }

    private static class NativeBackdrop
    {
        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(
            IntPtr window, int attribute, ref int value, int size);

    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (LaunchDetailColumn is null || ServiceDetailColumn is null)
        {
            return;
        }

        var compact = (ActualWidth > 0 ? ActualWidth : Width) < 1250
            || (ActualHeight > 0 ? ActualHeight : Height) < 740;
        LaunchDetailColumn.Width = new GridLength(compact ? 0 : 290);
        LaunchDetailGap.Width = new GridLength(compact ? 0 : 16);
        LaunchDetailPanel.Visibility = Visibility.Visible;
        Grid.SetColumn(LaunchDetailPanel, compact ? 0 : 2);
        Grid.SetRow(LaunchDetailPanel, compact ? 0 : 1);
        LaunchDetailPanel.Padding = compact ? new Thickness(0, 0, 0, 10) : new Thickness(16, 0, 0, 0);
        LaunchDetailPanel.BorderThickness = compact ? new Thickness(0, 0, 0, 1) : new Thickness(1, 0, 0, 0);
        SidebarColumn.Width = new GridLength(76);
        if (_launchCompact != compact) { _launchCompact = compact; UpdateLaunchMonitor(); }
        ServiceDetailColumn.Width = new GridLength(compact ? 0 : 280);
        ServiceDetailGap.Width = new GridLength(compact ? 0 : 14);
        Grid.SetColumn(ServiceDetailPanel, compact ? 0 : 2);
        Grid.SetRow(ServiceDetailPanel, compact ? 1 : 0);
        ServiceDetailPanel.Margin = new Thickness(0, compact ? 10 : 0, 0, 0);
        ServiceDetailScroll.MaxHeight = compact ? 170 : double.PositiveInfinity;
        if (_compactLayout != compact)
        {
            _compactLayout = compact;
            ServiceDetailExpander.IsExpanded = !compact;
            UpdateServiceDetailHeader();
        }
    }

    private void OpenAppMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not AppSnapshot app)
        {
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var (label, glyph, handler) in new (string, string, RoutedEventHandler)[]
        {
            ("打开服务", "\uE8A7", OpenApp), ("复制链接", "\uE8B4", CopyAppLink),
            ("抓取站点图标", "\uE91B", FetchAppIcon),
            ("重启", "\uE72C", RestartApp), ("诊断", "\uE946", DiagnoseApp),
            ("编辑", "\uE70F", EditApp), ("删除", "\uE74D", DeleteApp),
        })
        {
            var item = new MenuItem
            {
                Header = label, DataContext = app,
                Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13 },
            };
            if (handler == OpenApp || handler == CopyAppLink || handler == FetchAppIcon)
                item.IsEnabled = app.CanOpen;
            if (handler == RestartApp) item.IsEnabled = app.Running && app.Kind != "task";
            item.Click += handler;
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void OpenApp(object sender, RoutedEventArgs e)
    {
        if (_demoMode || ((FrameworkElement)sender).DataContext is not AppSnapshot { CanOpen: true } app) return;
        try { Process.Start(new ProcessStartInfo(app.OpenUrl!) { UseShellExecute = true }); }
        catch (Exception ex) { ShowOperationError(ex); }
    }

    private void CopyAppLink(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppSnapshot { CanOpen: true } app) return;
        try { Clipboard.SetText(app.OpenUrl!); }
        catch (Exception ex) { ShowOperationError(ex); }
    }

    private async void FetchAppIcon(object sender, RoutedEventArgs e)
    {
        if (_api is null || ((FrameworkElement)sender).DataContext is not AppSnapshot { CanOpen: true } app) return;
        try
        {
            using var result = await _api.PostAsync($"api/apps/{app.Id}/favicon", new { }, _lifetime.Token);
            _iconCache.Clear();
            await RefreshStateAsync();
        }
        catch (Exception ex) { ShowOperationError(ex); }
    }

    private void RegisterService(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ServiceSnapshot service) return;
        var registered = _state.Apps.FirstOrDefault(app => app.Id == service.AppId);
        OpenEditor(registered ?? new AppSnapshot
        {
            Name = service.DisplayName, Command = service.Command ?? "", Cwd = service.Cwd, Port = service.Port,
        });
    }

    private async Task LoadProjectIconsAsync(DashboardState state)
    {
        if (_api is null || !IsMonitoringVisible) return;
        var paths = state.Apps.Select(a => a.Icon ?? (a.Glyph is null ? a.Favicon : null))
            .Where(p => p is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _iconCache.Keys.Where(p => !paths.Contains(p)).ToArray()) _iconCache.Remove(stale);
        foreach (var app in state.Apps)
        {
            var path = app.Icon ?? (app.Glyph is null ? app.Favicon : null);
            if (path is null) continue;
            if (!_iconCache.TryGetValue(path, out var image))
            {
                try { image = ProjectIcons.Decode(await _api.GetIconAsync(path, _lifetime.Token)); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                catch { image = null; }
                _iconCache[path] = image;
            }
            app.IconImage = image;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        if (_tray is { Visible: true } && _closeBehavior == "tray")
        {
            e.Cancel = true;
            SaveWindowPosition();
            Hide();
        }
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void CloseBehaviorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || SelectedTag(CloseBehaviorCombo) is not ("tray" or "exit")
            || SelectedTag(CloseBehaviorCombo) == _closeBehavior) return;
        var behavior = SelectedTag(CloseBehaviorCombo)!;
        try
        {
            if (!_demoMode) ClientStorage.Write("CloseBehavior", behavior);
            _closeBehavior = behavior;
        }
        catch (Exception ex)
        {
            SelectComboTag(CloseBehaviorCombo, _closeBehavior);
            ShowOperationError(ex, "无法保存窗口设置");
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is not null)
        {
            var maximized = WindowState == WindowState.Maximized;
            MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
            System.Windows.Automation.AutomationProperties.SetName(
                MaximizeButton, maximized ? "还原窗口" : "最大化窗口");
            WindowFrame.CornerRadius = new CornerRadius(maximized ? 0 : 10);
        }
        if (_tray is not null && WindowState == WindowState.Minimized
            && MinimizeToTrayCheck.IsChecked == true)
        {
            Hide();
        }
        UpdatePollingVisibility();
    }

    private void WindowBehaviorChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _demoMode)
        {
            return;
        }

        try
        {
            ClientStorage.Write("MinimizeToTray", MinimizeToTrayCheck.IsChecked == true ? "true" : "false");
        }
        catch (Exception ex)
        {
            ShowOperationError(ex, "无法保存窗口设置");
        }
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenCommandPalette(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenEditor(null, "task");
            e.Handled = true;
        }
        else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowLogs(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            RefreshNow(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenEditor(null, "service");
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down && Keyboard.Modifiers == ModifierKeys.Alt && AppsList.IsKeyboardFocusWithin)
        {
            await MoveSelectedAppAsync(e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
        }
    }

    private void ExitFromButton(object? sender, RoutedEventArgs? e)
    {
        _quitting = true;
        _pollTimer.Stop();
        Close();
    }

    protected override async void OnClosed(EventArgs e)
    {
        _quitting = true;
        _lifetime.Cancel();
        _pollTimer.Stop();
        _notificationTimer.Stop();
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        SaveWindowPosition();
        _tray?.Dispose();
        _trayAppIcon?.Dispose();
        _api?.Dispose();
        _updater.Dispose();
        await _backend.DisposeAsync();
        _lifetime.Dispose();
        base.OnClosed(e);
        System.Windows.Application.Current.Shutdown();
    }

    private void SetBackendStatus(string status, string metadata, string brushKey)
    {
        BackendStatusText.Text = status;
        BackendStatusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        BackendMetaText.Text = metadata;
    }

    private string? ReadSetting(string name)
    {
        if (_demoMode)
        {
            return null;
        }
        return ClientStorage.Read(name);
    }

    private bool IsAutostartEnabled()
    {
        if (!IsInstalledClient())
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run");
        var value = key?.GetValue("LocalOpsWindows") as string;
        var executable = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(value)
            && !string.IsNullOrWhiteSpace(executable)
            && value.Contains(executable, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInstalledClient()
    {
        return ClientStorage.Installed;
    }

    private static void SelectComboTag(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
    }

    private static string? SelectedTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static string Csv(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private static void ShowOperationError(Exception ex, string title = "操作失败")
    {
        MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
