using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LocalOpsWindows.Preview;

public sealed class DemoProject : INotifyPropertyChanged
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; init; } = "service";
    public string Status { get; set; } = "未运行";
    public int Port { get; set; }
    public string Command { get; set; } = "";
    public string Notes { get; set; } = "";
    private bool _selected;
    public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
    public string Directory { get; set; } = @"C:\Projects\Example";
    public string Glyph { get; init; } = "server";
    public string Tone { get; init; } = "green";
    public double Cpu { get; set; }
    public double Memory { get; set; }
    public string Uptime { get; set; } = "尚未启动";
    public bool External { get; set; }
    public bool Pinned { get; set; }
    public bool Hidden { get; set; }
    public string PortLabel => Port > 0 ? $":{Port}" : "任务";
    public string CpuLabel => Status == "运行中" ? $"{Cpu:0.0}%" : "—";
    public string MemoryLabel => Status == "运行中" ? $"{Memory:0.0}%" : "—";
    public double CpuBar => Math.Clamp(Cpu * 10, 3, 46);
    public double MemoryBar => Math.Clamp(Memory * 10, 3, 46);
    public string ToggleLabel => Status == "运行中" ? "停止" : Kind == "task" ? "运行" : "启动";
    public string ToggleGlyph => Status == "运行中" ? "square" : "play";
    public string PidLabel => $"PID {4300 + Port % 7000}";
    public string ServiceCaption => $"{(External ? "后台进程" : "已登记项目")} · {Command}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

public sealed record DemoLog(string Time, string Level, string Message, string Target = "console", bool Technical = false);

