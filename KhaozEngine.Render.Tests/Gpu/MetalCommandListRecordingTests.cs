using System;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE RECORDING CONTRACT of the native Metal command list, device-free. Row 7 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, section 6.1.
    /// <para>
    /// EVERYTHING HERE IS A DECISION RATHER THAN A DRIVER CALL: which Begin is refused, which is legal, what a
    /// discarded recording releases, what the seal means, and the ownership rule that exactly one release follows
    /// each acquisition. All of it runs on the Linux and Windows legs, over a fake command-buffer source handing
    /// out opaque numbers.
    /// </para>
    /// </summary>
    public sealed class MetalCommandListRecordingTests : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose() => _harness.Dispose();

        // Every list here is built through the ring harness, because Begin is this backend's frame boundary and
        // therefore needs a real ring allocator behind it (M-R2). The harness is disposed with the fixture rather
        // than per test, since a fake shared event and a pinned array cost nothing to hold.
        readonly MetalRingHarness _harness = new();

        (MetalCommandList List, FakeMetalCommandBufferSource Buffers, FakeMetalEncoderCalls Calls,
            MetalUncommittedBuffers Uncommitted) NewList(int framesInFlight = MetalFramesInFlight.Default)
        {
            FakeMetalCommandBufferSource buffers = new();
            FakeMetalEncoderCalls calls = new();
            MetalUncommittedBuffers uncommitted = new(framesInFlight, new RecordingLogger());

            // The owner is an opaque token here, which is all the submit path compares it as. Whose it is, and
            // what a list from ANOTHER device's token gets, is MetalSubmitTargetIdentityTests.
            MetalCommandList list = _harness.NewList(new object(), buffers, calls, uncommitted);
            return (list, buffers, calls, uncommitted);
        }

        [Fact]
        public void AFreshListIsNeitherRecordingNorSealed()
        {
            (MetalCommandList list, FakeMetalCommandBufferSource buffers, _, _) = NewList();

            Assert.False(list.IsRecording);
            Assert.False(list.IsSealed);
            Assert.Empty(buffers.Acquired);
        }

        /// <summary>M-R2: a FRESH command buffer per Begin, from the queue, with no pool and no reset anywhere in
        /// the path.</summary>
        [Fact]
        public void EveryBeginTakesAFreshCommandBuffer()
        {
            (MetalCommandList list, FakeMetalCommandBufferSource buffers, _, _) = NewList();

            list.Begin();
            list.End();
            list.Begin();
            list.End();

            Assert.Equal(2, buffers.Acquired.Count);
            Assert.NotEqual(buffers.Acquired[0], buffers.Acquired[1]);
        }

        [Fact]
        public void ASecondBeginWhileRecordingIsRefusedByName()
        {
            (MetalCommandList list, _, _, _) = NewList();
            list.Begin();

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(list.Begin);
            Assert.Contains("already recording", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A BEGIN AFTER AN END THAT NOBODY SUBMITTED IS LEGAL, because the seam says a list is reusable frame
        /// after frame and that a Begin discards what came before. What it must not do is leak: the abandoned
        /// buffer goes back BEFORE the new one is taken, or a list re-Begun in a loop accumulates one uncommitted
        /// buffer per iteration against the queue's own maximum.
        /// </summary>
        [Fact]
        public void ASealedRecordingNobodySubmittedIsDiscardedByTheNextBegin()
        {
            (MetalCommandList list, FakeMetalCommandBufferSource buffers, _,
                MetalUncommittedBuffers uncommitted) = NewList();

            list.Begin();
            list.End();
            IntPtr abandoned = list.SealedCommandBuffer;

            list.Begin();

            Assert.Contains(abandoned, buffers.Released);
            Assert.False(list.IsSealed);
            Assert.Equal(1, uncommitted.Outstanding);
        }

        [Fact]
        public void EndWithoutBeginIsRefusedAndSaysSo()
        {
            (MetalCommandList list, _, _, _) = NewList();

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(list.End);
            Assert.Contains("not recording", thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ASecondEndIsRefusedWithItsOwnMessage()
        {
            (MetalCommandList list, _, _, _) = NewList();
            list.Begin();
            list.End();

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(list.End);
            Assert.Contains("twice", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// End closes any open encoder UNCONDITIONALLY. Committing a command buffer with an encoder still open is
        /// a call Metal refuses, and this is the only native obligation the seal carries: there is no
        /// <c>vkEndCommandBuffer</c> equivalent to make.
        /// </summary>
        [Fact]
        public void EndClosesAnOpenEncoder()
        {
            (MetalCommandList list, _, FakeMetalEncoderCalls calls, _) = NewList();

            list.Begin();
            list.Encoders.EnsureBlitEncoder();
            list.End();

            Assert.Equal(MetalEncoderKind.None, list.Encoders.Open);
            Assert.Equal(2, calls.EncoderBoundaries);
        }

        [Fact]
        public void EndOnARecordingThatOpenedNoEncoderEmitsNothing()
        {
            (MetalCommandList list, _, FakeMetalEncoderCalls calls, _) = NewList();

            list.Begin();
            list.End();

            Assert.Equal(0, calls.EncoderBoundaries);
        }

        [Fact]
        public void SubmittingWithoutASealIsRefusedByName()
        {
            (MetalCommandList list, _, _, _) = NewList();
            list.Begin();

            InvalidOperationException thrown =
                Assert.Throws<InvalidOperationException>(() => _ = list.SealedCommandBuffer);
            Assert.Contains("without a sealed recording", thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheSealNamesTheBufferTheRecordingUsed()
        {
            (MetalCommandList list, FakeMetalCommandBufferSource buffers, _, _) = NewList();

            list.Begin();
            list.End();

            Assert.Equal(buffers.Acquired[0], list.SealedCommandBuffer);
        }

        /// <summary>
        /// The submit path hands ownership back through <c>MarkSubmitted</c> AFTER the commit, so the retain
        /// Begin took is released there rather than here, and the seal clears with it: a second submit of the
        /// same list without a new recording would otherwise commit one buffer twice, which Metal refuses and
        /// which would take a second timeline value with it.
        /// </summary>
        [Fact]
        public void MarkSubmittedClearsTheSealAndDropsTheUncommittedCount()
        {
            (MetalCommandList list, FakeMetalCommandBufferSource buffers, _,
                MetalUncommittedBuffers uncommitted) = NewList();

            list.Begin();
            list.End();
            list.MarkSubmitted(1);

            Assert.False(list.IsSealed);
            Assert.Equal(0, uncommitted.Outstanding);

            // NOT released through the source: a committed buffer is the queue's until it completes, and the
            // release paired with the commit is the submit path's.
            Assert.Empty(buffers.Released);
            Assert.Throws<InvalidOperationException>(() => _ = list.SealedCommandBuffer);
        }

        /// <summary>
        /// THE SEAL GATES THE SUBMIT PATH AND NOTHING ELSE, so the encoder path needs its own answer. A scope that
        /// still held the committed handle would open an encoder on a command buffer Metal has already taken,
        /// which is not an exception this backend can report: it is a driver-side failed assertion
        /// (<c>_status &lt; MTLCommandBufferStatusCommitted</c>) that aborts the process. Asserted through the fake
        /// sink, so the claim is that nothing REACHED the native layer rather than that the driver forgave it.
        /// </summary>
        [Fact]
        public void AnEnsureAfterTheSubmitCannotReachTheDriverWithTheCommittedBuffer()
        {
            (MetalCommandList list, _, FakeMetalEncoderCalls calls, _) = NewList();

            list.Begin();
            list.End();
            list.MarkSubmitted(1);

            Assert.Throws<InvalidOperationException>(() => list.Encoders.EnsureBlitEncoder());
            Assert.Throws<InvalidOperationException>(() => list.Encoders.EnsureComputeEncoder());
            Assert.Throws<InvalidOperationException>(
                () => list.Encoders.EnsureRenderEncoder(new IntPtr(0xD5)));

            Assert.Empty(calls.Log);
            Assert.Equal(0, calls.EncoderBoundaries);
            Assert.Equal(MetalEncoderKind.None, list.Encoders.Open);
        }

        /// <summary>The same answer for the buffer this list released without committing, where the stale handle
        /// would be a released Objective-C object rather than a committed one.</summary>
        [Fact]
        public void AnEnsureAfterADiscardedRecordingCannotReachTheDriverEither()
        {
            (MetalCommandList list, _, FakeMetalEncoderCalls calls, _) = NewList();

            list.Begin();
            list.End();
            list.DiscardRecording();

            Assert.Throws<InvalidOperationException>(() => list.Encoders.EnsureBlitEncoder());
            Assert.Empty(calls.Log);
        }

        /// <summary>
        /// Disposing mid-recording is legal, the recording is discarded, and the OPEN ENCODER IS ENDED on the way
        /// out. The sink retains every encoder it opens and the end is the only release, so dropping one here
        /// leaks that +1 and, because an encoder holds a reference to its own command buffer, keeps the buffer
        /// alive after this list released it. The queue then never gets its uncommitted slot back and blocks
        /// inside <c>-commandBuffer</c> once enough of them have accumulated, which is a hang with nothing
        /// reporting why: <c>MetalUncommittedBuffers</c> already counted the buffer as released.
        /// </summary>
        [Fact]
        public void DisposingMidRecordingReleasesTheBufferAndEndsTheOpenEncoder()
        {
            (MetalCommandList list, FakeMetalCommandBufferSource buffers, FakeMetalEncoderCalls calls,
                MetalUncommittedBuffers uncommitted) = NewList();

            list.Begin();
            list.Encoders.EnsureRenderEncoder(new IntPtr(0xD5));
            list.Dispose();

            Assert.Equal(0, buffers.Outstanding);
            Assert.Equal(0, uncommitted.Outstanding);
            Assert.Equal(MetalEncoderKind.None, list.Encoders.Open);

            // A begin and its end, and the retain balanced with them.
            Assert.Equal(2, calls.EncoderBoundaries);
            Assert.Equal(0, calls.OutstandingEncoders);
            Assert.Equal(0, calls.UnbalancedEncoderReleases);
        }

        [Fact]
        public void DisposeIsIdempotent()
        {
            (MetalCommandList list, FakeMetalCommandBufferSource buffers, _, _) = NewList();

            list.Begin();
            list.Dispose();
            list.Dispose();

            Assert.Single(buffers.Released);
        }

        [Fact]
        public void EveryMemberIsRefusedAfterDisposal()
        {
            (MetalCommandList list, _, _, _) = NewList();
            list.Dispose();

            Assert.Throws<ObjectDisposedException>(list.Begin);
            Assert.Throws<ObjectDisposedException>(list.End);
        }

        /// <summary>
        /// A queue that will not hand out a command buffer is a device already in trouble, not a caller error:
        /// <c>-commandBuffer</c> takes no arguments to get wrong and BLOCKS rather than failing when the queue is
        /// merely full. Refusing by name beats recording into a nil handle, where every later call is a message
        /// to nil and the frame is silently empty.
        /// </summary>
        [Fact]
        public void ABeginThatCannotTakeABufferThrowsAndLeavesTheListUnrecorded()
        {
            (MetalCommandList list, FakeMetalCommandBufferSource buffers, _,
                MetalUncommittedBuffers uncommitted) = NewList();
            buffers.NextAcquireFails = true;

            Assert.Throws<InvalidOperationException>(list.Begin);
            Assert.False(list.IsRecording);
            Assert.Equal(0, uncommitted.Outstanding);
        }

        /// <summary>THE OWNERSHIP RULE, end to end: exactly one release per acquisition, at exactly one of the
        /// three exits. A leak here is an autoreleased command buffer retained for the life of the process, one
        /// per frame.</summary>
        [Fact]
        public void EveryAcquisitionIsReleasedExactlyOnceAcrossEveryExit()
        {
            (MetalCommandList list, FakeMetalCommandBufferSource buffers, _,
                MetalUncommittedBuffers uncommitted) = NewList();

            // Exit 1: committed.
            list.Begin();
            list.End();
            list.MarkSubmitted(1);

            // Exit 2: abandoned by the next Begin.
            list.Begin();
            list.End();
            list.Begin();

            // Exit 3: disposal.
            list.Dispose();

            Assert.Equal(3, buffers.Acquired.Count);
            Assert.Equal(0, uncommitted.Outstanding);
            Assert.Equal(2, buffers.Released.Count);
            Assert.Equal(buffers.Acquired[1], buffers.Released[0]);
            Assert.Equal(buffers.Acquired[2], buffers.Released[1]);
        }

        /// <summary>
        /// THE SAME RULE FOR ENCODERS, at the same three exits, because the retain is the same shape and the leak
        /// is worse. An encoder holds a reference to its own command buffer, so an abandoned one keeps a buffer
        /// counted against the queue's maximum number of uncommitted buffers after this list has released it, and
        /// <c>-commandBuffer</c> BLOCKS at that maximum rather than failing. The uncommitted counter cannot see
        /// it, so a leak here presents as a frame loop that hangs and nothing that says so.
        /// </summary>
        [Fact]
        public void EveryEncoderIsEndedExactlyOnceAcrossEveryExit()
        {
            (MetalCommandList list, _, FakeMetalEncoderCalls calls, _) = NewList();

            // Exit 1: committed. End is what closes the encoder, which is also what makes the buffer committable.
            list.Begin();
            list.Encoders.EnsureBlitEncoder();
            list.End();
            list.MarkSubmitted(1);

            // Exit 2: abandoned by the next Begin. The encoder is closed by End here too, because a list cannot
            // reach a Begin with one open: Begin refuses while recording and End ends unconditionally.
            list.Begin();
            list.Encoders.EnsureBlitEncoder();
            list.End();
            list.Begin();

            // Exit 3: disposal, mid-recording, with an encoder open. The one that leaked.
            list.Encoders.EnsureComputeEncoder();
            list.Dispose();

            Assert.Equal(3, calls.RetainedEncoders.Count);
            Assert.Equal(3, calls.ReleasedEncoders.Count);
            Assert.Equal(0, calls.OutstandingEncoders);
            Assert.Equal(0, calls.UnbalancedEncoderReleases);
        }

        [Fact]
        public void ANullDependencyIsRefusedAtConstruction()
        {
            MetalUncommittedBuffers uncommitted = new(MetalFramesInFlight.Default, new RecordingLogger());
            FakeMetalEncoderSink sink = new(new FakeMetalEncoderCalls());
            FakeMetalCommandBufferSource buffers = new();
            MetalRingAllocator rings = _harness.Rings;
            using MetalStagingArena arena = _harness.NewArena();
            FakeMetalBlitApi blit = _harness.Blit;
            FakeMetalDeviceLiveness liveness = _harness.Liveness;
            FakeMetalRenderApi render = new(new FakeMetalRenderCalls());
            object owner = new();

            Assert.Throws<ArgumentNullException>(
                () => new MetalCommandList(null!, uncommitted, sink, owner, rings, arena, blit, liveness, render));
            Assert.Throws<ArgumentNullException>(
                () => new MetalCommandList(buffers, null!, sink, owner, rings, arena, blit, liveness, render));
            Assert.Throws<ArgumentNullException>(
                () => new MetalCommandList(buffers, uncommitted, null!, owner, rings, arena, blit, liveness, render));
            Assert.Throws<ArgumentNullException>(
                () => new MetalCommandList(buffers, uncommitted, sink, null!, rings, arena, blit, liveness, render));
            Assert.Throws<ArgumentNullException>(
                () => new MetalCommandList(buffers, uncommitted, sink, owner, null!, arena, blit, liveness, render));
            Assert.Throws<ArgumentNullException>(
                () => new MetalCommandList(buffers, uncommitted, sink, owner, rings, null!, blit, liveness, render));
            Assert.Throws<ArgumentNullException>(
                () => new MetalCommandList(buffers, uncommitted, sink, owner, rings, arena, null!, liveness,
                    render));
            Assert.Throws<ArgumentNullException>(
                () => new MetalCommandList(buffers, uncommitted, sink, owner, rings, arena, blit, null!, render));
            Assert.Throws<ArgumentNullException>(
                () => new MetalCommandList(buffers, uncommitted, sink, owner, rings, arena, blit, liveness, null!));
        }

        /// <summary>
        /// THE LEDGER OF UNBUILT MEMBERS names the row that builds each one, so a reader who hits it knows
        /// whether to wait for a row or file a bug. Asserted rather than left as prose, because a message that
        /// says "not built" without saying by whom is what sends the next reader to the issue tracker.
        /// <para>
        /// IT MOVED FROM <c>SetFramebuffer</c> TO <c>SetPipeline</c> WHEN ROW 12 LANDED
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/578), which is the ledger discipline working: a row
        /// that fills a member takes it off the list, and this row keeps testing whatever is still on it. The
        /// live rows it names grow with each one, because a reader who hits the message needs to know whether
        /// the backend is unfinished or their machine is wrong.
        /// </para>
        /// </summary>
        [Fact]
        public void AnUnbuiltMemberNamesItsRowAndSaysWhatIsLive()
        {
            (MetalCommandList list, _, _, _) = NewList();
            list.Begin();

            NotSupportedException thrown =
                Assert.Throws<NotSupportedException>(() => list.SetPipeline(null!));

            Assert.Contains("issues/577", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("issues/573", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("issues/578", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("GpuBackendKind.Metal", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// AND THE FIVE PASS MEMBERS ARE LIVE, so what a caller gets from one is its own refusal rather than the
        /// ledger's. A recording guard rather than an unbuilt row is the difference between "wait for a row" and
        /// "call Begin", and those need different answers.
        /// </summary>
        [Fact]
        public void ThePassMembersRefuseALisThatIsNotRecording()
        {
            (MetalCommandList list, _, _, _) = NewList();

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => list.ClearColorTarget(0, new Color(1f, 1f, 1f, 1f)));

            Assert.Contains("Call Begin first", thrown.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("not built", thrown.Message, StringComparison.Ordinal);
        }
    }
}
