using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AgentWatch.Models;
using AgentWatch.Services;

namespace AgentWatch;

public partial class MainWindow : Window
{
    private const int MaxRecent = 80;
    private int _recentTotal;

    private readonly MonitorService _service;
    private readonly Settings _settings;
    /// <summary>Mixed list: SessionVm activity cards and running TaskVm cards, grouped by session.</summary>
    private readonly ObservableCollection<object> _runningItems = new();
    private List<TaskVm> _running = new();
    private List<SessionVm> _active = new();
    private readonly ObservableCollection<TaskVm> _recent = new();
    private readonly ObservableCollection<SessionVm> _sessions = new();
    private readonly Dictionary<string, TaskVm> _taskVms = new();
    private readonly Dictionary<string, SessionVm> _sessionVms = new();
    private readonly DispatcherTimer _tick;
    private MonitorSnapshot? _last;
    private bool _forceClose;

    /// <summary>(cards running, sessions needing input)</summary>
    public event Action<int, int>? RunningCountChanged;
    /// <summary>A session just started waiting for the user.</summary>
    public event Action<SessionVm>? InputNeeded;
    private HashSet<string> _prevNeedsInput = new();

    public MainWindow(MonitorService service, Settings settings)
    {
        InitializeComponent();
        _service = service;
        _settings = settings;

        RunningList.ItemsSource = _runningItems;
        RecentList.ItemsSource = _recent;
        SessionList.ItemsSource = _sessions;

        Width = settings.Width;
        Height = settings.Height;
        if (settings.Left is { } l && settings.Top is { } t && IsOnScreen(l, t))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        Topmost = settings.AlwaysOnTop;

        TopBox.IsChecked = settings.AlwaysOnTop;
        NotifyBox.IsChecked = settings.Notify;
        ShortBox.IsChecked = settings.ShowShortCommands;
        TopBox.Click += (_, _) => { _settings.AlwaysOnTop = Topmost = TopBox.IsChecked == true; _settings.Save(); };
        NotifyBox.Click += (_, _) => { _settings.Notify = NotifyBox.IsChecked == true; _settings.Save(); };
        ShortBox.Click += (_, _) => { _settings.ShowShortCommands = ShortBox.IsChecked == true; _settings.Save(); if (_last != null) Apply(_last); };

        _service.Updated += s => Dispatcher.BeginInvoke(() => Apply(s));
        _service.Error += msg => Dispatcher.BeginInvoke(() =>
        {
            ErrorText.Text = $"{DateTime.Now:HH:mm:ss}  {msg}";
            ErrorText.Visibility = Visibility.Visible;
        });

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _tick.Start();
        UpdateHeaders();
    }

    private void Apply(MonitorSnapshot snap)
    {
        _last = snap;
        var cutoff = DateTimeOffset.Now.AddHours(-_settings.RecentHours);

        // De-duplicate by key (last one wins).
        var tasks = new Dictionary<string, TaskSnapshot>();
        foreach (var t in snap.Tasks) tasks[t.Key] = t;

        foreach (var key in _taskVms.Keys.Where(k => !tasks.ContainsKey(k)).ToList())
            _taskVms.Remove(key);

        foreach (var t in tasks.Values)
        {
            snap.Outputs.TryGetValue(t.Key, out var o);
            snap.Estimates.TryGetValue(t.Key, out var e);
            if (_taskVms.TryGetValue(t.Key, out var vm)) vm.Update(t, o, e);
            else
            {
                vm = new TaskVm(t);
                vm.Update(t, o, e);
                _taskVms[t.Key] = vm;
            }
        }

        var running = _taskVms.Values.Where(v => v.IsRunning).OrderBy(v => v.S.StartedAt).ToList();
        var recent = _taskVms.Values
            .Where(v => !v.IsRunning && v.S.EndedAt is { } end && end > cutoff)
            .Where(v => _settings.ShowShortCommands || v.S.IsBackground || v.Elapsed.TotalSeconds >= 5)
            .OrderByDescending(v => v.S.EndedAt)
            .Take(MaxRecent)
            .ToList();
        _recentTotal = recent.Count;
        if (!_settings.RecentShowAll) recent = recent.Take(_settings.RecentShortCount).ToList();
        _running = running;
        Sync(_recent, recent);

        var sessions = new List<SessionVm>();
        var seen = new HashSet<string>();
        foreach (var s in snap.Sessions)
        {
            if (!seen.Add(s.Key)) continue;
            if (_sessionVms.TryGetValue(s.Key, out var vm)) vm.Update(s);
            else _sessionVms[s.Key] = vm = new SessionVm(s);
            sessions.Add(vm);
        }
        foreach (var key in _sessionVms.Keys.Where(k => !seen.Contains(k)).ToList()) _sessionVms.Remove(key);
        Sync(_sessions, sessions.OrderByDescending(v => v.NeedsInput).ThenByDescending(v => v.S.Busy).ThenByDescending(v => v.IsWaiting).ThenBy(v => v.S.Source).ThenBy(v => v.Name).ToList());
        _active = sessions.Where(v => v.S.Busy || v.S.RunningTasks > 0 || v.NeedsInput).ToList();
        var needsInput = sessions.Where(v => v.NeedsInput).ToList();
        foreach (var v in needsInput.Where(v => !_prevNeedsInput.Contains(v.Key)))
            InputNeeded?.Invoke(v);
        _prevNeedsInput = needsInput.Select(v => v.Key).ToHashSet();
        foreach (var v in sessions.Except(_active)) v.Fg = null;
        Sync(_runningItems, GroupBySession(_active, _running));

        UpdateHeaders();
    }

