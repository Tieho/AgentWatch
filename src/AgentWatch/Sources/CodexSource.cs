using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentWatch.Models;
using Microsoft.Data.Sqlite;

namespace AgentWatch.Sources;

/// <summary>
/// Watches OpenAI Codex. Active threads are read from ~/.codex/state_5.sqlite; their live rollout
/// files are tailed for in-flight commands, and finished commands come from thread_history_1.sqlite.
/// </summary>
public sealed partial class CodexSource
{
    private readonly string _root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    private readonly Dictionary<string, CodexRollout> _rollouts = new(StringComparer.OrdinalIgnoreCase);

    public string StateDb => System.IO.Path.Combine(_root, "state_5.sqlite");
    public string HistoryDb => System.IO.Path.Combine(_root, "thread_history_1.sqlite");

    private sealed record ThreadInfo(string Id, string RolloutPath, string Name, string Cwd, DateTimeOffset Updated);

    public void Poll(List<TaskSnapshot> tasks, List<SessionSnapshot> sessions)
    {
        if (!File.Exists(StateDb)) return;
        var now = DateTimeOffset.Now;

        var threads = QueryThreads(now.AddHours(-12));
        var busy = QueryBusyTurns(now.AddHours(-6));

        var tailedThreads = new HashSet<string>();
        foreach (var th in threads.Values)
        {
            bool isBusy = busy.TryGetValue(th.Id, out var turnStart);
            CodexRollout? ro = null;
            if (File.Exists(th.RolloutPath))
            {
                if (!_rollouts.TryGetValue(th.RolloutPath, out ro))
                    _rollouts[th.RolloutPath] = ro = new CodexRollout(th.RolloutPath, th.Id, th.Name, th.Cwd);
                ro.SessionName = th.Name;
                try { ro.Poll(); } catch (IOException) { }
                // The live rollout is fresher than the history DB's turn status.
                if (!isBusy && ro.TurnActive && now - ro.Activity.LastUpdate < TimeSpan.FromMinutes(10))
                {
                    isBusy = true;
                    turnStart = ro.Activity.TurnStart ?? ro.Activity.LastUpdate;
                }
            }

            if (isBusy || now - th.Updated < TimeSpan.FromHours(1))
                sessions.Add(new SessionSnapshot("codex:" + th.Id, AgentSource.Codex, th.Name, th.Cwd, isBusy, isBusy ? turnStart : th.Updated,
                    Activity: ro?.Activity.Snapshot(), WaitingFor: ro?.WaitingFor, WaitingSince: ro?.WaitingSince));

            if (ro == null) continue;
            tailedThreads.Add(th.Id);
            foreach (var t in ro.Tasks)
            {
                if (t.Status == AgentTaskStatus.Running && !isBusy && now - th.Updated > TimeSpan.FromMinutes(2) && !t.IsBackground)
                    t.Finish(AgentTaskStatus.Stopped, th.Updated, "turn ended");
                tasks.Add(t.ToSnapshot());
            }
            ro.PruneEnded(now.AddHours(-12));
        }

        foreach (var key in _rollouts.Keys.Where(k => !File.Exists(k)).ToList())
            _rollouts.Remove(key);

        // Commands recorded in the history DB (for threads we are not tailing live).
        foreach (var t in QueryHistoryCommands(now.AddHours(-12), threads))
        {
            if (tailedThreads.Contains(t.threadId)) continue;
            if (t.task.Status == AgentTaskStatus.Running && !busy.ContainsKey(t.threadId))
                t.task.Finish(AgentTaskStatus.Lost, now, "turn not running");
            tasks.Add(t.task.ToSnapshot());
        }
    }

