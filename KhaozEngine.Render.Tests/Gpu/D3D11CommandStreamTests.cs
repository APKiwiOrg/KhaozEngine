using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The op stream itself: the size of a recorded command, how each seam call encodes into one, and the two
    /// lifetimes a recording owns (its resource references and its bulk payloads).
    /// <para>
    /// Every test here is DEVICE-FREE and runs on macOS and Linux as well as Windows, which is decision P1
    /// working as designed and section 2.1's point about the emitter seam: because
    /// <c>ID3D11Emitter</c> is written in engine-owned handle types, a counting emitter is a plain object and
    /// none of this needs a Direct3D device to be checked.
    /// </para>
    /// </summary>
    public sealed class D3D11CommandStreamTests
    {
        /// <summary>
        /// THE 32-BYTE LAYOUT, asserted so growth is a red test rather than drift. Section 5.1 sizes the whole
        /// stream against this number: roughly 1000 draws at roughly 10 ops each is 10k ops, 320 KB of a reused
        /// array, and that budget is the order-of-magnitude claim milestone M1 is meant to be able to falsify.
        /// A quietly wider op moves the budget without moving the sentence that justifies it.
        /// <para>
        /// <see cref="Unsafe.SizeOf{T}"/> is the measure that matters, because it is the managed footprint of one
        /// element of the array the stream actually allocates. <see cref="Marshal.SizeOf{T}()"/> is asserted
        /// alongside it for a different reason: it throws outright on a field that is not blittable, so the pair
        /// catches both a wider op and an op that grew a managed reference, and a managed reference is the one
        /// addition that would make truncating the stream stop being a single integer write.
        /// </para>
        /// </summary>
        [Fact]
        public void ARecordedOp_IsExactly32Bytes()
        {
            Assert.Equal(32, Unsafe.SizeOf<D3D11Op>());
            Assert.Equal(32, Marshal.SizeOf<D3D11Op>());
        }

        /// <summary>
        /// A zeroed op is <see cref="D3D11OpCode.None"/> and never a real command. Worth pinning because the op
        /// array is reused across recordings and only rewound, so an off-by-one in the count would read a stale
        /// or never-written slot, and the difference between that being loud and being silent is this one
        /// ordinal.
        /// </summary>
        [Fact]
        public void AZeroedOp_IsNotACommand()
        {
            var empty = default(D3D11Op);

            Assert.Equal(D3D11OpCode.None, empty.Code);
            Assert.Equal(D3D11ReferenceList.NoReference, new D3D11Op(D3D11OpCode.Draw).Reference);
        }

        /// <summary>
        /// Floats survive a payload word bit-exactly. The clear colour and the clear depth are the only
        /// non-integer arguments the seam carries, and a clear colour that came back a rounding step away from
        /// what was recorded would show up as a golden failure with no obvious cause.
        /// </summary>
        [Theory]
        [InlineData(0f)]
        [InlineData(1f)]
        [InlineData(0.1f)]
        [InlineData(-3.4028235e38f)]
        [InlineData(float.Epsilon)]
        public void AFloatPayloadWord_RoundTripsBitExactly(float value)
            => Assert.Equal(value, D3D11Op.Float(D3D11Op.Bits(value)));

        /// <summary>The one signed argument on the seam is <c>DrawIndexed</c>'s vertex offset, and it is signed
        /// on purpose.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        [InlineData(-1)]
        public void ASignedPayloadWord_RoundTrips(int value)
            => Assert.Equal(value, D3D11Op.Signed(D3D11Op.Signed(value)));

        /// <summary>A mip level and an array layer share one word, which is what makes the subresource copy (two
        /// resources plus six numbers) fit in the designed size.</summary>
        [Theory]
        [InlineData(0u, 0u)]
        [InlineData(11u, 2047u)]
        [InlineData(65535u, 65535u)]
        public void ASubresourceWord_RoundTrips(uint mip, uint layer)
        {
            uint packed = D3D11Op.PackSubresource(mip, layer);

            Assert.Equal(mip, D3D11Op.MipOf(packed));
            Assert.Equal(layer, D3D11Op.LayerOf(packed));
        }

        /// <summary>Past sixteen bits the pack THROWS rather than silently truncating. D3D11 caps a 2D array at
        /// 2048 layers, so a value this large is a caller defect, and a truncated one would copy a different
        /// subresource than the caller named.</summary>
        [Fact]
        public void ASubresourceWord_RefusesAValueItCannotHold()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => D3D11Op.PackSubresource(65536u, 0u));
            Assert.Throws<ArgumentOutOfRangeException>(() => D3D11Op.PackSubresource(0u, 65536u));
        }

        // ---- The reference list: the one place a recording holds a managed reference ----

        /// <summary>
        /// A resource argument becomes an index, and the index resolves back to the same instance. This is the
        /// mechanism section 5.1 specifies and the reason ops can stay pure value data.
        /// </summary>
        [Fact]
        public void AResourceArgument_BecomesAnIndexThatResolvesBack()
        {
            var references = new D3D11ReferenceList();
            var first = new FakeBuffer(64);
            var second = new FakeBuffer(128);

            int a = references.Add(first);
            int b = references.Add(second);

            Assert.NotEqual(a, b);
            Assert.Same(first, references.Get<IGpuBuffer>(a));
            Assert.Same(second, references.Get<IGpuBuffer>(b));
            Assert.Equal(2, references.Count);
        }

        /// <summary>
        /// CONSECUTIVE references to one instance collapse to a single entry. Decision R5 names thousands of
        /// offsets-only rebinds of the same set per frame as the hot path, so without this the list grows once
        /// per rebind for a whole frame, which is the unbounded shape R5 rejects for the bound-record dedup for
        /// exactly the same reason.
        /// </summary>
        [Fact]
        public void RepeatedReferencesToOneResource_CollapseToOneEntry()
        {
            var references = new D3D11ReferenceList();
            var set = new FakeResourceSet();

            int first = references.Add(set);
            for (int i = 0; i < 999; i++) Assert.Equal(first, references.Add(set));

            Assert.Equal(1, references.Count);
        }

        /// <summary>
        /// THE LIFETIME RULE, in the direction that can leak: a reset drops the references, so a recording holds
        /// its resources alive for the recording and no longer. Rewinding the count alone would have been cheaper
        /// and would have left last frame's resources reachable from an array nobody reads, which is a retention
        /// pool the design does not have.
        /// </summary>
        [Fact]
        public void ResettingARecording_DropsTheReferencesItHeld()
        {
            var references = new D3D11ReferenceList();
            references.Add(new FakeBuffer(64));
            references.Add(new FakeBuffer(64));

            references.Reset();

            Assert.Equal(0, references.Count);
            // Nothing is readable behind the reset count, which is what proves the slot was cleared rather than
            // merely skipped: a rewound-only list still answers here.
            Assert.Throws<InvalidOperationException>(() => references.Get<IGpuBuffer>(0));
        }

        /// <summary>The dedup does not survive a reset. A stale last-seen index would make the first reference of
        /// a new recording point into the previous one.</summary>
        [Fact]
        public void TheDedup_DoesNotSurviveAReset()
        {
            var references = new D3D11ReferenceList();
            var buffer = new FakeBuffer(64);
            references.Add(new FakeBuffer(64));
            references.Add(buffer);

            references.Reset();

            Assert.Equal(0, references.Add(buffer));
            Assert.Same(buffer, references.Get<IGpuBuffer>(0));
        }

        /// <summary>A mismatch between what the encoder stored and what the replay asks for is a defect in this
        /// package, so it says so rather than handing back a null the emitter would dereference.</summary>
        [Fact]
        public void AMistypedReference_FailsWithAMessageNamingTheDisagreement()
        {
            var references = new D3D11ReferenceList();
            int index = references.Add(new FakeBuffer(64));

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => references.Get<IGpuTexture>(index));

            Assert.Contains("disagree", ex.Message, StringComparison.Ordinal);
        }

        // ---- The payload arena ----

        /// <summary>Bulk payloads round-trip through the arena, which they must: a caller's span is dangling by
        /// the time a deferred list is submitted.</summary>
        [Fact]
        public void APayload_RoundTripsThroughTheArena()
        {
            var arena = new D3D11PayloadArena(8);
            byte[] first = { 1, 2, 3, 4 };
            byte[] second = { 9, 8, 7, 6, 5 };

            int a = arena.Append(first);
            int b = arena.Append(second);

            Assert.True(arena.Slice(a, first.Length).SequenceEqual(first));
            Assert.True(arena.Slice(b, second.Length).SequenceEqual(second));
            Assert.Equal(first.Length + second.Length, arena.Length);
        }

        /// <summary>Growth preserves what is already written, because ops recorded earlier in the frame carry
        /// offsets into the same array.</summary>
        [Fact]
        public void GrowingTheArena_KeepsWhatWasAlreadyRecorded()
        {
            var arena = new D3D11PayloadArena(4);
            byte[] early = { 1, 2, 3, 4 };
            int offset = arena.Append(early);

            arena.Append(new byte[1000]);

            Assert.True(arena.Capacity >= 1004);
            Assert.True(arena.Slice(offset, early.Length).SequenceEqual(early));
        }

        /// <summary>A reset rewinds and keeps the allocation, so a steady frame allocates nothing here.</summary>
        [Fact]
        public void ResettingTheArena_RewindsAndKeepsTheAllocation()
        {
            var arena = new D3D11PayloadArena(4);
            arena.Append(new byte[64]);
            int grown = arena.Capacity;

            arena.Reset();

            Assert.Equal(0, arena.Length);
            Assert.Equal(grown, arena.Capacity);
        }

        // ---- The stream ----

        /// <summary>
        /// <c>Begin</c> TRUNCATES TO ZERO and drops both lifetimes, which is section 5.1's sentence and the whole
        /// reason a nested or concurrent recording is structurally harmless: two recorders are two arrays, so
        /// there is no shared device state for one to wipe out from under the other.
        /// </summary>
        [Fact]
        public void BeginningARecording_TruncatesTheStreamToZero()
        {
            var stream = new D3D11CommandStream();
            var emitter = new D3D11StreamEmitter(stream);
            emitter.Begin();
            emitter.SetVertexBuffer(0, new FakeBuffer(64), 0);
            emitter.UpdateBuffer(new FakeBuffer(64), 0, new byte[16]);
            emitter.End();

            emitter.Begin();

            Assert.Equal(0, stream.Count);
            Assert.Equal(0, stream.ReferenceCount);
            Assert.Equal(0, stream.PayloadLength);
            Assert.False(stream.Sealed);
        }

        /// <summary>The array is reused across recordings rather than reallocated, which is what makes the 320 KB
        /// in section 5.1 a steady-state figure instead of a per-frame allocation.</summary>
        [Fact]
        public void TheStreamArray_IsReusedAcrossRecordings()
        {
            var stream = new D3D11CommandStream(capacity: 2);
            var emitter = new D3D11StreamEmitter(stream);

            emitter.Begin();
            for (int i = 0; i < 100; i++) emitter.Draw((uint)i, 1, 0, 0);
            emitter.End();
            int grown = stream.Capacity;

            emitter.Begin();
            emitter.Draw(3, 1, 0, 0);

            Assert.True(grown >= 100);
            Assert.Equal(grown, stream.Capacity);
            Assert.Equal(1, stream.Count);
        }

        /// <summary><c>End</c> seals, and a <c>Begin</c> unseals. Submit reads this, because replaying a list
        /// that was never ended replays a half-recorded frame rather than failing.</summary>
        [Fact]
        public void EndSealsTheRecording_AndBeginUnsealsIt()
        {
            var stream = new D3D11CommandStream();
            var emitter = new D3D11StreamEmitter(stream);

            emitter.Begin();
            Assert.False(stream.Sealed);
            emitter.End();
            Assert.True(stream.Sealed);
            emitter.Begin();
            Assert.False(stream.Sealed);
        }

        /// <summary>
        /// The op encoding, checked at the level a reader can verify by eye: one seam call is one op, carrying the
        /// opcode, the reference and the payload words the replay will read back. The full round trip through the
        /// replay is <see cref="D3D11RecordingDriverTests"/>, which is the check that actually binds the encoder
        /// and the decoder together. This one exists so a change to the ENCODING alone still fails something.
        /// </summary>
        [Fact]
        public void EachSeamCall_EncodesToOneOpCarryingItsArguments()
        {
            var stream = new D3D11CommandStream();
            var emitter = new D3D11StreamEmitter(stream);
            var buffer = new FakeBuffer(256);
            emitter.Begin();

            emitter.SetVertexBuffer(2, buffer, 48);
            emitter.DrawIndexed(6, 1, 12, -4, 0);
            emitter.ClearDepthStencil(0.5f);

            ReadOnlySpan<D3D11Op> ops = stream.Ops;
            Assert.Equal(3, ops.Length);

            Assert.Equal(D3D11OpCode.SetVertexBuffer, ops[0].Code);
            Assert.Same(buffer, stream.Reference<IGpuBuffer>(ops[0].Reference));
            Assert.Equal(2u, ops[0].Arg0);
            Assert.Equal(48u, ops[0].Arg1);

            Assert.Equal(D3D11OpCode.DrawIndexed, ops[1].Code);
            Assert.Equal(D3D11ReferenceList.NoReference, ops[1].Reference);
            Assert.Equal(6u, ops[1].Arg0);
            Assert.Equal(-4, D3D11Op.Signed(ops[1].Arg3));

            Assert.Equal(D3D11OpCode.ClearDepthStencil, ops[2].Code);
            Assert.Equal(0.5f, D3D11Op.Float(ops[2].Arg0));
        }

        /// <summary>
        /// <c>Begin</c> and <c>End</c> are NEVER stored as ops. They are raised around the replay instead
        /// (decision R3, one <c>ClearState</c> per replay), which is what keeps the recording side free of device
        /// contact and lets one recorded stream be replayed twice into two clean scopes.
        /// </summary>
        [Fact]
        public void TheScopeMarkers_AreNeverStoredAsOps()
        {
            var stream = new D3D11CommandStream();
            var emitter = new D3D11StreamEmitter(stream);

            emitter.Begin();
            emitter.Draw(3, 1, 0, 0);
            emitter.End();

            Assert.Equal(1, stream.Count);
            foreach (D3D11Op op in stream.Ops)
            {
                Assert.NotEqual(D3D11OpCode.Begin, op.Code);
                Assert.NotEqual(D3D11OpCode.End, op.Code);
            }
        }

        /// <summary>A command with two resources carries the second as an index in a payload word, since a
        /// reference index is just an integer, and both resolve back.</summary>
        [Fact]
        public void ATwoResourceCommand_CarriesItsSecondResourceAsAPayloadIndex()
        {
            var stream = new D3D11CommandStream();
            var emitter = new D3D11StreamEmitter(stream);
            var source = new FakeTexture(4, 4, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);
            var destination = new FakeTexture(4, 4, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);
            emitter.Begin();

            emitter.CopyTexture(source, destination);

            D3D11Op op = stream.Ops[0];
            Assert.Same(source, stream.Reference<IGpuTexture>(op.Reference));
            Assert.Same(destination, stream.Reference<IGpuTexture>(D3D11Op.Signed(op.Arg0)));
        }
    }
}
