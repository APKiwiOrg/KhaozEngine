using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Windowing;

internal readonly record struct FrameDiagnosticsWindowState(bool Focused, bool Minimized, bool RenderSuppressed,
    int Width, int Height, int EffectiveBaseCapHz, bool Vsync, bool RendererInitializesSwapchain = false);

internal sealed class FrameDiagnosticsRecorder : IDisposable
{
    enum Phase { Waiting, Armed, Sampling, Stopped }

    readonly string _path;
    readonly string _trigger;
    readonly long _frequency;
    readonly FrameDiagnosticsSample[] _samples;
    readonly FrameTimingCapture _timing;
    readonly FrameDiagnosticsAnnotations _annotations;
    readonly FrameDiagnosticsShaderGateSchedule _shaderGates;
    readonly FrameEarlySubmitCapture _earlySubmit;
    readonly int? _framesInFlight;
    FrameDiagnosticsCaptureState _baselineCapture, _endCapture;
    FrameTimingClockMap _baselineClock, _endClock;
    Phase _phase;
    long? _lastPoll;
    FrameDiagnosticsBoundary _baseline;
    FrameDiagnosticsBoundary _previous;
    int _count;

    internal FrameDiagnosticsRecorder(string path, long frequency, int capacity = 4096,
        IFrameDiagnosticsAnnotator? annotator = null, IGpuDevice? device = null, IGpuCommandList? frameCommands = null,
        IFrameDiagnosticsShaderGates? shaderGates = null, bool shaderGateBlocks = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute result path is required.", nameof(path));
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (capacity < 1 || capacity > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        _path = Path.GetFullPath(path);
        if (File.Exists(_path) || Directory.Exists(_path)) throw new IOException("Result already exists.");
        if (!Directory.Exists(Path.GetDirectoryName(_path))) throw new DirectoryNotFoundException("Result directory must exist.");
        _trigger = _path + ".start";
        _frequency = frequency;
        _samples = new FrameDiagnosticsSample[capacity];
        _annotations = new(annotator, capacity);
        _shaderGates = new(shaderGates, capacity, rotate: shaderGateBlocks);
        _earlySubmit = new(capacity);
        _timing = new(frameCommands, passTiming: CreatePassTiming(device), earlySubmit: _earlySubmit);
        _framesInFlight = (device as MetalGpuDevice)?.DiagnosticFramesInFlight;
        MetalFrameTimingNative.Prepare();
    }

    static MetalPassTimingCapture? CreatePassTiming(IGpuDevice? device) => OperatingSystem.IsMacOS() && device is MetalGpuDevice metal
        ? CreateMetalPassTiming(metal) : null;

    [SupportedOSPlatform("macos")]
    static MetalPassTimingCapture CreateMetalPassTiming(MetalGpuDevice device) => new(new MetalPassTimingNative(device.Handle.Handle));

    internal static bool IsEnabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KE_FRAME_DIAGNOSTICS_PATH"));
    internal bool NeedsBoundary => _phase is Phase.Armed or Phase.Sampling;

    internal static FrameDiagnosticsRecorder? FromEnvironment(IFrameDiagnosticsAnnotator? annotator = null,
        IGpuDevice? device = null, IGpuCommandList? frameCommands = null, IFrameDiagnosticsShaderGates? shaderGates = null)
    {
        try
        {
            string? path = Environment.GetEnvironmentVariable("KE_FRAME_DIAGNOSTICS_PATH");
            return string.IsNullOrEmpty(path) ? null : new FrameDiagnosticsRecorder(path, Stopwatch.Frequency, annotator: annotator, device: device, frameCommands: frameCommands, shaderGates: shaderGates);
        }
        catch (Exception error)
        {
            Log("KE frame diagnostics failed to initialize (" + error.GetType().Name + "). No result will be published.");
            return null;
        }
    }

    internal Task Publication { get; private set; } = Task.CompletedTask;
    internal string? Failure { get; private set; }

    // Called before the next frame does any work. Counters include the entire PREVIOUS callback,
    // including prepare, update, render, UI, capture, submit and present. No SnapshotTool is involved.
    internal void Observe(double dt, long timestamp, FrameDiagnosticsWindowState window, GpuDeviceCounters counters, FrameDiagnosticsCaptureState captureState = default)
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
            _baselineCapture = captureState;
            _baselineClock = MetalFrameTimingNative.ClockMap();
            _baseline = _previous = boundary;
            _timing.PassTiming?.Boundary(0, timestamp);
            try { _timing.Start(); }
            catch (Exception error) { Fail("sampling could not start (" + error.GetType().Name + ")"); return; }
            _phase = Phase.Sampling;
            _shaderGates.Boundary(0, timestamp);
            _earlySubmit.Boundary(0, timestamp);
            return;
        }

        if (timestamp <= _previous.Timestamp)
        {
            Fail("non-increasing monotonic boundary");
            return;
        }
        try { _annotations.Sample(_count); }
        catch (Exception error) { Fail("annotation failed (" + error.GetType().Name + ")"); return; }
        _shaderGates.CallbackEnded(_count);
        _samples[_count++] = new FrameDiagnosticsSample(_previous.Timestamp, timestamp,
            (timestamp - _previous.Timestamp) / (double)_frequency, dt, _previous.Window, window, counters);
        _previous = boundary;
        bool durationReached = (timestamp - _baseline.Timestamp) / (double)_frequency >= 10;
        if (!durationReached && _count < _samples.Length)
        {
            _timing.CallbackIndex = _count;
            _timing.PassTiming?.Boundary(_count, timestamp);
            _shaderGates.Boundary(_count, timestamp);
            _earlySubmit.Boundary(_count, timestamp);
            return;
        }
        _shaderGates.End(_count, timestamp);
        _earlySubmit.End(_count, timestamp);
        _timing.Stop();
        _timing.PassTiming?.End(_count, timestamp);
        _endCapture = captureState;
        _endClock = MetalFrameTimingNative.ClockMap();

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
            var report = FrameDiagnosticsReport.Create(_frequency, _baseline, _previous, _samples, _count, reason,
                _timing.Snapshot().Window(_baseline.Timestamp, _previous.Timestamp), _annotations.Report(_count), _framesInFlight, _baselineCapture, _endCapture,
                _baselineClock, _endClock);
            report = report.WithPassTiming(_timing.PassTiming).WithShaderGates(_shaderGates.Report(_count)).WithEarlySubmit(_earlySubmit.Snapshot(_count));
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
        _shaderGates.Stop();
        _timing.Stop();
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
        _shaderGates.Stop();
        _timing.Stop();
        _phase = Phase.Stopped;
        Publication.GetAwaiter().GetResult(); // Loop exit joins an already started, exception-contained writer.
        _timing.Dispose();
    }
}
