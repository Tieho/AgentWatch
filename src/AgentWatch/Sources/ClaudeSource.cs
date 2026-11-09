using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AgentWatch.Models;

namespace AgentWatch.Sources;

/// <summary>Watches live Claude Code sessions (~/.claude/sessions/*.json) and tails their transcripts.</summary>
public sealed class ClaudeSource
{
    private const long InitialTailBytes = 4 * 1024 * 1024;

    private readonly string _root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    private readonly Dictionary<string, SessionState> _sessions = new();

    private sealed class SessionState
    {
        public required string SessionId;
        public string Name = "";
        public string Cwd = "";
        public bool Alive;
        public bool Busy;
        /// <summary>Claude Code is showing a prompt/dialog (session status "waiting").</summary>
        public string? WaitingFor;
        public DateTimeOffset StatusSince;
        public DateTimeOffset DiedAt = DateTimeOffset.MaxValue;
        public string? TranscriptDir; // ~/.claude/projects/<slug>
        public readonly Dictionary<string, ClaudeTranscript> Transcripts = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Primed = new(StringComparer.OrdinalIgnoreCase);
    }

    public string ProjectsDir => System.IO.Path.Combine(_root, "projects");

    public void Poll(List<TaskSnapshot> tasks, List<SessionSnapshot> sessions)
    {
        var now = DateTimeOffset.Now;
        var seen = new HashSet<string>();
        string sessionsDir = System.IO.Path.Combine(_root, "sessions");

        if (Directory.Exists(sessionsDir))
        {
            foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.json"))
            {
                SessionFile? sf = ReadSessionFile(file);
                if (sf == null || !IsAlive(sf.Pid, sf.ProcStart)) continue;
                seen.Add(sf.SessionId);

                if (!_sessions.TryGetValue(sf.SessionId, out var st))
                    _sessions[sf.SessionId] = st = new SessionState { SessionId = sf.SessionId };
                st.Alive = true;
                st.DiedAt = DateTimeOffset.MaxValue;
                st.Name = string.IsNullOrWhiteSpace(sf.Name) ? System.IO.Path.GetFileName(sf.Cwd.TrimEnd('\\', '/')) : sf.Name;
                st.Cwd = sf.Cwd;
                st.WaitingFor = sf.Status == "waiting" ? PrettyWaitingReason(sf.WaitingFor) : null;
                // Waiting on the user is part of a turn: keep it "busy" so running commands aren't marked stopped.
                st.Busy = sf.Status is "busy" or "running" or "working" or "waiting";
                st.StatusSince = sf.StatusSince;

                DiscoverTranscripts(st);
            }
        }

        foreach (var st in _sessions.Values.ToList())
        {
            if (!seen.Contains(st.SessionId) && st.Alive)
            {
                st.Alive = false;
                st.DiedAt = now;
            }

            foreach (var tr in st.Transcripts.Values)
            {
                tr.SessionName = st.Name;
                bool live = st.Primed.Contains(tr.Path);
                try { tr.Poll(live); }
                catch (IOException) { }
                st.Primed.Add(tr.Path);

                foreach (var t in tr.Tasks)
                {
                    if (t.Status == AgentTaskStatus.Running)
                    {
                        if (!st.Alive)
                            t.Finish(AgentTaskStatus.Lost, st.DiedAt, "session ended");
                        else if (!t.IsBackground && !st.Busy && !tr.IsSubagent && st.StatusSince > t.StartedAt.AddSeconds(2) && now - st.StatusSince > TimeSpan.FromSeconds(3))
                            t.Finish(AgentTaskStatus.Stopped, st.StatusSince, "session went idle");
                        else if (t.IsBackground && t.OutputFile != null && now - t.StartedAt > TimeSpan.FromSeconds(15) && !File.Exists(t.OutputFile))
                            t.Finish(AgentTaskStatus.Lost, now, "output file gone");
                        else if (t.IsBackground && t.OutputFile != null && Services.OutputReader.Read(t.OutputFile)?.LastLine == "[killed]")
                            t.Finish(AgentTaskStatus.Stopped, new DateTimeOffset(File.GetLastWriteTime(t.OutputFile)), "killed");
                    }
                    tasks.Add(t.ToSnapshot());
                }
                tr.PruneEnded(now.AddHours(-12));
            }

            if (st.Alive)
            {
                string? waiting = st.WaitingFor;
                DateTimeOffset? waitingSince = waiting != null ? st.StatusSince : null;
                if (waiting == null && st.Busy)
                {
                    foreach (var tr in st.Transcripts.Values)
                    {
                        if (tr.WaitingForUser(now) is { } w)
                        {
                            waiting = tr.IsSubagent ? "subagent: " + w.Reason : w.Reason;
                            waitingSince = w.Since;
                            break;
                        }
                    }
                }
                sessions.Add(new SessionSnapshot("claude:" + st.SessionId, AgentSource.Claude, st.Name, st.Cwd, st.Busy, st.StatusSince,
                    Activity: CurrentActivity(st, now), WaitingFor: waiting, WaitingSince: waitingSince));
            }

            if (!st.Alive && now - st.DiedAt > TimeSpan.FromHours(12))
                _sessions.Remove(st.SessionId);
        }
    }