    /// <summary>
    /// Sessions stay together, in stable first-seen order. Each session with anything going on gets a header card
    /// (its "foreground lane": the current command, step, or idle state), with its background jobs indented below.
    /// </summary>
    private static List<object> GroupBySession(List<SessionVm> active, List<TaskVm> running)
    {
        var groups = new Dictionary<string, (SessionVm? session, List<TaskVm> tasks)>();
        foreach (var s in active)
            groups[s.Key] = (s, new List<TaskVm>());
        foreach (var t in running.OrderBy(t => t.S.StartedAt))
        {
            if (!groups.TryGetValue(t.SessionKey, out var g))
                groups[t.SessionKey] = g = (null, new List<TaskVm>());
            g.tasks.Add(t);
        }
        var result = new List<object>();
        foreach (var g in groups.OrderBy(kv => SessionColors.Order(kv.Key)).Select(kv => kv.Value))
        {
            var foreground = g.tasks.Where(t => !t.S.IsBackground).ToList();
            var background = g.tasks.Where(t => t.S.IsBackground).ToList();
            if (g.session != null)
            {
                // The latest foreground command lives inside the header; parallel ones are listed under it.
                g.session.Fg = foreground.Count > 0 ? foreground[^1] : null;
                result.Add(g.session);
                foreach (var t in foreground.Take(foreground.Count - 1).Concat(background))
                {
                    t.Indented = true;
                    result.Add(t);
                }
            }
            else
            {
                foreach (var t in foreground.Concat(background))
                {
                    t.Indented = false;
                    result.Add(t);
                }
            }
        }
        return result;
    }

    private static void Sync<T>(ObservableCollection<T> target, IList<T> desired) where T : class
    {
        var want = new HashSet<T>(desired);
        for (int i = target.Count - 1; i >= 0; i--)
            if (!want.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            int existing = target.IndexOf(desired[i]);
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, desired[i]);
        }
    }

    private void Tick()
    {
        foreach (var vm in _running) vm.Tick();
        foreach (var vm in _sessions) vm.Tick();
        // Recent "x minutes ago" texts don't need per-second updates.
        if (DateTime.Now.Second % 15 == 0) foreach (var vm in _recent) vm.Tick();
    }

    private void UpdateHeaders()
    {
        int busy = _sessions.Count(s => s.S.Busy);
        int waiting = _sessions.Count(s => s.IsWaiting);
        int needInput = _sessions.Count(s => s.NeedsInput);
        int total = _runningItems.Count;
        RunningTitle.Text = total > 0 ? $"RUNNING NOW ({total})" : "RUNNING NOW";
        SessionsTitle.Text = $"SESSIONS ({_sessions.Count})";
        bool open = _settings.RecentExpanded;
        RecentTitle.Text = $"{(open ? "▾" : "▸")}  RECENTLY FINISHED ({_recentTotal}, last {_settings.RecentHours:0}h)";
        RecentList.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        int hidden = _recentTotal - _recent.Count;
        RecentMoreButton.Content = _settings.RecentShowAll ? "Show fewer" : $"Show all ({hidden} more)";
        RecentMoreButton.Visibility = open && (hidden > 0 || (_settings.RecentShowAll && _recentTotal > _settings.RecentShortCount))
            ? Visibility.Visible : Visibility.Collapsed;
        NothingRunning.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = _last == null ? "scanning…"
            : (needInput > 0 ? $"✋ {needInput} need{(needInput == 1 ? "s" : "")} your input · " : "")
              + $"{_running.Count} commands · {busy} working, {waiting} waiting, {_sessions.Count - busy - waiting} idle";
        SummaryText.Foreground = needInput > 0 ? Palette.Attention : (System.Windows.Media.Brush)FindResource("SubTextBrush");
        Title = (needInput > 0 ? "✋ " : "") + (total > 0 ? $"({total}) AgentWatch" : "AgentWatch");
        RunningCountChanged?.Invoke(total, needInput);
    }

    // ---- card buttons ----

    private void RecentTitle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _settings.RecentExpanded = !_settings.RecentExpanded;
        _settings.Save();
        UpdateHeaders();
    }

    private void RecentMore_Click(object sender, RoutedEventArgs e)
    {
        _settings.RecentShowAll = !_settings.RecentShowAll;
        _settings.Save();
        if (_last != null) Apply(_last); else UpdateHeaders();
    }

    private static TaskVm? VmOf(object sender) => (sender as FrameworkElement)?.DataContext as TaskVm;

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender)?.S.OutputFile is { } f && File.Exists(f))
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{f}\"") { UseShellExecute = true });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender)?.S.Project is { Length: > 0 } p && Directory.Exists(p))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true });
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender)?.Command is { Length: > 0 } c)
        {
            try { Clipboard.SetText(c); } catch (Exception) { }
        }
    }

    // ---- window lifetime ----

    public void SaveLayout()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Width = Width;
            _settings.Height = Height;
        }
        _settings.Save();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            // Close button hides to tray; exit from the tray menu.
            e.Cancel = true;
            SaveLayout();
            Hide();
            return;
        }
        _tick.Stop();
        base.OnClosing(e);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE).
        int on = 1;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 50 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;
}
