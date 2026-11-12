using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalOpsWindows.Client;

internal sealed record ReleaseAsset(string Name, Uri Url, string Sha256);
internal sealed record ResourceRelease(ReleaseAsset Asset, int Revision, Version Minimum, Version Maximum);
internal sealed record AvailableUpdate(Version ClientVersion, ReleaseAsset Installer, ResourceRelease Resources)
{
    internal bool NeedsInstaller(Version current) => ClientVersion > current;
    internal bool CanUpdateResources(Version current, int revision) => current >= Resources.Minimum
        && current <= Resources.Maximum && Resources.Revision > revision;
}

internal sealed class ReleaseUpdater : IDisposable
{
    internal const string Repository = "BigWai1312/local-ops-windows";
    internal const string ManifestName = "LocalOpsWindows.Update.json";
    internal const string InstallerName = "LocalOpsWindows-Setup.exe";
    internal const string ResourceName = "LocalOpsWindows.Resources.json";
    internal static Version CurrentVersion => typeof(ReleaseUpdater).Assembly.GetName().Version is { } version
        ? new Version(version.Major, version.Minor, version.Build) : new Version(0, 0, 0);
    private readonly HttpClient _http;
    internal ReleaseUpdater(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalOpsWindows/" + CurrentVersion);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal static Version ParseVersion(string? value)
    {
        if (value is null || !Regex.IsMatch(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
            || !Version.TryParse(value, out var version))
            throw new InvalidOperationException("更新版本号无效。");
        return version;
    }

    internal async Task<AvailableUpdate?> CheckAsync(CancellationToken token)
    {
        using var response = await _http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest",
            HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await ReadBytesAsync(response, 1024 * 1024, token));
        var root = release.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidOperationException("发行版本尚未正式发布。");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+(?:-resources\.\d+)?$"))
            throw new InvalidOperationException("发行标签格式无效。");
        var assets = root.GetProperty("assets").EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!,
            item => item.GetProperty("browser_download_url").GetString()!);
        Uri AssetUrl(string name)
        {
            if (!assets.TryGetValue(name, out var value)
                || value != $"https://github.com/{Repository}/releases/download/{tag}/{name}")
                throw new InvalidOperationException($"发行包缺少有效的 {name}。");
            return new Uri(value);
        }
        var manifestUri = AssetUrl(ManifestName);
        using var manifestResponse = await GetAssetAsync(manifestUri, token);
        using var document = JsonDocument.Parse(await ReadBytesAsync(manifestResponse, 65536, token));
        var manifest = document.RootElement;
        if (manifest.GetProperty("schemaVersion").GetInt32() != 1
            || manifest.GetProperty("product").GetString() != "LocalOpsWindows.CSharp")
            throw new InvalidOperationException("此发行包不是兼容的 C# 客户端。");
        ReleaseAsset ReadAsset(JsonElement entry, string expected)
        {
            var digest = entry.GetProperty("sha256").GetString() ?? "";
            if (entry.GetProperty("asset").GetString() != expected || !Regex.IsMatch(digest, "^[0-9a-fA-F]{64}$"))
                throw new InvalidOperationException("发行文件校验信息无效。");
            return new(expected, AssetUrl(expected), digest);
        }
        var resources = manifest.GetProperty("resources");
        var minimum = ParseVersion(resources.GetProperty("minClientVersion").GetString());
        var maximum = ParseVersion(resources.GetProperty("maxClientVersion").GetString());
        var revision = resources.GetProperty("revision").GetInt32();
        if (minimum > maximum || revision < 1) throw new InvalidOperationException("资源兼容范围无效。");
        var clientVersion = ParseVersion(manifest.GetProperty("clientVersion").GetString());
        if (tag != "v" + clientVersion && !tag.StartsWith("v" + clientVersion + "-resources.", StringComparison.Ordinal))
            throw new InvalidOperationException("发行标签和客户端版本不一致。");
        return new(clientVersion, ReadAsset(manifest.GetProperty("installer"), InstallerName),
            new(ReadAsset(resources, ResourceName), revision, minimum, maximum));
    }

    private async Task<HttpResponseMessage> GetAssetAsync(Uri initial, CancellationToken token)
    {
        if (initial.Scheme != "https" || initial.Host != "github.com" || !initial.IsDefaultPort
            || initial.UserInfo.Length > 0 || initial.Query.Length > 0 || initial.Fragment.Length > 0
            || !initial.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal))
            throw new InvalidOperationException("更新来源无效。");
        var url = initial;
        for (var count = 0; count < 5; count++)
        {
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidOperationException("更新下载重定向无效。");
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (next.Scheme != "https" || !next.IsDefaultPort || next.UserInfo.Length > 0 || next.Fragment.Length > 0
                    || !(next == initial || next.Host is "objects.githubusercontent.com" or "release-assets.githubusercontent.com"))
                    throw new InvalidOperationException("更新下载重定向离开了 GitHub。");
                url = next;
                continue;
            }
            try { response.EnsureSuccessStatusCode(); }
            catch { response.Dispose(); throw; }
            return response;
        }
        throw new InvalidOperationException("更新下载重定向过多。");
    }

    private static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidOperationException("更新响应超过大小限制。");
        using var source = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidOperationException("更新响应超过大小限制。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal async Task<byte[]> DownloadResourcesAsync(ResourceRelease resource, Version current, CancellationToken token)
    {
        using var response = await GetAssetAsync(resource.Asset.Url, token);
        var bytes = await ReadBytesAsync(response, ClientResources.MaximumBytes, token);
        VerifyHash(bytes, resource.Asset.Sha256);
        var pack = ClientResources.Parse(bytes, current);
        if (pack.Revision != resource.Revision || pack.Minimum != resource.Minimum || pack.Maximum != resource.Maximum)
            throw new InvalidOperationException("资源包与发行清单不一致。");
        return bytes;
    }

    internal async Task<string> DownloadInstallerAsync(AvailableUpdate update, string directory,
        IProgress<int>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, update.ClientVersion + "-" + update.Installer.Sha256[..12] + "-" + InstallerName);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await GetAssetAsync(update.Installer.Url, token);
            const long maximum = 250 * 1024 * 1024;
            var length = response.Content.Headers.ContentLength;
            if (length > maximum) throw new InvalidOperationException("安装包超过大小限制。");
            using var source = await response.Content.ReadAsStreamAsync(token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long total = 0;
                int count, lastProgress = -1;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    total += count;
                    if (total > maximum) throw new InvalidOperationException("安装包超过大小限制。");
                    hash.AppendData(buffer.AsSpan(0, count));
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    var percent = length is > 0 ? (int)Math.Min(100, total * 100 / length.Value) : 0;
                    if (percent != lastProgress) { progress?.Report(percent); lastProgress = percent; }
                }
                if (total < 2 || (length.HasValue && total != length.Value)
                    || !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Installer.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("安装包 SHA-256 校验失败。");
            }
            using (var pe = File.OpenRead(temporary))
                if (pe.ReadByte() != 'M' || pe.ReadByte() != 'Z') throw new InvalidOperationException("安装包格式无效。");
            File.Move(temporary, target, true);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void VerifyHash(byte[] bytes, string expected)
    {
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("资源包 SHA-256 校验失败。");
    }

    public void Dispose() => _http.Dispose();
}
