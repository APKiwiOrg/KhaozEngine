using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Captures the carry harness's actual setup and recording without creating a native device.</summary>
internal sealed class CoverageCarryTestDevice : IGpuDevice, IGpuResourceFactory
{
    readonly FakeGpuDevice inner;
    internal int WaitForIdleCalls;
    internal CoverageCarryTestDevice(bool compute = true, bool completion = true)
        => inner = new(GpuBackendKind.VulkanNative, capabilityOverride: new GpuCapabilities(false, true, "carry-test", supportsCompute: compute, supportsCompletionFences: completion));
    internal bool RefusePipeline;
    internal bool RefusePrecise;
    internal int ResourceSetFailureAfter = -1;
    internal readonly Dictionary<IGpuBuffer, uint[]> Memory = new();
    internal readonly List<IGpuBuffer> Buffers = new();
    internal readonly List<Pipeline> Pipelines = new();
    public GpuBackendKind Backend => inner.Backend;
    public GpuCapabilities Capabilities => inner.Capabilities;
    public IGpuResourceFactory Factory => this;
    public IGpuFramebuffer? SwapchainFramebuffer => null;
    public IGpuSampler PointSampler => inner.PointSampler;
    public IGpuSampler LinearSampler => inner.LinearSampler;
    public bool SyncToVerticalBlank { get; set; }
    public void Submit(IGpuCommandList commands) => throw new NotSupportedException();
    public void Submit(IGpuCommandList commands, IGpuFence fence) => throw new NotSupportedException();
    public void WaitForIdle() => WaitForIdleCalls++;
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, ReadOnlySpan<T> data) where T : unmanaged
        => MemoryMarshal.Cast<T, uint>(data).CopyTo(Memory[buffer].AsSpan((int)offset / 4));
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, T[] data) where T : unmanaged => UpdateBuffer<T>(buffer, offset, data.AsSpan());
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, in T data) where T : unmanaged
        => UpdateBuffer<T>(buffer, offset, MemoryMarshal.CreateReadOnlySpan(in data, 1));
    public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height) => throw new NotSupportedException();
    public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height, uint mip, uint layer) => throw new NotSupportedException();
    public MappedData Map(IGpuTexture texture, GpuMapMode mode) => throw new NotSupportedException();
    public void Unmap(IGpuTexture texture) => throw new NotSupportedException();
    public MappedData Map(IGpuBuffer buffer, GpuMapMode mode) => throw new NotSupportedException();
    public void Unmap(IGpuBuffer buffer) => throw new NotSupportedException();
    public void ResizeSwapchain(uint width, uint height) => throw new NotSupportedException();
    public void Present() => throw new NotSupportedException();
    public void Dispose() => inner.Dispose();

    public IGpuBuffer CreateBuffer(in GpuBufferDescription description)
    {
        var buffer = inner.Factory.CreateBuffer(description);
        Buffers.Add(buffer);
        Memory.Add(buffer, new uint[buffer.SizeInBytes / 4]);
        return buffer;
    }
    public IGpuTexture CreateTexture(in GpuTextureDescription description) => throw new NotSupportedException();
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) => throw new NotSupportedException();
    public IGpuSampler CreateSampler(in GpuSamplerDescription description) => throw new NotSupportedException();
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription description) => new Layout(description);
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription description)
    {
        if (ResourceSetFailureAfter == 0) { ResourceSetFailureAfter = -1; throw new InvalidOperationException(); }
        if (ResourceSetFailureAfter > 0) ResourceSetFailureAfter--;
        return new Set(description);
    }
    public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment) => throw new NotSupportedException();
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source) => new Shader();
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source, GpuShaderCompileOptions options)
    {
        if (RefusePrecise && options.PreciseMath)
            throw new GpuShaderPolicyUnsupportedException(GpuShaderPolicyRefusal.PreciseMathUnavailable, GpuShaderStages.Compute);
        return new Shader();
    }
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription description) => throw new NotSupportedException();
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription description)
    {
        if (RefusePipeline) throw new NotSupportedException();
        var pipeline = new Pipeline(description);
        Pipelines.Add(pipeline);
        return pipeline;
    }
    public IGpuCommandList CreateCommandList() => new Commands();
    public IGpuFence CreateFence() => throw new NotSupportedException();

    internal sealed class Layout(GpuResourceLayoutDescription description) : IGpuResourceLayout
    {
        internal GpuResourceLayoutDescription Description { get; } = description;
        public void Dispose() { }
    }
    internal sealed class Set(GpuResourceSetDescription description) : IGpuResourceSet
    {
        internal GpuResourceSetDescription Description { get; } = description;
        public void Dispose() { }
    }
    internal sealed class Pipeline(GpuComputePipelineDescription description) : IGpuComputePipeline
    {
        internal GpuComputePipelineDescription Description { get; } = description;
        public void Dispose() { }
    }
    internal sealed class Fence : IGpuFence
    {
        internal bool Complete;
        public bool Signaled => Complete;
        public void Reset() => Complete = false;
        public void Dispose() { }
    }

    sealed class Shader : IGpuComputeShader
    {
        public uint ThreadGroupSizeX => 64;
        public uint ThreadGroupSizeY => 1;
        public uint ThreadGroupSizeZ => 1;
        public void Dispose() { }
    }

    internal sealed class Commands : IGpuCommandList
    {
        Pipeline? pipeline;
        Set? set;
        internal readonly List<(Pipeline Pipeline, Set Set)> Dispatches = new();
        internal readonly List<(IGpuBuffer Buffer, uint Offset, uint[] Words)> Uploads = new();
        internal readonly List<(uint X, uint Y, uint Z)> Groups = new();
        internal readonly List<(IGpuBuffer Source, IGpuBuffer Target)> Copies = new();
        internal bool Capture = true;
        internal readonly List<uint> Markers = new();
        public void Begin() { pipeline = null; set = null; Dispatches.Clear(); Uploads.Clear(); Groups.Clear(); Markers.Clear(); Copies.Clear(); }
        public void End() { }
        public void SetComputePipeline(IGpuComputePipeline value) => pipeline = (Pipeline)value;
        public void SetComputeResourceSet(uint slot, IGpuResourceSet value)
        { if (slot != 0) throw new ArgumentOutOfRangeException(nameof(slot)); set = (Set)value; }
        public void SetComputeResourceSet(uint slot, IGpuResourceSet value, uint dynamicOffset) => throw new NotSupportedException();
        public void Dispatch(uint x, uint y, uint z)
        { if (pipeline is null || set is null) throw new InvalidOperationException(); if (Capture) { Dispatches.Add((pipeline, set)); Groups.Add((x, y, z)); } }
        public void WriteTimestamp(IGpuTimestampPool pool, uint index) => Markers.Add(index);
        public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, in T data) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, ReadOnlySpan<T> data) where T : unmanaged
        { if (Capture) Uploads.Add((buffer, offset, MemoryMarshal.Cast<T, uint>(data).ToArray())); }
        public void SetFramebuffer(IGpuFramebuffer framebuffer) => throw new NotSupportedException();
        public void ClearColorTarget(uint index, Color colour) => throw new NotSupportedException();
        public void ClearDepthStencil(float depth) => throw new NotSupportedException();
        public void SetPipeline(IGpuPipeline value) => throw new NotSupportedException();
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet value) => throw new NotSupportedException();
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet value, uint dynamicOffset) => throw new NotSupportedException();
        public void SetVertexBuffer(uint slot, IGpuBuffer buffer) => throw new NotSupportedException();
        public void SetVertexBuffer(uint slot, IGpuBuffer buffer, uint offset) => throw new NotSupportedException();
        public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format) => throw new NotSupportedException();
        public void SetScissorRect(uint index, uint x, uint y, uint width, uint height) => throw new NotSupportedException();
        public void SetFullScissorRects() => throw new NotSupportedException();
        public void Draw(uint count, uint instances, uint first, uint firstInstance) => throw new NotSupportedException();
        public void Draw(uint count) => throw new NotSupportedException();
        public void DrawIndexed(uint count, uint instances, uint first, int offset, uint firstInstance) => throw new NotSupportedException();
        public void CopyBuffer(IGpuBuffer source, uint sourceOffset, IGpuBuffer target, uint targetOffset, uint size)
        { if (Capture) Copies.Add((source, target)); }
        public void CopyTexture(IGpuTexture source, IGpuTexture target) => throw new NotSupportedException();
        public void CopyTextureSubresource(IGpuTexture source, uint mip, uint layer, IGpuTexture target, uint width, uint height) => throw new NotSupportedException();
        public void CopyTextureSubresource(IGpuTexture source, uint sourceMip, uint sourceLayer, IGpuTexture target,
            uint targetMip, uint targetLayer, uint width, uint height) => throw new NotSupportedException();
        public void GenerateMipmaps(IGpuTexture texture) => throw new NotSupportedException();
        public void ResolveTexture(IGpuTexture source, IGpuTexture target) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
