using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Tests.Gpu.Performance;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// The caller owns the native device. Each session owns a fresh workload, frame host and timestamp ring.
internal sealed class RendererPerfNativeSession : IRendererPerfSession
{
    readonly IGpuDevice _native, _device;
    readonly RendererPerfConstructionCapture? _capture;
    readonly RendererPerfSessionInfo _info;
    readonly RendererPerfBlock _block;
    readonly RendererPerfClock _clock;
    readonly RendererPerfRecorder _recorder;
    readonly RendererPerfPassObserver _observer;
    readonly GpuPerfTimestampRing _ring;
    readonly GpuPerfTimestampResult?[] _results;
    readonly IRendererPerfNativeWork _work;
    readonly Frame _frame;
    readonly Action<Frame> _prepare, _record;
    readonly Func<ImmutableDictionary<string, RendererPerfMetric>>? _telemetry;
    readonly RendererPerfStartupReport _startup;
    int _next, _consumed, _frameIndex;
    bool _disposed;
    RendererPerfCpuSpan? _drain, _harvest;
    string? _failure;

    internal RendererPerfNativeSession(IGpuDevice device, RendererPerfSessionInfo info,
        RendererPerfBlock block, RendererPerfPassDefinition[] passes, RendererPerfClock clock,
        Func<IGpuDevice, IRenderPassObserver, IRendererPerfNativeWork> createWork,
        Func<ImmutableDictionary<string, RendererPerfMetric>>? telemetry = null,
        RendererPerfConstructionCapture? capture = null, int operationOrdinal = 0)
    {
        RendererPerfAdmission.Device(device, info);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clock.Frequency);
        long coldStart = info.Startup?.Wall.StartTicks ?? clock.Now();
        _native = device; _info = info; _block = block; _clock = clock; _telemetry = telemetry;
        int count = checked(block.WarmupFrames + block.MeasuredFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        _results = new GpuPerfTimestampResult?[count];
        uint markerCount = block.Layout == RendererPerfLayout.TotalOnly ? 2 : checked((uint)Array.FindAll(passes, p => !p.DeviceOnly).Length * 2 + 2);
        GpuPerfTimestampRing? ring = null;
        IRendererPerfNativeWork? work = null;
        IGpuCommandList? hostCommands = null;
        Frame? frame = null;
        _capture = capture;
        capture?.BeginOperation(operationOrdinal);
        try
        {
            _ring = ring = new(device, count, markerCount, (id, result) => _results[id] = result);
            _recorder = new(count, block.Layout, passes, clock.Now, clock.Frequency, _ring);
            _observer = new(_recorder, block.Layout, passes);
            _device = capture is null ? RendererPerfDevice.Wrap(device, _recorder) : RendererPerfDevice.Wrap(device, _recorder, capture);
            _work = work = createWork(_device, _observer);
            ((RendererPerfDevice)_device).UseFrameTarget(work.Target);
            IGpuCommandList commands = hostCommands = _device.Factory.CreateCommandList();
            _recorder.FrameCommands = RendererPerfCommandList.Unwrap(commands);
            _frame = frame = new()
            {
                Commands = commands,
                Dt = RendererPerfRecipe.Dt,
                Width = (int)work.Target.Width,
                Height = (int)work.Target.Height
            };
            // Allocate host completion slots outside frame brackets, with the production host's owner.
            frame.BeginSubmission(_device);
            frame.Submission.Abandon();
            work.PrepareCold(-block.WarmupFrames);
            _prepare = Prepare; _record = Record;
            long coldEnd = clock.Now();
            var coldMetrics = ImmutableDictionary.CreateBuilder<string, RendererPerfMetric>();
            foreach (var counter in Enum.GetValues<RendererPerfCounter>())
                coldMetrics.Add(counter.ToString(), RendererPerfDriver.Value(_recorder.ReadCounter(-1, counter),
                    counter.ToString().EndsWith("Bytes", StringComparison.Ordinal) ? "bytes" : "count", "cold logical device commands"));
            _startup = new(block.SceneId, new("startup.cold", null, coldStart, coldEnd), clock.Frequency, coldMetrics.ToImmutable());
        }
        catch (Exception error)
        {
            var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
            RendererPerfFailures.Add(failures, "renderer.session.construct", error);
            RendererPerfFailures.Release(failures, "renderer.session.dispose.submission.3", frame is null ? null : frame.DisposeSubmission);
            if (frame is null || !frame.HasSubmission || frame.Submission.TeardownCompleted)
            {
                RendererPerfFailures.Release(failures, "renderer.session.dispose.commands.2", hostCommands is null ? null : hostCommands.Dispose);
                RendererPerfFailures.Release(failures, "renderer.session.dispose.work.1", work is null ? null : work.Dispose);
                RendererPerfFailures.Release(failures, "renderer.session.dispose.timestamps.0", ring is null ? null : ring.Dispose);
            }
            RendererPerfFailures.Release(failures, "renderer.session.dispose.capture.4", capture is null ? null : capture.EndOperation);
            throw new GpuDiagnosticFailureException(failures.ToImmutable());
        }
    }

