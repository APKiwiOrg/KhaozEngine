using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <see cref="IGpuCommandList"/> for the native Direct3D 11 backend, and BOTH of its drivers at once. Every
    /// seam call is translated once, here, into the matching <see cref="ID3D11Emitter"/> call, and which driver
    /// this is depends entirely on which emitter the type argument names.
    /// <list type="bullet">
    /// <item><description><c>D3D11CommandRecorder&lt;D3D11StreamEmitter&gt;</c> is the DEFERRED driver of
    /// decision R1: seam calls become ops in a CPU command stream, zero native calls happen during record, and
    /// the whole stream is replayed inside <c>Submit</c>.</description></item>
    /// <item><description><c>D3D11CommandRecorder&lt;TRealEmitter&gt;</c> is the IMMEDIATE driver of decision
    /// R2, the M1 fallback: the same seam calls reach the emitter as they are made, with no stream in the
    /// picture at all.</description></item>
    /// </list>
    /// <para>
    /// THAT IS WHY THIS CLASS IS GENERIC. Section 5.3 requires the fallback driver to share every line above the
    /// emitter, and section 16 requires the op stream to be ONE DRIVER of the emitter rather than a mandatory
    /// layer beneath it, both for the M1 A/B and because phase 3's Vulkan and Metal backends have real deferred
    /// command buffers and would emit at record time. Expressing the split as a type argument makes the sharing
    /// structural: there is one implementation of the seam and it cannot drift between drivers, because there is
    /// nothing to drift from. <see cref="D3D11RecordMode"/> picks the instantiation from
    /// <c>KE_D3D11_RECORD</c>.
    /// </para>
    /// <para>
    /// THE BEGIN, END AND SUBMIT CONTRACT (sections 5.1 and 2.1, decision R3). <see cref="Begin"/> resets and
    /// touches no device state, which under the deferred driver means truncating the stream to zero with no
    /// native call, no lock and no device contact. N lists may therefore record concurrently and a nested
    /// <c>Begin</c> cannot corrupt another recording, because two recorders are two arrays.
    /// <see cref="End"/> seals. <c>Submit</c> takes the device's submit lock, replays, and releases, which
    /// <see cref="D3D11CommandDrivers.Submit{TEmitter}"/> does. Note that this is the NATIVE backend's contract:
    /// decision R4 leaves the PORTABLE seam contract at one open recording per device, since the Veldrid D3D11
    /// leg ships alongside indefinitely and rejects a second recorder.
    /// </para>
    /// <para>
    /// Per-call state validation is deliberately absent from the record path, matching the incumbent wrapper,
    /// which validates nothing and lets the backend do it. The state that IS checked is the state a wrong answer
    /// silently corrupts a frame over: a double <see cref="Begin"/>, an <see cref="End"/> with no recording open,
    /// a command recorded outside a Begin and End pair (see <see cref="RequireRecording"/>, which is the one
    /// place the two drivers would otherwise answer differently), a command on a disposed list, and submitting a
    /// list that was never ended.
    /// </para>
    /// </summary>
    internal sealed class D3D11CommandRecorder<TEmitter> : IGpuCommandList
        where TEmitter : struct, ID3D11Emitter
    {
        TEmitter _emitter;
        bool _recording;
        bool _disposed;

        internal D3D11CommandRecorder(TEmitter emitter) => _emitter = emitter;

        /// <summary>
        /// The emitter this list drives, by READONLY reference. An <see cref="ID3D11Emitter"/> implementation is
        /// a readonly struct whose mutable state lives behind a class reference, so there is nothing here to
        /// mutate and the reference exists only to avoid copying the struct out.
        /// <para>
        /// An earlier version of this comment said the reference let a MUTABLE emitter be updated in place. That
        /// was wrong in the direction that matters: this field is a per-list COPY, so on the immediate driver a
        /// mutable emitter was never one instance to update, and N lists over one device context would have held
        /// N redundancy caches. The seam states the constraint and a test enforces it.
        /// </para>
        /// </summary>
        internal ref readonly TEmitter Emitter => ref _emitter;

        /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
        internal bool IsRecording => _recording;

        /// <summary>True once <see cref="End"/> has sealed a recording and until the next <see cref="Begin"/>.
        /// Submit checks it, because replaying a list that was never ended replays a half-recorded frame rather
        /// than failing.</summary>
        internal bool IsSealed { get; private set; }

        public void Begin()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recording)
                throw new InvalidOperationException(
                    "Begin was called on a Direct3D 11 command list that is already recording. Begin truncates "
                    + "the recording to zero, so a second call would silently discard everything recorded since "
                    + "the first. Call End first.");

            _recording = true;
            IsSealed = false;
            _emitter.Begin();
        }

        public void End()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_recording)
                throw new InvalidOperationException(
                    "End was called on a Direct3D 11 command list that is not recording. Every recording opens "
                    + "with Begin, which is what resets the stream.");

            _recording = false;
            IsSealed = true;
            _emitter.End();
        }

        /// <summary>
        /// THE RECORD-PATH GUARD, and the one place the two drivers would otherwise answer the same program
        /// differently. A command recorded outside a Begin and End pair is meaningless, and each driver has its
        /// own way of accepting it silently: the deferred one appends to a stream that <see cref="End"/> already
        /// sealed, so the command replays INSIDE the recording, while the immediate one has already emitted it,
        /// so it lands AFTER the recording's own End. <c>Begin, Draw(1), End, Draw(2), Submit</c> therefore
        /// renders <c>Begin Draw(1) Draw(2) End</c> on one driver and <c>Begin Draw(1) End Draw(2)</c> on the
        /// other, from one program, with neither complaining. That breaks the premise milestone M1 rests on,
        /// which is that the two drivers are A/B'd over the same set of legal programs.
        /// <para>
        /// The disposed check rides here rather than in each caller, so a command on a disposed list is an
        /// <see cref="ObjectDisposedException"/> at the call that made it instead of an unrelated
        /// <see cref="InvalidOperationException"/> from a later <see cref="End"/>.
        /// </para>
        /// </summary>
        void RequireRecording([CallerMemberName] string member = "")
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recording) return;

            throw new InvalidOperationException(
                member + " was called on a Direct3D 11 command list that is not recording. Every command belongs "
                + "to a recording that Begin opened and End closed. Recording after End would extend a sealed "
                + "recording on the deferred driver and emit outside the list on the immediate one, so the same "
                + "program would render two different frames depending on KE_D3D11_RECORD.");
        }

        public void SetFramebuffer(IGpuFramebuffer fb)
        {
            RequireRecording();
            _emitter.SetFramebuffer(fb);
        }

        public void ClearColorTarget(uint index, Color rgba)
        {
            RequireRecording();
            _emitter.ClearColorTarget(index, rgba);
        }

        public void ClearDepthStencil(float depth)
        {
            RequireRecording();
            _emitter.ClearDepthStencil(depth);
        }

        public void SetPipeline(IGpuPipeline p)
        {
            RequireRecording();
            _emitter.SetPipeline(p);
        }

        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
        {
            RequireRecording();
            _emitter.SetGraphicsResourceSet(slot, set);
        }

        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
        {
            RequireRecording();
            _emitter.SetGraphicsResourceSet(slot, set, dynamicOffset);
        }

        /// <summary>The no-offset overload is the offset overload at zero, exactly as the incumbent forwards it,
        /// so there is one binding path rather than two that could diverge.</summary>
        public void SetVertexBuffer(uint slot, IGpuBuffer b)
        {
            RequireRecording();
            _emitter.SetVertexBuffer(slot, b, 0u);
        }

        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes)
        {
            RequireRecording();
            _emitter.SetVertexBuffer(slot, b, offsetBytes);
        }

        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt)
        {
            RequireRecording();
            _emitter.SetIndexBuffer(b, fmt);
        }

        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h)
        {
            RequireRecording();
            _emitter.SetScissorRect(index, x, y, w, h);
        }

        public void SetFullScissorRects()
        {
            RequireRecording();
            _emitter.SetFullScissorRects();
        }

        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            RequireRecording();
            _emitter.Draw(vertexCount, instanceCount, vertexStart, instanceStart);
        }

        /// <summary>One instance from vertex zero, exactly as the incumbent forwards it.</summary>
        public void Draw(uint vertexCount)
        {
            RequireRecording();
            _emitter.Draw(vertexCount, 1u, 0u, 0u);
        }

        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
        {
            RequireRecording();
            _emitter.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart);
        }

        /// <summary>One struct, seen as its bytes. The span is built over the <c>in</c> argument and lives only
        /// for this call, which is all the emitter contract asks for: the deferred driver copies it into the
        /// recording's arena before returning.</summary>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
        {
            RequireRecording();
            ReadOnlySpan<T> one = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in data), 1);
            _emitter.UpdateBuffer(b, offsetBytes, MemoryMarshal.AsBytes(one));
        }

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
        {
            RequireRecording();
            _emitter.UpdateBuffer(b, offsetBytes, MemoryMarshal.AsBytes(data));
        }

        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
        {
            RequireRecording();
            _emitter.CopyBuffer(src, srcOffsetBytes, dst, dstOffsetBytes, sizeInBytes);
        }

        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
        {
            RequireRecording();
            _emitter.CopyTexture(src, dst);
        }

        /// <summary>Mip zero and layer zero of the destination, exactly as the incumbent forwards it.</summary>
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint width, uint height)
        {
            RequireRecording();
            _emitter.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, 0u, 0u, width, height);
        }

        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
        {
            RequireRecording();
            _emitter.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, dstMipLevel, dstArrayLayer,
                width, height);
        }

        public void GenerateMipmaps(IGpuTexture texture)
        {
            RequireRecording();
            _emitter.GenerateMipmaps(texture);
        }

        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
        {
            RequireRecording();
            _emitter.ResolveTexture(src, dst);
        }

        public void SetComputePipeline(IGpuComputePipeline p)
        {
            RequireRecording();
            _emitter.SetComputePipeline(p);
        }

        public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
        {
            RequireRecording();
            _emitter.SetComputeResourceSet(slot, set);
        }

        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
        {
            RequireRecording();
            _emitter.SetComputeResourceSet(slot, set, dynamicOffset);
        }

        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            RequireRecording();
            _emitter.Dispatch(groupCountX, groupCountY, groupCountZ);
        }

        /// <summary>
        /// Drop the recording. Under the deferred driver that RELEASES the resource references the stream holds,
        /// which is the other half of section 5.1's lifetime rule: a reference list keeps its resources alive for
        /// the recording's lifetime, and the recording's lifetime ends here. Leaving it to the collector would
        /// have worked (the stream is reachable only through this list) and would have made "for the recording's
        /// lifetime" mean "until a collection happens to run", which is not a lifetime anyone can reason about.
        /// <para>
        /// The type test is how a generic recorder reaches a driver-specific detail without putting a release
        /// hook on <see cref="ID3D11Emitter"/> that a real emitter would have to implement as a no-op. It costs
        /// one box on a disposal, which is not a path anything measures.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _recording = false;
            IsSealed = false;
            if (_emitter is D3D11StreamEmitter stream) stream.Stream.Reset();
        }
    }
}
