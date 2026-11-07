using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterCausticShaderTests
{
    const GpuShaderStages V = GpuShaderStages.Vertex;
    const GpuShaderStages VF = GpuShaderStages.Vertex | GpuShaderStages.Fragment;

    public static TheoryData<string, GpuResourceKind[], GpuShaderStages[], GpuVertexElementFormat[]> Programs() => new()
    {
        { "WaterSurfaceField", [GpuResourceKind.UniformBuffer], [VF], [] },
        { "WaterCausticSubtract", [GpuResourceKind.UniformBuffer], [V], [] },
        {
            "WaterCausticSheet", [GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly, GpuResourceKind.Sampler],
            [V, V, V], [GpuVertexElementFormat.Float3]
        },
    };

    /// <summary>The caustic pipeline layouts rest on these reflected shapes, on every path the backends compile through.</summary>
    [Theory]
    [MemberData(nameof(Programs))]
    public void Every_caustic_program_compiles_on_every_backend_path(string name, GpuResourceKind[] kinds,
        GpuShaderStages[] stages, GpuVertexElementFormat[] inputs)
    {
        ShippedGraphicsProgram program = ShippedShaderPrograms.GraphicsPrograms().Single(p => p.Name == name);
        ShaderValidation.ValidatePair(program.VertexGlsl, program.FragmentGlsl, name);
        byte[] vertex = SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, name);
        byte[] fragment = SpirvFrontEnd.ToSpirv(program.FragmentGlsl, GpuShaderStages.Fragment, name);

        foreach (CrossCompiledPair pair in new[]
                 {
                     SpirvCrossCompile.VertexFragmentToHlsl(vertex, fragment, name),
                     SpirvCrossCompile.VertexFragmentToMsl(vertex, fragment, name),
                 })
        {
            GpuResourceLayoutElement[] elements = Assert.Single(pair.Reflection.ResourceLayouts).Elements;
            Assert.Equal(kinds, elements.Select(e => e.Kind));
            Assert.Equal(stages, elements.Select(e => e.Stages));
            Assert.Equal(inputs, pair.Reflection.VertexElements.Select(e => e.Format));
        }
    }
}
