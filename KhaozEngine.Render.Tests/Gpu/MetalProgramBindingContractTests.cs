using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalProgramBindingContractTests
{
    [Fact]
    public void A_resource_free_program_requires_an_empty_table()
    {
        MetalShaderIndexTable empty = MetalShaderIndexTable.Build([], Uses([], []), "resource-free");
        MetalProgramBindingContract.Require(empty, []);
    }

    [Fact]
    public void A_nonempty_table_still_fails_when_a_declared_binding_is_missing()
    {
        GpuResourceLayoutDescription[] layouts = [new(
            new("Frame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex),
            new("Color", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment))];
        MetalShaderIndexTable dropped = MetalShaderIndexTable.Build(layouts, Uses([new(0, 0)], []), "dropped");
        Assert.Equal(1, dropped.Count);
        Assert.ThrowsAny<XunitException>(() => MetalProgramBindingContract.Require(dropped, layouts));
    }

    [Fact]
    public void Every_shipped_program_retains_its_declared_binding_contract()
    {
        foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
        {
            MetalMslProgram metal = MetalShaderBuild.Pair(program.VertexGlsl, program.FragmentGlsl, null, program.Name);
            GpuResourceLayoutDescription[] declared = SpirvCrossCompile.GlslPairToHlsl(
                program.VertexGlsl, program.FragmentGlsl, program.Name).Reflection.ResourceLayouts;
            MetalProgramBindingContract.Require(metal.Table, declared);
        }
    }

    static MetalStageResourceUse[] Uses(MslResourceRef[] vertex, MslResourceRef[] fragment)
        => [new(MetalShaderStage.Vertex, vertex), new(MetalShaderStage.Fragment, fragment)];
}
