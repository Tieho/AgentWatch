using System.IO;
using System.Text;
using AgentWatch.Models;

namespace AgentWatch.Services;

/// <summary>
/// Ties each running task to the shell process its agent spawned for it (plus processes it detached, such as
/// an editor started with <c>&amp;</c>), and reports which programs are doing the work right now.
/// </summary>
public sealed class ProcessMatcher
{
    private sealed class TaskProcs
    {
        public (int pid, long created)? Shell;
        /// <summary>When the matched shell was first missing (null while it runs).</summary>
        public DateTimeOffset? ShellGoneAt;
        /// <summary>No shell was ever found for it (it ended before it could be seen); ShellGoneAt is when that was noticed.</summary>
        public bool NeverSeen;
        public readonly HashSet<(int, long)> Adopted = new();
        public string? LastText;
        public DateTimeOffset? LastGoneAt;
    }

    private readonly Dictionary<string, TaskProcs> _state = new();

    /// <summary>
    /// Running tasks whose shell (seen earlier) has been gone for at least <paramref name="grace"/>, with when it
    /// went away. Processes the command detached don't count: they outlive it on purpose.
    /// </summary>
    public List<(string key, DateTimeOffset gone, bool seen)> ShellsEnded(TimeSpan grace)
    {
        var now = DateTimeOffset.Now;
        return _state.Where(kv => kv.Value.ShellGoneAt is { } g && now - g >= grace)
            .Select(kv => (kv.Key, kv.Value.ShellGoneAt!.Value, !kv.Value.NeverSeen)).ToList();
    }

