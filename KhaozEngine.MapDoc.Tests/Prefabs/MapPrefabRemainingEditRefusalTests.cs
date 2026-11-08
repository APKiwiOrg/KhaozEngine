using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabRemainingEditRefusalTests
{
    sealed record UnsupportedPrefabEdit() : MapPrefabEdit("unsupported");
    sealed record UnsupportedMarkerEdit() : MapMarkerEdit("unsupported");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrefabEntryPoint_DistinguishesCapturedAssetUpdatesFromUnsupportedEdits(bool update)
    {
        var fixture = PrefabFixtures.Create();
        string hash = fixture.Assets.GetResource("hut").Reference.Sha256;
        var assetUpdate = new MapUpdatePrefabAsset("hut", hash, "hut", hash);
        MapPrefabEdit edit = update ? assetUpdate : new UnsupportedPrefabEdit();
        string before = MapDocumentFile.SaveText(fixture.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, edit));
        Assert.Equal(update ? MapDocumentRefusal.PrefabCapturedUpdateRequired : MapDocumentRefusal.OperationKindUnsupported, error.Reason);
        Assert.Equal(edit.GetType(), error.RecordType);
        Assert.Equal(update ? "PrepareAssetUpdate" : "Prepare", error.Operation);
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));
        if (update)
            Assert.NotNull(MapPrefabEdits.PrepareAssetUpdate(fixture.Document, fixture.Assets, fixture.Assets, assetUpdate).After.Prefabs);
        else
        {
            var instance = fixture.Document.Prefabs!.Instances.Single();
            Assert.NotNull(MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, new MapMovePrefab(instance.Id, instance.Transform)).After.Prefabs);
        }
    }

    [Fact]
    public void MarkerEntryPoint_IdentifiesUnsupportedEditsBeforeWriting()
    {
        var fixture = MarkerFixtures.Create();
        string before = MapDocumentFile.SaveText(fixture.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapMarkerEdits.Prepare(fixture.Document, fixture.Assets, new UnsupportedMarkerEdit()));
        Assert.Equal(MapDocumentRefusal.OperationKindUnsupported, error.Reason);
        Assert.Equal(typeof(UnsupportedMarkerEdit), error.RecordType);
        Assert.Equal("Prepare", error.Operation);
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));
        var marker = fixture.Document.BuildingIntent!.Markers.Single();
        Assert.Contains(MapMarkerEdits.Prepare(fixture.Document, fixture.Assets, new MapSetMarker(marker)).After.BuildingIntent!.Markers,
            value => value.Id == marker.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void RoomSelection_RequiresExactlyOnePrism(int count)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabExtraction.RequireUniqueRoomPrismForTests("room", count));
        Assert.Equal(MapDocumentRefusal.SpaceRoomOccupancyInvalid, error.Reason);
        Assert.Equal("room", error.SpaceId);
        Assert.Equal((long)count, error.ActualCount);
        Assert.Equal(1L, error.ExpectedCount);
        MapPrefabExtraction.RequireUniqueRoomPrismForTests("room", 1);
    }
}
