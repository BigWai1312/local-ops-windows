using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LocalOpsWindows.Client;

internal static class PortConflictDialog
{
    internal const string HelperSwitch = "--stop-port-process";

    internal static Window Create(PortProcess target, string project, Window? owner = null)
    {
        var dialog = new Window
        {
            Title = "处理端口占用", Owner = owner, Width = 600, SizeToContent = SizeToContent.Height,
            MaxHeight = 640, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.FindResource("PopupBrush"),
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        void Text(string text, int size = 14, bool bold = false)
            => panel.Children.Add(new TextBlock { Text = text, FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        Text($"端口 {target.Port} 正被外部进程占用", 19, true);
        Text(project);
        Text($"进程：{Path.GetFileName(target.ImagePath)}\nPID：{target.Pid}\n启动时间：{DateTime.FromFileTimeUtc(target.Created).ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        panel.Children.Add(new TextBox { Text = target.ImagePath, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.FindResource("TextInput"), MinHeight = 58,
            MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 12) });
        Text("强制停止会立即中断该进程的全部工作，未保存的数据可能丢失。它可能同时提供其他端口的服务；本次不会结束其他进程或自动重启项目。");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, IsDefault = true,
            Style = (Style)Application.Current.FindResource("ToolbarButton") };
        var stop = new Button { Content = "强制停止此进程", Style = (Style)Application.Current.FindResource("DangerButton") };
        stop.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(stop);
        panel.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return dialog;
    }

    internal static async Task<bool> ElevateAsync(int pid, int port, long created = 0)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定客户端路径。"))
        {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var arg in new[] { HelperSwitch, pid.ToString(CultureInfo.InvariantCulture),
            port.ToString(CultureInfo.InvariantCulture), created.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(arg);
        try
        {
            using var helper = Process.Start(start) ?? throw new InvalidOperationException("未能启动管理员操作窗口。");
            await helper.WaitForExitAsync();
            if (helper.ExitCode == 0) return true;
            if (helper.ExitCode == 2) return false;
            throw new InvalidOperationException("管理员操作未完成，请查看操作窗口中的错误并刷新状态。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }
    }

    internal static async Task<int> RunHelperAsync(string[] args)
    {
        try
        {
            if (args.Length != 4 || args[0] != HelperSwitch
                || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var created) || created < 0)
                return 1;
            var target = await Task.Run(() => PortProcess.Inspect(port, pid, created == 0 ? null : created));
            // The elevated helper always shows the verified target; command-line arguments never authorize a silent kill.
            if (Create(target, "管理员操作 · 请再次核对目标进程").ShowDialog() != true) return 2;
            await Task.Run(target.Stop);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "无法强制停止", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
    }
}
