using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalRingAllocator
{
    internal Guid RingWaitDeviceIdentity { get; } = Guid.NewGuid();

    internal int BeginRecordingWithWaitReceipt(out GpuRecordingWaitReceipt receipt)
    {
        receipt = default;
        if (Monitor.IsEntered(_submitLock))
        {
            throw new InvalidOperationException(
                "BeginRecording was called on the native Metal ring allocator while the caller held the "
                + "submit lock. Claiming a segment waits for the GPU to finish with the submission that last "
                + "read it, and the value it waits for is registered under that same lock (M-N2), so waiting "
                + "while holding it would shut out the only thing that could end the wait. Begin takes it "
                + "nowhere, so reaching this means a new caller was added inside the lock.");
        }

        bool blocked = false;
        long waitedTicks = 0;
        int waitCalls = 0;

        bool taken = false;
        Monitor.Enter(_submitLock, ref taken);
        try
        {
            ulong ordinal = unchecked(++_recordingIndex);
            int segment = (int)(_recordingIndex % (ulong)FramesInFlight);

            while (true)
            {
                ulong target = _segmentOwner[segment];
                if (target == 0) break;
                if (_timeline.CompletedValue >= target) break;

                blocked = true;

                Monitor.Exit(_submitLock);
                taken = false;

                long start = Stopwatch.GetTimestamp();
                try
                {
                    // THE BACKPRESSURE WAIT, and MM4's only source. It blocks on the shared event rather than
                    // spinning, like the Vulkan sibling and unlike the Direct3D 11 one, and the difference is
                    // the primitive rather than a preference. There a completion poll on the fallback
                    // mechanism re-enters the submit lock, so a sleeping wait would shut out the submission
                    // that would end it. Here the wait holds no lock at all and
                    // waitUntilSignaledValue:timeoutMS: is a real blocking form, so burning a core to avoid a
                    // syscall would trade the one resource a stalled frame still needs.
                    waitCalls++;
                    _timeline.WaitForValue(target);
                }
                finally
                {
                    waitedTicks += Stopwatch.GetTimestamp() - start;
                    Monitor.Enter(_submitLock, ref taken);
                }
            }

            // THE PATCHES AND THE PUBLISH ARE ONE STEP, and were one hold even before the rest of the sequence
            // joined them. An off-timeline write that observed the new segment as current would copy into it
            // directly, so observing that before the replay would have its bytes overwritten by older queued
            // ones.
            ApplyPendingPatchesUnderLock(segment);
            _segment = segment;

            // ONLY THE BLOCK IS RECORDED, and once per acquisition however many revalidations it took: a
            // counter that ticks on a non-wait cannot answer "was anything ever blocked" with a zero, and a
            // counter that ticked per revalidation would report two stalls for one blocked caller.
            if (blocked)
            {
                Interlocked.Increment(ref _stallCount);
                _backpressure.Record(waitedTicks);
            }

            receipt = new(RingWaitDeviceIdentity, Guid.Empty, 0, ordinal, segment, FramesInFlight,
                blocked ? 1 : 0, waitedTicks, waitCalls, Stopwatch.Frequency);
            return segment;
        }
        finally
        {
            if (taken) Monitor.Exit(_submitLock);
        }
    }

    internal GpuRingWaitSnapshotStatus TryReadRingWaitTotals(out GpuRingWaitTotals totals)
    {
        totals = default;
        bool taken = false;
        try
        {
            Monitor.TryEnter(_submitLock, ref taken);
            if (!taken) return GpuRingWaitSnapshotStatus.Contended;
            // Both publishers hold this lock. The public counter getter keeps its prior contract.
            WaitTotals pair = _backpressure.Totals;
            totals = new(RingWaitDeviceIdentity, FramesInFlight, Stopwatch.Frequency, pair.Count, pair.Ticks);
            return GpuRingWaitSnapshotStatus.Available;
        }
        finally { if (taken) Monitor.Exit(_submitLock); }
    }
}
