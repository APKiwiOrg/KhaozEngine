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
        Assert.Contains("int candidateCount = fullPointLightFallback ? npl : int(clusterCount);",
            source, StringComparison.Ordinal);
        Assert.Contains("int lightIndex = fullPointLightFallback ? candidate : clusteredLightIndex",
            source, StringComparison.Ordinal);
        Assert.Contains(
            $"if (clusterCount > {PointLightClusterBuilder.MaxLightsPerCluster}u) fullPointLightFallback = true;",
            source, StringComparison.Ordinal);
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
        Assert.Contains("clusterIndex = uint((tileZ * 9 + tileY) * 16 + tileX);", source, StringComparison.Ordinal);
        Assert.Contains("uvec4 packedHeaders = PointLightClusters[clusterIndex >> 2u];", source,
            StringComparison.Ordinal);
        Assert.Contains($"clusterCount = clusterHeader & {PointLightClusterBuilder.CountMask}u;", source,
            StringComparison.Ordinal);
        Assert.Contains($"clusterOffset = clusterHeader >> {PointLightClusterBuilder.CountBits}u;", source,
            StringComparison.Ordinal);
        Assert.Contains($"PointLightClusters[{PointLightClusterBuilder.HeaderRegionUvec4s}u + (indexSlot >> 2u)]",
            source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClusterLoopKeepsItsVaryingCountFormForDirect3D()
    {
        // STABLE-POINT-LIGHTING-2026-09-19.md: the count varies per fragment and every atlas read inside the loop uses
        // explicit mip zero, so FXC never attempts an unbounded unroll.
        Assert.Contains("for (int candidate = 0; candidate < candidateCount; candidate++) {",
            ShaderSources.LightingCommonGlsl, StringComparison.Ordinal);
    }
}
