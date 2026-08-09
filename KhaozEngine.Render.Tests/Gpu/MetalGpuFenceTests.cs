using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The seam's <see cref="IGpuFence"/> on the native Metal backend, device-free (M-F1, M-F6). Row 5 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    /// <para>
    /// A fence here is a REMEMBERED VALUE on the device's one timeline and nothing else, so what these pin is
    /// the lifecycle the seam demands of it: unsignalled when it is submitted, signalled once the counter passes
    /// its target, re-armable through <c>Reset</c> with a strictly higher value, and signalled unconditionally
    /// once the device is gone.
    /// </para>
    /// </summary>
    public sealed class MetalGpuFenceTests
    {
        static (MetalTimeline Timeline, FakeMetalSharedEvent Event, FakeMetalDeviceLiveness Liveness) NewTimeline()
        {
            var sharedEvent = new FakeMetalSharedEvent();
            var liveness = new FakeMetalDeviceLiveness();
            return (new MetalTimeline(sharedEvent, liveness), sharedEvent, liveness);
        }

        static IntPtr Buffer(int n) => new(0x2000 + n);

        [Fact]
        public void AFreshFence_IsUnarmedAndUnsignaled()
        {
            (MetalTimeline timeline, _, _) = NewTimeline();
            MetalGpuFence fence = timeline.CreateFence();

            // The seam requires a fence to be unsignalled when it is submitted, and 0 is the unarmed marker
            // because the shared event starts at 0 and the first submission takes 1.
            Assert.Equal(0UL, fence.Target);
            Assert.False(fence.Signaled);
        }

        [Fact]
        public void AnArmedFence_ReadsSignaledOnlyOnceTheCounterReachesItsTarget()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();
            MetalGpuFence fence = timeline.CreateFence();

            ulong first = timeline.EncodeSignalForSubmit(Buffer(1));
            ulong second = timeline.EncodeSignalForSubmit(Buffer(2));
            fence.Arm(second);

            sharedEvent.Completed = first;
            Assert.False(fence.Signaled);

            sharedEvent.Completed = second;
            Assert.True(fence.Signaled);
        }

        [Fact]
        public void APollOnAFence_NeverWaits()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();
            MetalGpuFence fence = timeline.CreateFence();
            fence.Arm(timeline.EncodeSignalForSubmit(Buffer(1)));

            _ = fence.Signaled;
            _ = fence.Signaled;

            // "It polls and returns, it never waits" is met exactly rather than nearly: one signaledValue read
            // per poll and no wait at all. RetiredResourcePool polls constantly and must not serialise against
            // submission to do it.
            Assert.Equal(0, sharedEvent.WaitCount);
            Assert.Equal(2, sharedEvent.ReadCount);
        }

        [Fact]
        public void Reset_UnarmsTheFenceAndLetsALaterSubmissionRearmItHigher()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();
            MetalGpuFence fence = timeline.CreateFence();

            ulong first = timeline.EncodeSignalForSubmit(Buffer(1));
            fence.Arm(first);
            sharedEvent.Completed = first;
            Assert.True(fence.Signaled);

            fence.Reset();

            // Reset cannot unsignal anything and does not need to: the counter is device-wide and monotonic, so
            // a reset fence is re-armed by its next submission with a strictly HIGHER value, which is exactly
            // the fresh target the seam asks for.
            Assert.Equal(0UL, fence.Target);
            Assert.False(fence.Signaled);

            ulong second = timeline.EncodeSignalForSubmit(Buffer(2));
            fence.Arm(second);
            Assert.True(second > first);
            Assert.False(fence.Signaled);

            sharedEvent.Completed = second;
            Assert.True(fence.Signaled);
        }

        [Fact]
        public void ArmingAFenceThatIsStillArmed_Throws()
        {
            (MetalTimeline timeline, _, _) = NewTimeline();
            MetalGpuFence fence = timeline.CreateFence();
            fence.Arm(timeline.EncodeSignalForSubmit(Buffer(1)));

            // Overwriting the target silently would make the earlier submission's completion unobservable, and a
            // consumer polling for it would free resources the GPU is still reading.
            Assert.Throws<InvalidOperationException>(
                () => fence.Arm(timeline.EncodeSignalForSubmit(Buffer(2))));
        }

        [Fact]
        public void ArmingWithTheUnarmedMarker_Throws()
        {
            (MetalTimeline timeline, _, _) = NewTimeline();
            MetalGpuFence fence = timeline.CreateFence();

            // Reaching here means a fence was armed without a value having been allocated at all.
            Assert.Throws<ArgumentOutOfRangeException>(() => fence.Arm(0));
        }

        [Fact]
        public void ArmingADisposedFence_Throws()
        {
            (MetalTimeline timeline, _, _) = NewTimeline();
            MetalGpuFence fence = timeline.CreateFence();
            fence.Dispose();

            // Arming is the path where use-after-dispose is a defect. Polling and resetting are the paths where
            // it is a teardown-order accident, so those stay quiet.
            Assert.Throws<ObjectDisposedException>(() => fence.Arm(1));
        }

        [Fact]
        public void ADisposedFence_StillPollsAndResetsQuietly()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();
            MetalGpuFence fence = timeline.CreateFence();
            ulong value = timeline.EncodeSignalForSubmit(Buffer(1));
            fence.Arm(value);
            sharedEvent.Completed = value;
            fence.Dispose();

            // Nothing in the seam's contract says a fence has to be disposed at all, and a consumer that pools
            // them (GpuRetireBarrier does) disposes them last, after the device.
            Assert.True(fence.Signaled);
            fence.Reset();
            Assert.False(fence.Signaled);
        }

        [Fact]
        public void AfterDeviceDeath_EveryFenceReadsSignaled()
        {
            (MetalTimeline timeline, _, FakeMetalDeviceLiveness liveness) = NewTimeline();
            MetalGpuFence unarmed = timeline.CreateFence();
            MetalGpuFence armed = timeline.CreateFence();
            armed.Arm(timeline.EncodeSignalForSubmit(Buffer(1)));

            liveness.MarkDead();

            // M-F6, and death wins over the unarmed case too. A fence that read unsignalled after device death
            // would strand RetiredResourcePool forever on a batch it can never free, which is a teardown-order
            // hazard rather than a hypothetical.
            Assert.True(unarmed.Signaled);
            Assert.True(armed.Signaled);
        }

        [Fact]
        public void AFenceIsWhatTheSeamCallsAFence()
        {
            (MetalTimeline timeline, _, _) = NewTimeline();

            // There is no capability gate in front of CreateFence on this backend, because
            // SupportsCompletionFences is unconditionally true (M-F4).
            Assert.IsAssignableFrom<IGpuFence>(timeline.CreateFence());
        }

        [Fact]
        public void AFenceWithNoTimeline_IsRefused()
            => Assert.Throws<ArgumentNullException>(() => new MetalGpuFence(null!));
    }
}
