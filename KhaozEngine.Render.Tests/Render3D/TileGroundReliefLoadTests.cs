using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <c>Scene3D.LoadTileGroundMaterial</c> with relief layers, headless on <see cref="FakeGpuDevice"/>, which records
/// every texture and buffer the scene asks for: what the surface array holds, the surface refusals, and that a
/// refused relief layer creates nothing.
/// </summary>
public sealed class TileGroundReliefLoadTests
{
    static (FakeGpuResourceFactory Factory, Scene3D Scene) NewScene() => NewScene(new FakeGpuDevice());

    static (FakeGpuResourceFactory Factory, Scene3D Scene) NewScene(FakeGpuDevice fake)
    {
        var factory = (FakeGpuResourceFactory)fake.Factory;
        IGpuTexture tex = factory.CreateTexture(GpuTextureDescription.Texture2D(
            16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        IGpuFramebuffer fb = factory.CreateFramebuffer(null, tex);
        return (factory, new Scene3D(fake, fb.Outputs));
    }

    static TileGroundLayerImage Mapped(int surfaceTexels) => new()
    {
        AlbedoRgba = new byte[4 * 4 * 4],
        SurfaceRgba = new byte[surfaceTexels * 4],
        NormalMapped = true,
    };

    static TileGroundLayerImage Plain() => new() { AlbedoRgba = new byte[4 * 4 * 4] };

    [Fact]
    public void The_surface_array_holds_one_layer_per_mapped_layer_at_the_surface_size()
    {
        (FakeGpuResourceFactory factory, Scene3D scene) = NewScene();
        using (scene)
        {
            int before = factory.Textures.Count;

            Assert.True(scene.LoadTileGroundMaterial(4, 4, new[] { Mapped(8 * 8), Plain(), Mapped(8 * 8) }, 8, 8).IsValid);

            // The albedo array, then the surface array of the two mapped layers at their own size.
            FakeTexture[] made = factory.Textures.Skip(before).ToArray();
            Assert.Equal(2, made.Length);
            Assert.Equal((4u, 4u, 3u), (made[0].Width, made[0].Height, made[0].ArrayLayers));
            Assert.Equal((8u, 8u, 2u), (made[1].Width, made[1].Height, made[1].ArrayLayers));
            Assert.Equal(TileGroundMaterialConfig.MipLevelCount(8, 8), made[1].MipLevels);
        }
    }

    [Fact]
    public void A_relief_set_without_surface_texels_binds_one_flat_texel_and_a_plain_set_binds_none()
    {
        (FakeGpuResourceFactory factory, Scene3D scene) = NewScene();
        using (scene)
        {
            int before = factory.Textures.Count;
            Assert.True(scene.LoadTileGroundMaterial(4, 4, new[] { Plain(), Plain() }).IsValid);
            Assert.Equal(before + 1, factory.Textures.Count);

            before = factory.Textures.Count;
            TileGroundLayerImage height = Plain();
            height.HeightMapped = true;
            Assert.True(scene.LoadTileGroundMaterial(4, 4, new[] { height, Plain() }).IsValid);
            FakeTexture[] made = factory.Textures.Skip(before).ToArray();
            Assert.Equal(2, made.Length);
            Assert.Equal((1u, 1u, 1u, 1u), (made[1].Width, made[1].Height, made[1].ArrayLayers, made[1].MipLevels));
        }
    }

    [Fact]
    public void Surface_texels_of_the_wrong_length_are_refused_naming_the_layer()
    {
        (FakeGpuResourceFactory factory, Scene3D scene) = NewScene();
        using (scene)
        {
            var layers = new[] { Mapped(8 * 8), Mapped(4 * 4) };
            int textures = factory.Textures.Count;

            var error = Assert.Throws<TileGroundUploadArgumentException>(() =>
                scene.LoadTileGroundMaterial(4, 4, layers, 8, 8));

            Assert.Equal("layers", error.ParamName);
            Assert.Equal(TileGroundUploadRefusal.SurfaceByteLengthMismatch, error.Reason);
            Assert.Equal(1, error.LayerIndex);
            Assert.Equal(4 * 4 * 4, error.ActualByteLength);
            Assert.Equal(8 * 8 * 4, error.ExpectedByteLength);
            Assert.Equal(8, error.Width);
            Assert.Equal(8, error.Height);
            Assert.Equal(textures, factory.Textures.Count);

            layers[1].SurfaceRgba = new byte[8 * 8 * 4];
            Assert.True(scene.LoadTileGroundMaterial(4, 4, layers, 8, 8).IsValid);
        }
    }

    [Theory]
    [InlineData(0, 8, "surfaceWidth")]
    [InlineData(8, 0, "surfaceHeight")]
    public void Surface_texels_need_a_positive_surface_size(int width, int height, string parameter)
    {
        (_, Scene3D scene) = NewScene();
        using (scene)
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                scene.LoadTileGroundMaterial(4, 4, new[] { Mapped(8 * 8) }, width, height));
            Assert.Equal(parameter, error.ParamName);

            // A set with no surface texels never reads the surface size.
            TileGroundLayerImage breakup = Plain();
            breakup.FeatherBreakup = 0.5f;
            Assert.True(scene.LoadTileGroundMaterial(4, 4, new[] { breakup }, width, height).IsValid);
        }
    }

