using System.Text.Json;

namespace LocalOpsWindows.Client;

internal static class ClientStorage
{
    internal static string ResolveRoot(string directory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        if (current.Parent?.Name == "versions" && current.Parent.Parent is { } root
            && File.Exists(Path.Combine(root.FullName, "installation.json")))
        {
            using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "installation.json")));
            if (!marker.RootElement.TryGetProperty("product", out var product)
                || product.GetString() != "LocalOpsWindows")
                throw new InvalidDataException("安装目录标记无效。");
            return root.FullName;
        }
        return current.FullName;
    }

    internal static string Root => ResolveRoot(AppContext.BaseDirectory);
    internal static bool Installed => !string.Equals(Root.TrimEnd(Path.DirectorySeparatorChar),
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    internal static string Data => Path.Combine(Root, "data");
    internal static string Logs => Path.Combine(Root, "logs");
    internal static string Cache => Path.Combine(Root, "cache");
    internal static string Temporary => Path.Combine(Cache, "temp");
    private static string Settings => Path.Combine(Data, "client-settings.json");

    private static Dictionary<string, string>? Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static string? Read(string name, string? path = null)
    {
        path ??= Settings;
        return (Load(path) ?? Load(path + ".bak"))?.GetValueOrDefault(name);
    }

    internal static void Write(string name, string value, string? path = null)
    {
        path ??= Settings;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var primary = Load(path);
        var values = primary ?? Load(path + ".bak") ?? new();
        values[name] = value;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(values));
            if (File.Exists(path) && primary is not null) File.Replace(temporary, path, path + ".bak");
            else
            {
                if (File.Exists(path)) File.Copy(path, path + ".corrupt", overwrite: true);
                File.Move(temporary, path, overwrite: true);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
