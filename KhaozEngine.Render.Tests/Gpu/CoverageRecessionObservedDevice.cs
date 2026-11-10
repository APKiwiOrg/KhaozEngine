using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Non-owning native pass-through. Captures public factory inputs without wrapping native handles.</summary>
internal sealed class CoverageRecessionObservedDevice(IGpuDevice inner) : IGpuDevice, IGpuResourceFactory
{
    internal readonly List<CoverageRecessionFactoryFailure> Failures = [];
    internal readonly CoverageRecessionCleanup Cleanup = new();
    List<IDisposable>? constructing;
    T Track<T>(T resource) where T : IDisposable
    { constructing?.Add(resource); return resource; }

    // Scene3D construction has no returned owner on failure. Keep its raw native handles until ownership transfers.
    internal T Construct<T>(Func<T> create)
    {
        constructing = [];
        try { return create(); }
        catch
        {
            Cleanup.Active = true;
            if (Cleanup.Drain("construction.drain", inner.WaitForIdle))
                for (int i = constructing.Count - 1; i >= 0; i--)
                    Cleanup.Release("construction.resource." + i, constructing[i].Dispose);
            throw;
        }
        finally { if (!Cleanup.DrainFailed) constructing = null; }
    }
    internal void Capture(string operation, Exception error) => Failures.Add(CoverageRecessionFactoryFailure.Capture(operation, error));
    internal readonly Dictionary<IGpuResourceSet, GpuResourceSetDescription> Sets = [];
    internal readonly Dictionary<IGpuResourceLayout, GpuResourceLayoutDescription> Layouts = [];
    internal readonly Dictionary<IGpuPipeline, GpuPipelineDescription> Pipelines = [];
    readonly Dictionary<IGpuShaderSet, (string Vertex, string Fragment)> shaderPairs = [];
    internal readonly Dictionary<IGpuShaderSet, GpuShaderCompileOptions> ShaderOptions = [];
    internal readonly List<(string Source, GpuShaderCompileOptions Options)> ComputeShaders = [];
    readonly Dictionary<IGpuComputeShader, int> carryShaders = [];
    internal readonly Dictionary<IGpuComputePipeline, int> CarryStages = [];
    internal readonly List<(string Vertex, GpuShaderCompileOptions Options)> GraphicsShaders = [];
    public GpuBackendKind Backend => inner.Backend;
    public GpuCapabilities Capabilities => inner.Capabilities;
    public GpuDeviceDiagnostics Diagnostics => inner.Diagnostics;
    public IGpuResourceFactory Factory => this;
    public IGpuFramebuffer? SwapchainFramebuffer => inner.SwapchainFramebuffer;
    public IGpuSampler PointSampler => inner.PointSampler;
    public IGpuSampler LinearSampler => inner.LinearSampler;
    public bool SyncToVerticalBlank { get => inner.SyncToVerticalBlank; set => inner.SyncToVerticalBlank = value; }
    public void Submit(IGpuCommandList list) => inner.Submit(list);
    public void Submit(IGpuCommandList list, IGpuFence fence) => inner.Submit(list, fence);
    public void Submit(IGpuCommandList list, ReadOnlySpan<IGpuFence> fences) => inner.Submit(list, fences);
    public void WaitForIdle()
    {
        try { inner.WaitForIdle(); }
        catch
        {
            if (Cleanup.Active) Cleanup.RefuseDrain();
            throw;
        }
    }
    public void UpdateBuffer<T>(IGpuBuffer b, uint o, ReadOnlySpan<T> data) where T : unmanaged => inner.UpdateBuffer(b, o, data);
    public void UpdateBuffer<T>(IGpuBuffer b, uint o, T[] data) where T : unmanaged => inner.UpdateBuffer(b, o, data);
    public void UpdateBuffer<T>(IGpuBuffer b, uint o, in T data) where T : unmanaged => inner.UpdateBuffer(b, o, in data);
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h) => inner.UpdateTexture(t, data, x, y, w, h);
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h, uint m, uint l) => inner.UpdateTexture(t, data, x, y, w, h, m, l);
    public MappedData Map(IGpuTexture t, GpuMapMode mode) => inner.Map(t, mode);
    public void Unmap(IGpuTexture t) => inner.Unmap(t);
    public MappedData Map(IGpuBuffer b, GpuMapMode mode) => inner.Map(b, mode);
    public void Unmap(IGpuBuffer b) => inner.Unmap(b);
    public void ResizeSwapchain(uint w, uint h) => inner.ResizeSwapchain(w, h);
    public void Present() => inner.Present();
    public void Dispose() { }
    public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
    {
        try { return Track(inner.Factory.CreateBuffer(d)); }
        catch (Exception error) { Capture(nameof(CreateBuffer), error); throw; }
    }
    public IGpuTexture CreateTexture(in GpuTextureDescription d)
    {
        try { return Track(inner.Factory.CreateTexture(d)); }
        catch (Exception error) { Capture(nameof(CreateTexture), error); throw; }
    }
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] color)
    {
        try { return Track(inner.Factory.CreateFramebuffer(depth, color)); }
        catch (Exception error) { Capture(nameof(CreateFramebuffer), error); throw; }
    }
    public IGpuSampler CreateSampler(in GpuSamplerDescription d)
    {
        try { return Track(inner.Factory.CreateSampler(d)); }
        catch (Exception error) { Capture(nameof(CreateSampler), error); throw; }
    }
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
    {
        try { var layout = Track(inner.Factory.CreateResourceLayout(d)); Layouts.Add(layout, d); return layout; }
        catch (Exception error) { Capture(nameof(CreateResourceLayout), error); throw; }
    }
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
    {
        try { var set = Track(inner.Factory.CreateResourceSet(d)); Sets.Add(set, d); return set; }
        catch (Exception error) { Capture(nameof(CreateResourceSet), error); throw; }
    }
    public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment) => CreateShadersFromSpirv(vertex, fragment, default);
    public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment, GpuShaderCompileOptions options)
    {
        GraphicsShaders.Add((vertex, options));
        IGpuShaderSet shaders;
        try
        {
            shaders = inner.Factory.CreateShadersFromSpirv(vertex, fragment, options);
        }
        catch (Exception error) { Capture(nameof(CreateShadersFromSpirv), error); throw; }
        Track(shaders); shaderPairs.Add(shaders, (vertex, fragment)); ShaderOptions.Add(shaders, options); return shaders;
    }
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source) => CreateComputeShaderFromSpirv(source, default);
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source, GpuShaderCompileOptions options)
    {
        ComputeShaders.Add((source, options));
        IGpuComputeShader shader;
        try
        {
            shader = inner.Factory.CreateComputeShaderFromSpirv(source, options);
        }
        catch (Exception error) { Capture(nameof(CreateComputeShaderFromSpirv), error); throw; }
        Track(shader);
        for (int stage = 0; stage < CoverageCarryShaders.StageCount; stage++)
            if (source == CoverageCarryShaders.Kernels[stage].Source) carryShaders.Add(shader, stage);
        return shader;
    }
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
    {
        try { var pipeline = Track(inner.Factory.CreateGraphicsPipeline(d)); Pipelines.Add(pipeline, d); return pipeline; }
        catch (Exception error) { Capture(nameof(CreateGraphicsPipeline), error); throw; }
    }
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
    {
        IGpuComputePipeline pipeline;
        try { pipeline = Track(inner.Factory.CreateComputePipeline(d)); }
        catch (Exception error) { Capture(nameof(CreateComputePipeline), error); throw; }
        if (carryShaders.TryGetValue(d.Shader, out int stage)) CarryStages.Add(pipeline, stage);
        return pipeline;
    }
    public IGpuCommandList CreateCommandList()
    {
        try { return Track(inner.Factory.CreateCommandList()); }
        catch (Exception error) { Capture(nameof(CreateCommandList), error); throw; }
    }
    public IGpuFence CreateFence()
    {
        try { return Track(inner.Factory.CreateFence()); }
        catch (Exception error) { Capture(nameof(CreateFence), error); throw; }
    }
    public IGpuTimestampPool CreateTimestampPool(uint capacity)
    {
        try { return Track(inner.Factory.CreateTimestampPool(capacity)); }
        catch (Exception error) { Capture(nameof(CreateTimestampPool), error); throw; }
    }

    internal static bool IsCoverageVertex(string vertex) => vertex == ShaderSources.CoverageModelVert || vertex == ShaderSources.CoverageModelMotionVert
        || vertex == ShaderSources.CoverageCascadeVert || vertex == ShaderSources.CoverageCascadeDissolveVert
        || vertex == ShaderSources.CoveragePointVert || vertex == ShaderSources.CoveragePointDissolveVert
        || vertex == ShaderSources.CoverageOutlineVert;

    internal (bool Coverage, bool Precise) DrawPolicy(IGpuPipeline pipeline)
    {
        var shaders = Pipelines[pipeline].ShaderSet;
        string vertex = shaderPairs[shaders].Vertex;
        return (vertex == ShaderSources.CoverageModelVert || vertex == ShaderSources.CoverageModelMotionVert
            || vertex == ShaderSources.CoverageCascadeVert || vertex == ShaderSources.CoverageCascadeDissolveVert
            || vertex == ShaderSources.CoveragePointVert || vertex == ShaderSources.CoveragePointDissolveVert
            || vertex == ShaderSources.CoverageOutlineVert, ShaderOptions[shaders].PreciseMath);
    }

    internal string? Pass(RecordingGpuCommandList.DrawBindings draw)
    {
        if (draw.Pipeline is null) return null;
        var pair = shaderPairs[Pipelines[draw.Pipeline].ShaderSet];
        if (pair.Vertex == ShaderSources.CoverageModelMotionVert) return "motion";
        if (pair.Vertex == ShaderSources.CoverageModelVert) return "colour-depth";
        if (pair.Vertex == ShaderSources.CoverageCascadeVert || pair.Vertex == ShaderSources.CoverageCascadeDissolveVert) return "cascade";
        if (pair.Vertex == ShaderSources.CoveragePointVert || pair.Vertex == ShaderSources.CoveragePointDissolveVert) return "point";
        if (pair.Vertex == ShaderSources.CoverageOutlineVert)
            return pair.Fragment == ShaderSources.TargetOutlineFullMaskFrag ? "full" : "visible";
        return null;
    }

    internal void AssertPulling(RecordingGpuCommandList recorded, RecordingGpuCommandList.DrawBindings draw, CoverageSurfaceRange surface)
    {
        var pipeline = Pipelines[draw.Pipeline!];
        int slot = Array.FindIndex(pipeline.ResourceLayouts.ToArray(), l => Layouts[l].Elements[0].Name == "CoverageCurrent");
        Assert.True(slot >= 0);
        var bound = draw.Sets[(uint)slot];
        var resources = Sets[bound.Set].Resources;
        Assert.Same(surface.Current, resources[0]); Assert.Same(surface.Previous, resources[3]);
        Assert.Same(surface.Indices, resources[1]);
        var range = Assert.IsType<GpuBufferRange>(resources[2]);
        uint offset = range.Offset + bound.DynamicOffset;
        var bytes = new byte[range.Size];
        int overlaps = 0;
        foreach (var upload in recorded.Uploads)
        {
            if (!ReferenceEquals(upload.Buffer, range.Buffer) || upload.DrawsBefore > draw.DrawOrdinal) continue;
            long first = Math.Max(offset, upload.Offset), last = Math.Min(offset + range.Size, upload.Offset + upload.Bytes);
            if (last <= first) continue;
            Assert.NotNull(upload.Data); overlaps++;
            upload.Data.AsSpan((int)(first - upload.Offset), (int)(last - first)).CopyTo(bytes.AsSpan((int)(first - offset)));
        }
        Assert.True(overlaps > 0);
        var words = MemoryMarshal.Cast<byte, uint>(bytes);
        Assert.Equal(surface.CurrentBase, words[0]); Assert.Equal(surface.PreviousBase, words[3]);
        Assert.Equal(0u, words[2]); Assert.Equal((uint)surface.IndexCount, draw.ElementCount);
        if (Pass(draw) is "motion" or "colour-depth")
        {
            Assert.True(pipeline.DepthStencil.DepthWriteEnabled && pipeline.DepthStencil.DepthTestEnabled);
            Assert.True(pipeline.Outputs.Colour.Length >= 3);
        }
    }
}
