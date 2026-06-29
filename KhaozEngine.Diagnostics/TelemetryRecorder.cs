using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KhaozEngine.Diagnostics;

/// <summary>One named numeric channel sampled into a telemetry session (a raw value, not a display string).</summary>
public readonly record struct TelemetryChannel(string Name, double Value);

/// <summary>
/// Streams a telemetry session to a <see href="https://jsonlines.org">JSON Lines</see> file: one JSON object
/// per <see cref="Sample"/> call (<c>{"t":12.34,"fps":59.7,"rttMs":48,...}</c>), flushed after every line so a
/// crash leaves a valid partial file. It records raw numeric channels, not the overlay's formatted display
/// rows, so the output is chartable. Pure managed file IO, no renderer dependency.
/// <para>
/// Not thread-safe: drive it from one thread (the game loop). The arm/confirm UX is the game's concern; this
/// is just the recording mechanism.
/// </para>
/// </summary>
public sealed class TelemetryRecorder : IDisposable
{
    StreamWriter? _writer;
    readonly StringBuilder _line = new(256);

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsRecording => _writer != null;

    /// <summary>The file currently being written, or null when not recording.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>
    /// Open <paramref name="path"/> for a fresh recording (truncating any existing file and creating parent
    /// directories). Stops any recording already in progress first.
    /// </summary>
    public void Start(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        Stop();

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // FileShare.Read lets a reader (or a crash-recovery tool) open the partial file while we write.
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        CurrentPath = path;
    }

    /// <summary>
    /// Append one sample as a JSON object: <c>t</c> = <paramref name="elapsedSeconds"/> followed by each
    /// channel. No-op when not recording. The line is flushed to the underlying stream immediately.
    /// </summary>
    public void Sample(double elapsedSeconds, IReadOnlyList<TelemetryChannel> channels)
    {
        StreamWriter? w = _writer;
        if (w is null) return;

        _line.Clear();
        _line.Append("{\"t\":");
        AppendNumber(_line, elapsedSeconds);
        if (channels != null)
        {
            for (int i = 0; i < channels.Count; i++)
            {
                TelemetryChannel c = channels[i];
                _line.Append(",\"");
                AppendEscaped(_line, c.Name);
                _line.Append("\":");
                AppendNumber(_line, c.Value);
            }
        }
        _line.Append('}');

        w.WriteLine(_line.ToString());
        w.Flush();
    }

    /// <summary>Flush and close the file. Safe to call when not recording.</summary>
    public void Stop()
    {
        if (_writer is null) return;
        _writer.Flush();
        _writer.Dispose();
        _writer = null;
        CurrentPath = null;
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();

    static void AppendNumber(StringBuilder sb, double v)
    {
        // JSON has no NaN / Infinity literal; record those as null so every line stays valid JSON.
        if (double.IsNaN(v) || double.IsInfinity(v)) { sb.Append("null"); return; }
        sb.Append(v.ToString(CultureInfo.InvariantCulture)); // shortest round-trippable form
    }

    static void AppendEscaped(StringBuilder sb, string? s)
    {
        if (string.IsNullOrEmpty(s)) return;
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
    }
}
