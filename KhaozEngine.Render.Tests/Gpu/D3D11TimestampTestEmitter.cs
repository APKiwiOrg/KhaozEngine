using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gpu
{
    internal readonly struct D3D11TimestampTestEmitter : ID3D11Emitter
    {
        readonly D3D11NativeTraceEmitter _inner;
        readonly D3D11TimestampTestApi _api;
        internal D3D11TimestampTestEmitter(D3D11NativeCallLog log, D3D11TimestampTestApi api)
        {
            _inner = new D3D11NativeTraceEmitter(new D3D11DeviceState(), log);
            _api = api;
        }

        public void Begin() => _inner.Begin();
        public void End() => _inner.End();
        public void SetFramebuffer(IGpuFramebuffer framebuffer) => _inner.SetFramebuffer(framebuffer);
        public void ClearColorTarget(uint index, Color rgba) => _inner.ClearColorTarget(index, rgba);
        public void ClearDepthStencil(float depth) => _inner.ClearDepthStencil(depth);
        public void SetPipeline(IGpuPipeline pipeline) => _inner.SetPipeline(pipeline);
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
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
            => _inner.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart);

        public void UpdateBuffer(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
            => _inner.UpdateBuffer(buffer, offsetBytes, data);

        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
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
        public void BeginTimestamps(IGpuTimestampPool pool, uint count) { ((D3D11TimestampPool)pool).EmitBegin(); _inner.BeginTimestamps(pool, count); }
        public void WriteTimestamp(IGpuTimestampPool pool, uint index) { ((D3D11TimestampPool)pool).EmitWrite(index); _inner.WriteTimestamp(pool, index); }
        public void EndTimestamps(IGpuTimestampPool pool) { ((D3D11TimestampPool)pool).EmitEnd(); _inner.EndTimestamps(pool); }
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        { _api.Calls.Add($"Dispatch({groupCountX},{groupCountY},{groupCountZ})"); _inner.Dispatch(groupCountX, groupCountY, groupCountZ); }
    }
}
