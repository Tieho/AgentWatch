namespace AgentWatch.Models;

public enum AgentSource { Claude, Codex }

public enum AgentTaskStatus { Running, Completed, Failed, Stopped, Lost }

/// <summary>Mutable task state owned by a source (background thread only).</summary>
public sealed class TaskRecord
{
    public required string Key { get; init; }
    public AgentSource Source { get; init; }
    public string SessionKey { get; set; } = "";
    public string SessionName { get; set; } = "";
    public string Project { get; set; } = "";
    public string Description { get; set; } = "";
    public string Command { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Running;
    public bool IsBackground { get; set; }
    public string? BackgroundId { get; set; }
    public string? OutputFile { get; set; }
    public int? ExitCode { get; set; }
    public string? StatusDetail { get; set; }
    /// <summary>Assistant message id (Claude) used to detect orphaned foreground calls.</summary>
    public string? MessageId { get; set; }

    public void Finish(AgentTaskStatus status, DateTimeOffset at, string? detail = null)
    {
        if (Status != AgentTaskStatus.Running) return;
        Status = status;
        EndedAt = at < StartedAt ? StartedAt : at;
        if (detail != null) StatusDetail = detail;
    }

    public TaskSnapshot ToSnapshot() => new(Key, Source, SessionName, Project, Description, Command,
        StartedAt, EndedAt, Status, IsBackground, OutputFile, ExitCode, StatusDetail, SessionKey);
}

public sealed record TaskSnapshot(
    string Key,
    AgentSource Source,
    string SessionName,
    string Project,
    string Description,
    string Command,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    AgentTaskStatus Status,
    bool IsBackground,
    string? OutputFile,
    int? ExitCode,
    string? StatusDetail,
    string SessionKey = "")
{
    public TimeSpan Duration => (EndedAt ?? DateTimeOffset.Now) - StartedAt;
}

public sealed record SessionSnapshot(
    string Key,
    AgentSource Source,
    string Name,
    string Project,
    bool Busy,
    DateTimeOffset Since,
    int RunningTasks = 0,
    DateTimeOffset? OldestTaskStart = null,
    SessionActivity? Activity = null,
    string? WaitingFor = null,
    DateTimeOffset? WaitingSince = null,
    int? Pid = null);

public sealed record ActivityStep(DateTimeOffset At, string Text);

/// <summary>What a session's agent is doing in its current turn.</summary>
public sealed record SessionActivity(
    string Now,
    DateTimeOffset NowSince,
    DateTimeOffset? TurnStart,
    int TurnSteps,
    IReadOnlyList<ActivityStep> Steps);

/// <summary>Child programs of one kind (same name and window title) under a running program.</summary>
public sealed record ChildGroup(string Name, string? Title, int Count, double? Cpu);

/// <summary>A program a task is running, with its window title and its child programs.</summary>
public sealed record RunningProcess(int Pid, string Name, string Args, string CommandLine, DateTimeOffset Started, string Chain,
    bool IsShell, double? Cpu, string Tag, string FriendlyName, string? Title, IReadOnlyList<ChildGroup> Children)
{
    /// <summary>Not under the task's shell: DETACHED (its launcher exited) or RELATED (already running, used by the command).</summary>
    public bool Outside => Tag.Length > 0;
}

/// <summary>
/// What a running task is executing right now: the programs under its shell, and programs it detached.
/// While the shell has no child, LastRan says what it ran last and LastEnded when that went away.
/// </summary>
public sealed record ProcessDetail(int ShellPid, IReadOnlyList<RunningProcess> Procs, string? LastRan, DateTimeOffset? LastEnded);

public sealed record OutputInfo(string? LastLine, double? Percent);

public sealed record Estimate(double Seconds, int Samples);

public sealed record MonitorSnapshot(
    IReadOnlyList<TaskSnapshot> Tasks,
    IReadOnlyList<SessionSnapshot> Sessions,
    IReadOnlyDictionary<string, OutputInfo> Outputs,
    IReadOnlyDictionary<string, Estimate> Estimates,
    IReadOnlyDictionary<string, ProcessDetail> Processes);
