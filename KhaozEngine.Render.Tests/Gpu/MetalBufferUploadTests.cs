using System;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE RECORD-TIME UPLOAD FORK (2.1, M-M3, M-M8), device-free, over a real
    /// <see cref="MetalEncoderScope"/> so the boundaries are counted through the very seam M-T2 freezes its
    /// budget over.
    ///
    /// <para><b>THE CLAIM UNDER TEST IS A NEGATIVE, AND THAT IS WHY IT NEEDS A TEST AT ALL.</b> A backend that
    /// sent uniform writes down the staging path would render byte-identical pixels and would cost a FULL
    /// graphics-state re-activation per write, because opening a blit encoder ends the render encoder and
    /// discards the pipeline, every argument-table entry, the viewport, the scissor and every vertex stream
    /// (M-R4). No golden can see that. The incumbent does exactly it, for every record-time
    /// <c>UpdateBuffer</c> including the per-draw uniform ones, and moving those off that path is the whole
    /// reason the ring exists.</para>
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Either a uniform write reached the encoder (the regression this file
    /// exists for), or a bulk write did not (which is a copy that never happened and bytes that never arrive), or
    /// the staged payload does not match what the copy names.</para>
    /// </summary>
    public sealed class MetalBufferUploadTests : IDisposable
    {
        const uint DestinationSize = 1024;

        // The segment a recording would have captured at its Begin. Nothing here opens one, so it is the segment
        // a first recording gets, and the routing this file asserts does not depend on which one it is.
        const int Segment = 0;

        static readonly IntPtr Destination = new(0xDEAD);

        readonly MetalRingHarness _harness = new();
        readonly FakeMetalEncoderCalls _calls = new();
        readonly MetalEncoderScope _encoders;
        readonly MetalStagingArena _arena;

        public MetalBufferUploadTests()
        {
            _encoders = new MetalEncoderScope(new FakeMetalEncoderSink(_calls));
            _encoders.BeginRecording(new IntPtr(0x100));
            _arena = _harness.NewArena();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _arena.Dispose();
            _harness.Dispose();
        }

        /// <summary>
        /// THE HEADLINE: a ring-backed write opens NO ENCODER, emits NO COPY and takes NO STAGING BLOCK. Not
        /// "fewer than before" and not "usually", none.
        /// </summary>
        [Fact]
        public void ARingBackedWriteOpensNoEncoderAndEmitsNoCopy()
        {
            MetalUniformRing ring = _harness.NewRing(256, out _);
            byte[] payload = Payload(64, seed: 1);

            for (int i = 0; i < 16; i++) Record(ring, 0, payload);

            Assert.Equal(0, _calls.EncoderBoundaries);
            Assert.Equal(MetalEncoderKind.None, _encoders.Open);
            Assert.Empty(_harness.Blit.Copies);
            Assert.Equal(0, _arena.BlocksCreated);
        }

        /// <summary>
        /// AND THE COMPARISON THAT MAKES THAT NUMBER MEAN SOMETHING: the same call against a NON-uniform buffer
        /// opens an encoder and emits a copy. A test that only asserted the zero would pass on a backend whose
        /// upload path did nothing at all.
        /// </summary>
        [Fact]
        public void ABulkWriteOpensExactlyOneBlitEncoderAndEmitsExactlyOneCopy()
        {
            byte[] payload = Payload(64, seed: 2);

            Record(ring: null, 0, payload);

            Assert.Equal(MetalEncoderKind.Blit, _encoders.Open);
            Assert.Equal(1, _calls.EncoderBegins);
            Assert.Single(_harness.Blit.Copies);
        }

        /// <summary>
        /// A RUN OF BULK WRITES INSIDE ONE ENCODER PAYS THE BOUNDARY ONCE, because
        /// <see cref="MetalEncoderScope.EnsureBlitEncoder"/> is an Ensure rather than a Begin. That is the
        /// property the budget seam measures, and it is what makes the boundary count a statement about how many
        /// times the recorder SWITCHED kinds rather than how many uploads it made.
        /// </summary>
        [Fact]
        public void ARunOfBulkWritesSharesOneEncoderAndOneBlock()
        {
            byte[] payload = Payload(32, seed: 3);

            for (int i = 0; i < 8; i++) Record(ring: null, (uint)(i * 64), payload);

            Assert.Equal(1, _calls.EncoderBegins);
            Assert.Equal(8, _harness.Blit.Copies.Count);
            Assert.Equal(1, _arena.BlocksCreated);

            // Every copy names a distinct source range inside the one block, which is what sub-allocation is.
            Assert.Equal(8, _harness.Blit.Copies.Select(copy => copy.SourceOffset).Distinct().Count());
            Assert.Single(_harness.Blit.Copies.Select(copy => copy.Source).Distinct());
        }

        /// <summary>The bytes a bulk write stages are the bytes the copy names, at the offset it names, in the
        /// block it names. Without this the arena could be leasing correctly and the copy pointing
        /// elsewhere.</summary>
        [Fact]
        public void TheStagedBytesAreTheOnesTheCopyNames()
        {
            byte[] payload = Payload(48, seed: 4);

            Record(ring: null, 256, payload);

            (IntPtr _, IntPtr source, ulong sourceOffset, IntPtr destination, ulong destinationOffset,
                ulong size) = _harness.Blit.Copies[0];

            Assert.Equal(Destination, destination);
            Assert.Equal(256ul, destinationOffset);
            Assert.Equal(48ul, size);

            byte[] block = _harness.Staging.Contents(source);
            Assert.Equal(payload, block.Skip((int)sourceOffset).Take(payload.Length).ToArray());
        }

        /// <summary>
        /// THE SIZE PAD IS THE INCUMBENT'S, and the pad bytes are ZEROED rather than left holding whatever the
        /// block last carried. macOS requires the copy's size to be a multiple of four and
        /// <see cref="MetalBufferPolicy.AllocationBytes"/> is what makes the extra bytes land inside the
        /// destination's allocation. Leaving them stale would make a byte-for-byte readback depend on which
        /// upload previously used that block, which is a nondeterminism a golden finds months later and blames on
        /// something else.
        /// </summary>
        [Fact]
        public void AnOddLengthIsPaddedUpToFourAndThePadIsZeroed()
        {
            // First fill the block with a payload whose bytes are all non-zero, then reuse it.
            byte[] first = Payload(64, seed: 5);
            Record(ring: null, 0, first);

            byte[] second = { 0x11, 0x22, 0x33, 0x44, 0x55 };
            Record(ring: null, 128, second);

            (IntPtr _, IntPtr source, ulong sourceOffset, IntPtr _, ulong _, ulong size) =
                _harness.Blit.Copies[1];

            Assert.Equal(8ul, size);

            byte[] block = _harness.Staging.Contents(source);
            byte[] staged = block.Skip((int)sourceOffset).Take(8).ToArray();

            Assert.Equal(second, staged.Take(5).ToArray());
            Assert.Equal(new byte[] { 0, 0, 0 }, staged.Skip(5).ToArray());
        }

        [Theory]
        [InlineData(0u, 0u)]
        [InlineData(1u, 4u)]
        [InlineData(3u, 4u)]
        [InlineData(4u, 4u)]
        [InlineData(5u, 8u)]
        [InlineData(64u, 64u)]
        public void ThePadRoundsUpToFour(uint length, uint expected)
            => Assert.Equal(expected, MetalStagingArena.AlignedCopyBytes(length));

        /// <summary>
        /// AN UNALIGNED DESTINATION OFFSET IS REFUSED BY NAME, which is section 9.3's ruling: the size half is
        /// padded and the offset half throws, because the incumbent's answer to it is an embedded compute shader
        /// and a dedicated pipeline for a case no shipped call site produces. The message says what to do
        /// instead.
        /// </summary>
        [Fact]
        public void AnUnalignedDestinationOffsetIsRefusedByName()
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => Record(ring: null, 3, Payload(16, seed: 6)));

            Assert.Contains("multiple of 4", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("device-level UpdateBuffer", thrown.Message, StringComparison.Ordinal);

            // Nothing was staged and nothing was encoded, so the refusal is not half a recording.
            Assert.Equal(0, _calls.EncoderBoundaries);
            Assert.Empty(_harness.Blit.Copies);
        }

        /// <summary>A RING-BACKED WRITE NEEDS NO SUCH ALIGNMENT, because it is a memcpy rather than a copy
        /// command. Asserting it is what keeps the refusal narrow: applying it to both paths would refuse a
        /// uniform write the seam allows and the ring handles perfectly.</summary>
        [Fact]
        public void ARingBackedWriteTakesAnUnalignedOffsetHappily()
        {
            MetalUniformRing ring = _harness.NewRing(256, out _);
            byte[] payload = Payload(5, seed: 7);

            Record(ring, 3, payload);

            Assert.Equal(payload, ring.ReadSegment(Segment, 3, payload.Length));
        }

        /// <summary>A write past the destination's logical size is refused before anything is staged, on the
        /// bulk path exactly as the ring path refuses its own.</summary>
        [Fact]
        public void ABulkWritePastTheEndIsRefusedBeforeAnythingIsStaged()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => Record(ring: null, DestinationSize - 8, Payload(16, seed: 8)));

            Assert.Equal(0, _arena.BlocksCreated);
            Assert.Empty(_harness.Blit.Copies);
        }

        /// <summary>An empty payload is a no-op on both paths rather than a recorded copy of nothing.</summary>
        [Fact]
        public void AnEmptyPayloadRecordsNothingOnEitherPath()
        {
            MetalUniformRing ring = _harness.NewRing(256, out _);

            Record(ring, 0, Array.Empty<byte>());
            Record(ring: null, 0, Array.Empty<byte>());

            Assert.Equal(0, _calls.EncoderBoundaries);
            Assert.Empty(_harness.Blit.Copies);
            Assert.Equal(0, _arena.BlocksCreated);
        }

        /// <summary>
        /// A NIL ENCODER LEAVES THE LEASE LEASED AND EMITS NOTHING, rather than throwing. Metal answers nil when
        /// the command buffer is in a state it will not encode into, and
        /// <see cref="MetalEncoderScope"/> already refuses to adopt one, so this path inherits that decision
        /// rather than making a second one: throwing from inside a frame that is already failing is the worse of
        /// the two.
        /// </summary>
        [Fact]
        public void ANilBlitEncoderEmitsNoCopyAndDoesNotThrow()
        {
            _calls.NilForKind = MetalEncoderKind.Blit;

            Record(ring: null, 0, Payload(16, seed: 9));

            Assert.Empty(_harness.Blit.Copies);
            Assert.Equal(MetalEncoderKind.None, _encoders.Open);
        }

        /// <summary>
        /// A DISPOSED BUFFER'S SHAPE RECORDS NOTHING, which is the record-time half of the ring's disposal guard.
        /// A disposed <c>MetalBuffer</c> answers a null ring AND a nil handle, so the write arrives here as this
        /// pair, and the only two outcomes worth having are a no-op and a refusal. What it must never be is the
        /// third: a <c>memcpy</c> through the <c>contents()</c> pointer the ring took at creation, which the
        /// driver has since taken back.
        /// </summary>
        [Fact]
        public void ADisposedBuffersNullRingAndNilHandleRecordNothing()
        {
            byte[] payload = Payload(64, seed: 10);

            MetalBufferUpload.Record(ring: null, Segment, IntPtr.Zero, DestinationSize, 0, payload, _encoders,
                _arena, _harness.Blit);

            Assert.Equal(0, _calls.EncoderBoundaries);
            Assert.Equal(MetalEncoderKind.None, _encoders.Open);
            Assert.Empty(_harness.Blit.Copies);
            Assert.Equal(0, _arena.BlocksCreated);
        }

        /// <summary>AND THE REFUSALS STILL RUN FIRST on that shape, because a write past the end of the buffer is
        /// the caller's mistake whether or not the buffer was disposed. Without this the nil-handle return would
        /// be a way to make a bad write look fine.</summary>
        [Fact]
        public void ANilHandleStillRefusesAWritePastTheEnd()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MetalBufferUpload.Record(
                ring: null, Segment, IntPtr.Zero, DestinationSize, DestinationSize - 8, Payload(16, seed: 11),
                _encoders, _arena, _harness.Blit));
        }

        void Record(MetalUniformRing? ring, uint offsetBytes, ReadOnlySpan<byte> data)
            => MetalBufferUpload.Record(ring, Segment, Destination, DestinationSize, offsetBytes, data, _encoders,
                _arena, _harness.Blit);

        static byte[] Payload(int length, byte seed)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = (byte)((seed * 17) + i + 1);
            return bytes;
        }
    }
}
