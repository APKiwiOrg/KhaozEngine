using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfGeometryTraceTests;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Trace preparer lists as well as the frame, without changing inert resource semantics.</summary>
internal sealed class PostTraceFactory(IGpuResourceFactory inner) : IGpuResourceFactory
{
    internal readonly List<GeometryTraceCommands> Lists = [];
    public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => inner.CreateBuffer(d);
    public IGpuTexture CreateTexture(in GpuTextureDescription d) => inner.CreateTexture(d);
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colors) => inner.CreateFramebuffer(depth, colors);
    public IGpuSampler CreateSampler(in GpuSamplerDescription d) => inner.CreateSampler(d);
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => inner.CreateResourceLayout(d);
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => inner.CreateResourceSet(d);
    public IGpuShaderSet CreateShadersFromSpirv(string v, string f) => inner.CreateShadersFromSpirv(v, f);
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => inner.CreateGraphicsPipeline(d);
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source) => inner.CreateComputeShaderFromSpirv(source);
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => inner.CreateComputePipeline(d);
    public IGpuCommandList CreateCommandList()
    {
        var commands = new GeometryTraceCommands(inner.CreateCommandList());
        Lists.Add(commands);
        return commands;
    }
    public IGpuFence CreateFence() => inner.CreateFence();
}

/// <summary>Keep ownership on the actual list named by each callback, including the startup prime.</summary>
internal sealed class PostTraceObserver(GeometryTraceCommands frame) : IRenderPassObserver
{
    readonly GeometryObserver _observer = new(frame);
    internal List<(RenderPassKey Key, RenderPassKey? Parent)> Passes => _observer.Passes;
    internal List<RenderPassKey> Skipped => _observer.Skipped;
    internal List<RenderPassKey> Fused => _observer.Fused;
    public void BeginPass(IGpuCommandList cl, RenderPassKey key)
    {
        _observer.BeginPass(cl, key);
        if (cl is GeometryTraceCommands trace) trace.Owner = key;
    }
    public void EndPass(IGpuCommandList cl, RenderPassKey key)
    {
        _observer.EndPass(cl, key);
        if (cl is GeometryTraceCommands trace) trace.Owner = frame.Owner;
    }
    public void SkipPass(IGpuCommandList cl, RenderPassKey key) => _observer.SkipPass(cl, key);
    public void BeginCpuPhase(string id) => _observer.BeginCpuPhase(id);
    public void EndCpuPhase(string id) => _observer.EndCpuPhase(id);
    public void FusedMotion(IGpuCommandList cl, RenderPassKey key) => _observer.FusedMotion(cl, key);
    internal RendererPerfPassDefinition[] Freeze() => _observer.Freeze();
    internal void AssertClosed() => _observer.AssertClosed();
}
