using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapOccupiedPartitionRefusalTests
{
    [Theory]
    [InlineData("cycle", MapDocumentRefusal.SpacePrismParentCycle)]
    [InlineData("kind", MapDocumentRefusal.ReferenceKindMismatch)]
    [InlineData("parent", MapDocumentRefusal.SpacePrismParentMismatch)]
    public void PrismAncestry_ReportsTheFailedRuleOnPointAndWidePaths(string defect, MapDocumentRefusal reason)
    {
        var set = Scene(2);
        MapSurfacePatch patch = set.Patches.Values.Single();
        int room = patch.Records.FindIndex(r => r.Id == "room-0");
        int prismIndex = patch.Records.FindIndex(r => r is MapPrismDomain p && p.Space.Id == "room-0");
        var originalRoom = (MapSpaceDoc)patch.Records[room];
        var originalPrism = (MapPrismDomain)patch.Records[prismIndex];
        if (defect == "cycle")
        {
            var parent = new MapRecordRef("room-1", patch.Key);
            patch.Records[room] = originalRoom with { Parent = parent };
            patch.Records[prismIndex] = originalPrism with { Parent = parent };
        }
        else if (defect == "kind") patch.Records[room] = originalRoom with { Kind = MapSpaceKind.Exterior };
        else patch.Records[prismIndex] = originalPrism with { Parent = new("room-1", patch.Key) };
        MapScopedSurfaces view = View(set);
        MapPrismDomain[] prisms = MapSpaceGeometry.Records(view).OfType<MapPrismDomain>().ToArray();
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapOccupiedPartitions.At(view, Outside, [], prisms));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(defect == "cycle" ? "prism-01" : "prism-02", error.RecordId);
        Assert.Equal(defect == "cycle" ? "room-1" : "room-0", error.SpaceId);
        Assert.Equal(defect == "cycle" ? "room-0" : defect == "kind" ? "exterior" : "room-1", error.RelatedRecordId);
        MapOccupiedPartitionResult wide = Build(view);
        Assert.Empty(wide.Pieces);
        Assert.Equal(reason, wide.Finding!.Reason);
        Assert.Equal(error.RecordId, wide.Finding.RecordId);
        Assert.Same(wide.Finding, wide.Narrow().Finding);

        patch.Records[room] = originalRoom;
        patch.Records[prismIndex] = originalPrism;
        MapScopedSurfaces repaired = View(set);
        Assert.Empty(MapOccupiedPartitions.At(repaired, Outside, [], MapSpaceGeometry.Records(repaired).OfType<MapPrismDomain>().ToArray()));
        Assert.Equal(MapRefinementStatus.Complete, Build(repaired).Status);
    }

    [Fact]
    public void PrismDepth_ReportsTheReachedDepthAndAcceptsTheLastAllowedAncestor()
    {
        int limit = View(Scene(0)).Scope.Limits.MaxRecordDepth;
        var view = View(Scene(limit + 2));
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapOccupiedPartitions.At(view, Outside, [],
            MapSpaceGeometry.Records(view).OfType<MapPrismDomain>().ToArray()));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("MaxRecordDepth", error.Budget);
        Assert.Equal(limit + 1, error.ActualCount);
        Assert.Equal(limit, error.Limit);
        Assert.NotNull(error.RecordId);
        MapOccupiedPartitionResult wide = Build(view);
        Assert.Empty(wide.Pieces);
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, wide.Finding!.Reason);
        Assert.Equal("MaxRecordDepth", wide.Finding.Budget);
        Assert.Equal(limit + 1, wide.Finding.ActualCount);

        var repaired = View(Scene(limit + 1));
        Assert.Empty(MapOccupiedPartitions.At(repaired, Outside, [], MapSpaceGeometry.Records(repaired).OfType<MapPrismDomain>().ToArray()));
        Assert.Equal(MapRefinementStatus.Complete, Build(repaired).Status);
    }

    [Fact]
    public void MissingParentFootprint_NamesThePrismAndItsAuthoredParent()
    {
        var view = View(Scene(1));
        MapPrismDomain prism = MapSpaceGeometry.Records(view).OfType<MapPrismDomain>().Single();
        var inside = new MapExactXz(new(0, 1), new(0, 1));
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapOccupiedPartitions.At(view, inside, [], [prism]));
        Assert.Equal(MapDocumentRefusal.SpacePrismParentFootprintMissing, error.Reason);
        Assert.Equal(prism.Id, error.RecordId);
        Assert.Equal("room-0", error.SpaceId);
        Assert.Equal("exterior", error.RelatedRecordId);
        var parent = new MapSpaceBoundsPart("exterior", "exterior", new(0, 1), null, "floor", "open-top", false);
        Assert.NotEmpty(MapOccupiedPartitions.At(view, inside, [parent], [prism]));
    }

    [Fact]
    public void MissingOccupiedSpace_NamesTheRequestedChild()
    {
        var view = View(Scene(1));
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapOccupiedPartitions.Ancestor(view, "exterior", "missing"));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal("missing", error.SpaceId);
        Assert.True(MapOccupiedPartitions.Ancestor(view, "exterior", "room-0"));
    }

    [Fact]
    public void MissingCanonicalBound_RetainsTheOwningFootprintAndSpace()
    {
        var set = Scene(0);
        MapSurfacePatch patch = set.Patches.Values.Single();
        patch.SetPresent(0, 0, false);
        var result = Build(View(set));
        Assert.Empty(result.Pieces);
        Assert.Equal(MapDocumentRefusal.PatchUnavailable, result.Finding!.Reason);
        Assert.Equal("footprint", result.Finding.RecordId);
        Assert.Equal("exterior", result.Finding.SpaceId);
        Assert.Equal(patch.Key, result.Finding.Patch);
        Assert.Equal("Lower", result.Finding.FieldPath);
        patch.SetPresent(0, 0, true);
        Assert.Equal(MapRefinementStatus.Complete, Build(View(set)).Status);
    }

    [Theory]
    [InlineData("faces", 4)]
    [InlineData("vertices", 12)]
    public void OccupiedPieceBudget_ReportsTheBranchAndAcceptsItsBoundary(string budget, int accepted)
    {
        var view = View(Scene(1));
        var limits = budget == "faces" ? new MapRefinementLimits(MaxRefinementFaces: accepted - 1) : new(MaxRefinementVertices: accepted - 1);
        var result = Build(view, limits);
        Assert.Empty(result.Pieces);
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, result.Finding!.Reason);
        Assert.Equal(budget == "faces" ? "MaxRefinementFaces" : "MaxRefinementVertices", result.Finding.Budget);
        Assert.Equal(accepted, result.Finding.ActualCount);
        Assert.Equal(accepted - 1, result.Finding.Limit);
        var error = Assert.Throws<MapDocumentException>(() => MapOccupiedPartitions.Build(view,
            new(new(0, 1), new(0, 1)), new(new(1, 1), new(1, 1)), limits, false));
        Assert.Equal(result.Finding.Reason, error.Reason);
        Assert.Equal(result.Finding.Budget, error.Budget);
        Assert.Equal(result.Finding.Limit, error.Limit);
        Assert.Equal(result.Finding.ActualCount, error.ActualCount);
        Assert.Equal(MapRefinementStatus.Complete, Build(view, budget == "faces" ? limits with { MaxRefinementFaces = accepted }
            : limits with { MaxRefinementVertices = accepted }).Status);
    }

    internal static MapSurfaceSet Scene(int rooms)
    {
        var set = new MapSurfaceSet();
        var surface = MixedResolutionFixtures.Surface("floor", 1, MapSurfaceRole.SupportFloor);
        var patch = MixedResolutionFixtures.Patch(new("floor", 0, 0), 1, 1, (_, _) => 0);
        set.Refs.Add(surface);
        set.Patches.Add(patch.Key, patch);
        patch.Records.Add(new MapSpaceDoc("exterior", MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new MapSpaceFootprint("footprint", new("exterior", patch.Key), patch.Key, [0],
            new(MapBoundKind.SupportFloor, surface.Id, null), new(MapBoundKind.OpenTop, null, null)));
        for (int i = 0; i < rooms; i++)
        {
            var parent = new MapRecordRef(i == 0 ? "exterior" : "room-" + (i - 1), patch.Key);
            var space = new MapRecordRef("room-" + i, patch.Key);
            patch.Records.Add(new MapSpaceDoc(space.Id, MapSpaceKind.Room, parent, null, [], [], [], []));
            // Deepest first in canonical prism order so recursive parent admission reaches the depth boundary.
            patch.Records.Add(new MapPrismDomain("prism-" + (rooms - i).ToString("D2"), 1, space, parent, "", "storey",
                new(surface.Frame, [new(0, 0, 1, 1)], []), 0, 1));
        }
        return set;
    }

    internal static MapScopedSurfaces View(MapSurfaceSet set) => MapScopedSurfaces.CompleteEffective(new(set,
        new Dictionary<string, MapEffectiveTransform>(), new string('a', 64), [new(1, "storey", "Storey", null)]));
    static MapExactXz Outside => new(new(100, 1), new(100, 1));
    static MapOccupiedPartitionResult Build(MapScopedSurfaces view, MapRefinementLimits? limits = null) =>
        MapOccupiedPartitions.BuildWide(view, new(new(0, 1), new(0, 1)), new(new(1, 1), new(1, 1)), limits ?? new(), false);
}