    public void Frame(int frameIndex, ref RendererPerfFrameSample sample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_next == _results.Length || _next - _consumed >= 3 || frameIndex != _next - _block.WarmupFrames)
            throw new InvalidOperationException("The fixed frame count, sequence or three-frame pacing was violated.");
        if (_capture?.Failure is { } captureFailure) throw new InvalidOperationException(captureFailure);
        _capture?.EnterFrame(frameIndex < 0 ? RendererPerfConstructionStage.Warmup : RendererPerfConstructionStage.Measured, frameIndex);
        try
        {
            _frameIndex = frameIndex;
            _observer.BeginFrame();
            long start = _clock.Now();
            _recorder.BeginFrame(_next, _next, RendererPerfRecipe.SequenceIndex(frameIndex));
            ImmutableArray<GpuDiagnosticFailure>.Builder? failures = null;
            try
            {
                FramePhases.Run(_frame, true, _device, _frame.Commands, _work.ClearColor, _prepare, _record);
            }
            catch (Exception error)
            {
                _failure ??= error is GpuDiagnosticFailureException transport ? transport.Failures[0].Error.Message : error.Message;
                RendererPerfFailures.Add(ref failures, "renderer.frame", error);
                try { _recorder.Fail(error is GpuDiagnosticFailureException frameTransport ? frameTransport.Failures[0].Error.Message : error.Message); }
                catch (Exception recovery) { RendererPerfFailures.Add(ref failures, "renderer.frame.finalize", recovery); }
            }
            sample = new(_next, RendererPerfRecipe.SequenceIndex(frameIndex), start, start, _clock.Frequency,
                null, null, new(null, "ns", "native timestamp ring", RendererPerfMetricStatus.Unsupported,
                    "Awaiting the one batch completion harvest."), [], [], ImmutableDictionary<string, RendererPerfMetric>.Empty,
                []);
            try
            {
                if (_recorder.ReadCounter(_next, RendererPerfCounter.Submissions) != 1)
                    _recorder.Fail("An unexpected production submission occurred, including preparation.");
                bool conditioning = _block.SceneId.StartsWith("conditioning:", StringComparison.Ordinal);
                if (_recorder.ReadCounter(_next, RendererPerfCounter.WaitForIdleCalls) != 0)
                    _recorder.Fail("An unexpected production wait occurred.");
                if (frameIndex >= 0 && !conditioning && _recorder.ReadCounter(_next, RendererPerfCounter.ResourceCreations) != 0)
                    _recorder.Fail("Hot resource growth occurred in a steady frame.");
            }
            catch (Exception error) { RendererPerfFailures.Add(ref failures, "renderer.frame.finalize", error); }
            try
            {
                if (_native.Diagnostics.IsDeviceLost) _recorder.Fail(_native.Diagnostics.DeviceLossReason!);
            }
            catch (Exception error) { RendererPerfFailures.Add(ref failures, "renderer.frame.finalize", error); }
            // Optional observation failure cannot leave the recorder open or erase already recorded raw work.
            try { _recorder.EndFrame(); }
            catch (Exception error) { RendererPerfFailures.Add(ref failures, "renderer.frame.finalize", error); }
            try
            {
                string? error = _recorder.FrameFailure(_next);
                sample = new(_next, RendererPerfRecipe.SequenceIndex(frameIndex), start, _clock.Now(), _clock.Frequency,
                    null, null, new(null, "ns", "native timestamp ring", RendererPerfMetricStatus.Unsupported,
                        "Awaiting the one batch completion harvest."), [], [], ImmutableDictionary<string, RendererPerfMetric>.Empty,
                        error is null ? [] : [error]);
            }
            catch (Exception error) { RendererPerfFailures.Add(ref failures, "renderer.frame.finalize", error); }
            finally { _next++; }
            if (failures is not null) throw new GpuDiagnosticFailureException(failures.ToImmutable());
        }
        finally { _capture?.LeaveFrame(); }
    }

    void Prepare(Frame frame)
    {
        _recorder.UseSubmission(frame.Submission);
        _recorder.BeginCpuPhase("prepare");
        ImmutableArray<GpuDiagnosticFailure>.Builder? failures = null;
        try { _work.Prepare(_frameIndex, frame.Submission); }
        catch (Exception error) { RendererPerfFailures.Add(ref failures, "renderer.frame", error); }
        try { _recorder.EndCpuPhase("prepare"); }
        catch (Exception error) { RendererPerfFailures.Add(ref failures, "renderer.frame.finalize", error); }
        if (failures is not null) throw new GpuDiagnosticFailureException(failures.ToImmutable());
    }
    void Record(Frame frame) => _work.Record(frame.Commands, _frameIndex);

    public void CompleteBatch(Span<RendererPerfFrameSample> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samples.Length == 0 || samples.Length > 3 || samples.Length != _next - _consumed)
            throw new InvalidOperationException("A batch must consume every pending frame exactly once.");
        for (int i = 0; i < samples.Length; i++)
            if (samples[i].FrameId != _consumed + i) throw new InvalidOperationException("A foreign or duplicated frame entered harvest.");
        _drain = _harvest = null;
        long start = _clock.Now();
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        try { _native.WaitForIdle(); }
        catch (Exception error) { _failure ??= error is GpuDiagnosticFailureException transport ? transport.Failures[0].Error.Message : error.Message; RendererPerfFailures.Add(failures, "renderer.batch.complete", error); }
        long drained = _clock.Now();
        if (failures.Count == 0)
        {
            _drain = new("batch.completionDrain", null, start, drained);
            try { _ring.PollCompleted(); }
            catch (Exception error) { RendererPerfFailures.Add(failures, "renderer.batch.complete", error); }
        }
        try { HarvestPending(samples); }
        catch (Exception error) { RendererPerfFailures.Add(failures, "renderer.batch.harvest", error); }
        finally { if (_drain is not null) _harvest = new("batch.harvest", null, drained, _clock.Now()); }
        if (failures.Count == 0 && _next == _results.Length)
        {
            try
            {
                if (!_ring.CompleteBlock()) _failure ??= _ring.Failure;
                _work.ValidateOutput();
            }
            catch (Exception error) { RendererPerfFailures.Add(failures, "renderer.batch.complete", error); }
        }
        if (failures.Count != 0) throw new GpuDiagnosticFailureException(failures.ToImmutable());
    }

    void HarvestPending(Span<RendererPerfFrameSample> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            var result = _results[_consumed + i];
            samples[i] = _recorder.Harvest(_consumed + i, result);
            if (result is { Valid: true } && result.Period != _info.Identity.TimestampPeriodNs)
                samples[i] = samples[i] with { Errors = samples[i].Errors.Add("The native timestamp period changed from admission.") };
        }
        _consumed = _next;
    }

    public RendererPerfSessionInfo ReadInfo() => _info with
    {
        Recipe = _work.Recipe,
        ControlStateSha256 = _work.ControlStateSha256,
        Telemetry = RendererPerfSessionObservations.Telemetry(_telemetry?.Invoke() ?? _info.Telemetry).SetItems(_work.ControlMetrics),
        Memory = RendererPerfSessionObservations.Memory(_native),
        Failure = _failure ?? _ring.Failure ?? _native.Diagnostics.DeviceLossReason ?? _capture?.Failure,
        Drain = _drain,
        Harvest = _harvest,
        Startup = _startup
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        RendererPerfFailures.Release(failures, "renderer.session.dispose.submission.3", _frame.DisposeSubmission);
        if (failures.Count == 0 || _frame.Submission.TeardownCompleted)
        {
            RendererPerfFailures.Release(failures, "renderer.session.dispose.timestamps.0", _ring.Dispose);
            RendererPerfFailures.Release(failures, "renderer.session.dispose.commands.2", _frame.Commands.Dispose);
            RendererPerfFailures.Release(failures, "renderer.session.dispose.work.1", _work.Dispose);
        }
        RendererPerfFailures.Release(failures, "renderer.session.dispose.capture.4", _capture is null ? null : _capture.EndOperation);
        if (failures.Count != 0) throw new GpuDiagnosticFailureException(failures.ToImmutable());
    }
}
