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
        public readonly HashSet<(int, long)> Adopted = new();
        public string? LastText;
        public DateTimeOffset? LastGoneAt;
    }

    private readonly Dictionary<string, TaskProcs> _state = new();

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

            // Keep earlier matches while that process lives; match new tasks oldest first.
            foreach (var t in group.OrderBy(t => t.StartedAt))
            {
                var st = State(t.Key);
                ProcInfo? shell = null;
                if (st.Shell is { } s)
                {
                    shell = candidates.FirstOrDefault(c => c.Pid == s.pid && c.Created.UtcTicks == s.created);
                    if (shell == null) { st.Shell = null; taken.Remove(s); }
                }
                if (shell == null)
                {
                    shell = candidates
                        .Where(c => !taken.Contains((c.Pid, c.Created.UtcTicks)))
                        // Transcript timestamps and process start differ a little; long-lived children (MCP servers) predate the task.
                        .Where(c => c.Created >= t.StartedAt.AddSeconds(-5))
                        .Select(c => (c, text: MentionsCommand(c, t.Command), dt: Math.Abs((c.Created - t.StartedAt).TotalSeconds)))
                        .Where(x => x.text || x.dt < 30)
                        .OrderByDescending(x => x.text).ThenBy(x => x.dt)
                        .Select(x => x.c)
                        .FirstOrDefault();
                    if (shell != null)
                    {
                        st.Shell = (shell.Pid, shell.Created.UtcTicks);
                        taken.Add((shell.Pid, shell.Created.UtcTicks));
                    }
                }
                if (shell != null) shells[t.Key] = shell;
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
            foreach (var t in running)
            {
                if (t.Project.Length <= 3 || !NamesProgram(t.Command, r.Exe) || !InProject(cl, t.Project)) continue;
                if (adopted.TryGetValue(t.Key, out var a) && a.Contains(r)) continue;
                if (!related.TryGetValue(t.Key, out var list)) related[t.Key] = list = new();
                list.Add(r);
            }
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
            ProcessTree.IsShell(p), p.SubtreeCpu(), tag, p.FriendlyName, BestTitle(p), ChildGroups(p));
    }

    private static string Line(RunningProcess p) => (p.Name + " " + p.Args).TrimEnd();

    /// <summary>
    /// Child programs (all levels) grouped by name and window title, busiest first, like Task Manager's expanded group.
    /// </summary>
    private static IReadOnlyList<ChildGroup> ChildGroups(ProcInfo p) => p.Descendants()
        .Where(d => !ProcessTree.IsNoise(d))
        .GroupBy(d => (name: d.FriendlyName, title: BestTitle(d)))
        .Select(g => new ChildGroup(g.Key.name, g.Key.title, g.Count(),
            g.Any(d => d.Cpu != null) ? g.Sum(d => d.Cpu ?? 0) : null))
        .OrderByDescending(g => g.Cpu ?? 0).ThenBy(g => g.Title == null).ThenByDescending(g => g.Count).ThenBy(g => g.Name)
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

    /// <summary>The command mentions the program as a whole word ("unity" for Unity.exe, not "community").</summary>
    private static bool NamesProgram(string command, string exe)
    {
        string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        if (name.Length < 3 || command.Length == 0) return false;
        string cmd = command.ToLowerInvariant();
        for (int i = cmd.IndexOf(name, StringComparison.Ordinal); i >= 0; i = cmd.IndexOf(name, i + 1, StringComparison.Ordinal))
        {
            bool startOk = i == 0 || !char.IsLetterOrDigit(cmd[i - 1]);
            int e = i + name.Length;
            bool endOk = e == cmd.Length || !char.IsLetterOrDigit(cmd[e]);
            if (startOk && endOk) return true;
        }
        return false;
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
