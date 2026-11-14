using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalOpsWindows.Client;

public sealed class DashboardState
{
    [JsonPropertyName("apps")] public List<AppSnapshot> Apps { get; set; } = [];
    [JsonPropertyName("services")] public List<ServiceSnapshot> Services { get; set; } = [];
    [JsonPropertyName("watched")] public List<WatchedSnapshot> Watched { get; set; } = [];
    [JsonPropertyName("watchedKeywords")] public List<string> WatchedKeywords { get; set; } = [];
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("consolePort")] public int ConsolePort { get; set; }
    [JsonPropertyName("consoleCwd")] public string? ConsoleCwd { get; set; }
    [JsonPropertyName("degraded")] public bool Degraded { get; set; }
    [JsonPropertyName("degradedReasons")] public List<JsonElement> DegradedReasons { get; set; } = [];
    [JsonPropertyName("uiTheme")] public string UiTheme { get; set; } = "ops";
    [JsonPropertyName("themes")] public List<ThemeSnapshot> Themes { get; set; } = [];
}

public sealed class AppSnapshot
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "service";
    [JsonPropertyName("running")] public bool Running { get; set; }
    [JsonPropertyName("pid")] public int? Pid { get; set; }
    [JsonPropertyName("uptimeSec")] public double? UptimeSec { get; set; }
    [JsonPropertyName("lastExit")] public LastExitSnapshot? LastExit { get; set; }
    [JsonPropertyName("health")] public JsonElement Health { get; set; }
    [JsonPropertyName("portOccupied")] public bool PortOccupied { get; set; }
    [JsonPropertyName("portOccupiedPid")] public int? PortOccupiedPid { get; set; }
    [JsonPropertyName("ports")] public List<int> Ports { get; set; } = [];
    [JsonPropertyName("openHosts")] public Dictionary<string, string> OpenHosts { get; set; } = [];
    [JsonPropertyName("listening")] public bool? Listening { get; set; }
    [JsonPropertyName("portConflict")] public bool PortConflict { get; set; }
    [JsonPropertyName("glyph")] public string? Glyph { get; set; }
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("favicon")] public string? Favicon { get; set; }

    [JsonIgnore] public string KindLabel => Kind == "task" ? "任务" : "服务";
    [JsonIgnore] public bool PortMismatch => Running && Port is > 0 && Listening == false
        && ActualPorts.Count > 0 && !ActualPorts.Contains(Port.Value);
    [JsonIgnore] public List<int> ActualPorts => Ports.Where(p => p is > 0 and <= 65535).Distinct().ToList();
    [JsonIgnore] public int? PreferredPort => PortMismatch ? ActualPorts[0]
        : Port is > 0 and <= 65535 && (!Running || Listening != false) ? Port
        : ActualPorts.Cast<int?>().FirstOrDefault() ?? Port;
    [JsonIgnore] public bool CanOpen => Running && PreferredPort is > 0 and <= 65535
        && !PortOccupied && !PortConflict && (Port is null || Listening != false || PortMismatch);
    [JsonIgnore] public string? OpenUrl => PreferredPort is int port
        ? LocalServiceAddress.Create(port, OpenHosts.GetValueOrDefault(port.ToString())) : null;
    [JsonIgnore] public bool HasWarning => PortOccupied || PortConflict || PortMismatch || BlockingHealth;
    [JsonIgnore] public bool BlockingHealth => Health.ValueKind == JsonValueKind.Object
        && Health.TryGetProperty("blocking", out var blocking) && blocking.ValueKind == JsonValueKind.True;
    [JsonIgnore] public string StatusLabel => PortConflict ? "配置冲突" : PortOccupied ? "外部占用"
        : PortMismatch ? "端口不一致" : Running ? (Port is > 0 && Listening == false ? "等待端口" : "运行中")
        : BlockingHealth ? "配置不可用" : LastExit?.EffectiveStatus switch
        {
            "succeeded" => "已完成",
            "failed" => "失败",
            "canceled" => "已取消",
            "stopped" => "已中止",
            _ => "未运行"
        };
    [JsonIgnore] public string ToggleLabel => Running ? (Kind == "task" ? "中止" : "停止")
        : PortOccupied ? "处理占用" : Kind == "task" ? "运行" : "启动";
    [JsonIgnore] public string ToggleGlyph => Running ? "\uE71A" : PortOccupied ? "\uE7BA" : "\uE768";
    [JsonIgnore] public string PortLabel => PortOccupied ? $"端口 {Port} 被占用"
        : PortMismatch ? $":{PreferredPort}（实际）≠ :{Port}"
        : Running && ActualPorts.Count > 0 ? string.Join("、", ActualPorts.Select(p => $":{p}"))
        : Port is null ? "无端口" : $":{Port}";
    [JsonIgnore] public string PidLabel => Pid is null ? "未分配 PID" : $"PID {Pid}";
    [JsonIgnore] public string UptimeLabel => Running ? FormatDuration(UptimeSec)
        : LastExit is null ? "—" : $"退出码 {LastExit.Code?.ToString() ?? "—"} · {FormatDuration(LastExit.DurationSec)}";
    [JsonIgnore] public string IconGlyph => ProjectIcons.Glyph(Glyph);
    [JsonIgnore] public System.Windows.Media.ImageSource? IconImage { get; set; }

    internal static string FormatDuration(double? seconds) => seconds is null || !double.IsFinite(seconds.Value)
        || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds ? "—"
        : TimeSpan.FromSeconds(seconds.Value).ToString(seconds >= 86400 ? @"d\.hh\:mm\:ss"
            : seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
}

