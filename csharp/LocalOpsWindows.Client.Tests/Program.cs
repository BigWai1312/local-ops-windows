using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Net.Sockets;
using LocalOpsWindows.Client;

if (args.Contains("--idle-worker"))
{
    Console.WriteLine("ready");
    await Task.Delay(Timeout.Infinite);
    return;
}

if (args.Contains("--offscreen-ui"))
{
    try { await UiLayoutTests.RunAsync(); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--port-worker"))
{
    var address = args.Contains("--ipv6") ? IPAddress.IPv6Loopback : IPAddress.Loopback;
    using var listener = new TcpListener(address, 0);
    listener.Start();
    Console.WriteLine(((IPEndPoint)listener.LocalEndpoint).Port);
    await Task.Delay(Timeout.Infinite);
    return;
}

var checks = new List<(string Name, Func<Task> Run)>
{
    ("installed data root follows the selected directory", () => { TestStorageRoot(); return Task.CompletedTask; }),
    ("GitHub updates, download integrity and resource rollback", ReleaseUpdaterTests.RunAsync),
    ("state contract labels", () =>
    {
        TestStateContract();
        TestActualPorts();
        return Task.CompletedTask;
    }),
    ("monitor baselines, bounded history and task notifications", () =>
    {
        TestMonitorSession();
        return Task.CompletedTask;
    }),
    ("api client rejects backend errors", TestApiError),
    ("api client reads state", TestApiState),
    ("backend discovery follows port changes without stopping shared backend", TestBackendDiscovery),
    ("backend identity discovery avoids a full service scan", TestLightweightDiscovery),
    ("legacy backend discovery allows a slow state scan", TestSlowLegacyDiscovery),
    ("failed mutations are never replayed", TestMutationFailure),
    ("shutdown cleans only the owned startup process", TestOwnedStartupDisposal),
    ("port conflict stop verifies identity and releases fixture ports", TestPortConflictStop),
    ("H5 layout, aligned controls and accessible themes without desktop windows", TestOffscreenUi),
    ("WPF input text remains visible", TestInputLayout),
};

static void TestActualPorts()
{
    var app = new AppSnapshot
    {
        Port = 3000, Ports = [5173, 5173, -1, 70000], Running = true, Listening = false,
        OpenHosts = new() { ["5173"] = "localhost" },
    };
    Assert(app.PortMismatch && app.CanOpen && app.OpenUrl == "http://localhost:5173/",
        "mismatched configuration opens actual IPv6-compatible host");
    Assert(app.PortLabel.Contains("5173") && app.PortLabel.Contains("3000"), "port discrepancy is visible");
    app.OpenHosts["5173"] = "::1";
    Assert(app.OpenUrl == "http://[::1]:5173/", "IPv6 literal is bracketed");
    app.OpenHosts["5173"] = "remote.example";
    Assert(app.OpenUrl == "http://127.0.0.1:5173/", "untrusted host never leaves loopback");
    app.Ports.Clear();
    Assert(!app.CanOpen && app.StatusLabel == "等待端口", "waiting service is not openable");
    app.Running = false;
    Assert(!app.CanOpen, "stopped configured card must not open an unrelated listener");
    app.Kind = "task";
    app.LastExit = new LastExitSnapshot { Code = 1, DurationSec = 25 };
    Assert(app.StatusLabel == "失败" && app.ToggleLabel == "运行" && app.UptimeLabel.Contains("00:25"),
        "legacy task result has exit code and duration");
    Assert(AppSnapshot.FormatDuration(90061) == "1.01:01:01", "multi-day duration must not wrap");
}

static void TestStorageRoot()
{
    var root = Path.Combine(Path.GetTempPath(), "localops-storage-" + Guid.NewGuid().ToString("N"));
    var version = Path.Combine(root, "versions", "2.1.1-fixture");
    Directory.CreateDirectory(version);
    try
    {
        File.WriteAllText(Path.Combine(root, "installation.json"), """{"product":"LocalOpsWindows"}""");
        Assert(ClientStorage.ResolveRoot(version) == root,
            "versioned installs keep mutable data beside the chosen installation root");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static void TestMonitorSession()
{
    var monitor = new MonitorSession();
    ServiceSnapshot Service(string key, int port) => new() { Key = key, InstanceKey = key, Port = port, Pid = port, Group = "mine", Name = key, Cpu = 8, Mem = 2 };
    DashboardState Snapshot(params ServiceSnapshot[] services) => new() { Services = services.ToList() };
    var first = Service("first", 20001);
    var second = Service("second", 20002);
    monitor.Observe(Snapshot(first));
    Assert(!monitor.Events.Any() && !monitor.Discoveries.Any(), "first observation establishes a silent baseline");
    monitor.Observe(Snapshot(first, second));
    Assert(monitor.Discoveries.Single().Key == "second" && monitor.Events.Count == 1, "new service is actionable");
    monitor.Dismiss(second);
    monitor.Observe(Snapshot(first, second));
    Assert(!monitor.Discoveries.Any(), "dismissed discovery does not recur");
    monitor.ResetBaseline();
    var count = monitor.Events.Count;
    monitor.Observe(Snapshot(second));
    Assert(monitor.Events.Count == count && !monitor.Discoveries.Any(), "resume is silent");
    for (var i = 0; i < 160; i++) monitor.Observe(Snapshot(second, Service($"fixture-{i}", 30000 + i)));
    Assert(monitor.Events.Count == 50 && monitor.History["second"].Count == 120
        && monitor.History.Count == 2, "history and events do not grow unbounded");
    var degraded = Snapshot();
    using var reason = JsonDocument.Parse("""{"component":"services"}""");
    degraded.DegradedReasons.Add(reason.RootElement.Clone());
    monitor.Observe(degraded);
    count = monitor.Events.Count;
    monitor.Observe(Snapshot(first));
    Assert(monitor.Events.Count == count, "a failed scanner is not interpreted as mass service exit");
    var tracker = new TaskCompletionTracker();
    var task = new AppSnapshot { Id = "task", Kind = "task", Name = "fixture" };
    Assert(tracker.Observe([task]).Count == 0, "notification baseline is silent");
    task.LastExit = new() { Code = 0, Status = "succeeded", DurationSec = 3 };
    Assert(tracker.Observe([task]).Count == 1, "fast task completing between polls is detected");
    Assert(tracker.Observe([task]).Count == 0, "same completion never repeats");
    task.LastExit.Status = "stopped";
    Assert(tracker.Observe([task]).Count == 0, "manual stop does not notify twice");
    tracker.Reset();
    Assert(tracker.Observe([task]).Count == 0, "reconnect suppresses stale completions");
    Assert(MainWindow.TrendPoints([0, 200, double.NaN]).All(p => p.Y is >= 0 and <= 40), "invalid samples cannot escape graph");
}

foreach (var check in checks)
{
    await check.Run();
    Console.WriteLine($"PASS {check.Name}");
}

static async Task TestOffscreenUi()
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
    };
    if (Path.GetFileNameWithoutExtension(start.FileName) == "dotnet")
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--offscreen-ui");
    using var worker = Process.Start(start)!;
    var output = worker.StandardOutput.ReadToEndAsync();
    var error = worker.StandardError.ReadToEndAsync();
    try
    {
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert(worker.ExitCode == 0, $"offscreen UI worker: {await output}\n{await error}");
        Console.Write(await output);
    }
    finally
    {
        if (!worker.HasExited) worker.Kill(entireProcessTree: false);
    }
}

static void TestStateContract()
{
    const string json = """
    {
      "apps": [
        {
          "id": "a1b2c3d4",
          "name": "fixture-service",
          "command": "python -m http.server",
          "kind": "service",
          "running": false,
          "port": 18080,
          "portOccupied": true,
          "lastExit": { "status": "failed", "at": 0 }
        }
      ],
      "services": [
        {
          "key": "fixture:18080",
          "instanceKey": "fixture:18080",
          "name": "fixture",
          "port": 18080,
          "group": "mine",
          "cpu": 1.5,
          "mem": 4.25,
          "origin": { "label": "已登记项目" }
        }
      ],
      "watched": [
        { "pid": 42, "name": "fixture", "cmd": "fixture", "keyword": "fixture" }
      ],
      "watchedKeywords": ["fixture"]
    }
    """;
    var state = JsonSerializer.Deserialize<DashboardState>(json)
        ?? throw new InvalidOperationException("state did not deserialize");
    Assert(state.Apps.Count == 1, "app count");
    Assert(state.Apps[0].PortLabel == "端口 18080 被占用", "port warning label");
    Assert(state.Apps[0].StatusLabel == "外部占用", "conflict does not claim the service is stopped");
    Assert(state.Apps[0].ToggleLabel == "处理占用", "conflict opens inspection instead of attempting start");
    Assert(state.Services[0].MemoryLabel == "4.3%", "service memory label");
    Assert(state.Services[0].OriginLabel == "已登记项目", "origin label");
    Assert(state.Apps[0].ToggleGlyph == "\uE7BA", "conflict uses warning icon");
    state.Apps[0].PortOccupied = false;
    Assert(state.Apps[0].StatusLabel == "失败", "last exit status without a conflict");
    Assert(state.Apps[0].ToggleGlyph == "\uE768", "stopped app uses start icon");
    Assert(state.Services[0].PinGlyph == "\uE718", "unpinned service uses pin icon");
    Assert(state.Services[0].VisibilityGlyph == "\uE7B3", "visible service uses hide icon");
    Assert(state.Services[0].PromoteGlyph == "\uE710", "unpromoted service uses add icon");
    Assert(state.WatchedKeywords.Count == 1 && state.Watched.Count == 1, "watch contract");
    state.Apps[0].Running = true;
    state.Services[0].Pinned = true;
    state.Services[0].Hidden = true;
    state.Services[0].Promoted = true;
    Assert(state.Apps[0].ToggleGlyph == "\uE71A", "running app uses stop icon");
    Assert(state.Services[0].PinGlyph == "\uE77A", "pinned service uses unpin icon");
    Assert(state.Services[0].VisibilityGlyph == "\uE890", "hidden service uses view icon");
    Assert(state.Services[0].PromoteGlyph == "\uE73E", "promoted service uses check icon");
}

static async Task TestApiError()
{
    using var api = new ApiClient(9600, new StubHandler("""{"ok":false,"error":"安全拒绝"}"""));
    try
    {
        using var response = await api.PostAsync("api/apps/a1b2c3d4/stop", new { }, CancellationToken.None);
        throw new InvalidOperationException("expected backend error");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("安全拒绝", StringComparison.Ordinal))
    {
    }
}

static async Task TestApiState()
{
    using var api = new ApiClient(9600, new StubHandler("""{"apps":[],"services":[],"watched":[],"watchedKeywords":[]}"""));
    var state = await api.GetStateAsync(CancellationToken.None);
    Assert(state.Apps.Count == 0 && state.Services.Count == 0, "empty state");
    foreach (long? code in new long?[] { null, 0, 7, 130, -1, int.MinValue, int.MaxValue, 3221225786, uint.MaxValue })
    {
        var json = JsonSerializer.Serialize(new { apps = new[] { new { lastExit = new { code } } } });
        using var historicalApi = new ApiClient(9600, new StubHandler(json));
        var historical = await historicalApi.GetStateAsync(CancellationToken.None);
        var app = historical.Apps.Single();
        Assert(app.LastExit!.Code == code, "exit code is preserved without truncation");
        Assert(app.LastExit.EffectiveStatus == (code is null ? null : code == 0 ? "succeeded" : "failed"),
            "signed and unsigned Windows exit codes retain their status");
        if (code is not null)
            Assert(app.UptimeLabel.Contains(code.Value.ToString()), "full exit code is visible");
    }
}

static async Task TestBackendDiscovery()
{
    var port = 9607;
    var writes = 0;
    using var handler = new CallbackHandler(request =>
    {
        if (request.Method != HttpMethod.Get) writes++;
        var uri = request.RequestUri!;
        // A matching health response without a valid state must not be attached.
        if (uri.Port == 9602 && uri.AbsolutePath == "/api/health"
            || uri.Port == port && uri.AbsolutePath == "/api/health")
        {
            return JsonResponse("""{"platform":"windows","productName":"本地运维台 Windows"}""");
        }
        if (uri.Port == 9602) return JsonResponse("""{"consolePid":9999999999999999}""");
        return uri.Port == port
            ? JsonResponse("""{"consolePid":1234}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    });
    await using var supervisor = new BackendSupervisor(handler);
    Assert(await supervisor.StartAsync(CancellationToken.None) == 9607, "initial discovery");
    Assert(!supervisor.OwnsProcess, "attached backend is not owned");
    port = 11000;
    var ports = await Task.WhenAll(supervisor.StartAsync(CancellationToken.None),
        supervisor.StartAsync(CancellationToken.None));
    Assert(ports.All(value => value == 11000), "rediscovery follows changed port into fallback range");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    try
    {
        await supervisor.StartAsync(canceled.Token);
        throw new InvalidOperationException("expected cancellation");
    }
    catch (OperationCanceledException) { }
    await supervisor.DisposeAsync();
    Assert(writes == 0, "discovery and disposal must never stop an attached backend");
}

static async Task TestLightweightDiscovery()
{
    using var handler = new CallbackHandler(request =>
    {
        Assert(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/health",
            "modern discovery must not trigger a full scan or write request");
        return request.RequestUri!.Port switch
        {
            9600 => JsonResponse("""{"platform":"windows","productName":"本地运维台 Windows","consolePid":9999999999999999}"""),
            9601 => JsonResponse("""{"platform":"windows","productName":"other","consolePid":1234}"""),
            9602 => JsonResponse("""{"platform":"windows","productName":"本地运维台 Windows","consolePid":1234}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    });
    await using var supervisor = new BackendSupervisor(handler);
    Assert(await supervisor.StartAsync(CancellationToken.None) == 9602,
        "valid health identity is accepted while invalid identities are rejected");
    Assert(!supervisor.OwnsProcess, "fast discovery does not claim the backend process");
}

static async Task TestSlowLegacyDiscovery()
{
    var stateReads = 0;
    using var handler = new AsyncCallbackHandler(async (request, cancellationToken) =>
    {
        Assert(request.Method == HttpMethod.Get, "legacy discovery only reads state");
        if (request.RequestUri!.AbsolutePath == "/api/health")
            return JsonResponse("""{"platform":"windows","productName":"本地运维台 Windows"}""");
        stateReads++;
        await Task.Delay(1200, cancellationToken);
        return JsonResponse("""{"consolePid":1234}""");
    });
    await using var supervisor = new BackendSupervisor(handler);
    Assert(await supervisor.StartAsync(CancellationToken.None) == 9600 && stateReads == 1,
        "a scan slower than 900ms must connect without repeated discovery");
    using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    try
    {
        await supervisor.StartAsync(canceled.Token);
        throw new InvalidOperationException("expected cancellation during legacy scan");
    }
    catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
}

static async Task TestMutationFailure()
{
    var requests = 0;
    using var handler = new CallbackHandler(_ =>
    {
        requests++;
        throw new HttpRequestException("fixture disconnected");
    });
    using var api = new ApiClient(9600, handler);
    try
    {
        using var response = await api.PostAsync("api/apps/fixture/start", new { }, CancellationToken.None);
        throw new InvalidOperationException("expected transport failure");
    }
    catch (HttpRequestException) { }
    Assert(requests == 1, "a failed start must not be replayed");
}

static async Task TestOwnedStartupDisposal()
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
    };
    if (System.IO.Path.GetFileNameWithoutExtension(start.FileName) == "dotnet")
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--idle-worker");
    using var worker = Process.Start(start)!;
    using var observer = Process.GetProcessById(worker.Id);
    try
    {
        Assert(await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "ready",
            "fixture worker is running");
        var exited = observer.WaitForExitAsync();
        await using var supervisor = new BackendSupervisor(new CallbackHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        typeof(BackendSupervisor).GetField("_ownedProcess", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(supervisor, worker);
        await supervisor.DisposeAsync();
        await exited.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(observer.HasExited, "closing before readiness cleans up the exact owned worker");
    }
    finally
    {
        if (!observer.HasExited)
        {
            observer.Kill(entireProcessTree: false);
            await observer.WaitForExitAsync();
        }
    }
}

static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json")
};

static async Task TestPortConflictStop()
{
    foreach (var ipv6 in new[] { false, true })
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        if (System.IO.Path.GetFileNameWithoutExtension(start.FileName) == "dotnet")
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--port-worker");
        if (ipv6) start.ArgumentList.Add("--ipv6");
        using var worker = Process.Start(start)!;
        try
        {
            var port = int.Parse((await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
            var target = PortProcess.Inspect(port);
            Assert(target.Pid == worker.Id && target.Created > 0 && !string.IsNullOrWhiteSpace(target.ImagePath),
                "fresh TCP owner includes process creation identity and executable");
            void Reject(Action action, string name)
            {
                var rejected = false;
                try { action(); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected && !worker.HasExited, name);
            }
            Reject(() => PortProcess.Inspect(port, Environment.ProcessId), "stale PID is rejected without killing fixture");
            Reject(() => PortProcess.Inspect(port, worker.Id, target.Created + 1), "reused PID identity is rejected");
            Reject(() => (target with { Created = target.Created + 1 }).Stop(), "stop rechecks creation time");
            Reject(() => (target with { ImagePath = "wrong.exe" }).Stop(), "stop rechecks image path");
            Reject(() => PortProcess.Inspect(9600), "management ports cannot be targeted");
            Reject(() => (target with { Port = 9600 }).Stop(), "direct stop rejects management ports");
            Reject(() => PortProcess.Inspect(11000), "fallback management ports cannot be targeted");
            Reject(() => (target with { Port = 11000 }).Stop(), "direct stop rejects fallback management ports");
            target.Stop();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert(worker.HasExited, "only our fixture process was terminated");
            using var replacement = new TcpListener(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, port);
            replacement.Start();
            var refusedSelf = false;
            try { PortProcess.Inspect(port); }
            catch (InvalidOperationException) { refusedSelf = true; }
            Assert(refusedSelf, "client itself cannot be targeted after port reuse");
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: false);
                await worker.WaitForExitAsync();
            }
        }
    }
}

static Task TestInputLayout()
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            // Keep real WPF resources, but never run product startup or acquire its live mutex in tests.
            app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
                typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly)!);
            TestPortConflictDialog();
            var window = new MainWindow(["--demo"]);
            Assert(window.Icon is not null, "window has product icon");
            Assert(System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "local-ops.ico")),
                "tray icon is copied to output");
            var brandImage = Application.GetResourceStream(
                new Uri("pack://application:,,,/LocalOpsWindows.Client;component/Assets/local-ops.png"));
            Assert(brandImage?.Stream.CanRead == true, "brand image is compiled into resources");
            brandImage?.Stream.Dispose();
            var uiFont = (System.Windows.Media.FontFamily)app.FindResource("UiFontFamily");
            Assert(window.FontFamily.Equals(uiFont), "main window uses UI font");
            Assert(((TextBlock)window.FindName("PageTitleText")).FontFamily.Equals(uiFont),
                "page text inherits UI font");
            Assert(((TextBox)window.FindName("LogTextBox")).FontFamily.Equals(uiFont),
                "log text inherits UI font");
            var search = (TextBox)window.FindName("SearchBox");
            var keyword = (TextBox)window.FindName("WatchKeywordBox");
            var editor = new TextBox { Style = (Style)app.FindResource("TextInput") };
            foreach (var input in new[] { search, keyword, editor })
            {
                if (input.Parent is Panel parent) parent.Children.Remove(input);
                var container = new Grid { Width = 480 };
                container.Children.Add(input);
                void Layout()
                {
                    container.Measure(new Size(480, double.PositiveInfinity));
                    container.Arrange(new Rect(container.DesiredSize));
                    container.UpdateLayout();
                }

                input.Clear();
                Layout();
                Assert(input.FontFamily.Equals(uiFont), $"{input.Name}: input uses UI font");
                var placeholder = (TextBlock)input.Template.FindName("Placeholder", input);
                Assert(placeholder.Visibility == Visibility.Visible, "empty input shows placeholder");
                input.SelectedText = "Atlas 本地服务 5180";
                Layout();
                var host = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
                var firstCharacter = input.GetRectFromCharacterIndex(0);
                Assert(host.ViewportHeight >= input.FontSize,
                    $"{input.Name}: text viewport {host.ViewportHeight:0.0} must fit font {input.FontSize}");
                Assert(!firstCharacter.IsEmpty && firstCharacter.Height > 0
                    && firstCharacter.Bottom <= input.ActualHeight,
                    $"{input.Name}: character must fit vertically");
                Assert(firstCharacter.Left <= input.Padding.Left + input.BorderThickness.Left + 4,
                    $"{input.Name}: padding must apply once");
                Assert(placeholder.Visibility == Visibility.Collapsed, "input hides placeholder");
                input.SelectAll();
                input.SelectedText = "中文搜索";
                Layout();
                Assert(input.Text == "中文搜索", "selected text can be replaced");
                input.Clear();
                Layout();
                Assert(placeholder.Visibility == Visibility.Visible, "clearing restores placeholder");
            }
            TestWindowRecovery();
            TestWindowIdlePolling();
            TestProjectEditor();
            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
        finally
        {
            Application.Current?.Shutdown();
        }
    }) { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task;
}

