using System.Text;

namespace com.logdb.windows.iis;

/// <summary>
/// A TextWriter wrapper that prepends a timestamp to the start of each new line.
/// Partial writes (like progress dots) within a line are not timestamped.
/// </summary>
internal class TimestampedTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    private bool _lineStart = true;

    public TimestampedTextWriter(TextWriter inner) => _inner = inner;

    public override Encoding Encoding => _inner.Encoding;

    private void WriteTimestampIfNeeded()
    {
        if (_lineStart)
        {
            _inner.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss "));
            _lineStart = false;
        }
    }

    public override void Write(char value)
    {
        if (value == '\r') { _inner.Write(value); return; }
        if (value == '\n') { _inner.Write(value); _lineStart = true; return; }
        WriteTimestampIfNeeded();
        _inner.Write(value);
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (var c in value) Write(c);
    }

    public override void WriteLine() { _inner.WriteLine(); _lineStart = true; }

    public override void WriteLine(string? value)
    {
        WriteTimestampIfNeeded();
        _inner.WriteLine(value);
        _lineStart = true;
    }

    public override void Flush() => _inner.Flush();
}
