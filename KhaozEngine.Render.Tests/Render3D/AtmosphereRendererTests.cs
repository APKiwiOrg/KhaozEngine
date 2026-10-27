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
    [Fact]
    public void EveryOpaqueReceiver_UsesSharedFogAndAbsoluteHeight()
    {
        foreach (var (source, prefix) in new[]
        {
            (ShaderSources.ModelFrag, 11), (ShaderSources.ModelDissolveFrag, 11),
            (ShaderSources.SkinnedModelFrag, 4), (ShaderSources.SkinnedModelDissolveFrag, 4),
            (ShaderSources.SplatFrag, 3), (ShaderSources.TileGroundFrag, 3),
        })
        {
            Assert.Contains($"layout(std140, set=0, binding={prefix}) uniform Atmosphere", source);
            Assert.Contains("lit = atmosphereFog(lit, vWorldPos, false, RenderOrigin.w > 0.5);", source);
            Assert.Contains("fragmentRelative.y + Atmo.Origin.y", source);
            Assert.Contains("Atmo.Camera.y + Atmo.Origin.y", source);
        }
        Assert.DoesNotContain("atmosphereFog(", ShaderSources.ShadowDepthFrag);
        Assert.DoesNotContain("atmosphereFog(", ShaderSources.ShadowDepthDissolveFrag);
    }

    [Theory]
    [InlineData("Model", 11)]
    [InlineData("ModelDissolve", 11)]
    [InlineData("Foliage", 11)]
    [InlineData("SkinnedModel", 4)]
    [InlineData("SkinnedModelDissolve", 4)]
    [InlineData("Splat", 3)]
    [InlineData("TileGround", 3)]
    [InlineData("Sky", 1)]
    [InlineData("Water", 7)]
    [InlineData("Decal", 4)]
    [InlineData("Particle", 6)]
    [InlineData("Billboard", 1)]
    [InlineData("TexturedBillboard", 3)]
    [InlineData("TexturedBillboardMotion", 3)]
    [InlineData("Trail", 1)]
    [InlineData("TrailMotion", 1)]
    [InlineData("Beam", 1)]
    [InlineData("BeamMotion", 1)]
    public void ConsumerReflection_PinsSharedSlots(string program, int prefix)
    {
        var (vertex, fragment) = program switch
        {
            "Model" => (ShaderSources.ModelVert, ShaderSources.ModelFrag),
            "ModelDissolve" => (ShaderSources.ModelVert, ShaderSources.ModelDissolveFrag),
            "Foliage" => (ShaderSources.FoliageVert, ShaderSources.ModelFrag),
            "SkinnedModel" => (ShaderSources.SkinnedModelVert, ShaderSources.SkinnedModelFrag),
            "SkinnedModelDissolve" => (ShaderSources.SkinnedModelVert, ShaderSources.SkinnedModelDissolveFrag),
            "Splat" => (ShaderSources.SplatVert, ShaderSources.SplatFrag),
            "TileGround" => (ShaderSources.TileGroundVert, ShaderSources.TileGroundFrag),
            "Sky" => (ShaderSources.SkyVert, ShaderSources.SkyFrag),
            "Water" => (ShaderSources.WaterVert, ShaderSources.WaterFrag),
            "Decal" => (ShaderSources.DecalVert, ShaderSources.DecalFrag),
            "Particle" => (ShaderSources.ParticleVert, ShaderSources.ParticleFrag),
            "Billboard" => (ShaderSources.BillboardVert, ShaderSources.BillboardFrag),
            "TexturedBillboard" => (ShaderSources.BillboardVert, ShaderSources.TexturedBillboardFrag),
            "TexturedBillboardMotion" => (ShaderSources.BillboardVert, ShaderSources.TexturedBillboardMotionFrag),
            "Trail" => (ShaderSources.TrailVert, ShaderSources.TrailFrag),
            "TrailMotion" => (ShaderSources.TrailVert, ShaderSources.TrailMotionFrag),
            "Beam" => (ShaderSources.BeamVert, ShaderSources.BeamFrag),
            "BeamMotion" => (ShaderSources.BeamVert, ShaderSources.BeamMotionFrag),
            _ => throw new ArgumentOutOfRangeException(nameof(program)),
        };
        var vertexBytes = SpirvFrontEnd.ToSpirv(vertex, GpuShaderStages.Vertex, program);
        var fragmentBytes = SpirvFrontEnd.ToSpirv(fragment, GpuShaderStages.Fragment, program);
        var pair = SpirvCrossCompile.VertexFragmentToMsl(vertexBytes, fragmentBytes, program);
        var elements = pair.Reflection.ResourceLayouts[0].Elements;
        bool waterReceiver = program is "Model" or "ModelDissolve" or "Foliage" or "SkinnedModel"
            or "SkinnedModelDissolve" or "Splat" or "TileGround";
        Assert.Equal(prefix + 3 + (program == "Water" ? 7 : prefix == 11 ? 1 : 0)
            + (waterReceiver ? 1 : 0), elements.Length);
        if (waterReceiver) Assert.Equal(GpuResourceKind.StructuredBufferReadOnly, elements[^1].Kind);
        Assert.Equal(new[] { GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly,
            GpuResourceKind.Sampler }, elements.Skip(prefix).Take(3).Select(e => e.Kind));
        Assert.All(elements.Skip(prefix), e => Assert.Equal(GpuShaderStages.Fragment, e.Stages));
        if (prefix == 11)
        {
            Assert.Equal(GpuResourceKind.TextureReadOnly, elements[14].Kind);
        }
        var metal = MetalShaderBuild.Pair(vertex, fragment, null, program);
        metal.Table.RequireLayoutShape(pair.Reflection.ResourceLayouts, program);
    }

    [Fact]
    public void DisabledFog_ReturnsBeforeDepthOrSkyWork()
    {
        string source = ShaderSources.ModelFrag;
        int transmission = source.IndexOf("vec3 atmosphereTransmittance(", StringComparison.Ordinal);
        Assert.True(transmission >= 0);
        string body = source[transmission..];
        Assert.Contains("if (Atmo.Tint.w < 0.5 || Atmo.Planet.w == 0.0) return vec3(1.0);", body);
        Assert.True(body.IndexOf("if (Atmo.Tint.w < 0.5 || Atmo.Planet.w == 0.0) return vec3(1.0);",
            StringComparison.Ordinal) < body.IndexOf("atmosphereSegmentView(fragmentRelative, distanceWorld)", StringComparison.Ordinal));
        int fog = source.IndexOf("vec3 atmosphereFog(", StringComparison.Ordinal);
        Assert.True(fog >= 0);
        body = source[fog..];
        Assert.Contains("if (!enabled) return colour;", body);
        Assert.True(body.IndexOf("if (!enabled) return colour;", StringComparison.Ordinal)
            < body.IndexOf("atmosphereTransmittance(fragmentRelative)", StringComparison.Ordinal));
        Assert.Contains("if (all(equal(transmission, vec3(1.0)))) return colour;", body);
        Assert.True(body.IndexOf("if (additive) return colour * transmission;", StringComparison.Ordinal)
            < body.IndexOf("atmosphereFogColour(", StringComparison.Ordinal));
    }

    internal static void VerifyFogBlendLayout()
    {
        Assert.Equal(80, Marshal.SizeOf<AtmosphereBindings.FogBlendUniforms>());
        Assert.Equal(64, (int)Marshal.OffsetOf<AtmosphereBindings.FogBlendUniforms>("FogBlend"));
        Assert.Equal(80u, TexturedBillboardRenderer.FramePayloadBytes);
        Assert.True(TexturedBillboardRenderer.FrameSlotBytes >= TexturedBillboardRenderer.FramePayloadBytes);
        Assert.Equal(0u, TexturedBillboardRenderer.FrameSlotBytes % 256);
        foreach (var source in new[] { ShaderSources.BillboardVert, ShaderSources.BillboardFrag,
            ShaderSources.TexturedBillboardFrag, ShaderSources.TrailVert, ShaderSources.TrailFrag })
            Assert.Contains("uniform U { mat4 ViewProj; vec4 FogBlend; };", source);
        Assert.Contains("uniform U { mat4 ViewProj; };", ShaderSources.LineVert);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectBuffers_PreserveBlendSlotsAndAtmosphereOwnership(bool borrowed)
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var sceneBindings = new AtmosphereBindings(device);
        var bindings = borrowed ? sceneBindings : null;
        var output = new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm);
        using (var billboards = new BillboardRenderer(device, output, bindings))
        using (var trails = new TrailRenderer(device, ModelTargets.Base, bindings))
        using (var beams = new BeamRenderer(device, ModelTargets.Base, bindings))
        using (var textured = new TexturedBillboardRenderer(device, ModelTargets.Base, bindings))
        {
            using var texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(1, 1,
                GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            using var textureSet = textured.CreateTextureSet(texture);
            Assert.Equal(5, factory.ResourceSets.Count(set => set.Resources.Length == 4));
            Assert.Equal(6, Assert.IsType<FakeResourceSet>(textureSet).Resources.Length);
            var range = Assert.IsType<GpuBufferRange>(Assert.IsType<FakeResourceSet>(textureSet).Resources[0]);
            Assert.Equal(2 * TexturedBillboardRenderer.FrameSlotBytes, range.Buffer.SizeInBytes);
            Assert.Equal(TexturedBillboardRenderer.FrameSlotBytes, range.Size);
            var effectSets = factory.ResourceSets.ToArray();
            Assert.All(effectSets.Where(set => set.Resources.Length == 4), set =>
                Assert.Equal(80u, Assert.IsAssignableFrom<IGpuBuffer>(set.Resources[0]).SizeInBytes));
            if (borrowed) Assert.All(effectSets, set => Assert.Same(sceneBindings.Uniforms, set.Resources[^3]));
        }
        Assert.All(factory.ResourceSets, set =>
        {
            Assert.True(set.Disposed);
            Assert.Equal(!borrowed, Assert.IsType<FakeBuffer>(set.Resources[^3]).Disposed);
            Assert.Equal(!borrowed, Assert.IsType<FakeTexture>(set.Resources[^2]).Disposed);
        });
    }

    [Theory]
    [InlineData(DecalBlend.Alpha, 0f)]
    [InlineData(DecalBlend.Additive, 1f)]
    public void DecalInstances_PreserveCoverageAndCarryBlendMode(DecalBlend blend, float flag)
    {
        var decal = new GroundDecal
        {
            Blend = blend,
            FillColor = new(.2f, .3f, .4f, .5f),
            OutlineColor = new(.8f, .7f, .6f, .25f),
            VoidFallback = true
        };
        var receiver = GroundDecalRenderer.PackInstance(decal, Vector4.One);
        var fallback = GroundDecalRenderer.PackVoidInstance(decal, Vector4.One);
        Assert.Equal(flag, receiver.Misc.Z);
        Assert.Equal(flag, fallback.Misc.Z);
        Assert.Equal(.5f, receiver.Fill.W);
        Assert.Equal(.25f, receiver.Outline.W);
        Assert.Equal(receiver.Fill, fallback.Fill);
    }

    [Fact]
    public void StandaloneModel_OwnsOneAtmosphereSuffixAcrossAllFrameSets()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        FakeBuffer uniforms;
        FakeTexture sky;
        using (var model = new ModelRenderer(device, ModelTargets.Base, 64, 1))
        {
            model.EnsureSkinnedMainCapacity(1);
            var frameSets = factory.ResourceSets.Where(set => !set.Disposed
                && set.Resources[0] is IGpuBuffer buffer && buffer.SizeInBytes == ModelRenderer.UboBytes).ToArray();
            Assert.Equal(new[] { 7, 7, 8, 16 }, frameSets.Select(set => set.Resources.Length).OrderBy(n => n));
            FakeResourceSet rigid = Assert.Single(frameSets, set => set.Resources.Length == 16);
            uniforms = Assert.IsType<FakeBuffer>(rigid.Resources[11]);
            sky = Assert.IsType<FakeTexture>(rigid.Resources[12]);
            Assert.Equal(AtmosphereRenderer.UboBytes, uniforms.SizeInBytes);
            Assert.All(frameSets, set =>
            {
                int atmosphereStart = set.Resources.Length == 16 ? 11 : set.Resources.Length - 4;
                Assert.Same(uniforms, set.Resources[atmosphereStart]);
                Assert.Same(sky, set.Resources[atmosphereStart + 1]);
                Assert.Same(rigid.Resources[13], set.Resources[atmosphereStart + 2]);
                Assert.Same(rigid.Resources[15], set.Resources[^1]);
            });
        }
        Assert.True(uniforms.Disposed);
        Assert.True(sky.Disposed);
    }

    [Fact]
    public void BorrowedModel_RebuildsKeepAtmosphereAndDisposalKeepsSceneResources()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var atmosphere = new AtmosphereBindings(device);
        using var retired = GpuRetireQueue.CreateFrameCounted(device, GpuRetireQueue.DefaultFrameDelay);
        using (var model = new ModelRenderer(device, ModelTargets.Base, 64, 1, retired, atmosphere))
        {
            FakeBuffer water = Assert.Single(factory.Buffers, b => b.SizeInBytes == 652800);
            IGpuResourceSet rigid = model.CreateMaterialSet();
            IGpuResourceSet skinned = model.CreateSkinnedMaterialSet();
            model.EnsureSkinnedMainCapacity(1);
            AssertSuffixes();
            model.EnsureSkinnedMainCapacity(9);
            AssertSuffixes(retainedSkinnedSet: true);
            model.EnsurePointLightCapacity(17, [rigid, skinned], replace =>
            {
                rigid = replace(rigid);
                skinned = replace(skinned);
            });
            AssertSuffixes();
            var settings = new ShadowSettings();
            settings.CommitAtlas();
            Assert.True(model.ReplaceShadowLayout(128, 2, settings, [rigid, skinned], replace =>
            {
                rigid = replace(rigid);
                skinned = replace(skinned);
            }, () => { }));
            for (int i = 0; i < GpuRetireQueue.DefaultFrameDelay; i++) retired.BeginFrame();
            AssertSuffixes();

            void AssertSuffixes(bool retainedSkinnedSet = false)
            {
                var frameSets = factory.ResourceSets.Where(set => !set.Disposed
                    && set.Resources.Contains(model.PointLightBuffer)).ToArray();
                Assert.Equal(retainedSkinnedSet ? new[] { 7, 7, 8, 8, 16, 16 } : new[] { 7, 7, 8, 16, 16 },
                    frameSets.Select(set => set.Resources.Length).OrderBy(n => n));
                Assert.All(frameSets, set =>
                {
                    int atmosphereStart = set.Resources.Length == 16 ? 11 : set.Resources.Length - 4;
                    Assert.Same(atmosphere.Uniforms, set.Resources[atmosphereStart]);
                    Assert.Same(atmosphere.SkyView, set.Resources[atmosphereStart + 1]);
                    Assert.Same(atmosphere.SkySampler, set.Resources[atmosphereStart + 2]);
                    Assert.Same(water, set.Resources[^1]);
                });
                var material = Assert.IsType<FakeResourceSet>(skinned);
                Assert.Equal(8, material.Resources.Length);
                Assert.DoesNotContain(atmosphere.Uniforms, material.Resources);
            }
        }
        Assert.False(Assert.IsType<FakeBuffer>(atmosphere.Uniforms).Disposed);
        Assert.False(Assert.IsType<FakeTexture>(atmosphere.SkyView).Disposed);
    }

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

    [Fact]
    public void DisabledFrames_AfterEnableUploadOnlyOnTheDisableTransition()
    {
        using var device = new FakeGpuDevice();
        using var renderer = new AtmosphereRenderer(device);
        using var sink = device.Factory.CreateCommandList();
        var commands = new CommandTallyGpuCommandList(sink);
        var post = new PixelPostProcessSettings();
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, .1f, 100f);

        post.Atmosphere.Enabled = true;
        renderer.Record(commands, post, Vector3.Zero, Vector3.Zero, Matrix4x4.Identity, projection, 64, 64);
        Assert.Equal(1, commands.Tally[GpuCommandKind.UpdateBuffer]);
        Assert.Equal(3, commands.Tally[GpuCommandKind.Draw]);

        post.Atmosphere.Enabled = false;
        commands.Clear();
        renderer.Record(commands, post, Vector3.Zero, Vector3.Zero, Matrix4x4.Identity, projection, 64, 64);
        Assert.Equal(1, commands.Tally[GpuCommandKind.UpdateBuffer]);
        Assert.All(Enum.GetValues<GpuCommandKind>().Where(kind => kind != GpuCommandKind.UpdateBuffer),
            kind => Assert.Equal(0, commands.Tally[kind]));

        commands.Clear();
        renderer.Record(commands, post, Vector3.Zero, Vector3.Zero, Matrix4x4.Identity, projection, 64, 64);
        Assert.All(Enum.GetValues<GpuCommandKind>(), kind => Assert.Equal(0, commands.Tally[kind]));
    }

    [GpuFact]
    public void Constructor_CreatesAllThreeProducerPipelinesOnHeadlessMetal()
    {
        if (!KhaozEngineMetal.IsPlatformSupported) return;
        using var device = new MetalBackendProvider().CreateHeadless().Device;
        using var renderer = new AtmosphereRenderer(device);
        Assert.Equal(AtmosphereRenderer.UboBytes, renderer.Bindings.Uniforms.SizeInBytes);
        Assert.Null(renderer.TransmittanceTexture);
        Assert.Null(renderer.MultiScatteringTexture);
        Assert.Equal(0, renderer.StaticBuildCount);
        Assert.Equal(0, renderer.SkyViewDrawCount);
    }

    [Fact]
    public void TransmittanceFloat32_LastColumnIsUnoccludedAndMatchesCpu()
    {
        var settings = AtmosphereSettings.Earth();
        var model = new AtmosphereModel(settings);
        var oracle = model.Tables.Transmittance;
        for (int y = 0; y < 64; y++)
        {
            var uv = new Vector2(255.5f / 256f, (y + .5f) / 64f);
            var actual = AtmosphereShaderMath.TransmittanceTexel(settings, uv);
            Assert.True(actual.X > 0f && actual.Y > 0f && actual.Z > 0f,
                $"Transmittance last-column row {y} was treated as occluded: {actual}.");
            var error = Vector3.Abs(actual - oracle[y * 256 + 255]);
            Assert.True(MathF.Max(error.X, MathF.Max(error.Y, error.Z)) <= .01f,
                $"Transmittance last-column row {y} differs from the CPU oracle: {error}.");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(51)]
    [InlineData(56)]
    public void PlanetShadowFloat32_GrazingRaysRemainVisibleButPenetratingRaysAreBlocked(int y)
    {
        const float bottom = 6360f;
        var parameters = AtmosphereShaderMath.TransmittanceParameters(
            new Vector2(255.5f / 256f, (y + .5f) / 64f), bottom, 6460f);
        var position = new Vector3(0f, parameters.X, 0f);
        var tangent = AtmosphereShaderMath.TransmittanceDirection(parameters.X, parameters.Y, bottom);
        Assert.False(AtmosphereShaderMath.PlanetOccludes(position, tangent, bottom));
        var penetrating = Vector3.Normalize(tangent - Vector3.UnitY * .01f);
        Assert.True(AtmosphereShaderMath.PlanetOccludes(position, penetrating, bottom));
        Assert.True(AtmosphereShaderMath.PlanetOccludes(position, -Vector3.UnitY, bottom));
        Assert.False(AtmosphereShaderMath.PlanetOccludes(position, Vector3.UnitY, bottom));
    }

    [GpuFact]
    public void Golden_TransmittanceLastColumnIsNonzero()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var device = gpu.GpuDevice;
        using var renderer = new AtmosphereRenderer(device);
        using var staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(256, 64,
            GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Staging));
        using var commands = device.Factory.CreateCommandList();
        var post = new PixelPostProcessSettings();
        post.Atmosphere.Enabled = true;
        commands.Begin();
        renderer.Record(commands, post, new Vector3(0f, 2f, 0f), Vector3.Zero, Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, .1f, 100f), 64, 64);
        commands.CopyTexture(renderer.TransmittanceTexture!, staging);
        commands.End();
        device.Submit(commands);
        device.WaitForIdle();

        var map = device.Map(staging, GpuMapMode.Read);
        try
        {
            var texel = new byte[8];
            for (int y = 0; y < 64; y++)
            {
                Marshal.Copy(IntPtr.Add(map.Data, checked((int)(y * map.RowPitch + 255 * 8))), texel, 0, 8);
                float r = (float)BitConverter.ToHalf(texel, 0), g = (float)BitConverter.ToHalf(texel, 2);
                float b = (float)BitConverter.ToHalf(texel, 4);
                Assert.True(float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b)
                    && (r > 0f || g > 0f || b > 0f),
                    $"Rendered transmittance last-column row {y} is invalid or occluded: ({r}, {g}, {b}).");
            }
        }
        finally
        {
            device.Unmap(staging);
        }
    }

    internal static void VerifyUboLayout()
    {
        string[] names = { "Planet", "Rayleigh", "Mie", "Ozone", "GroundHaze", "Sun", "Moon", "Tint",
                "Floors", "Camera", "Origin", "SunT", "MoonT", "Right", "Up", "Back", "Ray", "Res", "Tone", "NightTint",
                "FogScatter", "FogSource", "FogHorizon" };
        Assert.Equal(368, Marshal.SizeOf<AtmosphereRenderer.AtmosphereUbo>());
        Assert.Equal(368u, AtmosphereRenderer.UboBytes);
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
        s.AerialOnsetDistance = 80f;
        var scattering = new AtmosphereFogScattering(new(.7f, .8f, .9f), .5f, new(.01f, .02f, .03f), .8f,
            Vector3.UnitY, new(.1f, .2f, .3f), -Vector3.UnitY, new(.4f, .5f, .6f), .03f);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1f, 2f, .1f, 100f);
        projection.M31 = .02f;
        projection.M32 = -.03f;
        var u = AtmosphereRenderer.PackUbo(s, frame, lighting, scattering, new(2f, 500f, 3f), new(100f, 1500f, 200f),
            Matrix4x4.Identity, projection, 800, 600, new HdrSettings());
        Assert.Equal(new Vector4(2f, 500f, 3f, 2f), u.Camera);
        Assert.Equal(new Vector4(100f, 1500f, 200f, 0f), u.Origin);
        // FogHorizon holds the horizon sine and cosine at the camera's 2 km absolute altitude.
        double radius = s.PlanetRadiusKm + 2.0;
        Assert.Equal(s.PlanetRadiusKm / radius, u.FogHorizon.X, 6);
        Assert.Equal(-Math.Sqrt((radius - s.PlanetRadiusKm) * (radius + s.PlanetRadiusKm)) / radius, u.FogHorizon.Y, 6);
        Assert.Equal(0f, u.FogHorizon.Z);
        Assert.Equal(0f, u.FogHorizon.W);
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
        Assert.Equal(new Vector4(.45f, .65f, 1f, 0f), u.NightTint);
        Assert.Equal(new Vector4(.7f, .8f, .9f, .5f), u.FogScatter);
        Assert.Equal(new Vector4(.01f, .02f, .03f, 80f), u.FogSource);
        Assert.Equal(new Vector4(1f, 1f, 0f, .75f), u.Tone);
        projection = Matrix4x4.CreateOrthographic(10f, 5f, .1f, 100f);
        u = AtmosphereRenderer.PackUbo(s, frame, lighting, scattering, default, default, Matrix4x4.Identity,
            projection, 800, 600, new HdrSettings { Enabled = false });
        Assert.Equal(1f, u.Res.Z);
        Assert.Equal(0f, u.Tone.X);
        Assert.Equal(1f, u.FogHorizon.X);
        Assert.True(MathF.Abs(u.FogHorizon.Y) < 1e-6f, $"Horizon cosine at sea level: {u.FogHorizon.Y}.");
    }
}
