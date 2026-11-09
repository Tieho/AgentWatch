using System.Diagnostics;
using System.IO;
using AgentWatch.Models;
using AgentWatch.Sources;

namespace AgentWatch.Services;

/// <summary>Polls all sources on a background timer and publishes snapshots.</summary>
public sealed class MonitorService : IDisposable
{
    private readonly ClaudeSource _claude = new();
    private readonly CodexSource _codex = new();
    private readonly Timer _timer;
    private int _running;
    private bool _first = true;
    private Dictionary<string, AgentTaskStatus> _prev = new();

    public DurationEstimator Estimator { get; }
    public event Action<MonitorSnapshot>? Updated;
    public event Action<TaskSnapshot>? TaskFinished;
    public event Action<string>? Error;

    /// <summary>%LOCALAPPDATA%\AgentWatch</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentWatch");

    public MonitorService()
    {
        Estimator = new DurationEstimator(Path.Combine(DataDir, "history.json"));
        _timer = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        if (!Estimator.IsSeeded)
            Task.Run(Seed);
        _timer.Change(0, 2000);
    }

    private void Refresh()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var tasks = new List<TaskSnapshot>();
            var sessions = new List<SessionSnapshot>();
            try { _claude.Poll(tasks, sessions); } catch (Exception e) { Error?.Invoke("Claude: " + e.Message); }
            try { _codex.Poll(tasks, sessions); } catch (Exception e) { Error?.Invoke("Codex: " + e.Message); }

            // Attach running-task counts to sessions (a session can be "idle" while its background jobs run).
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                var mine = tasks.Where(t => t.Status == AgentTaskStatus.Running && t.SessionKey == s.Key).ToList();
                if (mine.Count > 0)
                    sessions[i] = s with { RunningTasks = mine.Count, OldestTaskStart = mine.Min(t => t.StartedAt) };
            }

            var now = new Dictionary<string, AgentTaskStatus>();
            foreach (var t in tasks)
            {
                now[t.Key] = t.Status;
                if (!_first && t.Status != AgentTaskStatus.Running && _prev.TryGetValue(t.Key, out var before) && before == AgentTaskStatus.Running)
                {
                    if (t.Status == AgentTaskStatus.Completed && t.EndedAt is { } end)
                        Estimator.Record(t.Command, t.Description, (end - t.StartedAt).TotalSeconds, end);
                    TaskFinished?.Invoke(t);
                }
            }
            _prev = now;
            if (_first) GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true); // drop initial catch-up garbage
            _first = false;

            var outputs = new Dictionary<string, OutputInfo>();
            var estimates = new Dictionary<string, Estimate>();
            foreach (var t in tasks.Where(t => t.Status == AgentTaskStatus.Running))
            {
                if (t.OutputFile != null && OutputReader.Read(t.OutputFile) is { } o) outputs[t.Key] = o;
                if (Estimator.EstimateFor(t.Command, t.Description) is { } e) estimates[t.Key] = e;
            }

            Estimator.Save();
            Updated?.Invoke(new MonitorSnapshot(tasks, sessions, outputs, estimates));
        }
        catch (Exception e)
        {
            Error?.Invoke(e.Message);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>First run: learn durations from all existing Claude transcripts and Codex history.</summary>
    private void Seed()
    {
        var sw = Stopwatch.StartNew();
        int count = 0;
        try
        {
            if (Directory.Exists(_claude.ProjectsDir))
            {
                foreach (var file in Directory.EnumerateFiles(_claude.ProjectsDir, "*.jsonl", SearchOption.AllDirectories))
                {
                    try
                    {
                        var tr = new ClaudeTranscript(file, "", "", "", false, -1);
                        tr.Poll(live: false);
                        foreach (var t in tr.Tasks)
                        {
                            if (t.Status == AgentTaskStatus.Completed && t.EndedAt is { } end)
                            {
                                Estimator.Record(t.Command, t.Description, (end - t.StartedAt).TotalSeconds, end);
                                count++;
                            }
                        }
                    }
                    catch (Exception) { /* skip unreadable transcript */ }
                }
            }
            try
            {
                foreach (var (command, seconds, ended) in _codex.QueryAllDurations())
                {
                    Estimator.Record(command, "", seconds, ended);
                    count++;
                }
            }
            catch (Exception e) { Error?.Invoke("Codex history: " + e.Message); }
            Estimator.MarkSeeded();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            Debug.WriteLine($"Seeded {count} durations in {sw.Elapsed}");
        }
        catch (Exception e)
        {
            Error?.Invoke("Seeding: " + e.Message);
        }
    }

    public void Dispose() => _timer.Dispose();
}
