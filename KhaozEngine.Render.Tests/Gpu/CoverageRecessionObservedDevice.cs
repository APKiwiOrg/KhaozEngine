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
    internal readonly Dictionary<IGpuResourceSet, GpuResourceSetDescription> Sets = [];
    internal readonly Dictionary<IGpuResourceLayout, GpuResourceLayoutDescription> Layouts = [];
    internal readonly Dictionary<IGpuPipeline, GpuPipelineDescription> Pipelines = [];
    readonly Dictionary<IGpuShaderSet, (string Vertex, string Fragment)> shaderPairs = [];
    internal readonly List<(string Source, GpuShaderCompileOptions Options)> ComputeShaders = [];
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
    public void WaitForIdle() => inner.WaitForIdle();
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
    public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => inner.Factory.CreateBuffer(d);
    public IGpuTexture CreateTexture(in GpuTextureDescription d) => inner.Factory.CreateTexture(d);
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] color) => inner.Factory.CreateFramebuffer(depth, color);
    public IGpuSampler CreateSampler(in GpuSamplerDescription d) => inner.Factory.CreateSampler(d);
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
    { var layout = inner.Factory.CreateResourceLayout(d); Layouts.Add(layout, d); return layout; }
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
    { var set = inner.Factory.CreateResourceSet(d); Sets.Add(set, d); return set; }
    public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment) => CreateShadersFromSpirv(vertex, fragment, default);
    public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment, GpuShaderCompileOptions options)
    {
        GraphicsShaders.Add((vertex, options));
        var shaders = inner.Factory.CreateShadersFromSpirv(vertex, fragment, options);
        shaderPairs.Add(shaders, (vertex, fragment)); return shaders;
    }
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source) => CreateComputeShaderFromSpirv(source, default);
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source, GpuShaderCompileOptions options)
    { ComputeShaders.Add((source, options)); return inner.Factory.CreateComputeShaderFromSpirv(source, options); }
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
    { var pipeline = inner.Factory.CreateGraphicsPipeline(d); Pipelines.Add(pipeline, d); return pipeline; }
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => inner.Factory.CreateComputePipeline(d);
    public IGpuCommandList CreateCommandList() => inner.Factory.CreateCommandList();
    public IGpuFence CreateFence() => inner.Factory.CreateFence();
    public IGpuTimestampPool CreateTimestampPool(uint capacity) => inner.Factory.CreateTimestampPool(capacity);

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
