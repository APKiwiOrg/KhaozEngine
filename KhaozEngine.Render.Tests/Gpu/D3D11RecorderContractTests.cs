using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The two contracts that keep the native Direct3D 11 backend's two recording drivers answering the same
    /// program the same way: the SHAPE an emitter has to have, and the guard that says which calls belong to a
    /// recording at all.
    /// <para>
    /// Both are here because a violation of either is silent and driver-specific, which is the worst shape a
    /// defect can take on this row. Milestone M1 A/Bs the deferred driver against the immediate one on one
    /// build, and an A/B is only a measurement while both drivers accept the same set of legal programs and
    /// render them the same way. A divergence found by M1 would be read as a cost difference between the
    /// recording models, which is exactly the wrong conclusion.
    /// </para>
    /// </summary>
    public sealed class D3D11RecorderContractTests
    {
        // ---- The emitter shape: one emitter state per DEVICE, never one per list ----

        /// <summary>
        /// EVERY EMITTER IS A READONLY STRUCT, so all its mutable state necessarily sits behind a class
        /// reference. <c>D3D11CommandRecorder</c> stores its emitter BY VALUE, one copy per list, so on the
        /// immediate driver N lists hold N copies of the struct over ONE device context. An emitter carrying
        /// inline state would therefore be per-list on one driver and per-device on the other.
        /// <para>
        /// That matters from row 6, where the real emitter gains R6's redundancy caches. Those caches describe
        /// what is bound on the CONTEXT rather than what one list recorded, so two of them over one context is
        /// the stale-cache defect the next test demonstrates, and R8's precise unbind-and-scrub on disposal is
        /// reached from the device and would find only one of the copies. A reflection check rather than a
        /// comment, because the constraint is invisible at the call site and the compiler cannot express it: C#
        /// has no <c>where T : readonly struct</c>.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryEmitterInTheBackend_KeepsItsMutableStateBehindAClassReference()
        {
            Type[] emitters = typeof(ID3D11Emitter).Assembly.GetTypes()
                .Where(t => typeof(ID3D11Emitter).IsAssignableFrom(t) && t != typeof(ID3D11Emitter))
                .ToArray();

            // A scan that finds nothing passes without checking anything, which is how this test would rot the
            // day the emitters move or are renamed.
            Assert.Contains(typeof(D3D11StreamEmitter), emitters);
            Assert.Contains(typeof(D3D11CountingEmitter), emitters);

            string[] hazards = emitters.Select(InlineMutableStateHazard).OfType<string>().ToArray();

            Assert.True(hazards.Length == 0, string.Join(Environment.NewLine, hazards));
        }

        /// <summary>The check above is only worth having if it rejects the shape it exists to reject, so it is
        /// pointed at an emitter written deliberately wrong.</summary>
        [Fact]
        public void TheEmitterShapeCheck_RejectsAnEmitterThatCarriesStateInline()
            => Assert.NotNull(InlineMutableStateHazard(typeof(PerListCacheEmitter)));

        /// <summary>
        /// THE FAILURE THE SHAPE RULE PREVENTS, made executable. Two lists share one device context. List A
        /// binds a pipeline, list B binds a different one, then A binds its own again. A's inline cache still
        /// says A's pipeline is bound, so A skips the rebind, and A's draw runs with B's pipeline. Nothing
        /// throws, nothing logs, and the frame is wrong on the immediate driver only, which is the driver M1
        /// A/Bs against.
        /// <para>
        /// With the shape the seam requires (a readonly struct over a device-owned state object) the third bind
        /// is issued, because the one cache saw B's bind and knows A's pipeline is no longer current.
        /// </para>
        /// </summary>
        [Fact]
        public void AnInlineCache_GoesStalePerList_AndTheDrawUsesAnotherListsPipeline()
        {
            var log = new D3D11EmitterCallLog();
            var first = new FakePipeline();
            var second = new FakePipeline();
            var deviceEmitter = new PerListCacheEmitter(log);

            using IGpuCommandList a = D3D11CommandDrivers.Create(D3D11RecordMode.Immediate, deviceEmitter);
            using IGpuCommandList b = D3D11CommandDrivers.Create(D3D11RecordMode.Immediate, deviceEmitter);

            a.Begin();
            b.Begin();
            a.SetPipeline(first);
            b.SetPipeline(second);
            a.SetPipeline(first);
            a.Draw(3);

            Assert.Equal(2, log.Count(D3D11OpCode.SetPipeline));
            Assert.Equal(
                new[] { "Begin()", "Begin()", "SetPipeline(r0)", "SetPipeline(r1)", "Draw(3,1,0,0)" },
                log.Trace);
        }

        /// <summary>
        /// The correct shape, from the other side: two lists created from ONE emitter value reach ONE emitter
        /// state, because the copy each list holds is a copy of a handle. This is what makes the device's single
        /// emitter, its redundancy caches and its scrub-on-disposal reachable from every list it created.
        /// </summary>
        [Fact]
        public void TwoImmediateLists_FromOneEmitter_ReachOneEmitterState()
        {
            var log = new D3D11EmitterCallLog();
            var deviceEmitter = new D3D11CountingEmitter(log);

            using IGpuCommandList a = D3D11CommandDrivers.Create(D3D11RecordMode.Immediate, deviceEmitter);
            using IGpuCommandList b = D3D11CommandDrivers.Create(D3D11RecordMode.Immediate, deviceEmitter);

            a.Begin();
            a.Draw(1);
            b.Begin();
            b.Draw(2);

            Assert.Equal(
                new[] { "Begin()", "Draw(1,1,0,0)", "Begin()", "Draw(2,1,0,0)" },
                log.Trace);
        }

        /// <summary>The reason an emitter may not carry inline state, expressed as a check. Null means the type
        /// is safe to copy per list, a string is the reason it is not, phrased for whoever hit it.</summary>
        static string? InlineMutableStateHazard(Type emitter)
        {
            if (!emitter.IsValueType)
                return emitter.Name + " implements ID3D11Emitter but is not a struct, so it cannot satisfy the "
                    + "seam's struct constraint and every call through it would be an interface dispatch.";

            bool isReadOnly = emitter.GetCustomAttributesData().Any(
                a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute");
            if (isReadOnly) return null;

            return emitter.Name + " is a MUTABLE struct. An emitter is copied into every command list it drives, "
                + "so inline state is per-list on the immediate driver and per-device on the deferred one: two "
                + "lists over one device context would hold two redundancy caches, one list would skip a rebind "
                + "another list invalidated, and disposal could scrub only one of them. Make it a readonly "
                + "struct and put the mutable state in a class the struct points at.";
        }

        // ---- The record-path guard: a command belongs to a recording, or it is refused ----

        /// <summary>
        /// THE PROGRAM FROM THE REVIEW, verbatim: Begin, Draw(1), End, Draw(2), Submit. Unguarded, both drivers
        /// accepted it and meant different things by it, because the deferred one appends to a sealed stream (so
        /// the stray draw replays INSIDE the recording) while the immediate one has already emitted it (so it
        /// lands AFTER the recording's End). Two frames from one program, neither driver complaining.
        /// </summary>
        [Fact]
        public void BeginDrawEndDraw_IsRefusedByBothDrivers_RatherThanRenderingTwoDifferentFrames()
        {
            var deferredLog = new D3D11EmitterCallLog();
            using (D3D11CommandRecorder<D3D11StreamEmitter> deferred = D3D11CommandDrivers.CreateDeferred())
            {
                deferred.Begin();
                deferred.Draw(1);
                deferred.End();

                Assert.Throws<InvalidOperationException>(() => deferred.Draw(2));

                var emitter = new D3D11CountingEmitter(deferredLog);
                D3D11CommandDrivers.Replay(deferred, ref emitter);
            }

            var immediateLog = new D3D11EmitterCallLog();
            using (IGpuCommandList immediate = D3D11CommandDrivers.Create(
                D3D11RecordMode.Immediate, new D3D11CountingEmitter(immediateLog)))
            {
                immediate.Begin();
                immediate.Draw(1);
                immediate.End();

                Assert.Throws<InvalidOperationException>(() => immediate.Draw(2));
            }

            Assert.Equal(new[] { "Begin()", "Draw(1,1,0,0)", "End()" }, deferredLog.Trace);
            Assert.Equal(deferredLog.Trace, immediateLog.Trace);
        }

        /// <summary>A sealed recording keeps exactly what was recorded into it. The refusal above would be worth
        /// little if the op had already been appended by the time it threw.</summary>
        [Fact]
        public void ARefusedCommand_LeavesTheSealedRecordingAsItWas()
        {
            using D3D11CommandRecorder<D3D11StreamEmitter> list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.Draw(1);
            list.End();
            int recorded = list.Emitter.Stream.Count;

            Assert.Throws<InvalidOperationException>(() => list.Draw(2));

            Assert.Equal(recorded, list.Emitter.Stream.Count);
            Assert.True(list.IsSealed, "A refused command cleared the seal that End set.");
        }

        /// <summary>
        /// EVERY command the seam carries is guarded, before a <c>Begin</c> and after an <c>End</c>, on both
        /// drivers. One guarded call site is worth nothing on its own here: the hazard is a seam member that
        /// forwards straight to the emitter, and the way that happens is somebody adding a member and copying
        /// the shape of a neighbour.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EverySeamCommand_IsRefusedOutsideARecording_OnBothDrivers(bool immediate)
        {
            var fixtures = new D3D11RecordingDriverTests.Fixtures();

            foreach ((string member, Action<IGpuCommandList> call) in EverySeamCommand(fixtures))
            {
                var log = new D3D11EmitterCallLog();
                using IGpuCommandList list = CreateList(immediate, log);

                InvalidOperationException before = Assert.Throws<InvalidOperationException>(() => call(list));
                Assert.Contains(member, before.Message, StringComparison.Ordinal);

                list.Begin();
                list.End();

                InvalidOperationException after = Assert.Throws<InvalidOperationException>(() => call(list));
                Assert.Contains(member, after.Message, StringComparison.Ordinal);
            }
        }

        /// <summary>The guard coverage above is a hand-written list, so this is what keeps it honest when the
        /// seam grows a member.</summary>
        [Fact]
        public void TheGuardCoverage_NamesEverySeamCommand()
        {
            string[] covered = EverySeamCommand(new D3D11RecordingDriverTests.Fixtures())
                .Select(c => c.Member)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            string[] declared = typeof(IGpuCommandList).GetMethods()
                .Select(m => m.Name)
                .Where(n => n is not ("Begin" or "End" or "Dispose"))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(declared, covered);
        }

        // ---- Disposal, on the same guard ----

        /// <summary>
        /// A DISPOSED LIST REFUSES EVERY COMMAND, and says it was disposed rather than that it was not
        /// recording. Disposal drops the recording's resource references, so a command afterwards is not a
        /// sequencing mistake inside a live object, it is use of an object that has already released what the
        /// command would have needed.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ADisposedList_RefusesEverySeamCommand_AsObjectDisposed(bool immediate)
        {
            var fixtures = new D3D11RecordingDriverTests.Fixtures();

            foreach ((string _, Action<IGpuCommandList> call) in EverySeamCommand(fixtures))
            {
                var log = new D3D11EmitterCallLog();
                IGpuCommandList list = CreateList(immediate, log);
                list.Begin();
                list.Dispose();

                Assert.Throws<ObjectDisposedException>(() => call(list));
            }
        }

        /// <summary><c>End</c> on a disposed list answers the same way. It used to report a sequencing error,
        /// which sent the reader looking for a missing <c>Begin</c> that was never the problem.</summary>
        [Fact]
        public void EndOnADisposedList_SaysDisposedRatherThanNotRecording()
        {
            IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.Dispose();

            Assert.Throws<ObjectDisposedException>(list.End);
        }

        /// <summary>And nothing reaches the recording after disposal, which is what makes the released reference
        /// list safe to have released.</summary>
        [Fact]
        public void ADisposedList_AppendsNothingMore()
        {
            D3D11CommandRecorder<D3D11StreamEmitter> list = D3D11CommandDrivers.CreateDeferred();
            D3D11CommandStream stream = list.Emitter.Stream;
            list.Begin();
            list.Draw(1);
            list.Dispose();

            Assert.Throws<ObjectDisposedException>(() => list.Draw(2));
            Assert.Equal(0, stream.Count);
        }

        // ---- Fixtures ----

        static IGpuCommandList CreateList(bool immediate, D3D11EmitterCallLog log)
            => immediate
                ? D3D11CommandDrivers.Create(D3D11RecordMode.Immediate, new D3D11CountingEmitter(log))
                : (IGpuCommandList)D3D11CommandDrivers.CreateDeferred();

        static readonly uint OneWord = 1u;

        /// <summary>One call per seam command, each labelled with the member it exercises so a failure names the
        /// unguarded one and so <see cref="TheGuardCoverage_NamesEverySeamCommand"/> can compare the list
        /// against the interface. Arguments are irrelevant here: every call is expected to throw before the
        /// emitter is reached.</summary>
        static IEnumerable<(string Member, Action<IGpuCommandList> Call)> EverySeamCommand(
            D3D11RecordingDriverTests.Fixtures f)
            => new (string, Action<IGpuCommandList>)[]
            {
                (nameof(IGpuCommandList.SetFramebuffer), l => l.SetFramebuffer(f.Framebuffer)),
                (nameof(IGpuCommandList.ClearColorTarget), l => l.ClearColorTarget(0, new Color(1f, 1f, 1f, 1f))),
                (nameof(IGpuCommandList.ClearDepthStencil), l => l.ClearDepthStencil(1f)),
                (nameof(IGpuCommandList.SetPipeline), l => l.SetPipeline(f.Pipeline)),
                (nameof(IGpuCommandList.SetGraphicsResourceSet), l => l.SetGraphicsResourceSet(0, f.Set)),
                (nameof(IGpuCommandList.SetGraphicsResourceSet), l => l.SetGraphicsResourceSet(0, f.Set, 256)),
                (nameof(IGpuCommandList.SetVertexBuffer), l => l.SetVertexBuffer(0, f.Vertices)),
                (nameof(IGpuCommandList.SetVertexBuffer), l => l.SetVertexBuffer(0, f.Vertices, 64)),
                (nameof(IGpuCommandList.SetIndexBuffer), l => l.SetIndexBuffer(f.Indices, GpuIndexFormat.UInt32)),
                (nameof(IGpuCommandList.SetScissorRect), l => l.SetScissorRect(0, 0, 0, 16, 16)),
                (nameof(IGpuCommandList.SetFullScissorRects), l => l.SetFullScissorRects()),
                (nameof(IGpuCommandList.Draw), l => l.Draw(3)),
                (nameof(IGpuCommandList.Draw), l => l.Draw(3, 1, 0, 0)),
                (nameof(IGpuCommandList.DrawIndexed), l => l.DrawIndexed(3, 1, 0, 0, 0)),
                (nameof(IGpuCommandList.UpdateBuffer), l => l.UpdateBuffer(f.Uniforms, 0, in OneWord)),
                (nameof(IGpuCommandList.UpdateBuffer), l => l.UpdateBuffer<byte>(f.Uniforms, 0, new byte[] { 1 })),
                (nameof(IGpuCommandList.CopyBuffer), l => l.CopyBuffer(f.Uniforms, 0, f.Staging, 0, 16)),
                (nameof(IGpuCommandList.CopyTexture), l => l.CopyTexture(f.Colour, f.Readback)),
                (nameof(IGpuCommandList.CopyTextureSubresource),
                    l => l.CopyTextureSubresource(f.Colour, 0, 0, f.Readback, 16, 16)),
                (nameof(IGpuCommandList.CopyTextureSubresource),
                    l => l.CopyTextureSubresource(f.Colour, 0, 0, f.Readback, 0, 0, 16, 16)),
                (nameof(IGpuCommandList.GenerateMipmaps), l => l.GenerateMipmaps(f.Colour)),
                (nameof(IGpuCommandList.ResolveTexture), l => l.ResolveTexture(f.Multisampled, f.Readback)),
                (nameof(IGpuCommandList.SetComputePipeline), l => l.SetComputePipeline(f.ComputePipeline)),
                (nameof(IGpuCommandList.SetComputeResourceSet), l => l.SetComputeResourceSet(0, f.Set)),
                (nameof(IGpuCommandList.SetComputeResourceSet), l => l.SetComputeResourceSet(0, f.Set, 256)),
                (nameof(IGpuCommandList.Dispatch), l => l.Dispatch(1, 1, 1)),
            };

        /// <summary>
        /// AN EMITTER WRITTEN DELIBERATELY WRONG: a mutable struct whose R6-style redundancy cache sits inline,
        /// which is the shape the seam forbids and the shape a real emitter is most tempted into. It exists so
        /// the rule is demonstrated rather than asserted, and it is the reason
        /// <see cref="EveryEmitterInTheBackend_KeepsItsMutableStateBehindAClassReference"/> scans the backend
        /// assembly rather than this one.
        /// </summary>
        internal struct PerListCacheEmitter : ID3D11Emitter
        {
            readonly D3D11CountingEmitter _inner;
            IGpuPipeline? _boundPipeline;

            internal PerListCacheEmitter(D3D11EmitterCallLog log)
            {
                _inner = new D3D11CountingEmitter(log);
                _boundPipeline = null;
            }

            /// <summary>The redundancy cache of decision R6, in the wrong place: this field lives in whichever
            /// COPY of the struct is being called, so it describes one list's view of a shared context.</summary>
            public void SetPipeline(IGpuPipeline pipeline)
            {
                if (ReferenceEquals(_boundPipeline, pipeline)) return;

                _boundPipeline = pipeline;
                _inner.SetPipeline(pipeline);
            }

            public void Begin() => _inner.Begin();
            public void End() => _inner.End();
            public void SetFramebuffer(IGpuFramebuffer framebuffer) => _inner.SetFramebuffer(framebuffer);
            public void ClearColorTarget(uint index, Color rgba) => _inner.ClearColorTarget(index, rgba);
            public void ClearDepthStencil(float depth) => _inner.ClearDepthStencil(depth);
            public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
                => _inner.SetGraphicsResourceSet(slot, set);
            public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
                => _inner.SetGraphicsResourceSet(slot, set, dynamicOffset);
            public void SetVertexBuffer(uint slot, IGpuBuffer buffer, uint offsetBytes)
                => _inner.SetVertexBuffer(slot, buffer, offsetBytes);
            public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format)
                => _inner.SetIndexBuffer(buffer, format);
            public void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
                => _inner.SetScissorRect(index, x, y, width, height);
            public void SetFullScissorRects() => _inner.SetFullScissorRects();
            public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
                => _inner.Draw(vertexCount, instanceCount, vertexStart, instanceStart);
            public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset,
                uint instanceStart)
                => _inner.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart);
            public void UpdateBuffer(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
                => _inner.UpdateBuffer(buffer, offsetBytes, data);
            public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes,
                uint sizeInBytes)
                => _inner.CopyBuffer(src, srcOffsetBytes, dst, dstOffsetBytes, sizeInBytes);
            public void CopyTexture(IGpuTexture src, IGpuTexture dst) => _inner.CopyTexture(src, dst);
            public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
                IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
                => _inner.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, dstMipLevel, dstArrayLayer,
                    width, height);
            public void GenerateMipmaps(IGpuTexture texture) => _inner.GenerateMipmaps(texture);
            public void ResolveTexture(IGpuTexture src, IGpuTexture dst) => _inner.ResolveTexture(src, dst);
            public void SetComputePipeline(IGpuComputePipeline pipeline) => _inner.SetComputePipeline(pipeline);
            public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
                => _inner.SetComputeResourceSet(slot, set);
            public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
                => _inner.SetComputeResourceSet(slot, set, dynamicOffset);
            public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
                => _inner.Dispatch(groupCountX, groupCountY, groupCountZ);
        }
    }
}
