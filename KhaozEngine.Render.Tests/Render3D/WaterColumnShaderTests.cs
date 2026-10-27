using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterColumnShaderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BothOpticsAndVertexVariantsCompileWithLiveColourBindings(bool triangle, bool clipmap)
    {
        string fragment = triangle ? ShaderSources.WaterTriangleFrag : ShaderSources.WaterFrag;
        string vertex = clipmap ? ShaderSources.WaterClipmapVert : ShaderSources.WaterVert;
        var pair = SpirvCrossCompile.GlslPairToHlsl(vertex, fragment);
        var elements = Assert.Single(pair.Reflection.ResourceLayouts).Elements;
        Assert.Equal(17, elements.Length);
        Assert.Equal(GpuResourceKind.TextureReadOnly, elements[15].Kind);
        Assert.Equal(GpuResourceKind.Sampler, elements[16].Kind);
        Assert.All(elements[15..], e => Assert.Equal(GpuShaderStages.Fragment, e.Stages));
        Assert.DoesNotContain(elements, e => e.Kind == GpuResourceKind.StructuredBufferReadOnly);
        ShaderValidation.ValidatePair(vertex, fragment, "column composite");
        Assert.Contains(WaterColumnShaderSource.Glsl, fragment, StringComparison.Ordinal);
        Assert.Equal(1, fragment.Split("= 1.333;", StringSplitOptions.None).Length - 1);
        Assert.Contains("for (int i = 0; i < 4; i++)", WaterColumnShaderSource.Glsl, StringComparison.Ordinal);
        Assert.Equal(1, WaterColumnShaderSource.Glsl.Split("waterColumnEntryVisibility(E)", StringSplitOptions.None).Length - 1);
        Assert.Contains("texelFetch(sampler2D(ColumnSceneColor, ColumnColorSampler), ivec2(gl_FragCoord.xy), 0)", fragment, StringComparison.Ordinal);
        Assert.Contains("oColor = vec4(waterColumnComposite(", fragment, StringComparison.Ordinal);
        Assert.Contains("glint, foam, shoreFade), 1.0);", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("atmosphereFog(", WaterColumnShaderSource.Glsl, StringComparison.Ordinal);
        foreach (string legacy in new[] { "ShallowColor", "DeepColor", "Absorption", "ShoreGlint.w", "DetailParams.w" })
            Assert.DoesNotContain(legacy, WaterColumnShaderSource.Glsl, StringComparison.Ordinal);
        Assert.Contains("V = -waterColumnIncident(ndc.xy)", fragment, StringComparison.Ordinal);
        Assert.Contains("E.y = max(X.y, SurfShape.z)", fragment, StringComparison.Ordinal);
        // The far reconstruction supplies a finite path. Its numerical cap must not replace a real bed depth.
        Assert.DoesNotContain("return limit;", WaterColumnShaderSource.Glsl, StringComparison.Ordinal);
        Assert.Contains("return max(bedDepth, 0.0);", WaterColumnShaderSource.Glsl, StringComparison.Ordinal);
    }

    [Fact]
    public void ColourSourceIdentityAndGenerationInvalidatePreparedSets()
    {
        using var device = new FakeGpuDevice();
        using var targets = new RenderResources(device, 96, 64, false);
        using var retired = GpuRetireQueue.CreateFrameCounted(device, GpuRetireQueue.DefaultFrameDelay);
        using var renderer = new WaterRenderer(device, targets.ColorDepthFB.Outputs, retired);
        using var source = new WaterColumnColorSource(device, retired);
        var settings = new WaterSettings { WaterColumnLight = true, SwellAmplitude = 0f };
        WaterPlane[] planes = [new(0f, 0f, 0f, 5f)];
        var frame = new FramePrepare(settings, planes, 0f);
        source.Prepare(targets, true);
        renderer.ColumnShadows.SetColor(source.Texture, source.Generation);
        renderer.PrepareFrame(frame);
        renderer.PrepareOpticsFrame(targets, frame);
        var factory = (FakeGpuResourceFactory)device.Factory;
        FakeResourceSet first = factory.ResourceSets.Last(s => s.Resources.Length == 17);
        Assert.Same(source.Texture, first.Resources[15]);
        Assert.Equal("ColumnSceneColor", ((FakeResourceLayout)first.Layout!).Description.Elements[15].Name);
        Assert.Equal("ColumnColorSampler", ((FakeResourceLayout)first.Layout!).Description.Elements[16].Name);
        renderer.ColumnShadows.SetColor(source.Texture, source.Generation + 1);
        Assert.Throws<InvalidOperationException>(() => renderer.VerifyOpticsFrame(targets, planes, settings, 0f));
        renderer.PrepareOpticsFrame(targets, frame);
        FakeResourceSet second = factory.ResourceSets.Last(s => s.Resources.Length == 17);
        Assert.NotSame(first, second);
        source.Prepare(targets, false);
        renderer.ColumnShadows.SetColor(source.Texture, source.Generation);
        Assert.Throws<InvalidOperationException>(() => renderer.VerifyOpticsFrame(targets, planes, settings, 0f));
        renderer.PrepareOpticsFrame(targets, frame);
        Assert.Same(source.Texture, factory.ResourceSets.Last(s => s.Resources.Length == 17).Resources[15]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PhaseUsesLatchedFrameOnlyForTheRenderThatResolves(bool temporal, bool motionOnly)
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        scene.Post.Water.WaterColumnLight = true;
        scene.Post.Water.SwellAmplitude = 0f;
        scene.Post.Quality.AntiAliasing = temporal ? AntiAliasing.Temporal : AntiAliasing.Off;
        scene.ForceTemporalForTests = motionOnly;
        scene.Begin();
        scene.DrawWater(new WaterPlane(0f, 0f, 0f, 100f));
        scene.PrepareFrame(96, 64);
        using IGpuFramebuffer target = device.Factory.CreateFramebuffer(null, scene.ResolvedDepthTarget);
        scene.RenderInternal(commands, 96, 64, target);
        AssertPhase(temporal ? (float)(scene.CurrentFrameView.FrameIndex % 64) : 0f);

        void AssertPhase(float expected)
        {
            var upload = Assert.Single(commands.Uploads, u => u.Data is { Length: >= 1136 }
                && u.Buffer is FakeBuffer b && b.Usage.HasFlag(GpuBufferUsage.UniformBuffer)
                && u.Data.Length % 1280 == 0);
            Assert.Equal(expected, MemoryMarshal.Read<Vector4>(upload.Data!.AsSpan(1120)).W);
        }
    }

    [Theory]
    [InlineData(0L, 0u)]
    [InlineData(63L, 63u)]
    [InlineData(64L, 0u)]
    [InlineData(129L, 1u)]
    public void NoisePhaseWrapsAndUnresolvedRendersKeepThePatternFixed(long frame, uint expected)
    {
        Assert.Equal(expected, WaterColumnPacking.NoisePhase(frame, true));
        Assert.Equal(0u, WaterColumnPacking.NoisePhase(frame, false));
    }
}
