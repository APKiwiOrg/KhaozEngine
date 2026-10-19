using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Precreated state bindings for GPU conditional transition rectangles.</summary>
internal sealed class CoverageTransitionGpu : IDisposable
{
    readonly IGpuDevice device;
    readonly GpuRetireQueue retired;
    readonly IGpuResourceLayout layout;
    readonly IGpuShaderSet shaders;
    IGpuBuffer? uniforms, first, second;
    IGpuResourceSet? firstSet, secondSet;
    IGpuPipeline? pipeline;
    uint[] image = [];

    internal CoverageTransitionGpu(IGpuDevice device, GpuRetireQueue retired)
    {
        this.device = device; this.retired = retired;
        layout = device.Factory.CreateResourceLayout(new(
            new GpuResourceLayoutElement("TransitionState", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex),
            new GpuResourceLayoutElement("TransitionOffset", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
        shaders = device.Factory.CreateShadersFromSpirv(ShaderSources.CoverageTransitionVert, ShaderSources.CoverageTransitionFrag);
    }

    internal void Reserve(CoverageGpuBatch batch, int count)
    {
        int words = checked(Math.Max(1, count) * 64);
        if (image.Length >= words && (ReferenceEquals(first, batch.Current) || ReferenceEquals(second, batch.Current))) return;
        bool grow = image.Length < words;
        IGpuBuffer next = grow ? device.Factory.CreateBuffer(new((uint)words * 4, GpuBufferUsage.UniformBuffer)) : uniforms!;
        IGpuResourceSet? a = null, b = null;
        try
        {
            a = device.Factory.CreateResourceSet(new(layout, batch.Current, new GpuBufferRange(next, 0, 16)));
            b = device.Factory.CreateResourceSet(new(layout, batch.Previous, new GpuBufferRange(next, 0, 16)));
        }
        catch { a?.Dispose(); b?.Dispose(); if (grow) next.Dispose(); throw; }
        retired.Retire(firstSet); retired.Retire(secondSet);
        if (grow) { retired.Retire(uniforms); uniforms = next; image = new uint[words]; }
        firstSet = a; secondSet = b; first = batch.Current; second = batch.Previous;
    }

    internal void Build(GpuOutputDescription outputs)
    {
        pipeline ??= device.Factory.CreateGraphicsPipeline(new()
        {
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, true),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [layout],
            ShaderSet = shaders,
            VertexLayouts = [],
            Outputs = outputs,
        });
    }

    internal void Bind(IGpuCommandList commands, IGpuBuffer state, uint offset, int slot)
    {
        var set = ReferenceEquals(first, state) ? firstSet : ReferenceEquals(second, state) ? secondSet : null;
        if (set is null) throw new InvalidOperationException("Reserve transition state before recording.");
        int start = checked(slot * 64);
        image[start] = offset;
        commands.UpdateBuffer(uniforms!, (uint)start * 4, image.AsSpan(start, 4));
        commands.SetPipeline(pipeline!);
        commands.SetGraphicsResourceSet(0, set, (uint)slot * 256);
    }

    public void Dispose()
    {
        pipeline?.Dispose(); shaders.Dispose(); firstSet?.Dispose(); secondSet?.Dispose(); uniforms?.Dispose(); layout.Dispose();
    }
}
