using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapInteriorRefusalTests
{
    [Theory]
    [InlineData("kind", MapDocumentRefusal.ReferenceKindMismatch)]
    [InlineData("id", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("anchor", MapDocumentRefusal.AuthoredOwnershipMismatch)]
    public void InteriorReplacement_KeepsItsSpecificReferenceAndIdentityRules(string defect, MapDocumentRefusal reason)
    {
        var fixture = PrefabFixtures.Create();
        var document = MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, new MapUnpackPrefab("a")).After;
        var prism = document.Surfaces.AllRecords().OfType<MapPrismDomain>().Single();
        var invalid = defect switch
        {
            "kind" => prism with { Space = prism.Parent },
            "id" => prism with { Id = "replacement" },
            _ => prism with
            {
                Space = prism.Space with
                { Anchor = document.Surfaces.Patches.Keys.First(k => k != prism.Space.Anchor) }
            }
        };
        var before = MapDocumentFile.SaveText(document);
        var error = Assert.Throws<MapDocumentException>(() => MapInteriorEdits.Prepare(document, fixture.Assets,
            new MapSetInterior(invalid)));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(invalid.Id, error.RecordId);
        Assert.Equal(invalid.Space.Id, error.SpaceId);
        Assert.Equal(before, MapDocumentFile.SaveText(document));
        Assert.Contains(prism.Id, MapInteriorEdits.Prepare(document, fixture.Assets,
            new MapSetInterior(prism)).WriteSet.RecordIds);
    }

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
