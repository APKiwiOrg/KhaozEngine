using System;
using System.Threading.Tasks;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The native Metal backend's completion timeline (M-F1 to M-F5), driven device-free over
    /// <see cref="FakeMetalSharedEvent"/>. Row 5 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    /// <para>
    /// WHAT THESE COVER is everything that can be wrong about ORDERING and ACCOUNTING: the monotonic allocation,
    /// the encode that goes with it, the two high-waters and why they differ, the dead-device answers, the
    /// drain's three no-count cases, and the slice loop. All of it runs under a plain <c>dotnet test</c> on
    /// Linux and Windows, which is the entire reason <see cref="IMetalSharedEvent"/> is an interface.
    /// </para>
    /// <para>
    /// WHAT THEY CANNOT COVER is a value being signalled because real GPU work finished.
    /// <c>MetalTimelineGpuTests</c> is that half, and it needs a device.
    /// </para>
    /// </summary>
    public sealed class MetalTimelineTests
    {
        static (MetalTimeline Timeline, FakeMetalSharedEvent Event, FakeMetalDeviceLiveness Liveness) NewTimeline()
        {
            var sharedEvent = new FakeMetalSharedEvent();
            var liveness = new FakeMetalDeviceLiveness();
            return (new MetalTimeline(sharedEvent, liveness), sharedEvent, liveness);
        }

        // A stand-in for an MTLCommandBuffer handle. The timeline never dereferences it, it only hands it to the
        // event, which is the whole reason the encode is testable without Metal.
        static IntPtr Buffer(int n) => new(0x1000 + n);

        [Fact]
        public void AFreshTimeline_HasIssuedNothingAndSubmittedNothing()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();

            Assert.Equal(0UL, timeline.LastAllocated);
            Assert.Equal(0UL, timeline.LastSubmitted);
            Assert.Equal(0, timeline.TotalDrain.Count);
            Assert.Empty(sharedEvent.Encoded);
        }

        [Fact]
        public void SupportsCompletionFences_IsUnconditionallyTrue()
        {
            // M-F4: PARITY rather than an upgrade. VeldridMap (deleted in 18.0.0) already answered true for
            // GraphicsBackend.Metal, so the gate criterion for this backend was NO NEW SKIPS rather than two
            // fewer, and this constant is where row 16 reads the answer from.
            Assert.True(MetalTimeline.SupportsCompletionFences);
        }

        [Fact]
        public void ValuesAreHandedOut_StrictlyIncreasingFromOne_AndEachIsEncodedOnItsOwnBuffer()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();

            Assert.Equal(1UL, timeline.EncodeSignalForSubmit(Buffer(1)));
            Assert.Equal(2UL, timeline.EncodeSignalForSubmit(Buffer(2)));
            Assert.Equal(3UL, timeline.EncodeSignalForSubmit(Buffer(3)));

            // Allocation and encode are ONE step on this backend, which is the shape difference from the Vulkan
            // sibling: there is no window in which a value exists without a buffer carrying its signal.
            Assert.Equal(
                new[] { (Buffer(1), 1UL), (Buffer(2), 2UL), (Buffer(3), 3UL) },
                sharedEvent.Encoded);
            Assert.Equal(3UL, timeline.LastAllocated);
        }

        [Fact]
        public void ValueAllocation_HandsNoValueOutTwiceUnderConcurrency()
        {
            (MetalTimeline timeline, _, _) = NewTimeline();

            const int threads = 8;
            const int per = 250;
            var taken = new ulong[threads * per];

            Parallel.For(0, threads, t =>
            {
                for (int i = 0; i < per; i++)
                    taken[(t * per) + i] = timeline.EncodeSignalForSubmit(Buffer(t));
            });

            // Two submissions sharing a value would make a fence on the first read signalled when only the
            // second had finished, which is why the allocation is interlocked rather than _issued + 1.
            Array.Sort(taken);
            for (int i = 0; i < taken.Length; i++) Assert.Equal((ulong)(i + 1), taken[i]);
            Assert.Equal((ulong)taken.Length, timeline.LastAllocated);
        }

        [Fact]
        public void CompletedValue_ReadsTheEventEveryTime()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();
            sharedEvent.Completed = 7;

            Assert.Equal(7UL, timeline.CompletedValue);
            sharedEvent.Completed = 9;
            Assert.Equal(9UL, timeline.CompletedValue);
            Assert.Equal(2, sharedEvent.ReadCount);
        }

        [Fact]
        public void AfterDeviceDeath_CompletedValueIsWhatWasIssuedAndTheEventIsNotRead()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, FakeMetalDeviceLiveness liveness) =
                NewTimeline();
            timeline.EncodeSignalForSubmit(Buffer(1));
            timeline.EncodeSignalForSubmit(Buffer(2));
            liveness.MarkDead();

            Assert.Equal(2UL, timeline.CompletedValue);
            Assert.Equal(0, sharedEvent.ReadCount);
        }

        [Fact]
        public void ALossDiscoveredInsideTheRead_AnswersFromWhatWasIssued()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, FakeMetalDeviceLiveness liveness) =
                NewTimeline();
            timeline.EncodeSignalForSubmit(Buffer(1));
            timeline.EncodeSignalForSubmit(Buffer(2));
            timeline.EncodeSignalForSubmit(Buffer(3));

            // The error latch flips liveness from Metal's own completion thread, so the device can die WHILE the
            // read is in flight and the number the driver handed back describes a device that is already gone.
            // That is what the second liveness check exists for.
            sharedEvent.Completed = 1;
            sharedEvent.OnRead = liveness.MarkDead;

            Assert.Equal(3UL, timeline.CompletedValue);
        }

        [Fact]
        public void WaitForIdle_WithNothingSubmitted_DoesNotWaitAndCountsNothing()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();
            timeline.EncodeSignalForSubmit(Buffer(1));   // allocated, never registered

            timeline.WaitForIdle();

            Assert.Equal(0, sharedEvent.WaitCount);
            Assert.Equal(0, timeline.TotalDrain.Count);
        }

        [Fact]
        public void WaitForIdle_WithTheGpuCaughtUp_DoesNotWaitAndCountsNothing()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();
            ulong value = timeline.EncodeSignalForSubmit(Buffer(1));
            timeline.RegisterSubmitted(value);
            sharedEvent.Completed = value;

            timeline.WaitForIdle();

            // The seam's DrainCount doc says a wait that found the GPU already caught up is not counted, and on
            // this backend honouring it costs one non-blocking read, because the target is the last SUBMITTED
            // value rather than a fresh point signalled per drain.
            Assert.Equal(0, sharedEvent.WaitCount);
            Assert.Equal(0, timeline.TotalDrain.Count);
        }

        [Fact]
        public void WaitForIdle_WithWorkOutstanding_WaitsForTheLastSubmittedValueAndCountsIt()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();
            timeline.RegisterSubmitted(timeline.EncodeSignalForSubmit(Buffer(1)));
            ulong last = timeline.EncodeSignalForSubmit(Buffer(2));
            timeline.RegisterSubmitted(last);

            timeline.WaitForIdle();

            Assert.Equal(1, sharedEvent.WaitCount);
            Assert.Equal(last, sharedEvent.LastWaitValue);
            Assert.Equal(MetalTimeline.DrainSliceMs, sharedEvent.LastWaitTimeoutMs);
            Assert.Equal(1, timeline.TotalDrain.Count);
            Assert.True(timeline.TotalDrain.Ticks >= 0);
        }

        [Fact]
        public void WaitForIdle_ReIssuesTheWaitUntilTheValueArrives()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();
            timeline.RegisterSubmitted(timeline.EncodeSignalForSubmit(Buffer(1)));

            // Two expired slices and then an arrival. A slice expiring is NOT forward progress: the drain blocks
            // for as long as the GPU takes, exactly as the Vulkan sibling's infinite wait does. What the slice
            // buys is the liveness re-check below.
            int slice = 0;
            sharedEvent.WaitReachesTheValue = false;
            sharedEvent.OnWait = () =>
            {
                if (++slice == 3) sharedEvent.WaitReachesTheValue = true;
            };

            timeline.WaitForIdle();

            Assert.Equal(3, sharedEvent.WaitCount);
            Assert.Equal(1, timeline.TotalDrain.Count);
        }

        [Fact]
        public void WaitForIdle_ThatOnlyEndedBecauseTheDeviceDied_ReturnsAndIsStillCounted()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, FakeMetalDeviceLiveness liveness) =
                NewTimeline();
            timeline.RegisterSubmitted(timeline.EncodeSignalForSubmit(Buffer(1)));

            // The Metal shape of a device loss: the signal never arrives, and the only notification is the error
            // latch flipping liveness from the completion thread. A single unbounded wait would never see it.
            sharedEvent.WaitReachesTheValue = false;
            sharedEvent.OnWait = liveness.MarkDead;

            timeline.WaitForIdle();

            Assert.Equal(1, sharedEvent.WaitCount);

            // It blocked, for the time recorded, so it counts. Dropping it would under-report exactly the drains
            // a post-mortem cares about.
            Assert.Equal(1, timeline.TotalDrain.Count);
        }

        [Fact]
        public void DrainTotals_AccumulateAcrossDrainsAndAreNeverRolled()
        {
            (MetalTimeline timeline, _, _) = NewTimeline();

            for (int i = 1; i <= 3; i++)
            {
                timeline.RegisterSubmitted(timeline.EncodeSignalForSubmit(Buffer(i)));
                timeline.WaitForIdle();
            }

            // Cumulative rather than per frame, so two sampled telemetry rows bracket a window exactly.
            Assert.Equal(3, timeline.TotalDrain.Count);
            Assert.True(timeline.TotalDrain.TotalMs >= 0);
        }

        [Fact]
        public void WaitForIdle_OnADeadDevice_DoesNothing()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, FakeMetalDeviceLiveness liveness) =
                NewTimeline();
            timeline.RegisterSubmitted(timeline.EncodeSignalForSubmit(Buffer(1)));
            liveness.MarkDead();

            timeline.WaitForIdle();

            Assert.Equal(0, sharedEvent.WaitCount);
            Assert.Equal(0, timeline.TotalDrain.Count);
        }

        [Fact]
        public void AnAllocationWithoutARegistration_LeavesTheDrainTargetWhereItWas()
        {
            (MetalTimeline timeline, _, _) = NewTimeline();
            timeline.RegisterSubmitted(timeline.EncodeSignalForSubmit(Buffer(1)));

            // A submission can throw between the encode and the commit returning. If LastSubmitted reported the
            // ALLOCATION, a value nothing will ever signal would sit permanently above anything the GPU can
            // reach and the next drain would block until its liveness check released it.
            timeline.EncodeSignalForSubmit(Buffer(2));

            Assert.Equal(2UL, timeline.LastAllocated);
            Assert.Equal(1UL, timeline.LastSubmitted);
        }

        [Fact]
        public void AValueNothingSignalled_IsSteppedOverByTheNextSuccessfulOne()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();
            timeline.EncodeSignalForSubmit(Buffer(1));   // the commit that failed
            ulong second = timeline.EncodeSignalForSubmit(Buffer(2));
            timeline.RegisterSubmitted(second);

            // A hole in the value SPACE is not a hole in the ORDER: the counter steps over it, and the theorem
            // says only that the counter reaching V covers every submission signalling at or below V.
            sharedEvent.Completed = second;
            Assert.Equal(second, timeline.LastSubmitted);
            Assert.Equal(2UL, timeline.CompletedValue);
        }

        [Fact]
        public void RegisterSubmitted_NeverLowersTheDrainTarget()
        {
            (MetalTimeline timeline, _, _) = NewTimeline();
            timeline.RegisterSubmitted(5);
            timeline.RegisterSubmitted(2);

            // A target that went backwards would release a fence over work that has not finished.
            Assert.Equal(5UL, timeline.LastSubmitted);
        }

        [Fact]
        public void AfterDeviceDeath_TheAnswerCoversTheAllocationHighWaterAndNotJustTheRegisteredOne()
        {
            (MetalTimeline timeline, _, FakeMetalDeviceLiveness liveness) = NewTimeline();
            timeline.RegisterSubmitted(timeline.EncodeSignalForSubmit(Buffer(1)));
            ulong inFlight = timeline.EncodeSignalForSubmit(Buffer(2));
            liveness.MarkDead();

            // Answering with the smaller of the two would leave exactly the fences armed in that window
            // unreleased at the moment nothing can ever advance the counter again.
            Assert.Equal(inFlight, timeline.CompletedValue);
        }

        [Fact]
        public void Dispose_ReleasesTheEventOnce()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();

            timeline.Dispose();
            timeline.Dispose();

            Assert.True(sharedEvent.Disposed);
            Assert.Equal(1, sharedEvent.DisposeCount);
        }

        [Fact]
        public void Dispose_OnADeadDevice_STILL_ReleasesTheEvent()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, FakeMetalDeviceLiveness liveness) =
                NewTimeline();
            liveness.MarkDead();

            timeline.Dispose();

            // THE ONE PLACE THIS DIVERGES FROM THE VULKAN SIBLING, which skips its native destroy after device
            // death because vkDestroyDevice already destroyed every child object and calling the loader
            // afterwards aborts the process. An MTLSharedEvent is an ordinary reference-counted Objective-C
            // object with no such rule, so skipping the release would leak it on exactly the teardown path that
            // matters. It is also the fact M-H3 rests on when it declines a retire list.
            Assert.True(sharedEvent.Disposed);
            Assert.Equal(1, sharedEvent.DisposeCount);
        }

        [Fact]
        public void CompletedValue_AfterDispose_NeverTouchesTheReleasedEvent()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();
            sharedEvent.Completed = 3;
            ulong allocated = timeline.EncodeSignalForSubmit(Buffer(1));
            timeline.RegisterSubmitted(allocated);

            timeline.Dispose();
            int readsBefore = sharedEvent.ReadCount;

            // DEFENCE IN DEPTH, NOT THE ONLY DEFENCE. M-F6's teardown flips liveness before disposal, so a
            // correctly ordered teardown never reaches this guard. What it stops is the order NOT being
            // honoured: Dispose releases the MTLSharedEvent unconditionally, so a poll after it without the flip
            // would send signaledValue to a released Objective-C object, which is a use-after-free rather than a
            // wrong number. The answer is the dead answer, because a timeline that is gone has nothing left to
            // finish.
            Assert.Equal(timeline.LastAllocated, timeline.CompletedValue);
            Assert.Equal(readsBefore, sharedEvent.ReadCount);
        }

        [Fact]
        public void WaitForIdle_AfterDispose_NeverTouchesTheReleasedEvent()
        {
            (MetalTimeline timeline, FakeMetalSharedEvent sharedEvent, _) = NewTimeline();
            ulong allocated = timeline.EncodeSignalForSubmit(Buffer(1));
            timeline.RegisterSubmitted(allocated);
            sharedEvent.WaitReachesTheValue = false;

            timeline.Dispose();
            int waitsBefore = sharedEvent.WaitCount;

            // The disposal guard covers the drain for free rather than by a second check: the target is
            // LastSubmitted, which can never exceed LastAllocated, so the caught-up early return fires before
            // the slice loop can wait on a released event. Without it this would spin forever on a fake whose
            // wait never reaches the value.
            timeline.WaitForIdle();

            Assert.Equal(waitsBefore, sharedEvent.WaitCount);
            Assert.Equal(0, timeline.TotalDrain.Count);
        }

        [Fact]
        public void ATimelineWithNoLivenessToken_TreatsTheDeviceAsAlive()
        {
            var sharedEvent = new FakeMetalSharedEvent { Completed = 4 };
            using var timeline = new MetalTimeline(sharedEvent);

            // Defaulting to dead would make every fence read signalled and every drain a no-op, which is silent
            // before death: a pool would free resources the GPU is still reading.
            Assert.False(timeline.IsDeviceDead);
            Assert.Equal(4UL, timeline.CompletedValue);
        }

        [Fact]
        public void ATimelineWithNoEvent_IsRefused()
            => Assert.Throws<ArgumentNullException>(() => new MetalTimeline(null!));
    }
}
