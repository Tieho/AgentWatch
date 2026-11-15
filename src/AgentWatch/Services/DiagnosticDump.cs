using System.IO;
using System.Text;
using AgentWatch.Models;

namespace AgentWatch.Services;

/// <summary>`AgentWatch.exe --dump`: polls twice and writes what was detected to dump.txt.</summary>
public static class DiagnosticDump
{
    public static void Run()
    {
        var sb = new StringBuilder();
        using var svc = new MonitorService();
        MonitorSnapshot? last = null;
        svc.Updated += s => last = s;
        svc.Error += m => sb.AppendLine("ERROR " + m);
        var refresh = typeof(MonitorService).GetMethod("Refresh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        refresh.Invoke(svc, null);
        Thread.Sleep(500);
        refresh.Invoke(svc, null);

        if (last != null)
        {
            sb.AppendLine("SESSIONS");
            foreach (var s in last.Sessions) sb.AppendLine($"  {s.Source} busy={s.Busy} {s.Name} | {s.Project} | since {s.Since:HH:mm:ss}");
            sb.AppendLine("RUNNING");
            foreach (var t in last.Tasks.Where(t => t.Status == AgentTaskStatus.Running))
            {
                last.Estimates.TryGetValue(t.Key, out var est);
                last.Outputs.TryGetValue(t.Key, out var o);
                sb.AppendLine($"  {t.Source} {t.SessionName} | {t.Description} | started {t.StartedAt.ToLocalTime():HH:mm:ss} bg={t.IsBackground} est={est?.Seconds:0}s/{est?.Samples} pct={o?.Percent} last={o?.LastLine}");
                if (last.Processes.TryGetValue(t.Key, out var pd))
                {
                    foreach (var l in pd.Procs)
                    {
                        sb.AppendLine($"      proc pid={l.Pid} cpu={l.Cpu:0.0} tag={l.Tag} shell={l.IsShell} [{l.Chain}] {l.Name} {(l.Args.Length > 160 ? l.Args[..160] + "…" : l.Args)} | {l.FriendlyName} | title={l.Title}");
                        foreach (var g in l.Children)
                            sb.AppendLine($"          └ {g.Name} x{g.Count} cpu={g.Cpu:0.0} title={g.Title}");
                    }
                    if (pd.LastRan != null) sb.AppendLine($"      last ran: {pd.LastRan} ended {pd.LastEnded:HH:mm:ss}");
                }
                else
                    sb.AppendLine("      proc (no matching process)");
            }
            sb.AppendLine("FINISHED (last 12h)");
            foreach (var t in last.Tasks.Where(t => t.Status != AgentTaskStatus.Running).OrderByDescending(t => t.EndedAt).Take(40))
                sb.AppendLine($"  {t.Source} {t.Status} {t.Duration.TotalSeconds:0}s exit={t.ExitCode} {t.SessionName} | {t.Description} | {t.StatusDetail}");
            sb.AppendLine($"TOTAL tasks={last.Tasks.Count}");
        }
        sb.AppendLine("ESTIMATOR seeded=" + svc.Estimator.IsSeeded);
        foreach (var probe in new[] { "dotnet build", "npm run build" })
            sb.AppendLine($"  probe '{probe}': {svc.Estimator.EstimateFor(probe, "")}");
        Directory.CreateDirectory(MonitorService.DataDir);
        File.WriteAllText(Path.Combine(MonitorService.DataDir, "dump.txt"), sb.ToString());
    }
}
