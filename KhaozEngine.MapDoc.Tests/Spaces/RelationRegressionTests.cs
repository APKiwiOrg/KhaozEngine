using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class RelationRegressionTests
{
    [Fact]
    public void MissingVerticalLinkAnchor_CannotPublishAnOpeningPathWithAcquiredGeometry()
    {
        var (document, link) = CaveWithSeparateLinkAnchor();
        var source = new FilteredSurfaceSource(MapDocumentSurfaceSource.Capture(document), link.Anchor);
        MapRelationResult result = RelationFixtures.Relate(document, new(2.5f, 5.5f, 6.5f), new(2.5f, -19f, 6.5f),
            out MapScopedSurfaces acquired, source: source);

        Assert.Contains(new MapRecordRef("shaft-top", new("cave-floor", 0, 0)), acquired.Witness.Records);
        Assert.Contains(new MapRecordRef("shaft-bottom", new("deep-ceiling", 0, 0)), acquired.Witness.Records);
        Assert.Contains(new MapRecordRef("chamber-shaft-cells", new("cave-floor", 0, 0)), acquired.Witness.Records);
        Assert.Contains(new MapRecordRef("shaft-cells", new("cave-floor", 0, 0)), acquired.Witness.Records);
        Assert.Contains(new MapRecordRef("deep-shaft-cells", new("deep-floor", 0, 0)), acquired.Witness.Records);
        Assert.Equal(MapPatchStatus.Present, acquired.Patch(new("cave-floor", 0, 0)).Status);
        Assert.Equal(MapPatchStatus.Present, acquired.Patch(new("cave-ceiling", 0, 0)).Status);
        Assert.Equal(MapPatchStatus.Present, acquired.Patch(new("deep-floor", 0, 0)).Status);
        Assert.Equal(MapPatchStatus.Present, acquired.Patch(new("deep-ceiling", 0, 0)).Status);
        Assert.False(acquired.TryRecord(link, out _, out MapPatchStatus status));
        Assert.Equal(MapPatchStatus.Unloaded, status);
        Assert.DoesNotContain(link, acquired.Witness.Records);
        Assert.Equal((MapMembershipStatus.Resolved, "chamber"), (result.MembershipA.Status, result.MembershipA.SpaceId));
        Assert.Equal((MapMembershipStatus.Resolved, "deep"), (result.MembershipB.Status, result.MembershipB.SpaceId));

        Assert.Equal((MapRelationStatus.MissingGeometry, MapGeometricRelation.Undetermined), (result.Status, result.Relation));
        Assert.Empty(result.Path);
        Assert.Contains("shaft-top", result.Detail);
        Assert.Same(acquired.ReadWitness, result.Witness);
    }

    [Fact]
    public void AcquiredVerticalLinkAnchor_IsIncludedInEveryOpeningFactProvenance()
    {
        var (document, link) = CaveWithSeparateLinkAnchor();
        MapRelationResult result = RelationFixtures.Relate(document, new(2.5f, 5.5f, 6.5f), new(2.5f, -19f, 6.5f),
            out MapScopedSurfaces acquired);

        Assert.Equal((MapRelationStatus.Resolved, MapGeometricRelation.ConnectedThroughVerticalLink), (result.Status, result.Relation));
        Assert.Equal(new[] { "shaft-top", "shaft-bottom" }, result.Path.Select(f => f.Record.Id));
        Assert.Contains(link, acquired.Witness.Records);
        Assert.All(result.Path, fact =>
        {
            Assert.Equal(link, fact.ViaLink);
            Assert.Contains(link, fact.Geometry.Provenance);
            Assert.All(fact.Geometry.Provenance, record => Assert.Contains(record, acquired.Witness.Records));
        });
    }

    [Fact]
    public void WallPortal_DoesNotRequireAnUnavailableVerticalLink()
    {
        var (document, link) = CaveWithSeparateLinkAnchor();
        var source = new FilteredSurfaceSource(MapDocumentSurfaceSource.Capture(document), link.Anchor);
        MapRelationResult result = RelationFixtures.Relate(document, new(2.5f, 10.5f, 4.5f), new(2.5f, 5.5f, 4.5f),
            source: source);

        Assert.Equal((MapRelationStatus.Resolved, MapGeometricRelation.ConnectedThroughPortals), (result.Status, result.Relation));
        MapPortalFact portal = Assert.Single(result.Path);
        Assert.Equal(MapPortalKind.WallPortal, portal.Kind);
        Assert.Null(portal.ViaLink);
        Assert.DoesNotContain(link, portal.Geometry.Provenance);
    }

    static (MapDocument Document, MapRecordRef Link) CaveWithSeparateLinkAnchor()
    {
        MapDocument document = CaveFixtures.RampChamberShaft();
        MapSurfacePatch floor = document.Surfaces.Patches[new("cave-floor", 0, 0)];
        MapVerticalLink link = floor.Records.OfType<MapVerticalLink>().Single();
        floor.Records.Remove(link);
        var reference = new MapRecordRef(link.Id, new("cave-floor", 1, 0));
        var anchor = new MapSurfacePatch
        {
            Key = reference.Anchor,
            Width = 1,
            Depth = 1,
            Heights = new int[4],
            Cells = new MapSurfaceCell[1],
            Presence = new ulong[1],
        };
        anchor.Records.Add(link);
        document.Surfaces.Patches.Add(anchor.Key, anchor);
        foreach (MapSurfacePatch patch in document.Surfaces.Patches.Values)
            for (int i = 0; i < patch.Records.Count; i++)
                if (patch.Records[i] is MapSpaceDoc space)
                    patch.Records[i] = space with
                    {
                        Links = space.Links.Select(r => r.Id == link.Id ? reference : r).ToArray(),
                    };
        Assert.Empty(anchor.ValidateLocal());
        Assert.Empty(MapTopologyReferenceValidator.Validate(document.Surfaces.Refs, document.Surfaces.Patches.Values.ToArray()));
        return (document, reference);
    }
}
