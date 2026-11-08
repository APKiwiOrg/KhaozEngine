using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapSpaceGeometryRefusalTests
{
    [Theory]
    [InlineData(true, MapDocumentRefusal.SpaceAliasCycle)]
    [InlineData(false, MapDocumentRefusal.SpaceParentCycle)]
    public void SpaceTraversal_NamesTheCycleAndAcceptsTheRepairedEdge(bool alias, MapDocumentRefusal reason)
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(0);
        var patch = set.Patches.Values.Single();
        int index = patch.Records.FindIndex(r => r is MapSpaceDoc);
        var valid = (MapSpaceDoc)patch.Records[index];
        var self = new MapRecordRef(valid.Id, patch.Key);
        patch.Records[index] = alias ? valid with { AliasOf = self } : valid with { Parent = self };
        var view = MapScopedSurfaces.CompleteView(set);
        var space = MapSpaceGeometry.Record<MapSpaceDoc>(view, self);
        var error = Assert.Throws<MapDocumentException>(() => Traverse(view, space));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(valid.Id, error.SpaceId);
        patch.Records[index] = valid;
        var repaired = MapScopedSurfaces.CompleteView(set);
        Assert.Equal(valid.Id, Traverse(repaired, MapSpaceGeometry.Record<MapSpaceDoc>(repaired, self)));

        string Traverse(MapScopedSurfaces source, MapSpaceDoc start) => alias
            ? MapSpaceGeometry.Owner(source, start) : MapSpaceGeometry.Ancestors(source, start).Last().Id;
    }

    [Fact]
    public void MissingSurface_NamesTheRequestedIdentity()
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(0);
        var surface = set.Refs.Single();
        set.Patches.Clear();
        set.Refs.Clear();
        var error = Assert.Throws<MapDocumentException>(() => MapSpaceGeometry.Surface(MapScopedSurfaces.CompleteView(set), surface.Id));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal(surface.Id, error.SurfaceId);
        set.Refs.Add(surface);
        Assert.Equal(surface, MapSpaceGeometry.Surface(MapScopedSurfaces.CompleteView(set), surface.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceCellPlanning_NamesTheUnavailablePatch(bool segment)
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(0);
        var patch = set.Patches.Values.Single();
        var missing = patch.Key with { SlotX = 1 };
        var view = MapScopedSurfaces.CompleteView(set);
        var planner = new MapSpacePointBounds(view, new(), null);
        var chain = new MapBoundaryChain("source", MapChainKind.SurfaceEdge, missing,
            [new(new(patch.Key.SurfaceId, MapLatticeAddress.Corner(64, 0)), null),
                new(new(patch.Key.SurfaceId, MapLatticeAddress.Corner(65, 0)), null)]);
        var point = new MapExactXz(new(64, 1), default);
        var error = Assert.Throws<MapDocumentException>(() => Read(planner));
        Assert.Equal(MapDocumentRefusal.PatchUnavailable, error.Reason);
        Assert.Equal(missing, error.Patch);
        Assert.Equal(MapPatchStatus.Unloaded, error.PatchStatus);
        patch = patch.Clone();
        patch.Key = missing;
        patch.Records.Clear();
        set.Patches.Add(missing, patch);
        Assert.Equal(missing, Read(new(MapScopedSurfaces.CompleteView(set), new(), null)).Patch);

        MapCellDemand Read(MapSpacePointBounds source) => segment
            ? source.SourceCells(chain).First() : source.SourceCell(missing, point);
    }

    [Fact]
    public void PointBound_MissingSurfaceNamesTheFootprintField()
    {
        var (view, footprint) = BoundFaceContextFixtures.EightByEight();
        var planner = new MapSpacePointBounds(view, new(), null);
        var invalid = footprint with { Upper = footprint.Upper with { SurfaceId = null } };
        var point = view.Surfaces.Single(s => s.Id == footprint.Upper.SurfaceId).Frame.WorldXz(MapLatticeAddress.Create(1, 1, 2));
        var error = Assert.Throws<MapDocumentException>(() => planner.Plan(invalid, invalid.Upper, point));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal(footprint.Id, error.RecordId);
        Assert.Equal("Upper.SurfaceId", error.FieldPath);
        Assert.Equal(MapMembershipStatus.Resolved, planner.Plan(footprint, footprint.Upper, point).Status);
    }

    [Fact]
    public void PointBound_MissingOpeningNamesTheFootprintField()
    {
        var (surface, patch, opening) = BoundaryFixtures.TwoCellOpening();
        var set = new MapSurfaceSet();
        set.Refs.Add(surface);
        set.Patches.Add(patch.Key, patch);
        var footprint = new MapSpaceFootprint("footprint", new("space", patch.Key), patch.Key, [0],
            new(MapBoundKind.HorizontalOpening, null, null), new(MapBoundKind.OpenTop, null, null));
        var planner = new MapSpacePointBounds(MapScopedSurfaces.CompleteView(set), new(), null);
        var point = surface.Frame.WorldXz(MapLatticeAddress.Create(1, 1, 2));
        var error = Assert.Throws<MapDocumentException>(() => planner.Plan(footprint, footprint.Lower, point));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal(footprint.Id, error.RecordId);
        Assert.Equal("Lower.Opening", error.FieldPath);
        var repaired = footprint with { Lower = footprint.Lower with { Opening = new(opening.Id, patch.Key) } };
        Assert.Equal(MapMembershipStatus.Resolved, planner.Plan(repaired, repaired.Lower, point).Status);
    }
}
