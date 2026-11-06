using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapInteriorRefusalTests
{
    [Fact]
    public void AliasRoom_RefusesInteriorReplacementWithoutChangingItsSource()
    {
        var fixture = PrefabFixtures.Create();
        var document = MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, new MapUnpackPrefab("a")).After;
        var prism = document.Surfaces.AllRecords().OfType<MapPrismDomain>().Single();
        var patch = document.Surfaces.Patches[prism.Space.Anchor];
        var room = patch.Records.OfType<MapSpaceDoc>().Single(s => s.Id == prism.Space.Id);
        var alias = new MapRecordRef("alias-room", patch.Key);
        patch.Records.Add(room with { Id = alias.Id, AliasOf = prism.Space });
        var before = MapDocumentFile.SaveText(document);
        var error = Assert.Throws<MapDocumentException>(() => MapInteriorEdits.Prepare(document, fixture.Assets,
            new MapSetInterior(prism with { Space = alias })));
        Assert.Equal(MapDocumentRefusal.InteriorAliasUnsupported, error.Reason);
        Assert.Equal(alias.Id, error.SpaceId);
        Assert.Equal(prism.Id, error.RecordId);
        Assert.Equal(before, MapDocumentFile.SaveText(document));
        Assert.Contains(prism.Id, MapInteriorEdits.Prepare(document, fixture.Assets,
            new MapSetInterior(prism)).WriteSet.RecordIds);
    }
}
