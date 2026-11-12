using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace LocalOpsWindows.Client;

public partial class MainWindow
{
    private void OpenCommandPalette(object sender, RoutedEventArgs e)
    {
        var commands = new List<PaletteCommand>
        {
            new("启动台", "", () => ShowLaunchpad(this, e)),
            new("服务监控", "", () => ShowServices(this, e)),
            new("日志中心", "", () => ShowLogs(this, e)),
            new("设置", "", () => ShowSettings(this, e)),
            new("添加服务", "", () => OpenEditor(null, "service")),
            new("添加任务", "", () => OpenEditor(null, "task")),
            new("刷新状态", "", () => RefreshNow(this, e)),
        };
        foreach (var app in _state.Apps)
        {
            commands.Add(new($"编辑 · {app.Name}", app.Command, () => OpenEditor(app)));
            commands.Add(new($"日志 · {app.Name}", app.Command, () =>
            {
                _logTarget = app.Id;
                ShowLogs(this, e);
            }));
            commands.Add(new($"{app.ToggleLabel} · {app.Name}", app.Command, () =>
                ToggleApp(new Button { DataContext = app }, e)));
        }
        new CommandPalette(commands) { Owner = this, Icon = Icon }.ShowDialog();
    }

    private void MonitorTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && ReferenceEquals(e.Source, MonitorTabs)) RefreshCollections();
    }

    private void ClearEvents(object sender, RoutedEventArgs e)
    {
        _monitor.Events.Clear();
        RefreshCollections();
    }

    private void DismissDiscovery(object sender, RoutedEventArgs e)
    {
        if (ServicesGrid.SelectedItem is ServiceSnapshot service) _monitor.Dismiss(service);
        RefreshCollections();
    }

    private void CopyServiceLink(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ServiceSnapshot service) return;
        try { Clipboard.SetText(service.OpenUrl); }
        catch (Exception ex) { ShowOperationError(ex); }
    }

    internal static PointCollection TrendPoints(IEnumerable<double>? values)
    {
        var samples = values?.TakeLast(120).ToArray() ?? [];
        if (samples.Length < 2) return [];
        return new PointCollection(samples.Select((value, index) =>
            new Point(index * 240.0 / (samples.Length - 1),
                40 - Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100) * .4)));
    }

    private void TaskNotificationsChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _demoMode) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key?.SetValue("TaskNotifications", TaskNotifyCheck.IsChecked == true ? "true" : "false");
            _notificationTimer.Stop();
            if (TaskNotifyCheck.IsChecked == true && !IsMonitoringVisible && !_backendStopped)
                _notificationTimer.Start();
        }
        catch (Exception ex) { ShowOperationError(ex, "无法保存通知设置"); }
    }

    private async Task CheckTaskNotificationsAsync()
    {
        if (_notificationInFlight || _quitting || _api is null || _demoMode
            || IsMonitoringVisible || TaskNotifyCheck.IsChecked != true || _backendStopped) return;
        _notificationInFlight = true;
        try
        {
            var snapshot = await _api.GetAsync<DashboardState>("api/tasks/status", _lifetime.Token);
            if (snapshot is null) return;
            var completed = _taskCompletions.Observe(snapshot.Apps);
            if (completed.Count == 0) return;
            var body = string.Join("\n", completed.Take(4).Select(a => $"{a.Name} · {a.StatusLabel} · {a.UptimeLabel}"));
            _tray?.ShowBalloonTip(5000, completed.Count == 1 ? "任务结束" : $"{completed.Count} 个任务结束",
                body, completed.Any(a => a.LastExit?.EffectiveStatus == "failed") ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            _taskCompletions.Reset();
            // Older backends lack the compact endpoint; never fall back to full scans while hidden.
            _notificationTimer.Stop();
            SetBackendStatus("通知暂不可用", "恢复窗口后重新连接后台", "WarningBrush");
        }
        finally { _notificationInFlight = false; }
    }

    private async void StopBackend(object sender, RoutedEventArgs e)
    {
        if (_api is null || _demoMode || MessageBox.Show(this,
            "停止管理后台？已托管项目继续运行，客户端停止刷新；点击刷新可重新连接。",
            "停止管理后台", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try
        {
            using var result = await _api.PostAsync("api/console/stop", new { }, _lifetime.Token);
            _backendStopped = true;
            _pollTimer.Stop();
            _notificationTimer.Stop();
            _api.Dispose();
            _api = null;
            _monitor.ResetBaseline();
            _taskCompletions.Reset();
            SetBackendStatus("后台已停止", "托管项目不受影响", "MutedTextBrush");
        }
        catch (Exception ex) { ShowOperationError(ex, "停止后台失败"); }
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_quitting) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_quitting && SelectedTag(ThemeCombo) == "system") ApplyTheme("system");
        }));
    }

    private void RestoreWindowPosition()
    {
        if (_demoMode) return;
        try
        {
            if (ReadSetting("WindowBounds") is not { } saved) return;
            var bounds = JsonSerializer.Deserialize<double[]>(saved);
            if (bounds is not { Length: 5 } || bounds.Any(x => !double.IsFinite(x))) return;
            Width = Math.Clamp(bounds[2], MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
            Height = Math.Clamp(bounds[3], MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var visible = Rect.Intersect(desktop, new Rect(bounds[0], bounds[1], Width, Height));
            if (visible.Width >= 120 && visible.Height >= 80)
            {
                Left = bounds[0];
                Top = bounds[1];
            }
            if (bounds[4] == 1) WindowState = WindowState.Maximized;
        }
        catch { /* Invalid preferences must not prevent startup. */ }
    }

    private void SaveWindowPosition()
    {
        if (_demoMode) return;
        try
        {
            var bounds = RestoreBounds;
            if (bounds.IsEmpty) return;
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key?.SetValue("WindowBounds", JsonSerializer.Serialize(new[]
            {
                bounds.X, bounds.Y, bounds.Width, bounds.Height, WindowState == WindowState.Maximized ? 1.0 : 0.0,
            }));
        }
        catch { /* Window geometry is optional; closing must remain reliable. */ }
    }
}
