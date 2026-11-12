using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalOpsWindows.Client;

internal sealed record ResourcePack(int Revision, Version Minimum, Version Maximum,
    Dictionary<string, string> Light, Dictionary<string, string> Dark);

internal static class ClientResources
{
    internal const int MaximumBytes = 256 * 1024;
    private static readonly HashSet<string> ColorKeys =
    [
        "CanvasBrush", "SurfaceBrush", "SurfaceRaisedBrush", "SurfaceHoverBrush", "GlassBrush", "GlassStrongBrush", "PopupBrush",
        "TrackBrush", "GlassHoverBrush", "GlassPressedBrush", "BorderBrush", "SoftBorderBrush",
        "TextBrush", "MutedTextBrush", "DimTextBrush", "AccentBrush", "AccentStrongBrush",
        "AccentSoftBrush", "WarningBrush", "WarningSoftBrush", "DangerBrush", "DangerSoftBrush",
        "InfoBrush", "InfoSoftBrush", "InsetBrush", "InputBrush",
    ];
    internal static string CachePath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "LocalOpsWindows", "client", "resources.json");

    internal static ResourcePack Parse(byte[] bytes, Version current)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidOperationException("资源包过大。");
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Select(p => p.Name).Distinct().Count() != root.EnumerateObject().Count()
            || root.EnumerateObject().Any(p => p.Name is not ("schemaVersion" or "revision" or "minClientVersion" or "maxClientVersion" or "light" or "dark"))
            || root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidOperationException("资源包格式不受支持。");
        var revision = root.GetProperty("revision").GetInt32();
        var minimum = ReleaseUpdater.ParseVersion(root.GetProperty("minClientVersion").GetString());
        var maximum = ReleaseUpdater.ParseVersion(root.GetProperty("maxClientVersion").GetString());
        if (revision < 1 || minimum > maximum || current < minimum || current > maximum)
            throw new InvalidOperationException("资源包与当前客户端不兼容，请安装程序更新。");
        Dictionary<string, string> Colors(string name)
        {
            var result = new Dictionary<string, string>();
            foreach (var property in root.GetProperty(name).EnumerateObject())
            {
                var value = property.Value.GetString();
                if (!ColorKeys.Contains(property.Name) || value is null
                    || !Regex.IsMatch(value, "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")
                    || !result.TryAdd(property.Name, value))
                    throw new InvalidOperationException("资源包包含不允许的内容。");
            }
            return result;
        }
        return new(revision, minimum, maximum, Colors("light"), Colors("dark"));
    }

    internal static ResourcePack Load(Version current, bool includeCache, string? cachePath = null)
    {
        var baseline = Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "resources.json")), current);
        if (!includeCache) return baseline;
        cachePath ??= CachePath;
        foreach (var path in new[] { cachePath, cachePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) continue;
                var candidate = Parse(File.ReadAllBytes(path), current);
                if (candidate.Revision >= baseline.Revision) return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { }
        }
        return baseline;
    }

    internal static ResourcePack Save(byte[] bytes, Version current, string path)
    {
        var pack = Parse(bytes, current);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return pack;
    }
}
