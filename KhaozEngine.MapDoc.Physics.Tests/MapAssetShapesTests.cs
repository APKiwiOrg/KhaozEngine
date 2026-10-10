using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>How an asset's collider and selection resources read out of a verified closure, and which shape data
/// is refused rather than installed.</summary>
public class MapAssetShapesTests
{
    [Fact]
    public void CompoundCollider_ReadsAsSolidWithItsDigest()
    {
        var f = NativeWorldFixtures.Assets();
        var shapes = MapAssetShapes.Read(f.Closure, "doorway");
        Assert.True(shapes.IsSolid);
        Assert.IsType<CompoundShape>(shapes.Collider);
        Assert.Equal(f.Closure.GetResource("doorway.collider").Reference.Sha256, shapes.ColliderSha256);
        Assert.Null(shapes.Selection);
    }

    [Fact]
    public void SelectionOnlyAsset_IsNotSolid()
    {
        var f = NativeWorldFixtures.Assets();
        var shapes = MapAssetShapes.Read(f.Closure, "examine-sign");
        Assert.False(shapes.IsSolid);
        Assert.Null(shapes.Collider);
        Assert.Null(shapes.ColliderSha256);
        Assert.IsType<BoxShape>(shapes.Selection);
        Assert.Equal(f.Closure.GetResource("examine-sign.selection").Reference.Sha256, shapes.SelectionSha256);
        Assert.Equal("examine-sign", shapes.AssetId);
        Assert.Equal(1f, shapes.SourceUnitsToMetres);
    }

    [Theory]
    [InlineData("garbage-collider", "collision payload")]
    [InlineData("trailing-byte-collider", "collision payload")]
    [InlineData("nan-box-collider", "collision payload")]
    [InlineData("empty-hull-collider", "collision payload")]
    [InlineData("empty-mesh-collider", "collision payload")]
    [InlineData("degenerate-orientation-collider", "collision payload")]
    [InlineData("mesh-in-compound", "triangle mesh inside a compound")]
    [InlineData("mesh-in-nested-compound", "triangle mesh inside a compound")]
    [InlineData("deck-with-support", "placement-local support surfaces arrive with R5")]
    public void UnsupportedShapeData_Refuses(string assetId, string message)
    {
        var f = NativeWorldFixtures.Assets();
        var e = Assert.Throws<MapDocumentException>(() => MapAssetShapes.Read(f.Closure, assetId));
        Assert.Contains(message, e.Message);
    }
}
