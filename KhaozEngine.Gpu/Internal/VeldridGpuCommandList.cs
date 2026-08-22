using System;
using KhaozEngine.Primitives;
using Veldrid;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>Wraps a Veldrid <see cref="CommandList"/>; maps the engine command calls 1:1.</summary>
    internal sealed class VeldridGpuCommandList : IGpuCommandList
    {
        internal CommandList CommandList { get; }
        readonly DeviceLiveness _liveness;
        readonly bool _owns;
        public VeldridGpuCommandList(DeviceLiveness liveness, CommandList cl, bool owns = true)
        {
            _liveness = liveness; CommandList = cl; _owns = owns;
        }

        public void Begin() => CommandList.Begin();
        public void End() => CommandList.End();

        public void SetFramebuffer(IGpuFramebuffer fb)
            => CommandList.SetFramebuffer(((VeldridGpuFramebuffer)fb).Framebuffer);

        public void ClearColorTarget(uint index, Color rgba)
            => CommandList.ClearColorTarget(index, new RgbaFloat(rgba.R, rgba.G, rgba.B, rgba.A));

        public void ClearDepthStencil(float depth) => CommandList.ClearDepthStencil(depth);

        public void SetPipeline(IGpuPipeline p) => CommandList.SetPipeline(((VeldridGpuPipeline)p).Pipeline);

        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
            => CommandList.SetGraphicsResourceSet(slot, ((VeldridGpuResourceSet)set).Set);

        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => CommandList.SetGraphicsResourceSet(slot, ((VeldridGpuResourceSet)set).Set, 1u, ref dynamicOffset);

        public void SetVertexBuffer(uint slot, IGpuBuffer b)
            => CommandList.SetVertexBuffer(slot, ((VeldridGpuBuffer)b).Buffer);

        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes)
            => CommandList.SetVertexBuffer(slot, ((VeldridGpuBuffer)b).Buffer, offsetBytes);

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

        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
            => CommandList.CopyBuffer(((VeldridGpuBuffer)src).Buffer, srcOffsetBytes,
                ((VeldridGpuBuffer)dst).Buffer, dstOffsetBytes, sizeInBytes);

        /// <summary>
        /// A WHOLE-RESOURCE COPY, EXCEPT WHERE A SIDE CARRIES A PHANTOM SLICE (#666). Veldrid's own
        /// <c>CopyTexture(src, dst)</c> names every subresource on both sides and refuses a shape mismatch
        /// outright, so an emulated one-layer array (two slices on the GPU, one on the seam) could not be copied
        /// into the one-slice staging texture <c>GpuReadback.ToRgba</c> allocates: it threw here and succeeded on
        /// the three native backends. When either side pads, this walks the LOGICAL subresources instead, which
        /// is the same set of texels the natives copy and leaves the phantom out of it. Where neither pads, which
        /// is every texture in the engine bar a one-layer array, the call is Veldrid's own, unchanged.
        /// </summary>
        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
        {
            var s = (VeldridGpuTexture)src;
            var d = (VeldridGpuTexture)dst;
            if (!s.HasPhantomLayer && !d.HasPhantomLayer)
            {
                CommandList.CopyTexture(s.Texture, d.Texture);
                return;
            }

            for (uint layer = 0; layer < s.ArrayLayers; layer++)
                for (uint mip = 0; mip < s.Texture.MipLevels; mip++)
                    CommandList.CopyTexture(
                        s.Texture, 0, 0, 0, mip, layer,
                        d.Texture, 0, 0, 0, mip, layer,
                        Math.Max(1u, s.Texture.Width >> (int)mip),
                        Math.Max(1u, s.Texture.Height >> (int)mip), 1, 1);
        }

        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height)
            => CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, 0, 0, width, height);

        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
            => CommandList.CopyTexture(
                ((VeldridGpuTexture)src).Texture, 0, 0, 0, srcMipLevel, srcArrayLayer,
                ((VeldridGpuTexture)dst).Texture, 0, 0, 0, dstMipLevel, dstArrayLayer,
                width, height, 1, 1);

        public void GenerateMipmaps(IGpuTexture texture)
            => CommandList.GenerateMipmaps(((VeldridGpuTexture)texture).Texture);

        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
            => CommandList.ResolveTexture(((VeldridGpuTexture)src).Texture, ((VeldridGpuTexture)dst).Texture);

        public void SetComputePipeline(IGpuComputePipeline p)
            => CommandList.SetPipeline(((VeldridGpuComputePipeline)p).Pipeline);

        public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
            => CommandList.SetComputeResourceSet(slot, ((VeldridGpuResourceSet)set).Set);

        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => CommandList.SetComputeResourceSet(slot, ((VeldridGpuResourceSet)set).Set, 1u, ref dynamicOffset);

        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
            => CommandList.Dispatch(groupCountX, groupCountY, groupCountZ);

        public void Dispose() { if (_owns && _liveness.IsAlive) CommandList.Dispose(); }
    }
}
