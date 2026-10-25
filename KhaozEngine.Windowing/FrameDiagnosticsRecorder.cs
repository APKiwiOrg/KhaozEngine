using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing;

internal readonly record struct FrameDiagnosticsWindowState(bool Focused, bool Minimized, bool RenderSuppressed,
    int Width, int Height, int EffectiveBaseCapHz, bool Vsync, bool RendererInitializesSwapchain = false,
    bool PreparePhaseSplitActive = false, long PreparePhaseSubmissionCount = 0, long PreparePhaseCompositeCount = 0);

internal sealed partial class FrameDiagnosticsRecorder : IDisposable
{
    enum Phase { Waiting, Armed, Sampling, Stopped }

    readonly string _path;
    readonly string _trigger;
    readonly long _frequency;
    readonly FrameDiagnosticsSample[] _samples;
    Phase _phase;
    long? _lastPoll;
    FrameDiagnosticsBoundary _baseline;
    FrameDiagnosticsBoundary _previous;
    int _count;

    internal FrameDiagnosticsRecorder(string path, long frequency, int capacity = 4096, ShadowStageProbeMode mode = ShadowStageProbeMode.Off)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute result path is required.", nameof(path));
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (capacity < 1 || capacity > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (mode is < ShadowStageProbeMode.Off or > ShadowStageProbeMode.Timings) throw new ArgumentOutOfRangeException(nameof(mode));
        _shadowMode = mode;
        _path = Path.GetFullPath(path);
        if (File.Exists(_path) || Directory.Exists(_path)) throw new IOException("Result already exists.");
        if (!Directory.Exists(Path.GetDirectoryName(_path))) throw new DirectoryNotFoundException("Result directory must exist.");
        _trigger = _path + ".start";
        _frequency = frequency;
        _samples = new FrameDiagnosticsSample[capacity];
    }

    internal static FrameDiagnosticsRecorder? FromEnvironment()
    {
        try
        {
            string? path = Environment.GetEnvironmentVariable("KE_FRAME_DIAGNOSTICS_PATH");
            string? mode = Environment.GetEnvironmentVariable("KE_SHADOW_STAGE_MODE");
            return FromSettings(path, mode);
        }
        catch (Exception error)
        {
            Log("KE frame diagnostics failed to initialize (" + error.GetType().Name + ": " + error.Message + "). No result will be published.");
            return null;
        }
    }

    internal Task Publication { get; private set; } = Task.CompletedTask;
    internal string? Failure { get; private set; }

    // Called before the next frame does any work. Counters include the entire PREVIOUS callback,
    // including prepare, update, render, UI, capture, submit and present. No SnapshotTool is involved.
    internal void Observe(double dt, long timestamp, FrameDiagnosticsWindowState window, GpuDeviceCounters counters)
        => Observe(dt, timestamp, window, counters, default);

    internal void Observe(double dt, long timestamp, FrameDiagnosticsWindowState window, GpuDeviceCounters counters,
        in ShadowStageCallbackResult completed)
    {
        if (_phase == Phase.Stopped) return;
        if (_phase == Phase.Waiting)
        {
            PollTrigger(timestamp);
            return;
        }

        var boundary = new FrameDiagnosticsBoundary(timestamp, window, counters);
        if (_phase == Phase.Armed)
        {
            // Trigger I/O was on the previous callback. Its partial interval and dt are discarded.
            _baseline = _previous = boundary;
            _shadowSequence = 1;
            _phase = Phase.Sampling;
            return;
        }

        if (timestamp <= _previous.Timestamp)
        {
            Fail("non-increasing monotonic boundary");
            return;
        }
        _samples[_count++] = new FrameDiagnosticsSample(_previous.Timestamp, timestamp,
            (timestamp - _previous.Timestamp) / (double)_frequency, dt, _previous.Window, window, counters,
            ShadowRow(completed, _previous.Window.RenderSuppressed));
        _shadowSequence++;
        _previous = boundary;
        bool durationReached = (timestamp - _baseline.Timestamp) / (double)_frequency >= 10;
        if (!durationReached && _count < _samples.Length) return;

        // The final boundary has been captured. Everything allocating, formatting or doing I/O is
        // outside the measured interval. Neither this buffer nor its boundaries are modified again.
        _phase = Phase.Stopped;
        try { Publication = Task.Run(Publish); }
        catch (Exception error) { Fail("publication could not start (" + error.GetType().Name + ")"); }
    }

    void PollTrigger(long timestamp)
    {
        if (_lastPoll.HasValue && (timestamp - _lastPoll.Value) / (double)_frequency < 0.5) return;
        _lastPoll = timestamp;
        try
        {
            File.GetAttributes(_trigger); // Unlike File.Exists, permission and I/O errors remain explicit.
            File.Delete(_trigger);
            _phase = Phase.Armed;
        }
        catch (FileNotFoundException) { }
        catch (Exception error) { Fail("trigger could not be consumed (" + error.GetType().Name + ")"); }
    }

    void Publish()
    {
        string reason = (_previous.Timestamp - _baseline.Timestamp) / (double)_frequency >= 10 ? "duration" : "capacity";
        string? temporary = null;
        try
        {
            var report = FrameDiagnosticsReport.Create(_frequency, _baseline, _previous, _samples, _count, reason, _shadowMode);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, report, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                });
                stream.Flush(true);
            }
            File.Move(temporary, _path, overwrite: false);
            Log("KE frame diagnostics complete. " + _count + " intervals, " + reason + ", " + _path);
        }
        catch (Exception error) { Fail("publication failed (" + error.GetType().Name + ")"); }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) { Log("KE frame diagnostics temporary cleanup failed (" + error.GetType().Name + ")."); }
            }
        }
    }

    void Fail(string reason)
    {
        _phase = Phase.Stopped;
        Failure = reason;
        Log("KE frame diagnostics failed. " + reason + ". No completed result was published.");
    }

    static void Log(string message)
    {
        try { Console.WriteLine(message); }
        catch { /* Diagnostic output must not take down the game. */ }
    }

    public void Dispose()
    {
        if (_phase is Phase.Armed or Phase.Sampling) Fail("closed before measurement completed");
        _phase = Phase.Stopped;
        Publication.GetAwaiter().GetResult(); // Loop exit joins an already started, exception-contained writer.
    }
}