internal static class DemoData
{
    internal static List<DemoProject> Projects() =>
    [
        P("atlas", "Atlas API 网关", "server", "green", 8088, "运行中", "dotnet Atlas.Gateway.dll --urls http://127.0.0.1:8088", @"C:\Projects\Atlas\gateway", 1.2, 2.4, "2 小时 18 分"),
        P("harbor", "Harbor 开发工作台", "code", "blue", 4173, "运行中", "npm run dev -- --host 127.0.0.1 --port 4173", @"C:\Projects\Harbor\web", .6, 1.8, "1 小时 42 分"),
        P("orchard", "Orchard 任务调度中心", "container", "green", 9002, "未运行", "python -m orchard.worker --queue local", @"C:\Projects\Orchard\worker"),
        P("mail", "邮件归档服务", "database", "blue", 18088, "运行中", "docker compose up mail-archive", @"C:\Projects\MailArchive", .3, 1.1, "3 小时 06 分"),
        P("insight", "Insight 数据分析与本地报告预览服务", "activity", "amber", 8050, "失败", "python -m insight.preview --project quarterly-report --port 8050", @"C:\Projects\Insight\reports\quarterly", uptime: "退出码 1 · 10:24:08"),
        P("cache", "Redis 本地缓存", "package", "amber", 6379, "运行中", "docker start local-redis", @"C:\Projects\Infrastructure\redis", .1, .4, "4 小时 12 分"),
        P("studio", "Studio 静态资源预览", "image", "blue", 4321, "未运行", "npm run preview -- --port 4321", @"C:\Projects\Studio\assets"),
        P("sync", "文件同步与增量索引服务", "folder", "amber", 7820, "端口占用", "node server.mjs --watch ./workspace --index ./cache/index", @"C:\Projects\FileSync\workspace", uptime: "端口被其他进程占用"),
        P("paper", "Paper 文档处理 API", "file-text", "green", 8010, "运行中", "python -m uvicorn paper.api:app --host 127.0.0.1 --port 8010", @"C:\Projects\Paper\document-api", .7, 1.5, "46 分钟"),
        P("metrics", "Metrics 指标采集器", "gauge", "blue", 9191, "未运行", "metrics-agent.exe --config ./local.json", @"C:\Projects\Metrics\agent"),
        P("mirror", "Mirror 资源镜像", "globe", "green", 5050, "未运行", "node ./src/server.js --mode local", @"C:\Projects\Mirror\server"),
        P("backup", "每日项目备份", "hard-drive", "green", 0, "已完成", "powershell -NoProfile -File ./backup.ps1 -Mode Incremental", @"C:\Projects\Maintenance\backup", uptime: "上次完成 09:00 · 耗时 12 秒"),
        P("report", "月度运营数据汇总", "file-text", "blue", 0, "未运行", "python build_report.py --month 2026-10 --format xlsx", @"C:\Projects\Reports\monthly", uptime: "等待运行"),
        P("cleanup", "清理过期构建产物", "package", "amber", 0, "已完成", "node ./scripts/cleanup.mjs --older-than 14d", @"C:\Projects\Maintenance\cleanup", uptime: "上次完成 昨天 18:30"),
        P("export", "导出本地配置清单", "download", "blue", 0, "未运行", "python export.py --output ./exports/workspace.json", @"C:\Projects\Maintenance\exports"),
        P("catalog", "更新素材目录索引", "folder-git-2", "amber", 0, "失败", "node rebuild-index.mjs --source ./assets", @"C:\Projects\Studio\catalog", uptime: "退出码 2 · 昨天 16:10"),
        P("health", "开发环境健康检查", "check", "green", 0, "未运行", "powershell -NoProfile -File ./check-env.ps1", @"C:\Projects\Maintenance\checks"),
    ];
    internal static List<DemoProject> External() =>
    [
        P("proxy", "本地调试代理", "globe", "blue", 7890, "运行中", "dev-proxy.exe --mode local", @"C:\Projects\Tools\proxy", .2, .7, "52 分钟", true),
        P("preview", "设计资源服务器", "image", "blue", 6006, "运行中", "node ./storybook.mjs", @"C:\Projects\Design\storybook", .4, .9, "28 分钟", true),
        P("db", "本地数据库", "database", "green", 5432, "运行中", "postgres.exe -D ./data", @"C:\Projects\Infrastructure\postgres", .3, 1.2, "5 小时 11 分", true),
    ];
    private static DemoProject P(string id, string name, string glyph, string tone, int port, string status, string command, string directory, double cpu = 0, double memory = 0, string uptime = "尚未启动", bool external = false) => new()
    { Id = id, Name = name, Glyph = glyph, Tone = tone, Port = port, Status = status, Command = command, Directory = directory, Cpu = cpu, Memory = memory, Uptime = uptime, External = external, Kind = port == 0 ? "task" : "service",
        Notes = id switch { "atlas" => "本地 API 统一入口，供开发工作台与报告服务调用。", "harbor" => "前端开发与联调环境，修改资源后自动刷新。", "orchard" => "处理本地排队任务；运行前先确认工作目录和依赖。", "insight" => "季度报告预览。需补齐 local.json 后再启动，避免重复失败。", "sync" => "同步工作目录并更新索引，不包含归档与备份文件。", "backup" => "每日增量备份；保留最近 14 天的项目归档。", "mail" => "归档测试邮件，便于本地查询与检索。", _ => port == 0 ? "按需运行的维护任务，执行完成后可在日志中查看结果。" : "本地开发服务，仅用于当前工作区。" } };
    internal static List<DemoLog> Logs() =>
    [
        new("10:36:12", "INFO", "工作区已连接，共载入 17 个项目。"),
        new("10:36:12", "HTTP", "127.0.0.1 - \"GET /api/health HTTP/1.1\" 200 -", Technical: true),
        new("10:36:13", "INFO", "Atlas API 网关已启动，监听端口 :8088。", "atlas"),
        new("10:36:13", "HTTP", "127.0.0.1 - \"GET /icons/a756e718.png HTTP/1.1\" 200 -", Technical: true),
        new("10:36:18", "INFO", "Harbor 开发工作台已启动，监听端口 :4173。", "harbor"),
        new("10:36:23", "HTTP", "127.0.0.1 - \"GET /api/state HTTP/1.1\" 200 -", Technical: true),
        new("10:36:41", "WARN", "文件同步服务端口 :7820 已被占用，项目未启动。", "sync"),
        new("10:37:02", "INFO", "每日项目备份执行完成，退出码 0，耗时 12 秒。", "backup"),
        new("10:37:08", "ERROR", "Insight 报告预览启动失败：缺少配置文件 local.json。", "insight"),
        new("10:37:11", "HTTP", "127.0.0.1 - \"GET /api/console/log?tail=300 HTTP/1.1\" 200 -", Technical: true),
        new("10:37:16", "INFO", "Paper 文档处理 API 已就绪，监听端口 :8010。", "paper"),
        new("10:37:18", "HTTP", "127.0.0.1 - \"GET /api/console/log?tail=300 HTTP/1.1\" 200 -", Technical: true),
        new("10:37:21", "INFO", "已保存启动台项目排序。"),
    ];
}
