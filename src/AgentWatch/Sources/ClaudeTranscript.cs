using System.Text.Json;
using System.Text.RegularExpressions;
using AgentWatch.Models;

namespace AgentWatch.Sources;

/// <summary>Parses one Claude Code transcript (.jsonl) into command/agent tasks.</summary>
public sealed partial class ClaudeTranscript
{
    private static readonly HashSet<string> TrackedTools = new(StringComparer.Ordinal)
    {
        "Bash", "PowerShell", "Agent", "Task",
    };

    private readonly Dictionary<string, TaskRecord> _byToolUse = new();
    private readonly Dictionary<string, TaskRecord> _byBackgroundId = new();
    private readonly JsonlTailer _tailer;
    private string? _lastAssistantMessageId;
    private readonly HashSet<string> _activityIds = new();
    /// <summary>Tool calls of the current turn still waiting for their result (id → description), in call order.</summary>
    private readonly List<PendingTool> _pendingTools = new();
    private sealed record PendingTool(string Id, string Name, string Text, DateTimeOffset At);

    /// <summary>Tools that only finish when the user responds.</summary>
    private static readonly Dictionary<string, string> UserInputTools = new()
    {
        ["AskUserQuestion"] = "answer a question",
        ["ExitPlanMode"] = "approve the plan",
    };

    /// <summary>Tools that finish in milliseconds; if one hangs it is waiting for permission.</summary>
    private static readonly HashSet<string> InstantTools = new(StringComparer.Ordinal)
    {
        "Read", "Edit", "MultiEdit", "Write", "NotebookEdit", "Glob", "Grep",
    };

    /// <summary>What the agent is waiting on the user for (from pending tool calls), or null.</summary>
    public (string Reason, DateTimeOffset Since)? WaitingForUser(DateTimeOffset now)
    {
        foreach (var p in _pendingTools)
        {
            if (UserInputTools.TryGetValue(p.Name, out var why)) return (why, p.At);
            if (InstantTools.Contains(p.Name) && now - p.At > TimeSpan.FromSeconds(10))
                return ("approve: " + p.Text, p.At);
        }
        return null;
    }

    public string SessionId { get; }
    public string SessionName { get; set; }
    public string Project { get; set; }
    public bool IsSubagent { get; }
    public IEnumerable<TaskRecord> Tasks => _byToolUse.Values;
    public ActivityTracker Activity { get; } = new();

    public ClaudeTranscript(string path, string sessionId, string sessionName, string project, bool isSubagent, long initialTailBytes)
    {
        _tailer = new JsonlTailer(path, initialTailBytes);
        SessionId = sessionId;
        SessionName = sessionName;
        Project = project;
        IsSubagent = isSubagent;
    }

    public string Path => _tailer.Path;

    public void Poll(bool live) => _tailer.ReadNewLines(line => ProcessLine(line, live));

    public void PruneEnded(DateTimeOffset olderThan)
    {
        foreach (var kv in _byToolUse.Where(kv => kv.Value.EndedAt is { } e && e < olderThan).ToList())
        {
            _byToolUse.Remove(kv.Key);
            if (kv.Value.BackgroundId != null) _byBackgroundId.Remove(kv.Value.BackgroundId);
        }
    }

