using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.MapDoc.Scene;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapWaterTests
{
    static readonly MapResourceStamp Stamp = new("closure", "source", "scope", "water", 6);
    static MapScopedWaterResult Water(string variant, double motionPadding = 1)
    {
        using var fixture = NativeSceneFixtures.Create(variant);
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(NativeSceneFixtures.WaterWindow(0, 10, 7) with { MotionPaddingMetres = motionPadding }).Scene!;
        return MapWaterScopedCompiler.Compile(scene);
    }
    static WaterLook Look() => new()
    {
        WaveSource = WaterWaveSource.Procedural,
        SwellAmplitude = .12f,
        SwellWavelength = 4f,
        SwellSteepness = .5f,
        Opacity = .63f
    };
    static WaterLook BoundaryLook()
    {
        var look = Look(); look.SwellWavelength = 2; look.SwellComponents = 1; look.SwellDirectionDegrees = 0;
        look.SwellSpreadDegrees = 0; look.SwellSpeed = 0; look.SwellSeed = 0; return look;
    }
    static Vector2 Displaced(WaterLook look, float x, float z)
    {
        var components = new GerstnerWaves.Component[1];
        GerstnerWaves.BuildComponents(look.SwellAmplitude!.Value, look.SwellWavelength!.Value,
            look.SwellDirectionDegrees!.Value * MathF.PI / 180, 0, look.SwellSteepness!.Value, 0, 0, 1, components);
        var sample = GerstnerWaves.Evaluate(x, z, 0, look.SwellSteepness.Value, components);
        return new(x + sample.Offset.X, z + sample.Offset.Z);
    }
    static MapDrawSet Draw(MapWaterRenderBatch batch)
    {
        var recorder = new MapDrawRecorder();
        batch.Register(Stamp, recorder);
        var focus = new MapFramePoint(WorldFrame.Origin, new(1, 2, 1));
        batch.Record(new(WorldFrame.Origin, focus, MapRoofMode.Auto, 3, 6, focus), recorder);
        return recorder.Snapshot();
    }

    [Fact]
    public void DefaultLook_InheritsDefaultSettingsAndPreparesCertifiedWater()
    {
        var look = new WaterLook(); var settings = new WaterSettings();
        double padding = NativeMapWater.RequiredMotionPadding(look, settings);
        Assert.True(double.IsFinite(padding) && padding > NativeMapWater.ClipEpsilonMetres);
        var batch = NativeMapWater.Prepare(Water("water-notch", padding), look, settings);
        var effective = Assert.Single(Draw(batch).Draws).Water!.Value.Look.ToWaterLook();
        Assert.Equal(WaterWaveSource.Procedural, effective.WaveSource);
        Assert.Equal(settings.SwellAmplitude, effective.SwellAmplitude);
        Assert.Equal(settings.SwellWavelength, effective.SwellWavelength);
        Assert.Equal(WaterSurfaceDisplacement.MaximumHorizontal(effective) + NativeMapWater.ClipEpsilonMetres, padding);
        AssertEffectiveLook(settings, effective);
    }

    [Fact]
    public void PartialLook_OverridesMixWithInheritedSettingsAndFreezeTogether()
    {
        var settings = new WaterSettings
        {
            SwellAmplitude = .18f,
            SwellWavelength = 6,
            SwellSteepness = .3f,
            Opacity = .8f,
            DeepColor = new(.1f, .2f, .3f, .4f),
            RippleComponents = 7,
            UnderwaterSunlight = true,
            UnderwaterPenumbraPerMetre = .08f,
            WaterColumnLight = true,
            WaterColor = new(.2f, .3f, .4f),
            Clarity = 3.5f,
            FoamStrength = .2f
        };
        var look = new WaterLook { SwellAmplitude = .09f, Opacity = .5f, FoamStrength = .7f };
        var expected = look.ResolveInto(new WaterSettings(), settings);
        double padding = NativeMapWater.RequiredMotionPadding(look, settings);
        var batch = NativeMapWater.Prepare(Water("water-notch", padding), look, settings);
        settings.SwellWavelength = 100; settings.DeepColor = Color.White; settings.RippleComponents = 1;
        settings.WaterColumnLight = false; settings.Clarity = 99;
        look.SwellAmplitude = 4; look.Opacity = 1;
        var effective = Assert.Single(Draw(batch).Draws).Water!.Value.Look.ToWaterLook();
        Assert.Equal(.09f, effective.SwellAmplitude); Assert.Equal(6, effective.SwellWavelength);
        Assert.Equal(.5f, effective.Opacity); Assert.Equal(.7f, effective.FoamStrength);
        Assert.Equal(7, effective.RippleComponents);
        AssertEffectiveLook(expected, effective);
    }

    [Fact]
    public void EffectiveLook_AllFieldsMatchReleasedRendererResolution()
    {
        var settings = new WaterSettings
        {
            SwellWavelength = 5,
            SwellSteepness = .4f,
            SwellDirectionDegrees = 93,
            SwellSpreadDegrees = 12,
            SwellSpeed = .4f,
            SwellSeed = .7f,
            SwellComponents = 6,
            ShallowColor = new(.3f, .4f, .5f, .6f),
            GlintExponent = 83,
            RippleSeed = .9f,
            ShoreFadeDistance = .2f
        };
        var look = new WaterLook
        {
            SwellSpeed = .2f,
            SwellComponents = 2,
            ShoalingStrength = .3f,
            SurfStrength = 0,
            AbsorptionPerMetre = new(.2f, .3f, .4f, 0),
            GlintRoughness = .4f,
            UnderwaterSunlight = false,
            UnderwaterPenumbraPerMetre = .12f,
            WaterColumnLight = false,
            WaterColor = new(.7f, .2f, .3f),
            Clarity = 2.5f,
            FoamColor = new(.8f, .7f, .6f, 1)
        };
        var rendererEffective = look.ResolveInto(new WaterSettings(), settings);
        double padding = NativeMapWater.RequiredMotionPadding(look, settings);
        var batch = NativeMapWater.Prepare(Water("water-notch", padding), look, settings);
        var effective = Assert.Single(Draw(batch).Draws).Water!.Value.Look.ToWaterLook();
        AssertEffectiveLook(rendererEffective, effective);
        AssertEffectiveLook(rendererEffective, effective.ResolveInto(new WaterSettings(), new WaterSettings { SwellWavelength = 100, Opacity = 0 }));
    }

    [Fact]
    public void InheritedInvalidAndFftSettings_RefuseWithTypedStatusUnlessOverridden()
    {
        var look = new WaterLook(); var settings = new WaterSettings { SwellAmplitude = float.NaN };
        Assert.Equal(MapRenderAssetStatus.Invalid, Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.RequiredMotionPadding(look, settings)).Status);
        Assert.Equal(MapRenderAssetStatus.Invalid, Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.Prepare(Water("water-notch"), look, settings)).Status);
        look.SwellAmplitude = 0;
        Assert.Single(Draw(NativeMapWater.Prepare(Water("water-notch"), look, settings)).Draws);
        settings = new WaterSettings { WaveSource = WaterWaveSource.FftOcean };
        Assert.Equal(MapRenderAssetStatus.Unsupported, Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.RequiredMotionPadding(look, settings)).Status);
        Assert.Equal(MapRenderAssetStatus.Unsupported, Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.Prepare(Water("water-notch"), look, settings)).Status);
        look.WaveSource = WaterWaveSource.Procedural;
        Assert.Single(Draw(NativeMapWater.Prepare(Water("water-notch"), look, settings)).Draws);
    }

    static void AssertEffectiveLook(WaterSettings expected, object actual)
    {
        foreach (var field in typeof(WaterLook).GetFields())
        {
            var actualField = actual.GetType().GetField(field.Name);
            Assert.NotNull(actualField);
            object? value = actualField.GetValue(actual);
            Assert.NotNull(value);
            Assert.Equal(typeof(WaterSettings).GetField(field.Name)!.GetValue(expected), value);
        }
    }

    [Fact]
    public void BentWaterBody_AreaScaledWorkAndAuthoredShapeSurvive()
    {
        var one = NativeMapWater.Prepare(Water("water-bent-one"), Look(), new WaterSettings());
        var sixteen = NativeMapWater.Prepare(Water("water-bent-sixteen"), Look(), new WaterSettings());
        var a = Draw(one); var b = Draw(sixteen);
        var meshes = a.Resources.Select(r => Assert.IsType<MapWaterResource>(r.Payload).Mesh).ToArray();
        Assert.Equal(16d, meshes.Sum(Area));
        Assert.Equal(meshes.Sum(m => m.Vertices.Length), b.Resources.Sum(r => Assert.IsType<MapWaterResource>(r.Payload).Mesh.Vertices.Length));
        Assert.True(meshes.Sum(m => m.Vertices.Length) < 100);
        Assert.All(a.Resources, r => Assert.NotEmpty(Assert.IsType<MapWaterResource>(r.Payload).ClipData.ToArray()));
        Assert.All(a.Draws, d =>
        {
            Assert.Equal(.63f, d.Water!.Value.Look.Opacity);
            Assert.Equal(.12f, d.Water.Value.Look.SwellAmplitude);
            Assert.Equal(Matrix4x4.Identity, d.LocalTransform);
        });
        foreach (var resource in a.Resources)
        {
            var mesh = Assert.IsType<MapWaterResource>(resource.Payload).Mesh;
            var draw = a.Draws.Single(d => d.Resource == resource.Reference);
            foreach (var v in mesh.Vertices)
            {
                double x = v.Position.X + draw.Anchor.X, z = v.Position.Z + draw.Anchor.Z;
                Assert.True(x >= 0 && z >= 0 && ((x <= 10 && z <= 1) || (x <= 1 && z <= 7)));
            }
            for (int i = 0; i < mesh.Indices32.Length; i += 3)
            {
                Vector2 World(uint index) => new(mesh.Vertices[index].Position.X + draw.Anchor.X, mesh.Vertices[index].Position.Z + draw.Anchor.Z);
                Assert.True(InsideBentTriangle(World(mesh.Indices32[i]), World(mesh.Indices32[i + 1]), World(mesh.Indices32[i + 2])));
            }
        }
        Assert.True(one.RetainedBytes > 0);
    }

    [Fact]
    public void OverlappingWindows_CanonicalWaterCellBytesAgree()
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var source = NativeSceneFixtures.Open(fixture, false);
        var a = NativeMapWater.Prepare(MapWaterScopedCompiler.Compile(source.Acquire(NativeSceneFixtures.WaterWindow(0, 1.25f) with { MotionPaddingMetres = 1 }).Scene!), Look(), new WaterSettings());
        var b = NativeMapWater.Prepare(MapWaterScopedCompiler.Compile(source.Acquire(NativeSceneFixtures.WaterWindow(.75f, 2) with { MotionPaddingMetres = 1 }).Scene!), Look(), new WaterSettings());
        var ar = Assert.Single(Draw(a).Resources); var br = Assert.Single(Draw(b).Resources);
        var am = Assert.IsType<MapWaterResource>(ar.Payload); var bm = Assert.IsType<MapWaterResource>(br.Payload);
        Assert.Equal(ar.PayloadDigest, br.PayloadDigest);
        Assert.Equal(am.Mesh.Vertices, bm.Mesh.Vertices);
        Assert.Equal(am.Mesh.Indices32, bm.Mesh.Indices32);
        Assert.Equal(am.ClipData.ToArray(), bm.ClipData.ToArray());
        Assert.Equal(3, Area(am.Mesh));
    }

    [Fact]
    public void ClosedUnresolvedAndMissingWater_NeverBecomeVisibleOcean()
    {
        Assert.Empty(Draw(NativeMapWater.Prepare(Water("water-closed"), Look(), new WaterSettings())).Draws);
        Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.Prepare(Water("water-missing"), Look(), new WaterSettings()));
    }

    [Fact]
    public void LegacyWaterPlane_UnusedNativePathIsIdentical()
    {
        WaterPlane[] oldWaterPlaneDrawCommands = [new(2, .25f, 3, 4, look: new() { Opacity = .7f })];
        var unusedNativeRouteDrawCommands = WaterRenderer.LegacyPlanesWithBoundedDemand(oldWaterPlaneDrawCommands, []);
        Assert.Equal(oldWaterPlaneDrawCommands, unusedNativeRouteDrawCommands.ToArray());
    }

    [Fact]
    public void OriginalDomain_RejectsDisplacedFragmentsInTheDryNotch()
    {
        var draw = Draw(NativeMapWater.Prepare(Water("water-notch"), Look(), new WaterSettings()));
        var resource = Assert.IsType<MapWaterResource>(Assert.Single(draw.Resources).Payload);
        Assert.True(WaterSurfaceDomain.Contains(resource.ClipData.Span, new(1.5f, .5f)));
        Assert.True(WaterSurfaceDomain.Contains(resource.ClipData.Span, new(.5f, 1.5f)));
        Assert.False(WaterSurfaceDomain.Contains(resource.ClipData.Span, new(1.5f, 1.5f)));
        Assert.Contains("RenderToLocal * vec4(vWorldPos, 1.0)", ShaderSources.BoundedWaterFrag, StringComparison.Ordinal);
        Assert.DoesNotContain("BoundedWaterDomain", ShaderSources.WaterFrag, StringComparison.Ordinal);
        Assert.DoesNotContain("BoundedWaterTransform", ShaderSources.WaterVert, StringComparison.Ordinal);
    }

    [Fact]
    public void LookAndInventory_AreFrozenAndDuplicateCellsUploadOnce()
    {
        var water = Water("water-notch");
        var look = Look(); look.Opacity = .43f; look.SwellSpeed = .8f;
        var batch = NativeMapWater.Prepare(water with { Cells = water.Cells.Concat(water.Cells).ToArray() }, look, new WaterSettings());
        look.Opacity = .99f;
        var recorded = Draw(batch);
        Assert.Single(recorded.Resources);
        Assert.Single(batch.Cells);
        Assert.Equal(.43f, Assert.Single(recorded.Draws).Water!.Value.Look.Opacity);
        Assert.Equal(.8f, Assert.Single(recorded.Draws).Water!.Value.Look.SwellSpeed);
        Assert.Throws<InvalidOperationException>(() => batch.Register(Stamp with { Generation = 8 }, new MapDrawRecorder()));
        var changed = water.Cells[0] with { PayloadDigest = "different" };
        Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.Prepare(water with { Cells = [water.Cells[0], changed] }, look, new WaterSettings()));
    }

    [Fact]
    public void PublishedWaterQueue_UsesExistingMeshAndClearsAndRefusesStaleHandles()
    {
        var resource = Assert.IsType<MapWaterResource>(Assert.Single(Draw(NativeMapWater.Prepare(Water("water-notch"), Look(), new WaterSettings())).Resources).Payload);
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var target = factory.CreateTexture(GpuTextureDescription.Texture2D(16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using var framebuffer = factory.CreateFramebuffer(null, target);
        using var scene = new Scene3D(device, framebuffer.Outputs);
        var mesh = scene.LoadWaterMesh(resource.Mesh, resource.ClipData);
        int buffers = factory.Buffers.Count;
        var surface = new WaterSurfaceMesh(mesh, new(32000, 0, -32000), Matrix4x4.Identity, new() { Opacity = .4f });
        scene.Begin();
        scene.DrawWater(surface);
        Assert.Equal(1, scene.BoundedWaterCount);
        Assert.Equal(0, scene.WaterPlaneCount);
        Assert.Equal(buffers, factory.Buffers.Count);
        scene.Begin();
        Assert.Equal(0, scene.BoundedWaterCount);
        scene.UnloadMesh(mesh);
        Assert.Throws<ArgumentException>(() => scene.DrawWater(surface));
        Assert.Throws<ArgumentException>(() => WaterSurfaceDomain.Validate(new byte[16]));
    }

    [Fact]
    public void MotionPadding_DrawsDisplacedTrianglesIntoTheNarrowWindow()
    {
        using var fixture = NativeSceneFixtures.Create("water-bent-one");
        var source = NativeSceneFixtures.Open(fixture, false);
        var look = BoundaryLook(); look.SwellDirectionDegrees = 180;
        MapDrawSet Window(float maxX) => Draw(NativeMapWater.Prepare(MapWaterScopedCompiler.Compile(source.Acquire(
            NativeSceneFixtures.WaterWindow(0, maxX, 1) with { MotionPaddingMetres = 1 }).Scene!), look, new WaterSettings()));
        var narrow = Window(2); var wide = Window(4);
        var wetPoint = new Vector2(1.9f, .5f);
        Assert.True(Displaced(look, 2, .5f).X < wetPoint.X);
        Assert.True(Covers(wide), "The wider-window control must cover the authored wet point.");
        Assert.Equal(Covers(wide), Covers(narrow));

        bool Covers(MapDrawSet set)
        {
            foreach (var draw in set.Draws)
            {
                var resource = Assert.IsType<MapWaterResource>(set.Resources.Single(r => r.Reference == draw.Resource).Payload);
                if (!WaterSurfaceDomain.Contains(resource.ClipData.Span, wetPoint - new Vector2(draw.Anchor.X, draw.Anchor.Z))) continue;
                var mesh = resource.Mesh;
                Vector2 Vertex(uint index) => Displaced(look, mesh.Vertices[index].Position.X + draw.Anchor.X,
                    mesh.Vertices[index].Position.Z + draw.Anchor.Z);
                for (int i = 0; i < mesh.Indices32.Length; i += 3)
                {
                    var a = Vertex(mesh.Indices32[i]); var b = Vertex(mesh.Indices32[i + 1]); var c = Vertex(mesh.Indices32[i + 2]);
                    float area = Cross(b - a, c - a);
                    if (area == 0) continue;
                    float ab = Cross(b - a, wetPoint - a), bc = Cross(c - b, wetPoint - b), ca = Cross(a - c, wetPoint - c);
                    if (area > 0 ? ab >= 0 && bc >= 0 && ca >= 0 : ab <= 0 && bc <= 0 && ca <= 0) return true;
                }
            }
            return false;
        }
        static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    }

    [Fact]
    public void AdjacentCells_OriginalDomainClipKeepsWetCoverageAcrossInternalBoundary()
    {
        var look = BoundaryLook();
        var recorded = Draw(NativeMapWater.Prepare(Water("water-bent-one"), look, new WaterSettings()));
        var left = recorded.Draws.Single(d => d.Anchor.X == 0 && d.Anchor.Z == 0);
        var resource = Assert.IsType<MapWaterResource>(recorded.Resources.Single(r => r.Reference == left.Resource).Payload);
        // This point is inside the authored bottom arm [0,10] x [0,1]. A positive horizontal swell at the shared
        // x=2 edge makes the left mesh cover it. Clipping to the storage cell creates a dry strip before the
        // displaced right mesh begins. Only authored water boundaries may reject the fragment.
        Assert.True(WaterSurfaceDomain.Contains(resource.ClipData.Span, new(2.125f, .5f)),
            "The original-domain clip must retain wet coverage beyond the internal x=2 canonical-cell boundary.");
        Assert.True(Displaced(look, 2, .5f).X > 2.125f);
    }

    [Fact]
    public void DisplacedPosition_ClipsOutwardShorelineAndDryNotch()
    {
        var look = BoundaryLook();
        var shoreline = Draw(NativeMapWater.Prepare(Water("water-bent-one"), look, new WaterSettings()));
        var left = shoreline.Draws.Single(d => d.Anchor.X == 0 && d.Anchor.Z == 0);
        var clip = Assert.IsType<MapWaterResource>(shoreline.Resources.Single(r => r.Reference == left.Resource).Payload).ClipData;
        Assert.False(WaterSurfaceDomain.Contains(clip.Span, new(-.125f, .5f)));
        var last = shoreline.Draws.Single(d => d.Anchor.X == 8 && d.Anchor.Z == 0);
        var lastClip = Assert.IsType<MapWaterResource>(shoreline.Resources.Single(r => r.Reference == last.Resource).Payload).ClipData;
        var outward = Displaced(look, 10, .5f);
        Assert.True(outward.X > 10);
        Assert.False(WaterSurfaceDomain.Contains(lastClip.Span, outward - new Vector2(8, 0)));
        look.SwellDirectionDegrees = 180;
        var notch = Assert.IsType<MapWaterResource>(Assert.Single(Draw(NativeMapWater.Prepare(Water("water-notch"), look, new WaterSettings())).Resources).Payload).ClipData;
        Assert.False(WaterSurfaceDomain.Contains(notch.Span, new(1.125f, 1.125f)));
        Assert.True(WaterSurfaceDomain.Contains(notch.Span, new(.875f, 1.125f)));
        var intoNotch = Displaced(look, 1, 1.5f);
        Assert.True(intoNotch.X > 1);
        Assert.False(WaterSurfaceDomain.Contains(notch.Span, intoNotch));
    }

    [Fact]
    public void MotionPaddingBelowCertifiedDisplacement_RefusesInsteadOfClamping()
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var source = NativeSceneFixtures.Open(fixture, false);
        var unpadded = MapWaterScopedCompiler.Compile(source.Acquire(NativeSceneFixtures.WaterWindow()).Scene!);
        Assert.Equal(MapRenderAssetStatus.Invalid, Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.Prepare(unpadded, Look(), new WaterSettings())).Status);
        var fft = Look(); fft.WaveSource = WaterWaveSource.FftOcean;
        Assert.Equal(MapRenderAssetStatus.Unsupported, Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.Prepare(Water("water-notch"), fft, new WaterSettings())).Status);
        var invalid = Look(); invalid.SwellAmplitude = float.NaN;
        Assert.Equal(MapRenderAssetStatus.Invalid, Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.RequiredMotionPadding(invalid, new WaterSettings())).Status);
        invalid = Look(); invalid.SwellWavelength = 1e-36f;
        Assert.Equal(MapRenderAssetStatus.Invalid, Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.RequiredMotionPadding(invalid, new WaterSettings())).Status);
        double required = NativeMapWater.RequiredMotionPadding(Look(), new WaterSettings());
        var exact = MapWaterScopedCompiler.Compile(source.Acquire(NativeSceneFixtures.WaterWindow() with { MotionPaddingMetres = required }).Scene!);
        Assert.Single(Draw(NativeMapWater.Prepare(exact, Look(), new WaterSettings())).Draws);
        var shortByOneBit = MapWaterScopedCompiler.Compile(source.Acquire(NativeSceneFixtures.WaterWindow() with { MotionPaddingMetres = Math.BitDecrement(required) }).Scene!);
        Assert.Equal(MapRenderAssetStatus.Invalid, Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.Prepare(shortByOneBit, Look(), new WaterSettings())).Status);
    }

    [Fact]
    public void NeighbourMutation_ChangesDependentRenderDigestWithoutChangingCanonicalCell()
    {
        using var fixture = NativeSceneFixtures.Create("water-bent-one");
        var scope = NativeSceneFixtures.WaterWindow(0, 1, 1) with { MotionPaddingMetres = 1 };
        var before = MapWaterScopedCompiler.Compile(NativeSceneFixtures.Open(fixture, false).Acquire(scope).Scene!);
        var a = Assert.Single(Draw(NativeMapWater.Prepare(before, Look(), new WaterSettings())).Resources);
        var body = fixture.Document.Water!.Bodies[0];
        fixture.Document.Water.Bodies[0] = body with
        {
            Domain = body.Domain with
            { Rings = [], Rects = [new(0, 0, 2, 1), new(3, 0, 10, 1), new(0, 1, 1, 7)] }
        };
        var after = MapWaterScopedCompiler.Compile(NativeSceneFixtures.Open(fixture, false).Acquire(scope).Scene!);
        var b = Assert.Single(Draw(NativeMapWater.Prepare(after, Look(), new WaterSettings())).Resources);
        Assert.Equal(Assert.Single(before.Cells).Key, Assert.Single(after.Cells).Key);
        Assert.Equal(before.Cells[0].PayloadDigest, after.Cells[0].PayloadDigest);
        Assert.NotEqual(a.PayloadDigest, b.PayloadDigest);
    }

    [Fact]
    public void OtherResidentCells_DoNotChangeACellsClipPayload()
    {
        using var fixture = NativeSceneFixtures.Create("water-bent-one");
        var body = fixture.Document.Water!.Bodies[0];
        fixture.Document.Water.Bodies[0] = body with
        {
            Domain = body.Domain with
            { Rings = [], Rects = [new(0, 0, 2, 1), new(3, 0, 10, 1), new(0, 1, 1, 7)] }
        };
        var source = NativeSceneFixtures.Open(fixture, false);
        MapDrawSet DrawWindow(float max) => Draw(NativeMapWater.Prepare(MapWaterScopedCompiler.Compile(source
            .Acquire(NativeSceneFixtures.WaterWindow(0, max, 1) with { MotionPaddingMetres = 1 }).Scene!), Look(), new WaterSettings()));
        var narrow = DrawWindow(1); var wide = DrawWindow(6);
        MapPreparedResource First(MapDrawSet set) => set.Resources.Single(r => r.Reference == set.Draws.Single(d => d.Anchor.X == 0 && d.Anchor.Z == 0).Resource);
        var a = First(narrow); var b = First(wide);
        Assert.Equal(a.PayloadDigest, b.PayloadDigest);
        Assert.Equal(Assert.IsType<MapWaterResource>(a.Payload).ClipData.ToArray(), Assert.IsType<MapWaterResource>(b.Payload).ClipData.ToArray());
    }

    [Theory]
    [InlineData(1e-38f, 0f, 0f, 0f)]
    [InlineData(2f, 0f, float.MaxValue, 0f)]
    [InlineData(2f, .5f, float.MaxValue, 0f)]
    [InlineData(2f, 0f, 0f, float.MaxValue)]
    [InlineData(2f, .5f, 0f, float.MaxValue)]
    public void ActiveGenerationOverflow_RefusesInvalidEvenWhenHorizontalSteepnessIsZero(float wavelength, float steepness, float speed, float seed)
    {
        var look = BoundaryLook(); look.SwellWavelength = wavelength; look.SwellSteepness = steepness;
        look.SwellSpeed = null; look.SwellSeed = null;
        var settings = new WaterSettings { SwellSpeed = speed, SwellSeed = seed };
        var components = new GerstnerWaves.Component[1];
        GerstnerWaves.BuildComponents(.12f, wavelength, 0, 0, steepness, speed, seed, 1, components);
        var offset = GerstnerWaves.Evaluate(.5f, .5f, 1, steepness, components).Offset;
        Assert.False(float.IsFinite(offset.X) && float.IsFinite(offset.Y) && float.IsFinite(offset.Z));
        Assert.Equal(MapRenderAssetStatus.Invalid, Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.RequiredMotionPadding(look, settings)).Status);
        Assert.Equal(MapRenderAssetStatus.Invalid, Assert.Throws<MapRenderAssetRefusal>(() => NativeMapWater.Prepare(Water("water-notch"), look, settings)).Status);
    }

    [Fact]
    public void ValidZeroSteepness_CertifiesZeroHorizontalReachWithActiveVerticalWaves()
    {
        var look = BoundaryLook(); look.SwellSteepness = 0; look.SwellSpeed = .8f; look.SwellSeed = .37f;
        var settings = new WaterSettings();
        Assert.Equal(0, WaterSurfaceDisplacement.MaximumHorizontal(look));
        double padding = NativeMapWater.RequiredMotionPadding(look, settings);
        Assert.Equal(NativeMapWater.ClipEpsilonMetres, padding);
        Assert.Single(Draw(NativeMapWater.Prepare(Water("water-notch", padding), look, settings)).Draws);
        var components = new GerstnerWaves.Component[1];
        GerstnerWaves.BuildComponents(.12f, 2, 0, 0, 0, .8f, .37f, 1, components);
        var offset = GerstnerWaves.Evaluate(.25f, .5f, 0, 0, components).Offset;
        Assert.Equal(0, offset.X); Assert.Equal(0, offset.Z);
        Assert.True(float.IsFinite(offset.Y)); Assert.NotEqual(0, offset.Y);
    }

    [Theory]
    [InlineData(.12f, 4f, .5f)]
    [InlineData(.45f, 42f, .6f)]
    [InlineData(0f, 42f, .6f)]
    public void CertifiedEnvelope_CoversTheCurrentGerstnerDisplacement(float amplitude, float wavelength, float steepness)
    {
        var look = Look(); look.SwellAmplitude = amplitude; look.SwellWavelength = wavelength; look.SwellSteepness = steepness;
        double maximum = WaterSurfaceDisplacement.MaximumHorizontal(look);
        var components = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
        for (int count = 1; count <= GerstnerWaves.MaxComponents; count++)
        {
            int written = GerstnerWaves.BuildComponents(amplitude, wavelength, .73f, .41f, steepness, .8f, .37f, count, components);
            var sample = GerstnerWaves.Evaluate(2.125f, .5f, 1.7f, steepness, components.AsSpan(0, written));
            Assert.True(new Vector2(sample.Offset.X, sample.Offset.Z).Length() <= maximum);
        }
    }

    [Fact]
    public void TriangleContainmentControl_RejectsAnEdgeCrossingTheDryNotch()
        => Assert.False(InsideBentTriangle(new(0, 0), new(2, 1), new(1, 2)));

    static bool InsideBentTriangle(Vector2 a, Vector2 b, Vector2 c)
    {
        var points = new[] { a, b, c };
        foreach (var point in points)
            if (point.X < 0 || point.Y < 0 || !((point.X <= 10 && point.Y <= 1) || (point.X <= 1 && point.Y <= 7))) return false;
        // A bounded triangle can meet the unbounded dry quadrant only if one of its edges meets it. Check
        // where each horizontal-arm endpoint's edge crosses x=1 toward the vertical arm, in both directions.
        for (int i = 0; i < 3; i++)
        {
            var p = points[i]; var q = points[(i + 1) % 3];
            if (Crosses(p, q) || Crosses(q, p)) return false;
        }
        return true;
        static bool Crosses(Vector2 p, Vector2 q) => p.X > 1 && q.Y > 1 &&
            p.Y + ((double)q.Y - p.Y) * (p.X - 1) / (p.X - q.X) > 1;
    }

    static double Area(GltfMesh mesh)
    {
        double area = 0;
        for (int i = 0; i < mesh.Indices32.Length; i += 3)
        {
            var a = mesh.Vertices[mesh.Indices32[i]].Position;
            var b = mesh.Vertices[mesh.Indices32[i + 1]].Position;
            var c = mesh.Vertices[mesh.Indices32[i + 2]].Position;
            area += Math.Abs((double)(b.X - a.X) * (c.Z - a.Z) - (double)(b.Z - a.Z) * (c.X - a.X)) * .5;
        }
        return area;
    }
}
