using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace LocalOpsWindows.Client;

internal sealed class DiagnosticReport
{
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("issues")] public List<DiagnosticIssue> Issues { get; set; } = [];
}

internal sealed class DiagnosticIssue
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
    [JsonPropertyName("fix")] public string Fix { get; set; } = "";
    [JsonPropertyName("action")] public string? Action { get; set; }
}

internal sealed class DiagnosticWindow : Window
{
    internal string? RequestedAction { get; private set; }

    internal DiagnosticWindow(AppSnapshot app, DiagnosticReport report)
    {
        Title = $"诊断 · {app.Name}";
        Width = 640;
        Height = Math.Min(640, SystemParameters.WorkArea.Height - 40);
        MinWidth = 480;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(BackgroundProperty, "PopupBrush");
        var root = new DockPanel { Margin = new Thickness(24) };
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,16,0,0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        AddButton(footer, "打开日志", "logs");
        AddButton(footer, "编辑项目", "edit");
        var close = new Button { Content = "关闭", IsCancel = true, Style = (Style)FindResource("ToolbarButton") };
        footer.Children.Add(close);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = app.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        AddText(content, $"{app.StatusLabel} · {app.UptimeLabel}");
        AddText(content, report.Summary);
        foreach (var issue in report.Issues)
        {
            var section = new StackPanel { Margin = new Thickness(0,18,0,0) };
            section.Children.Add(new TextBlock { Text = issue.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            AddText(section, issue.Detail);
            AddText(section, $"修复建议：{issue.Fix}");
            if (issue.Action is "pick-script" or "pick-cwd" or "edit-command")
                AddButton(section, issue.Action == "pick-script" ? "重新选择脚本"
                    : issue.Action == "pick-cwd" ? "重新选择目录" : "编辑命令", issue.Action);
            content.Children.Add(section);
        }
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private void AddButton(Panel parent, string label, string action)
    {
        var button = new Button { Content = label, Style = (Style)FindResource("ToolbarButton"),
            HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => { RequestedAction = action; DialogResult = true; };
        parent.Children.Add(button);
    }

    private static void AddText(Panel parent, string text) =>
        parent.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0) });
}