public sealed class LastExitSnapshot
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("durationSec")] public double? DurationSec { get; set; }
    [JsonPropertyName("at")] public JsonElement At { get; set; }
    [JsonPropertyName("code")] public long? Code { get; set; }
    [JsonIgnore] public string? EffectiveStatus => Status ?? (Code is null ? null : Code == 0 ? "succeeded" : "failed");
}

public sealed class ServiceSnapshot
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("instanceKey")] public string InstanceKey { get; set; } = "";
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("project")] public string? Project { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("cmd")] public string? Command { get; set; }
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("appId")] public string? AppId { get; set; }
    [JsonPropertyName("appName")] public string? AppName { get; set; }
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }
    [JsonPropertyName("promoted")] public bool Promoted { get; set; }
    [JsonPropertyName("cpu")] public double Cpu { get; set; }
    [JsonPropertyName("mem")] public double Mem { get; set; }
    [JsonPropertyName("origin")] public JsonElement Origin { get; set; }
    [JsonPropertyName("openHost")] public string? OpenHost { get; set; }
    [JsonPropertyName("uptimeSec")] public double? UptimeSec { get; set; }

    [JsonIgnore] public string DisplayName => AppName ?? Project ?? Name;
    [JsonIgnore] public string MemoryLabel => $"{Mem:0.0}%";
    [JsonIgnore] public string PinLabel => Pinned ? "取消置顶" : "置顶";
    [JsonIgnore] public string PinGlyph => Pinned ? "\uE77A" : "\uE718";
    [JsonIgnore] public string VisibilityLabel => Hidden ? "取消隐藏" : "隐藏";
    [JsonIgnore] public string VisibilityGlyph => Hidden ? "\uE890" : "\uE7B3";
    [JsonIgnore] public string PromoteLabel => Promoted ? "取消提升" : "加入我的服务";
    [JsonIgnore] public string PromoteGlyph => Promoted ? "\uE73E" : "\uE710";
    [JsonIgnore] public string OpenUrl => LocalServiceAddress.Create(Port, OpenHost);
    [JsonIgnore] public string RegistrationLabel => string.IsNullOrEmpty(AppId) ? "加入启动台" : "编辑项目";
    [JsonIgnore] public string OriginLabel
    {
        get
        {
            if (Origin.ValueKind == JsonValueKind.Object
                && Origin.TryGetProperty("label", out var label))
            {
                return label.GetString() ?? "系统发现";
            }

            return Group switch
            {
                "mine" => "已登记项目",
                "background" => "后台进程",
                _ => "系统发现"
            };
        }
    }
}

public sealed class WatchedSnapshot
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("cmd")] public string Command { get; set; } = "";
    [JsonPropertyName("keyword")] public string Keyword { get; set; } = "";
    [JsonPropertyName("cpu")] public double Cpu { get; set; }
    [JsonPropertyName("mem")] public double Mem { get; set; }
    [JsonPropertyName("uptimeSec")] public double? UptimeSec { get; set; }
    [JsonIgnore] public string UptimeLabel => AppSnapshot.FormatDuration(UptimeSec);
}

public sealed class ThemeSnapshot
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class HealthSnapshot
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("degraded")] public bool Degraded { get; set; }
    [JsonPropertyName("issues")] public List<string> Issues { get; set; } = [];
}

internal static class LocalServiceAddress
{
    internal static string Create(int port, string? host)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        // Only backend-observed loopback families are accepted; never open an arbitrary supplied host.
        var loopback = host is "::1" or "[::1]" ? "[::1]" : host == "localhost" ? "localhost" : "127.0.0.1";
        return $"http://{loopback}:{port}/";
    }
}
