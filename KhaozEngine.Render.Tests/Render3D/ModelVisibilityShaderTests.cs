using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ModelVisibilityShaderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SelectInvokesFallbackFactoryOnlyWithoutVisibility(bool motion, bool visibility)
    {
        using var device = new FakeGpuDevice();
        using var shaders = new ModelVisibilityShaders();
        using var fallback = new FakeShaderSet("fallback vertex", "fallback fragment");
        string fragment = motion ? ShaderSources.SilhouetteMotionFrag : ShaderSources.SilhouetteFrag;
        int fallbackCalls = 0;

        IGpuShaderSet selected = shaders.Select(device.Factory, TemporalVisibilityLayoutTests.Layout(motion, visibility),
            ShaderSources.SilhouetteVert, fragment, () =>
            {
                fallbackCalls++;
                return fallback;
            }, preserve: true);

        Assert.Equal(visibility ? 0 : 1, fallbackCalls);
        if (visibility) Assert.NotSame(fallback, selected);
        else Assert.Same(fallback, selected);
    }

    [Fact]
    public void SelectingFallbackDisposesCachedVisibilityShadersButLeavesFallbackWithItsOwner()
    {
        using var device = new FakeGpuDevice();
        using var shaders = new ModelVisibilityShaders();
        using var fallback = new FakeShaderSet("fallback vertex", "fallback fragment");
        IGpuShaderSet Select(bool visibility) => shaders.Select(device.Factory,
            TemporalVisibilityLayoutTests.Layout(motion: true, visibility), ShaderSources.SilhouetteVert,
            ShaderSources.SilhouetteMotionFrag, () => fallback, preserve: true);

        var visibilityShaders = Assert.IsType<FakeShaderSet>(Select(visibility: true));
        Assert.Same(visibilityShaders, Select(visibility: true));
        Assert.False(visibilityShaders.Disposed);
        Assert.Same(fallback, Select(visibility: false));
        Assert.True(visibilityShaders.Disposed);
        shaders.Dispose();
        Assert.False(fallback.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransparentRenderersOnlyHoldMotionFallbackWithoutVisibility(bool visibility)
    {
        using var device = new FakeGpuDevice();
        GpuOutputDescription outputs = TemporalVisibilityLayoutTests.Layout(motion: true, visibility);
        using var silhouettes = new SilhouetteRenderer(device, outputs);
        using var billboards = new TexturedBillboardRenderer(device, outputs);
        using var overlays = new OverlayMeshRenderer(device, outputs);
        using var trails = new TrailRenderer(device, outputs);
        using var beams = new BeamRenderer(device, outputs);

        Assert.Equal(!visibility, silhouettes.HoldsMotionShadersForTests);
        Assert.Equal(!visibility, billboards.HoldsMotionShadersForTests);
        Assert.Equal(!visibility, overlays.HoldsMotionShadersForTests);
        Assert.Equal(!visibility, trails.HoldsMotionShadersForTests);
        Assert.Equal(!visibility, beams.HoldsMotionShadersForTests);
    }
}
