using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LocalOpsWindows.Client;

public partial class ProjectEditor : Window
{
    private readonly ApiClient? _api;
    private readonly CancellationTokenSource _closed;
    private string? _savedId;
    private string? _glyph;
    private string? _storedIcon;
    private byte[]? _pendingIcon;
    private bool _removeIcon;
    private bool _busy;
    private int _detectSequence;
    private string? _autoPort;
    public bool Saved { get; private set; }

    internal ProjectEditor(ApiClient? api, AppSnapshot? app, string kind, CancellationToken lifetime)
    {
        _api = api;
        _closed = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        InitializeComponent();
        Height = Math.Min(770, SystemParameters.WorkArea.Height - 40);
        Title = Heading.Text = string.IsNullOrEmpty(app?.Id) ? "添加项目" : $"编辑 · {app.Name}";
        _savedId = string.IsNullOrEmpty(app?.Id) ? null : app.Id;
        _glyph = app?.Glyph;
        _storedIcon = app?.Icon;
        NameInput.Text = app?.Name ?? "";
        CwdInput.Text = app?.Cwd ?? "";
        CommandInput.Text = app?.Command ?? "";
        PortInput.Text = app?.Port?.ToString() ?? "";
        TaskMode.IsChecked = (app?.Kind ?? kind) == "task";
        ServiceMode.IsChecked = TaskMode.IsChecked != true;
        if (app?.Running == true)
            StopButton.Visibility = RunningNotice.Visibility = Visibility.Visible;
        foreach (var (name, label, code) in ProjectIcons.Choices)
        {
            var button = new Button
            {
                Content = code, Tag = name, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Style = (Style)FindResource("IconButton"), ToolTip = label,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"选择图标 {label}");
            button.Click += (_, _) =>
            {
                _glyph = name;
                _pendingIcon = null;
                _removeIcon = _storedIcon is not null;
                RenderIcon();
            };
            GlyphChoices.Children.Add(button);
        }
        IconPreview.Source = app?.IconImage;
        RenderIcon(keepImage: true);
        Closed += (_, _) => { _closed.Cancel(); _closed.Dispose(); };
    }

    private void KindChanged(object sender, RoutedEventArgs e)
    {
        if (PortField is not null)
            PortField.Visibility = TaskMode.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void PickFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择工作目录" };
        if (picker.ShowDialog(this) != true) return;
        CwdInput.Text = picker.FolderName;
        await DetectAsync();
    }

    internal void FocusRepair(string? action)
    {
        if (action == "pick-script") PickScript(this, new RoutedEventArgs());
        else if (action == "pick-cwd") PickFolder(this, new RoutedEventArgs());
        else { CommandInput.Focus(); CommandInput.SelectAll(); }
    }

