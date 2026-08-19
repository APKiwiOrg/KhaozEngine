using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>A pass-through <see cref="IGpuCommandList"/> that records every <c>UpdateBuffer</c> and every
    /// <c>ResolveTexture</c> a pass records, so a test can assert on the SHAPE of a frame (how many uploads, to
    /// which buffer, covering what extent, and which multisampled source lands in which single-sample destination)
    /// rather than on pixels. Everything else forwards untouched, so the frame renders exactly as it would have.
    /// <para>Give <see cref="Inner"/> to <c>IGpuDevice.Submit</c>: the device needs the real list, not this.</para>
    /// <para>
    /// THE RESOLVE HALF IS DEVICE-FREE ON PURPOSE. Wrapped around a <see cref="NullGpuCommandList"/> over a
    /// <see cref="FakeGpuDevice"/> it answers "which resolves did this pass ask for", which is the wiring half of
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/603">#603</see>: a resolve deleted or pointed at
    /// the wrong texture is a source-code fault that no GPU is needed to see, and catching it on the ordinary
    /// push-path suite is far cheaper than waiting for a leg with a device.
    /// </para></summary>
    internal sealed class RecordingGpuCommandList : IGpuCommandList
    {
        /// <summary>One recorded upload: which buffer, at what byte offset, how many bytes, and (only when
        /// <see cref="CapturePayloads"/> is on) the bytes themselves.</summary>
        internal readonly record struct Upload(IGpuBuffer Buffer, uint Offset, uint Bytes, byte[]? Data = null)
        {
            /// <summary>Whether this write covers the destination from offset 0 to its end. That is the only shape
            /// Veldrid's D3D11 backend sends down the cheap <c>UpdateSubresource</c> path for a uniform buffer;
            /// anything narrower is a staging round trip that Maps the immediate context and waits on the GPU.</summary>
            public bool IsWholeBuffer => Offset == 0 && Bytes == Buffer.SizeInBytes;
        }

        /// <summary>One recorded multisample resolve: which texture was averaged into which.</summary>
        internal readonly record struct Resolve(IGpuTexture Source, IGpuTexture Destination);

        readonly List<Upload> _uploads = new();
        readonly List<Resolve> _resolves = new();

        public RecordingGpuCommandList(IGpuCommandList inner) => Inner = inner;

        /// <summary>Keep a COPY of each upload's bytes in <see cref="Upload.Data"/>, so a test can assert what a
        /// packed CPU image actually holds and not only its extent. Off by default: a real frame's list carries
        /// megabytes of vertex data a shape assertion never reads, and copying it would make every
        /// <c>[GpuFact]</c> here pay for the one thing only the device-free mirror tests want.</summary>
        public bool CapturePayloads { get; set; }

        /// <summary>The wrapped list. Submit THIS to the device.</summary>
        public IGpuCommandList Inner { get; }

        /// <summary>Uploads recorded since the last <see cref="Clear"/>, in the order they were recorded.</summary>
        public IReadOnlyList<Upload> Uploads => _uploads;

        /// <summary>Resolves recorded since the last <see cref="Clear"/>, IN THE ORDER THEY WERE RECORDED. The
        /// order is part of what a test asserts: a resolve issued before the pass that writes its source publishes
        /// the previous frame, which is a defect the destination's contents show and a count never would.</summary>
        public IReadOnlyList<Resolve> Resolves => _resolves;

        /// <summary>Forget everything recorded so far (call between frames to assert on ONE frame).</summary>
        public void Clear()
        {
            _uploads.Clear();
            _resolves.Clear();
        }

        /// <summary>Every recorded upload whose destination is <paramref name="sizeInBytes"/> bytes.</summary>
        public List<Upload> ToBuffersOfSize(uint sizeInBytes)
        {
            var hits = new List<Upload>();
            foreach (Upload u in _uploads) if (u.Buffer.SizeInBytes == sizeInBytes) hits.Add(u);
            return hits;
        }

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
        {
            _uploads.Add(new Upload(b, offsetBytes, (uint)Unsafe.SizeOf<T>(),
                CapturePayloads ? MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in data)).ToArray() : null));
            Inner.UpdateBuffer(b, offsetBytes, in data);
        }

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
        {
            _uploads.Add(new Upload(b, offsetBytes, (uint)(data.Length * Unsafe.SizeOf<T>()),
                CapturePayloads ? MemoryMarshal.AsBytes(data).ToArray() : null));
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
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
        {
            _resolves.Add(new Resolve(src, dst));
            Inner.ResolveTexture(src, dst);
        }
        public void SetComputePipeline(IGpuComputePipeline p) => Inner.SetComputePipeline(p);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) => Inner.SetComputeResourceSet(slot, set);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => Inner.SetComputeResourceSet(slot, set, dynamicOffset);
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) => Inner.Dispatch(groupCountX, groupCountY, groupCountZ);
        public void Dispose() => Inner.Dispose();
    }
}
