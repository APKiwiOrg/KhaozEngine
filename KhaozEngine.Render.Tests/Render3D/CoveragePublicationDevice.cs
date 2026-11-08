using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Scene-capable inert GPU boundary. Real Scene3D records commands, without claiming device output.</summary>
internal sealed class CoveragePublicationDevice : IGpuDevice, IGpuResourceFactory
{
    readonly FakeGpuDevice inner;
    readonly List<CoverageCarryTestDevice.Fence> fences = [];
    internal readonly List<(string Vertex, string Fragment, GpuShaderCompileOptions Options)> PrecisePrograms = [];
    internal bool AutoComplete = true, RefusePrecise, FailSubmission;
    internal FakeGpuResourceFactory Resources => (FakeGpuResourceFactory)inner.Factory;
    internal CoveragePublicationDevice(bool compute = true, bool completion = true)
        => inner = new(capabilityOverride: new GpuCapabilities(false, true, "coverage-publication",
            supportsCompute: compute, supportsCompletionFences: completion, supportsShadowMaps: true));
    public GpuBackendKind Backend => inner.Backend;
    public GpuCapabilities Capabilities => inner.Capabilities;
    public IGpuResourceFactory Factory => this;
    public IGpuFramebuffer? SwapchainFramebuffer => null;
    public IGpuSampler PointSampler => inner.PointSampler;
    public IGpuSampler LinearSampler => inner.LinearSampler;
    public bool SyncToVerticalBlank { get; set; }
    public void Submit(IGpuCommandList commands) { if (FailSubmission) throw new InvalidOperationException(); }
    public void Submit(IGpuCommandList commands, IGpuFence completion) => Submit(commands, new[] { completion });
    public void Submit(IGpuCommandList commands, ReadOnlySpan<IGpuFence> completions)
    {
        Submit(commands);
        if (AutoComplete) foreach (var fence in completions) ((CoverageCarryTestDevice.Fence)fence).Complete = true;
    }
    public void WaitForIdle() { foreach (var fence in fences) fence.Complete = true; }
    public void UpdateBuffer<T>(IGpuBuffer b, uint o, ReadOnlySpan<T> data) where T : unmanaged { }
    public void UpdateBuffer<T>(IGpuBuffer b, uint o, T[] data) where T : unmanaged { }
    public void UpdateBuffer<T>(IGpuBuffer b, uint o, in T data) where T : unmanaged { }
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h) { }
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h, uint m, uint l) { }
    public MappedData Map(IGpuTexture t, GpuMapMode mode) => throw new NotSupportedException();
    public void Unmap(IGpuTexture t) { }
    public MappedData Map(IGpuBuffer b, GpuMapMode mode) => throw new NotSupportedException();
    public void Unmap(IGpuBuffer b) { }
    public void ResizeSwapchain(uint w, uint h) { }
    public void Present() { }
    public void Dispose() => inner.Dispose();
    public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => Resources.CreateBuffer(d);
    public IGpuTexture CreateTexture(in GpuTextureDescription d) => Resources.CreateTexture(d);
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] color) => Resources.CreateFramebuffer(depth, color);
    public IGpuSampler CreateSampler(in GpuSamplerDescription d) => Resources.CreateSampler(d);
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => Resources.CreateResourceLayout(d);
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => Resources.CreateResourceSet(d);
    public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment) => Resources.CreateShadersFromSpirv(vertex, fragment);
    public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment, GpuShaderCompileOptions options)
    {
        if (options.PreciseMath) { RequirePrecise(GpuShaderStages.Vertex); PrecisePrograms.Add((vertex, fragment, options)); }
        return CreateShadersFromSpirv(vertex, fragment);
    }
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source) => new ComputeShader();
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source, GpuShaderCompileOptions options)
    { if (options.PreciseMath) RequirePrecise(GpuShaderStages.Compute); return new ComputeShader(); }
    void RequirePrecise(GpuShaderStages stage)
    { if (RefusePrecise) throw new GpuShaderPolicyUnsupportedException(GpuShaderPolicyRefusal.PreciseMathUnavailable, stage); }
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => Resources.CreateGraphicsPipeline(d);
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => new ComputePipeline();
    public IGpuCommandList CreateCommandList() => new NullGpuCommandList();
    public IGpuFence CreateFence()
    { var fence = new CoverageCarryTestDevice.Fence(); fences.Add(fence); return fence; }
    sealed class ComputePipeline : IGpuComputePipeline { public void Dispose() { } }
    sealed class ComputeShader : IGpuComputeShader
    {
        public uint ThreadGroupSizeX => 64;
        public uint ThreadGroupSizeY => 1;
        public uint ThreadGroupSizeZ => 1;
        public void Dispose() { }
    }
}
