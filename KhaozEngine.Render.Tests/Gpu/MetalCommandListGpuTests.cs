using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
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
    /// beside the suite's own and registers that queue into the same process-static completion table,
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

        /// <summary>
        /// THE ROW 6 AND ROW 7 JOIN, on hardware: a device-level upload is still PENDING when a frame is
        /// submitted, and the submit commits that batch before it commits the recording (M-M9).
        ///
        /// <para><b>THE ORDER ITSELF IS PINNED DEVICE-FREE</b> by <c>MetalSubmitSetupOrderTests</c>, over the
        /// static pre-lock phase. What only a device can settle is that the wiring is really on
        /// <c>IGpuDevice.Submit</c> and that both buffers complete, so this asserts the batch was committed by
        /// the submit and then reads the batch's own outcome after the drain.</para>
        ///
        /// <para><b>AND THE DRAIN IS THE OTHER HALF OF THE SAME MERGE.</b> A setup batch signals no timeline
        /// value, so <c>WaitForIdle</c> takes the queue drain as well whenever its flush committed one. Here the
        /// SUBMIT already flushed, so the batch is ahead of the frame in enqueue order and the timeline's own
        /// counted drain covers it, which is the case that needs no second drain at all.</para>
        /// </summary>
        [GpuFact]
        public void ASubmitFlushesThePendingSetupBatchBeforeItCommitsTheRecording()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();

            using IGpuTexture target = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));

            device.UpdateTexture(target, new byte[16 * 16 * 4], 0, 0, 16, 16);

            Assert.True(device.Setup.HasPendingWork);
            Assert.Equal(0, device.Setup.FlushCount);
            Assert.Null(device.Setup.LastCommittedFault());

            using MetalCommandList list = device.CreateCommandList();
            list.Begin();
            list.Encoders.EnsureBlitEncoder();
            list.End();
            device.Submit(list);

            // Committed by the SUBMIT, before it returned, which is what puts the upload ahead of the frame.
            Assert.Equal(1, device.Setup.FlushCount);
            Assert.False(device.Setup.HasPendingWork);

            device.WaitForIdle();

            MetalCommandBufferFault? outcome = device.Setup.LastCommittedFault();
            Assert.NotNull(outcome);
            Assert.Equal(MTLCommandBufferStatus.Completed, outcome.Value.Status);
            Assert.False(outcome.Value.IsFailure);

            Assert.Equal(1UL, device.Timeline.LastSubmitted);
            Assert.Null(device.Diagnostics.DeviceLossReason);
        }

        /// <summary>
        /// AND THE CASE THE MERGE ACTUALLY BROKE, from the other direction: an upload with NO submit behind it,
        /// drained through <c>WaitForIdle</c> alone.
        ///
        /// <para>Row 7 moved <c>WaitForIdle</c> onto the timeline's counted drain, and a setup batch signals no
        /// timeline value, so on a device that has never submitted anything the target is 0 and that drain
        /// returns without waiting. The batch would then read <c>Committed</c> rather than <c>Completed</c> and a
        /// <c>Map</c> would hand back bytes the blit had not written. The queue drain is what covers it.</para>
        ///
        /// <para><b>THE DETERMINISTIC DETECTOR IS ROW 6's OWN, AND THAT WAS MEASURED RATHER THAN ASSUMED.</b>
        /// Deleting the queue drain and re-running was tried at the merge: it failed
        /// <c>MetalResourceGpuTests.EightDeviceLevelTextureUploads_ShareOneSetupCommandBuffer</c>, which reads
        /// <c>Committed</c> where it asserts <c>Completed</c>, and it did NOT fail this row. A committed buffer
        /// completes whenever the GPU gets to it, so a four-megabyte blit can still finish inside the few
        /// instructions between the flush and the reading. This row is kept because it states the case in the
        /// shape the merge broke it (an upload with no submit behind it, so the timeline is provably not what
        /// covered it) and because it is where a reader looks, not because it is the tripwire.</para>
        /// </summary>
        [GpuFact]
        public void AnUploadWithNoSubmitBehindIt_IsStillCompleteAfterWaitForIdle()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();

            using IGpuTexture target = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(1024, 1024, GpuPixelFormat.R8G8B8A8UNorm,
                    GpuTextureUsage.Sampled));

            device.UpdateTexture(target, new byte[1024 * 1024 * 4], 0, 0, 1024, 1024);

            device.WaitForIdle();

            // NOTHING was ever submitted, so the timeline has no value to wait for and cannot be what covered it.
            Assert.Equal(0UL, device.Timeline.LastSubmitted);

            MetalCommandBufferFault? outcome = device.Setup.LastCommittedFault();
            Assert.NotNull(outcome);
            Assert.Equal(MTLCommandBufferStatus.Completed, outcome.Value.Status);
            Assert.False(outcome.Value.IsFailure);

            _output.WriteLine($"the setup batch finished at {outcome.Value.Status} with the timeline still at "
                + $"{device.Timeline.LastSubmitted}, which is what says the queue drain covered it");
        }

        // A [SupportedOSPlatformGuard] rather than an inline check at every row, which is the same mechanism the
        // package itself uses and what lets CA1416 see that every call below is on a macOS-only path.
        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (KhaozEngineMetal.IsPlatformSupported) return true;

            // KE_METAL_REQUIRED=1 turns this into a throw on the leg that declared a device mandatory.
            MetalDormancy.ThrowIfRequired("this is not macOS at all");
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
