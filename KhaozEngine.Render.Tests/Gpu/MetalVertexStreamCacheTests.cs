using System;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE VERTEX-STREAM CACHE, AND THE INVALIDATION THAT MAKES KEEPING IT SAFE (section 6.3, M-R4, M-B2).
    /// Work-breakdown row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/579).
    ///
    /// <para>
    /// THE REGRESSION THIS IS AIMED AT IS RECORDED RATHER THAN IMAGINED. The incumbent's
    /// <c>EndCurrentRenderPass</c> clears the active-set array and re-marks the viewport and scissor, and does
    /// NOT clear <c>_vertexBuffersActive</c>. What stops that being a corruption is a SECOND defect:
    /// <c>PreDrawCommand</c>'s loop issues <c>setVertexBuffer</c> when the flag is false and never sets it true,
    /// so the cache is permanently cold. Porting the tracking without the invalidation ships a corruption no
    /// golden would catch, because the goldens do not restart a render pass mid-scene. So the boundary test here
    /// is the load-bearing one and the redundancy test is the reason the boundary test can fail at all.
    /// </para>
    /// </summary>
    public sealed class MetalVertexStreamCacheTests
    {
        // M-B2's NUMBERING IS PINNED ONCE, IN MetalVertexInputTests.StreamsAreNumberedFromTheTopDownward, and
        // this row deliberately does not carry a second copy. Two independent pieces of code read the mapping
        // (this flush's setVertexBuffers: index and row 11's MTLVertexDescriptor layout index) and a device
        // reports nothing at all when they disagree, which is an argument for ONE type and therefore for one
        // pin: a second copy of the numbers here would pass while the two readers drifted, because it would be
        // asserting the same shared type both of them already come from.

        /// <summary>
        /// TWO DIRTY STREAMS ARE ONE ARRAY CALL, not one per stream. They are ordinary <c>[[buffer(n)]]</c>
        /// bindings of the vertex stage, so they go through the same <c>setVertexBuffers:offsets:withRange:</c>
        /// the resource buffers do, which is why <see cref="IMetalEncoderSink"/> has no member of their own. The
        /// run is ASCENDING in index, which for streams means descending in slot.
        /// </summary>
        [Fact]
        public void TwoDirtyStreamsAreOneArrayCallOverTheirContiguousRun()
        {
            var streams = new MetalVertexStreamRecords();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            streams.Record(0, new IntPtr(0xA), 0);
            streams.Record(1, new IntPtr(0xB), 48);
            streams.Flush(ref sink, Encoder, Epoch);

            FakeMetalArrayWrite write = Assert.Single(calls.ArrayWrites);
            Assert.Equal(MetalShaderStage.Vertex, write.Stage);
            Assert.Equal(MetalIndexSpace.Buffer, write.Space);

            // Stream 1 is index 29 and stream 0 is index 30, so the run starts at 29 and stream 0's buffer is
            // the SECOND entry.
            Assert.Equal(29u, write.FirstIndex);
            Assert.Equal(new[] { new IntPtr(0xB), new IntPtr(0xA) }, write.Objects);
            Assert.Equal(new nuint[] { 48, 0 }, write.Offsets);
        }

        /// <summary>
        /// THE CACHE IS ACTUALLY MAINTAINED, which is what the incumbent's second defect prevents. A re-record of
        /// the same buffer at the same offset marks nothing, and a second draw emits nothing. That marginal is a
        /// REGRESSION target rather than a parity target: the incumbent pays one call per stream per draw
        /// unconditionally, so a change that reintroduces the unconditional bind is a red test rather than an
        /// invisible cost.
        /// </summary>
        [Fact]
        public void ASecondDrawWithTheSameStreamsEmitsNothing()
        {
            var streams = new MetalVertexStreamRecords();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            streams.Record(0, new IntPtr(0xA), 0);
            streams.Flush(ref sink, Encoder, Epoch);
            Assert.Equal(1, calls.ArgumentTableWrites);

            for (int i = 0; i < 20; i++) streams.Record(0, new IntPtr(0xA), 0);
            streams.Flush(ref sink, Encoder, Epoch);

            Assert.Equal(1, calls.ArgumentTableWrites);
            Assert.False(streams.IsDirty(0));

            // AND MOVING THE OFFSET ALONE IS STILL A REBIND, because a stream has no offsets-only call: the
            // window is part of the binding rather than a separate integer the way a resource buffer's is.
            streams.Record(0, new IntPtr(0xA), 64);
            streams.Flush(ref sink, Encoder, Epoch);

            Assert.Equal(2, calls.ArgumentTableWrites);
            Assert.Equal((nuint)64, calls.ArrayWrites[1].Offsets[0]);
        }

        /// <summary>
        /// THE LOAD-BEARING ONE (M-R4), WRITTEN BEHAVIOURALLY. Record a stream, flush it, force an encoder end
        /// through a blit, reopen, and assert the second flush RE-ISSUES the stream bind although nothing about
        /// the stream changed. It fails on the corruption rather than on the bookkeeping: the incumbent's shape
        /// leaves the flag set across the boundary and the second draw reads vertex data from an argument table
        /// that no longer has any.
        /// </summary>
        [Fact]
        public void AnEncoderBoundaryReIssuesEveryStreamAlthoughNothingChanged()
        {
            var streams = new MetalVertexStreamRecords();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);
            var scope = new MetalEncoderScope(sink);
            scope.BeginRecording(new IntPtr(0x100));

            IntPtr first = scope.EnsureRenderEncoder(Descriptor);
            streams.Record(0, new IntPtr(0xA), 0);
            streams.Flush(ref sink, first, scope.Epoch);

            Assert.Equal(1, calls.ArgumentTableWrites);
            Assert.True(streams.IsEmittedIn(0, scope.Epoch));

            scope.EnsureBlitEncoder();
            IntPtr second = scope.EnsureRenderEncoder(Descriptor);

            Assert.NotEqual(first, second);
            Assert.False(streams.IsDirty(0));
            Assert.False(streams.IsEmittedIn(0, scope.Epoch));

            streams.Flush(ref sink, second, scope.Epoch);

            Assert.Equal(2, calls.ArgumentTableWrites);
            Assert.Equal(second, calls.ArrayWrites[1].Encoder);
            Assert.Equal(new[] { new IntPtr(0xA) }, calls.ArrayWrites[1].Objects);
        }

        /// <summary>A stream recorded as nil is BOUND to nil rather than skipped, which is the difference from a
        /// null resource set: a stream is one index rather than a whole set's worth, and writing nil there is how
        /// a caller unbinds it.</summary>
        [Fact]
        public void ANilStreamIsBoundRatherThanSkipped()
        {
            var streams = new MetalVertexStreamRecords();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            streams.Record(0, new IntPtr(0xA), 0);
            streams.Flush(ref sink, Encoder, Epoch);

            streams.Record(0, IntPtr.Zero, 0);
            streams.Flush(ref sink, Encoder, Epoch);

            Assert.Equal(2, calls.ArrayWrites.Count);
            Assert.Equal(IntPtr.Zero, calls.ArrayWrites[1].Objects[0]);
        }

        /// <summary>A wild slot is refused by name, a nil encoder with work owed is refused, and a
        /// <c>Reset</c> forgets everything.</summary>
        [Fact]
        public void AWildSlotAndANilEncoderAreRefusedAndAResetForgets()
        {
            var streams = new MetalVertexStreamRecords();
            var sink = new FakeMetalEncoderSink(new FakeMetalEncoderCalls());

            Assert.Throws<ArgumentOutOfRangeException>(
                () => streams.Record(MetalVertexStreamRecords.MaxSlot + 1, new IntPtr(0xA), 0));

            // A flush with nothing owed does not need an encoder at all.
            streams.Flush(ref sink, IntPtr.Zero, Epoch);

            streams.Record(0, new IntPtr(0xA), 0);
            Assert.Throws<InvalidOperationException>(() => streams.Flush(ref sink, IntPtr.Zero, Epoch));
            Assert.True(streams.IsDirty(0));

            streams.Reset();
            Assert.Equal(0, streams.RecordedSlotCount);
            Assert.Equal(IntPtr.Zero, streams.RecordedBuffer(0));
        }

        static readonly IntPtr Encoder = new(0x4D544C45);
        static readonly IntPtr Descriptor = new(0x4D544C44);

        const ulong Epoch = 7;
    }
}
