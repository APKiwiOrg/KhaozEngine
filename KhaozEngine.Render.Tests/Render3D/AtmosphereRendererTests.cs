using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("NativeDeviceLifecycle")]
public sealed class AtmosphereRendererTests
{
    [Theory]
    [InlineData("Transmittance")]
    [InlineData("MultiScattering")]
    [InlineData("SkyView")]
    public void ProducerShaders_ReflectExactDenseFragmentBindings(string pass)
    {
        var (source, kinds) = pass switch
        {
            "Transmittance" => (ShaderSources.AtmosphereTransmittanceFrag,
                new[] { GpuResourceKind.UniformBuffer }),
            "MultiScattering" => (ShaderSources.AtmosphereMultiScatteringFrag,
                new[] { GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly, GpuResourceKind.Sampler }),
            "SkyView" => (ShaderSources.AtmosphereSkyViewFrag,
                new[] { GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly,
                    GpuResourceKind.TextureReadOnly, GpuResourceKind.Sampler }),
            _ => throw new ArgumentOutOfRangeException(nameof(pass)),
        };
        var pair = SpirvCrossCompile.GlslPairToHlsl(ShaderSources.FullscreenVert, source);
        var elements = Assert.Single(pair.Reflection.ResourceLayouts).Elements;
        Assert.Equal(kinds, elements.Select(e => e.Kind));
        Assert.All(elements, e => Assert.Equal(GpuShaderStages.Fragment, e.Stages));
        // Metal joins the original SPIR-V binding decorations to the dense reflected layout.
        // This also rejects a sampler declared at 3 when its actual dense position must be 2.
        var metal = MetalShaderBuild.Pair(ShaderSources.FullscreenVert, source, null, "Atmosphere" + pass);
        metal.Table.RequireLayoutShape(pair.Reflection.ResourceLayouts, "Atmosphere" + pass);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(11)]
    public void ConsumerShaders_ReflectTheDenseBindingSuffix(int count)
    {
        string bindings = ConsumerBindings(count);
        string prefix = string.Concat(Enumerable.Range(0, count).Select(i =>
            $"layout(set=0,binding={i}) uniform Prefix{i} {{ vec4 value; }} prefix{i};\n"));
        string livePrefix = string.Join(" + ", Enumerable.Range(0, count).Select(i => $"prefix{i}.value"));
        string fragment = "#version 450\n" + prefix + bindings
            + "layout(location=0) in vec2 vUv;\nlayout(location=0) out vec4 oColor;\n"
            + "void main() { oColor = texture(sampler2D(AtmoSkyView, AtmoSkySampler), vUv)"
            + " * Atmo.Tint.w + (" + livePrefix + "); }";
        var pair = SpirvCrossCompile.GlslPairToHlsl(ShaderSources.FullscreenVert, fragment);
        var elements = Assert.Single(pair.Reflection.ResourceLayouts).Elements;
        Assert.Equal(count + 3, elements.Length);
        Assert.Equal(new[] { GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly,
            GpuResourceKind.Sampler }, elements.Skip(count).Select(e => e.Kind));
        var layout = AtmosphereBindings.AppendLayout(Enumerable.Range(0, count).Select(i =>
            new GpuResourceLayoutElement("Prefix" + i, GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)).ToArray());
        Assert.Equal(layout.Select(e => e.Kind), elements.Select(e => e.Kind));
        Assert.All(elements, e => Assert.Equal(GpuShaderStages.Fragment, e.Stages));
        var metal = MetalShaderBuild.Pair(ShaderSources.FullscreenVert, fragment, null, "AtmosphereConsumer" + count);
        metal.Table.RequireLayoutShape(new[] { new GpuResourceLayoutDescription(layout) }, "AtmosphereConsumer" + count);
    }

