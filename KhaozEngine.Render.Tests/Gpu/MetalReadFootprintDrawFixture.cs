using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Real draw recording with padded mesh and instance streams, without a native device.</summary>
internal sealed class MetalReadFootprintDrawFixture : IDisposable
{
    static readonly MetalShaderIndexTable Table = MetalBindProgram.Table();
    internal readonly MetalRingHarness Harness = new();
    internal readonly MetalCommandList List;
    internal readonly MetalBuffer Vertices, Instances, Uniform, Material, Indices;
    internal readonly MetalBoundSet Set;
    internal readonly MetalGraphicsPipeline Pipeline;

    internal MetalReadFootprintDrawFixture(int draws = 3000)
    {
        Vertices = Harness.NewBuffer(32768, GpuBufferUsage.VertexBuffer);
        Instances = Harness.NewBuffer((uint)(draws * 384 + 1024), GpuBufferUsage.VertexBuffer);
        Uniform = Harness.NewBuffer((uint)(draws * 256 + 256), GpuBufferUsage.UniformBuffer);
        Material = Harness.NewBuffer((uint)(draws * 256 + 256), GpuBufferUsage.UniformBuffer);
        Indices = Harness.NewBuffer(2048, GpuBufferUsage.IndexBuffer);
        Set = MetalBindProgram.Set(
            new MetalBoundResource(MetalIndexSpace.Buffer, Uniform, 0, 64, true),
            new(MetalIndexSpace.Buffer, Material, 0, 32, true));
        var shaders = new MetalShaderSet(Harness.Liveness,
            [new(MetalShaderStage.Vertex, default, default), new(MetalShaderStage.Fragment, default, default)], Table);
        var layouts = new IGpuResourceLayout[Table.Layouts.Count];
        for (int i = 0; i < layouts.Length; i++) layouts[i] = new MetalResourceLayout(Harness.Liveness, Table.Layouts[i]);
        var meshElements = new GpuVertexElement[6];
        for (int i = 0; i < meshElements.Length; i++) meshElements[i] = new("A" + i, GpuVertexElementFormat.Float3);
        var description = new GpuPipelineDescription
        {
            ShaderSet = shaders,
            ResourceLayouts = layouts,
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            VertexLayouts = new List<GpuVertexLayoutDescription>
            {
                new(96, 0, meshElements),
                new(96, 1, [new("I0", GpuVertexElementFormat.Float4), new("I1", GpuVertexElementFormat.Float4)]),
            },
            Outputs = new(null, GpuPixelFormat.B8G8R8A8UNorm),
        };
        Pipeline = new(Harness.Liveness, MetalGraphicsPipelinePlan.Build(Harness.Liveness, description), default, default);
        List = Harness.NewList(new());
    }

    internal void Begin()
    {
        List.Begin();
        List.SetFramebuffer(new RecordedFramebuffer());
        List.SetPipeline(Pipeline);
        List.SetVertexBuffer(0, Vertices);
        List.SetVertexBuffer(1, Instances);
        List.SetIndexBuffer(Indices, GpuIndexFormat.UInt16);
    }

    internal void Draw(uint draw, bool changeUniforms = true, bool advanceInstances = true)
    {
        List.GraphicsBinds.Record(0, Set, changeUniforms ? draw * 256 : 0);
        List.DrawIndexed(1024, 4, 0, 0, advanceInstances ? draw * 4 : 0);
    }

    public void Dispose()
    {
        List.Dispose();
        Harness.Dispose();
    }
}
