using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Frame boundaries own the transition back to harvestable raw storage, even when optional observations fail.
internal sealed partial class RendererPerfRecorder
{
    internal void BeginFrame(int sampleId, int frameId, int sequenceIndex, GpuFrameSubmission? submission = null)
    {
        if (!Enabled) return;
        if (_frame != -1 || (uint)sampleId >= (uint)(_frames.Length - 1) || _frames[sampleId + 1].Begun)
            throw new InvalidOperationException("A frame is active, outside capacity, or already recorded.");
        _frame = sampleId; _submission = submission; _commands = null; _pool = null;
        _passDepth = _cpuDepth = _recordings = 0; _nextMarker = 1;
        var frame = Current;
        frame.Error = _frames[0].Error;
        frame.Begun = true; frame.FrameId = frameId; frame.SequenceIndex = sequenceIndex;
        frame.ThreadId = Environment.CurrentManagedThreadId;
        frame.ThreadStart = GC.GetAllocatedBytesForCurrentThread();
        frame.ProcessStart = GC.GetTotalAllocatedBytes(precise: false);
        for (int i = 0; i < 3; i++) frame.GcStart[i] = GC.CollectionCount(i);
        frame.NativeStart = _nativeStateCalls?.Invoke(); frame.DeviceStart = _device?.Counters ?? default;
        frame.Start = _segmentStart = _now();
    }

    internal void EndFrame()
    {
        if (!Enabled) return;
        if (_frame < 0) throw new InvalidOperationException("No frame is active.");
        var frame = Current;
        ImmutableArray<GpuDiagnosticFailure>.Builder? failures = null;
        try
        {
            frame.End = _now();
            CloseCpuSegment(frame.End);
            if (_passDepth != 0 || _cpuDepth != 0) Fail("A CPU phase or render pass was left open.");
            if (_layout == RendererPerfLayout.PerPass)
                foreach (byte state in frame.PassStates) if (state == 0) Fail("A reserved pass occurrence is missing.");
            frame.ThreadChanged = frame.ThreadId != Environment.CurrentManagedThreadId;
            if (frame.ThreadChanged) Fail("The measured frame changed its recording thread.");
            frame.ThreadBytes = GC.GetAllocatedBytesForCurrentThread() - frame.ThreadStart;
            frame.ProcessBytes = GC.GetTotalAllocatedBytes(precise: false) - frame.ProcessStart;
            for (int i = 0; i < 3; i++) frame.GcDelta[i] = GC.CollectionCount(i) - frame.GcStart[i];
        }
        catch (Exception error) { RendererPerfFailures.Add(ref failures, "renderer.frame.finalize", error); }
        try { frame.NativeEnd = _nativeStateCalls?.Invoke(); }
        catch (Exception error) { RendererPerfFailures.Add(ref failures, "renderer.frame.finalize", error); }
        try { frame.DeviceEnd = _device?.Counters ?? default; }
        catch (Exception error) { RendererPerfFailures.Add(ref failures, "renderer.frame.finalize", error); }
        if (failures is not null) Fail("Frame finalization observation failed. Actual exceptions retained by the caller.");
        frame.Ended = true; _passDepth = _cpuDepth = 0; _frame = -1; _submission = null; _commands = null; _pool = null;
        if (failures is not null) throw new GpuDiagnosticFailureException(failures.ToImmutable());
    }
}
