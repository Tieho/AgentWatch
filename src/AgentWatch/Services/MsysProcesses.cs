using System.Diagnostics;
using System.IO;

namespace AgentWatch.Services;

/// <summary>One process in Git Bash's (MSYS2/Cygwin) own process table.</summary>
public sealed record MsysProc(int Pid, int PPid, int Pgid, int WinPid, string Command);

/// <summary>
/// Git Bash breaks Windows parent links (fork emulation, <c>( … &amp;)</c> subshells), but its own table keeps
/// the real parent and the process group of everything a shell started. Read via Git's <c>ps</c>.
/// </summary>
public static class MsysProcesses
{
    private static string? _ps;
    private static bool _searched;

    public static List<MsysProc> Read()
    {
        var result = new List<MsysProc>();
        if (ProcessTree.FindByName("bash.exe").Count == 0) return result;
        string? ps = FindPs();
        if (ps == null) return result;
        var psi = new ProcessStartInfo(ps, "-l")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return result;
        var read = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(3000)) { try { p.Kill(); } catch (Exception) { } return result; }
        foreach (var raw in read.Result.Split('\n'))
        {
            var tok = raw.Split((char[]?)null, 9, StringSplitOptions.RemoveEmptyEntries);
            // Optional leading status flag ("I", "S", "O"...), then PID PPID PGID WINPID TTY UID STIME COMMAND.
            int i = tok.Length > 0 && !int.TryParse(tok[0], out _) ? 1 : 0;
            if (tok.Length < i + 8) continue;
            if (int.TryParse(tok[i], out int pid) && int.TryParse(tok[i + 1], out int ppid) &&
                int.TryParse(tok[i + 2], out int pgid) && int.TryParse(tok[i + 3], out int win))
                result.Add(new MsysProc(pid, ppid, pgid, win, string.Join(' ', tok.Skip(i + 7)).Trim()));
        }
        return result;
    }

    /// <summary>Git's ps.exe, found next to a running bash.exe (or in the usual install folders).</summary>
    private static string? FindPs()
    {
        if (_ps != null && File.Exists(_ps)) return _ps;
        var dirs = new List<string>();
        foreach (int pid in ProcessTree.FindByName("bash.exe").Take(5))
        {
            if (ProcessTree.ImagePath(pid) is not { } img) continue;
            string dir = Path.GetDirectoryName(img)!;
            dirs.Add(dir);
            dirs.Add(Path.Combine(Path.GetDirectoryName(dir)!, "usr", "bin")); // Git\bin\bash.exe is a launcher
        }
        if (!_searched)
        {
            _searched = true;
            foreach (var pf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData })
            {
                string root = Environment.GetFolderPath(pf);
                dirs.Add(Path.Combine(root, "Git", "usr", "bin"));
                dirs.Add(Path.Combine(root, "Programs", "Git", "usr", "bin"));
            }
        }
        foreach (var d in dirs)
        {
            string ps = Path.Combine(d, "ps.exe");
            if (File.Exists(ps)) return _ps = ps;
        }
        return null;
    }
}
