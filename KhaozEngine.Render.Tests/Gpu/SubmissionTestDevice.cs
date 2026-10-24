using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gpu;

internal sealed class SubmissionTestFence : IGpuFence
{
    bool _signaled;
    internal int PollCalls, ResetCalls, DisposeCalls;
    public bool Signaled { get { PollCalls++; return _signaled; } set => _signaled = value; }
    public void Reset() { ResetCalls++; _signaled = false; }
    public void Dispose() => DisposeCalls++;
}

internal sealed class SubmissionTestBuffer : IGpuBuffer
{
    internal readonly byte[] Bytes;
    internal readonly GpuBufferUsage Usage;
    internal int DisposeCalls;
    internal Action? OnDispose;
    internal SubmissionTestBuffer(uint bytes, GpuBufferUsage usage = GpuBufferUsage.StructuredBufferReadWrite)
    { Bytes = new byte[bytes]; Usage = usage; }
    public uint SizeInBytes => (uint)Bytes.Length;
    public void Dispose() { DisposeCalls++; OnDispose?.Invoke(); }
}

// Copies preserve bytes. Tests control completion and count every submission and drain.
// Unsupported data operations throw rather than synthesizing GPU output.
internal sealed class SubmissionTestDevice : IGpuDevice, IGpuResourceFactory, IGpuCommandList
{
    internal readonly SubmissionTestBuffer[] Buffers;
    internal int CreatedCount, CopyCalls, ReadCalls, WaitCalls, FlushCalls, SubmitCalls;
    internal readonly System.Collections.Generic.List<SubmissionTestFence> Fences = new();
    internal bool FailSubmit, FailWait, AllowCommandList;
    internal IGpuFence? SubmittedFence;
    internal IGpuFence[] SubmittedFences = Array.Empty<IGpuFence>();
    internal SubmissionTestDevice(int capacity) => Buffers = new SubmissionTestBuffer[capacity];
    public IGpuResourceFactory Factory => this;
    public GpuBackendKind Backend => GpuBackendKind.MetalNative;
    public GpuCapabilities Capabilities => new(false, true, "SubmissionTestDevice", supportsCompletionFences: true);
    public IGpuFramebuffer? SwapchainFramebuffer => null;
    public IGpuSampler PointSampler => throw new NotSupportedException();
    public IGpuSampler LinearSampler => throw new NotSupportedException();
    public bool SyncToVerticalBlank { get; set; }

    public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
    {
        var buffer = new SubmissionTestBuffer(d.SizeInBytes, d.Usage);
        Buffers[CreatedCount++] = buffer;
        return buffer;
    }

    public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
    {
        CopyCalls++;
        ((SubmissionTestBuffer)src).Bytes.AsSpan((int)srcOffsetBytes, (int)sizeInBytes)
            .CopyTo(((SubmissionTestBuffer)dst).Bytes.AsSpan((int)dstOffsetBytes, (int)sizeInBytes));
    }

    public bool TryReadBuffer(IGpuBuffer staging, IGpuFence completion, uint offsetBytes, Span<byte> destination)
    {
        ReadCalls++;
        if (!completion.Signaled) return false;
        ((SubmissionTestBuffer)staging).Bytes.AsSpan((int)offsetBytes, destination.Length).CopyTo(destination);
        return true;
    }

