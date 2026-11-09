using System.IO;
using System.Text.Json;
using AgentWatch.Models;

namespace AgentWatch.Sources;

/// <summary>Tracks what an agent is doing right now within its current turn.</summary>
public sealed class ActivityTracker
{
    private const int MaxSteps = 30;
    private readonly List<ActivityStep> _steps = new();

    public string Now { get; private set; } = "";
    public DateTimeOffset NowSince { get; private set; }
    public DateTimeOffset? TurnStart { get; private set; }
    public int TurnSteps { get; private set; }
    public DateTimeOffset LastUpdate { get; private set; }

    public void StartTurn(DateTimeOffset ts, string what = "Thinking…")
    {
        TurnStart = ts;
        TurnSteps = 0;
        _steps.Clear();
        Set(what, ts);
    }

    /// <summary>A non-step state change (thinking, writing, waiting for model).</summary>
    public void Set(string what, DateTimeOffset ts)
    {
        if (ts < LastUpdate) return;
        if (what != Now) NowSince = ts;
        Now = what;
        LastUpdate = ts;
        TurnStart ??= ts;
    }

    /// <summary>A tool call: becomes the current activity and is added to the step list.</summary>
    public void Step(string what, DateTimeOffset ts)
    {
        TurnStart ??= ts;
        TurnSteps++;
        _steps.Add(new ActivityStep(ts, what));
        if (_steps.Count > MaxSteps) _steps.RemoveAt(0);
        if (ts >= LastUpdate)
        {
            Now = what;
            NowSince = ts;
            LastUpdate = ts;
        }
    }

    public SessionActivity? Snapshot(string prefix = "") => Now.Length == 0 ? null
        : new SessionActivity(prefix + Now, NowSince, TurnStart, TurnSteps, _steps.ToList());

    // ---- describing tool calls ----

    public static string DescribeTool(string name, JsonElement input)
    {
        string? S(string p) => ClaudeTranscript.Str(input, p);
        string F(string? path) => string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path.TrimEnd('\\', '/'));
        string Q(string? s, int max = 60) => s == null ? "" : ClaudeTranscript.FirstLine(s, max);

        switch (name)
        {
            case "Bash" or "PowerShell":
                return "Running: " + (S("description") ?? Q(S("command"), 80));
            case "Agent" or "Task":
                return "Subagent: " + (S("description") ?? Q(S("prompt")));
            case "Read":
                return "Reading " + F(S("file_path"));
            case "Edit" or "MultiEdit":
                return "Editing " + F(S("file_path"));
            case "Write":
                return "Writing " + F(S("file_path"));
            case "NotebookEdit":
                return "Editing " + F(S("notebook_path"));
            case "Grep":
                return $"Searching for \"{Q(S("pattern"), 50)}\"";
            case "Glob":
                return "Finding files " + Q(S("pattern"), 50);
            case "WebFetch":
                return "Fetching " + Q(S("url"), 70);
            case "WebSearch":
                return $"Web search \"{Q(S("query"), 60)}\"";
            case "TodoWrite":
                return "Updating todo list";
            case "Skill":
                return "Using skill " + (S("skill") ?? "");
            case "ToolSearch":
                return "Loading tools";
            case "AskUserQuestion":
                return "Asking you a question";
            case "ExitPlanMode":
                return "Presenting plan for approval";
        }
        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = name.Split("__", 3);
            string server = parts.Length > 1 ? parts[1] : "";
            string tool = parts.Length > 2 ? parts[2] : name;
            if (server.Length > 20) server = server[..8] + "…";
            if (tool.StartsWith("update_plan") || tool == "wrap_up") return "Updating task list";
            if (server.Contains("Browser", StringComparison.OrdinalIgnoreCase)) return "Browser: " + tool.Replace('_', ' ');
            return $"{server}: {tool.Replace('_', ' ')}";
        }
        return name;
    }
}