    /// <summary>All finished Codex commands with durations, for seeding the estimator.</summary>
    public IEnumerable<(string command, double seconds, DateTimeOffset ended)> QueryAllDurations()
    {
        if (!File.Exists(HistoryDb)) yield break;
        using var c = Open(HistoryDb);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT coalesce(json_extract(item_json,'$.commandActions[0].command'), json_extract(item_json,'$.command')),
                   json_extract(item_json,'$.durationMs'), coalesce(completed_at_ms, created_at_ms)
            FROM thread_items
            WHERE item_type='commandExecution' AND json_extract(item_json,'$.status')='completed'
                  AND json_extract(item_json,'$.durationMs') IS NOT NULL
            ORDER BY created_at_ms
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0) || r.IsDBNull(1)) continue;
            yield return (r.GetString(0), r.GetDouble(1) / 1000.0, DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)));
        }
    }

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=1000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private Dictionary<string, ThreadInfo> QueryThreads(DateTimeOffset since)
    {
        var result = new Dictionary<string, ThreadInfo>();
        using var c = Open(StateDb);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, rollout_path, coalesce(nullif(name,''), nullif(title,''), id), cwd, coalesce(updated_at_ms, updated_at*1000)
            FROM threads WHERE coalesce(updated_at_ms, updated_at*1000) > $since AND coalesce(archived,0)=0
            """;
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string name = r.GetString(2);
            name = ClaudeTranscript.FirstLine(name, 70);
            result[r.GetString(0)] = new ThreadInfo(r.GetString(0), r.GetString(1), name, r.IsDBNull(3) ? "" : r.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)).ToLocalTime());
        }
        return result;
    }

    private Dictionary<string, DateTimeOffset> QueryBusyTurns(DateTimeOffset since)
    {
        var result = new Dictionary<string, DateTimeOffset>();
        if (!File.Exists(HistoryDb)) return result;
        using var c = Open(HistoryDb);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT thread_id, started_at FROM thread_turns WHERE status='inProgress' AND started_at IS NOT NULL";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var started = FromUnixAuto(r.GetInt64(1));
            if (started > since) result[r.GetString(0)] = started;
        }
        return result;
    }

    private IEnumerable<(string threadId, TaskRecord task)> QueryHistoryCommands(DateTimeOffset since, Dictionary<string, ThreadInfo> threads)
    {
        var list = new List<(string, TaskRecord)>();
        if (!File.Exists(HistoryDb)) return list;
        using var c = Open(HistoryDb);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT thread_id, item_id, coalesce(started_at_ms, created_at_ms), completed_at_ms,
                   json_extract(item_json,'$.status'),
                   coalesce(json_extract(item_json,'$.commandActions[0].command'), json_extract(item_json,'$.command')),
                   json_extract(item_json,'$.cwd'), json_extract(item_json,'$.exitCode'), json_extract(item_json,'$.durationMs')
            FROM thread_items
            WHERE item_type='commandExecution' AND created_at_ms > $since
            """;
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string threadId = r.GetString(0);
            var started = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).ToLocalTime();
            string status = r.IsDBNull(4) ? "" : r.GetString(4);
            string command = r.IsDBNull(5) ? "" : r.GetString(5);
            threads.TryGetValue(threadId, out var th);
            var t = new TaskRecord
            {
                Key = "codex:" + r.GetString(1),
                Source = AgentSource.Codex,
                SessionKey = "codex:" + threadId,
                SessionName = th?.Name ?? "Codex",
                Project = r.IsDBNull(6) ? th?.Cwd ?? "" : r.GetString(6),
                Description = ClaudeTranscript.FirstLine(command, 100),
                Command = command,
                StartedAt = started,
                ExitCode = r.IsDBNull(7) ? null : r.GetInt32(7),
            };
            DateTimeOffset? ended = !r.IsDBNull(3) ? DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)).ToLocalTime()
                : !r.IsDBNull(8) ? started.AddMilliseconds(r.GetDouble(8)) : null;
            if (status == "completed") t.Finish(AgentTaskStatus.Completed, ended ?? started);
            else if (status == "failed") t.Finish(AgentTaskStatus.Failed, ended ?? started);
            else if (status is "declined" or "interrupted" or "cancelled") t.Finish(AgentTaskStatus.Stopped, ended ?? started, status);
            list.Add((threadId, t));
        }
        return list;
    }

    private static DateTimeOffset FromUnixAuto(long v) =>
        (v > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(v) : DateTimeOffset.FromUnixTimeSeconds(v)).ToLocalTime();
}

/// <summary>Parses a live Codex rollout (.jsonl) for shell/exec tool calls.</summary>
public sealed partial class CodexRollout
{
    private readonly JsonlTailer _tailer;
    private readonly Dictionary<string, TaskRecord> _byCallId = new();
    private readonly Dictionary<string, TaskRecord> _bySession = new();
    private readonly Dictionary<string, string> _writeStdinSession = new();

    public string SessionName { get; set; }
    public string Cwd { get; }
    public IEnumerable<TaskRecord> Tasks => _byCallId.Values;

