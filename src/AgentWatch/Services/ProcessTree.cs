using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace AgentWatch.Services;

/// <summary>One live OS process (read-only view).</summary>
public sealed class ProcInfo(int pid, int parentPid, string exe, DateTimeOffset created)
{
    public int Pid { get; } = pid;
    public int ParentPid { get; } = parentPid;
    public string Exe { get; } = exe;
    public DateTimeOffset Created { get; } = created;
    public string? CommandLine { get; set; }
    /// <summary>Share of total machine CPU since the previous capture (Task Manager style); null on first sight.</summary>
    public double? Cpu { get; set; }
    /// <summary>The exe's own description, as Task Manager shows it ("Unity Shader Compiler"); the exe name if it has none.</summary>
    public string FriendlyName { get; set; } = "";
    /// <summary>Titles of the process's visible top-level windows ("Opening project...", "Hold on...").</summary>
    public List<string> Titles { get; } = new();
    public List<ProcInfo> Children { get; } = new();
    public (int, long) Id => (Pid, Created.UtcTicks);

    public IEnumerable<ProcInfo> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));
    public double? SubtreeCpu() => Cpu is null ? null : Cpu + Descendants().Sum(d => d.Cpu ?? 0);
}

public sealed class ProcessForest
{
    /// <summary>Requested roots that are alive, with their descendant trees.</summary>
    public Dictionary<int, ProcInfo> Roots { get; } = new();
    /// <summary>Every captured process by pid.</summary>
    public Dictionary<int, ProcInfo> All { get; } = new();
    /// <summary>
    /// Processes whose parent has exited (detached with <c>&amp;</c>, <c>start</c>, a launcher that quit...),
    /// started after the cutoff, with their descendant trees.
    /// </summary>
    public List<ProcInfo> Orphans { get; } = new();
}

/// <summary>
/// Cheap snapshot of process trees: Toolhelp32 for parent links, then times and command lines only for the
/// processes of interest.
/// </summary>
public static class ProcessTree
{
    private static readonly Dictionary<(int, long), string?> CmdCache = new();
    private static readonly Dictionary<(int, long), (long cpu, long at)> CpuPrev = new();
    private static readonly Dictionary<(int, long), string> NameCache = new();
    private static readonly Dictionary<string, string?> DescCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Processes that never do interesting work themselves.</summary>
    public static bool IsNoise(ProcInfo p) => Path.GetFileNameWithoutExtension(p.Exe).ToLowerInvariant() is
        "conhost" or "openconsole" or "unitycrashhandler64" or "unityautoquitter" or "werfault";

    public static bool IsShell(ProcInfo p) => Path.GetFileNameWithoutExtension(p.Exe).ToLowerInvariant() is
        "bash" or "sh" or "zsh" or "cmd" or "powershell" or "pwsh" or "chcp" or "env" or "timeout" or "nohup";

    /// <summary>Pids of all running processes whose exe name passes the filter.</summary>
    public static List<int> FindWhere(Func<string, bool> exe) =>
        Enumerate().Where(e => e.pid > 4 && exe(e.exe)).Select(e => e.pid).ToList();

    /// <summary>Pids of all running processes with this exe name (e.g. "codex.exe").</summary>
    public static List<int> FindByName(string exe) =>
        Enumerate().Where(e => e.exe.Equals(exe, StringComparison.OrdinalIgnoreCase)).Select(e => e.pid).ToList();

