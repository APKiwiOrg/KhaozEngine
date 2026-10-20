using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.ExceptionServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Gpu;

internal delegate CoveragePerfHost CoveragePerfHostFactory(CoveragePerfManifest manifest, CoveragePerfBlock block);

/// <summary>The native entrypoint supplies a device owner. Fakes explicitly disclose their semantic-only sink.</summary>
internal sealed record CoveragePerfHost(IGpuDevice Device, Func<CoverageCostGeometry, CoveragePerfEquipment> ImportEquipment,
    bool Synthetic, Action Release)
{
    internal Action<string, Frame>? Observe { get; init; }
}

/// <summary>Retained shared production host. No command-list capture wrapper enters the native path.</summary>
internal sealed class CoveragePerfNativeSession : ICoveragePerfSession
{
    readonly CoveragePerfClock _clock;
    readonly CoveragePerfHost _host;
    readonly CoveragePerfDevice _device;
    readonly IGpuTexture _colour;
    readonly IGpuFramebuffer _target;
    readonly IGpuCommandList _commands;
    readonly Frame _frame;
    readonly Action<Frame> _prepare, _record;
    readonly PendingFrame[] _pending;
    readonly CoverageGpuOutcome[] _bufferedOutcomes;
    readonly CoveragePerfTimerLayout _timerLayout;
    readonly CoveragePerfTimestampRing? _timestamps;
    long _aggregateStart, _aggregateTicks, _resolutionStart, _surfaceStart, _resolutionTicks, _surfaceTicks;
    int _frameIndex, _pendingCount, _nextFrame, _bufferedOutcomeCount;
    bool _presented, _disposed, _outcomeOverflow;
    int _endCreations, _endWaits, _endSubmits;
    GpuDeviceCounters _endCounters;
    internal CoveragePerfTypicalScene Typical { get; }
    internal IGpuDevice Device => _device;
    internal bool EffectiveRoutesValid => Typical.EffectiveRoutesValid;
    internal long HarnessDrainTicks { get; private set; }
    internal int HarnessDrains { get; private set; }
    internal int CompletedOutcomes => _pending.Sum(p => p.Completed);
    internal ReadOnlySpan<CoverageGpuOutcome> OutcomesForFrame(int batchIndex) => _pending[batchIndex].Outcomes;

