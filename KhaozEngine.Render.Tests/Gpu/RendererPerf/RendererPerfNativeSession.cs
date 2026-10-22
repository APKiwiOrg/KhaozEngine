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
    internal const string ConstructionCleanupErrors = "renderer-perf.construction-cleanup";
    readonly IGpuDevice _native, _device;
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
        Func<ImmutableDictionary<string, RendererPerfMetric>>? telemetry = null)
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
        try
        {
            _ring = ring = new(device, count, markerCount, (id, result) => _results[id] = result);
            _recorder = new(count, block.Layout, passes, clock.Now, clock.Frequency, _ring);
            _observer = new(_recorder, block.Layout, passes);
            _device = RendererPerfDevice.Wrap(device, _recorder);
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
            var cleanup = ImmutableArray.CreateBuilder<string>();
            void Release(Action? dispose)
            {
                try { dispose?.Invoke(); }
                catch (Exception failure) { cleanup.Add(failure.Message); }
            }
            Release(frame is null ? null : frame.DisposeSubmission);
            Release(hostCommands is null ? null : hostCommands.Dispose);
            Release(work is null ? null : work.Dispose);
            Release(ring is null ? null : ring.Dispose);
            if (cleanup.Count != 0) error.Data[ConstructionCleanupErrors] = cleanup.ToImmutable();
            throw;
        }
    }

    public void Frame(int frameIndex, ref RendererPerfFrameSample sample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_next == _results.Length || _next - _consumed >= 3 || frameIndex != _next - _block.WarmupFrames)
            throw new InvalidOperationException("The fixed frame count, sequence or three-frame pacing was violated.");
        _frameIndex = frameIndex;
        _observer.BeginFrame();
        long start = _clock.Now();
        _recorder.BeginFrame(_next, _next, RendererPerfRecipe.SequenceIndex(frameIndex));
        try
        {
            FramePhases.Run(_frame, true, _device, _frame.Commands, _work.ClearColor, _prepare, _record);
        }
        catch (Exception error)
        {
            _failure ??= error.Message;
            _recorder.Fail(error.Message);
            throw;
        }
        finally
        {
            if (_recorder.ReadCounter(_next, RendererPerfCounter.Submissions) != 1)
                _recorder.Fail("An unexpected production submission occurred, including preparation.");
            if (_recorder.ReadCounter(_next, RendererPerfCounter.WaitForIdleCalls) != 0)
                _recorder.Fail("An unexpected production wait occurred.");
            if (frameIndex >= 0 && !_block.SceneId.StartsWith("conditioning:", StringComparison.Ordinal) && _recorder.ReadCounter(_next, RendererPerfCounter.ResourceCreations) != 0)
                _recorder.Fail("Hot resource growth occurred in a steady frame.");
            if (_native.Diagnostics.IsDeviceLost) _recorder.Fail(_native.Diagnostics.DeviceLossReason!);
            _recorder.EndFrame();
            string? error = _recorder.FrameFailure(_next);
            sample = new(_next++, RendererPerfRecipe.SequenceIndex(frameIndex), start, _clock.Now(), _clock.Frequency,
                null, null, new(null, "ns", "native timestamp ring", RendererPerfMetricStatus.Unsupported,
                    "Awaiting the one batch completion harvest."), [], [], ImmutableDictionary<string, RendererPerfMetric>.Empty,
                    error is null ? [] : [error]);
        }
    }

    void Prepare(Frame frame)
    {
        _recorder.UseSubmission(frame.Submission);
        _recorder.BeginCpuPhase("prepare");
        try { _work.Prepare(_frameIndex); }
        finally { _recorder.EndCpuPhase("prepare"); }
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
        _native.WaitForIdle();
        long drained = _clock.Now();
        _drain = new("batch.completionDrain", null, start, drained);
        try { _ring.PollCompleted(); }
        finally
        {
            for (int i = 0; i < samples.Length; i++)
            {
                var result = _results[_consumed + i];
                samples[i] = _recorder.Harvest(_consumed + i, result);
                if (result is { Valid: true } && result.Period != _info.Identity.TimestampPeriodNs)
                    samples[i] = samples[i] with { Errors = samples[i].Errors.Add("The native timestamp period changed from admission.") };
            }
            _consumed = _next;
            _harvest = new("batch.harvest", null, drained, _clock.Now());
        }
        if (_next == _results.Length)
        {
            if (!_ring.CompleteBlock()) _failure ??= _ring.Failure;
            _work.ValidateOutput();
        }
    }

    public RendererPerfSessionInfo ReadInfo() => _info with
    {
        Recipe = _work.Recipe,
        ControlStateSha256 = _work.ControlStateSha256,
        Telemetry = RendererPerfSessionObservations.Telemetry(_telemetry?.Invoke() ?? _info.Telemetry),
        Memory = RendererPerfSessionObservations.Memory(_native),
        Failure = _failure ?? _ring.Failure ?? _native.Diagnostics.DeviceLossReason,
        Drain = _drain,
        Harvest = _harvest,
        Startup = _startup
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? failure = null;
        void Release(Action dispose)
        {
            try { dispose(); }
            catch (Exception error) { failure ??= error; }
        }
        // Submission teardown releases every retained fence before its pools and scene resources.
        Release(_frame.DisposeSubmission);
        Release(_ring.Dispose);
        Release(_frame.Commands.Dispose);
        Release(_work.Dispose);
        if (failure is not null) throw new InvalidOperationException("Native session cleanup failed.", failure);
    }
}
