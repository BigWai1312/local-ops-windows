using System.Net;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalOpsWindows.Client;

internal static class ReleaseUpdaterTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "local-ops-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var version = new Version(2, 1, 4);
            var installer = Encoding.UTF8.GetBytes("MZ-fixture-installer");
            var resources = Encoding.UTF8.GetBytes("""
                {"schemaVersion":1,"revision":2,"minClientVersion":"2.1.4","maxClientVersion":"2.1.4",
                 "light":{"AccentBrush":"#123456"},"dark":{"AccentBrush":"#ABCDEF"}}
                """);
            string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var tag = "v2.1.4-resources.2";
            var root = $"https://github.com/{ReleaseUpdater.Repository}/releases/download/{tag}/";
            var manifest = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, product = "LocalOpsWindows.CSharp", clientVersion = "2.1.4",
                installer = new { asset = ReleaseUpdater.InstallerName, sha256 = Digest(installer) },
                resources = new { asset = ReleaseUpdater.ResourceName, sha256 = Digest(resources),
                    revision = 2, minClientVersion = "2.1.4", maxClientVersion = "2.1.4" },
            });
            var release = JsonSerializer.Serialize(new
            {
                draft = false, prerelease = false, tag_name = tag,
                assets = new[] { ReleaseUpdater.ManifestName, ReleaseUpdater.InstallerName, ReleaseUpdater.ResourceName }
                    .Select(name => new { name, browser_download_url = root + name }),
            });
            var corrupt = false;
            Uri? redirect = null;
            using var handler = new CallbackHandler(request =>
            {
                var uri = request.RequestUri!;
                if (uri.Host == "api.github.com") return Json(release);
                if (uri.AbsolutePath.EndsWith(ReleaseUpdater.ManifestName)) return Json(manifest);
                if (redirect is not null && uri.Host == "github.com")
                    return new(HttpStatusCode.Redirect) { Headers = { Location = redirect } };
                var bytes = uri.AbsolutePath.EndsWith(ReleaseUpdater.ResourceName) ? resources : installer;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt ? [1, 2, 3] : bytes) };
            });
            using var updater = new ReleaseUpdater(handler);
            var update = await updater.CheckAsync(CancellationToken.None) ?? throw new Exception("missing release");
            Check(!update.NeedsInstaller(version) && update.CanUpdateResources(version, 1), "resource-only release");
            Check(update.NeedsInstaller(new Version(2, 0, 2)), "older client requires installer");
            Check(!update.CanUpdateResources(new Version(2, 2, 0), 1), "incompatible resources excluded");
            Check(!update.CanUpdateResources(version, 2), "resources are not replayed");
            var bytes = await updater.DownloadResourcesAsync(update.Resources, version, CancellationToken.None);
            var cache = Path.Combine(directory, "resources.json");
            ClientResources.Save(bytes, version, cache);
            ClientResources.Save(bytes, version, cache);
            File.WriteAllText(cache, "{\"revision\":9223372036854775807}");
            Check(ClientResources.Load(version, true, cache).Revision == 2, "corrupt resource cache uses backup");
            var path = await updater.DownloadInstallerAsync(update, directory, null, CancellationToken.None);
            Check(File.ReadAllBytes(path).SequenceEqual(installer), "verified installer committed");
            corrupt = true;
            await Reject(() => updater.DownloadResourcesAsync(update.Resources, version, CancellationToken.None));
            await Reject(() => updater.DownloadInstallerAsync(update, directory, null, CancellationToken.None));
            Check(File.ReadAllBytes(path).SequenceEqual(installer), "failed download preserves verified file");
            Check(!Directory.EnumerateFiles(directory, "*.part").Any(), "failed downloads cleaned");
            corrupt = false;
            redirect = new Uri("https://example.invalid/installer.exe");
            await Reject(() => updater.DownloadInstallerAsync(update, directory, null, CancellationToken.None));
            redirect = new Uri("https://release-assets.githubusercontent.com/fixture");
            await updater.DownloadInstallerAsync(update, directory, null, CancellationToken.None);
            redirect = null;
            var originalResources = resources;
            foreach (var value in new[]
            {
                "{\"schemaVersion\":1,\"script\":\"run\"}",
                Encoding.UTF8.GetString(originalResources).Replace("AccentBrush", "Command"),
                Encoding.UTF8.GetString(originalResources).Replace("#123456", "https://example.invalid"),
                Encoding.UTF8.GetString(originalResources).Replace("\"revision\":2", "\"revision\":2,\"revision\":3"),
                Encoding.UTF8.GetString(originalResources).Replace("2.1.4", "3.0.0"),
            })
                await Reject(() => Task.FromResult(ClientResources.Parse(Encoding.UTF8.GetBytes(value), version)));
            using var absent = new ReleaseUpdater(new CallbackHandler(_ => new(HttpStatusCode.NotFound)));
            Check(await absent.CheckAsync(CancellationToken.None) is null, "first release 404 is handled");
            release = release.Replace(ReleaseUpdater.Repository, "other/repo");
            await Reject(() => updater.CheckAsync(CancellationToken.None));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Reject(() => updater.DownloadInstallerAsync(update, directory, null, canceled.Token));
            Check(!Directory.EnumerateFiles(directory, "*.part").Any(), "canceled downloads cleaned");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("assertion failed: " + name);
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or KeyNotFoundException
            or FormatException or OverflowException or OperationCanceledException) { return; }
        throw new Exception("Expected invalid update to be rejected");
    }
}
