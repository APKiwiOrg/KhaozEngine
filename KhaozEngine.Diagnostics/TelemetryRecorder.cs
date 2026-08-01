using System;
using System.Collections.Generic;
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
/// Every recording opens with a <see cref="TelemetrySessionHeader"/> line, so a capture always says which
/// engine, build, and backend produced it (see <see cref="Start(string, TelemetrySessionInfo)"/>). The header
/// carries no <c>t</c> field and every sample row does, so a reader tells them apart on one key.
/// </para>
/// <para>
/// Not thread-safe: drive it from one thread (the game loop). The arm/confirm UX is the game's concern; this
/// is just the recording mechanism.
/// </para>
/// </summary>
public sealed class TelemetryRecorder : IDisposable
{
    StreamWriter? _writer;
    readonly StringBuilder _line = new(256);

    /// <summary>True between <see cref="Start(string, TelemetrySessionInfo)"/> and <see cref="Stop"/>.</summary>
    public bool IsRecording => _writer != null;

    /// <summary>The file currently being written, or null when not recording.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>
    /// Open <paramref name="path"/> for a fresh recording carrying only the identity the engine knows on its
    /// own (engine version and the set <c>KE_</c> levers). Prefer
    /// <see cref="Start(string, TelemetrySessionInfo)"/>, which also records the app and GPU identity.
    /// </summary>
    public void Start(string path) => Start(path, null);

    /// <summary>
    /// Open <paramref name="path"/> for a fresh recording (truncating any existing file and creating parent
    /// directories) and write the session header as its first line. Stops any recording already in progress
    /// first. The header is resolved here, at start, so it describes the run being recorded and not whatever
    /// the process looked like later.
    /// </summary>
    /// <param name="path">The file to write. Parent directories are created.</param>
    /// <param name="session">
    /// The app, GPU, and game-owned identity to record, or null for the engine-only header. The engine version
    /// and the <c>KE_</c> environment levers are always read by the engine itself and are not taken from here.
    /// </param>
    public void Start(string path, TelemetrySessionInfo? session)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        Stop();

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // FileShare.Read lets a reader (or a crash-recovery tool) open the partial file while we write.
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        CurrentPath = path;

        // Flushed like every other line, so even a recording that dies before its first sample still says
        // what produced it.
        //
        // Guarded because the header is an ADDITION to what Start used to do, and it must not add a way for
        // Start to fail. An unguarded throw here would leave the recorder open and recording (the file is
        // already created and CurrentPath is already set) while the caller sees an exception, which is the worst
        // of both, and it would contradict this package's standing "logging never throws" posture. A header the
        // machine would not let us write degrades to a headerless recording instead: the samples are the point.
        try
        {
            _writer.WriteLine(TelemetrySessionHeader.Build(session));
            _writer.Flush();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
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
        TelemetryJson.AppendNumber(_line, elapsedSeconds);
        if (channels != null)
        {
            for (int i = 0; i < channels.Count; i++)
            {
                TelemetryChannel c = channels[i];
                _line.Append(',');
                TelemetryJson.AppendKey(_line, c.Name);
                TelemetryJson.AppendNumber(_line, c.Value);
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
}
