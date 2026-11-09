using System;
using System.IO;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapCompleteBuilderRegressionTests
{
    [Fact]
    public void CompleteEntryPoints_KeepRefusingPartialDocuments()
    {
        var fixture = PrefabFixtures.Create();
        fixture.Document.Placements.Add(new()
        {
            Id = "off-window",
            Kind = "item",
            AssetId = "item",
            X = 20,
            Z = 20,
            Y = 0,
            NumericId = ++fixture.Document.NumericIdHighWaterMark
        });
        string directory = Path.Combine(Path.GetTempPath(), "khaozengine-complete-builder-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveTiled(fixture.Document, directory);
            var partial = MapDocumentFile.LoadTiled(directory, new MapTileRect(new(-1, -1), new(-1, -1)));
            Assert.True(partial.Tiles!.IsPartial);
            Assert.Throws<MapDocumentException>(() => MapPrefabResolver.Expand(partial, fixture.Assets));
            Assert.Throws<MapDocumentException>(() => MapWorldBuilder.Build(partial, fixture.Assets,
                new(new("public-scene", 1, "scene/1", 2), "scene/1", new())));
            Assert.Equal(MapFoliageStatus.PartialResidency, MapFoliageGenerationContext.Capture(partial, fixture.Assets).Status);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