    public Dictionary<string, ProcessDetail> Match(IReadOnlyList<TaskSnapshot> tasks, IReadOnlyList<SessionSnapshot> sessions)
    {
        var result = new Dictionary<string, ProcessDetail>();
        var running = tasks.Where(t => t.Status == AgentTaskStatus.Running).ToList();
        var runningKeys = running.Select(t => t.Key).ToHashSet();
        foreach (var k in _state.Keys.Where(k => !runningKeys.Contains(k)).ToList()) _state.Remove(k);
        if (running.Count == 0) return result;

        // Roots: a Claude session's own pid; for Codex every codex.exe (threads don't record their pid).
        var claudeRoots = sessions.Where(s => s.Pid is not null).ToDictionary(s => s.Key, s => s.Pid!.Value);
        var codexRoots = running.Any(t => t.Source == AgentSource.Codex) ? ProcessTree.FindByName("codex.exe") : new List<int>();
        List<MsysProc> msys;
        try { msys = MsysProcesses.Read(); } catch (Exception) { msys = new(); }
        var agentRoots = claudeRoots.Values.Concat(codexRoots).ToList();
        // Programs a command names (e.g. "unity" for Unity.exe) that may already be running outside it.
        var named = ProcessTree.FindWhere(exe => !IsShellName(exe) && running.Any(t => NamesProgram(t.Command, exe)));
        named.Remove(Environment.ProcessId);
        // Git Bash processes go in as extra roots so they are captured even when their Windows parent is gone.
        var forest = ProcessTree.Capture(agentRoots.Concat(msys.Select(m => m.WinPid)).Concat(named),
            running.Min(t => t.StartedAt).AddSeconds(-5), agentRoots.ToHashSet());
        RelinkMsys(forest, msys, agentRoots);

        List<ProcInfo> ChildrenOf(IEnumerable<int> roots) => roots
            .Where(forest.Roots.ContainsKey).SelectMany(r => forest.Roots[r].Children).Where(c => !ProcessTree.IsNoise(c)).ToList();

        // ---- 1. shells: direct children of the agent process ----
        var shells = new Dictionary<string, ProcInfo>();
        var taken = _state.Values.Where(s => s.Shell != null).Select(s => s.Shell!.Value).ToHashSet();
        foreach (var group in running.GroupBy(t => t.Source == AgentSource.Codex ? "codex" : t.SessionKey))
        {
            List<ProcInfo> candidates = group.Key == "codex"
                ? ChildrenOf(codexRoots)
                : claudeRoots.TryGetValue(group.Key, out int pid) ? ChildrenOf([pid]) : new();

            bool Near(ProcInfo c, TaskSnapshot t) =>
                // Transcript timestamps and process start differ a little; long-lived children (MCP servers) predate the task.
                c.Created >= t.StartedAt.AddSeconds(-5) && (c.Created - t.StartedAt).TotalSeconds < 30;
            bool Free(ProcInfo c) => !taken.Contains((c.Pid, c.Created.UtcTicks));

            void Take(TaskSnapshot t, TaskProcs st, ProcInfo shell)
            {
                st.Shell = (shell.Pid, shell.Created.UtcTicks);
                st.ShellGoneAt = null;
                st.NeverSeen = false;
                taken.Add((shell.Pid, shell.Created.UtcTicks));
                shells[t.Key] = shell;
            }

            // Keep earlier matches while that process lives. Once matched, a task keeps its shell: when that exits,
            // the command is over (no re-matching).
            var unmatched = new List<TaskSnapshot>();
            foreach (var t in group.OrderBy(t => t.StartedAt))
            {
                var st = State(t.Key);
                if (st.Shell is not { } s) { unmatched.Add(t); continue; }
                var shell = candidates.FirstOrDefault(c => c.Pid == s.pid && c.Created.UtcTicks == s.created);
                if (shell == null) st.ShellGoneAt ??= DateTimeOffset.Now;
                else { st.ShellGoneAt = null; shells[t.Key] = shell; }
            }

            // New tasks: first the shells whose command line contains the task's command, then the closest in time
            // (commands started in the same second must not swap shells).
            foreach (var t in unmatched)
            {
                var c = candidates.Where(c => Free(c) && c.Created >= t.StartedAt.AddSeconds(-5) && MentionsCommand(c, t.Command))
                    .OrderBy(c => Math.Abs((c.Created - t.StartedAt).TotalSeconds)).FirstOrDefault();
                if (c != null) Take(t, State(t.Key), c);
            }
            foreach (var t in unmatched.Where(t => !shells.ContainsKey(t.Key)))
            {
                var st = State(t.Key);
                var c = candidates.Where(c => Free(c) && Near(c, t))
                    .OrderBy(c => Math.Abs((c.Created - t.StartedAt).TotalSeconds)).FirstOrDefault();
                if (c != null) { Take(t, st, c); continue; }

                // The Claude process is alive but has no shell at all from when this command started (not even one
                // matched to another task): it already exited, e.g. before AgentWatch was started.
                if (group.Key != "codex" && claudeRoots.TryGetValue(group.Key, out int root) && forest.Roots.ContainsKey(root) &&
                    DateTimeOffset.Now - t.StartedAt > TimeSpan.FromMinutes(1) &&
                    !candidates.Any(c => Near(c, t) || MentionsCommand(c, t.Command)))
                {
                    st.NeverSeen = true;
                    st.ShellGoneAt ??= DateTimeOffset.Now;
                }
                else if (st.NeverSeen) { st.NeverSeen = false; st.ShellGoneAt = null; }
            }
        }

        // ---- 2. detached processes: parent gone, started during a task, and clearly related to it ----
        var adopted = new Dictionary<string, List<ProcInfo>>();
        foreach (var o in forest.Orphans)
        {
            if (ProcessTree.IsNoise(o)) continue;
            string? owner = _state.FirstOrDefault(kv => kv.Value.Adopted.Contains(o.Id)).Key;
            if (owner == null)
            {
                owner = running
                    .Where(t => t.StartedAt.AddSeconds(-2) <= o.Created && RelatedTo(o, t))
                    .OrderByDescending(t => t.StartedAt) // the latest task that could have started it
                    .FirstOrDefault()?.Key;
                if (owner == null) continue;
                State(owner).Adopted.Add(o.Id);
            }
            if (!adopted.TryGetValue(owner, out var list)) adopted[owner] = list = new();
            list.Add(o);
        }
        var orphanIds = forest.Orphans.Select(o => o.Id).ToHashSet();
        foreach (var st in _state.Values) st.Adopted.IntersectWith(orphanIds);

        // ---- 3. related: already running, named by the command and working in the same project (an editor it talks to) ----
        var related = new Dictionary<string, List<ProcInfo>>();
        var agentSet = agentRoots.ToHashSet();
        foreach (var r in named.Where(p => !agentSet.Contains(p) && forest.Roots.ContainsKey(p)).Select(p => forest.Roots[p]))
        {
            if (orphanIds.Contains(r.Id) || r.CommandLine is not { Length: > 0 } cl) continue;
            if (adopted.Values.Any(a => a.Contains(r))) continue;
            // Shown once: on the newest running command that runs it in its project.
            var owner = running
                .Where(t => t.Project.Length > 3 && NamesProgram(t.Command, r.Exe) && InProject(cl, t.Project))
                .OrderByDescending(t => t.StartedAt)
                .FirstOrDefault();
            if (owner == null) continue;
            if (!related.TryGetValue(owner.Key, out var list)) related[owner.Key] = list = new();
            list.Add(r);
        }

        // ---- 4. describe ----
        var now = DateTimeOffset.Now;
        foreach (var t in running)
        {
            shells.TryGetValue(t.Key, out var shell);
            adopted.TryGetValue(t.Key, out var detached);
            related.TryGetValue(t.Key, out var rel);
            if (shell == null && detached == null && rel == null) continue;
            var st = State(t.Key);

            var procs = new List<RunningProcess>();
            if (shell != null) Commands(shell, new List<string>(), procs);
            bool shellIdle = shell != null && procs.Count == 0;

            // Remember the last command the shell ran, to show while it sits between steps.
            var cmd = procs.FirstOrDefault(p => !p.IsShell);
            if (cmd != null)
            {
                st.LastText = Line(cmd);
                st.LastGoneAt = null;
            }
            else if (st.LastText != null && st.LastGoneAt == null)
                st.LastGoneAt = now;

            if (shellIdle)
                procs.Add(Describe(shell!, ShortExe(shell!.Exe)) with { IsShell = true, Children = Array.Empty<ChildGroup>() });
            foreach (var d in detached ?? new())
                procs.Add(Describe(d, "detached: started by this command, its launcher has exited", "DETACHED"));
            foreach (var r in rel ?? new())
                procs.Add(Describe(r, "related: already running in this project, and the command names it", "RELATED"));

            result[t.Key] = new ProcessDetail(shell?.Pid ?? 0, procs,
                shellIdle ? st.LastText : null, shellIdle ? st.LastGoneAt : null);
        }
        return result;
    }

