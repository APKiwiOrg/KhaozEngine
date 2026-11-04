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
    internal readonly MetalGraphicsPipeline[] PassPipelines;
    internal readonly MetalBuffer[] InstanceStreams;
    internal readonly MetalBuffer[] Meshes;
    internal readonly MetalBoundSet[] StaticSets;
    readonly uint _uniformStep;

    internal MetalReadFootprintDrawFixture(int draws = 3000, bool freshSlices = false, bool multiPass = false)
    {
        _uniformStep = freshSlices ? 512u : 256u;
        Vertices = Harness.NewBuffer(32768, GpuBufferUsage.VertexBuffer);
        Meshes = [Vertices, Harness.NewBuffer(32768, GpuBufferUsage.VertexBuffer),
            Harness.NewBuffer(32768, GpuBufferUsage.VertexBuffer), Harness.NewBuffer(32768, GpuBufferUsage.VertexBuffer)];
        Instances = Harness.NewBuffer((uint)(draws * 384 + 1024), GpuBufferUsage.VertexBuffer);
        InstanceStreams = multiPass ? [Instances,
            Harness.NewBuffer(262144, GpuBufferUsage.VertexBuffer),
            Harness.NewBuffer(262144, GpuBufferUsage.VertexBuffer)] : [Instances];
        Uniform = Harness.NewBuffer((uint)draws * _uniformStep + 256, GpuBufferUsage.UniformBuffer);
        Material = Harness.NewBuffer((uint)draws * _uniformStep + 256, GpuBufferUsage.UniformBuffer);
        Indices = Harness.NewBuffer(2048, GpuBufferUsage.IndexBuffer);
        Set = MetalBindProgram.Set(
            new MetalBoundResource(MetalIndexSpace.Buffer, Uniform, 0, 64, true),
            new(MetalIndexSpace.Buffer, Material, 0, freshSlices ? 256u : 32u, true));
        var table = Table;
        StaticSets = [];
        if (freshSlices)
        {
            var staticLayout = new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Static", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex));
            table = MetalShaderIndexTable.Build([Table.Layouts[0], staticLayout],
                [new(MetalShaderStage.Vertex, [new(0, 0), new(1, 0)]),
                    new(MetalShaderStage.Fragment, [new(0, 0), new(0, 1)])], "fresh ring slices");
            var buffer = Harness.NewBuffer(1024, GpuBufferUsage.UniformBuffer);
            StaticSets = [MetalBindProgram.Set(new MetalBoundResource(MetalIndexSpace.Buffer, buffer, 0, 64, false)),
                MetalBindProgram.Set(new MetalBoundResource(MetalIndexSpace.Buffer, buffer, 256, 64, false))];
        }
        var shaders = new MetalShaderSet(Harness.Liveness,
            [new(MetalShaderStage.Vertex, default, default), new(MetalShaderStage.Fragment, default, default)], table);
        var layouts = new IGpuResourceLayout[table.Layouts.Count];
        for (int i = 0; i < layouts.Length; i++) layouts[i] = new MetalResourceLayout(Harness.Liveness, table.Layouts[i]);
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
        if (multiPass)
            description.VertexLayouts.Add(new(32, 0, [new("Previous", GpuVertexElementFormat.Float3)]));
        Pipeline = new(Harness.Liveness, MetalGraphicsPipelinePlan.Build(Harness.Liveness, description), default, default);
        PassPipelines = multiPass ? [Pipeline,
            new(Harness.Liveness, MetalGraphicsPipelinePlan.Build(Harness.Liveness, description), default, default),
            new(Harness.Liveness, MetalGraphicsPipelinePlan.Build(Harness.Liveness, description), default, default),
            new(Harness.Liveness, MetalGraphicsPipelinePlan.Build(Harness.Liveness, description), default, default)] : [Pipeline];
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
        if (PassPipelines.Length > 1) List.SetVertexBuffer(2, Meshes[3]);
    }

    internal void Draw(uint draw, bool changeUniforms = true, bool advanceInstances = true)
    {
        if (StaticSets.Length != 0)
        {
            List.SetVertexBuffer(0, Meshes[draw / 750 % (uint)Meshes.Length]);
            List.GraphicsBinds.Record(1, StaticSets[draw / 500 % (uint)StaticSets.Length], 0);
        }
        List.GraphicsBinds.Record(0, Set, changeUniforms ? draw * _uniformStep : 0);
        List.DrawIndexed(1024, 4, 0, 0, advanceInstances ? draw * 4 : 0);
    }

    public void Dispose()
    {
        List.Dispose();
        Harness.Dispose();
    }
}