    internal CoveragePerfNativeSession(CoveragePerfManifest manifest, CoveragePerfBlock block,
        CoveragePerfHostFactory factory, CoveragePerfClock clock)
    {
        if (block.Condition == CoveragePerfCondition.DirectOff)
            throw new NotSupportedException("DirectOff requires the Task 7 pinned legacy host.");
        if (clock.Frequency <= 0) throw new ArgumentOutOfRangeException(nameof(clock));
        if (block.Fixture != CoveragePerfManifest.FixtureVersion || manifest.Profile != CoveragePerfManifest.ProfileVersion)
            throw new ArgumentException("The session requires the approved typical/full fixture.");
        _clock = clock;
        _timerLayout = block.TimerLayout;
        _host = factory(manifest, block);
        _device = new(_host.Device);
        try
        {
            _colour = _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(CoveragePerfTypicalScene.Width,
                CoveragePerfTypicalScene.Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = _device.Factory.CreateFramebuffer(null, _colour);
            _device.Target = _target;
            var geometry = CoverageCostGeometry.Build(CoverageCostFixtureMode.Typical);
            Typical = new(_device, _target, block, geometry, _host.ImportEquipment(geometry));
            _commands = _device.Factory.CreateCommandList();
            _frame = new Frame
            {
                Commands = _commands,
                Width = CoveragePerfTypicalScene.Width,
                Height = CoveragePerfTypicalScene.Height,
                LogicalWidth = CoveragePerfTypicalScene.Width,
                LogicalHeight = CoveragePerfTypicalScene.Height,
                Dt = 1f / 60
            };
            _prepare = Prepare; _record = Record;
            _pending = Enumerable.Range(0, CoveragePerfManifest.BatchFrameCount).Select(_ => new PendingFrame(block.Actors)).ToArray();
            // Each frame records one row per actor. No frame starts beyond this batch until its outcomes are drained.
            _bufferedOutcomes = new CoverageGpuOutcome[checked(_pending.Length * block.Actors)];
            if (_timerLayout != CoveragePerfTimerLayout.CpuOnly && _device.Capabilities.SupportsTimestamps
                && _device.Capabilities.SupportsCompletionFences && _device.Diagnostics.SoftwareAdapter != true)
                _timestamps = new(_device, checked(block.WarmupFrames + block.MeasuredFrames), _timerLayout, PublishTimestamps);
            Typical.Scene.CoverageAllocationObserverForTests = entering => Child(entering, resolution: true);
            Typical.Scene.CoverageSurfaceAllocationObserverForTests = entering => Child(entering, resolution: false);
            _device.PresentEntry = PresentEntry;
        }
        catch
        {
            _timestamps?.Dispose(); _commands?.Dispose(); Typical?.Dispose(); _target?.Dispose(); _colour?.Dispose(); _host.Release();
            throw;
        }
    }

    public void Frame(int frameIndex, ref CoveragePerfFrameSample sample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_timerLayout != CoveragePerfTimerLayout.CpuOnly && _timestamps is null)
            throw new NotSupportedException("The device cannot establish GPU timestamp proof.");
        if (frameIndex != _nextFrame || _pendingCount == _pending.Length)
            throw new InvalidOperationException("Frames must be sequential and completed in fixed batches.");
        _frameIndex = frameIndex;
        _presented = false; _aggregateTicks = _resolutionTicks = _surfaceTicks = 0;
        int creations = _device.Creations, waits = _device.Waits, submits = _device.Submissions;
        var counters = _device.Counters;
        // This tick precedes BeginSubmission and Scene.Begin. PresentEntry owns the matching closing tick.
        _aggregateStart = _clock.Now();
        Observe("aggregate-start");
        FramePhases.Run(_frame, true, _device, _commands, Color.Black, _prepare, _record);
        if (!_presented) throw new InvalidOperationException("The production host never reached Present.");
        MatchOutcomes();
        var schedule = Typical.PassSchedule();
        bool stageComplete = Typical.Actors.All(a => a.Staged);
        bool fallback = !EffectiveRoutesValid || Typical.Covered && Typical.Actors.Any(a =>
            Typical.Scene.GetCoverageOutcome(a.Frame).Presentation != CoveragePresentation.Dispatched);
        var pending = _pending[_pendingCount++];
        pending.Begin(frameIndex, Typical);
        sample = new CoveragePerfFrameSample
        {
            FrameIndex = frameIndex,
            PoseId = frameIndex % 2,
            FrameTicks = _aggregateTicks,
            CpuFramePreparationTicks = _aggregateTicks,
            CpuChildren = [new("coverage.resolution", _resolutionTicks), new("coverage.surface", _surfaceTicks)],
            Actors = Typical.Actors.Length,
            MainVisibleActors = Typical.MainVisibleActors(),
            Passes = schedule.Passes,
            PointShadowFaces = schedule.Faces,
            UploadedBytes = Typical.Scene.LastFrameStats.BufferUpdateBytes,
            ResourceGrowth = _endCreations - creations,
            ProductionWaits = checked(Math.Max(_endWaits - waits, (int)(_endCounters.DrainCount - counters.DrainCount))
                + (int)(_endCounters.BackpressureStallCount - counters.BackpressureStallCount)
                + (int)(_endCounters.AcquireWaitCount - counters.AcquireWaitCount)),
            Submissions = _endSubmits - submits,
            StageComplete = stageComplete,
            Fallback = fallback,
            PoseFingerprint = Typical.PoseFingerprint(),
            PassFingerprint = schedule.Fingerprint,
        };
        _nextFrame++;
    }

    void Prepare(Frame frame)
    {
        BufferOutcomes();
        Typical.Scene.Begin(); Observe("scene-begin");
        Typical.Queue(_frameIndex); Observe("pose-and-stage");
        Typical.Scene.PrepareFrame(CoveragePerfTypicalScene.Width, CoveragePerfTypicalScene.Height); Observe("prepare");
    }

