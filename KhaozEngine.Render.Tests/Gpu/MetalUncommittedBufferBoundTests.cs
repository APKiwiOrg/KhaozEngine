using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// SECTION 6.1's UNCOMMITTED-COMMAND-BUFFER BOUND: that the backend never holds more uncommitted
    /// <c>MTLCommandBuffer</c>s than <c>FramesInFlight</c> plus one. Row 7 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para><b>WHY THIS IS A TEST RATHER THAN A COMMENT.</b> <c>MTLCommandQueue</c> has a maximum number of
    /// uncommitted command buffers and <c>-commandBuffer</c> BLOCKS when it is reached. That is a real bound with
    /// a real block, it is NOT the uniform ring's, and a blocked <c>-commandBuffer</c> would present as a
    /// frame-loop stall with no counter attached, which is exactly the shape section 16 exists to keep off the
    /// list. Two things keep the queue's bound out of reach rather than relying on it: <c>Begin</c> waits on the
    /// ring's frame slot, and this counter says whether the first one worked.</para>
    ///
    /// <para><b>THE PLUS ONE IS M-W6's PRESENT BUFFER</b>, which row 15 makes occupied
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/581). Until then the recording shapes this backend can
    /// produce peak one lower, which is a fact about coverage rather than about the bound, so the assertions
    /// below are written against <c>Bound</c> rather than against a number.</para>
    /// </summary>
    public sealed class MetalUncommittedBufferBoundTests : IDisposable
    {
        readonly MetalRingHarness _harness = new();

        /// <inheritdoc/>
        public void Dispose() => _harness.Dispose();

        MetalCommandList NewList(FakeMetalCommandBufferSource buffers, MetalUncommittedBuffers uncommitted)
            => _harness.NewList(new object(), buffers, uncommitted: uncommitted);

        [Fact]
        public void TheBoundIsTheFrameDepthPlusThePresentBuffer()
        {
            MetalUncommittedBuffers uncommitted = new(MetalFramesInFlight.Default, new RecordingLogger());

            Assert.Equal(MetalFramesInFlight.Default + 1, uncommitted.Bound);
            Assert.Equal(0, uncommitted.Outstanding);
            Assert.Equal(0, uncommitted.Peak);
            Assert.False(uncommitted.ExceededBound);
        }

        /// <summary>
        /// THE REAL SHAPE: <c>FramesInFlight</c> lists, each recorded and submitted in turn, which is what a
        /// frame loop at full depth does. The peak must stay inside the bound, and it is the PEAK rather than the
        /// instantaneous count that matters, because the queue blocks at a moment nobody is watching.
        /// </summary>
        [Fact]
        public void AFrameLoopAtFullDepthStaysInsideTheBound()
        {
            const int frames = MetalFramesInFlight.Default;
            FakeMetalCommandBufferSource buffers = new();
            MetalUncommittedBuffers uncommitted = new(frames, new RecordingLogger());
            List<MetalCommandList> lists = new();

            for (int i = 0; i < frames; i++) lists.Add(NewList(buffers, uncommitted));

            // Every list open at once, which is the worst case this backend can reach without the present buffer:
            // N lists recording concurrently is a genuine backend property here (M-R3).
            foreach (MetalCommandList list in lists)
            {
                list.Begin();
                list.End();
            }

            Assert.Equal(frames, uncommitted.Outstanding);

            foreach (MetalCommandList list in lists) list.MarkSubmitted(1);

            Assert.Equal(0, uncommitted.Outstanding);
            Assert.Equal(frames, uncommitted.Peak);
            Assert.True(uncommitted.Peak <= uncommitted.Bound,
                $"the backend held {uncommitted.Peak} uncommitted command buffers against a bound of "
                + $"{uncommitted.Bound}, so -commandBuffer can block with nothing counting it");
            Assert.False(uncommitted.ExceededBound);
            foreach (MetalCommandList list in lists) list.Dispose();
        }

        /// <summary>Re-Begin in a loop is the shape that would leak one buffer per frame if the abandoned
        /// recording were not released before the new one is taken, and it is the shape a frame loop actually
        /// has: one list, re-recorded every frame.</summary>
        [Fact]
        public void OneListRecordedEveryFrameHoldsOneBuffer()
        {
            FakeMetalCommandBufferSource buffers = new();
            MetalUncommittedBuffers uncommitted = new(MetalFramesInFlight.Default, new RecordingLogger());
            using MetalCommandList list = NewList(buffers, uncommitted);

            for (int frame = 0; frame < 32; frame++)
            {
                list.Begin();
                list.End();
                list.MarkSubmitted(1);
            }

            Assert.Equal(0, uncommitted.Outstanding);
            Assert.Equal(1, uncommitted.Peak);
            Assert.Equal(32, buffers.Acquired.Count);
        }

        /// <summary>
        /// EXCEEDING THE BOUND REPORTS AND DOES NOT THROW. It is a pacing defect rather than a corruption: the
        /// work is still correct and the queue's own block is what would eventually stop it, so throwing would
        /// turn a measurable pacing problem into a crash in a consumer's frame loop. The warning fires ONCE, so a
        /// frame loop past the bound does not produce one line per frame.
        /// </summary>
        [Fact]
        public void PassingTheBoundWarnsOnceAndKeepsCounting()
        {
            RecordingLogger logger = new();
            MetalUncommittedBuffers uncommitted = new(1, logger);

            for (int i = 0; i < 5; i++) uncommitted.Acquired();

            Assert.Equal(5, uncommitted.Outstanding);
            Assert.Equal(5, uncommitted.Peak);
            Assert.True(uncommitted.ExceededBound);
            Assert.Single(logger.Warns);
            Assert.Contains(MetalFramesInFlight.EnvVarName, logger.Warns[0], StringComparison.Ordinal);
        }

        [Fact]
        public void ThePeakRemembersTheHighWaterAfterEverythingIsReleased()
        {
            MetalUncommittedBuffers uncommitted = new(MetalFramesInFlight.Default, new RecordingLogger());

            uncommitted.Acquired();
            uncommitted.Acquired();
            uncommitted.Released();
            uncommitted.Released();

            Assert.Equal(0, uncommitted.Outstanding);
            Assert.Equal(2, uncommitted.Peak);
        }
    }
}
