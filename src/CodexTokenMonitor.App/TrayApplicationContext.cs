using System.Diagnostics;
using CodexTokenMonitor.Core.Metadata;
using CodexTokenMonitor.Core.Monitoring;
using CodexTokenMonitor.Core.Parsing;
using CodexTokenMonitor.Core.Storage;

namespace CodexTokenMonitor.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ForcedExitDelay = TimeSpan.FromSeconds(5);

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly CodexSessionMonitor _monitor;
    private readonly WebDashboardServer _webServer;
    private RegisteredWaitHandle? _activationRegistration;
    private bool _stopping;
    private int _exitWatchdogStarted;

    private TrayApplicationContext(
        NotifyIcon notifyIcon,
        Icon icon,
        CodexSessionMonitor monitor,
        WebDashboardServer webServer)
    {
        _notifyIcon = notifyIcon;
        _icon = icon;
        _monitor = monitor;
        _webServer = webServer;
    }

    public static TrayApplicationContext Create(string[] args, EventWaitHandle activationEvent)
    {
        var paths = AppPaths.Resolve();
        var icon = TrayIconFactory.Create();
        var notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "Codex Token Monitor",
            Visible = true
        };
        var database = new MonitorDatabase(paths.DatabasePath);
        var health = new MonitorHealthTracker();
        var monitor = new CodexSessionMonitor(
            paths.CodexHome,
            database,
            new RolloutFileProcessor(new IncrementalJsonlReader(), new RolloutEventParser()),
            new CodexMetadataReader(),
            health);
        monitor.StartAsync().GetAwaiter().GetResult();
        var webServer = new WebDashboardServer(database, monitor, health, new StartupRegistrationService());
        webServer.StartAsync().GetAwaiter().GetResult();

        var context = new TrayApplicationContext(notifyIcon, icon, monitor, webServer);
        webServer.UnexpectedlyStopped += context.OnWebServerUnexpectedlyStopped;
        context.BuildMenu();
        context._activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            activationEvent,
            static (state, timedOut) =>
            {
                if (!timedOut)
                {
                    ((TrayApplicationContext)state!).OpenDashboard();
                }
            },
            context,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);
        notifyIcon.DoubleClick += (_, _) => context.OpenDashboard();
        if (!args.Contains("--start-hidden", StringComparer.OrdinalIgnoreCase) &&
            !args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
        {
            context.OpenDashboard();
        }

        return context;
    }

    protected override void ExitThreadCore()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _activationRegistration?.Unregister(null);
        _webServer.UnexpectedlyStopped -= OnWebServerUnexpectedlyStopped;
        _notifyIcon.Visible = false;
        try
        {
            Task.WhenAll(_webServer.StopAsync(), _monitor.StopAsync())
                .WaitAsync(ShutdownTimeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException)
        {
            // Exit must release the single-instance lock even if a background scan is slow to cancel.
        }

        _notifyIcon.Dispose();
        _icon.Dispose();
        base.ExitThreadCore();
    }

    private void BuildMenu()
    {
        var open = new ToolStripMenuItem("打开监控面板");
        open.Click += (_, _) => OpenDashboard();
        var refresh = new ToolStripMenuItem("立即刷新");
        refresh.Click += (_, _) => _monitor.RequestRefresh();
        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitFromTray();
        _notifyIcon.ContextMenuStrip = new ContextMenuStrip();
        _notifyIcon.ContextMenuStrip.Items.Add(open);
        _notifyIcon.ContextMenuStrip.Items.Add(refresh);
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add(exit);
    }

    private void ExitFromTray()
    {
        if (Interlocked.Exchange(ref _exitWatchdogStarted, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(ForcedExitDelay).ConfigureAwait(false);
            Environment.Exit(0);
        });
        ExitThread();
    }

    private void OpenDashboard()
    {
        if (_stopping)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(_webServer.Url) { UseShellExecute = true });
    }

    private void OnWebServerUnexpectedlyStopped()
    {
        if (!_stopping)
        {
            Application.Exit();
        }
    }
}