static void TestProjectEditor()
{
    var creates = 0;
    var updates = 0;
    var uploads = 0;
    using var api = new ApiClient(9600, new CallbackHandler(request =>
    {
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/apps")
        {
            creates++;
            return JsonResponse("""{"id":"aabb1122","name":"fixture"}""");
        }
        if (request.Method == HttpMethod.Put)
        {
            updates++;
            return JsonResponse("""{"id":"aabb1122","name":"fixture"}""");
        }
        if (request.RequestUri!.AbsolutePath.EndsWith("/icon"))
        {
            uploads++;
            return uploads == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                { Content = new StringContent("""{"ok":false,"error":"fixture upload failed"}""") }
                : JsonResponse("""{"ok":true}""");
        }
        throw new InvalidOperationException("Unexpected editor request");
    }));
    var editor = new ProjectEditor(api, null, "service", CancellationToken.None);
    editor.NameInput.Text = "Fixture 服务";
    editor.CommandInput.Text = "fixture run";
    editor.PortInput.Text = "12345";
    editor.CandidatesList.ItemsSource = new[]
    {
        new ProjectCandidate { Label = "开发", Command = "npm run dev", Port = 5173 },
        new ProjectCandidate { Label = "构建", Command = "npm run build", Kind = "task" },
    };
    editor.CandidatesList.SelectedIndex = 0;
    Assert(editor.CommandInput.Text == "npm run dev" && editor.PortInput.Text == "12345",
        "selecting a candidate keeps a manually specified port");
    editor.PortInput.Clear();
    editor.CandidatesList.SelectedIndex = -1;
    editor.CandidatesList.SelectedIndex = 0;
    Assert(editor.PortInput.Text == "5173", "candidate can fill an empty port");
    editor.CandidatesList.SelectedIndex = 1;
    Assert(editor.TaskMode.IsChecked == true && editor.PortField.Visibility == Visibility.Collapsed,
        "task candidate updates kind and hides port");
    typeof(ProjectEditor).GetField("_pendingIcon", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(editor, new byte[] { 1, 2, 3 });
    var failed = false;
    try { editor.SaveAsync().GetAwaiter().GetResult(); }
    catch (InvalidOperationException ex) { failed = ex.Message.Contains("fixture upload failed"); }
    Assert(failed && editor.Saved && creates == 1, "icon failure preserves the saved project identity");
    editor.SaveAsync().GetAwaiter().GetResult();
    Assert(creates == 1 && updates == 1 && uploads == 2, "icon retry updates the same project");
    var rejected = false;
    try { api.GetIconAsync("https://remote.example/image.png", CancellationToken.None).GetAwaiter().GetResult(); }
    catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "icon loader rejects remote paths before issuing a request");
    editor.Close();
    Console.WriteLine("PASS project candidate selection, partial save retry, and icon URL boundary");
}

