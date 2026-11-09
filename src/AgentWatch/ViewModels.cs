using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using AgentWatch.Models;

namespace AgentWatch;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Palette
{
    public static readonly Brush Claude = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)));
    public static readonly Brush Codex = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0xA3, 0x7F)));
    public static readonly Brush Ok = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0x7A)));
    public static readonly Brush Bad = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x5C, 0x5C)));
    public static readonly Brush Warn = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xB0, 0x4B)));
    public static readonly Brush Neutral = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99)));
    public static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x9C, 0xF5)));
    /// <summary>The agent is blocked on the user.</summary>
    public static readonly Brush Attention = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0x61, 0x7A)));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}

/// <summary>Stable, distinct accent colors per session (assigned in order of first appearance).</summary>
public static class SessionColors
{
    private static readonly Color[] Accents =
    [
        Color.FromRgb(0x5B, 0x9C, 0xF5), // blue
        Color.FromRgb(0xB0, 0x7C, 0xF0), // purple
        Color.FromRgb(0x4C, 0xC2, 0xA0), // teal
        Color.FromRgb(0xE0, 0xA8, 0x4B), // amber
        Color.FromRgb(0xE5, 0x7A, 0xA8), // pink
        Color.FromRgb(0x8F, 0xBF, 0x55), // green
        Color.FromRgb(0x5C, 0xC4, 0xD9), // cyan
        Color.FromRgb(0xD9, 0x82, 0x5B), // orange
    ];
    private static readonly Color Card = Color.FromRgb(0x26, 0x28, 0x2C);
    private static readonly Dictionary<string, (Brush accent, Brush tint)> Assigned = new();

    public static (Brush Accent, Brush Tint) For(string key)
    {
        if (Assigned.TryGetValue(key, out var c)) return c;
        var a = Accents[Assigned.Count % Accents.Length];
        var accent = new SolidColorBrush(a);
        var tint = new SolidColorBrush(Blend(Card, a, 0.09));
        accent.Freeze();
        tint.Freeze();
        return Assigned[key] = (accent, tint);
    }

    /// <summary>Order of first appearance; stable while the app runs.</summary>
    public static int Order(string key)
    {
        For(key);
        int i = 0;
        foreach (var k in Assigned.Keys) { if (k == key) return i; i++; }
        return i;
    }

    private static Color Blend(Color b, Color a, double t) => Color.FromRgb(
        (byte)(b.R + (a.R - b.R) * t), (byte)(b.G + (a.G - b.G) * t), (byte)(b.B + (a.B - b.B) * t));
}

public static class Fmt
{
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }

    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string Ago(DateTimeOffset when)
    {
        var d = DateTimeOffset.Now - when;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        return $"{(int)d.TotalHours}h {d.Minutes}m ago";
    }

    public static string ProjectName(string path) =>
        string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path;
}

public sealed class TaskVm : Observable
{
    public TaskSnapshot S { get; private set; }
    private OutputInfo? _output;
    private Estimate? _estimate;

    public TaskVm(TaskSnapshot s) => S = s;

    public string Key => S.Key;
    public string SessionKey => S.SessionKey.Length > 0 ? S.SessionKey : "name:" + S.SessionName;
    public Brush SessionAccent => SessionColors.For(SessionKey).Accent;
    public Brush SessionTint => SessionColors.For(SessionKey).Tint;
    public bool IsRunning => S.Status == AgentTaskStatus.Running;
    private bool _indented;
    /// <summary>Shown under its session's header card.</summary>
    public bool Indented
    {
        get => _indented;
        set { if (_indented != value) { _indented = value; Raise(nameof(CardMargin)); } }
    }
    public System.Windows.Thickness CardMargin => _indented ? new(26, 0, 0, 6) : new(0, 0, 0, 6);
    public string SourceLabel => S.Source == AgentSource.Claude ? "CLAUDE" : "CODEX";
    public Brush SourceBrush => S.Source == AgentSource.Claude ? Palette.Claude : Palette.Codex;
    public string Header => S.Project.Length > 0 ? $"{S.SessionName}  ·  {Fmt.ProjectName(S.Project)}" : S.SessionName;
    public string Description => S.Description.Length > 0 ? S.Description : CommandShort;
    public string Command => S.Command;
    public string CommandShort
    {
        get
        {
            string c = S.Command.Replace("\r", " ").Replace('\n', ' ');
            return c.Length > 220 ? c[..220] + "…" : c;
        }
    }
    public string Kind => S.IsBackground ? "background" : "foreground";
    public bool HasOutputFile => S.OutputFile != null;
    public bool HasProject => S.Project.Length > 0;

