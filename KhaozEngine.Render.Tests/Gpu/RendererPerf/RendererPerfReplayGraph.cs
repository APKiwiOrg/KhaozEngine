using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfReplayGraph
{
    internal static object Create(RendererPerfCompilerInventory inventory, RendererPerfPipelineRecipe recipe,
        IGpuResourceFactory factory, List<(string Kind, IDisposable Resource)> owned, RendererPerfUnitBudget budget)
    {
        T Create<T>(string kind, Func<T> create) where T : IDisposable
        {
            budget.Check("renderer.graph." + kind + ".before");
            T resource = create(); owned.Add((kind, resource));
            budget.Check("renderer.graph." + kind + ".after");
            return resource;
        }
        var sources = inventory.Shaders[recipe.ShaderId].Stages;
        IDisposable shader = recipe.Kind == RendererPerfPipelineKind.Compute
            ? Create("shader", () => factory.CreateComputeShaderFromSpirv(sources[0].Source))
            : Create("shader", () => factory.CreateShadersFromSpirv(sources[0].Source, sources[1].Source));
        var layouts = recipe.LayoutIds.Select(id => Create("layout", () => factory.CreateResourceLayout(
            new GpuResourceLayoutDescription(inventory.Layouts[id].Elements.ToArray())))).ToArray();
        if (recipe.Kind == RendererPerfPipelineKind.Compute)
            return Create("pipeline", () => factory.CreateComputePipeline(new((IGpuComputeShader)shader, layouts)));
        var g = recipe.Graphics ?? throw new InvalidOperationException("Graphics recipe omitted state.");
        return Create("pipeline", () => factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            ShaderSet = (IGpuShaderSet)shader,
            ResourceLayouts = layouts,
            BlendFactor = g.BlendFactor,
            BlendAttachments = g.BlendAttachments.ToArray(),
            DepthStencil = g.DepthStencil,
            Rasterizer = g.Rasterizer,
            Topology = g.Topology,
            VertexLayouts = g.VertexLayouts.Select(v => new GpuVertexLayoutDescription(v.Stride, v.InstanceStepRate, v.Elements.ToArray())).ToList(),
            Outputs = new GpuOutputDescription(g.Depth, g.Colour.ToArray()).WithSampleCount(g.SampleCount)
        }));
    }
}
