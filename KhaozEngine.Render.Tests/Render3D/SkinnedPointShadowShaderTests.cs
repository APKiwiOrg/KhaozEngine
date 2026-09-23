using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedPointShadowShaderTests
{
    [Fact]
    public void BaseOnlyBranchPrecedesEveryTransientRead()
    {
        string source = ShaderSources.LightingCommonGlsl;
        int function = source.IndexOf("float samplePointShadowCombined(", StringComparison.Ordinal);
        Assert.True(function >= 0);
        int gate = source.IndexOf("if (params.w < 0.0)", function, StringComparison.Ordinal);
        int baseReturn = source.IndexOf("return samplePointShadow(baseAtlas, samp", function, StringComparison.Ordinal);
        int combinedCall = source.IndexOf("samplePointShadowHardCombined(", function, StringComparison.Ordinal);
        Assert.True(function < gate && gate < baseReturn && baseReturn < combinedCall);
        int bodyStart = source.IndexOf('{', function) + 1;
        Assert.True(bodyStart > function && bodyStart < gate);
        string beforeGate = source.Substring(bodyStart, gate - bodyStart);
        Assert.DoesNotContain("transientAtlas", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("PointShadowTransientAtlas", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("PointShadowTransientMap", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("samplePointShadowHardCombined", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("samplePointShadowSoftCombined", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("pointShadowCombinedDepthAt", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("pointShadowDepthAtFaceUv", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("textureLod(", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("texture(", beforeGate, StringComparison.Ordinal);
        Assert.DoesNotContain("texelFetch(", beforeGate, StringComparison.Ordinal);
    }

    [Fact]
    public void HardBlockerAndFilterTapsTakeTheNearerStoredDistance()
    {
        string source = ShaderSources.LightingCommonGlsl;
        Assert.Contains("return min(baseStored, transientStored);", source, StringComparison.Ordinal);
        Assert.Contains("samplePointShadowHardCombined", source, StringComparison.Ordinal);
        Assert.Contains("pointShadowBlockerSearchCombined", source, StringComparison.Ordinal);
        Assert.Contains("pointShadowFilterDiscCombined", source, StringComparison.Ordinal);
    }
}