    public void Submit(IGpuCommandList cl) { SubmitCalls++; if (FailSubmit) throw new InvalidOperationException("planned submit failure"); }
    public void Submit(IGpuCommandList cl, IGpuFence fence) { SubmitCalls++; SubmittedFence = fence; if (FailSubmit) throw new InvalidOperationException("planned submit failure"); }
    public void Submit(IGpuCommandList cl, ReadOnlySpan<IGpuFence> completions)
    {
        GpuCompletionFences.Validate(completions);
        if (SubmittedFences.Length != completions.Length) SubmittedFences = new IGpuFence[completions.Length];
        completions.CopyTo(SubmittedFences);
        SubmittedFence = completions.IsEmpty ? null : completions[0];
        Submit(cl);
    }
    public void WaitForIdle() { WaitCalls++; FlushCalls++; if (FailWait) throw new InvalidOperationException("planned drain failure"); foreach (var fence in Fences) fence.Signaled = true; }
    public MappedData Map(IGpuBuffer staging, GpuMapMode mode) { WaitCalls++; throw new NotSupportedException(); }
    public MappedData Map(IGpuTexture staging, GpuMapMode mode) => throw new NotSupportedException();
    public void Unmap(IGpuBuffer staging) => throw new NotSupportedException();
    public void Unmap(IGpuTexture staging) => throw new NotSupportedException();
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged => throw new NotSupportedException();
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged => throw new NotSupportedException();
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged => throw new NotSupportedException();
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h) => throw new NotSupportedException();
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h, uint mip, uint layer) => throw new NotSupportedException();
    public void ResizeSwapchain(uint w, uint h) => throw new NotSupportedException();
    public void Present() { FlushCalls++; throw new NotSupportedException(); }
    public IGpuTexture CreateTexture(in GpuTextureDescription d) => throw new NotSupportedException();
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) => throw new NotSupportedException();
    public IGpuSampler CreateSampler(in GpuSamplerDescription d) => throw new NotSupportedException();
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => throw new NotSupportedException();
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => throw new NotSupportedException();
    public IGpuShaderSet CreateShadersFromSpirv(string v, string f) => throw new NotSupportedException();
    public IGpuComputeShader CreateComputeShaderFromSpirv(string c) => throw new NotSupportedException();
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => throw new NotSupportedException();
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => throw new NotSupportedException();
    public IGpuCommandList CreateCommandList() => AllowCommandList ? new NullGpuCommandList() : throw new NotSupportedException();
    public IGpuFence CreateFence() { var fence = new SubmissionTestFence(); Fences.Add(fence); return fence; }
    public void Begin() => throw new NotSupportedException();
    public void End() => throw new NotSupportedException();
    public void SetFramebuffer(IGpuFramebuffer fb) => throw new NotSupportedException();
    public void ClearColorTarget(uint index, Color rgba) => throw new NotSupportedException();
    public void ClearDepthStencil(float depth) => throw new NotSupportedException();
    public void SetPipeline(IGpuPipeline p) => throw new NotSupportedException();
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) => throw new NotSupportedException();
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) => throw new NotSupportedException();
    public void SetVertexBuffer(uint slot, IGpuBuffer b) => throw new NotSupportedException();
    public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) => throw new NotSupportedException();
    public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) => throw new NotSupportedException();
    public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) => throw new NotSupportedException();
    public void SetFullScissorRects() => throw new NotSupportedException();
    public void Draw(uint count, uint instances, uint start, uint instanceStart) => throw new NotSupportedException();
    public void Draw(uint count) => throw new NotSupportedException();
    public void DrawIndexed(uint count, uint instances, uint start, int offset, uint instanceStart) => throw new NotSupportedException();
    public void CopyTexture(IGpuTexture src, IGpuTexture dst) => throw new NotSupportedException();
    public void CopyTextureSubresource(IGpuTexture src, uint mip, uint layer, IGpuTexture dst, uint w, uint h) => throw new NotSupportedException();
    public void CopyTextureSubresource(IGpuTexture src, uint srcMip, uint srcLayer, IGpuTexture dst,
        uint dstMip, uint dstLayer, uint w, uint h) => throw new NotSupportedException();
    public void GenerateMipmaps(IGpuTexture texture) => throw new NotSupportedException();
    public void ResolveTexture(IGpuTexture src, IGpuTexture dst) => throw new NotSupportedException();
    public void SetComputePipeline(IGpuComputePipeline p) => throw new NotSupportedException();
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set) => throw new NotSupportedException();
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint offset) => throw new NotSupportedException();
    public void Dispatch(uint x, uint y, uint z) => throw new NotSupportedException();
    public void Dispose() { }
}
