using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE OP ENCODER, written as an emitter. Every <see cref="ID3D11Emitter"/> call turns into one
    /// <see cref="D3D11Op"/> appended to a <see cref="D3D11CommandStream"/> instead of a native call, which is
    /// decision R1's deferred driver: zero native calls during record, everything inside <c>Submit</c>.
    /// <para>
    /// Being an emitter rather than a layer under one is the point (section 16). The recorder above it is the
    /// same class for both drivers, and the only difference between them is which emitter it was handed. That is
    /// what makes the immediate driver of decision R2 share every line above the emitter by construction rather
    /// than by discipline, and what keeps a phase 3 Vulkan or Metal emitter free to emit at record time into a
    /// real command buffer with no stream anywhere.
    /// </para>
    /// <para>
    /// A readonly struct holding one class reference. The stream is where all the mutation happens, so the
    /// emitter itself never needs to be written back, and a copy of it drives the same recording.
    /// </para>
    /// </summary>
    internal readonly struct D3D11StreamEmitter : ID3D11Emitter
    {
        readonly D3D11CommandStream _stream;

        internal D3D11StreamEmitter(D3D11CommandStream stream) => _stream = stream;

        /// <summary>The recording this emitter appends to. Submit replays it.</summary>
        internal D3D11CommandStream Stream => _stream;

        /// <summary>Truncate the recording to zero. No native call, no lock, no device contact (section 5.1).
        /// Deliberately does NOT record an op: decision R3 puts the single <c>ClearState</c> at the head of each
        /// REPLAY, and <see cref="D3D11StreamReplay"/> raises it there.</summary>
        public void Begin() => _stream.Reset();

        /// <summary>Seal the recording.</summary>
        public void End() => _stream.Seal();

        public void SetFramebuffer(IGpuFramebuffer framebuffer)
            => _stream.Append(new D3D11Op(D3D11OpCode.SetFramebuffer, _stream.AddReference(framebuffer)));

        public void ClearColorTarget(uint index, Color rgba)
            => _stream.Append(new D3D11Op(D3D11OpCode.ClearColorTarget, a0: index,
                a1: D3D11Op.Bits(rgba.R), a2: D3D11Op.Bits(rgba.G), a3: D3D11Op.Bits(rgba.B), a4: D3D11Op.Bits(rgba.A)));

        public void ClearDepthStencil(float depth)
            => _stream.Append(new D3D11Op(D3D11OpCode.ClearDepthStencil, a0: D3D11Op.Bits(depth)));

        public void SetPipeline(IGpuPipeline pipeline)
            => _stream.Append(new D3D11Op(D3D11OpCode.SetPipeline, _stream.AddReference(pipeline)));

        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
            => _stream.Append(new D3D11Op(D3D11OpCode.SetGraphicsResourceSet, _stream.AddReference(set), a0: slot));

        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => _stream.Append(new D3D11Op(D3D11OpCode.SetGraphicsResourceSetDynamic, _stream.AddReference(set),
                a0: slot, a1: dynamicOffset));

        public void SetVertexBuffer(uint slot, IGpuBuffer buffer, uint offsetBytes)
            => _stream.Append(new D3D11Op(D3D11OpCode.SetVertexBuffer, _stream.AddReference(buffer),
                a0: slot, a1: offsetBytes));

        public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format)
            => _stream.Append(new D3D11Op(D3D11OpCode.SetIndexBuffer, _stream.AddReference(buffer),
                a0: (uint)format));

        public void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
            => _stream.Append(new D3D11Op(D3D11OpCode.SetScissorRect,
                a0: index, a1: x, a2: y, a3: width, a4: height));

        public void SetFullScissorRects()
            => _stream.Append(new D3D11Op(D3D11OpCode.SetFullScissorRects));

        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
            => _stream.Append(new D3D11Op(D3D11OpCode.Draw,
                a0: vertexCount, a1: instanceCount, a2: vertexStart, a3: instanceStart));

        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
            => _stream.Append(new D3D11Op(D3D11OpCode.DrawIndexed,
                a0: indexCount, a1: instanceCount, a2: indexStart, a3: D3D11Op.Signed(vertexOffset), a4: instanceStart));

        /// <summary>The bytes are copied into the recording's arena, because the caller's span is dangling by the
        /// time the list is submitted. The op carries the arena offset and length.</summary>
        public void UpdateBuffer(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            int payload = _stream.AddPayload(data);
            _stream.Append(new D3D11Op(D3D11OpCode.UpdateBuffer, _stream.AddReference(buffer),
                a0: offsetBytes, a1: (uint)payload, a2: (uint)data.Length));
        }

        /// <summary>Two resources, so the destination rides in a payload word. A reference index is just an
        /// integer, and the op's dedicated <see cref="D3D11Op.Reference"/> field carries the primary one.</summary>
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
        {
            int source = _stream.AddReference(src);
            int destination = _stream.AddReference(dst);
            _stream.Append(new D3D11Op(D3D11OpCode.CopyBuffer, source,
                a0: D3D11Op.Signed(destination), a1: srcOffsetBytes, a2: dstOffsetBytes, a3: sizeInBytes));
        }

        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
        {
            int source = _stream.AddReference(src);
            int destination = _stream.AddReference(dst);
            _stream.Append(new D3D11Op(D3D11OpCode.CopyTexture, source, a0: D3D11Op.Signed(destination)));
        }

        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
        {
            int source = _stream.AddReference(src);
            int destination = _stream.AddReference(dst);
            _stream.Append(new D3D11Op(D3D11OpCode.CopyTextureSubresource, source,
                a0: D3D11Op.Signed(destination),
                a1: D3D11Op.PackSubresource(srcMipLevel, srcArrayLayer),
                a2: D3D11Op.PackSubresource(dstMipLevel, dstArrayLayer),
                a3: width, a4: height));
        }

        public void GenerateMipmaps(IGpuTexture texture)
            => _stream.Append(new D3D11Op(D3D11OpCode.GenerateMipmaps, _stream.AddReference(texture)));

        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
        {
            int source = _stream.AddReference(src);
            int destination = _stream.AddReference(dst);
            _stream.Append(new D3D11Op(D3D11OpCode.ResolveTexture, source, a0: D3D11Op.Signed(destination)));
        }

        public void SetComputePipeline(IGpuComputePipeline pipeline)
            => _stream.Append(new D3D11Op(D3D11OpCode.SetComputePipeline, _stream.AddReference(pipeline)));

        public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
            => _stream.Append(new D3D11Op(D3D11OpCode.SetComputeResourceSet, _stream.AddReference(set), a0: slot));

        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => _stream.Append(new D3D11Op(D3D11OpCode.SetComputeResourceSetDynamic, _stream.AddReference(set),
                a0: slot, a1: dynamicOffset));

        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
            => _stream.Append(new D3D11Op(D3D11OpCode.Dispatch, a0: groupCountX, a1: groupCountY, a2: groupCountZ));
    }
}
