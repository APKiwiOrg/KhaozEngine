using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>A pass-through <see cref="IGpuCommandList"/> that records every <c>UpdateBuffer</c> a pass records,
    /// so a test can assert on the SHAPE of a frame's uploads (how many, to which buffer, covering what extent)
    /// rather than on pixels. Everything else forwards untouched, so the frame renders exactly as it would have.
    /// <para>Give <see cref="Inner"/> to <c>IGpuDevice.Submit</c>: the device needs the real list, not this.</para></summary>
    internal sealed class RecordingGpuCommandList : IGpuCommandList
    {
        /// <summary>One recorded upload: which buffer, at what byte offset, how many bytes.</summary>
        internal readonly record struct Upload(IGpuBuffer Buffer, uint Offset, uint Bytes)
        {
            /// <summary>Whether this write covers the destination from offset 0 to its end. That is the only shape
            /// Veldrid's D3D11 backend sends down the cheap <c>UpdateSubresource</c> path for a uniform buffer;
            /// anything narrower is a staging round trip that Maps the immediate context and waits on the GPU.</summary>
            public bool IsWholeBuffer => Offset == 0 && Bytes == Buffer.SizeInBytes;
        }

        readonly List<Upload> _uploads = new();

        public RecordingGpuCommandList(IGpuCommandList inner) => Inner = inner;

        /// <summary>The wrapped list. Submit THIS to the device.</summary>
        public IGpuCommandList Inner { get; }

        /// <summary>Uploads recorded since the last <see cref="Clear"/>, in the order they were recorded.</summary>
        public IReadOnlyList<Upload> Uploads => _uploads;

        /// <summary>Forget everything recorded so far (call between frames to assert on ONE frame).</summary>
        public void Clear() => _uploads.Clear();

        /// <summary>Every recorded upload whose destination is <paramref name="sizeInBytes"/> bytes.</summary>
        public List<Upload> ToBuffersOfSize(uint sizeInBytes)
        {
            var hits = new List<Upload>();
            foreach (Upload u in _uploads) if (u.Buffer.SizeInBytes == sizeInBytes) hits.Add(u);
            return hits;
        }

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
        {
            _uploads.Add(new Upload(b, offsetBytes, (uint)Unsafe.SizeOf<T>()));
            Inner.UpdateBuffer(b, offsetBytes, in data);
        }

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
        {
            _uploads.Add(new Upload(b, offsetBytes, (uint)(data.Length * Unsafe.SizeOf<T>())));
            Inner.UpdateBuffer(b, offsetBytes, data);
        }

        public void Begin() => Inner.Begin();
        public void End() => Inner.End();
        public void SetFramebuffer(IGpuFramebuffer fb) => Inner.SetFramebuffer(fb);
        public void ClearColorTarget(uint index, Color rgba) => Inner.ClearColorTarget(index, rgba);
        public void ClearDepthStencil(float depth) => Inner.ClearDepthStencil(depth);
        public void SetPipeline(IGpuPipeline p) => Inner.SetPipeline(p);
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) => Inner.SetGraphicsResourceSet(slot, set);
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => Inner.SetGraphicsResourceSet(slot, set, dynamicOffset);
        public void SetVertexBuffer(uint slot, IGpuBuffer b) => Inner.SetVertexBuffer(slot, b);
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) => Inner.SetVertexBuffer(slot, b, offsetBytes);
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) => Inner.SetIndexBuffer(b, fmt);
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) => Inner.SetScissorRect(index, x, y, w, h);
        public void SetFullScissorRects() => Inner.SetFullScissorRects();
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
            => Inner.Draw(vertexCount, instanceCount, vertexStart, instanceStart);
        public void Draw(uint vertexCount) => Inner.Draw(vertexCount);
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
            => Inner.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart);
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
            => Inner.CopyBuffer(src, srcOffsetBytes, dst, dstOffsetBytes, sizeInBytes);
        public void CopyTexture(IGpuTexture src, IGpuTexture dst) => Inner.CopyTexture(src, dst);
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height)
            => Inner.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, width, height);
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
            => Inner.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, dstMipLevel, dstArrayLayer, width, height);
        public void GenerateMipmaps(IGpuTexture texture) => Inner.GenerateMipmaps(texture);
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst) => Inner.ResolveTexture(src, dst);
        public void SetComputePipeline(IGpuComputePipeline p) => Inner.SetComputePipeline(p);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) => Inner.SetComputeResourceSet(slot, set);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => Inner.SetComputeResourceSet(slot, set, dynamicOffset);
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) => Inner.Dispatch(groupCountX, groupCountY, groupCountZ);
        public void Dispose() => Inner.Dispose();
    }
}