    /// <summary>
    /// Re-attach Git Bash processes whose Windows parent link is broken under their real parent (from Git Bash's own
    /// table), or under their process group leader when that parent is gone too (e.g. <c>( cmd &amp; )</c>).
    /// </summary>
    private static void RelinkMsys(ProcessForest forest, List<MsysProc> msys, List<int> agentRoots)
    {
        var byMsysPid = msys.ToDictionary(m => m.Pid, m => m);
        var agent = agentRoots.ToHashSet();
        foreach (var m in msys)
        {
            // Only processes captured as separate roots are detached from their real parent.
            if (agent.Contains(m.WinPid) || !forest.Roots.TryGetValue(m.WinPid, out var node)) continue;
            ProcInfo? target = null;
            if (byMsysPid.TryGetValue(m.PPid, out var parent) && parent.WinPid != m.WinPid)
                forest.All.TryGetValue(parent.WinPid, out target);
            if (target == null && m.Pgid != m.Pid && byMsysPid.TryGetValue(m.Pgid, out var leader))
                forest.All.TryGetValue(leader.WinPid, out target);
            if (target == null || ReferenceEquals(target, node) || node.Descendants().Contains(target)) continue;
            target.Children.Add(node);
            forest.Roots.Remove(m.WinPid);
        }
    }

    private TaskProcs State(string key)
    {
        if (!_state.TryGetValue(key, out var s)) _state[key] = s = new TaskProcs();
        return s;
    }

