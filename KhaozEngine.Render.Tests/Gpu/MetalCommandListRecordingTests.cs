using System;
using KhaozEngine.Gpu.Metal.Internal;
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
    public sealed class MetalCommandListRecordingTests
    {
        static (MetalCommandList List, FakeMetalCommandBufferSource Buffers, FakeMetalEncoderCalls Calls,
            MetalUncommittedBuffers Uncommitted) NewList(int framesInFlight = MetalFramesInFlight.Default)
        {
            FakeMetalCommandBufferSource buffers = new();
            FakeMetalEncoderCalls calls = new();
            MetalUncommittedBuffers uncommitted = new(framesInFlight, new RecordingLogger());
            MetalCommandList list = new(buffers, uncommitted, new FakeMetalEncoderSink(calls));
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
            list.MarkSubmitted();

            Assert.False(list.IsSealed);
            Assert.Equal(0, uncommitted.Outstanding);

            // NOT released through the source: a committed buffer is the queue's until it completes, and the
            // release paired with the commit is the submit path's.
            Assert.Empty(buffers.Released);
            Assert.Throws<InvalidOperationException>(() => _ = list.SealedCommandBuffer);
        }

        /// <summary>Disposing mid-recording is legal and ends nothing: the recording is discarded, which is what
        /// disposing a list mid-record asks for, and an endEncoding on a buffer nobody will commit is a native
        /// call bought for nothing.</summary>
        [Fact]
        public void DisposingMidRecordingReleasesTheBufferAndEndsNoEncoder()
        {
            (MetalCommandList list, FakeMetalCommandBufferSource buffers, FakeMetalEncoderCalls calls,
                MetalUncommittedBuffers uncommitted) = NewList();

            list.Begin();
            list.Encoders.EnsureRenderEncoder(new IntPtr(0xD5));
            list.Dispose();

            Assert.Equal(0, buffers.Outstanding);
            Assert.Equal(0, uncommitted.Outstanding);
            Assert.Equal(1, calls.EncoderBoundaries);
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
            list.MarkSubmitted();

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

        [Fact]
        public void ANullDependencyIsRefusedAtConstruction()
        {
            MetalUncommittedBuffers uncommitted = new(MetalFramesInFlight.Default, new RecordingLogger());
            FakeMetalEncoderSink sink = new(new FakeMetalEncoderCalls());

            Assert.Throws<ArgumentNullException>(() => new MetalCommandList(null!, uncommitted, sink));
            Assert.Throws<ArgumentNullException>(
                () => new MetalCommandList(new FakeMetalCommandBufferSource(), null!, sink));
            Assert.Throws<ArgumentNullException>(
                () => new MetalCommandList(new FakeMetalCommandBufferSource(), uncommitted, null!));
        }

        /// <summary>
        /// THE LEDGER OF UNBUILT MEMBERS names the row that builds each one, so a reader who hits it knows
        /// whether to wait for a row or file a bug. Asserted rather than left as prose, because a message that
        /// says "not built" without saying by whom is what sends the next reader to the issue tracker.
        /// </summary>
        [Fact]
        public void AnUnbuiltMemberNamesItsRowAndSaysWhatIsLive()
        {
            (MetalCommandList list, _, _, _) = NewList();
            list.Begin();

            NotSupportedException thrown =
                Assert.Throws<NotSupportedException>(() => list.SetFramebuffer(null!));

            Assert.Contains("issues/578", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("issues/573", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("GpuBackendKind.Metal", thrown.Message, StringComparison.Ordinal);
        }
    }
}