    void Record(Frame frame)
    {
        if (_timestamps is not null && !_timestamps.TryBegin(frame.Commands, frame.Submission, _frameIndex, _timerLayout))
            throw new InvalidOperationException(_timestamps.Failure);
        Typical.Scene.RenderInternal(frame.Commands, CoveragePerfTypicalScene.Width, CoveragePerfTypicalScene.Height, _target);
        Observe("render");
        // Registration is required even for zero rows, because it publishes the production history callbacks.
        Typical.Scene.RecordCoverageReadbacks(frame.Commands, frame.Submission);
        Observe("readbacks");
        if (_timestamps?.ActivePool is { } pool)
        {
            if (_timerLayout == CoveragePerfTimerLayout.Phases) frame.Commands.WriteTimestamp(pool, 3);
            ((IGpuCommandListTimestampEnd)frame.Commands).FinishTimestampsOnEnd(pool,
                _timerLayout == CoveragePerfTimerLayout.Total ? 1u : 4u);
        }
    }

    void PresentEntry()
    {
        _aggregateTicks = checked(_clock.Now() - _aggregateStart);
        _endCreations = _device.Creations; _endWaits = _device.Waits; _endSubmits = _device.Submissions;
        _endCounters = _device.Counters;
        if (_aggregateTicks < 0) throw new InvalidOperationException("Injected clock moved backwards.");
        _presented = true;
        Observe("aggregate-stop");
    }

    void Child(bool entering, bool resolution)
    {
        if (resolution && _timerLayout == CoveragePerfTimerLayout.Phases && _timestamps?.ActivePool is { } pool)
            _commands.WriteTimestamp(pool, entering ? 1u : 2u);
        long now = _clock.Now();
        if (resolution)
        {
            if (entering) _resolutionStart = now;
            else _resolutionTicks = checked(now - _resolutionStart);
        }
        else
        {
            if (entering) _surfaceStart = now;
            else _surfaceTicks = checked(now - _surfaceStart);
        }
        Observe(resolution ? entering ? "resolution-start" : "resolution-stop" : entering ? "surface-start" : "surface-stop");
    }

    void Observe(string name) => _host.Observe?.Invoke(name, _frame);

    void BufferOutcomes()
    {
        // Only production consumption and bounded value copies belong in the preparation interval.
        while (Typical.Scene.TryReadCoverageOutcome(out var outcome))
        {
            if (_bufferedOutcomeCount < _bufferedOutcomes.Length) _bufferedOutcomes[_bufferedOutcomeCount++] = outcome;
            else _outcomeOverflow = true;
        }
    }

    void MatchOutcomes()
    {
        if (_outcomeOverflow) throw new InvalidOperationException("Coverage outcomes exceeded the retained batch storage bound.");
        for (int row = 0; row < _bufferedOutcomeCount; row++)
        {
            var outcome = _bufferedOutcomes[row];
            Observe("outcome-match");
            bool found = false;
            for (int i = 0; i < _pendingCount; i++) found |= _pending[i].Accept(outcome);
            if (!found) throw new InvalidOperationException("A coverage outcome did not match a retained frame/view/actor.");
        }
        _bufferedOutcomeCount = 0;
    }

