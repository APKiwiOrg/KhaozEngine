using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A pass-through <see cref="IGpuCommandList"/> that COUNTS every command a pass records, by kind, and
    /// forwards each one untouched. The sibling of <see cref="RecordingGpuCommandList"/>: that one records the
    /// shape of uniform uploads (which buffer, what extent), this one records how MANY operations of each kind a
    /// frame issues.
    /// <para>
    /// Why the count matters at all. The cost the engine keeps regressing on is per-command CPU work during
    /// recording, and on a Direct3D11 driver that cannot build command lists the runtime emulates them by
    /// recording every call into a token stream and replaying it later, so the per-call tax is paid on every
    /// single one. That makes the NUMBER of commands a frame records a real, load-bearing property, and one that
    /// no golden image and no timing test can see. Two regressions of exactly this shape shipped and were caught
    /// only by a field report weeks later.
    /// </para>
    /// <para>
    /// Wrap this around <see cref="NullGpuCommandList"/> to count with no GPU at all, or around a real list to
    /// count while genuinely rendering. Give <see cref="Inner"/> to <c>IGpuDevice.Submit</c>: the device needs
    /// the real list, not this.
    /// </para>
    /// </summary>
    internal sealed class CommandTallyGpuCommandList : IGpuCommandList
    {
        public CommandTallyGpuCommandList(IGpuCommandList inner) => Inner = inner;

        /// <summary>The wrapped list. Submit THIS to the device.</summary>
        public IGpuCommandList Inner { get; }

        /// <summary>Commands counted since the last <see cref="Clear"/>.</summary>
        public GpuCommandTally Tally { get; } = new();

        /// <summary>Forget everything counted so far (call between frames to assert on ONE frame).</summary>
        public void Clear() => Tally.Clear();

        void Count(GpuCommandKind kind) => Tally.Add(kind);

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
        {
            Count(GpuCommandKind.UpdateBuffer);
            Inner.UpdateBuffer(b, offsetBytes, in data);
        }

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
        {
            Count(GpuCommandKind.UpdateBuffer);
            Inner.UpdateBuffer(b, offsetBytes, data);
        }

        public void SetPipeline(IGpuPipeline p) { Count(GpuCommandKind.SetPipeline); Inner.SetPipeline(p); }

        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
        {
            Count(GpuCommandKind.SetGraphicsResourceSet);
            Inner.SetGraphicsResourceSet(slot, set);
        }

        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
        {
            Count(GpuCommandKind.SetGraphicsResourceSet);
            Inner.SetGraphicsResourceSet(slot, set, dynamicOffset);
        }

        public void SetVertexBuffer(uint slot, IGpuBuffer b)
        {
            Count(GpuCommandKind.SetVertexBuffer);
            Inner.SetVertexBuffer(slot, b);
        }

        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes)
        {
            Count(GpuCommandKind.SetVertexBuffer);
            Inner.SetVertexBuffer(slot, b, offsetBytes);
        }

        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt)
        {
            Count(GpuCommandKind.SetIndexBuffer);
            Inner.SetIndexBuffer(b, fmt);
        }

        public void SetFramebuffer(IGpuFramebuffer fb)
        {
            Count(GpuCommandKind.SetFramebuffer);
            Inner.SetFramebuffer(fb);
        }

        public void ClearColorTarget(uint index, Color rgba)
        {
            Count(GpuCommandKind.ClearColorTarget);
            Inner.ClearColorTarget(index, rgba);
        }

        public void ClearDepthStencil(float depth)
        {
            Count(GpuCommandKind.ClearDepthStencil);
            Inner.ClearDepthStencil(depth);
        }

        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            Count(GpuCommandKind.Draw);
            Inner.Draw(vertexCount, instanceCount, vertexStart, instanceStart);
        }

        public void Draw(uint vertexCount) { Count(GpuCommandKind.Draw); Inner.Draw(vertexCount); }

        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
        {
            Count(GpuCommandKind.DrawIndexed);
            Inner.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart);
        }

        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
        {
            Count(GpuCommandKind.CopyTexture);
            Inner.CopyTexture(src, dst);
        }

        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
        {
            Count(GpuCommandKind.ResolveTexture);
            Inner.ResolveTexture(src, dst);
        }

        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            Count(GpuCommandKind.Dispatch);
            Inner.Dispatch(groupCountX, groupCountY, groupCountZ);
        }

        // Uncounted pass-throughs: recording bookkeeping and the copies no pass issues per draw. They forward
        // untouched so the frame behaves exactly as it would have.
        public void Begin() => Inner.Begin();
        public void End() => Inner.End();
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) => Inner.SetScissorRect(index, x, y, w, h);
        public void SetFullScissorRects() => Inner.SetFullScissorRects();
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
            => Inner.CopyBuffer(src, srcOffsetBytes, dst, dstOffsetBytes, sizeInBytes);
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height)
            => Inner.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, width, height);
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
            => Inner.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, dstMipLevel, dstArrayLayer, width, height);
        public void GenerateMipmaps(IGpuTexture texture) => Inner.GenerateMipmaps(texture);
        public void SetComputePipeline(IGpuComputePipeline p) => Inner.SetComputePipeline(p);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) => Inner.SetComputeResourceSet(slot, set);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => Inner.SetComputeResourceSet(slot, set, dynamicOffset);
        public void Dispose() => Inner.Dispose();
    }
}