    // A one-vertex-per-lane tile-ground mesh naming the given slots: four corner lanes and the overlay lane (slot plus
    // one, 0 for none). Only the slots matter to program selection.
    static GltfMesh Naming(int s0, int s1, int s2, int s3, int overlayPlusOne = 0)
    {
        var v = new ModelVertex(System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY,
            new System.Numerics.Vector4(1f, 0f, 0f, 0f), new System.Numerics.Vector2(s0, s1),
            new System.Numerics.Vector4(s2, s3, 1f, overlayPlusOne));
        return new GltfMesh(new[] { v, v, v }, new ushort[] { 0, 1, 2 });
    }

    [Fact]
    public void A_mesh_draws_with_the_cheapest_program_its_slots_need()
    {
        (_, Scene3D scene) = NewScene();
        using (scene)
        {
            TileGroundLayerImage mapped = Plain();
            mapped.HeightMapped = true;
            TileGroundLayerImage hex = Plain();
            hex.AntiTiling = TileGroundAntiTiling.Rotate;
            TileGroundLayerImage companionOfMapped = Plain();
            companionOfMapped.Steep = new TileGroundSteepLayer { CompanionSlot = 1, FromDegrees = 40f, ToDegrees = 55f };
            var relief = scene.LoadTileGroundMaterial(4, 4, new[] { Plain(), mapped, hex, companionOfMapped }, 0, 0);
            var plainSet = scene.LoadTileGroundMaterial(4, 4, new[] { Plain(), Plain() });

            TileGroundProgram Program(GltfMesh mesh, Scene3D.TileGroundMaterialHandle material) =>
                scene.TileGroundProgramForTests(scene.LoadMesh(mesh, material));

            Assert.Equal(TileGroundProgram.Plain, Program(Naming(0, 0, 0, 0), relief));
            Assert.Equal(TileGroundProgram.Relief, Program(Naming(0, 0, 0, 1), relief));
            Assert.Equal(TileGroundProgram.ReliefHex, Program(Naming(0, 1, 2, 0), relief));
            // A plain layer whose steep companion is mapped takes the companion's taps, so it needs relief.
            Assert.Equal(TileGroundProgram.Relief, Program(Naming(3, 3, 3, 3), relief));
            // The overlay lane counts, as the slot plus one.
            Assert.Equal(TileGroundProgram.ReliefHex, Program(Naming(0, 0, 0, 0, overlayPlusOne: 3), relief));
            // A slot past the set clamps to its last layer, as the fragment reads it.
            Assert.Equal(TileGroundProgram.Relief, Program(Naming(9, 9, 9, 9), relief));
            Assert.Equal(TileGroundProgram.Plain, Program(Naming(0, 1, 1, 0), plainSet));
        }
    }

