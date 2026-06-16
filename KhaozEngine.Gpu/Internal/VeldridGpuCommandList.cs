using System;
using System.Numerics;
using Veldrid;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>Wraps a Veldrid <see cref="CommandList"/>; maps the engine command calls 1:1.</summary>
    internal sealed class VeldridGpuCommandList : IGpuCommandList
    {
        internal CommandList CommandList { get; }
        public VeldridGpuCommandList(CommandList cl) => CommandList = cl;

        public void Begin() => CommandList.Begin();
        public void End() => CommandList.End();

        public void SetFramebuffer(IGpuFramebuffer fb)
            => CommandList.SetFramebuffer(((VeldridGpuFramebuffer)fb).Framebuffer);

        public void ClearColorTarget(uint index, Vector4 rgba)
            => CommandList.ClearColorTarget(index, new RgbaFloat(rgba.X, rgba.Y, rgba.Z, rgba.W));

        public void ClearDepthStencil(float depth) => CommandList.ClearDepthStencil(depth);

        public void SetPipeline(IGpuPipeline p) => CommandList.SetPipeline(((VeldridGpuPipeline)p).Pipeline);

        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
            => CommandList.SetGraphicsResourceSet(slot, ((VeldridGpuResourceSet)set).Set);

        public void SetVertexBuffer(uint slot, IGpuBuffer b)
            => CommandList.SetVertexBuffer(slot, ((VeldridGpuBuffer)b).Buffer);

        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt)
            => CommandList.SetIndexBuffer(((VeldridGpuBuffer)b).Buffer, VeldridMap.ToVeldrid(fmt));

        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h)
            => CommandList.SetScissorRect(index, x, y, w, h);

        public void SetFullScissorRects() => CommandList.SetFullScissorRects();

        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
            => CommandList.Draw(vertexCount, instanceCount, vertexStart, instanceStart);

        public void Draw(uint vertexCount) => CommandList.Draw(vertexCount);

        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
            => CommandList.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart);

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => CommandList.UpdateBuffer(((VeldridGpuBuffer)b).Buffer, offsetBytes, data);

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => CommandList.UpdateBuffer(((VeldridGpuBuffer)b).Buffer, offsetBytes, data);

        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
            => CommandList.CopyTexture(((VeldridGpuTexture)src).Texture, ((VeldridGpuTexture)dst).Texture);

        public void Dispose() => CommandList.Dispose();
    }
}
