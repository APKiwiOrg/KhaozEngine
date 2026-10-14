using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapInteriorCreationTests
{
    [Fact]
    public void CreateInterior_DerivesRoomAndPublishesBothRecordsFromAValidMap()
    {
        var f = MapInteriorCreationFixtures.Empty();
        MapBoundDocumentValidation.Validate(f.Document, f.Assets);
        string before = MapDocumentFile.SaveText(f.Document);
        var domain = MapInteriorCreationFixtures.Domain();
        var result = MapInteriorEdits.Prepare(f.Document, f.Assets, new MapSetInterior(domain));
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        var room = Assert.Single(result.After.Surfaces.AllRecords().OfType<MapSpaceDoc>(), r => r.Id == "new-room");
        Assert.Equal(MapSpaceKind.Room, room.Kind); Assert.Equal(domain.Parent, room.Parent); Assert.Null(room.AliasOf);
        Assert.Empty(room.DomainTags); Assert.Empty(room.Walls); Assert.Empty(room.Portals); Assert.Empty(room.Links);
        var published = Assert.Single(result.After.Surfaces.AllRecords().OfType<MapPrismDomain>());
        Assert.Equal(("new-volume", "new-room", "ground-floor", 0f, 3f),
            (published.Id, published.Space.Id, published.StoreyId, published.LowerY, published.UpperY));
        Assert.Equal(domain.Space.Anchor, published.Space.Anchor); Assert.Equal(domain.Parent, published.Parent);
        Assert.Equal(domain.Footprint.Rects, published.Footprint.Rects); Assert.Empty(published.Footprint.Rings);
        Assert.Contains("new-room", result.WriteSet.RecordIds); Assert.Contains("new-volume", result.WriteSet.RecordIds);
        MapBoundDocumentValidation.Validate(result.After, f.Assets);
        var reload = MapDocumentFile.LoadText(MapDocumentFile.SaveText(result.After));
        Assert.Equal(MapPrefabResolver.Expand(result.After, f.Assets).Digest, MapPrefabResolver.Expand(reload, f.Assets).Digest);
    }
    [Fact]
    public void AliasRoom_RefusesBeforeChangingAnyRecord()
    {
        var source = PrefabFixtures.Create();
        var f = new PrefabFixture(MapPrefabEdits.Prepare(source.Document, source.Assets, new MapUnpackPrefab("a")).After, source.Assets);
        var anchor = new MapPatchKey("a/floor", 0, 0);
        f.Document.Surfaces.Patches[anchor].Records.Add(new MapSpaceDoc("alias-room", MapSpaceKind.Room,
            new("exterior", new("exterior", 0, 0)), new("a/room", anchor), [], [], [], []));
        string before = MapDocumentFile.SaveText(f.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapInteriorEdits.Prepare(f.Document, f.Assets,
            new MapSetInterior(MapInteriorCreationFixtures.Domain() with { Space = new("alias-room", anchor) })));
        Assert.Contains("alias room", error.Message);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
    }
    [Fact]
    public void NonRoomSpace_RefusesBeforeChangingAnyRecord()
    {
        var f = MapInteriorCreationFixtures.Empty();
        string before = MapDocumentFile.SaveText(f.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapInteriorEdits.Prepare(f.Document, f.Assets,
            new MapSetInterior(MapInteriorCreationFixtures.Domain() with { Space = new("exterior", new("exterior", 0, 0)) })));
        Assert.Contains("Room", error.Message);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
    }
    [Fact]
    public void ReleasedReplacement_RetainsRoomFieldsAndThePrismRecordId()
    {
        var f = MapInteriorCreationFixtures.Empty(); var domain = MapInteriorCreationFixtures.Domain();
        var created = MapInteriorEdits.Prepare(f.Document, f.Assets, new MapSetInterior(domain)).After;
        var room = created.Surfaces.AllRecords().OfType<MapSpaceDoc>().Single(r => r.Id == "new-room");
        created.Surfaces.Patches[domain.Space.Anchor].Records.Remove(room);
        created.Surfaces.Patches[domain.Space.Anchor].Records.Add(room with { DomainTags = new[] { "kept" } });
        var replaced = MapInteriorEdits.Prepare(created, f.Assets, new MapSetInterior(domain with { UpperY = 2.5f })).After;
        Assert.Equal(new[] { "kept" }, replaced.Surfaces.AllRecords().OfType<MapSpaceDoc>().Single(r => r.Id == "new-room").DomainTags);
        Assert.Equal("new-volume", Assert.Single(replaced.Surfaces.AllRecords().OfType<MapPrismDomain>()).Id);
        Assert.Equal(2.5f, Assert.Single(replaced.Surfaces.AllRecords().OfType<MapPrismDomain>()).UpperY);
        Assert.Throws<MapDocumentException>(() => MapInteriorEdits.Prepare(replaced, f.Assets, new MapSetInterior(domain with { Id = "other-volume" })));
    }
}
