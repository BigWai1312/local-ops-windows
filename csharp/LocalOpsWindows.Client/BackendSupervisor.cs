using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace LocalOpsWindows.Client;

public sealed class BackendSupervisor : IAsyncDisposable
{
    private readonly HttpClient _probe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _ownedProcess;
    private int _ownedPort;
    private bool _disposed;

    public BackendSupervisor(HttpMessageHandler? handler = null)
    {
        _probe = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    public int Port { get; private set; }
    public bool OwnsProcess => _ownedProcess is not null;

    public void DetachForUpdate()
    {
        _ownedProcess?.Dispose();
        _ownedProcess = null;
        _ownedPort = 0;
        _probe.Dispose();
    }

    public async Task<int> StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await EnsureStartedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        Port = 0;
        if (_ownedProcess?.HasExited == true)
        {
            _ownedProcess.Dispose();
            _ownedProcess = null;
            _ownedPort = 0;
        }

        var existing = await FindProductAsync(cancellationToken, _ownedProcess?.Id);
        if (existing.Port > 0)
        {
            Port = existing.Port;
            if (_ownedProcess is not null)
            {
                _ownedPort = existing.Port;
            }
            return Port;
        }

        if (_ownedProcess is null)
        {
            var root = FindProjectRoot(AppContext.BaseDirectory);
            var packagedBackend = Path.Combine(AppContext.BaseDirectory, "backend", "LocalOpsWindowsBackend.exe");
            var startInfo = File.Exists(packagedBackend)
                ? new ProcessStartInfo(packagedBackend)
                : CreatePythonStartInfo(root);
            startInfo.ArgumentList.Add("--no-browser");
            startInfo.ArgumentList.Add("--background");
            startInfo.ArgumentList.Add("--preferred-port");
            startInfo.ArgumentList.Add("9600");
            startInfo.WorkingDirectory = File.Exists(packagedBackend)
                ? Path.GetDirectoryName(packagedBackend)!
                : root;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            startInfo.Environment["PYTHONUTF8"] = "1";

            _ownedProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动本地运维台后台进程。");
            _ownedProcess.OutputDataReceived += (_, _) => { };
            _ownedProcess.ErrorDataReceived += (_, _) => { };
            _ownedProcess.BeginOutputReadLine();
            _ownedProcess.BeginErrorReadLine();
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var candidate = await FindProductAsync(cancellationToken, _ownedProcess.Id);
            if (candidate.Port > 0 && candidate.ConsolePid == _ownedProcess.Id)
            {
                Port = candidate.Port;
                _ownedPort = candidate.Port;
                return Port;
            }

            if (_ownedProcess.HasExited)
            {
                throw new InvalidOperationException(
                    $"本地运维台后台启动失败，退出码 {_ownedProcess.ExitCode}。");
            }

            await Task.Delay(250, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("等待本地运维台后台就绪超时。详情见本机日志目录的 console.log。");
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopOwnedProcessAsync();
        }
        finally
        {
            _probe.Dispose();
            _gate.Release();
        }
    }

    private async Task StopOwnedProcessAsync()
    {
        if (_ownedProcess is null)
        {
            return;
        }

        try
        {
            var candidate = await FindProductAsync(CancellationToken.None, _ownedProcess.Id);
            if (candidate.Port == _ownedPort && candidate.ConsolePid == _ownedProcess.Id)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"http://127.0.0.1:{_ownedPort}/api/console/stop")
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8,
                        "application/json")
                };
                using var response = await _probe.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(2));
                response.EnsureSuccessStatusCode();
                await _ownedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
            }
        }
        catch
        {
            // A failed probe must not prevent cleanup of the exact owned child.
        }
        finally
        {
            try
            {
                if (!_ownedProcess.HasExited)
                {
                    _ownedProcess.Kill(entireProcessTree: false);
                    await _ownedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            catch
            {
                // The process may already be gone or exiting.
            }
            _ownedProcess.Dispose();
            _ownedProcess = null;
            _ownedPort = 0;
        }
    }

    private async Task<BackendCandidate> FindProductAsync(
        CancellationToken cancellationToken, int? expectedPid = null)
    {
        for (var port = 9600; port <= 9609; port++)
        {
            try
            {
                using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                healthTimeout.CancelAfter(TimeSpan.FromMilliseconds(900));
                using var response = await _probe.GetAsync(
                    $"http://127.0.0.1:{port}/api/health", healthTimeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("platform", out var platform)
                    && platform.ValueKind == JsonValueKind.String && platform.GetString() == "windows"
                    && root.TryGetProperty("productName", out var product)
                    && product.ValueKind == JsonValueKind.String && product.GetString() == "本地运维台 Windows")
                {
                    int consolePid;
                    if (root.TryGetProperty("consolePid", out var pid))
                    {
                        if (pid.ValueKind != JsonValueKind.Number || !pid.TryGetInt32(out consolePid)) continue;
                    }
                    else
                    {
                        // Older backends need a full scan, which can exceed the health probe timeout.
                        consolePid = await ReadConsolePidAsync(port, cancellationToken);
                    }
                    if (consolePid > 0 && (expectedPid is null || consolePid == expectedPid))
                    {
                        return new BackendCandidate(port, consolePid);
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (JsonException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        return new BackendCandidate(0, 0);
    }

    private readonly record struct BackendCandidate(int Port, int ConsolePid);

    private async Task<int> ReadConsolePidAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _probe.GetAsync(
                $"http://127.0.0.1:{port}/api/state", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("consolePid", out var pid)
                && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var value) ? value : 0;
        }
        catch (HttpRequestException)
        {
            return 0;
        }
        catch (JsonException)
        {
            return 0;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static ProcessStartInfo CreatePythonStartInfo(string root)
    {
        var server = Path.Combine(root, "server.py");
        if (!File.Exists(server))
        {
            throw new FileNotFoundException(
                "找不到本地运维台后台。请将 LocalOpsWindowsBackend.exe 或 server.py 放在客户端目录附近。",
                server);
        }

        var info = new ProcessStartInfo(FindPython());
        info.ArgumentList.Add(server);
        return info;
    }

    private static string FindPython()
    {
        var local = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
        if (Directory.Exists(local))
        {
            var candidate = Directory.EnumerateDirectories(local)
                .Select(path => Path.Combine(path, "python.exe")).FirstOrDefault(File.Exists);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return "python";
    }

    private static string FindProjectRoot(string start)
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "server.py")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return start;
    }
}
