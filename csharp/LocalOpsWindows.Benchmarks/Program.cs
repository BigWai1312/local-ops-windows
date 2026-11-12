using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LocalOpsWindows.Client;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        if (args[0] == "--export-state")
        {
            var factory = typeof(MainWindow).GetMethod("CreateDemoState", BindingFlags.Static | BindingFlags.NonPublic)!;
            File.WriteAllText(args[1], JsonSerializer.Serialize(factory.Invoke(null, null),
                new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault }));
            return;
        }
        var port = int.Parse(args[0]);
        var ready = args[1];
        var app = new App();
        app.InitializeComponent();
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
            typeof(App).GetMethod("OnStartup", hidden | BindingFlags.DeclaredOnly)!);
        // Run the actual window and polling code without singleton, tray, real backend, or user-data writes.
        var window = new MainWindow(["--demo", "--services"]);
        window.Title = "Local Ops - WPF benchmark";
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, async () =>
        {
            try
            {
                typeof(MainWindow).GetField("_api", hidden)!.SetValue(window, new ApiClient(port));
                await (Task)typeof(MainWindow).GetMethod("RefreshStateAsync", hidden)!.Invoke(window, null)!;
                var timer = (DispatcherTimer)typeof(MainWindow).GetField("_pollTimer", hidden)!.GetValue(window)!;
                timer.Start();
                void Status(string phase)
                {
                    window.UpdateLayout();
                    var state = (DashboardState)typeof(MainWindow).GetField("_state", hidden)!.GetValue(window)!;
                    var text = JsonSerializer.Serialize(new {
                        phase, apps = state.Apps.Count, services = state.Services.Count,
                        rendered = ((ListView)window.FindName("ServicesGrid")).Items.Count,
                        width = window.ActualWidth, height = window.ActualHeight,
                        hidden = !window.IsVisible, polling = timer.IsEnabled,
                        pollSeconds = timer.Interval.TotalSeconds,
                        renderTier = System.Windows.Media.RenderCapability.Tier >> 16,
                    });
                    File.WriteAllText(ready + ".tmp", text);
                    File.Move(ready + ".tmp", ready, overwrite: true);
                }
                Status("ready");
                using var control = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
                while (true)
                {
                    var command = await control.GetStringAsync($"http://127.0.0.1:{port}/bench/command");
                    if (command == "exit") { Environment.Exit(0); return; }
                    if (command == "hide") window.Hide();
                    else if (command == "show") window.Show();
                    else throw new InvalidOperationException("Unknown benchmark command");
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    Status(command);
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(ready + ".error", ex.ToString());
                app.Shutdown(1);
            }
        });
        window.Show();
        Dispatcher.Run();
    }
}
