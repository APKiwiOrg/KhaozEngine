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
    readonly bool _legacy;
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
    long _beginTicks, _submitStart, _submitTicks;
    long _endAndPreSubmitStart, _endAndPreSubmitTicks;
    CoveragePerfSubmissionCounts _submissionCounts;
    long _aggregateStart, _aggregateTicks, _presentEntry, _resolutionStart, _surfaceStart, _resolutionTicks, _surfaceTicks;
    int _frameIndex, _pendingCount, _nextFrame, _bufferedOutcomeCount;
    bool _presented, _disposed, _outcomeOverflow;
    int _endCreations, _endWaits, _endSubmits, _endCompletionFences, _endGen0, _endGen1, _endGen2;
    long _endAllocated;
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
        _legacy = block.Condition == CoveragePerfCondition.DirectOff;
        if (_legacy && block.TimerLayout != CoveragePerfTimerLayout.CpuOnly)
            throw new NotSupportedException("DirectOff is a CpuOnly pinned legacy control.");
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
            _ = CoveragePerfSubmissionProbe.Capture(_frame);
            _pending = Enumerable.Range(0, CoveragePerfManifest.BatchFrameCount).Select(_ => new PendingFrame(block.Actors)).ToArray();
            // Each frame records one row per actor. No frame starts beyond this batch until its outcomes are drained.
            _bufferedOutcomes = new CoverageGpuOutcome[checked(_pending.Length * block.Actors)];
            if (_timerLayout != CoveragePerfTimerLayout.CpuOnly && _device.Capabilities.SupportsTimestamps
                && _device.Capabilities.SupportsCompletionFences && _device.Diagnostics.SoftwareAdapter != true)
                _timestamps = new(_device, checked(block.WarmupFrames + block.MeasuredFrames), _timerLayout, PublishTimestamps);
            Typical.Scene.CoverageAllocationObserverForTests = entering => Child(entering, resolution: true);
            Typical.Scene.CoverageSurfaceAllocationObserverForTests = entering => Child(entering, resolution: false);
            _device.PresentEntry = PresentEntry;
            _device.SubmitBoundary = entering =>
            {
                if (entering)
                {
                    _submitStart = _clock.Now();
                    _endAndPreSubmitTicks = checked(_submitStart - _endAndPreSubmitStart);
                    Observe("end-and-pre-submit-stop");
                }
                else _submitTicks = checked(_clock.Now() - _submitStart);
            };
        }
        catch
        {
            _timestamps?.Dispose(); _commands?.Dispose(); Typical?.Dispose(); _target?.Dispose(); _colour?.Dispose(); _host.Release();
            throw;
        }
    }

    public void Frame(int frameIndex, ref CoveragePerfFrameSample sample)
    {
        // Wall spans use the injected clock and locals only. None is added to the aggregate.
        long entry = _clock.Now();
        long? aggregateStart = null, presentEntry = null, hostReturn = null, frameEnd = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timerLayout != CoveragePerfTimerLayout.CpuOnly && _timestamps is null)
                throw new NotSupportedException("The device cannot establish GPU timestamp proof.");
            if (frameIndex != _nextFrame || _pendingCount == _pending.Length)
                throw new InvalidOperationException("Frames must be sequential and completed in fixed batches.");
            _frameIndex = frameIndex;
            _presented = false; _aggregateTicks = _resolutionTicks = _surfaceTicks = _beginTicks = _submitTicks = _endAndPreSubmitTicks = 0;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            int creations = _device.Creations, waits = _device.Waits, submits = _device.Submissions;
            int completionFences = _device.SubmittedCompletionFences;
            var counters = _device.Counters;
            // This tick precedes BeginSubmission and Scene.Begin. PresentEntry owns the matching closing tick.
            _aggregateStart = _clock.Now();
            aggregateStart = _aggregateStart;
            Observe("aggregate-start");
            if (_legacy) CoveragePerfLegacyHost.Run(_frame, true, _device, _commands, Color.Black, _prepare, _record);
            else FramePhases.Run(_frame, true, _device, _commands, Color.Black, _prepare, _record);
            hostReturn = _clock.Now();
            if (!_presented) throw new InvalidOperationException("The production host never reached Present.");
            // _presented is reset above, so this is this frame's Present entry tick.
            presentEntry = _presentEntry;
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
                CpuChildren = [new("host.beginSubmission", _beginTicks), new("host.endAndPreSubmit", _endAndPreSubmitTicks), new("device.submit", _submitTicks),
                    new("coverage.resolution", _resolutionTicks), new("coverage.surface", _surfaceTicks)],
                AllocatedBytes = _endAllocated - allocated,
                Gen0Collections = _endGen0 - gen0,
                Gen1Collections = _endGen1 - gen1,
                Gen2Collections = _endGen2 - gen2,
                Actors = Typical.Actors.Length,
                MainVisibleActors = MainVisibleActors(),
                Passes = schedule.Passes,
                PointShadowFaces = schedule.Faces,
                UploadedBytes = Typical.Scene.LastFrameStats.BufferUpdateBytes,
                ResourceGrowth = _endCreations - creations,
                ProductionWaits = checked(Math.Max(_endWaits - waits, (int)(_endCounters.DrainCount - counters.DrainCount))
                    + (int)(_endCounters.BackpressureStallCount - counters.BackpressureStallCount)
                    + (int)(_endCounters.AcquireWaitCount - counters.AcquireWaitCount)),
                Submissions = _endSubmits - submits,
                SubmittedCompletionFences = _endCompletionFences - completionFences,
                SubmissionParticipants = _submissionCounts.Participants,
                CompletionConsumers = _submissionCounts.Consumers,
                StageComplete = stageComplete,
                Fallback = fallback,
                PoseFingerprint = Typical.PoseFingerprint(),
                PassFingerprint = schedule.Fingerprint,
            };
            frameEnd = _clock.Now();
            _nextFrame++;
        }
        finally
        {
            // A span is recorded when both of its bounding ticks were taken in this frame, on a throw as well.
            // One whose closing tick was never taken stays null.
            if (aggregateStart is long opened) sample.FrameEntryToAggregateTicks = checked(opened - entry);
            if (presentEntry is long present && hostReturn is long returned) sample.PresentToHostReturnTicks = checked(returned - present);
            if (hostReturn is long host && frameEnd is long end) sample.HostReturnToFrameEndTicks = checked(end - host);
        }
    }

    int MainVisibleActors()
    {
        // A fake host observes this call to place the final Frame clock read after the sample initializer.
        Observe("sample-initializer");
        return Typical.MainVisibleActors();
    }

    void Prepare(Frame frame)
    {
        _beginTicks = checked(_clock.Now() - _aggregateStart);
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
        if (!_legacy)
        {
            Typical.Scene.RecordCoverageReadbacks(frame.Commands, frame.Submission);
            Observe("readbacks");
        }
        if (_timestamps?.ActivePool is { } pool)
        {
            if (_timerLayout == CoveragePerfTimerLayout.Phases) frame.Commands.WriteTimestamp(pool, 3);
            ((IGpuCommandListTimestampEnd)frame.Commands).FinishTimestampsOnEnd(pool,
                _timerLayout == CoveragePerfTimerLayout.Total ? 1u : 4u);
        }
        _submissionCounts = CoveragePerfSubmissionProbe.Capture(frame);
        // The callback return through the device Submit entry includes recording-scope End and release,
        // production host/submission checks and completion-list preparation. It is not isolated End time.
        _endAndPreSubmitStart = _clock.Now();
        Observe("end-and-pre-submit-start");
    }

    void PresentEntry()
    {
        _presentEntry = _clock.Now();
        _aggregateTicks = checked(_presentEntry - _aggregateStart);
        _endCreations = _device.Creations; _endWaits = _device.Waits; _endSubmits = _device.Submissions;
        _endCompletionFences = _device.SubmittedCompletionFences;
        _endCounters = _device.Counters;
        _endAllocated = GC.GetAllocatedBytesForCurrentThread();
        _endGen0 = GC.CollectionCount(0); _endGen1 = GC.CollectionCount(1); _endGen2 = GC.CollectionCount(2);
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
        long drainTicks = checked(_clock.Now() - start);
        HarnessDrainTicks += drainTicks; HarnessDrains++;
        // Charge each drain once to its batch's last raw sample, outside frame preparation.
        samples[^1].HarnessDrains = 1;
        samples[^1].HarnessDrainTicks = drainTicks;
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
                samples[i].CompletedFrameTail = pending.Gpu is { Valid: true } && _timestamps is not null;
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
        Vendor = CoveragePerfHardwareIdentity.FormatId(_device.Capabilities.PciVendorId),
        Device = CoveragePerfHardwareIdentity.FormatId(_device.Capabilities.PciDeviceId),
        Driver = _device.Capabilities.DriverVersion ?? "unavailable",
        HardwareIdentityBasis = CoveragePerfHardwareIdentity.Basis(_device.Capabilities),
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
