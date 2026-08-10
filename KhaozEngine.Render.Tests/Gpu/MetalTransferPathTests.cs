using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE TRANSFER FAMILY DRIVEN THROUGH A REAL <see cref="MetalCommandList"/> WITH NO DEVICE UNDER IT. Work
    /// breakdown row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/580), over
    /// <c>MetalCommandList.Transfers.cs</c>.
    ///
    /// <para><b>WHAT IS HERE IS THE BUFFER COPY AND THE ARGUMENT REFUSALS, AND THAT IS A LIMIT OF THE FIXTURE
    /// RATHER THAN OF THE ROW.</b> A <see cref="MetalTexture"/> has a private constructor and one factory that
    /// takes an <c>MTLDevice</c>, so there is no way to stand one up off a device and no fake can substitute for
    /// it: the four texture members type-check their arguments to that concrete class. So every claim below the
    /// ownership check on <c>CopyTexture</c>, both <c>CopyTextureSubresource</c> overloads,
    /// <c>GenerateMipmaps</c> and <c>ResolveTexture</c> lives in the <c>[GpuFact]</c> companion instead, and this
    /// file asserts what it genuinely can: the whole of <c>CopyBuffer</c>, and the argument, ownership, recording
    /// and disposal refusals every member shares. <see cref="MetalRenderPassScheduleTests"/> records the same
    /// constraint for the pass schedule, for the same reason.</para>
    ///
    /// <para><b>THE ENCODER BOUNDARY IS THE CLAIM WORTH THE MOST HERE.</b> Every member of this family opens a
    /// DIFFERENT encoder kind from the one a draw uses, and ending the open render pass first is M-A5 enforced by
    /// <see cref="MetalEncoderScope"/> rather than by a line repeated in five places. That is a decision no golden
    /// can see: a copy that failed to end the pass would be a driver refusal on a device and nothing at all on a
    /// fake, so the boundary is asserted through <see cref="FakeMetalEncoderCalls"/> where it is visible.</para>
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Either a copy stopped ending the open pass (M-A5), or the alignment
    /// ruling of section 9.3 stopped being asymmetric (the offsets throw by name, the size is padded up), or a
    /// refusal started spending an encoder boundary before it refused, or the zero-byte no-op turned back into the
    /// Vulkan sibling's throw.</para>
    /// </summary>
    public sealed class MetalTransferPathTests : IDisposable
    {
        const uint SourceSize = 256;
        const uint DestinationSize = 128;

        // A NON-NIL DESCRIPTOR THAT IS NEVER DEREFERENCED, for the one place a test opens a render encoder
        // directly. The encoder scope refuses a nil one by name and the fake underneath refuses it too, so the
        // number only has to be non-zero.
        static readonly IntPtr Descriptor = new(0x7777);

        readonly MetalRingHarness _harness = new();
        readonly FakeMetalEncoderCalls _calls = new();
        readonly FakeMetalRenderCalls _render = new();
        readonly MetalCommandList _list;

        public MetalTransferPathTests()
            => _list = _harness.NewList(new object(), calls: _calls, render: _render);

        /// <inheritdoc/>
        public void Dispose()
        {
            _list.Dispose();
            _harness.Dispose();
        }

        // ---- CopyBuffer: the copy itself ---------------------------------------------------------------------

        /// <summary>
        /// THE HEADLINE: one aligned copy emits EXACTLY ONE entry, at the offsets it was given, with the SIZE
        /// PADDED UP to a multiple of four. The pad is the half of section 9.3's ruling that does not throw, and
        /// it is the same <c>(4 - size % 4) % 4</c> the incumbent already applies on its own aligned path.
        /// <para>
        /// THE PAD IS SAFE BY ARITHMETIC RATHER THAN BY A BOUND CHECK, which <see cref="MetalCopyAlignment"/>
        /// carries in full: every buffer is allocated at <see cref="MetalBufferPolicy.AllocationBytes"/>, so an
        /// aligned offset plus a rounded-up size is still inside what was allocated on both sides.
        /// </para>
        /// <para>
        /// WHAT A RED RUN MEANS: either the copy is moving a different number of bytes than macOS will accept, or
        /// it is naming the wrong window of one of the two buffers.
        /// </para>
        /// </summary>
        [Fact]
        public void AnAlignedCopyEmitsOneEntryWithTheSizePaddedUpToFour()
        {
            (MetalBuffer source, MetalBuffer destination) = Buffers();
            _list.Begin();

            _list.CopyBuffer(source, 8, destination, 16, 30);

            (IntPtr encoder, IntPtr from, ulong fromOffset, IntPtr to, ulong toOffset, ulong size) =
                Assert.Single(_harness.Blit.Copies);

            Assert.Equal(8ul, fromOffset);
            Assert.Equal(16ul, toOffset);
            Assert.Equal(32ul, size);
            Assert.Equal(MetalCopyAlignment.PaddedSize(30), size);

            // The copy went into the encoder the scope actually opened, and both handles reached the selector.
            // WHICH handle is which is not assertable here: the harness fabricates ONE MTLBuffer handle for every
            // buffer it builds, so source and destination are the same number. That pairing is a [GpuFact].
            Assert.Equal(_list.Encoders.Current, encoder);
            Assert.NotEqual(IntPtr.Zero, from);
            Assert.NotEqual(IntPtr.Zero, to);
        }

        /// <summary>
        /// A SIZE ALREADY ON A FOUR-BYTE BOUNDARY IS NOT PADDED, which is what says the round-up is a round-up
        /// rather than an unconditional bump. Without this row a pad that always added four would pass the one
        /// above.
        /// </summary>
        [Fact]
        public void AnAlreadyAlignedSizeIsCopiedUnchanged()
        {
            (MetalBuffer source, MetalBuffer destination) = Buffers();
            _list.Begin();

            _list.CopyBuffer(source, 0, destination, 0, 64);

            Assert.Equal(64ul, Assert.Single(_harness.Blit.Copies).Size);
        }

        /// <summary>
        /// M-A5 THROUGH THE ONE OWNER OF EVERY TRANSITION: a copy ENDS THE OPEN RENDER ENCODER before it opens its
        /// blit one. The copy does not do that itself, and no line in <c>MetalCommandList.Transfers.cs</c> says so.
        /// <see cref="MetalEncoderScope.EnsureBlitEncoder"/> does it for all five members at once, which is the
        /// decision row 12 recorded when it did NOT add an <c>EndPass</c> for these callers to call.
        /// <para>
        /// THE OPEN PASS IS STAGED THROUGH THE SCOPE RATHER THAN THROUGH A DRAW, because a real pass needs a
        /// framebuffer, a framebuffer needs textures and a texture needs a device. What is under test is the
        /// transition, and the scope is where the transition lives.
        /// </para>
        /// <para>
        /// WHAT A RED RUN MEANS: a blit encoded while a render encoder is open, which Metal refuses outright, and
        /// which nothing device-free would otherwise notice.
        /// </para>
        /// </summary>
        [Fact]
        public void ACopyEndsAnOpenRenderEncoderBeforeItOpensItsBlitOne()
        {
            (MetalBuffer source, MetalBuffer destination) = Buffers();
            _list.Begin();

            Assert.NotEqual(IntPtr.Zero, _list.Encoders.EnsureRenderEncoder(Descriptor));
            Assert.Equal(MetalEncoderKind.Render, _list.Encoders.Open);

            _list.CopyBuffer(source, 0, destination, 0, 32);

            Assert.Equal(MetalEncoderKind.Blit, _list.Encoders.Open);
            Assert.Equal(2, _calls.EncoderBegins);

            // The END of the render encoder came BEFORE the BEGIN of the blit one, which is the ordering the
            // count alone cannot say.
            int ended = IndexOf("end Render");
            int began = IndexOf("begin Blit");
            Assert.True(ended >= 0 && began > ended,
                "A native Metal buffer copy opened its blit encoder without ending the render encoder that was "
                + "already open. The log was:\n" + string.Join("\n", _calls.Log));

            _list.End();
            Assert.Equal(0, _calls.OutstandingEncoders);
        }

        /// <summary>
        /// A RUN OF COPIES INSIDE ONE ENCODER PAYS THE BOUNDARY ONCE, because the scope's helper is an Ensure
        /// rather than a Begin. Same property <see cref="MetalBufferUploadTests"/> asserts for the record-time
        /// upload path, and it is what makes the boundary count a statement about how many times the recorder
        /// switched KINDS rather than how many copies it made.
        /// </summary>
        [Fact]
        public void ARunOfCopiesSharesOneBlitEncoder()
        {
            (MetalBuffer source, MetalBuffer destination) = Buffers();
            _list.Begin();

            for (uint i = 0; i < 8; i++) _list.CopyBuffer(source, i * 4, destination, i * 4, 4);

            Assert.Equal(8, _harness.Blit.Copies.Count);
            Assert.Equal(1, _calls.EncoderBegins);
            Assert.Single(_harness.Blit.Copies.Select(copy => copy.Encoder).Distinct());
        }

        // ---- CopyBuffer: the refusals ------------------------------------------------------------------------

        /// <summary>
        /// AN UNALIGNED SOURCE OFFSET IS REFUSED AND THE MESSAGE SAYS SOURCE. Section 9.3's ruling is asymmetric
        /// on purpose: the size is padded and the offsets throw, because the incumbent's answer to an unaligned
        /// offset is an embedded compute shader driven by a dedicated compute pipeline, and shipping a second
        /// metallib for a case no shipped call site produces is the unreachable-code reproduction G1 declined
        /// once already.
        /// <para>
        /// NAMING THE SIDE IS THE POINT OF THE ROW. Both offsets go through one helper, so a refusal that named
        /// the wrong end would send a caller to inspect the buffer that was fine.
        /// </para>
        /// </summary>
        [Fact]
        public void AnUnalignedSourceOffsetIsRefusedAndNamesTheSource()
        {
            (MetalBuffer source, MetalBuffer destination) = Buffers();
            _list.Begin();

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => _list.CopyBuffer(source, 3, destination, 0, 16));

            Assert.Contains("source offset of 3", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("multiple of 4", thrown.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("destination offset", thrown.Message, StringComparison.Ordinal);

            AssertNothingWasSpent();
        }

        /// <summary>
        /// AND THE DESTINATION END, SEPARATELY, with an ALIGNED source so only one of the two can be the cause.
        /// A single row covering both offsets would pass on an implementation that checked one of them twice.
        /// </summary>
        [Fact]
        public void AnUnalignedDestinationOffsetIsRefusedAndNamesTheDestination()
        {
            (MetalBuffer source, MetalBuffer destination) = Buffers();
            _list.Begin();

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => _list.CopyBuffer(source, 4, destination, 6, 16));

            Assert.Contains("destination offset of 6", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("multiple of 4", thrown.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("source offset", thrown.Message, StringComparison.Ordinal);

            AssertNothingWasSpent();
        }

        /// <summary>
        /// A WINDOW THAT RUNS PAST THE END IS REFUSED ON EITHER SIDE, before any alignment question is asked. The
        /// incumbent copies into <c>contents()</c> with no bound check at all, so the same call there overwrites
        /// whatever the driver placed after the allocation.
        /// </summary>
        [Fact]
        public void AWindowPastTheEndIsRefusedOnEitherSide()
        {
            (MetalBuffer source, MetalBuffer destination) = Buffers();
            _list.Begin();

            // The SOURCE is 256 bytes, so a 64-byte read from 224 runs off it.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _list.CopyBuffer(source, SourceSize - 32, destination, 0, 64));

            // The DESTINATION is 128, so a 64-byte write from 96 runs off it while the source is comfortable.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => _list.CopyBuffer(source, 0, destination, DestinationSize - 32, 64));

            AssertNothingWasSpent();
        }

        /// <summary>
        /// A ZERO-BYTE COPY RECORDS NOTHING AND DOES NOT THROW, which is a DELIBERATE divergence from the Vulkan
        /// sibling. The reason is written at the branch in <c>MetalCommandList.Transfers.cs</c>: "There a region
        /// of size 0 is a documented VUID violation the driver refuses, so a refusal is the only honest answer.
        /// Metal's copy takes a length and a length of zero is a no-op, so refusing would be this backend
        /// inventing a rule, and the seam's own callers legitimately compute a length that can come out zero."
        /// <para>
        /// AND IT SPENDS NO BOUNDARY EITHER, which is the second half of "records nothing": opening a blit encoder
        /// for a copy that is never emitted would end an open render pass and cost a full graphics-state
        /// re-activation for nothing at all.
        /// </para>
        /// </summary>
        [Fact]
        public void AZeroByteCopyRecordsNothingAndDoesNotThrow()
        {
            (MetalBuffer source, MetalBuffer destination) = Buffers();
            _list.Begin();

            _list.CopyBuffer(source, 0, destination, 0, 0);

            AssertNothingWasSpent();
        }

        /// <summary>
        /// A NIL BLIT ENCODER EMITS NO COPY AND DOES NOT THROW. Metal answers nil when the command buffer is in a
        /// state it will not encode into, and the scope already refuses to adopt one, so this path inherits that
        /// decision rather than making a second one: throwing from inside a frame that is already failing is the
        /// worse of the two.
        /// </summary>
        [Fact]
        public void ANilBlitEncoderRecordsNothing()
        {
            (MetalBuffer source, MetalBuffer destination) = Buffers();
            _list.Begin();
            _calls.NilForKind = MetalEncoderKind.Blit;

            _list.CopyBuffer(source, 0, destination, 0, 16);

            Assert.Empty(_harness.Blit.Copies);
            Assert.Equal(MetalEncoderKind.None, _list.Encoders.Open);
        }

        /// <summary>
        /// A COPY ON A LIST THAT IS NOT RECORDING IS REFUSED BY THE RECORDING GUARD, with its own message rather
        /// than a null-reference from somewhere further down. This is the one member of the family whose recording
        /// guard is reachable device-free, because it is the only one whose arguments can be stood up without a
        /// device.
        /// </summary>
        [Fact]
        public void ACopyOnAListThatIsNotRecordingIsRefused()
        {
            (MetalBuffer source, MetalBuffer destination) = Buffers();

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => _list.CopyBuffer(source, 0, destination, 0, 16));

            Assert.Contains("Copying between buffers", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("Call Begin", thrown.Message, StringComparison.Ordinal);

            AssertNothingWasSpent();
        }

        /// <summary>A buffer another backend created is refused by name, on either side. The cast every device
        /// entry point makes asks two questions and this is the first of them.</summary>
        [Fact]
        public void ABufferFromAnotherBackendIsRefusedOnEitherSide()
        {
            (MetalBuffer source, MetalBuffer destination) = Buffers();
            var foreign = new FakeBuffer(SourceSize);
            _list.Begin();

            Assert.Contains("not created by the native Metal backend",
                Assert.Throws<ArgumentException>(() => _list.CopyBuffer(foreign, 0, destination, 0, 16)).Message,
                StringComparison.Ordinal);

            Assert.Contains("not created by the native Metal backend",
                Assert.Throws<ArgumentException>(() => _list.CopyBuffer(source, 0, foreign, 0, 16)).Message,
                StringComparison.Ordinal);

            AssertNothingWasSpent();
        }

        // ---- The refusals every member of the family shares --------------------------------------------------

        /// <summary>
        /// EVERY MEMBER REFUSES NULL BEFORE ANYTHING ELSE, and both overloads of
        /// <c>CopyTextureSubresource</c> are walked rather than one standing in for the other. A null argument is
        /// the shape an entry point taking several seam resources makes trivially reachable, and the refusal has
        /// to carry the CALLER's parameter name rather than a helper's word.
        /// </summary>
        [Fact]
        public void EveryTransferMemberRefusesNull()
        {
            _list.Begin();

            foreach (Action member in EveryMemberWith(null!, null!)) Assert.Throws<ArgumentNullException>(member);

            AssertNothingWasSpent();
        }

        /// <summary>
        /// AND EVERY MEMBER REFUSES A RESOURCE ANOTHER BACKEND CREATED, which is as far into the four texture
        /// members as a device-free fixture can reach: the check is a type test against
        /// <see cref="MetalTexture"/>, and that class has a private constructor and one factory that takes an
        /// <c>MTLDevice</c>.
        /// <para>
        /// WHAT A RED RUN MEANS: a resource created on one backend is being used on another's device, which on a
        /// machine with more than one Metal device is an illegal copy and on Apple silicon, where the whole
        /// process shares one <c>MTLDevice</c>, SUCCEEDS and leaves the two devices' teardowns disagreeing about
        /// who releases it.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryTransferMemberRefusesAResourceFromAnotherBackend()
        {
            var texture = new FakeTexture(8, 8, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);
            _list.Begin();

            foreach (Action member in EveryMemberWith(texture, texture))
            {
                ArgumentException thrown = Assert.Throws<ArgumentException>(member);
                Assert.Contains("not created by the native Metal backend", thrown.Message,
                    StringComparison.Ordinal);
            }

            AssertNothingWasSpent();
        }

        /// <summary>
        /// AND EVERY MEMBER ON A DISPOSED LIST THROWS <see cref="ObjectDisposedException"/> ahead of every other
        /// guard, including the argument ones. A disposed list has released its command buffer, so an encoder
        /// opened on it would be a message send to a released Objective-C object rather than a driver refusal.
        /// </summary>
        [Fact]
        public void EveryTransferMemberOnADisposedListThrowsObjectDisposed()
        {
            var texture = new FakeTexture(8, 8, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);
            _list.Begin();
            _list.Dispose();

            foreach (Action member in EveryMemberWith(texture, texture))
                Assert.Throws<ObjectDisposedException>(member);

            Assert.Throws<ObjectDisposedException>(() => _list.CopyBuffer(null!, 0, null!, 0, 16));
        }

        // ---- The fixture -------------------------------------------------------------------------------------

        // A SOURCE AND A DESTINATION OF DIFFERENT SIZES, so a window refusal can be aimed at one side at a time.
        // Neither is ring-backed: a uniform buffer would route a write through the ring instead, which is
        // MetalBufferUploadTests' subject rather than this file's.
        (MetalBuffer Source, MetalBuffer Destination) Buffers()
            => (_harness.NewBuffer(SourceSize, GpuBufferUsage.StructuredBufferReadWrite),
                _harness.NewBuffer(DestinationSize, GpuBufferUsage.Staging));

        // THE FOUR TEXTURE MEMBERS, both CopyTextureSubresource overloads included, so a refusal that regressed on
        // exactly one of them cannot hide behind the other four.
        Action[] EveryMemberWith(IGpuTexture source, IGpuTexture destination) =>
        [
            () => _list.CopyTexture(source, destination),
            () => _list.CopyTextureSubresource(source, 0, 0, destination, 8, 8),
            () => _list.CopyTextureSubresource(source, 0, 0, destination, 0, 0, 8, 8),
            () => _list.GenerateMipmaps(source),
            () => _list.ResolveTexture(source, destination),
        ];

        // WHERE A LINE FIRST APPEARS IN THE BOUNDARY LOG, or -1. The log is prefix-matched rather than compared
        // whole, because the fake writes the fabricated handle into every line and those numbers are not the
        // claim.
        int IndexOf(string prefix)
        {
            for (int i = 0; i < _calls.Log.Count; i++)
            {
                if (_calls.Log[i].StartsWith(prefix, StringComparison.Ordinal)) return i;
            }

            return -1;
        }

        // A REFUSAL IS NOT HALF A RECORDING: nothing encoded, and no encoder boundary spent either. The second
        // half matters as much as the first, because opening a blit encoder ends an open render pass and discards
        // the pipeline, every argument-table entry, the viewport, the scissor and every vertex stream (M-R4), so a
        // guard that refused AFTER opening one would cost a full re-activation for a copy that never happened.
        void AssertNothingWasSpent()
        {
            Assert.Empty(_harness.Blit.Copies);
            Assert.Empty(_harness.Blit.TextureCopies);
            Assert.Empty(_harness.Blit.Readbacks);
            Assert.Empty(_harness.Blit.Uploads);
            Assert.Empty(_harness.Blit.MipChains);
            Assert.Empty(_render.Resolves);
            Assert.Equal(0, _calls.EncoderBoundaries);
            Assert.Equal(MetalEncoderKind.None, _list.Encoders.Open);
        }
    }
}