    /// <summary>
    /// The programs the shell is running: walk down through wrapper shells and stop at the first real program
    /// (its own children are summarized under it).
    /// </summary>
    private static void Commands(ProcInfo p, List<string> chain, List<RunningProcess> into)
    {
        var here = new List<string>(chain) { ShortExe(p.Exe) };
        foreach (var k in p.Children.Where(c => !ProcessTree.IsNoise(c)).OrderBy(k => k.Created))
        {
            if (ProcessTree.IsShell(k) && k.Children.Any(c => !ProcessTree.IsNoise(c)))
                Commands(k, here, into);
            else
                into.Add(Describe(k, string.Join(" › ", here.Append(ShortExe(k.Exe)))));
        }
    }

    private static RunningProcess Describe(ProcInfo p, string chain, string tag = "")
    {
        var (name, args) = Split(p);
        return new RunningProcess(p.Pid, name, args, p.CommandLine ?? p.Exe, p.Created, chain,
            ProcessTree.IsShell(p), p.SubtreeCpu(), tag, p.FriendlyName, BestTitle(p), ChildGroups(p), p.NotResponding);
    }

    private static string Line(RunningProcess p) => (p.Name + " " + p.Args).TrimEnd();

    /// <summary>
    /// Child programs (all levels) grouped by name and window title, busiest first, like Task Manager's expanded group.
    /// </summary>
    private static IReadOnlyList<ChildGroup> ChildGroups(ProcInfo p) => p.Descendants()
        .Where(d => !ProcessTree.IsNoise(d))
        .GroupBy(d => (name: d.FriendlyName, title: BestTitle(d), hung: d.NotResponding))
        .Select(g => new ChildGroup(g.Key.name, g.Key.title, g.Count(),
            g.Any(d => d.Cpu != null) ? g.Sum(d => d.Cpu ?? 0) : null, g.Key.hung))
        .OrderByDescending(g => g.NotResponding).ThenByDescending(g => g.Cpu ?? 0).ThenBy(g => g.Title == null).ThenByDescending(g => g.Count).ThenBy(g => g.Name)
        .ToList();

    /// <summary>The most telling window title: a progress or dialog window rather than the main window.</summary>
    private static string? BestTitle(ProcInfo p) => p.Titles.Count switch
    {
        0 => null,
        1 => p.Titles[0],
        // The main window's title is usually the longest ("project - scene - platform - version"); dialogs are short.
        _ => string.Join("  |  ", p.Titles.OrderBy(t => t.Length).Take(2)),
    };

