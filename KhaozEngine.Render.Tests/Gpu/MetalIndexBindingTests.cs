using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE BOUND INDEX BUFFER AND ITS ONE PIECE OF ARITHMETIC, device-free. Work-breakdown row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580).
    ///
    /// <para><b>THE WHOLE TYPE IS A DECISION, WHICH IS WHY EVERY ROW HERE IS A PLAIN <c>[Fact]</c>.</b>
    /// <see cref="MetalIndexBinding"/> makes no native call at all: Metal takes the index buffer, its byte offset
    /// and its element width as ARGUMENTS to <c>-drawIndexedPrimitives:</c>, so what this type produces is three
    /// numbers a draw hands over. Nothing about producing them needs an <c>MTLDevice</c>, and everything about
    /// getting them wrong is invisible on a device: a wrong offset draws a DIFFERENT mesh out of a shared index
    /// buffer, with no validation error and no crash.</para>
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Either the offset arithmetic moved (a wrong picture with nothing
    /// reported, and the overflow row below is the case no shipped mesh would ever reach), or the two refusal
    /// states collapsed into one message (a caller who forgot <c>SetIndexBuffer</c> being told their buffer was
    /// disposed, or the reverse), or the reset stopped clearing, which would let one recording's index buffer
    /// serve the next one's draws.</para>
    ///
    /// <para><b>THE ENCODER BOUNDARY IS DELIBERATELY ABSENT FROM THIS FILE.</b> This is the one cache in the
    /// backend an encoder boundary does not invalidate, because an index buffer never reaches an argument table,
    /// so there is no M-R4 row to write here. <see cref="MetalDrawPathTests"/> carries the boundary behaviour of
    /// everything that does.</para>
    /// </summary>
    public sealed class MetalIndexBindingTests
    {
        /// <summary>
        /// THE ARITHMETIC ON BOTH WIDTHS: <c>indexStart</c> is an ELEMENT index on the seam and a BYTE offset in
        /// the call, so the whole of the conversion is the element width times the start. A red row here is the
        /// draw reading from the wrong place in a shared index buffer, which renders a different mesh and reports
        /// nothing.
        /// <para>
        /// A <c>[Fact]</c> OVER A CASE TABLE RATHER THAN A <c>[Theory]</c>, because <c>MTLIndexType</c> is
        /// internal to the backend and an xUnit test method has to be public, so an inline datum of that type
        /// would not compile.
        /// </para>
        /// </summary>
        [Fact]
        public void TheOffsetIsTheElementWidthTimesTheStart()
        {
            (MTLIndexType Type, uint IndexStart, ulong Expected)[] cases =
            [
                (MTLIndexType.UInt16, 0u, 0ul),
                (MTLIndexType.UInt16, 1u, 2ul),
                (MTLIndexType.UInt16, 1024u, 2048ul),
                (MTLIndexType.UInt32, 0u, 0ul),
                (MTLIndexType.UInt32, 1u, 4ul),
                (MTLIndexType.UInt32, 1024u, 4096ul),
            ];

            foreach ((MTLIndexType type, uint indexStart, ulong expected) in cases)
            {
                MetalIndexBinding binding = default;
                binding.Record(new IntPtr(0xB0), type);

                Assert.Equal((nuint)expected, binding.OffsetFor(indexStart));
            }
        }

        /// <summary>
        /// THE CLAIM THE TYPE'S OWN REMARK MAKES, WHICH IS THE ONE A NARROWER MULTIPLY WOULD SILENTLY BREAK. A
        /// 32-bit index buffer starting past 2^30 elements overflows a <see cref="uint"/> product on the way to an
        /// <c>NSUInteger</c> argument that has room for it, and an overflowed offset points somewhere INSIDE the
        /// buffer rather than past the end of it, so the draw succeeds and reads the wrong indices.
        ///
        /// <para><b>NO SHIPPED MESH IS ANYWHERE NEAR THIS, WHICH IS EXACTLY WHY IT EARNS A ROW.</b> A defect no
        /// content reaches is a defect no golden bakes and no playtest finds, so the only place it can be caught
        /// is here. 2^30 + 1 elements times four bytes is 4294967300, which is four past what a 32-bit product
        /// can hold, so a narrowed multiply answers 4 and this row answers the whole number.</para>
        /// </summary>
        [Fact]
        public void AThirtyTwoBitStartPastTwoToTheThirtyWidensBeforeItMultiplies()
        {
            MetalIndexBinding binding = default;
            binding.Record(new IntPtr(0xB0), MTLIndexType.UInt32);

            const uint IndexStart = (1u << 30) + 1u;

            // Through a local rather than a constant, because a constant conversion to nuint is refused outright
            // on the grounds that nuint is 32 bits wide on a 32-bit process. This suite is x64 (the natives it
            // loads ship x64 only), so the wide answer is the one that has to come back.
            ulong widened = 4294967300ul;
            Assert.Equal((nuint)widened, binding.OffsetFor(IndexStart));

            // The negative control, spelled out rather than implied: this is precisely what a uint product would
            // have answered, so the row cannot pass by accident on a build where the widening was removed.
            Assert.NotEqual((nuint)unchecked(4u * IndexStart), binding.OffsetFor(IndexStart));
        }

        /// <summary>A live binding owes no refusal, which is the positive control the two refusal rows below are
        /// only meaningful against.</summary>
        [Fact]
        public void ALiveBindingOwesNoRefusal()
        {
            MetalIndexBinding binding = default;
            binding.Record(new IntPtr(0xB0), MTLIndexType.UInt16);

            Assert.True(binding.IsBound);
            Assert.True(binding.IsDrawable);
            Assert.Null(binding.DrawRefusal());
        }

        /// <summary>
        /// THE TWO REFUSAL STATES ARE DIFFERENT MISTAKES AND EACH NAMES ITS OWN. Nothing bound is a recording that
        /// called <c>DrawIndexed</c> without <c>SetIndexBuffer</c>. A nil handle is a buffer that WAS bound and
        /// has been disposed since, which <c>MetalBuffer.Handle</c> answers nil for deliberately.
        ///
        /// <para><b>THE ASSERTIONS ARE DELIBERATELY NOT A SHARED SUBSTRING.</b> Both messages would happily
        /// contain "index buffer", so a test written that way passes on a backend that answers one message for
        /// both states, and a caller who forgot the bind is then told their buffer was disposed. Each arm here
        /// asserts the phrase only ITS case can produce, and asserts the other case's phrase is absent.</para>
        /// </summary>
        [Fact]
        public void TheTwoRefusalStatesCarryTheirOwnMessages()
        {
            MetalIndexBinding nothingBound = default;

            string? unbound = nothingBound.DrawRefusal();
            Assert.NotNull(unbound);
            Assert.Contains("with no index buffer bound", unbound, StringComparison.Ordinal);
            Assert.Contains("Call SetIndexBuffer first", unbound, StringComparison.Ordinal);
            Assert.DoesNotContain("disposed", unbound, StringComparison.Ordinal);

            // A buffer that WAS bound and has since been let go of, which is the pair MetalBuffer degrades to.
            MetalIndexBinding disposed = default;
            disposed.Record(IntPtr.Zero, MTLIndexType.UInt32);

            string? released = disposed.DrawRefusal();
            Assert.NotNull(released);
            Assert.Contains("has been disposed", released, StringComparison.Ordinal);
            Assert.Contains("Bind a live buffer", released, StringComparison.Ordinal);
            Assert.DoesNotContain("Call SetIndexBuffer first", released, StringComparison.Ordinal);

            // AND THE STATE BEHIND EACH MESSAGE, so the two are told apart by what the record holds rather than
            // only by what it says: a disposed binding is still BOUND and is no longer DRAWABLE.
            Assert.False(nothingBound.IsBound);
            Assert.True(disposed.IsBound);
            Assert.False(disposed.IsDrawable);
        }

        /// <summary>
        /// <c>Reset</c> RETURNS IT TO THE NOTHING-BOUND STATE, which matters because a <c>Begin</c> is the only
        /// thing that clears this record. Everything else a draw needs dies at an encoder boundary, so a reset
        /// that left the buffer behind would let one recording's index buffer serve the draws of the next one,
        /// against a command buffer it was never bound on.
        /// </summary>
        [Fact]
        public void ResetReturnsItToTheNothingBoundState()
        {
            MetalIndexBinding binding = default;
            binding.Record(new IntPtr(0xB0), MTLIndexType.UInt32);

            binding.Reset();

            Assert.False(binding.IsBound);
            Assert.False(binding.IsDrawable);
            Assert.Equal(IntPtr.Zero, binding.Buffer);

            string? refusal = binding.DrawRefusal();
            Assert.NotNull(refusal);
            Assert.Contains("with no index buffer bound", refusal, StringComparison.Ordinal);
            Assert.Equal("no index buffer", binding.Describe());
        }

        /// <summary>The seam's two index formats map to Metal's two, and the mapping is total, so there is no
        /// unmappable arm to refuse and no default to fall through.</summary>
        [Fact]
        public void TheSeamsIndexFormatMapsToMetals()
        {
            Assert.Equal(MTLIndexType.UInt16, MetalIndexBinding.ToIndexType(GpuIndexFormat.UInt16));
            Assert.Equal(MTLIndexType.UInt32, MetalIndexBinding.ToIndexType(GpuIndexFormat.UInt32));
        }

        /// <summary>The element widths, which are what the offset arithmetic above multiplies by and what
        /// <see cref="MetalIndexBinding.Describe"/> renders in bits.</summary>
        [Fact]
        public void EachIndexTypeCarriesItsElementWidth()
        {
            Assert.Equal(2u, MetalIndexBinding.ElementBytes(MTLIndexType.UInt16));
            Assert.Equal(4u, MetalIndexBinding.ElementBytes(MTLIndexType.UInt32));
        }

        /// <summary>
        /// The description a refusal quotes, on both widths and on nothing bound. It exists so a message can say
        /// what was bound without the message holding the buffer, and a red row here is a diagnostic that names
        /// the wrong width while everything it describes is correct.
        /// </summary>
        [Fact]
        public void DescribeNamesTheWidthOrSaysThereIsNone()
        {
            MetalIndexBinding sixteen = default;
            sixteen.Record(new IntPtr(0xB0), MTLIndexType.UInt16);
            Assert.Equal("a 16-bit index buffer", sixteen.Describe());

            MetalIndexBinding thirtyTwo = default;
            thirtyTwo.Record(new IntPtr(0xB0), MTLIndexType.UInt32);
            Assert.Equal("a 32-bit index buffer", thirtyTwo.Describe());

            MetalIndexBinding none = default;
            Assert.Equal("no index buffer", none.Describe());
        }

        /// <summary>A record overwrites rather than accumulating, so the LAST bind of a recording is the one an
        /// indexed draw reads. Two binds of different widths is the shape a pass that draws a 16-bit mesh and
        /// then a 32-bit one has.</summary>
        [Fact]
        public void ASecondRecordReplacesTheFirst()
        {
            MetalIndexBinding binding = default;
            binding.Record(new IntPtr(0xB0), MTLIndexType.UInt16);
            binding.Record(new IntPtr(0xC0), MTLIndexType.UInt32);

            Assert.Equal(new IntPtr(0xC0), binding.Buffer);
            Assert.Equal(MTLIndexType.UInt32, binding.IndexType);
            Assert.Equal((nuint)40, binding.OffsetFor(10));
        }
    }
}