    private async void PickScript(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        var picker = new OpenFileDialog
        {
            Title = "选择脚本",
            Filter = "脚本或程序|*.py;*.ps1;*.bat;*.cmd;*.sh;*.bash;*.zsh;*.exe;*.com|所有文件|*.*",
        };
        if (picker.ShowDialog(this) != true) return;
        await PerformAsync(async () =>
        {
            using var result = await _api.PostAsync("api/pick", new { what = "script", path = picker.FileName }, _closed.Token);
            CommandInput.Text = result?.RootElement.GetProperty("command").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(CwdInput.Text)) CwdInput.Text = Path.GetDirectoryName(picker.FileName) ?? "";
            if (string.IsNullOrWhiteSpace(NameInput.Text)) NameInput.Text = Path.GetFileNameWithoutExtension(picker.FileName);
            CandidatesList.SelectedIndex = -1;
        });
    }

    private void DirectoryChanged(object sender, TextChangedEventArgs e)
    {
        _detectSequence++;
        if (CandidatesList is null) return;
        CandidatesList.ItemsSource = null;
        DetectionText.Text = "";
        _autoPort = null;
    }

    private async void DetectProject(object sender, RoutedEventArgs e) => await DetectAsync();

    private async Task DetectAsync()
    {
        if (_api is null) return;
        var cwd = CwdInput.Text.Trim();
        if (cwd.Length == 0) { ErrorText.Text = "请先选择工作目录。"; return; }
        var sequence = ++_detectSequence;
        await PerformAsync(async () =>
        {
            using var result = await _api.PostAsync("api/project/detect", new { cwd }, _closed.Token);
            if (result is null || sequence != _detectSequence) return;
            var detection = result.RootElement.Deserialize<ProjectDetection>() ?? new();
            CandidatesList.ItemsSource = detection.Candidates;
            DetectionText.Text = detection.Candidates.Count == 0 ? "没有识别到候选，可选择脚本或手动填写。"
                : $"找到 {detection.Candidates.Count} 个候选 · {string.Join("、", detection.Files)}";
            if (string.IsNullOrWhiteSpace(NameInput.Text)) NameInput.Text = detection.Name ?? "";
        });
    }

    private void CandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CandidatesList.SelectedItem is not ProjectCandidate candidate) return;
        CommandInput.Text = candidate.Command;
        TaskMode.IsChecked = candidate.Kind == "task";
        ServiceMode.IsChecked = candidate.Kind != "task";
        if (PortInput.Text.Trim().Length == 0 || PortInput.Text.Trim() == _autoPort)
        {
            PortInput.Text = candidate.Port?.ToString() ?? "";
            _autoPort = PortInput.Text;
        }
    }

    private void RenderIcon(bool keepImage = false)
    {
        IconGlyph.Text = ProjectIcons.Glyph(_glyph);
        if (!keepImage) IconPreview.Source = _pendingIcon is null ? null : ProjectIcons.Decode(_pendingIcon);
        IconGlyph.Visibility = IconPreview.Source is null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in GlyphChoices.Children.OfType<Button>())
            button.SetResourceReference(BackgroundProperty, (string)button.Tag == _glyph ? "AccentSoftBrush" : "InputBrush");
    }

    private void SetImage(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        if (output.Length > 5 * 1024 * 1024) throw new InvalidOperationException("图标不能超过 5MB。");
        _pendingIcon = output.ToArray();
        _glyph = null;
        _removeIcon = false;
        RenderIcon();
    }

    private void UploadImage(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择项目图标", Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.ico" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 5 * 1024 * 1024)
                throw new InvalidOperationException("图标不能超过 5MB。");
            SetImage(ProjectIcons.Decode(File.ReadAllBytes(picker.FileName)));
            ErrorText.Text = "";
        }
        catch (Exception ex) { ErrorText.Text = $"无法读取图标：{ex.Message}"; }
    }

    private void PasteImage(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.GetImage() is { } image) { SetImage(image); ErrorText.Text = ""; }
            else ErrorText.Text = "剪贴板中没有图片。";
        }
        catch (Exception ex) { ErrorText.Text = $"无法粘贴图片：{ex.Message}"; }
    }

    private void ClearImage(object sender, RoutedEventArgs e)
    {
        _glyph = null;
        _pendingIcon = null;
        _removeIcon = _storedIcon is not null;
        RenderIcon();
    }

    private async void StopProject(object sender, RoutedEventArgs e)
    {
        if (_api is null || _savedId is null || MessageBox.Show(this,
            $"停止“{NameInput.Text}”？未保存的编辑内容会保留。",
            "停止项目", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await PerformAsync(async () =>
        {
            using var result = await _api.PostAsync($"api/apps/{_savedId}/stop", new { }, _closed.Token);
            StopButton.Visibility = RunningNotice.Visibility = Visibility.Collapsed;
        });
    }

    private async void SaveProject(object sender, RoutedEventArgs e)
    {
        if (_api is null) { ErrorText.Text = "演示模式不会保存项目。"; return; }
        await PerformAsync(async () =>
        {
            await SaveAsync();
            if (IsVisible) DialogResult = true;
        });
    }

    internal async Task SaveAsync()
    {
        if (_api is null) throw new InvalidOperationException("后台未连接。");
        if (string.IsNullOrWhiteSpace(NameInput.Text) || string.IsNullOrWhiteSpace(CommandInput.Text))
            throw new InvalidOperationException("名称和执行命令不能为空。");
        int? port = null;
        if (ServiceMode.IsChecked == true && !string.IsNullOrWhiteSpace(PortInput.Text))
        {
            if (!int.TryParse(PortInput.Text, out var value) || value is < 1 or > 65535)
                throw new InvalidOperationException("端口必须是 1–65535 的整数。");
            port = value;
        }
        var payload = new
        {
            name = NameInput.Text.Trim(), command = CommandInput.Text.Trim(),
            cwd = string.IsNullOrWhiteSpace(CwdInput.Text) ? null : CwdInput.Text.Trim(),
            port, kind = TaskMode.IsChecked == true ? "task" : "service", glyph = _glyph,
        };
        using var result = _savedId is null
            ? await _api.PostAsync("api/apps", payload, _closed.Token)
            : await _api.PutAsync($"api/apps/{_savedId}", payload, _closed.Token);
        // Keep the created id before icon I/O so a failed upload cannot create a second card.
        _savedId ??= result?.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("后台未返回项目 ID。");
        Saved = true;
        if (_pendingIcon is not null)
        {
            await _api.UploadIconAsync(_savedId, _pendingIcon, _closed.Token);
            _pendingIcon = null;
        }
        else if (_removeIcon)
        {
            using var removed = await _api.DeleteAsync($"api/apps/{_savedId}/icon", _closed.Token);
            _removeIcon = false;
            _storedIcon = null;
        }
    }

    private async Task PerformAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        FormPanel.IsEnabled = SaveButton.IsEnabled = StopButton.IsEnabled = false;
        ErrorText.Text = "";
        try { await action(); }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ErrorText.Text = $"{(Saved ? "项目已保存，可重试未完成操作。" : "")}{ex.Message}";
            if (ex is ApiException { RequiresStop: true })
                StopButton.Visibility = RunningNotice.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            FormPanel.IsEnabled = SaveButton.IsEnabled = StopButton.IsEnabled = true;
        }
    }
}

internal sealed class ProjectDetection
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("files")] public List<string> Files { get; set; } = [];
    [JsonPropertyName("candidates")] public List<ProjectCandidate> Candidates { get; set; } = [];
}

internal sealed class ProjectCandidate
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "service";
    [JsonPropertyName("port")] public int? Port { get; set; }
    public string Detail => Kind == "task" ? "批处理任务" : Port is null ? "服务" : $"端口 {Port}";
}