    static string ConsumerBindings(int count) => count switch
    {
        1 => ShaderSources.AtmosphereBindingsAt1Glsl,
        3 => ShaderSources.AtmosphereBindingsAt3Glsl,
        4 => ShaderSources.AtmosphereBindingsAt4Glsl,
        6 => ShaderSources.AtmosphereBindingsAt6Glsl,
        7 => ShaderSources.AtmosphereBindingsAt7Glsl,
        11 => ShaderSources.AtmosphereBindingsAt11Glsl,
        _ => throw new ArgumentOutOfRangeException(nameof(count)),
    };

    [GpuFact]
    public void Constructor_CreatesAllThreeProducerPipelinesOnHeadlessMetal()
    {
        if (!KhaozEngineMetal.IsPlatformSupported) return;
        using var device = new MetalBackendProvider().CreateHeadless().Device;
        using var renderer = new AtmosphereRenderer(device);
        Assert.Equal(304u, renderer.Bindings.Uniforms.SizeInBytes);
        Assert.Null(renderer.TransmittanceTexture);
        Assert.Null(renderer.MultiScatteringTexture);
        Assert.Equal(0, renderer.StaticBuildCount);
        Assert.Equal(0, renderer.SkyViewDrawCount);
    }

    internal static void VerifyUboLayout()
    {
        string[] names = { "Planet", "Rayleigh", "Mie", "Ozone", "GroundHaze", "Sun", "Moon", "Tint",
                "Floors", "Camera", "Origin", "SunT", "MoonT", "Right", "Up", "Back", "Ray", "Res", "Tone" };
        Assert.Equal(304, Marshal.SizeOf<AtmosphereRenderer.AtmosphereUbo>());
        for (int i = 0; i < names.Length; i++)
            Assert.Equal(i * 16, (int)Marshal.OffsetOf<AtmosphereRenderer.AtmosphereUbo>(names[i]));
        foreach (string source in new[] { ConsumerBindings(1), ConsumerBindings(3), ConsumerBindings(4),
                ConsumerBindings(6), ConsumerBindings(7), ConsumerBindings(11),
                ShaderSources.AtmosphereTransmittanceFrag, ShaderSources.AtmosphereMultiScatteringFrag,
                ShaderSources.AtmosphereSkyViewFrag })
        {
            int previous = -1;
            foreach (string name in names)
            {
                int offset = source.IndexOf("vec4 " + name + ";", StringComparison.Ordinal);
                Assert.True(offset > previous, $"Atmosphere UBO lost or reordered {name}.");
                previous = offset;
            }
        }
    }


    [Fact]
    public void ToggleAndArtChanges_DoNotRebakeStaticLuts()
    {
        var s = AtmosphereSettings.Earth();
        var policy = new AtmosphereUpdatePolicy();
        Assert.Equal((false, false), policy.Decide(s));
        s.Enabled = true;
        Assert.Equal((true, true), policy.Decide(s));
        Assert.Equal((false, true), policy.Decide(s));
        s.NightTint = Vector3.One;
        s.WorldUnitsPerKilometre = 10f;
        s.AerialDistanceScale = 3f;
        s.HazeDensityPerKm = .2f;
        s.HazeScaleHeightKm = 1f;
        s.SunIlluminance = 2f;
        Assert.Equal((false, true), policy.Decide(s));
        s.Enabled = false;
        Assert.Equal((false, false), policy.Decide(s));
        s.Enabled = true;
        Assert.Equal((false, true), policy.Decide(s));
        s.MieExtinctionPerKm *= 2;
        Assert.Equal((true, true), policy.Decide(s));
        Assert.Equal((false, true), policy.Decide(s.Clone()));
    }

