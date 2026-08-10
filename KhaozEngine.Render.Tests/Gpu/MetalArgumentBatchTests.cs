using System;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE RUN CUTTING (M-R6), DRIVEN DIRECTLY. <see cref="MetalArgumentBatch"/> is what turns a set of
    /// (index, object, offset) triples into array calls, and the two things it can be wrong about are invisible
    /// through the flush above it: a run that swallows a hole would unbind something, and two entries at one
    /// index would write one and then overwrite it.
    ///
    /// <para>
    /// DRIVEN HERE RATHER THAN THROUGH A PROGRAM, because arranging a non-contiguous index run out of a real MSL
    /// emission would mean writing a shader whose emission happens to skip an index, which is a fact about
    /// SPIRV-Cross rather than about this type and would stop being true on a package bump. The flush's own tests
    /// bind through a real table and this one owns the arithmetic.
    /// </para>
    /// </summary>
    public sealed class MetalArgumentBatchTests
    {
        /// <summary>
        /// A HOLE CUTS THE RUN AND IS NOT PADDED WITH NIL. One call over the whole span with nil in the gap
        /// would be one native call instead of two, and it would UNBIND whatever is legitimately sitting in the
        /// gap: Metal's argument tables are absolute and per encoder, so an index this flush is not writing still
        /// holds what an earlier flush or an earlier slot put there.
        /// </summary>
        [Fact]
        public void AHoleCutsTheRunRatherThanBeingPaddedWithNil()
        {
            var batch = new MetalArgumentBatch();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            batch.Add(MetalIndexSpace.Buffer, 0, new IntPtr(0xA), 16);
            batch.Add(MetalIndexSpace.Buffer, 1, new IntPtr(0xB), 32);
            batch.Add(MetalIndexSpace.Buffer, 4, new IntPtr(0xC), 64);

            batch.Emit(ref sink, MetalShaderStage.Fragment, Encoder);

            Assert.Equal(2, calls.ArrayWrites.Count);

            Assert.Equal(0u, calls.ArrayWrites[0].FirstIndex);
            Assert.Equal(new[] { new IntPtr(0xA), new IntPtr(0xB) }, calls.ArrayWrites[0].Objects);
            Assert.Equal(new nuint[] { 16, 32 }, calls.ArrayWrites[0].Offsets);

            Assert.Equal(4u, calls.ArrayWrites[1].FirstIndex);
            Assert.Equal(new[] { new IntPtr(0xC) }, calls.ArrayWrites[1].Objects);
        }

        /// <summary>
        /// THE INDICES ARRIVE IN SLOT ORDER AND ARE EMITTED IN INDEX ORDER, because the two are unrelated: an
        /// element's index is a fact about the emission, so slot 0's uniform can land at a higher index than slot
        /// 1's. Over the shipped set 80 of 159 emitted arguments carry an index that differs from their binding
        /// number, so this is the ordinary case.
        /// </summary>
        [Fact]
        public void EntriesAddedOutOfOrderAreEmittedAsOneAscendingRun()
        {
            var batch = new MetalArgumentBatch();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            batch.Add(MetalIndexSpace.Texture, 3, new IntPtr(0xD), 0);
            batch.Add(MetalIndexSpace.Texture, 1, new IntPtr(0xB), 0);
            batch.Add(MetalIndexSpace.Texture, 2, new IntPtr(0xC), 0);

            batch.Emit(ref sink, MetalShaderStage.Fragment, Encoder);

            FakeMetalArrayWrite write = Assert.Single(calls.ArrayWrites);
            Assert.Equal(1u, write.FirstIndex);
            Assert.Equal(new[] { new IntPtr(0xB), new IntPtr(0xC), new IntPtr(0xD) }, write.Objects);
        }

        /// <summary>
        /// THE THREE SPACES ARE INDEPENDENT, which is section 8.1's fact rather than a policy: index 0 means
        /// three different things, so three entries at index 0 are three calls through three different selectors
        /// and not a collision.
        /// </summary>
        [Fact]
        public void OneIndexInEachSpaceIsThreeCallsRatherThanACollision()
        {
            var batch = new MetalArgumentBatch();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            batch.Add(MetalIndexSpace.Buffer, 0, new IntPtr(0xA), 0);
            batch.Add(MetalIndexSpace.Texture, 0, new IntPtr(0xB), 0);
            batch.Add(MetalIndexSpace.Sampler, 0, new IntPtr(0xC), 0);

            batch.Emit(ref sink, MetalShaderStage.Compute, Encoder);

            Assert.Equal(
                new[] { MetalIndexSpace.Buffer, MetalIndexSpace.Texture, MetalIndexSpace.Sampler },
                calls.ArrayWrites.Select(w => w.Space).ToArray());
        }

        /// <summary>
        /// TWO ENTRIES AT ONE INDEX ARE REFUSED RATHER THAN RESOLVED. It cannot happen through a correct index
        /// table, so a collision means the table and the sets bound against it disagree about what is where, and
        /// the run it would produce writes one resource and then overwrites it with nothing reported.
        /// </summary>
        [Fact]
        public void TwoEntriesAtOneIndexAreRefusedByName()
        {
            var batch = new MetalArgumentBatch();
            var sink = new FakeMetalEncoderSink(new FakeMetalEncoderCalls());

            batch.Add(MetalIndexSpace.Buffer, 2, new IntPtr(0xA), 0);
            batch.Add(MetalIndexSpace.Buffer, 2, new IntPtr(0xB), 0);

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => batch.Emit(ref sink, MetalShaderStage.Vertex, Encoder));

            Assert.Contains("[[buffer(2)]]", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("vertex stage", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>An emission clears the batch even when a sink threw part way through it, so entries staged
        /// for one stage cannot be emitted a second time into the next stage's tables.</summary>
        [Fact]
        public void AnEmissionClearsTheBatchEvenWhenItThrew()
        {
            var batch = new MetalArgumentBatch();
            var sink = new FakeMetalEncoderSink(new FakeMetalEncoderCalls());

            batch.Add(MetalIndexSpace.Buffer, 2, new IntPtr(0xA), 0);
            batch.Add(MetalIndexSpace.Buffer, 2, new IntPtr(0xB), 0);
            Assert.Equal(2, batch.CountIn(MetalIndexSpace.Buffer));

            Assert.ThrowsAny<Exception>(() => batch.Emit(ref sink, MetalShaderStage.Vertex, Encoder));

            Assert.Equal(0, batch.CountIn(MetalIndexSpace.Buffer));
        }

        /// <summary>An empty batch emits nothing at all, which is what makes a stage that references none of the
        /// dirty slots' elements cost zero native calls rather than an empty range.</summary>
        [Fact]
        public void AnEmptyBatchEmitsNothing()
        {
            var batch = new MetalArgumentBatch();
            var calls = new FakeMetalEncoderCalls();
            var sink = new FakeMetalEncoderSink(calls);

            batch.Emit(ref sink, MetalShaderStage.Vertex, Encoder);

            Assert.Equal(0, calls.ArgumentTableWrites);
        }

        static readonly IntPtr Encoder = new(0x4D544C45);
    }
}
