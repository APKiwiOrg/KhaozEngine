using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SUBMIT PATH ON REAL HARDWARE: a real <c>MTLCommandBuffer</c> taken per <c>Begin</c>, a real encoder
    /// opened and ended, a real <c>-commit</c> under the submit lock, and a real fence signalled because the GPU
    /// finished. Row 7 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Everything the list DECIDES is covered device-free by
    /// <see cref="MetalCommandListRecordingTests"/>, <see cref="MetalEncoderScopeTests"/> and
    /// <see cref="MetalEncoderScopeInvalidationTests"/>, so a failure here is about the native calls underneath
    /// and the ownership around them: <c>-commandBuffer</c> and the retain that makes its lifetime the list's,
    /// the three encoder factories and <c>-endEncoding</c>, <c>encodeSignalEvent:value:</c> and
    /// <c>addCompletedHandler:</c> landing on the buffer BEFORE the commit, and the commit itself. Rows 8 and 12
    /// to 14 record their content into exactly this, so a red run here is a red run everywhere later.</para>
    ///
    /// <para><b>DORMANT OFF macOS RATHER THAN SKIPPED</b>, which is phase 3's row-19 lesson: under
    /// <c>KE_GPU_TESTS=1</c> the Vulkan and Direct3D 11 legs run this assembly in strict mode where a skip is a
    /// failure, so each row returns early with the platform recorded instead.</para>
    ///
    /// <para><b>IT SITS IN <c>NativeDeviceLifecycle</c></b> because it builds a whole <c>MTLDevice</c> and queue
    /// beside the suite's own and registers that queue into the same four-slot process-static completion table,
    /// which is what the collection serialises.</para>
    ///
    /// <para><b>WHAT <c>Assert.Null(DeviceLossReason)</c> MEANS IN EVERY ROW BELOW, ONCE.</b> It is not the
    /// channel a state-machine defect arrives on. Metal enforces its own rules (a second encoder while one is
    /// open, an encoder on a committed buffer, a buffer committed twice) with DRIVER-SIDE FAILED ASSERTIONS that
    /// abort the process, so those defects are observed as a CRASHED RUN and no assertion in this file is reached
    /// at all. What this reads is the latch M-G4 fills from a command-buffer error reported asynchronously, and
    /// it reads it BEST EFFORT: <c>WaitForIdle</c> returns on the shared event reaching the submitted value, and
    /// Metal delivers completion handlers on its own thread in no order relative to that, so a failure latched
    /// just after the drain reads null here. That is a FALSE NEGATIVE only, never a false alarm, and closing it
    /// would mean the completion path counting or signalling something, which is exactly the ordering
    /// responsibility M-F2 keeps off it and which no test is worth putting back. The routing from a completion to
    /// the right device's latch is pinned exactly and deterministically by <c>MetalCompletionHandlerTests</c>
    /// over a fake sink, which is where that claim belongs.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalCommandListGpuTests
    {
        readonly ITestOutputHelper _output;

        public MetalCommandListGpuTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// THE WHOLE PATH, once: record a real encoder boundary pair, submit with a fence, drain, and read the
        /// fence back. The fence is what says the GPU actually ran the buffer, rather than that the calls
        /// returned.
        /// </summary>
        [GpuFact]
        public void ARecordedBufferCommitsAndItsFenceSignalsWhenTheGpuFinishes()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using MetalCommandList list = device.CreateCommandList();
            using MetalGpuFence fence = device.Timeline.CreateFence();

            Assert.False(fence.Signaled, "a fence straight from the timeline read signaled before it was armed");

            list.Begin();

            // A REAL ENCODER, because an empty command buffer would commit and complete without ever exercising
            // the three factories or -endEncoding. Blit is the cheapest of the three that needs no descriptor,
            // no pipeline and no resources, all of which are other rows'.
            IntPtr encoder = list.Encoders.EnsureBlitEncoder();
            Assert.NotEqual(IntPtr.Zero, encoder);
            Assert.Equal(MetalEncoderKind.Blit, list.Encoders.Open);

            list.End();

            // End closed it, which is what makes the buffer committable at all.
            Assert.Equal(MetalEncoderKind.None, list.Encoders.Open);
            Assert.True(list.IsSealed);
            Assert.Equal(1, device.Uncommitted.Outstanding);

            device.Submit(list, fence);

            Assert.False(list.IsSealed);
            Assert.Equal(0, device.Uncommitted.Outstanding);
            Assert.Equal(1UL, device.Timeline.LastSubmitted);

            device.WaitForIdle();

            Assert.True(fence.Signaled,
                "the fence armed at the submitted value never signaled after a drain, so either the signal was "
                + "not encoded into the committed buffer or the buffer did not run");
            Assert.Null(device.Diagnostics.DeviceLossReason);

            _output.WriteLine($"submitted value {device.Timeline.LastSubmitted}, completed "
                + $"{device.Timeline.CompletedValue}, peak uncommitted {device.Uncommitted.Peak} against a bound "
                + $"of {device.Uncommitted.Bound}");
        }

        /// <summary>
        /// A FRAME LOOP, which is the shape that leaks if the retain and release are not paired at every exit:
        /// <c>-commandBuffer</c> hands back an AUTORELEASED object, so a list re-Begun every frame would hold one
        /// per frame for the life of the process. The peak is what says it did not.
        /// </summary>
        [GpuFact]
        public void AFrameLoopHoldsOneUncommittedBufferAndStaysInsideTheBound()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using MetalCommandList list = device.CreateCommandList();

            for (int frame = 0; frame < 8; frame++)
            {
                list.Begin();
                list.Encoders.EnsureBlitEncoder();
                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Equal(8UL, device.Timeline.LastSubmitted);
            Assert.Equal(0, device.Uncommitted.Outstanding);
            Assert.Equal(1, device.Uncommitted.Peak);
            Assert.True(device.Uncommitted.Peak <= device.Uncommitted.Bound);
            Assert.False(device.Uncommitted.ExceededBound);
            Assert.Null(device.Diagnostics.DeviceLossReason);

            _output.WriteLine($"8 frames, peak uncommitted {device.Uncommitted.Peak}, completed "
                + $"{device.Timeline.CompletedValue}");
        }

        /// <summary>
        /// EVERY TRANSITION, on a real command buffer, in one recording. Metal refuses a second encoder while one
        /// is open, so a scope that failed to end the outgoing kind would fail HERE rather than in a device-free
        /// test, and it would fail as a nil encoder or a validation abort rather than as a wrong count.
        /// <para>
        /// POINTER IDENTITY IS NOT ASSERTED BETWEEN THE THREE, deliberately. Each encoder is released when it is
        /// ended, and Objective-C hands the freed allocation straight back, so the third encoder legitimately
        /// arrives at the second one's address. Asserting otherwise measures the allocator rather than the state
        /// machine, and it failed on this machine the first time it was written.
        /// </para>
        /// </summary>
        [GpuFact]
        public void EveryEncoderKindOpensAndClosesOnOneCommandBuffer()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using MetalCommandList list = device.CreateCommandList();

            list.Begin();

            Assert.NotEqual(IntPtr.Zero, list.Encoders.EnsureBlitEncoder());
            Assert.Equal(MetalEncoderKind.Blit, list.Encoders.Open);

            Assert.NotEqual(IntPtr.Zero, list.Encoders.EnsureComputeEncoder());
            Assert.Equal(MetalEncoderKind.Compute, list.Encoders.Open);

            // Back to blit: a third begin on a buffer that has already had two, which is the transition an
            // upload after a dispatch takes.
            Assert.NotEqual(IntPtr.Zero, list.Encoders.EnsureBlitEncoder());
            Assert.Equal(MetalEncoderKind.Blit, list.Encoders.Open);

            list.End();
            device.Submit(list);
            device.WaitForIdle();

            // WHAT THIS LINE ACTUALLY CLAIMS, because the mechanism is not the one it is easy to write down. A
            // missing endEncoding never reaches the latch at all: Metal enforces the one-encoder rule with a
            // DRIVER-SIDE FAILED ASSERTION that aborts the process, so that defect is observed as a crashed run
            // and this line is never reached. What DeviceLossReason catches is the other kind, a command-buffer
            // error reported asynchronously through the completion handler, and it catches it BEST EFFORT: the
            // drain above returns when the shared event reaches the submitted value, while Metal runs completion
            // handlers on its own thread in no order relative to that, so a failure latched a moment later reads
            // null here. The window can only HIDE a failure, never invent one, and closing it would mean the
            // completion path counting or signalling something, which is the ordering responsibility M-F2
            // deliberately keeps off it. The routing itself is pinned exactly, and deterministically, by
            // MetalCompletionHandlerTests over the fake sink. See the class summary.
            Assert.Null(device.Diagnostics.DeviceLossReason);
        }

        /// <summary>
        /// The submit path releases the buffer AFTER the commit rather than before it, and the queue retains a
        /// committed buffer until it completes, so a list disposed immediately after a submit cannot free one the
        /// GPU is running. Asserted by draining afterwards on a device that reports no loss: a premature release
        /// shows up as a crash or as a command-buffer failure rather than as a wrong number, and the class
        /// summary says which half of that this row can actually see.
        /// </summary>
        [GpuFact]
        public void DisposingAListRightAfterSubmittingDoesNotDisturbTheWorkInFlight()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            MetalCommandList list = device.CreateCommandList();

            list.Begin();
            list.Encoders.EnsureBlitEncoder();
            list.End();
            device.Submit(list);
            list.Dispose();

            device.WaitForIdle();

            Assert.Equal(1UL, device.Timeline.LastSubmitted);
            Assert.Null(device.Diagnostics.DeviceLossReason);
        }

        // A [SupportedOSPlatformGuard] rather than an inline check at every row, which is the same mechanism the
        // package itself uses and what lets CA1416 see that every call below is on a macOS-only path.
        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (KhaozEngineMetal.IsPlatformSupported) return true;

            _output.WriteLine("dormant: not macOS, so there is no Metal device to record against.");
            return false;
        }

        // Through the provider, like every other Metal [GpuFact], and then down to the concrete type because the
        // list, the timeline and the uncommitted counter are all internal: the seam hands lists out through
        // IGpuResourceFactory.CreateCommandList, which is row 6's
        // (https://github.com/APKiwiOrg/KhaozEngine/issues/572).
        static MetalGpuDevice CreateHeadless()
            => (MetalGpuDevice)new MetalBackendProvider().CreateHeadless().Device;
    }
}
