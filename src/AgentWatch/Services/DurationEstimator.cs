using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentWatch.Models;

namespace AgentWatch.Services;

/// <summary>
/// Learns how long commands usually take, keyed by normalized command signatures,
/// and estimates the duration of a running command from past runs.
/// </summary>
public sealed partial class DurationEstimator
{
    private const int MaxSamplesPerKey = 20;
    private const int SamplesForMedian = 10;
    private const double MinSecondsToRecord = 3;
    /// <summary>Bump when signature keys change: older history is discarded and relearned.</summary>
    private const int CurrentVersion = 5;

    private static readonly HashSet<string> KnownTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "msbuild", "npm", "npx", "pnpm", "yarn", "bun", "node", "cargo", "gradle", "gradlew", "mvn",
        "make", "cmake", "ninja", "pytest", "python", "python3", "py", "pip", "uv", "unity", "docker", "git",
        "tsc", "vite", "webpack", "jest", "vitest", "playwright", "flutter", "xcodebuild", "swift", "rustc", "gcc",
        "clang", "cl", "devenv", "godot", "unreal", "ue", "uat", "runuat", "buildpatchtool", "terraform", "kubectl",
    };

    private readonly object _lock = new();
    private readonly string _file;
    private HistoryFile _data = new();
    private bool _dirty;

    private sealed class HistoryFile
    {
        public int Version { get; set; } = CurrentVersion;
        public bool Seeded { get; set; }
        public Dictionary<string, List<Sample>> Keys { get; set; } = new();
    }

    private sealed class Sample
    {
        public long End { get; set; }
        public double Sec { get; set; }
    }

    public DurationEstimator(string file)
    {
        _file = file;
        try
        {
            if (File.Exists(file))
                _data = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(file)) ?? new HistoryFile();
            if (_data.Version != CurrentVersion) _data = new HistoryFile();
        }
        catch (Exception) { _data = new HistoryFile(); }
    }

    public bool IsSeeded { get { lock (_lock) return _data.Seeded; } }

    public void MarkSeeded()
    {
        lock (_lock) { _data.Seeded = true; _dirty = true; }
        Save();
    }

    public void Record(string command, string description, double seconds, DateTimeOffset ended)
    {
        if (seconds < MinSecondsToRecord || seconds > 24 * 3600) return;
        lock (_lock)
        {
            foreach (var key in KeysFor(command, description))
            {
                if (!_data.Keys.TryGetValue(key, out var list)) _data.Keys[key] = list = new List<Sample>();
                list.Add(new Sample { End = ended.ToUnixTimeSeconds(), Sec = Math.Round(seconds, 1) });
                list.Sort((a, b) => a.End.CompareTo(b.End));
                if (list.Count > MaxSamplesPerKey) list.RemoveRange(0, list.Count - MaxSamplesPerKey);
            }
            _dirty = true;
        }
    }

    public Estimate? EstimateFor(string command, string description)
    {
        lock (_lock)
        {
            foreach (var key in KeysFor(command, description))
            {
                if (_data.Keys.TryGetValue(key, out var list) && list.Count > 0)
                {
                    var recent = list.Skip(Math.Max(0, list.Count - SamplesForMedian)).Select(s => s.Sec).OrderBy(x => x).ToList();
                    double median = recent.Count % 2 == 1
                        ? recent[recent.Count / 2]
                        : (recent[recent.Count / 2 - 1] + recent[recent.Count / 2]) / 2;
                    return new Estimate(median, recent.Count);
                }
            }
            return null;
        }
    }

    public void Save()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(_data);
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>Signature keys from most to least specific.</summary>
    internal static IEnumerable<string> KeysFor(string command, string description)
    {
        var keys = new List<string>(3);
        string norm = Normalize(command);
        if (norm.Length > 0) keys.Add("c:" + norm);
        string coarse = CoarseKey(command);
        string desc = Normalize(description);
        if (desc.Length > 0 && desc != norm) keys.Add("d:" + desc);
        if (coarse.Length > 0) keys.Add("k:" + coarse);
        return keys.Distinct();
    }

    internal static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.ToLowerInvariant();
        s = TempPathRegex().Replace(s, "<tmp>");
        s = GuidRegex().Replace(s, "<id>");
        s = DigitsRegex().Replace(s, "#");
        s = WhitespaceRegex().Replace(s, " ").Trim();
        return s.Length > 300 ? s[..300] : s;
    }

    /// <summary>"dotnet build", "npm run test", "unity -executemethod foo.bar" …</summary>
    internal static string CoarseKey(string command)
    {
        // Unity batch runs are identified by the method they execute, wherever it appears.
        var em = ExecuteMethodRegex().Match(command);
        if (em.Success) return "unity executemethod " + em.Groups[1].Value.ToLowerInvariant();

        // Otherwise look only at the program each statement actually runs (not its arguments).
        foreach (var segment in SegmentRegex().Split(command))
        {
            var tokens = TokenRegex().Matches(segment).Select(m => m.Value.Trim('"', '\'', '`', '(', ')', '{', '}')).Where(t => t.Length > 0).ToList();
            int i = 0;
            if (tokens.Count >= 2 && tokens[0].StartsWith('$') && tokens[1] == "=") i = 2;          // $x = cmd
            while (i < tokens.Count && (tokens[i] is "&" or "call" or "time" or "exec" || tokens[i].Contains('=')))
                i++;                                                                                  // & "x.exe", FOO=1 cmd
            if (i < tokens.Count && tokens[i].Equals("Start-Process", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                while (i < tokens.Count && tokens[i].StartsWith('-')) i += 2;                         // -FilePath x
            }
            if (i >= tokens.Count) continue;

            string baseName = ToolName(tokens[i]);
            if (!KnownTools.Contains(baseName)) continue;
            var parts = new List<string> { baseName };
            for (int j = i + 1; j < tokens.Count && parts.Count < 3; j++)
            {
                string tk = tokens[j];
                if (!PlainWordRegex().IsMatch(tk)) continue;
                parts.Add(tk.ToLowerInvariant());
            }
            // A bare tool name ("git", "python") is too vague to estimate from.
            if (parts.Count >= 2) return string.Join(' ', parts);
        }
        return "";
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z_-]{1,30}$")]
    private static partial Regex PlainWordRegex();

    [GeneratedRegex(@"-executeMethod\s+([A-Za-z_][\w.]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecuteMethodRegex();

    [GeneratedRegex(@"\r?\n|;|\|\||&&|\||\bif\b|\{|\}")]
    private static partial Regex SegmentRegex();

    private static string ToolName(string token)
    {
        string t = token.Replace('/', '\\');
        int slash = t.LastIndexOf('\\');
        if (slash >= 0) t = t[(slash + 1)..];
        if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            t = t[..^4];
        return t.ToLowerInvariant();
    }

    [GeneratedRegex(@"[a-z]:\\users\\[^\\]+\\appdata\\local\\temp\\[^\s""']*|/tmp/[^\s""']*")]
    private static partial Regex TempPathRegex();

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"""[^""]*""|'[^']*'|\S+")]
    private static partial Regex TokenRegex();
}