    /// <summary>Main transcript's activity; if a subagent did something more recently, show that as "now".</summary>
    private static SessionActivity? CurrentActivity(SessionState st, DateTimeOffset now)
    {
        var main = st.Transcripts.Values.FirstOrDefault(t => !t.IsSubagent);
        var mainAct = main?.Activity.Snapshot();
        var sub = st.Transcripts.Values
            .Where(t => t.IsSubagent && t.Activity.Now.Length > 0 && now - t.Activity.LastUpdate < TimeSpan.FromMinutes(3))
            .OrderByDescending(t => t.Activity.LastUpdate)
            .FirstOrDefault();
        if (sub != null && (main == null || sub.Activity.LastUpdate > main.Activity.LastUpdate))
        {
            var subAct = sub.Activity.Snapshot("Subagent › ")!;
            return mainAct == null ? subAct : mainAct with { Now = subAct.Now, NowSince = subAct.NowSince };
        }
        return mainAct;
    }

    private void DiscoverTranscripts(SessionState st)
    {
        if (st.TranscriptDir == null)
        {
            if (!Directory.Exists(ProjectsDir)) return;
            foreach (var dir in Directory.EnumerateDirectories(ProjectsDir))
            {
                if (File.Exists(System.IO.Path.Combine(dir, st.SessionId + ".jsonl")))
                {
                    st.TranscriptDir = dir;
                    break;
                }
            }
            if (st.TranscriptDir == null) return;
        }

        string main = System.IO.Path.Combine(st.TranscriptDir, st.SessionId + ".jsonl");
        if (!st.Transcripts.ContainsKey(main) && File.Exists(main))
            st.Transcripts[main] = new ClaudeTranscript(main, st.SessionId, st.Name, st.Cwd, false, InitialTailBytes);

        string subDir = System.IO.Path.Combine(st.TranscriptDir, st.SessionId, "subagents");
        if (Directory.Exists(subDir))
        {
            foreach (var f in Directory.EnumerateFiles(subDir, "*.jsonl"))
            {
                if (st.Transcripts.ContainsKey(f)) continue;
                // Old subagent transcripts that haven't changed for a while are not interesting.
                if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromHours(2)) continue;
                st.Transcripts[f] = new ClaudeTranscript(f, st.SessionId, st.Name, st.Cwd, true, InitialTailBytes);
            }
        }
    }

    private static string PrettyWaitingReason(string? raw) => raw switch
    {
        null or "" => "input needed",
        "dialog open" => "respond to a dialog",
        "sandbox request" => "approve a sandbox request",
        "worker request" => "approve a worker request",
        "goal proposal" => "review a goal proposal",
        _ => raw,
    };

    private sealed record SessionFile(int Pid, string SessionId, string Cwd, string? Name, string? Status, DateTimeOffset StatusSince, long? ProcStart, string? WaitingFor);

    private static SessionFile? ReadSessionFile(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var r = doc.RootElement;
            if (!r.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out int pid)) return null;
            string? sid = ClaudeTranscript.Str(r, "sessionId");
            if (sid == null) return null;
            long since = 0;
            if (r.TryGetProperty("statusUpdatedAt", out var su) && su.TryGetInt64(out var s1)) since = s1;
            else if (r.TryGetProperty("updatedAt", out var ua) && ua.TryGetInt64(out var s2)) since = s2;
            else if (r.TryGetProperty("startedAt", out var sa) && sa.TryGetInt64(out var s3)) since = s3;
            long? procStart = long.TryParse(ClaudeTranscript.Str(r, "procStart"), out var ps) ? ps : null;
            return new SessionFile(pid, sid, ClaudeTranscript.Str(r, "cwd") ?? "", ClaudeTranscript.Str(r, "name"),
                ClaudeTranscript.Str(r, "status"), DateTimeOffset.FromUnixTimeMilliseconds(since).ToLocalTime(), procStart,
                ClaudeTranscript.Str(r, "waitingFor"));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsAlive(int pid, long? procStartFileTime)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (procStartFileTime is { } ft)
            {
                try
                {
                    // Guard against PID reuse: start time must match (within 2 s).
                    long actual = p.StartTime.ToFileTimeUtc();
                    if (Math.Abs(actual - ft) > 20_000_000) return false;
                }
                catch (Exception) { /* access denied: assume alive */ }
            }
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