    private static string ShortExe(string exe) =>
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    /// <summary>Splits a command line into a short program name and its arguments.</summary>
    private static (string name, string args) Split(ProcInfo p)
    {
        string cmd = (p.CommandLine ?? "").Trim();
        string args = "";
        if (cmd.Length > 0)
        {
            int end;
            if (cmd[0] == '"') { end = cmd.IndexOf('"', 1); end = end < 0 ? cmd.Length : end + 1; }
            else { end = cmd.IndexOf(' '); if (end < 0) end = cmd.Length; }
            args = Collapse(cmd[end..]);
        }
        return (ShortExe(p.Exe), args);
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char ch in s)
        {
            if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>A detached process belongs to a task if it works in the task's project or the command names its program.</summary>
    private static bool RelatedTo(ProcInfo p, TaskSnapshot t) =>
        (t.Project.Length > 3 && p.CommandLine is { Length: > 0 } cl && InProject(cl, t.Project)) ||
        (!ProcessTree.IsShell(p) && NamesProgram(t.Command, p.Exe));

    private static bool InProject(string commandLine, string project) =>
        Letters(commandLine).Contains(Letters(project), StringComparison.Ordinal);

    private static bool IsShellName(string exe) => Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() is
        "bash" or "sh" or "zsh" or "cmd" or "powershell" or "pwsh" or "conhost";

    /// <summary>
    /// The command runs the program: its name is the first word of one of the command's parts ("unity command …",
    /// "x && /c/tools/unity.exe …", "s=$(unity …)"), not merely a file or package name in it ("Scenes/Town.unity").
    /// </summary>
    private static bool NamesProgram(string command, string exe)
    {
        string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        return name.Length >= 2 && CommandNames(command).Contains(name);
    }

    private static readonly Dictionary<string, HashSet<string>> CommandNamesCache = new();
    /// <summary>A shell word: non-space characters and quoted strings (spaces kept), e.g. <c>x="a b"</c> is one word.</summary>
    private static readonly System.Text.RegularExpressions.Regex Words =
        new(@"(?:[^\s""']|""[^""]*""|'[^']*')+", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex Assignment =
        new(@"^[A-Za-z_][A-Za-z0-9_]*=", System.Text.RegularExpressions.RegexOptions.Compiled);
    /// <summary>Words that run the next word as the command.</summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "do", "then", "else", "elif", "if", "while", "until", "time", "exec", "nohup", "sudo", "env", "command",
        "builtin", "xargs", "nice", "timeout", "start", "call", "!", "start-process", "invoke-expression", "iex",
    };

    /// <summary>
    /// Splits a command line at ; &amp; | newlines ( ) { } and backticks outside quotes, and at <c>$(</c> anywhere except
    /// in single quotes (a command substitution also runs inside double quotes).
    /// </summary>
    private static List<string> Parts(string command)
    {
        var parts = new List<string>();
        var cur = new System.Text.StringBuilder();
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (quote != '\'' && c == '$' && i + 1 < command.Length && command[i + 1] == '(')
            {
                parts.Add(cur.ToString());
                cur.Clear();
                i++;
                continue;
            }
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                cur.Append(c);
                continue;
            }
            if (c is '"' or '\'') { quote = c; cur.Append(c); continue; }
            if (c is ';' or '&' or '|' or '\r' or '\n' or '(' or ')' or '{' or '}' or '`')
            {
                parts.Add(cur.ToString());
                cur.Clear();
                continue;
            }
            cur.Append(c);
        }
        parts.Add(cur.ToString());
        return parts;
    }

    /// <summary>Program names (lower case, no path or extension) in command position anywhere in a command.</summary>
    private static HashSet<string> CommandNames(string command)
    {
        if (CommandNamesCache.TryGetValue(command, out var cached)) return cached;
        var names = new HashSet<string>();
        foreach (var part in Parts(command))
        {
            var words = Words.Matches(part).Select(m => m.Value);
            bool afterWrapper = false;
            foreach (var raw in words)
            {
                string w = raw.Trim('"', '\'');
                if (w.Length == 0 || Assignment.IsMatch(w)) continue;
                if (Wrappers.Contains(w)) { afterWrapper = true; continue; }
                // Options and numbers of a wrapper ("timeout 30 dotnet …", "nice -n 5 make").
                if (afterWrapper && (w.StartsWith('-') || w.All(char.IsDigit))) continue;
                string file = w.Replace('\\', '/');
                file = file[(file.LastIndexOf('/') + 1)..].ToLowerInvariant();
                foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".ps1", ".sh" })
                    if (file.EndsWith(ext)) { file = file[..^ext.Length]; break; }
                if (file.Length > 0) names.Add(file);
                break;
            }
        }
        if (CommandNamesCache.Count > 2000) CommandNamesCache.Clear();
        return CommandNamesCache[command] = names;
    }

    /// <summary>The shell's command line contains a recognizable piece of the task's command.</summary>
    private static bool MentionsCommand(ProcInfo shell, string command)
    {
        if (string.IsNullOrEmpty(shell.CommandLine) || command.Length == 0) return false;
        string hay = Letters(shell.CommandLine);
        string needle = Letters(command);
        if (needle.Length < 6) return false;
        return hay.Contains(needle.Length > 48 ? needle[..48] : needle, StringComparison.Ordinal);
    }

    /// <summary>Letters and digits only, so quoting, slashes and escaping differences don't matter.</summary>
    private static string Letters(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
