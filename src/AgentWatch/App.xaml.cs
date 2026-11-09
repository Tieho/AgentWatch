using System.Windows;
using AgentWatch.Models;
using AgentWatch.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AgentWatch;

public partial class App : Application
{
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private MonitorService? _service;
    private Settings _settings = new();
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--dump"))
        {
            DiagnosticDump.Run();
            Shutdown();
            return;
        }
        _singleInstance = new Mutex(true, "AgentWatch.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("AgentWatch is already running (check the tray).", "AgentWatch");
            Shutdown();
            return;
        }

        _settings = Settings.Load();
        _service = new MonitorService();
        _window = new MainWindow(_service, _settings);

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "AgentWatch",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Items.Add("Show", null, (_, _) => ShowWindow());
        _tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowWindow(); };
        _tray.BalloonTipClicked += (_, _) => ShowWindow();

        _window.RunningCountChanged += (n, input) =>
        {
            if (_tray == null) return;
            string text = n == 0 ? "AgentWatch: idle" : $"AgentWatch: {n} running";
            if (input > 0) text = $"AgentWatch: {input} waiting for your input";
            _tray.Text = text.Length > 63 ? text[..63] : text;
        };
        _window.InputNeeded += s =>
        {
            if (_tray == null || !_settings.Notify) return;
            string body = $"{s.Name}\n{s.S.WaitingFor}";
            _tray.ShowBalloonTip(8000, "✋ Waiting for your input", body.Length > 250 ? body[..250] : body, Forms.ToolTipIcon.Warning);
        };
        _service.TaskFinished += t => Dispatcher.BeginInvoke(() => OnTaskFinished(t));

        _window.Show();
        _service.Start();
    }

    private void OnTaskFinished(TaskSnapshot t)
    {
        if (_tray == null || !_settings.Notify) return;
        if (t.Duration.TotalSeconds < _settings.NotifyMinSeconds && !t.IsBackground) return;
        string title = t.Status switch
        {
            AgentTaskStatus.Completed => $"✓ Finished in {Fmt.Duration(t.Duration)}",
            AgentTaskStatus.Failed => $"✗ Failed after {Fmt.Duration(t.Duration)}",
            _ => $"■ {t.Status} after {Fmt.Duration(t.Duration)}",
        };
        string text = $"{t.Description}\n{t.SessionName}";
        if (text.Length > 250) text = text[..250];
        _tray.ShowBalloonTip(5000, title, text, t.Status == AgentTaskStatus.Failed ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        _service?.Dispose();
        _service?.Estimator.Save();
        _window?.SaveLayout();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _window?.ForceClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>The app icon at the system's small-icon size (crisp in the notification area).</summary>
    private static Drawing.Icon LoadTrayIcon()
    {
        var res = GetResourceStream(new Uri("pack://application:,,,/AgentWatch.ico"));
        using var stream = res!.Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