    public void CompleteBatch(Span<CoveragePerfFrameSample> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samples.Length != _pendingCount || samples.IsEmpty)
            throw new InvalidOperationException("Completion must receive the current pending batch.");
        for (int i = 0; i < samples.Length; i++)
            if (samples[i].FrameIndex != _pending[i].FrameIndex) throw new InvalidOperationException("Batch identities differ.");
        long start = _clock.Now();
        _device.WaitForIdle();
        HarnessDrainTicks += checked(_clock.Now() - start); HarnessDrains++;
        try
        {
            BufferOutcomes();
            MatchOutcomes();
            _timestamps?.PollCompleted();
        }
        finally
        {
            // Preserve partial completion evidence before propagating a harvest or resolution failure.
            for (int i = 0; i < samples.Length; i++)
            {
                var pending = _pending[i];
                // A fake never claims execution. Synthetic marks its semantic completion and stays Unproved.
                samples[i].DeviceComplete = _host.Synthetic || !Typical.Covered || pending.Completed == Typical.Actors.Length;
                samples[i].Fallback |= pending.Fallback;
                if (!samples[i].DeviceComplete) samples[i].Error = "Coverage device outcomes remain incomplete after the batch drain.";
                samples[i].Gpu = pending.Gpu;
                samples[i].Error ??= _timestamps?.Failure;
            }
        }
        _pendingCount = 0;
    }

    void PublishTimestamps(int frameIndex, CoveragePerfGpuResult result)
    {
        for (int i = 0; i < _pendingCount; i++)
            if (_pending[i].FrameIndex == frameIndex) { _pending[i].Gpu = result; return; }
        throw new InvalidOperationException("A GPU timestamp result did not match its retained frame.");
    }

    public CoveragePerfSessionInfo ReadInfo() => new()
    {
        Synthetic = _host.Synthetic,
        NativeDevice = !_host.Synthetic,
        Backend = _device.Backend,
        Adapter = _device.Capabilities.DeviceName ?? "unavailable",
        SoftwareAdapter = _device.Diagnostics.SoftwareAdapter ?? false,
        TimestampSupported = _device.Capabilities.SupportsTimestamps,
        TimestampPeriodNanoseconds = _timestamps?.FirstReadyPeriod,
        OrdinarySkinningRoute = CoveragePerfManifest.OrdinarySkinningRoute,
        CoveredSurfaceRoute = CoveragePerfManifest.CoveredSurfaceRoute,
        FixtureFingerprint = Typical.FixtureFingerprint,
        ProfileFingerprint = Typical.ProfileFingerprint,
        CompletionPolicy = "three-frame batches, one harness drain, one nonblocking outcome harvest",
        Environment = [new("fixture", Typical.Fixture), new("profile", Typical.Profile),
            new("settings", Typical.SettingsJson), new("topology", System.Text.Json.JsonSerializer.Serialize(Typical.ConstructionTopology)),
            new("reservations", System.Text.Json.JsonSerializer.Serialize(Typical.Actors.Select(a => a.Conformer.Gpu.Reservation))),
            new("gpuGuards", $"commands={CoverageGpuLayout.MaxCommands},regions={CoverageGpuLayout.MaxRegions},vertices={HostCoverageMap.MaxSurfaceVertices},triangles={HostCoverageMap.MaxTriangles}")],
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ExceptionDispatchInfo? error = null;
        try { _frame.DisposeSubmission(); }
        catch (Exception failure) { error ??= ExceptionDispatchInfo.Capture(failure); }
        try { _timestamps?.Dispose(); }
        catch (Exception failure) { error ??= ExceptionDispatchInfo.Capture(failure); }
        try { _commands.Dispose(); }
        catch (Exception failure) { error ??= ExceptionDispatchInfo.Capture(failure); }
        try { Typical.Dispose(); }
        catch (Exception failure) { error ??= ExceptionDispatchInfo.Capture(failure); }
        try { _target.Dispose(); }
        catch (Exception failure) { error ??= ExceptionDispatchInfo.Capture(failure); }
        try { _colour.Dispose(); }
        catch (Exception failure) { error ??= ExceptionDispatchInfo.Capture(failure); }
        try { _host.Release(); }
        catch (Exception failure) { error ??= ExceptionDispatchInfo.Capture(failure); }
        error?.Throw();
    }

    sealed class PendingFrame(int actors)
    {
        readonly Expected[] _actors = new Expected[actors];
        internal readonly CoverageGpuOutcome[] Outcomes = new CoverageGpuOutcome[actors];
        internal int FrameIndex, Completed;
        internal bool Fallback;
        internal CoveragePerfGpuResult? Gpu;
        internal void Begin(int index, CoveragePerfTypicalScene scene)
        {
            FrameIndex = index; Completed = 0; Fallback = false; Gpu = null;
            if (!scene.Covered) return;
            for (int i = 0; i < _actors.Length; i++)
            {
                var p = scene.Actors[i].Frame.Preparation!;
                _actors[i] = new(p.Generation, p.View, p.Actor, false);
            }
        }
        internal bool Accept(CoverageGpuOutcome outcome)
        {
            for (int i = 0; i < _actors.Length; i++)
            {
                ref var expected = ref _actors[i];
                if (expected.Frame != outcome.FrameId || expected.View != outcome.ViewId || expected.Actor != outcome.Actor) continue;
                if (expected.Complete) throw new InvalidOperationException("Duplicate coverage outcome.");
                expected = expected with { Complete = true }; Outcomes[i] = outcome; Completed++;
                Fallback |= outcome.Outcome.Presentation != CoveragePresentation.Covered || !outcome.Outcome.Failure.IsSuccess;
                return true;
            }
            return false;
        }
        readonly record struct Expected(ulong Frame, ulong View, MotionKey Actor, bool Complete);
    }
}
