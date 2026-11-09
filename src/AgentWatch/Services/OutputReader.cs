using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AgentWatch.Models;

namespace AgentWatch.Services;

/// <summary>Reads the tail of a background task's output file: last line and any progress it reports.</summary>
public static partial class OutputReader
{
    public static OutputInfo? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            if (len == 0) return new OutputInfo(null, null);
            int take = (int)Math.Min(len, 8192);
            fs.Seek(len - take, SeekOrigin.Begin);
            var buf = new byte[take];
            int read = fs.Read(buf, 0, take);
            string text = Encoding.UTF8.GetString(buf, 0, read);
            text = AnsiRegex().Replace(text, "");

            // Progress bars rewrite the line with \r: keep only the latest segment of each line.
            var lines = text.Split('\n')
                .Select(l => { int cr = l.TrimEnd('\r').LastIndexOf('\r'); return (cr >= 0 ? l[(cr + 1)..] : l).Trim(); })
                .Where(l => l.Length > 0)
                .ToList();
            if (take < len && lines.Count > 1) lines.RemoveAt(0); // first line is likely partial

            string? last = lines.Count > 0 ? lines[^1] : null;
            if (last is { Length: > 300 }) last = last[..300] + "…";

            double? pct = null;
            for (int i = lines.Count - 1; i >= Math.Max(0, lines.Count - 5) && pct == null; i--)
                pct = ParseProgress(lines[i]);

            return new OutputInfo(last, pct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static double? ParseProgress(string line)
    {
        double? best = null;
        int bestPos = -1;
        foreach (Match m in FractionRegex().Matches(line))
        {
            int g = m.Groups[1].Success ? 1 : 3;
            double a = double.Parse(m.Groups[g].Value), b = double.Parse(m.Groups[g + 1].Value);
            if (b > 0 && a <= b && m.Index > bestPos) { best = a / b * 100; bestPos = m.Index; }
        }
        foreach (Match m in PercentRegex().Matches(line))
        {
            double v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (v <= 100 && m.Index > bestPos) { best = v; bestPos = m.Index; }
        }
        return best;
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"(\d{1,3}(?:\.\d+)?)\s?%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"\[\s*(\d+)\s*/\s*(\d+)\s*\]|\b(?:step|test|file|item)s?\s+(\d+)\s+of\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FractionRegex();
}
