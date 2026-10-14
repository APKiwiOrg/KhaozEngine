using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabExtractionSupportTests
{
    [Fact]
    public void Extraction_KeepsMissingYAndWorldSupportRequest()
    {
        var f = PrefabFixtures.SpacelessRoomless();
        f.Document.Placements.Add(new()
        {
            Id = "bound-item",
            Kind = "item",
            AssetId = "item",
            X = 1,
            Y = null,
            Z = -1,
            SupportBinding = new(MapSupportBindingKind.Surface, "ground", null, 0, 4, 4)
        });
        var asset = MapPrefabExtraction.Extract(f.Document, f.Assets,
            new[] { PrefabFixtures.World(MapPrefabChildKind.Placement, "bound-item") }, new(Vector3.Zero, 0, 1), "bound-item");
        var placement = asset.Children.OfType<MapPrefabPlacementChild>().Single();
        Assert.Null(placement.Value.Y);
        Assert.Equal(f.Document.Placements.Single().SupportBinding, placement.Value.SupportBinding);
        Assert.Equal(MapPrefabReferenceScope.World, placement.References.Single().Target.Scope);
    }
}