    public string ThreadId { get; }
    public ActivityTracker Activity { get; } = new();
    /// <summary>A user turn started and no completion event has been seen yet.</summary>
    public bool TurnActive { get; private set; }
    /// <summary>Pending approval request (command or patch), or null.</summary>
    public string? WaitingFor { get; private set; }
    public DateTimeOffset? WaitingSince { get; private set; }

    public CodexRollout(string path, string threadId, string name, string cwd)
    {
        ThreadId = threadId;
        _tailer = new JsonlTailer(path, 4 * 1024 * 1024);
        SessionName = name;
        Cwd = cwd;
    }

    public void Poll() => _tailer.ReadNewLines(ProcessLine);

    public void PruneEnded(DateTimeOffset olderThan)
    {
        foreach (var k in _byCallId.Where(kv => kv.Value.EndedAt is { } e && e < olderThan).Select(kv => kv.Key).ToList())
            _byCallId.Remove(k);
    }

    private void ProcessLine(string line)
    {
        if (line.Contains("\"token_count\"", StringComparison.Ordinal)) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) return;
            var ts = DateTimeOffset.TryParse(ClaudeTranscript.Str(root, "timestamp"), out var t0) ? t0 : DateTimeOffset.Now;
            string? ptype = ClaudeTranscript.Str(p, "type");
            string? callId = ClaudeTranscript.Str(p, "call_id");

            // ---- approvals: any later event means the request was answered ----
            switch (ptype)
            {
                case "exec_approval_request":
                {
                    string cmd = p.TryGetProperty("command", out var c) ? (ExtractCommandValue(c) ?? "") : "";
                    WaitingFor = "approve: " + ClaudeTranscript.FirstLine(cmd, 80);
                    WaitingSince = ts;
                    Activity.Set("Waiting for approval", ts);
                    return;
                }
                case "apply_patch_approval_request":
                    WaitingFor = "approve file changes";
                    WaitingSince = ts;
                    Activity.Set("Waiting for approval", ts);
                    return;
                case "request_user_input" or "elicitation_request":
                    WaitingFor = "input needed";
                    WaitingSince = ts;
                    return;
                default:
                    WaitingFor = null;
                    WaitingSince = null;
                    break;
            }

            // ---- activity (what the agent is doing) ----
            switch (ptype)
            {
                case "user_message" or "task_started" or "turn_started":
                    if (!TurnActive || ptype == "user_message") { Activity.StartTurn(ts); TurnActive = true; }
                    return;
                case "task_complete" or "turn_complete" or "turn_aborted":
                    Activity.Set(ptype == "turn_aborted" ? "Interrupted" : "Done", ts);
                    TurnActive = false;
                    return;
                case "reasoning" or "agent_reasoning" or "agent_reasoning_delta":
                    Activity.Set("Thinking…", ts);
                    return;
                case "agent_message" or "message" when ClaudeTranscript.Str(p, "role") is null or "assistant":
                    Activity.Set("Writing reply…", ts);
                    return;
                case "function_call_output" or "custom_tool_call_output" or "local_shell_call_output":
                    Activity.Set("Thinking…", ts);
                    break;
                case "web_search_call":
                    Activity.Step("Web search", ts);
                    return;
            }

