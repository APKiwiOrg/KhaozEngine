using System;
using System.IO;
using System.Text.Json.Nodes;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileFoliageLayerPersistenceTests
{
    static TileFoliageLayer Layer(byte[]? density = null) => new(
        id: "meadow",
        plane: 0,
        originX: -2f,
        originZ: -3f,
        cellSize: 0.5f,
        width: 3,
        height: 2,
        density: density ?? [0, 64, 255, 12, 128, 220],
        seed: 91,
        spacing: 0.4f,
        scaleMin: 0.7f,
        scaleMax: 1.2f,
        rootOffset: -0.04f,
        archetypes: [new TileFoliageArchetype("bush", 3f), new TileFoliageArchetype("tree", 1f)],
        allowedUnderlays: [1],
        excludeIndoors: true,
        excludeSolidObjects: true,
        doorClearance: 1.25f,
        edgeFade: 0.75f);

    [Fact]
    public void Empty_foliage_keeps_the_old_manifest_and_world_hash_shape()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0));
        string before = TileWorldHash.OfWorld(doc);
        TileWorldFile.Save(doc, tmp.Sub("world"));

        string json = File.ReadAllText(tmp.Sub("world/world.json"));
        Assert.DoesNotContain("foliageLayers", json, StringComparison.Ordinal);
        Assert.Equal(before, TileWorldHash.OfWorld(TileWorldFile.Load(tmp.Sub("world"))));
    }

    [Fact]
    public void Foliage_round_trips_through_a_partially_loaded_world_and_changes_its_hash()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        string before = TileWorldHash.OfWorld(doc);
        doc.SetFoliageLayer(Layer());
        string withFoliage = TileWorldHash.OfWorld(doc);
        Assert.NotEqual(before, withFoliage);

        string dir = tmp.Sub("world");
        TileWorldFile.Save(doc, dir);
        TileWorldSource source = TileWorldSource.Open(dir);
        Assert.Empty(source.Document.Regions);
        Assert.Equal(withFoliage, TileWorldHash.OfWorld(source.Document));
        TileFoliageLayer back = Assert.Single(source.Document.FoliageLayers);
        Assert.Equal("meadow", back.Id);
        Assert.Equal(new byte[] { 0, 64, 255, 12, 128, 220 }, back.CopyDensity());
        Assert.Equal(new ushort[] { 1 }, back.AllowedUnderlays);

        TileWorldFile.Save(source.Document, dir);
        TileWorldDocument fullyLoaded = TileWorldFile.Load(dir);
        Assert.Equal(withFoliage, TileWorldHash.OfWorld(fullyLoaded));
        Assert.Single(fullyLoaded.FoliageLayers);
    }

    [Fact]
    public void Foliage_query_results_cannot_mutate_document_state()
    {
        TileWorldDocument doc = new();
        byte[] source = [1, 2, 3, 4, 5, 6];
        doc.SetFoliageLayer(Layer(source));
        source[0] = 99;
        byte[] copy = doc.GetFoliageLayer("meadow")!.CopyDensity();
        copy[1] = 88;

        Assert.Equal(1, doc.GetFoliageLayer("meadow")!.DensityAt(0, 0));
        Assert.Equal(2, doc.GetFoliageLayer("meadow")!.DensityAt(1, 0));
    }

    [Fact]
    public void Invalid_layers_are_rejected_before_mutation_or_save()
    {
        TileWorldDocument doc = new();
        TileFoliageLayer control = Layer();
        doc.SetFoliageLayer(control);
        doc.RemoveFoliageLayer(control.Id);
        var density = Assert.Throws<TileFoliageArgumentException>(() => Layer([1, 2]));
        Assert.Equal(TileWorldRefusal.FoliageDensityLengthMismatch, density.Reason);
        Assert.Equal("meadow", density.LayerId);
        Assert.Equal(2, density.ActualCount);
        Assert.Equal(6L, density.ExpectedCount);
        Assert.Equal(3, density.Width);
        Assert.Equal(2, density.Height);
        Assert.Empty(doc.FoliageLayers);

        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileWorldFile.Save(TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0)), dir);
        string manifest = TileWorldFile.ManifestPath(dir);
        string before = File.ReadAllText(manifest);
        string malformed = before.Replace("\"regions\":", "\"foliageLayers\":[{\"id\":\"bad\",\"width\":2,\"height\":2,\"density\":\"AQIDBA==\",\"spacing\":1,\"scaleMin\":1,\"scaleMax\":1,\"archetypes\":[{\"id\":\"bush\",\"weight\":1}]}],\"regions\":", StringComparison.Ordinal);
        File.WriteAllText(manifest, malformed);

        TileWorldException ex = Assert.Throws<TileWorldException>(() => TileWorldSource.Open(dir));
        Assert.Equal(TileWorldRefusal.FoliageCellSizeInvalid, ex.Reason);
        Assert.Equal(manifest, ex.SourceName);
        Assert.Equal("bad", ex.LayerId);
        Assert.Equal(0f, ex.CellSize);
        JsonNode corrected = JsonNode.Parse(malformed)!;
        corrected["foliageLayers"]![0]!["cellSize"] = 0.5f;
        File.WriteAllText(manifest, corrected.ToJsonString());
        Assert.Equal("bad", Assert.Single(TileWorldSource.Open(dir).Document.FoliageLayers).Id);
    }

    [Theory]
    [InlineData(0f, 3, 2, TileWorldRefusal.FoliageCellSizeInvalid)]
    [InlineData(0.5f, 0, 2, TileWorldRefusal.FoliageDimensionsNonPositive)]
    [InlineData(0.5f, 3, -1, TileWorldRefusal.FoliageDimensionsNonPositive)]
    public void Invalid_dimensions_fail_clearly(float cellSize, int width, int height, TileWorldRefusal reason)
    {
        static TileFoliageLayer Build(float cell, int w, int h) => new(
            "bad", 0, 0f, 0f, cell, w, h, new byte[6], 1, 1f, 1f, 1f, 0f,
            [new TileFoliageArchetype("bush", 1f)], [1], true, true, 0f, 0f);
        Assert.Equal(6, Build(0.5f, 3, 2).CopyDensity().Length);
        // A non-positive dimension cannot have a matching density raster. The reason identifies the guard.
        var ex = Assert.Throws<TileFoliageArgumentException>(() => Build(cellSize, width, height));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal("bad", ex.LayerId);
        if (reason == TileWorldRefusal.FoliageCellSizeInvalid) Assert.Equal(cellSize, ex.CellSize);
        else
        {
            Assert.Equal(width, ex.Width);
            Assert.Equal(height, ex.Height);
        }
    }

    [Fact]
    public void Bad_archetype_ids_and_weights_fail_clearly()
    {
        static TileFoliageLayer Build(TileFoliageArchetype row) => new(
            "bad", 0, 0f, 0f, 1f, 1, 1, [255], 1, 1f, 1f, 1f, 0f, [row], [1], true, true, 0f, 0f);
        Assert.Single(Build(new TileFoliageArchetype("bush", 1f)).Archetypes);
        var blank = Assert.Throws<TileFoliageArgumentException>(() => Build(new TileFoliageArchetype("", 1f)));
        Assert.Equal(TileWorldRefusal.FoliageArchetypeIdBlank, blank.Reason);
        Assert.Equal("bad", blank.LayerId);
        Assert.Equal("", blank.ArchetypeId);
        Assert.Equal(0, blank.ArchetypeIndex);
        var weight = Assert.Throws<TileFoliageArgumentException>(() => Build(new TileFoliageArchetype("bush", 0f)));
        Assert.Equal(TileWorldRefusal.FoliageWeightInvalid, weight.Reason);
        Assert.Equal("bad", weight.LayerId);
        Assert.Equal("bush", weight.ArchetypeId);
        Assert.Equal(0f, weight.Weight);
    }
}
