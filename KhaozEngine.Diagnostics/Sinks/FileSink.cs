using System;
using System.IO;
using System.Text;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Writes formatted entries to a file with rotate-on-launch plus optional size-based rotation and
/// retention. Uses an <c>AutoFlush</c> writer so entries survive a hard crash. Thread-safe; never throws.
/// </summary>
public sealed class FileSink : ILogSink
{
    private readonly object gate = new();
    private readonly FileSinkOptions options;
    private readonly Func<LogEntry, string> formatter;
    private StreamWriter? writer;
    private long bytesWritten;

    /// <summary>Opens the sink, performing rotate-on-launch if configured.</summary>
    public FileSink(FileSinkOptions options, IClock? clock = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Path)) throw new ArgumentException("FileSinkOptions.Path is required.", nameof(options));
        formatter = options.Formatter ?? (e => LogFormatter.Format(e));
        // clock is currently unused (archive naming is index-based, not time-based); accepted for API symmetry.
        Open();
    }

    private void Open()
    {
        try
        {
            string? dir = Path.GetDirectoryName(options.Path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            if (!string.IsNullOrWhiteSpace(options.PreviousPath) && File.Exists(options.Path))
            {
                try { File.Copy(options.Path, options.PreviousPath!, overwrite: true); }
                catch { /* best-effort rotation */ }
            }

            writer = new StreamWriter(options.Path, append: false, Encoding.UTF8) { AutoFlush = true };
            bytesWritten = 0;
        }
        catch
        {
            writer = null;   // fall back silently; Emit becomes a no-op
        }
    }

    /// <inheritdoc />
    public void Emit(in LogEntry entry)
    {
        if (options.MinimumLevel.HasValue && entry.Level < options.MinimumLevel.Value) return;
        lock (gate)
        {
            if (writer is null) return;
            try
            {
                string line = formatter(entry);
                writer.WriteLine(line);
                // Char count, not encoded byte count: UTF-8 multibyte content rotates slightly late
                // (never early), so a file can modestly exceed MaxBytes. Fine as a rotation threshold.
                bytesWritten += line.Length + Environment.NewLine.Length;
                if (options.MaxBytes is long max && bytesWritten >= max)
                {
                    RollBySize();
                }
            }
            catch { /* never throw */ }
        }
    }

    private void RollBySize()
    {
        try
        {
            writer!.Flush();
            writer.Dispose();
            writer = null;

            int keep = options.MaxFiles is int n && n > 0 ? n : 1;

            string oldest = options.Path + "." + keep;
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = keep - 1; i >= 1; i--)
            {
                string src = options.Path + "." + i;
                string dst = options.Path + "." + (i + 1);
                if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            }
            if (File.Exists(options.Path)) File.Move(options.Path, options.Path + ".1", overwrite: true);

            writer = new StreamWriter(options.Path, append: false, Encoding.UTF8) { AutoFlush = true };
            bytesWritten = 0;
        }
        catch
        {
            writer = null;
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        lock (gate)
        {
            try { writer?.Flush(); }
            catch { /* best-effort */ }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            try { writer?.Flush(); writer?.Dispose(); }
            catch { /* best-effort */ }
            writer = null;
        }
    }
}