            switch (ptype)
            {
                case "function_call" or "custom_tool_call" or "local_shell_call":
                {
                    if (callId == null) return;
                    string name = ClaudeTranscript.Str(p, "name") ?? (ptype == "local_shell_call" ? "shell" : "");
                    JsonElement args = default;
                    JsonDocument? argDoc = null;
                    string? raw = ClaudeTranscript.Str(p, "arguments") ?? ClaudeTranscript.Str(p, "input");
                    if (raw != null) { try { argDoc = JsonDocument.Parse(raw); args = argDoc.RootElement; } catch (JsonException) { } }
                    else if (p.TryGetProperty("action", out var action)) args = action;
                    using (argDoc)
                    {
                        if (name == "write_stdin")
                        {
                            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("session_id", out var sid))
                                _writeStdinSession[callId] = sid.ToString();
                            Activity.Set("Waiting on a running command", ts);
                            return;
                        }
                        if (!(name.Contains("shell", StringComparison.OrdinalIgnoreCase) || name.Contains("exec", StringComparison.OrdinalIgnoreCase)))
                        {
                            Activity.Step(DescribeCodexTool(name, args), ts);
                            return;
                        }
                        string command = ExtractCommand(args) ?? raw ?? name;
                        Activity.Step("Running: " + ClaudeTranscript.FirstLine(command, 80), ts);
                        if (_byCallId.ContainsKey(callId)) return;
                        _byCallId[callId] = new TaskRecord
                        {
                            Key = "codex:" + callId,
                            Source = AgentSource.Codex,
                            SessionKey = "codex:" + ThreadId,
                            SessionName = SessionName,
                            Project = Cwd,
                            Description = ClaudeTranscript.FirstLine(command, 100),
                            Command = command,
                            StartedAt = ts,
                        };
                    }
                    break;
                }
                case "exec_command_begin":
                {
                    if (callId == null || _byCallId.ContainsKey(callId)) return;
                    string command = p.TryGetProperty("command", out var c) ? (ExtractCommandValue(c) ?? "") : "";
                    _byCallId[callId] = new TaskRecord
                    {
                        Key = "codex:" + callId, Source = AgentSource.Codex, SessionKey = "codex:" + ThreadId, SessionName = SessionName,
                        Project = ClaudeTranscript.Str(p, "cwd") ?? Cwd,
                        Description = ClaudeTranscript.FirstLine(command, 100), Command = command, StartedAt = ts,
                    };
                    break;
                }
                case "exec_command_end":
                {
                    if (callId == null || !_byCallId.TryGetValue(callId, out var task)) return;
                    if (p.TryGetProperty("exit_code", out var ec) && ec.TryGetInt32(out int code)) task.ExitCode = code;
                    task.Finish(task.ExitCode is null or 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed, ts);
                    break;
                }
                case "function_call_output" or "custom_tool_call_output":
                {
                    if (callId == null) return;
                    string output = OutputText(p);
                    if (_writeStdinSession.Remove(callId, out var sessionId))
                    {
                        if (_bySession.TryGetValue(sessionId, out var bg)) FinishFromOutput(bg, output, ts, requireExit: true);
                        return;
                    }
                    if (!_byCallId.TryGetValue(callId, out var task)) return;
                    var running = SessionRunningRegex().Match(output);
                    if (running.Success)
                    {
                        task.IsBackground = true;
                        task.BackgroundId = running.Groups[1].Value;
                        _bySession[task.BackgroundId] = task;
                        return;
                    }
                    FinishFromOutput(task, output, ts, requireExit: false);
                    break;
                }
            }
        }
    }

    private static string DescribeCodexTool(string name, JsonElement args)
    {
        string? S(string p) => ClaudeTranscript.Str(args, p);
        return name switch
        {
            "apply_patch" => "Editing files",
            "update_plan" => "Updating plan",
            "view_image" => "Viewing image " + System.IO.Path.GetFileName(S("path") ?? ""),
            "web_search" or "search" => "Web search " + (S("query") ?? ""),
            "spawn_agent" or "create_agent" => "Starting subagent",
            _ when name.StartsWith("mcp__", StringComparison.Ordinal) || name.Contains("__") => ActivityTracker.DescribeTool(name, args),
            _ => name.Replace('_', ' '),
        };
    }

    private static void FinishFromOutput(TaskRecord task, string output, DateTimeOffset ts, bool requireExit)
    {
        var m = ExitRegex().Match(output);
        if (m.Success) task.ExitCode = int.Parse(m.Groups[1].Value);
        else if (requireExit) return;
        task.Finish(task.ExitCode is null or 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed, ts);
    }

    private static string OutputText(JsonElement p)
    {
        if (!p.TryGetProperty("output", out var o)) return "";
        if (o.ValueKind == JsonValueKind.String) return o.GetString() ?? "";
        if (o.ValueKind == JsonValueKind.Object && ClaudeTranscript.Str(o, "content") is { } s) return s;
        return o.ToString();
    }

    private static string? ExtractCommand(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "cmd", "command", "script" })
            if (args.TryGetProperty(key, out var v) && ExtractCommandValue(v) is { } s) return s;
        return null;
    }

    private static string? ExtractCommandValue(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Array)
        {
            var parts = v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
            if (parts.Count == 0) return null;
            // ["bash","-lc","actual command"] -> actual command
            if (parts.Count >= 3 && parts[^2] is "-lc" or "-c" or "-Command") return parts[^1];
            return string.Join(' ', parts);
        }
        return null;
    }

    [GeneratedRegex(@"[Pp]rocess running with session ID\s*(\d+)")]
    private static partial Regex SessionRunningRegex();

    [GeneratedRegex(@"(?:[Pp]rocess exited with code|[Ee]xit code:?)\s*(-?\d+)")]
    private static partial Regex ExitRegex();
}
