using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The exact stage bindings and per-kind indices implied by an independently reflected program.</summary>
internal static class MetalProgramBindingContract
{
    internal static void Require(MetalShaderIndexTable table, GpuResourceLayoutDescription[] layouts)
    {
        var expected = new List<KeyValuePair<MetalIndexTableKey, MetalIndexTableEntry>>();
        var next = new int[3];
        for (int set = 0; set < layouts.Length; set++)
        {
            GpuResourceLayoutElement[] elements = layouts[set].Elements;
            for (int binding = 0; binding < elements.Length; binding++)
            {
                GpuResourceLayoutElement element = elements[binding];
                MetalIndexSpace space = element.Kind switch
                {
                    GpuResourceKind.UniformBuffer or GpuResourceKind.StructuredBufferReadOnly
                        or GpuResourceKind.StructuredBufferReadWrite => MetalIndexSpace.Buffer,
                    GpuResourceKind.TextureReadOnly or GpuResourceKind.TextureReadWrite => MetalIndexSpace.Texture,
                    GpuResourceKind.Sampler => MetalIndexSpace.Sampler,
                    _ => throw new ArgumentOutOfRangeException(nameof(layouts), element.Kind, "Unknown resource kind."),
                };
                var index = new MetalIndexTableEntry(space, next[(int)space]++);
                if ((element.Stages & GpuShaderStages.Vertex) != 0)
                    expected.Add(new(new(set, binding, MetalShaderStage.Vertex), index));
                if ((element.Stages & GpuShaderStages.Fragment) != 0)
                    expected.Add(new(new(set, binding, MetalShaderStage.Fragment), index));
            }
        }
        Assert.Equal(expected, table.Entries());
    }
}