    /// <param name="keepRoots">Roots that stay roots even if another root turns out to be their parent (the agents).</param>
    public static ProcessForest Capture(IEnumerable<int> rootPids, DateTimeOffset? orphansSince, IReadOnlySet<int>? keepRoots = null)
    {
        var entries = Enumerate();
        var byParent = new Dictionary<int, List<(int pid, int ppid, string exe)>>();
        var byPid = new Dictionary<int, (int pid, int ppid, string exe)>();
        foreach (var e in entries)
        {
            byPid[e.pid] = e;
            if (!byParent.TryGetValue(e.ppid, out var l)) byParent[e.ppid] = l = new();
            l.Add(e);
        }

        long now = DateTime.UtcNow.Ticks;
        var times = new Dictionary<int, (DateTimeOffset created, long cpu)?>();
        (DateTimeOffset created, long cpu)? Times(int pid)
        {
            if (!times.TryGetValue(pid, out var t)) times[pid] = t = ReadTimes(pid);
            return t;
        }

        var forest = new ProcessForest();
        var inTree = new HashSet<int>();
        var live = new HashSet<(int, long)>();

        ProcInfo Node((int pid, int ppid, string exe) e, DateTimeOffset created, long cpu)
        {
            var p = new ProcInfo(e.pid, e.ppid, e.exe, created);
            forest.All[e.pid] = p;
            inTree.Add(e.pid);
            live.Add(p.Id);
            if (!CmdCache.TryGetValue(p.Id, out var cmd)) CmdCache[p.Id] = cmd = ReadCommandLine(e.pid);
            p.CommandLine = cmd;
            if (!NameCache.TryGetValue(p.Id, out var friendly)) NameCache[p.Id] = friendly = Friendly(e.pid, e.exe);
            p.FriendlyName = friendly;
            if (CpuPrev.TryGetValue(p.Id, out var prev) && now > prev.at)
                p.Cpu = Math.Clamp((cpu - prev.cpu) * 100.0 / ((now - prev.at) * (double)Environment.ProcessorCount), 0, 100);
            CpuPrev[p.Id] = (cpu, now);

            if (byParent.TryGetValue(e.pid, out var kids))
            {
                foreach (var k in kids)
                {
                    if (k.pid == e.pid || k.pid == 0) continue;
                    if (inTree.Contains(k.pid))
                    {
                        // Captured earlier as a separate root (extra roots come in any order): move it under its parent.
                        if (keepRoots?.Contains(k.pid) != true && forest.Roots.Remove(k.pid, out var stolen) && stolen.Created >= created)
                            p.Children.Add(stolen);
                        continue;
                    }
                    // A stale parent pid can point at a newer process that reused the number.
                    if (Times(k.pid) is not { } kt || kt.created < created) continue;
                    p.Children.Add(Node(k, kt.created, kt.cpu));
                }
            }
            return p;
        }

        foreach (int rootPid in rootPids.Distinct())
        {
            if (inTree.Contains(rootPid) || !byPid.TryGetValue(rootPid, out var re) || Times(rootPid) is not { } rt) continue;
            forest.Roots[rootPid] = Node(re, rt.created, rt.cpu);
        }

        if (orphansSince is { } since)
        {
            foreach (var e in entries)
            {
                if (e.pid <= 4 || inTree.Contains(e.pid)) continue;
                if (Times(e.pid) is not { } t || t.created < since) continue;
                bool parentAlive = byPid.ContainsKey(e.ppid) && Times(e.ppid) is { } pt && pt.created <= t.created;
                if (parentAlive) continue;
                forest.Orphans.Add(Node(e, t.created, t.cpu));
            }
        }

        foreach (var (pid, title) in WindowTitles())
            if (forest.All.TryGetValue(pid, out var owner) && !owner.Titles.Contains(title)) owner.Titles.Add(title);

        foreach (var k in CmdCache.Keys.Where(k => !live.Contains(k)).ToList()) CmdCache.Remove(k);
        foreach (var k in NameCache.Keys.Where(k => !live.Contains(k)).ToList()) NameCache.Remove(k);
        foreach (var k in CpuPrev.Keys.Where(k => !live.Contains(k)).ToList()) CpuPrev.Remove(k);
        return forest;
    }

    // ---------------- Win32 ----------------

    private static List<(int pid, int ppid, string exe)> Enumerate()
    {
        var list = new List<(int, int, string)>(512);
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) throw new Win32Exception();
        try
        {
            var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref pe)) return list;
            do list.Add(((int)pe.th32ProcessID, (int)pe.th32ParentProcessID, pe.szExeFile));
            while (Process32NextW(snap, ref pe));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    private static string Friendly(int pid, string exe)
    {
        string fallback = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
        if (ImagePath(pid) is not { } path) return fallback;
        if (!DescCache.TryGetValue(path, out var desc))
        {
            try { desc = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); }
            catch (Exception) { desc = null; }
            DescCache[path] = desc;
        }
        return string.IsNullOrWhiteSpace(desc) ? fallback : desc;
    }

    /// <summary>(pid, title) of every visible, titled top-level window.</summary>
    private static List<(int pid, string title)> WindowTitles()
    {
        var list = new List<(int, string)>();
        EnumWindowsProc cb = (h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLengthW(h);
            if (len <= 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowTextW(h, sb, sb.Capacity);
            GetWindowThreadProcessId(h, out uint pid);
            string t = sb.ToString().Trim();
            if (t.Length > 0) list.Add(((int)pid, t));
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return list;
    }

    public static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int len = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref len) ? sb.ToString(0, len) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Creation time and total CPU time (100 ns ticks).</summary>
    private static (DateTimeOffset created, long cpu)? ReadTimes(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            return GetProcessTimes(h, out long created, out _, out long kernel, out long user)
                ? (DateTimeOffset.FromFileTime(created), kernel + user)
                : null;
        }
        finally { CloseHandle(h); }
    }

    private static string? ReadCommandLine(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        IntPtr buf = IntPtr.Zero;
        try
        {
            int size = 4096;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                buf = Marshal.AllocHGlobal(size);
                int status = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, size, out int needed);
                if (status == 0)
                {
                    // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
                    int len = (ushort)Marshal.ReadInt16(buf);
                    IntPtr str = Marshal.ReadIntPtr(buf, IntPtr.Size);
                    return len == 0 || str == IntPtr.Zero ? "" : Marshal.PtrToStringUni(str, len / 2);
                }
                Marshal.FreeHGlobal(buf);
                buf = IntPtr.Zero;
                if (needed <= size) return null;
                size = needed;
            }
            return null;
        }
        catch (Exception) { return null; }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseHandle(h);
        }
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W pe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W pe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int infoClass, IntPtr info, int length, out int returnLength);
}