    // Whatever the rest of the set carries, a refused relief layer throws before any resource exists. A map flag
    // without its texels beside plain layers is the case that once loaded as a plain material instead.
    [Theory]
    [InlineData("normalFlag", TileGroundReliefRefusal.MapFlagWithoutSurface)]
    [InlineData("roughnessFlag", TileGroundReliefRefusal.MapFlagWithoutSurface)]
    [InlineData("breakup", TileGroundReliefRefusal.FeatherBreakupOutsideRange)]
    public void A_refused_relief_layer_throws_before_any_resource_is_created(string rule, TileGroundReliefRefusal reason)
    {
        (FakeGpuResourceFactory factory, Scene3D scene) = NewScene();
        using (scene)
        {
            TileGroundLayerImage layer = rule switch
            {
                "normalFlag" => new TileGroundLayerImage { AlbedoRgba = new byte[4 * 4 * 4], NormalMapped = true },
                "roughnessFlag" => new TileGroundLayerImage { AlbedoRgba = new byte[4 * 4 * 4], RoughnessMapped = true },
                _ => Mapped(8 * 8),
            };
            if (rule == "breakup") layer.FeatherBreakup = 2f;
            var before = (factory.Textures.Count, factory.Buffers.Count, factory.ResourceSets.Count, factory.Samplers.Count);

            var ex = Assert.Throws<TileGroundReliefArgumentException>(() =>
                scene.LoadTileGroundMaterial(4, 4, new[] { Plain(), layer }, 8, 8));

            Assert.Equal(reason, ex.Reason);
            Assert.Equal(1, ex.LayerIndex);
            Assert.Equal(before,
                (factory.Textures.Count, factory.Buffers.Count, factory.ResourceSets.Count, factory.Samplers.Count));
        }
    }

    public enum LoadFault { SurfaceArray, ParamsBuffer, ParamsWrite, PlainSet, ReliefSet }

    // A relief material with a sampler override makes every kind of resource a load can: the albedo array, then the
    // surface array, the params buffer, the sampler, the plain set and the relief set. Each row refuses one of them
    // and names how many textures, buffers, samplers and sets exist by then, so it is known the refusal came there.
    [Theory]
    [InlineData(LoadFault.SurfaceArray, 1, 0, 0, 0)]
    [InlineData(LoadFault.ParamsBuffer, 2, 0, 0, 0)]
    [InlineData(LoadFault.ParamsWrite, 2, 1, 0, 0)]
    [InlineData(LoadFault.PlainSet, 2, 1, 1, 0)]
    [InlineData(LoadFault.ReliefSet, 2, 1, 1, 1)]
    public void A_load_the_backend_refuses_partway_frees_everything_it_made(LoadFault fault, int textures, int buffers,
        int samplers, int sets)
    {
        var device = new FakeGpuDevice();
        (FakeGpuResourceFactory factory, Scene3D scene) = NewScene(device);
        using (scene)
        {
            (int t, int b, int s, int r) = (factory.Textures.Count, factory.Buffers.Count, factory.Samplers.Count,
                factory.ResourceSets.Count);
            switch (fault)
            {
                case LoadFault.SurfaceArray: factory.FailTextureCreationAfter(1); break;
                case LoadFault.ParamsBuffer: factory.FailBufferCreationAfter(0); break;
                case LoadFault.ParamsWrite: device.OnBufferUpdate = _ => throw new InvalidOperationException("refused write"); break;
                case LoadFault.PlainSet: factory.FailResourceSetCreationAfter(0); break;
                case LoadFault.ReliefSet: factory.FailResourceSetCreationAfter(1); break;
            }

            Assert.Throws<InvalidOperationException>(() => scene.LoadTileGroundMaterial(4, 4,
                new[] { Mapped(8 * 8), Plain() }, 8, 8, sampler: TerrainSamplerConfig.Default));
            device.OnBufferUpdate = null;

            FakeTexture[] madeTextures = factory.Textures.Skip(t).ToArray();
            FakeBuffer[] madeBuffers = factory.Buffers.Skip(b).ToArray();
            FakeSampler[] madeSamplers = factory.Samplers.Skip(s).ToArray();
            FakeResourceSet[] madeSets = factory.ResourceSets.Skip(r).ToArray();
            Assert.Equal((textures, buffers, samplers, sets),
                (madeTextures.Length, madeBuffers.Length, madeSamplers.Length, madeSets.Length));
            Assert.All(madeTextures, texture => Assert.True(texture.Disposed));
            Assert.All(madeBuffers, buffer => Assert.True(buffer.Disposed));
            Assert.All(madeSamplers, sampler => Assert.True(sampler.Disposed));
            Assert.All(madeSets, set => Assert.True(set.Disposed));
            Assert.Equal(0, scene.LiveTileGroundMaterialCount);
        }
    }
}
