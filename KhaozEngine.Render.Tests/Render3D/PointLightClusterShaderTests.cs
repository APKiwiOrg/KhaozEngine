using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class PointLightClusterShaderTests
{
    [Fact]
    public void EveryLitFragmentReadsTheSharedClusterBufferAtSetZeroBindingTwo()
    {
        const string declaration =
            "layout(std430, set=0, binding=2) readonly buffer PointLightClusterBuffer";
        foreach ((string name, string source) in new[]
        {
            ("ModelFrag", ShaderSources.ModelFrag),
            ("ModelDissolveFrag", ShaderSources.ModelDissolveFrag),
            ("SkinnedModelFrag", ShaderSources.SkinnedModelFrag),
            ("SkinnedModelDissolveFrag", ShaderSources.SkinnedModelDissolveFrag),
            ("SplatFrag", ShaderSources.SplatFrag),
            ("TileGroundFrag", ShaderSources.TileGroundFrag),
        })
            Assert.Contains(declaration, source, StringComparison.Ordinal);

        Assert.DoesNotContain("PointLightClusterBuffer", ShaderSources.ModelVert, StringComparison.Ordinal);
        Assert.DoesNotContain("PointLightClusterBuffer", ShaderSources.SkinnedModelVert, StringComparison.Ordinal);
        Assert.DoesNotContain("PointLightClusterBuffer", ShaderSources.SplatVert, StringComparison.Ordinal);
        Assert.DoesNotContain("PointLightClusterBuffer", ShaderSources.TileGroundVert, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroLightsReturnBeforeReadingAStaleClusterAndFallbackWalksTheFullQueue()
    {
        string source = ShaderSources.LightingCommonGlsl;
        int zeroReturn = source.IndexOf("if (npl <= 0) return;", StringComparison.Ordinal);
        int clusterRead = source.IndexOf("PointLightClusters[", StringComparison.Ordinal);
        Assert.True(zeroReturn >= 0, "zero lights need an explicit early return");
        Assert.True(clusterRead > zeroReturn, "zero lights must return before the cluster buffer is read");
        Assert.Contains("int candidateCount = fullPointLightFallback ? npl : int(clusterHeader.x);",
            source, StringComparison.Ordinal);
        Assert.Contains("int lightIndex = fullPointLightFallback ? candidate : clusteredLightIndex",
            source, StringComparison.Ordinal);
        Assert.Contains("clusterHeader.y != 0u", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClusterLookupClampsSignedTilesAndFallsBackOutsideTheConservativeDepthDomain()
    {
        string source = ShaderSources.LightingCommonGlsl;
        Assert.Contains("int tileX = clamp(", source, StringComparison.Ordinal);
        Assert.Contains("int tileY = clamp(", source, StringComparison.Ordinal);
        Assert.Contains("int tileZ = clamp(", source, StringComparison.Ordinal);
        Assert.Contains("1e-4 * max(1.0, max(abs(ClusterDepth.x), abs(ClusterDepth.y)))",
            source, StringComparison.Ordinal);
        Assert.Contains("clip.w <= 0.0", source, StringComparison.Ordinal);
        Assert.Contains("isnan", source, StringComparison.Ordinal);
        Assert.Contains("isinf", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderGridConstantsMatchTheCpuPackedImage()
    {
        string source = ShaderSources.LightingCommonGlsl;
        Assert.Contains($"* {PointLightClusterBuilder.ClusterCountX}.0", source, StringComparison.Ordinal);
        Assert.Contains($"* {PointLightClusterBuilder.ClusterCountY}.0", source, StringComparison.Ordinal);
        Assert.Contains($"* {PointLightClusterBuilder.ClusterCountZ}.0", source, StringComparison.Ordinal);
        Assert.Contains($"cluster * {PointLightClusterBuilder.ClusterStrideUInts / 4}", source,
            StringComparison.Ordinal);
        Assert.Contains($"clusterHeader.x > {PointLightClusterBuilder.MaxLightsPerCluster}u", source,
            StringComparison.Ordinal);
    }
}