    public TimeSpan Elapsed => (S.EndedAt ?? DateTimeOffset.Now) - S.StartedAt;
    public string ElapsedText => Fmt.Clock(Elapsed);
    public string StartedText => "started " + S.StartedAt.ToLocalTime().ToString("HH:mm:ss");

    public string? LastLine => _output?.LastLine;
    public bool HasLastLine => !string.IsNullOrEmpty(_output?.LastLine);

    // ---- progress ----
    public bool IsIndeterminate => _output?.Percent == null && _estimate == null;
    public double Progress
    {
        get
        {
            if (_output?.Percent is { } p) return Math.Clamp(p, 0, 100);
            if (_estimate is { } e && e.Seconds > 0) return Math.Min(99, Elapsed.TotalSeconds / e.Seconds * 100);
            return 0;
        }
    }
    public bool IsOverrunning => _output?.Percent == null && _estimate is { } e && Elapsed.TotalSeconds > e.Seconds;
    public Brush ProgressBrush => IsOverrunning ? Palette.Warn : SourceBrush;
    public string ProgressText
    {
        get
        {
            if (_output?.Percent is { } p) return $"{p:0}% (from output)";
            if (_estimate is { } e)
            {
                var est = TimeSpan.FromSeconds(e.Seconds);
                var left = est - Elapsed;
                string basis = e.Samples == 1 ? "1 previous run" : $"{e.Samples} previous runs";
                return left > TimeSpan.Zero
                    ? $"~{Fmt.Duration(left)} left  ·  usually {Fmt.Duration(est)} ({basis})"
                    : $"overrunning by {Fmt.Duration(-left)}  ·  usually {Fmt.Duration(est)} ({basis})";
            }
            return "no estimate yet: first time this command is seen";
        }
    }

    // ---- finished ----
    public string StatusGlyph => S.Status switch
    {
        AgentTaskStatus.Completed => "✓",
        AgentTaskStatus.Failed => "✗",
        AgentTaskStatus.Stopped => "■",
        AgentTaskStatus.Lost => "?",
        _ => "●",
    };
    public Brush StatusBrush => S.Status switch
    {
        AgentTaskStatus.Completed => Palette.Ok,
        AgentTaskStatus.Failed => Palette.Bad,
        AgentTaskStatus.Running => Palette.Accent,
        _ => Palette.Neutral,
    };
    public string StatusText
    {
        get
        {
            string s = S.Status.ToString().ToLowerInvariant();
            if (S.ExitCode is { } c && c != 0) s += $" (exit {c})";
            if (S.StatusDetail is { Length: > 0 } d && S.Status != AgentTaskStatus.Failed) s += " · " + d;
            return s;
        }
    }
    public string FinishedText => S.EndedAt is { } e ? $"{Fmt.Duration(Elapsed)}  ·  {Fmt.Ago(e)}" : "";

    public void Update(TaskSnapshot s, OutputInfo? output, Estimate? estimate)
    {
        S = s;
        _output = output;
        _estimate = estimate;
        RaiseAll();
    }

    public void Tick() => RaiseAll();
}