    private void ProcessLine(string line, bool live)
    {
        bool hasToolUse = line.Contains("\"tool_use\"", StringComparison.Ordinal);
        bool hasToolResult = line.Contains("\"tool_result\"", StringComparison.Ordinal);
        bool hasNotification = line.Contains("<task-notification>", StringComparison.Ordinal);
        bool isAssistant = line.Contains("\"assistant\"", StringComparison.Ordinal);
        bool isUser = line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
        if (!hasToolUse && !hasToolResult && !hasNotification && !isAssistant && !isUser) return;
        // Raw API request dumps duplicate the whole conversation; ignore them.
        if (line.StartsWith("{\"type\":\"api-request-blob\"", StringComparison.Ordinal)) return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            string? type = Str(root, "type");
            if (type == "api-request-blob") return;

            DateTimeOffset? ts = null;
            if (Str(root, "timestamp") is { } tsStr && DateTimeOffset.TryParse(tsStr, out var parsed)) ts = parsed;

            if (type == "assistant" && root.TryGetProperty("message", out var msg))
                HandleAssistant(msg, ts ?? DateTimeOffset.Now);
            else if (type == "user" && hasToolResult && root.TryGetProperty("message", out var umsg))
            {
                HandleToolResults(root, umsg, ts ?? DateTimeOffset.Now);
                // With parallel tool calls, other calls may still be running.
                Activity.Set(_pendingTools.Count > 0 ? _pendingTools[^1].Text : "Thinking…", ts ?? DateTimeOffset.Now);
            }
            else if (type == "user" && ts is { } uts && !(root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True))
            {
                // A genuine prompt (or a background-task notice) starts a new turn.
                Activity.StartTurn(uts, hasNotification ? "Handling background task result…" : "Thinking…");
                _activityIds.Clear();
                _pendingTools.Clear();
            }

            if (hasNotification)
            {
                if (ts == null && !live) return; // can't date it
                HandleNotifications(line, ts ?? DateTimeOffset.Now);
            }
        }
    }

    private void HandleAssistant(JsonElement msg, DateTimeOffset ts)
    {
        string? messageId = Str(msg, "id");
        if (messageId != null && messageId != _lastAssistantMessageId)
        {
            // A new model turn means every earlier foreground call has returned.
            foreach (var t in _byToolUse.Values)
            {
                if (t.Status == AgentTaskStatus.Running && !t.IsBackground && t.MessageId != null && t.MessageId != messageId && t.StartedAt <= ts)
                    t.Finish(AgentTaskStatus.Stopped, ts, "no result recorded");
            }
            _lastAssistantMessageId = messageId;
            _pendingTools.Clear(); // the model only continues once all earlier calls have returned
        }

        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            string? btype = Str(block, "type");
            if (btype is "thinking" or "redacted_thinking") { Activity.Set("Thinking…", ts); continue; }
            if (btype == "text") { Activity.Set("Writing reply…", ts); continue; }
            if (btype != "tool_use") continue;
            string? name = Str(block, "name");
            string? id = Str(block, "id");
            block.TryGetProperty("input", out var input);
            if (name != null && (id == null || _activityIds.Add(id)))
            {
                string text = ActivityTracker.DescribeTool(name, input);
                Activity.Step(text, ts);
                if (id != null) _pendingTools.Add(new(id, name, text, ts));
            }
            if (name == null || id == null || !TrackedTools.Contains(name) || _byToolUse.ContainsKey(id)) continue;

            bool isAgent = name is "Agent" or "Task";
            string command = isAgent ? (Str(input, "prompt") ?? "") : (Str(input, "command") ?? "");
            string description = Str(input, "description") ?? FirstLine(command, 100);
            bool bg = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("run_in_background", out var rib) && rib.ValueKind == JsonValueKind.True;

            _byToolUse[id] = new TaskRecord
            {
                Key = "claude:" + id,
                Source = AgentSource.Claude,
                SessionKey = "claude:" + SessionId,
                SessionName = IsSubagent ? SessionName + " › subagent" : SessionName,
                Project = Project,
                Description = isAgent ? "Subagent: " + description : description,
                Command = isAgent ? FirstLine(command, 300) : command,
                StartedAt = ts,
                IsBackground = bg,
                MessageId = messageId,
            };
        }
    }

    private void HandleToolResults(JsonElement root, JsonElement msg, DateTimeOffset ts)
    {
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        root.TryGetProperty("toolUseResult", out var tur);

        foreach (var block in content.EnumerateArray())
        {
            if (Str(block, "type") != "tool_result") continue;
            string? id = Str(block, "tool_use_id");
            if (id != null) _pendingTools.RemoveAll(p => p.Id == id);
            if (id == null || !_byToolUse.TryGetValue(id, out var task)) continue;
            if (task.Status != AgentTaskStatus.Running) continue;

            string text = ContentText(block);
            string? bgId = Str(tur, "backgroundTaskId");
            if (bgId == null && task.IsBackground)
            {
                var m = BackgroundIdRegex().Match(text);
                if (m.Success) bgId = m.Groups[1].Value;
            }

            if (bgId != null)
            {
                task.IsBackground = true;
                task.BackgroundId = bgId;
                // The "running in background" result is written right when the process spawns; the tool_use
                // timestamp is earlier by permission checks, hooks or a pending approval.
                if (ts > task.StartedAt) task.StartedAt = ts;
                _byBackgroundId[bgId] = task;
                var om = OutputFileRegex().Match(text);
                if (om.Success) task.OutputFile = om.Groups[1].Value.Trim();
                continue;
            }

            bool isError = block.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
            bool interrupted = tur.ValueKind == JsonValueKind.Object && tur.TryGetProperty("interrupted", out var intr) && intr.ValueKind == JsonValueKind.True;
            if (isError)
            {
                // Claude prefixes failed shell results with "Exit code N"; don't scan the command's own output.
                var em = ExitCodeRegex().Match(text.Length > 40 ? text[..40] : text);
                if (em.Success && em.Index < 5) task.ExitCode = int.Parse(em.Groups[1].Value);
            }

            if (interrupted) task.Finish(AgentTaskStatus.Stopped, ts, "interrupted");
            else if (isError) task.Finish(AgentTaskStatus.Failed, ts, FirstLine(text, 160));
            else task.Finish(AgentTaskStatus.Completed, ts);
        }
    }

    private void HandleNotifications(string line, DateTimeOffset ts)
    {
        foreach (Match m in NotificationRegex().Matches(line))
        {
            string body = m.Groups[1].Value;
            string? taskId = Tag(body, "task-id");
            string? toolUseId = Tag(body, "tool-use-id");
            string status = Tag(body, "status") ?? "completed";
            string? summary = Tag(body, "summary");

            TaskRecord? task = null;
            if (toolUseId != null) _byToolUse.TryGetValue(toolUseId, out task);
            if (task == null && taskId != null) _byBackgroundId.TryGetValue(taskId, out task);
            if (task == null || task.Status != AgentTaskStatus.Running) continue;

            if (summary != null)
            {
                var em = ExitCodeRegex().Match(summary);
                if (em.Success) task.ExitCode = int.Parse(em.Groups[1].Value);
            }
            var st = status switch
            {
                "completed" => task.ExitCode is > 0 ? AgentTaskStatus.Failed : AgentTaskStatus.Completed,
                "failed" => AgentTaskStatus.Failed,
                _ => AgentTaskStatus.Stopped,
            };
            task.Finish(st, ts, status is "killed" or "stopped" ? status : null);
        }
    }

    // ---- helpers ----

    private static string? Tag(string body, string tag)
    {
        int s = body.IndexOf("<" + tag + ">", StringComparison.Ordinal);
        if (s < 0) return null;
        s += tag.Length + 2;
        int e = body.IndexOf("</" + tag + ">", s, StringComparison.Ordinal);
        return e < 0 ? null : body[s..e].Trim();
    }

    internal static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string ContentText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var parts = new List<string>();
        foreach (var p in c.EnumerateArray())
            if (Str(p, "text") is { } t) parts.Add(t);
        return string.Join("\n", parts);
    }

    internal static string FirstLine(string s, int max)
    {
        s = s.TrimStart();
        int nl = s.IndexOfAny(['\r', '\n']);
        if (nl >= 0) s = s[..nl];
        return s.Length > max ? s[..max] + "…" : s;
    }

    [GeneratedRegex(@"<task-notification>(.*?)</task-notification>", RegexOptions.Singleline)]
    private static partial Regex NotificationRegex();

    [GeneratedRegex(@"with ID:\s*([A-Za-z0-9_-]+)")]
    private static partial Regex BackgroundIdRegex();

    [GeneratedRegex(@"written to:\s*(.+?\.output)")]
    private static partial Regex OutputFileRegex();

    [GeneratedRegex(@"[Ee]xit code[:\s]+(-?\d+)")]
    private static partial Regex ExitCodeRegex();
}
