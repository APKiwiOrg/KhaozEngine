using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabTiledSnapshotTests
{
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void StructuralSnapshot_RetainsNonFiniteSculptRefusal(float delta)
    {
        var source = PrefabFixtures.Create("roomless").Document;
        source.TerrainOverrides = new();
        source.TerrainOverrides.SetDelta(0, 0, delta);
        Assert.Throws<ArgumentException>(() => MapDocumentFile.SaveText(source));
        Assert.Throws<MapDocumentException>(() => MapDocumentSnapshot.Copy(source, MapDocRegistry.CreateDefault()));
    }

    [Fact]
    public void TiledExpansion_ExceedsEmbeddingLimitWithoutChangingPrefabResults()
    {
        var small = PrefabFixtures.Create("roomless");
        var expected = MapPrefabResolver.Expand(small.Document, small.Assets);
        var large = PrefabTiledSnapshotFixtures.Create(257);
        string root = Path.Combine(Path.GetTempPath(), "khaozengine-task15-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveTiled(large.Document, root);
            Assert.Empty(MapDocumentFile.VerifyTiled(root));
            var loaded = MapDocumentFile.LoadTiled(root);
            Assert.Equal("surface embedding exceeds its limit, use tiled storage",
                Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(loaded)).Message);
            using var stream = new MemoryStream();
            Assert.Equal("surface embedding exceeds its limit, use tiled storage",
                Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTo(loaded, stream)).Message);
            Assert.Equal(0, stream.Length);
            string monolithicPath = Path.Combine(root, "oversized.json");
            File.WriteAllText(monolithicPath, "existing target");
            Assert.Equal("surface embedding exceeds its limit, use tiled storage",
                Assert.Throws<MapDocumentException>(() => MapDocumentFile.Save(loaded, monolithicPath)).Message);
            Assert.Equal("existing target", File.ReadAllText(monolithicPath));
            var actual = MapPrefabResolver.Expand(loaded, large.Assets);
            Assert.Equal(expected.Placements.Select(p => (p.PlacementId, p.AssetId, p.Transform)),
                actual.Placements.Select(p => (p.PlacementId, p.AssetId, p.Transform)));
            Assert.Equal(expected.BuildingIntent.Roofs, actual.BuildingIntent.Roofs);
            Assert.Equal(expected.BuildingIntent.Lights, actual.BuildingIntent.Lights);
            Assert.Equal(expected.BuildingIntent.Markers, actual.BuildingIntent.Markers);
            Assert.Equal(257, actual.Surfaces.Patches.Count);
            Assert.Throws<MapDocumentException>(() => MapPrefabResolver.Expand(loaded, large.Assets, new(MaxPatches: 256)));
            loaded.Surfaces.Patches.First().Value.Heights[0] = 123;
            Assert.All(actual.Surfaces.Patches.Values, p => Assert.Equal(0, p.Heights[0]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
