using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Imaging;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The material set a tile world's ground draws with: one layer per catalog material in ascending id
/// order, a reserved magenta layer last, textures decoded relative to the catalog file that declared them, and a
/// flat colour layer for every material that carries no texture. Every test here is headless, and the one that
/// reads a real PNG writes it first with <see cref="PngWriter"/> rather than shipping an image in the repo.</summary>
public class TileGroundMaterialsTests
{
    // The greybox catalog's six colours, as the bytes a flat layer must be filled with.
    static readonly byte[] Grass = { 0x4d, 0x8a, 0x3a, 0xff };
    static readonly byte[] Road = { 0x6a, 0x6a, 0x5a, 0xff };
    static readonly byte[] Magenta = { 0xff, 0x00, 0xff, 0xff };

    // A 2x2 image whose four texels are all different, so a layer that lost its row order or its channel order
    // fails rather than passing on a uniform fill.
    static readonly byte[] Checker =
    {
        0xff, 0x00, 0x00, 0x40, 0x00, 0xff, 0x00, 0xff,
        0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0x00, 0xff,
    };

    static ImageRgba Image2x2() => new((byte[])Checker.Clone(), 2, 2);

    static ImageRgba Solid(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 4] = r; pixels[i * 4 + 1] = g; pixels[i * 4 + 2] = b; pixels[i * 4 + 3] = 0xff;
        }
        return new ImageRgba(pixels, width, height);
    }

    // A catalog built in memory, which is the case that can carry no relative texture path.
    static TileWorldCatalogs InMemory(string materialsJson) =>
        TileWorldCatalogs.LoadJson($"{{ \"materials\": [{materialsJson}] }}", "memory");

    static void AssertEveryTexelIs(byte[] expectedRgba, TileGroundLayerImage layer)
    {
        Assert.Equal(0, layer.AlbedoRgba.Length % 4);
        for (int i = 0; i < layer.AlbedoRgba.Length; i += 4)
            Assert.Equal(expectedRgba, layer.AlbedoRgba.Skip(i).Take(4).ToArray());
    }

    [Fact]
    public void Every_untextured_material_becomes_a_flat_layer_of_its_catalog_colour()
    {
        TileGroundMaterialSet set = TileGroundMaterials.Build(TileRenderTestData.Catalogs);

        // Nothing is textured, so the set is the smallest one that can hold six flat fills.
        Assert.Equal(1, set.Width);
        Assert.Equal(1, set.Height);
        Assert.Equal(7, set.Layers.Count);
        Assert.Equal(0, set.SurfaceWidth);
        Assert.Equal(0, set.SurfaceHeight);
        Assert.All(set.Layers, AssertPlain);
        AssertEveryTexelIs(Grass, set.Layers[set.SlotOf(TileRenderTestData.Grass)]);
        AssertEveryTexelIs(Road, set.Layers[set.SlotOf(TileRenderTestData.Road)]);
        // White, because the LAYER is the colour: a tint would multiply it in twice.
        Assert.Equal(new Vector4(1f, 1f, 1f, 1f), (Vector4)set.Layers[0].Tint);
    }

    [Fact]
    public void The_last_layer_is_the_reserved_magenta_a_dangling_id_lands_on()
    {
        TileGroundMaterialSet set = TileGroundMaterials.Build(TileRenderTestData.Catalogs);

        Assert.Equal(set.Layers.Count - 1, set.MissingSlot);
        AssertEveryTexelIs(Magenta, set.Layers[set.MissingSlot]);
        Assert.Equal(set.MissingSlot, set.SlotOf(99));
        Assert.Equal(set.MissingSlot, set.SlotOf(0));
    }

    [Fact]
    public void Slots_run_in_ascending_catalog_id_order()
    {
        TileGroundMaterialSet set = TileGroundMaterials.Build(TileRenderTestData.Catalogs);

        Assert.Equal(new ushort[] { 1, 2, 3, 4, 5, 6 }, set.MaterialIds.ToArray());
        for (int slot = 0; slot < set.MaterialIds.Count; slot++)
            Assert.Equal(slot, set.SlotOf(set.MaterialIds[slot]));
    }

    [Fact]
    public void A_textured_material_is_decoded_through_the_loader_and_sets_the_size()
    {
        string texture = Path.GetFullPath("grass-albedo.png");
        TileWorldCatalogs catalogs = InMemory(
            $"{{ \"id\": 1, \"name\": \"grass\", \"color\": \"#4d8a3a\", \"texture\": {JsonSerializer.Serialize(texture)} }}," +
            "{ \"id\": 2, \"name\": \"road\", \"color\": \"#6a6a5a\" }");

        var asked = new List<string>();
        TileGroundMaterialSet set = TileGroundMaterials.Build(catalogs, path =>
        {
            asked.Add(path);
            return Image2x2();
        });

        // An absolute texture path is taken as written, so nothing was resolved against a catalog file.
        Assert.Equal(new[] { texture }, asked.ToArray());
        Assert.Equal(2, set.Width);
        Assert.Equal(2, set.Height);
        Assert.Equal(Checker, set.Layers[set.SlotOf(1)].AlbedoRgba);
        Assert.Equal(0, set.SurfaceWidth);
        Assert.Equal(0, set.SurfaceHeight);
        Assert.All(set.Layers, AssertPlain);
        // The untextured sibling and the reserved layer are filled to the SAME size, which is what the one
        // texture array the set uploads into requires.
        AssertEveryTexelIs(Road, set.Layers[set.SlotOf(2)]);
        AssertEveryTexelIs(Magenta, set.Layers[set.MissingSlot]);
        Assert.All(set.Layers, layer => Assert.Equal(2 * 2 * 4, layer.AlbedoRgba.Length));
    }

    [Theory]
    [InlineData("texture", "texture")]
    [InlineData("texture", "heightTexture")]
    [InlineData("heightTexture", "texture")]
    [InlineData("heightTexture", "heightTexture")]
    [InlineData("normalTexture", "roughnessTexture")]
    [InlineData("roughnessTexture", "normalTexture")]
    public void A_textured_layer_of_another_size_throws_naming_the_material_and_both_sizes(string referenceField, string field)
    {
        string first = Path.GetFullPath("first.png");
        string second = Path.GetFullPath("second.png");
        TileWorldCatalogs catalogs = InMemory(
            $"{{ \"id\": 1, \"name\": \"grass\", \"color\": \"#4d8a3a\", \"{referenceField}\": {JsonSerializer.Serialize(first)} }}," +
            $"{{ \"id\": 2, \"name\": \"road\", \"color\": \"#6a6a5a\", \"{field}\": {JsonSerializer.Serialize(second)} }}");

        TileWorldException ex = Assert.Throws<TileWorldException>(() => TileGroundMaterials.Build(
            catalogs, path => path == first ? Solid(4, 4, 1, 2, 3) : Solid(2, 8, 4, 5, 6)));

        Assert.Equal(TileWorldRefusal.GroundMaterialTextureSizeMismatch, ex.Reason);
        Assert.Equal((ushort)2, ex.MaterialId);
        Assert.Equal("road", ex.MaterialName);
        Assert.Equal(field, ex.Field);
        Assert.Equal(2, ex.Width);
        Assert.Equal(8, ex.Height);
        Assert.Equal((ushort)1, ex.ReferenceMaterialId);
        Assert.Equal("grass", ex.ReferenceMaterialName);
        Assert.Equal(4, ex.ExpectedWidth);
        Assert.Equal(4, ex.ExpectedHeight);

        TileGroundMaterialSet control = TileGroundMaterials.Build(catalogs, _ => Solid(4, 4, 1, 2, 3));
        Assert.Equal(4, referenceField is "normalTexture" or "roughnessTexture" ? control.SurfaceWidth : control.Width);
        Assert.Equal(4, referenceField is "normalTexture" or "roughnessTexture" ? control.SurfaceHeight : control.Height);
    }

    [Fact]
    public void A_relative_texture_resolves_against_the_catalog_FILE_that_declared_it()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "textures"));
        PngWriter.Save(Path.Combine(temp.Path, "textures", "grass.png"), Checker, 2, 2);
        string catalogPath = temp.Sub("ground.catalog.json");
        File.WriteAllText(catalogPath,
            "{ \"materials\": [ { \"id\": 1, \"name\": \"grass\", \"color\": \"#4d8a3a\", " +
            "\"texture\": \"textures/grass.png\" } ] }");

        // No loader, so this decodes the PNG for real through ImageRgba.Load.
        TileGroundMaterialSet set = TileGroundMaterials.Build(TileWorldCatalogs.Load(new[] { catalogPath }));

        Assert.Equal(2, set.Width);
        Assert.Equal(2, set.Height);
        Assert.Equal(Checker, set.Layers[set.SlotOf(1)].AlbedoRgba);
    }

    [Theory]
    [InlineData("texture")]
    [InlineData("normalTexture")]
    [InlineData("roughnessTexture")]
    [InlineData("heightTexture")]
    public void A_relative_texture_on_a_catalog_that_came_from_no_file_throws_naming_the_material(string field)
    {
        TileWorldCatalogs catalogs = InMemory(
            $$"""{ "id": 1, "name": "grass", "color": "#4d8a3a", "{{field}}": "textures/grass.png" }""");

        TileWorldException ex = Assert.Throws<TileWorldException>(() => TileGroundMaterials.Build(catalogs));

        Assert.Equal(TileWorldRefusal.GroundMaterialTextureSourceMissing, ex.Reason);
        Assert.Equal((ushort)1, ex.MaterialId);
        Assert.Equal("grass", ex.MaterialName);
        Assert.Equal("textures/grass.png", ex.TexturePath);
        Assert.Equal(field, ex.Field);
        catalogs.Material(1)!.Texture = null;
        catalogs.Material(1)!.NormalTexture = null;
        catalogs.Material(1)!.RoughnessTexture = null;
        catalogs.Material(1)!.HeightTexture = null;
        Assert.Equal(0, TileGroundMaterials.Build(catalogs).SurfaceWidth);
    }

    [Theory]
    [InlineData(false, GroundMaterialAntiTiling.Offset, TileGroundAntiTiling.Offset)]
    [InlineData(true, GroundMaterialAntiTiling.Rotate, TileGroundAntiTiling.Rotate)]
    public void Height_green_becomes_albedo_alpha_without_mutating_loader_pixels(
        bool textured, GroundMaterialAntiTiling antiTiling, TileGroundAntiTiling mappedAntiTiling)
    {
        TileWorldCatalogs catalogs = InMemory("""{ "id": 7, "name": "stone", "color": "#123456" }""");
        GroundMaterial material = catalogs.Material(7)!;
        string albedoPath = Path.GetFullPath("albedo.png"), heightPath = Path.GetFullPath("height.png");
        material.Texture = textured ? albedoPath : null;
        material.HeightTexture = heightPath;
        material.HeightBlendSharpness = 16;
        material.Feather = new() { Breakup = 0.75f };
        material.AntiTiling = antiTiling;
        ImageRgba albedo = Image2x2();
        var heightBytes = new byte[] { 1, 10, 3, 4, 5, 20, 7, 8, 9, 30, 11, 12, 13, 40, 15, 16 };
        TileGroundMaterialSet set = TileGroundMaterials.Build(catalogs,
            path => path == albedoPath ? albedo : new ImageRgba(heightBytes, 2, 2));
        TileGroundLayerImage layer = set.Layers[set.SlotOf(7)];
        byte[] expected = textured ? (byte[])Checker.Clone() : new byte[]
            { 0x12, 0x34, 0x56, 255, 0x12, 0x34, 0x56, 255, 0x12, 0x34, 0x56, 255, 0x12, 0x34, 0x56, 255 };
        expected[3] = 10; expected[7] = 20; expected[11] = 30; expected[15] = 40;
        Assert.Equal(expected, layer.AlbedoRgba);
        Assert.True(layer.HeightMapped);
        Assert.Equal(material.HeightBlendSharpness, layer.HeightBlendSharpness);
        Assert.Equal(material.Feather.Breakup, layer.FeatherBreakup);
        Assert.Equal(mappedAntiTiling, layer.AntiTiling);
        Assert.Equal(Checker, albedo.Pixels);
        Assert.Equal(new byte[] { 1, 10, 3, 4, 5, 20, 7, 8, 9, 30, 11, 12, 13, 40, 15, 16 }, heightBytes);
        AssertPlain(set.Layers[set.MissingSlot]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Surface_packs_normal_rgb_and_roughness_green_at_a_separate_size(bool normal, bool roughness)
    {
        TileWorldCatalogs catalogs = InMemory("""{ "id": 7, "name": "stone", "color": "#123456" }""");
        GroundMaterial material = catalogs.Material(7)!;
        string albedo = Path.GetFullPath("albedo.png"), normalPath = Path.GetFullPath("normal.png"), roughnessPath = Path.GetFullPath("roughness.png");
        material.Texture = albedo;
        material.NormalTexture = normal ? normalPath : null;
        material.RoughnessTexture = roughness ? roughnessPath : null;
        TileGroundMaterialSet set = TileGroundMaterials.Build(catalogs, path => path == albedo ? Image2x2()
            : path == normalPath ? new ImageRgba(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 2, 1)
            : new ImageRgba(new byte[] { 20, 21, 22, 23, 24, 25, 26, 27 }, 2, 1));
        TileGroundLayerImage layer = set.Layers[set.SlotOf(7)];
        Assert.Equal(2, set.Width);
        Assert.Equal(2, set.Height);
        Assert.Equal(2, set.SurfaceWidth);
        Assert.Equal(1, set.SurfaceHeight);
        Assert.Equal(new byte[] { normal ? (byte)1 : (byte)128, normal ? (byte)2 : (byte)128, normal ? (byte)3 : (byte)255,
            roughness ? (byte)21 : (byte)128, normal ? (byte)5 : (byte)128, normal ? (byte)6 : (byte)128,
            normal ? (byte)7 : (byte)255, roughness ? (byte)25 : (byte)128 }, layer.SurfaceRgba);
        Assert.Equal(normal, layer.NormalMapped);
        Assert.Equal(roughness, layer.RoughnessMapped);
        Assert.Equal(Checker, layer.AlbedoRgba);
        AssertPlain(set.Layers[set.MissingSlot]);
    }

    [Fact]
    public void Texture_precedes_height_when_one_material_fixes_the_albedo_size()
    {
        TileWorldCatalogs catalogs = InMemory("""{ "id": 7, "name": "stone", "color": "#123456" }""");
        catalogs.Material(7)!.Texture = Path.GetFullPath("albedo.png");
        catalogs.Material(7)!.HeightTexture = Path.GetFullPath("height.png");
        TileWorldException ex = Assert.Throws<TileWorldException>(() => TileGroundMaterials.Build(catalogs,
            path => path == catalogs.Material(7)!.Texture ? Image2x2() : Solid(1, 1, 0, 0, 0)));
        Assert.Equal(TileWorldRefusal.GroundMaterialTextureSizeMismatch, ex.Reason);
        Assert.Equal("heightTexture", ex.Field);
        Assert.Equal((ushort)7, ex.ReferenceMaterialId);
        Assert.Equal(2, ex.ExpectedWidth);
        Assert.Equal(2, ex.ExpectedHeight);
        Assert.Equal(2, TileGroundMaterials.Build(catalogs, _ => Image2x2()).Width);
    }

    [Fact]
    public void Build_refuses_invalid_relief_before_decoding()
    {
        TileWorldCatalogs catalogs = InMemory("""{ "id": 7, "name": "stone", "color": "#123456", "texture": "relative.png" }""");
        catalogs.Material(7)!.Feather = new() { Breakup = 2 };
        TileWorldException ex = Assert.Throws<TileWorldException>(() => TileGroundMaterials.Build(catalogs));
        Assert.Equal(TileWorldRefusal.GroundMaterialFeatherBreakupOutsideRange, ex.Reason);
        Assert.Equal("ground material set", ex.SourceName);
        Assert.Equal("feather.breakup", ex.Field);
        catalogs.Material(7)!.Feather!.Breakup = 1;
        catalogs.Material(7)!.Texture = Path.GetFullPath("albedo.png");
        Assert.Equal(1f, TileGroundMaterials.Build(catalogs, _ => Image2x2()).Layers[0].FeatherBreakup);
    }

    [Fact]
    public void Catalog_sharpness_bounds_match_the_renderer()
    {
        Assert.Equal(TileGroundRelief.MinimumHeightBlendSharpness, GroundMaterialRelief.MinimumHeightBlendSharpness);
        Assert.Equal(TileGroundRelief.MaximumHeightBlendSharpness, GroundMaterialRelief.MaximumHeightBlendSharpness);
    }

    static void AssertPlain(TileGroundLayerImage layer)
    {
        Assert.Null(layer.SurfaceRgba);
        Assert.False(layer.NormalMapped);
        Assert.False(layer.RoughnessMapped);
        Assert.False(layer.HeightMapped);
        Assert.Equal(TileGroundRelief.DefaultHeightBlendSharpness, layer.HeightBlendSharpness);
        Assert.Equal(0f, layer.FeatherBreakup);
        Assert.Equal(TileGroundAntiTiling.None, layer.AntiTiling);
    }

    [Fact]
    public void A_hand_built_set_refuses_wrong_surface_byte_length_with_surface_dimensions()
    {
        var layers = new[] { new TileGroundLayerImage { AlbedoRgba = new byte[4], NormalMapped = true,
            SurfaceRgba = new byte[4] }, new TileGroundLayerImage { AlbedoRgba = new byte[4] } };
        var ex = Assert.Throws<TileGroundMaterialSetArgumentException>(() =>
            new TileGroundMaterialSet(1, 1, new ushort[] { 7 }, layers, 2, 3));
        Assert.Equal(TileGroundMaterialSetRefusal.SurfaceByteLengthMismatch, ex.Reason);
        Assert.Equal(0, ex.LayerIndex);
        Assert.Equal(4, ex.ActualByteLength);
        Assert.Equal(24, ex.ExpectedByteLength);
        Assert.Equal(2, ex.Width);
        Assert.Equal(3, ex.Height);
        layers[0].SurfaceRgba = new byte[24];
        var control = new TileGroundMaterialSet(1, 1, new ushort[] { 7 }, layers, 2, 3);
        Assert.Equal(2, control.SurfaceWidth);
        Assert.Equal(3, control.SurfaceHeight);
    }

    [Theory]
    [InlineData(0, 1, "surfaceWidth")]
    [InlineData(-1, 1, "surfaceWidth")]
    [InlineData(1, 0, "surfaceHeight")]
    [InlineData(1, -1, "surfaceHeight")]
    public void Surface_images_require_positive_surface_dimensions(int width, int height, string parameter)
    {
        var layers = new[] { new TileGroundLayerImage { AlbedoRgba = new byte[4], NormalMapped = true,
            SurfaceRgba = new byte[4] }, new TileGroundLayerImage { AlbedoRgba = new byte[4] } };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TileGroundMaterialSet(1, 1, new ushort[] { 7 }, layers, width, height));
        Assert.Equal(parameter, ex.ParamName);
        Assert.Equal(1, new TileGroundMaterialSet(1, 1, new ushort[] { 7 }, layers, 1, 1).SurfaceWidth);
    }

    [Theory]
    [InlineData("surface")]
    [InlineData("height")]
    [InlineData("breakup")]
    [InlineData("antiTiling")]
    public void The_reserved_missing_layer_must_stay_plain(string member)
    {
        var missing = new TileGroundLayerImage { AlbedoRgba = new byte[4] };
        var layers = new[] { new TileGroundLayerImage { AlbedoRgba = new byte[4] }, missing };
        switch (member)
        {
            case "surface": missing.SurfaceRgba = new byte[4]; missing.NormalMapped = true; break;
            case "height": missing.HeightMapped = true; break;
            case "breakup": missing.FeatherBreakup = 1; break;
            case "antiTiling": missing.AntiTiling = TileGroundAntiTiling.Offset; break;
        }
        var ex = Assert.Throws<TileGroundMaterialSetArgumentException>(() =>
            new TileGroundMaterialSet(1, 1, new ushort[] { 7 }, layers, 1, 1));
        Assert.Equal(TileGroundMaterialSetRefusal.MissingLayerHasRelief, ex.Reason);
        Assert.Equal(1, ex.LayerIndex);
        layers[1] = new() { AlbedoRgba = new byte[4] };
        AssertPlain(new TileGroundMaterialSet(1, 1, new ushort[] { 7 }, layers, 1, 1).Layers[1]);
    }

    [Fact]
    public void A_relief_refusal_surfaces_from_the_set_with_its_layer_slot()
    {
        var layers = new[] { new TileGroundLayerImage { AlbedoRgba = new byte[4] },
            new TileGroundLayerImage { AlbedoRgba = new byte[4], NormalMapped = true },
            new TileGroundLayerImage { AlbedoRgba = new byte[4] } };
        var ex = Assert.Throws<TileGroundReliefArgumentException>(() =>
            new TileGroundMaterialSet(1, 1, new ushort[] { 7, 8 }, layers));
        Assert.Equal(TileGroundReliefRefusal.MapFlagWithoutSurface, ex.Reason);
        Assert.Equal(1, ex.LayerIndex);
        layers[1].NormalMapped = false;
        Assert.Equal(1, new TileGroundMaterialSet(1, 1, new ushort[] { 7, 8 }, layers).SlotOf(8));
    }

    [Fact]
    public void TilesPerMetre_is_the_catalog_override_or_a_two_metre_repeat()
    {
        TileWorldCatalogs catalogs = InMemory(
            "{ \"id\": 1, \"name\": \"grass\", \"color\": \"#4d8a3a\" }," +
            "{ \"id\": 2, \"name\": \"road\", \"color\": \"#6a6a5a\", \"tilesPerMetre\": 4 }");

        TileGroundMaterialSet set = TileGroundMaterials.Build(catalogs);

        Assert.Equal(TileGroundMaterials.DefaultTilesPerMetre, set.Layers[set.SlotOf(1)].TilesPerMetre);
        Assert.Equal(4f, set.Layers[set.SlotOf(2)].TilesPerMetre);
        Assert.Equal(TileGroundMaterials.DefaultTilesPerMetre, set.Layers[set.MissingSlot].TilesPerMetre);
    }

    [Fact]
    public void A_catalog_that_fills_every_slot_but_the_reserved_one_still_builds()
    {
        TileGroundMaterialSet set = TileGroundMaterials.Build(Materials(TileGroundMaterialConfig.MaxMaterials - 1));

        Assert.Equal(TileGroundMaterialConfig.MaxMaterials, set.Layers.Count);
        Assert.Equal(TileGroundMaterialConfig.MaxMaterials - 1, set.MissingSlot);
    }

    [Fact]
    public void A_catalog_too_large_for_one_set_throws_rather_than_dropping_materials()
    {
        TileWorldCatalogs catalogs = Materials(TileGroundMaterialConfig.MaxMaterials);

        var ex = Assert.Throws<TileWorldException>(() => TileGroundMaterials.Build(catalogs));
        Assert.Equal(TileWorldRefusal.GroundMaterialLayerLimitExceeded, ex.Reason);
        Assert.Equal(TileGroundMaterialConfig.MaxMaterials, ex.MaterialCount);
        Assert.Equal(TileGroundMaterialConfig.MaxMaterials - 1, ex.MaximumMaterialCount);
    }

    [Fact]
    public void A_hand_built_set_refuses_a_layer_that_is_not_the_size_it_declares()
    {
        var layers = new[]
        {
            new TileGroundLayerImage { AlbedoRgba = new byte[2 * 2 * 4] },
            new TileGroundLayerImage { AlbedoRgba = new byte[4] },
        };

        var ex = Assert.Throws<TileGroundMaterialSetArgumentException>(
            () => new TileGroundMaterialSet(2, 2, new ushort[] { 7 }, layers));
        Assert.IsAssignableFrom<ArgumentException>(ex);
        Assert.Equal(TileGroundMaterialSetRefusal.LayerByteLengthMismatch, ex.Reason);
        Assert.Equal(1, ex.LayerIndex);
        Assert.Equal(4, ex.ActualByteLength);
        Assert.Equal(16, ex.ExpectedByteLength);
        Assert.Equal(2, ex.Width);
        Assert.Equal(2, ex.Height);

        Assert.Equal("layers", ex.ParamName);
    }

    [Fact]
    public void A_hand_built_set_maps_the_ids_it_was_given_and_nothing_else()
    {
        var layers = new[]
        {
            new TileGroundLayerImage { AlbedoRgba = new byte[4] },
            new TileGroundLayerImage { AlbedoRgba = new byte[4] },
            new TileGroundLayerImage { AlbedoRgba = new byte[4] },
        };

        var set = new TileGroundMaterialSet(1, 1, new ushort[] { 40, 7 }, layers);

        Assert.Equal(0, set.SlotOf(40));
        Assert.Equal(1, set.SlotOf(7));
        Assert.Equal(2, set.MissingSlot);
        Assert.Equal(2, set.SlotOf(41));
    }

    [Theory]
    [InlineData(0, 0, TileGroundMaterialSetRefusal.LayerCountMismatch)]
    [InlineData(TileGroundMaterialConfig.MaxMaterials, TileGroundMaterialConfig.MaxMaterials + 1,
        TileGroundMaterialSetRefusal.LayerLimitExceeded)]
    public void A_hand_built_set_refuses_an_invalid_layer_count(
        int materialCount, int layerCount, TileGroundMaterialSetRefusal reason)
    {
        var ids = new ushort[materialCount];
        for (int i = 0; i < ids.Length; i++) ids[i] = (ushort)i;
        var layers = new TileGroundLayerImage[layerCount];
        for (int i = 0; i < layers.Length; i++) layers[i] = new() { AlbedoRgba = new byte[4] };

        var error = Assert.Throws<TileGroundMaterialSetArgumentException>(() =>
            new TileGroundMaterialSet(1, 1, ids, layers));

        Assert.Equal("layers", error.ParamName);
        Assert.Equal(reason, error.Reason);
        if (reason == TileGroundMaterialSetRefusal.LayerCountMismatch)
        {
            Assert.Equal(materialCount, error.MaterialCount);
            Assert.Equal(layerCount, error.ActualLayerCount);
            Assert.Equal(materialCount + 1, error.RequiredLayerCount);
        }
        else
        {
            Assert.Equal(layerCount, error.LayerCount);
            Assert.Equal(TileGroundMaterialConfig.MaxMaterials, error.MaximumLayerCount);
        }
    }

    [Fact]
    public void A_hand_built_set_refuses_duplicate_material_ids()
    {
        var layers = new[]
        {
            new TileGroundLayerImage { AlbedoRgba = new byte[4] },
            new TileGroundLayerImage { AlbedoRgba = new byte[4] },
            new TileGroundLayerImage { AlbedoRgba = new byte[4] },
        };

        var error = Assert.Throws<TileGroundMaterialSetArgumentException>(() =>
            new TileGroundMaterialSet(1, 1, new ushort[] { 7, 7 }, layers));

        Assert.Equal("materialIds", error.ParamName);
        Assert.Equal(TileGroundMaterialSetRefusal.DuplicateMaterialId, error.Reason);
        Assert.Equal((ushort)7, error.MaterialId);
        Assert.Equal(0, error.FirstSlot);
        Assert.Equal(1, error.DuplicateSlot);
        Assert.Equal(1, new TileGroundMaterialSet(1, 1, new ushort[] { 7, 8 }, layers).SlotOf(8));
    }

    // A catalog of `count` flat materials, ids 1 upward, which is the only way to reach the slot ceiling.
    static TileWorldCatalogs Materials(int count)
    {
        var json = new List<string>(count);
        for (int i = 1; i <= count; i++)
            json.Add($"{{ \"id\": {i}, \"name\": \"m{i}\", \"color\": \"#010203\" }}");
        return InMemory(string.Join(",", json));
    }
}