    [Fact]
    public void PhysicalChangesWhileDisabled_RebuildOnNextEnable()
    {
        var s = AtmosphereSettings.Earth();
        var policy = new AtmosphereUpdatePolicy();
        s.Enabled = true;
        Assert.Equal((true, true), policy.Decide(s));
        s.Enabled = false;
        s.GroundAlbedo = .5f;
        Assert.Equal((false, false), policy.Decide(s));
        s.Enabled = true;
        Assert.Equal((true, true), policy.Decide(s));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(11)]
    public void BindingSuffix_PreservesExistingSlots(int count)
    {
        var prefix = Enumerable.Range(0, count).Select(i => new GpuResourceLayoutElement(
            "Existing" + i, GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)).ToArray();
        var result = AtmosphereBindings.AppendLayout(prefix);
        Assert.Equal(count + 3, result.Length);
        Assert.Equal(prefix, result.Take(count));
        Assert.Equal(GpuResourceKind.UniformBuffer, result[count].Kind);
        Assert.Equal(GpuResourceKind.TextureReadOnly, result[count + 1].Kind);
        Assert.Equal(GpuResourceKind.Sampler, result[count + 2].Kind);
        Assert.All(result.Skip(count), e => Assert.Equal(GpuShaderStages.Fragment, e.Stages));

        using var device = new FakeGpuDevice();
        using var bindings = new AtmosphereBindings(device);
        IGpuBindableResource[] resources = Enumerable.Range(0, count).Select(_ =>
            (IGpuBindableResource)device.PointSampler).ToArray();
        var appended = bindings.AppendResources(resources);
        Assert.Equal(count + 3, appended.Length);
        Assert.Equal(resources, appended.Take(count));
        Assert.Same(bindings.Uniforms, appended[count]);
        Assert.Same(bindings.SkyView, appended[count + 1]);
        Assert.Same(bindings.SkySampler, appended[count + 2]);
        Assert.Single(((FakeGpuResourceFactory)device.Factory).Textures);
    }

    [Fact]
    public void PackUbo_UsesAbsoluteHeightAndUnscaledCameraRays()
    {
        var s = AtmosphereSettings.Earth();
        s.Enabled = true;
        s.AerialDistanceScale = 7f;
        var frame = new AtmosphereFrame(Vector3.UnitY, -Vector3.UnitY, .03f, new(.8f, .9f, 1f),
            SunOpacity: .6f, MoonOpacity: .4f);
        var lighting = new AtmosphereLighting(default, default, default, default, new(.1f, .2f, .3f), new(.4f, .5f, .6f));
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1f, 2f, .1f, 100f);
        projection.M31 = .02f;
        projection.M32 = -.03f;
        var u = AtmosphereRenderer.PackUbo(s, frame, lighting, new(2f, 500f, 3f), new(100f, 1500f, 200f),
            Matrix4x4.Identity, projection, 800, 600, new HdrSettings());
        Assert.Equal(new Vector4(2f, 500f, 3f, 2f), u.Camera);
        Assert.Equal(new Vector4(100f, 1500f, 200f, 0f), u.Origin);
        Assert.Equal(new Vector4(1f, 0f, 0f, 0f), u.Right);
        Assert.Equal(new Vector4(0f, 1f, 0f, 0f), u.Up);
        Assert.Equal(new Vector4(0f, 0f, 1f, 0f), u.Back);
        Assert.Equal(new Vector4(1f / projection.M11, 1f / projection.M22,
            projection.M31 / projection.M11, projection.M32 / projection.M22), u.Ray);
        Assert.Equal(new Vector4(1f / 800f, 1f / 600f, 0f, .004675f), u.Res);
        Assert.Equal(new Vector4(.1f, .2f, .3f, .6f), u.SunT);
        Assert.Equal(new Vector4(.4f, .5f, .6f, .4f), u.MoonT);
        Assert.Equal(.03f, u.Moon.W);
        Assert.Equal(7f, u.Planet.W);
        Assert.Equal(1f, u.Tint.W);
        Assert.Equal(new Vector4(1f, 1f, 0f, .75f), u.Tone);
        projection = Matrix4x4.CreateOrthographic(10f, 5f, .1f, 100f);
        u = AtmosphereRenderer.PackUbo(s, frame, lighting, default, default, Matrix4x4.Identity,
            projection, 800, 600, new HdrSettings { Enabled = false });
        Assert.Equal(1f, u.Res.Z);
        Assert.Equal(0f, u.Tone.X);
    }
}
