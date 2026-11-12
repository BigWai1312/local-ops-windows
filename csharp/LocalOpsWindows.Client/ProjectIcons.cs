using System.Windows.Media.Imaging;

namespace LocalOpsWindows.Client;

internal static class ProjectIcons
{
    internal static readonly (string Name, string Label, string Code)[] Choices =
    [
        ("rocket", "启动", "\uE7C1"), ("globe", "网站", "\uE774"),
        ("terminal", "终端", "\uE756"), ("server", "服务", "\uE968"),
        ("database", "数据库", "\uE8B7"), ("bot", "助手", "\uE99A"),
        ("gamepad-2", "游戏", "\uE7FC"), ("film", "视频", "\uE714"),
        ("music", "音乐", "\uE8D6"), ("code", "代码", "\uE943"),
        ("folder-git-2", "项目", "\uE8B7"), ("zap", "任务", "\uE945"),
        ("container", "容器", "\uE7B8"), ("cpu", "计算", "\uE950"),
        ("wifi", "网络", "\uE701"), ("hard-drive", "存储", "\uEDA2"),
        ("package", "程序包", "\uE7B8"), ("wrench", "工具", "\uE90F"),
    ];

    internal static string Glyph(string? name) =>
        Choices.FirstOrDefault(x => x.Name == name).Code ?? "\uE756";

    internal static BitmapImage Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 96;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
