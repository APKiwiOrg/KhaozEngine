using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapInteriorAdmissionTests
{
    [Theory]
    [InlineData(false, MapDocumentRefusal.AuthoredOwnershipMismatch)]
    [InlineData(true, MapDocumentRefusal.ReferenceMissing)]
    public void StandaloneInterior_RequiresStandaloneOwnershipAndAnAuthoredAnchor(bool missingAnchor, MapDocumentRefusal reason)
    {
        var f = MapInteriorCreationFixtures.Empty();
        var control = MapInteriorCreationFixtures.Domain();
        var refused = missingAnchor
            ? control with { Space = new(control.Space.Id, new("missing", 0, 0)) }
            : control with { InstanceId = "prefab-owner" };
        string before = MapDocumentFile.SaveText(f.Document);
        var refusal = Assert.Throws<MapDocumentException>(() => MapInteriorEdits.Prepare(f.Document, f.Assets, new MapSetInterior(refused)));
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal((refused.Id, refused.Id, refused.Space.Id, refused.Space.Anchor),
            (refusal.EntityId, refusal.RecordId, refusal.SpaceId, refusal.Patch));
        Assert.Equal(MapPrefabChildKind.Record, refusal.ChildKind);
        if (!missingAnchor) Assert.Equal(refused.InstanceId, refusal.InstanceId);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        var accepted = MapInteriorEdits.Prepare(f.Document, f.Assets, new MapSetInterior(control));
        var published = Assert.Single(accepted.After.Surfaces.AllRecords().OfType<MapPrismDomain>());
        Assert.Equal(MapPrefabJson.Digest("test/interior", control), MapPrefabJson.Digest("test/interior", published));
    }
}
