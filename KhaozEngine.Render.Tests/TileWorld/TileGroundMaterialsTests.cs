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
        0xff, 0x00, 0x00, 0xff, 0x00, 0xff, 0x00, 0xff,
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
        // The untextured sibling and the reserved layer are filled to the SAME size, which is what the one
        // texture array the set uploads into requires.
        AssertEveryTexelIs(Road, set.Layers[set.SlotOf(2)]);
        AssertEveryTexelIs(Magenta, set.Layers[set.MissingSlot]);
        Assert.All(set.Layers, layer => Assert.Equal(2 * 2 * 4, layer.AlbedoRgba.Length));
    }

    [Fact]
    public void A_textured_layer_of_another_size_throws_naming_the_material_and_both_sizes()
    {
        string first = Path.GetFullPath("first.png");
        string second = Path.GetFullPath("second.png");
        TileWorldCatalogs catalogs = InMemory(
            $"{{ \"id\": 1, \"name\": \"grass\", \"color\": \"#4d8a3a\", \"texture\": {JsonSerializer.Serialize(first)} }}," +
            $"{{ \"id\": 2, \"name\": \"road\", \"color\": \"#6a6a5a\", \"texture\": {JsonSerializer.Serialize(second)} }}");

        TileWorldException ex = Assert.Throws<TileWorldException>(() => TileGroundMaterials.Build(
            catalogs, path => path == first ? Solid(4, 4, 1, 2, 3) : Solid(2, 8, 4, 5, 6)));

        Assert.Contains("road", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2x8", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4x4", ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public void A_relative_texture_on_a_catalog_that_came_from_no_file_throws_naming_the_material()
    {
        TileWorldCatalogs catalogs = InMemory(
            "{ \"id\": 1, \"name\": \"grass\", \"color\": \"#4d8a3a\", \"texture\": \"textures/grass.png\" }");

        TileWorldException ex = Assert.Throws<TileWorldException>(() => TileGroundMaterials.Build(catalogs));

        Assert.Contains("grass", ex.Message, StringComparison.Ordinal);
        Assert.Contains("textures/grass.png", ex.Message, StringComparison.Ordinal);
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

        TileWorldException ex = Assert.Throws<TileWorldException>(() => TileGroundMaterials.Build(catalogs));

        Assert.Contains($"{TileGroundMaterialConfig.MaxMaterials}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hand_built_set_refuses_a_layer_that_is_not_the_size_it_declares()
    {
        var layers = new[]
        {
            new TileGroundLayerImage { AlbedoRgba = new byte[2 * 2 * 4] },
            new TileGroundLayerImage { AlbedoRgba = new byte[4] },
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => new TileGroundMaterialSet(2, 2, new ushort[] { 7 }, layers));

        Assert.Contains("2x2", ex.Message, StringComparison.Ordinal);
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

    // A catalog of `count` flat materials, ids 1 upward, which is the only way to reach the slot ceiling.
    static TileWorldCatalogs Materials(int count)
    {
        var json = new List<string>(count);
        for (int i = 1; i <= count; i++)
            json.Add($"{{ \"id\": {i}, \"name\": \"m{i}\", \"color\": \"#010203\" }}");
        return InMemory(string.Join(",", json));
    }
}
