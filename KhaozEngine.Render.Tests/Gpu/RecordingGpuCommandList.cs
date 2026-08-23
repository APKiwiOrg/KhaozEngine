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
    /// <para>The READ half (which uniform windows each draw bound) is opt-in and lives in
    /// <c>RecordingGpuCommandList.Reads.cs</c>: set <see cref="UniformWindowsOfSet"/> before recording and the
    /// draws stamp <see cref="Reads"/>, which is what turns a write timeline into an answer about what any draw
    /// could actually have observed.</para>
    /// <para>
    /// THE RESOLVE HALF IS DEVICE-FREE ON PURPOSE. Wrapped around a <see cref="NullGpuCommandList"/> over a
    /// <see cref="FakeGpuDevice"/> it answers "which resolves did this pass ask for", which is the wiring half of
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/603">#603</see>: a resolve deleted or pointed at
    /// the wrong texture is a source-code fault that no GPU is needed to see, and catching it on the ordinary
    /// push-path suite is far cheaper than waiting for a leg with a device.
    /// </para></summary>
    internal sealed partial class RecordingGpuCommandList : IGpuCommandList
    {
        /// <summary>One recorded upload: which buffer, at what byte offset, how many bytes, (only when
        /// <see cref="CapturePayloads"/> is on) the bytes themselves, and how many draws or dispatches had already
        /// been recorded when it went in. That last number is what turns a pair of uploads into an ORDERING fact:
        /// two writes to one range with no draw between them are a redundant write, and two with a draw between
        /// them are the ring-collapse hazard <see cref="UniformRewriteAudit"/> looks for (#483).</summary>
        internal readonly record struct Upload(IGpuBuffer Buffer, uint Offset, uint Bytes, byte[]? Data = null,
            int DrawsBefore = 0)
        {
            /// <summary>Whether this write covers the destination from offset 0 to its end. That is the only shape
            /// Veldrid's D3D11 backend sent down the cheap <c>UpdateSubresource</c> path for a uniform buffer;
            /// anything narrower is a staging round trip that Maps the immediate context and waits on the GPU.</summary>
            public bool IsWholeBuffer => Offset == 0 && Bytes == Buffer.SizeInBytes;
        }

        /// <summary>One recorded multisample resolve: which texture was averaged into which.</summary>
        internal readonly record struct Resolve(IGpuTexture Source, IGpuTexture Destination);

        readonly List<Upload> _uploads = new();
        readonly List<Resolve> _resolves = new();
        int _draws;

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
            _draws = 0;
            ClearReads();
        }

        /// <summary>Draws and dispatches recorded since the last <see cref="Clear"/>. A dispatch counts because a
        /// compute pass reads a uniform buffer exactly as a draw does, and the ocean's FFT is the engine's one
        /// record-time uniform write feeding one.</summary>
        public int DrawCount => _draws;

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
                CapturePayloads ? MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in data)).ToArray() : null, _draws));
            Inner.UpdateBuffer(b, offsetBytes, in data);
        }

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
        {
            _uploads.Add(new Upload(b, offsetBytes, (uint)(data.Length * Unsafe.SizeOf<T>()),
                CapturePayloads ? MemoryMarshal.AsBytes(data).ToArray() : null, _draws));
            Inner.UpdateBuffer(b, offsetBytes, data);
        }

        public void Begin() => Inner.Begin();
        public void End() => Inner.End();
        public void SetFramebuffer(IGpuFramebuffer fb) => Inner.SetFramebuffer(fb);
        public void ClearColorTarget(uint index, Color rgba) => Inner.ClearColorTarget(index, rgba);
        public void ClearDepthStencil(float depth) => Inner.ClearDepthStencil(depth);
        public void SetPipeline(IGpuPipeline p) => Inner.SetPipeline(p);
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
        {
            NoteGraphicsSet(slot, set, 0);
            Inner.SetGraphicsResourceSet(slot, set);
        }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
        {
            NoteGraphicsSet(slot, set, dynamicOffset);
            Inner.SetGraphicsResourceSet(slot, set, dynamicOffset);
        }
        public void SetVertexBuffer(uint slot, IGpuBuffer b) => Inner.SetVertexBuffer(slot, b);
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) => Inner.SetVertexBuffer(slot, b, offsetBytes);
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) => Inner.SetIndexBuffer(b, fmt);
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) => Inner.SetScissorRect(index, x, y, w, h);
        public void SetFullScissorRects() => Inner.SetFullScissorRects();
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            NoteGraphicsReads();
            _draws++;
            Inner.Draw(vertexCount, instanceCount, vertexStart, instanceStart);
        }
        public void Draw(uint vertexCount)
        {
            NoteGraphicsReads();
            _draws++;
            Inner.Draw(vertexCount);
        }
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
        {
            NoteGraphicsReads();
            _draws++;
            Inner.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart);
        }
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
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
        {
            NoteComputeSet(slot, set, 0);
            Inner.SetComputeResourceSet(slot, set);
        }
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
        {
            NoteComputeSet(slot, set, dynamicOffset);
            Inner.SetComputeResourceSet(slot, set, dynamicOffset);
        }
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            NoteComputeReads();
            _draws++;
            Inner.Dispatch(groupCountX, groupCountY, groupCountZ);
        }
        public void Dispose() => Inner.Dispose();
    }
}
