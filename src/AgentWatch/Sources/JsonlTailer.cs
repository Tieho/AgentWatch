using System.IO;
using System.Text;

namespace AgentWatch.Sources;

/// <summary>
/// Reads newly appended lines of a growing JSONL file. Remembers the byte offset so
/// huge transcripts are only read once; the first read can start near the end.
/// </summary>
public sealed class JsonlTailer
{
    private readonly long _initialTailBytes;
    private long _offset = -1;
    private bool _skipPartialFirstLine;
    private readonly MemoryStream _pending = new();

    public string Path { get; }

    /// <param name="initialTailBytes">On first read start this many bytes before EOF; negative = whole file.</param>
    public JsonlTailer(string path, long initialTailBytes)
    {
        Path = path;
        _initialTailBytes = initialTailBytes;
    }

    public void ReadNewLines(Action<string> onLine)
    {
        FileInfo fi = new(Path);
        if (!fi.Exists) return;
        long len = fi.Length;

        if (_offset < 0)
        {
            _offset = _initialTailBytes < 0 ? 0 : Math.Max(0, len - _initialTailBytes);
            _skipPartialFirstLine = _offset > 0;
        }
        if (len < _offset)
        {
            // File was truncated or replaced: start over.
            _offset = 0;
            _pending.SetLength(0);
            _skipPartialFirstLine = false;
        }
        if (len == _offset) return;

        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        fs.Seek(_offset, SeekOrigin.Begin);
        var buf = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            _offset += n;
            int start = 0;
            while (start < n)
            {
                int nl = Array.IndexOf(buf, (byte)'\n', start, n - start);
                if (nl < 0)
                {
                    _pending.Write(buf, start, n - start);
                    break;
                }
                _pending.Write(buf, start, nl - start);
                EmitPending(onLine);
                start = nl + 1;
            }
        }
    }

    private void EmitPending(Action<string> onLine)
    {
        if (_skipPartialFirstLine)
        {
            _skipPartialFirstLine = false;
            _pending.SetLength(0);
            return;
        }
        if (_pending.Length > 0)
        {
            string line = Encoding.UTF8.GetString(_pending.GetBuffer(), 0, (int)_pending.Length).TrimEnd('\r');
            _pending.SetLength(0);
            if (_pending.Capacity > 1 << 20) _pending.Capacity = 64 * 1024; // don't hold on to huge-line buffers
            if (line.Length > 0) onLine(line);
        }
    }
}