public sealed class SessionVm : Observable
{
    public SessionSnapshot S { get; private set; }
    public SessionVm(SessionSnapshot s) => S = s;
    public string Key => S.Key;
    public Brush SessionAccent => SessionColors.For(S.Key).Accent;
    public Brush SessionTint => SessionColors.For(S.Key).Tint;
    public string Name => S.Name;
    public string SourceLabel => S.Source == AgentSource.Claude ? "CLAUDE" : "CODEX";
    public Brush SourceBrush => S.Source == AgentSource.Claude ? Palette.Claude : Palette.Codex;
    public string Project => Fmt.ProjectName(S.Project);
    public string ProjectPath => S.Project;
    /// <summary>Agent is idle but has commands/background jobs still running.</summary>
    public bool IsWaiting => !S.Busy && S.RunningTasks > 0;
    /// <summary>The agent is blocked until the user answers a prompt, question or permission request.</summary>
    public bool NeedsInput => S.WaitingFor != null;
    public string NeedsInputText => $"Needs your input · {S.WaitingFor}";
    public string NeedsInputFor => Fmt.Duration(DateTimeOffset.Now - (S.WaitingSince ?? S.Since));
    public bool IsActive => S.Busy || S.RunningTasks > 0;
    public Brush DotBrush => NeedsInput ? Palette.Attention : S.Busy ? Palette.Ok : IsWaiting ? Palette.Warn : Palette.Neutral;
    public string StateText
    {
        get
        {
            var now = DateTimeOffset.Now;
            string jobs = S.RunningTasks == 1 ? "1 task" : $"{S.RunningTasks} tasks";
            if (NeedsInput)
                return $"needs your input · {NeedsInputFor}";
            if (S.Busy)
                return S.RunningTasks > 0 ? $"working {Fmt.Duration(now - S.Since)} · {jobs} running" : $"working {Fmt.Duration(now - S.Since)}";
            if (IsWaiting)
                return $"waiting on {jobs} · {Fmt.Duration(now - (S.OldestTaskStart ?? S.Since))}";
            return $"idle {Fmt.Duration(now - S.Since)}";
        }
    }
    // ---- session header card ("foreground lane"): shown while busy or while background jobs run ----
    private TaskVm? _fg;

    /// <summary>The foreground command the agent is blocked on, embedded in the header.</summary>
    public TaskVm? Fg
    {
        get => _fg;
        set { if (!ReferenceEquals(_fg, value)) { _fg = value; RaiseAll(); } }
    }
    public bool HasForeground => _fg != null;
    public bool IsIdle => !S.Busy;
    public double CardOpacity => S.Busy || NeedsInput ? 1.0 : 0.72;
    public string LaneLabel => NeedsInput ? "waiting for you"
        : S.Busy ? (_fg != null ? "agent waiting on command" : "agent working") : "agent idle";

    public string ActivityNow => S.Activity?.Now ?? "Working…";
    public string Title
    {
        get
        {
            if (_fg != null) return _fg.Description;
            if (S.Busy) return ActivityNow;
            int bg = S.RunningTasks;
            return bg switch { 0 => "Idle", 1 => "Idle · waiting on 1 background task", _ => $"Idle · waiting on {bg} background tasks" };
        }
    }
    /// <summary>How long the current step has been going (only for non-command steps).</summary>
    public string TitleFor => _fg == null && S.Busy && S.Activity is { } a ? Fmt.Duration(DateTimeOffset.Now - a.NowSince) : "";
    public bool ShowBusyBar => S.Busy && _fg == null && !NeedsInput;

    /// <summary>
    /// Busy: from the start of the turn (status changes like busy → waiting → busy reset Since mid-turn).
    /// Idle: from when it went idle.
    /// </summary>
    private DateTimeOffset LaneStart =>
        S.Busy && S.Activity?.TurnStart is { } t && t < S.Since && S.Since - t < TimeSpan.FromHours(12) ? t : S.Since;
    public string LaneTimerText => Fmt.Clock(DateTimeOffset.Now - LaneStart);
    public string LaneTimerLabel => (S.Busy ? "working since " : "idle since ") + LaneStart.ToLocalTime().ToString("HH:mm:ss");

    public string StepsText => S.Activity is { TurnSteps: > 0 } a
        ? (S.Busy ? (a.TurnSteps == 1 ? "1 step this turn" : $"{a.TurnSteps} steps this turn")
                  : (a.TurnSteps == 1 ? "last turn: 1 step" : $"last turn: {a.TurnSteps} steps"))
        : "";
    public bool HasSteps => S.Activity is { Steps.Count: > 0 };
    public IReadOnlyList<string> RecentSteps => S.Activity?.Steps
        .Skip(Math.Max(0, S.Activity.Steps.Count - 5))
        .Select(s => $"{s.At.ToLocalTime():HH:mm:ss}  {s.Text}")
        .ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

    public void Update(SessionSnapshot s) { S = s; RaiseAll(); }
    public void Tick() => RaiseAll();
}
