using System.Text.Json;

namespace LocalOpsWindows.Client;

internal sealed record MonitorEvent(DateTime At, string Title, string Detail, bool Warning = false)
{
    public string Time => At.ToString("HH:mm:ss");
}

internal sealed class MonitorSession
{
    private DashboardState? _previous;
    private readonly HashSet<string> _seen = [];
    private readonly Dictionary<string, ServiceSnapshot> _discoveries = [];
    internal List<MonitorEvent> Events { get; } = [];
    internal Dictionary<string, Queue<(double Cpu, double Mem)>> History { get; } = [];
    internal IEnumerable<ServiceSnapshot> Discoveries => _discoveries.Values;
    internal static string Key(ServiceSnapshot service) =>
        string.IsNullOrEmpty(service.InstanceKey) ? service.Key : service.InstanceKey;

    internal void ResetBaseline()
    {
        _previous = null;
        _seen.Clear();
        _discoveries.Clear();
        History.Clear();
    }

    internal void Dismiss(ServiceSnapshot service) => _discoveries.Remove(Key(service));

    internal void Observe(DashboardState next)
    {
        if (next.DegradedReasons.Any(reason => reason.ValueKind == JsonValueKind.Object
            && reason.TryGetProperty("component", out var component) && component.GetString() == "services"))
        {
            ResetBaseline();
            return;
        }
        var current = next.Services.Where(s => Key(s).Length > 0).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var knownPorts = next.Apps.Where(a => a.Running).SelectMany(a => a.ActualPorts
            .Concat(a.Port is > 0 && a.Listening != false ? [a.Port.Value] : Array.Empty<int>())).ToHashSet();
        bool Eligible(ServiceSnapshot s) => s.Group == "mine" && !s.Hidden && !s.Pinned
            && !s.Promoted && string.IsNullOrEmpty(s.AppId) && !knownPorts.Contains(s.Port);
        if (_previous is not null)
        {
            var apps = _previous.Apps.ToDictionary(a => a.Id);
            foreach (var app in next.Apps)
            {
                if (!apps.TryGetValue(app.Id, out var before)) continue;
                if (app.Running != before.Running)
                    Add($"{app.Name} · {(app.Running ? "开始运行" : "已停止")}", app.PortLabel);
                if (app.HasWarning && !before.HasWarning) Add($"{app.Name} · {app.StatusLabel}", app.PortLabel, true);
                if (app.LastExit is not null && ExitSignature(app.LastExit) != ExitSignature(before.LastExit))
                    Add($"{app.Name} · {app.StatusLabel}", app.UptimeLabel, app.LastExit.EffectiveStatus == "failed");
            }
            var previousServices = _previous.Services.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
            foreach (var (key, service) in current)
            {
                if (!_seen.Contains(key) && Eligible(service)) _discoveries[key] = service;
                if (!previousServices.ContainsKey(key) && service.Group == "mine" && !service.Hidden && service.AppId is null)
                    Add($"{service.DisplayName} · 开始监听", $":{service.Port} · PID {service.Pid}");
            }
            foreach (var (key, service) in previousServices)
                if (!current.ContainsKey(key) && service.Group == "mine" && !service.Hidden && service.AppId is null)
                    Add($"{service.DisplayName} · 停止监听", $":{service.Port} · PID {service.Pid}");
            if (next.Degraded && !_previous.Degraded) Add("后台数据不完整", "请查看控制台日志", true);
        }
        foreach (var key in _discoveries.Keys.ToArray())
        {
            if (current.TryGetValue(key, out var service) && Eligible(service)) _discoveries[key] = service;
            else _discoveries.Remove(key);
        }
        // Session history is bounded; long-running workstations must not accumulate departed processes.
        if (_seen.Count + current.Count > 10000) _seen.Clear();
        _seen.UnionWith(current.Keys);
        foreach (var key in History.Keys.Where(k => !current.ContainsKey(k)).ToArray()) History.Remove(key);
        foreach (var (key, service) in current.Take(1000))
        {
            if (!History.TryGetValue(key, out var samples)) History[key] = samples = new();
            samples.Enqueue((service.Cpu, service.Mem));
            while (samples.Count > 120) samples.Dequeue();
        }
        _previous = next;
    }

    private void Add(string title, string detail, bool warning = false)
    {
        Events.Insert(0, new(DateTime.Now, title, detail, warning));
        if (Events.Count > 50) Events.RemoveAt(50);
    }

    internal static string ExitSignature(LastExitSnapshot? exit) => exit is null ? ""
        : $"{exit.At}:{exit.Code}:{exit.Status}:{exit.DurationSec}";
}

internal sealed class TaskCompletionTracker
{
    private Dictionary<string, string>? _previous;
    internal void Reset() => _previous = null;
    internal List<AppSnapshot> Observe(IEnumerable<AppSnapshot> apps)
    {
        var tasks = apps.Where(a => a.Kind == "task").ToList();
        var completed = _previous is null ? [] : tasks.Where(a => a.LastExit is not null
            && a.LastExit.EffectiveStatus != "stopped"
            && _previous.TryGetValue(a.Id, out var signature)
            && signature != MonitorSession.ExitSignature(a.LastExit)).ToList();
        _previous = tasks.ToDictionary(a => a.Id, a => MonitorSession.ExitSignature(a.LastExit));
        return completed;
    }
}
