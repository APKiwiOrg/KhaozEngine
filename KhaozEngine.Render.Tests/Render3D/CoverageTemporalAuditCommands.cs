using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D;

// Fixed-storage command evidence. No payload arrays, lists, dictionaries or assertions are created while recording.
internal sealed class CoverageTemporalAuditCommands(int capacity) : IGpuCommandList
{
    readonly Vector3[] previous = new Vector3[capacity];
    IGpuPipeline? pipeline;
    IGpuFramebuffer? framebuffer;
    IGpuResourceSet? set;
    IGpuBuffer? previousBound;
    internal IGpuBuffer? PreviousBuffer;
    internal IGpuTexture? Mask;
    internal string? ResolveFragment;
    internal int PreviousCount, PreviousDraws, MaskClears, MaskFills, ResolveMaskReads;
    internal bool WrongMaskClear, WrongUploadSize;
    internal ReadOnlySpan<Vector3> PreviousPositions => previous.AsSpan(0, PreviousCount);

    internal void Reset()
    {
        PreviousCount = PreviousDraws = MaskClears = MaskFills = ResolveMaskReads = 0;
        WrongMaskClear = WrongUploadSize = false;
        pipeline = null; framebuffer = null; set = null; previousBound = null;
    }

    string? Fragment => (pipeline as FakePipeline)?.Request?.FragmentGlsl;
    public void Begin() { }
    public void End() { }
    public void SetFramebuffer(IGpuFramebuffer value) => framebuffer = value;
    public void ClearColorTarget(uint index, Color rgba)
    {
        if (framebuffer is not FakeFramebuffer { Colour.Length: 1 } fb || !ReferenceEquals(fb.Colour[0], Mask)) return;
        MaskClears++;
        WrongMaskClear |= rgba.R != 0 || rgba.G != 0 || rgba.B != 0 || rgba.A != 0;
    }
    public void SetPipeline(IGpuPipeline value) => pipeline = value;
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet value) { if (slot == 0) set = value; }
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet value, uint dynamicOffset) => SetGraphicsResourceSet(slot, value);
    public void SetVertexBuffer(uint slot, IGpuBuffer value) { if (slot == 2) previousBound = value; }
    public void SetVertexBuffer(uint slot, IGpuBuffer value, uint offsetBytes) => SetVertexBuffer(slot, value);
    public void Draw(uint vertexCount) => Draw(vertexCount, 1, 0, 0);
    public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
    {
        if (ReferenceEquals(Fragment, ShaderSources.CoverageTransitionFrag)
            && framebuffer is FakeFramebuffer { Colour.Length: 1 } fb && ReferenceEquals(fb.Colour[0], Mask)) MaskFills++;
        if (!ReferenceEquals(Fragment, ResolveFragment) || set is not FakeResourceSet resource) return;
        foreach (var value in resource.Resources)
            if (ReferenceEquals(value, Mask)) ResolveMaskReads++;
    }
    public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
    {
        if (ReferenceEquals(Fragment, ShaderSources.ModelMotionFrag) && ReferenceEquals(previousBound, PreviousBuffer)) PreviousDraws++;
    }
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offsetBytes, in T data) where T : unmanaged =>
        UpdateBuffer(buffer, offsetBytes, MemoryMarshal.CreateReadOnlySpan(in data, 1));
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
    {
        if (!ReferenceEquals(buffer, PreviousBuffer)) return;
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        WrongUploadSize |= offsetBytes != 0 || bytes.Length > previous.Length * 12 || bytes.Length % 12 != 0;
        if (WrongUploadSize) return;
        bytes.CopyTo(MemoryMarshal.AsBytes(previous.AsSpan()));
        PreviousCount = bytes.Length / 12;
    }
    public void ClearDepthStencil(float depth) { }
    public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format) { }
    public void SetScissorRect(uint index, uint x, uint y, uint width, uint height) { }
    public void SetFullScissorRects() { }
    public void CopyBuffer(IGpuBuffer source, uint sourceOffset, IGpuBuffer target, uint targetOffset, uint bytes) { }
    public void CopyTexture(IGpuTexture source, IGpuTexture target) { }
    public void CopyTextureSubresource(IGpuTexture source, uint sourceMip, uint sourceLayer, IGpuTexture target, uint width, uint height) { }
    public void CopyTextureSubresource(IGpuTexture source, uint sourceMip, uint sourceLayer, IGpuTexture target, uint targetMip,
        uint targetLayer, uint width, uint height)
    { }
    public void GenerateMipmaps(IGpuTexture texture) { }
    public void ResolveTexture(IGpuTexture source, IGpuTexture target) { }
    public void SetComputePipeline(IGpuComputePipeline value) { }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet value) { }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet value, uint dynamicOffset) { }
    public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) { }
    public void Dispose() { }
}