static void TestPortConflictDialog()
{
    var type = typeof(MainWindow).Assembly.GetType("LocalOpsWindows.Client.PortConflictDialog")!;
    var flags = BindingFlags.Static | BindingFlags.NonPublic;
    var target = new PortProcess(12345, 18080, DateTime.UtcNow.ToFileTimeUtc(), @"C:\Projects\Fixture\worker.exe");
    var dialog = (Window)type.GetMethod("Create", flags)!.Invoke(null, new object?[] { target, "Fixture", null })!;
    var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
    var buttons = (StackPanel)panel.Children[panel.Children.Count - 1];
    Assert(buttons.Children.OfType<Button>().Single(button => button.IsDefault).IsCancel,
        "Enter defaults to cancel, never to force stop");
    Assert(panel.Children.OfType<TextBox>().Single().Text == target.ImagePath,
        "confirmation exposes the complete executable path");
    var helperResult = (Task<int>)type.GetMethod("RunHelperAsync", flags)!.Invoke(null,
        new object[] { new[] { "--stop-port-process", "invalid" } })!;
    Assert(helperResult.GetAwaiter().GetResult() == 1, "invalid helper arguments fail without an action");
    Console.WriteLine("PASS port conflict confirmation defaults and helper arguments");
}

static void TestWindowIdlePolling()
{
    var requests = 0;
    var cpu = 1.0;
    using var api = new ApiClient(9600, new CallbackHandler(_ =>
    {
        requests++;
        return JsonResponse(JsonSerializer.Serialize(new
        {
            apps = new[] { new { id = "fixture-app", name = "Fixture App", kind = "service" } },
            services = Enumerable.Range(0, 400).Select(index => new
            {
                key = $"fixture-{index:D3}", instanceKey = $"instance-{index:D3}",
                name = $"Fixture {index:D3}", port = 20000 + index, pid = index + 1000, cpu,
            }),
            watched = Array.Empty<object>(), watchedKeywords = new[] { "fixture" },
        }));
    }));
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    var window = new MainWindow(["--demo"]);
    var timer = (System.Windows.Threading.DispatcherTimer)typeof(MainWindow)
        .GetField("_pollTimer", flags)!.GetValue(window)!;
    void Invoke(string name) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, null);
    void Refresh() => ((Task)typeof(MainWindow).GetMethod("RefreshStateAsync", flags)!
        .Invoke(window, null)!).GetAwaiter().GetResult();
    void Navigate(string name) => ((Button)window.FindName(name))
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    try
    {
        window.Show();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert(window.IsLoaded, "fixture window must finish loading before testing polling");
        typeof(MainWindow).GetField("_api", flags)!.SetValue(window, api);
        Invoke("UpdatePollingVisibility");
        Assert(timer.IsEnabled && timer.Interval == TimeSpan.FromSeconds(5),
            $"visible polling is five seconds (visible={window.IsVisible}, state={window.WindowState})");
        var apps = (ListView)window.FindName("AppsList");
        var services = (ListView)window.FindName("ServicesGrid");
        apps.SelectedIndex = 0;
        var inactiveServices = services.ItemsSource;
        Refresh();
        Assert(ReferenceEquals(inactiveServices, services.ItemsSource), "inactive service list is not rebuilt");
        Assert(apps.SelectedItem is AppSnapshot { Id: "fixture-app" }, "app selection survives refresh");
        Navigate("ServicesNav");
        Assert(services.Items.Count == 400, "switching page renders the latest snapshot");
        services.SelectedIndex = 37;
        cpu = 7.5;
        Refresh();
        Assert(services.SelectedItem is ServiceSnapshot { Key: "fixture-037", Cpu: 7.5 },
            "service identity and new metrics survive refresh");
        var search = (TextBox)window.FindName("SearchBox");
        search.Text = "Fixture 037";
        Assert(services.Items.Count == 1, "search applies to the new list");
        search.Clear();
        Navigate("LogsNav");
        var logs = (ComboBox)window.FindName("LogTargetCombo");
        var firstTarget = logs.Items[1];
        Refresh();
        Assert(ReferenceEquals(firstTarget, logs.Items[1]), "unchanged log targets are retained");
        window.Hide();
        var hiddenRequests = requests;
        Assert(!timer.IsEnabled, "hidden window stops the polling timer");
        // Run the dispatcher beyond a poll period to catch queued timer regressions.
        var frame = new System.Windows.Threading.DispatcherFrame();
        var deadline = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5.2) };
        deadline.Tick += (_, _) => { deadline.Stop(); frame.Continue = false; };
        deadline.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Assert(requests == hiddenRequests, "hidden window issues no state or log reads");
        window.Show();
        Assert(timer.IsEnabled && requests > hiddenRequests, "restoring immediately fetches fresh state");
        window.WindowState = WindowState.Minimized;
        Assert(!timer.IsEnabled, "minimized taskbar window also stops polling");
        window.WindowState = WindowState.Normal;
        Assert(timer.IsEnabled, "restoring a minimized window resumes polling");
        Console.WriteLine("PASS WPF idle polling, navigation, selection and log targets");
    }
    finally
    {
        timer.Stop();
        window.Hide();
    }
}

