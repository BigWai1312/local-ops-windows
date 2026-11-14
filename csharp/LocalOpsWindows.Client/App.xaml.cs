using System.Threading;
using System.Windows;

namespace LocalOpsWindows.Client;

public partial class App : System.Windows.Application
{
    public App()
    {
        // Keep the small dashboard independent of GPU and virtual-display compositing.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }

    private const string MutexName = "LocalOpsWindows.CSharpClient";
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showRegistration;
    private bool _started;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        if (e.Args.Contains(PortConflictDialog.HelperSwitch, StringComparer.Ordinal))
        {
            Shutdown(await PortConflictDialog.RunHelperAsync(e.Args));
            return;
        }
        var demoMode = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var instanceName = demoMode ? MutexName + ".Demo" : MutexName;
        var showEventName = instanceName + ".Show";
        _instanceMutex = new Mutex(true, instanceName, out var created);
        if (!created)
        {
            TryFocusExistingWindow(showEventName);
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, showEventName);
        var window = new MainWindow(e.Args);
        MainWindow = window;
        _started = true;
        window.Show();
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
        {
            if (!_started) return;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_started) return;
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            }));
        }, null, Timeout.Infinite, false);
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _showRegistration?.Unregister(null);
        try { _showEvent?.Dispose(); } catch (ObjectDisposedException) { }
        if (_started)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _started = false;
        _instanceMutex?.Dispose();
    }

    private static void TryFocusExistingWindow(string showEventName)
    {
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(showEventName);
            showEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The existing process may still be creating its window.
        }

        System.Windows.Application.Current.Shutdown();
    }
}
