using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageGraphicsProgramTests
{
    public static IEnumerable<object[]> Programs()
    {
        yield return ["main", ShaderSources.CoverageModelVert, ShaderSources.SkinnedModelFrag];
        yield return ["dissolve", ShaderSources.CoverageModelVert, ShaderSources.SkinnedModelDissolveFrag];
        yield return ["motion", ShaderSources.CoverageModelMotionVert, ShaderSources.SkinnedModelMotionFrag];
        yield return ["motion-dissolve", ShaderSources.CoverageModelMotionVert, ShaderSources.SkinnedModelDissolveMotionFrag];
        yield return ["cascade", ShaderSources.CoverageCascadeVert, ShaderSources.ShadowDepthFrag];
        yield return ["cascade-dissolve", ShaderSources.CoverageCascadeDissolveVert, ShaderSources.SkinnedShadowDepthDissolveFrag];
        yield return ["cascade-complement", ShaderSources.CoverageCascadeDissolveVert, ShaderSources.SkinnedShadowDepthDissolveInvertedFrag];
        yield return ["point", ShaderSources.CoveragePointVert, ShaderSources.PointShadowRigidFrag];
        yield return ["point-dissolve", ShaderSources.CoveragePointDissolveVert, ShaderSources.PointShadowSkinnedDissolveFrag];
        yield return ["point-complement", ShaderSources.CoveragePointDissolveVert, ShaderSources.PointShadowSkinnedDissolveInvertedFrag];
        yield return ["outline-full", ShaderSources.CoverageOutlineVert, ShaderSources.TargetOutlineFullMaskFrag];
        yield return ["outline-visible", ShaderSources.CoverageOutlineVert, ShaderSources.TargetOutlineVisibleMaskFrag];
    }

    [Theory]
    [MemberData(nameof(Programs))]
    public void Shared_surface_programs_link_and_cross_compile_with_precise_vertices(string name, string vertex, string fragment)
    {
        var precise = new GpuShaderCompileOptions(PreciseMath: true);
        ShaderValidation.ValidatePair(vertex, fragment, precise, name);
        byte[] v = SpirvFrontEnd.ToSpirv(vertex, GpuShaderStages.Vertex, name, precise);
        byte[] f = SpirvFrontEnd.ToSpirv(fragment, GpuShaderStages.Fragment, name);
        Assert.NotEmpty(SpirvCrossCompile.VertexFragmentToHlsl(v, f, vertexOptions: precise).VertexSource);
        Assert.NotEmpty(SpirvCrossCompile.VertexFragmentToMsl(v, f, vertexOptions: precise).VertexSource);
    }
}