static void TestWindowRecovery()
{
    var port = 9607;
    var failStartup = true;
    var disconnected = false;
    var connectedPorts = new List<int>();
    var requests = new List<HttpMethod>();
    using var probe = new CallbackHandler(request =>
    {
        if (failStartup) throw new InvalidOperationException("fixture startup failure");
        if (request.RequestUri!.Port != port) return new HttpResponseMessage(HttpStatusCode.NotFound);
        return request.RequestUri.AbsolutePath == "/api/health"
            ? JsonResponse("""{"platform":"windows","productName":"本地运维台 Windows"}""")
            : JsonResponse("""{"consolePid":1234}""");
    });
    var backend = new BackendSupervisor(probe);
    var window = new MainWindow([], backend, discoveredPort =>
    {
        connectedPorts.Add(discoveredPort);
        return new ApiClient(discoveredPort, new CallbackHandler(request =>
        {
            requests.Add(request.Method);
            if (disconnected) throw new HttpRequestException("fixture disconnected");
            return JsonResponse($$"""{"consolePort":{{port}},"apps":[],"services":[],"watched":[]}""");
        }));
    });
    var flags = BindingFlags.NonPublic | BindingFlags.Instance;
    var apiField = typeof(MainWindow).GetField("_api", flags)!;
    void Refresh() => ((Task)typeof(MainWindow).GetMethod("RefreshStateAsync", flags)!
        .Invoke(window, null)!).GetAwaiter().GetResult();
    void AssertDisconnected()
    {
        Assert(apiField.GetValue(window) is null, "disconnect discards stale API client");
        Assert(((TextBlock)window.FindName("BackendStatusText")).Text == "正在自动重连",
            "disconnect shows recovery status");
        Assert(((TextBlock)window.FindName("LastSyncText")).Text.Contains("上次同步"),
            "stale values are identified");
    }
    try
    {
        Refresh();
        AssertDisconnected();
        failStartup = false;
        Refresh();
        Assert(((TextBlock)window.FindName("BackendStatusText")).Text == "已连接",
            "initial startup failure can recover");
        disconnected = true;
        Refresh();
        AssertDisconnected();
        disconnected = false;
        port = 9609;
        Refresh();
        Assert(((TextBlock)window.FindName("BackendStatusText")).Text == "已连接",
            "runtime disconnect can recover");
        Assert(connectedPorts.SequenceEqual(new[] { 9607, 9609 }), "window recreates API on new port");
        Assert(requests.All(method => method == HttpMethod.Get), "recovery only reads state");
    }
    finally
    {
        (apiField.GetValue(window) as ApiClient)?.Dispose();
        backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

static void Assert(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException($"assertion failed: {name}");
    }
}

sealed class StubHandler : HttpMessageHandler
{
    private readonly string _body;

    public StubHandler(string body) => _body = body;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        });
    }
}

sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}

sealed class AsyncCallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => respond(request, cancellationToken);
}
