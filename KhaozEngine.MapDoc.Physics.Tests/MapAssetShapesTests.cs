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
    [InlineData("garbage-collider", MapDocumentRefusal.AssetPayloadUnreadable)]
    [InlineData("trailing-byte-collider", MapDocumentRefusal.AssetCollisionTrailingBytes)]
    [InlineData("nan-box-collider", MapDocumentRefusal.AssetPayloadUnreadable)]
    [InlineData("empty-hull-collider", MapDocumentRefusal.AssetCollisionEmptyPoints)]
    [InlineData("empty-mesh-collider", MapDocumentRefusal.AssetCollisionEmptyPoints)]
    [InlineData("degenerate-orientation-collider", MapDocumentRefusal.AssetPayloadUnreadable)]
    [InlineData("mesh-in-compound", MapDocumentRefusal.AssetCollisionNestedMesh)]
    [InlineData("mesh-in-nested-compound", MapDocumentRefusal.AssetCollisionNestedMesh)]
    [InlineData("deck-with-support", MapDocumentRefusal.AssetTypedPayloadOpaque)]
    public void UnsupportedShapeData_Refuses(string assetId, MapDocumentRefusal reason)
    {
        var f = NativeWorldFixtures.Assets();
        var e = Assert.Throws<MapDocumentException>(() => MapAssetShapes.Read(f.Closure, assetId));
        Assert.Equal(reason, e.Reason);
        string resourceId = assetId + (assetId == "deck-with-support" ? ".surface" : ".collider");
        Assert.Equal(resourceId, e.ResourceId);
        if (reason != MapDocumentRefusal.AssetTypedPayloadOpaque) Assert.Equal(assetId, e.AssetId);
        if (reason == MapDocumentRefusal.AssetCollisionTrailingBytes) Assert.Equal(1L, e.ActualByteCount);
        if (reason == MapDocumentRefusal.AssetCollisionEmptyPoints)
            Assert.Equal(assetId == "empty-hull-collider" ? typeof(ConvexHullShape) : typeof(TriangleMeshShape), e.ShapeType);
        if (reason == MapDocumentRefusal.AssetPayloadUnreadable)
            Assert.Equal(f.Closure.GetResource(resourceId).Reference.Sha256, e.ExpectedHash);
        if (reason == MapDocumentRefusal.AssetTypedPayloadOpaque)
            Assert.Equal("kemap/support-resource", e.ExpectedSchema);
        Assert.True(MapAssetShapes.Read(NativeWorldFixtures.RepairedShapeAsset(assetId), assetId).IsSolid);
    }
}
