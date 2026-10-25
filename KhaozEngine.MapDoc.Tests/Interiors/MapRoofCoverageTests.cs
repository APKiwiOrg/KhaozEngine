using System;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapRoofCoverageTests
{
    [Fact]
    public void SmallFootprint_PreparationDoesNotGrowWithAcquiredSurface()
    {
        var small = Prepare(16);
        var large = Prepare(64);
        Assert.Equal(small.Demand.Cells, large.Demand.Cells);
        Assert.Equal(small.Demand.Faces, large.Demand.Faces);
        Assert.Equal(small.Buffer.RetainedBytes, large.Buffer.RetainedBytes);
        // Hand inventory: 16 cell graphs, 32 triangles, four footprint polygons, one 31-node index,
        // 16 lower rows, one space and ancestry row, the 12,640-byte exact workspace and 533-byte point scratch.
        Assert.Equal(103157, small.Buffer.RetainedBytes);
        Assert.Equal(832, small.Buffer.Counts.NormalizedCoefficientBytes);
        Assert.InRange(small.Peak, 103157, 2 * 103157 + 98304);
        // Masked compilation retains the released patch-sized subdivision workspace.
        Assert.InRange(large.Peak, small.Peak, small.Peak + 61440);
        Assert.Equal(16, large.Buffer.Counts.UniqueTargetSlots);
        Assert.Equal(16, large.Buffer.Counts.BoundAssociations);
        Assert.Equal(32, large.Buffer.Counts.PhysicalFaces);
        Assert.Equal(61440, large.Buffer.Counts.PeakPatchBytes - small.Buffer.Counts.PeakPatchBytes);
        Assert.Equal(small.Buffer.Counts.NormalizedCoefficientBytes, large.Buffer.Counts.NormalizedCoefficientBytes);
        large.Buffer.Evaluate(new(WorldFrame.Origin, new(1.5f, 1, -1.5f)), MapRoofMode.Auto);
        Assert.Equal(MapMembershipStatus.Resolved, large.Buffer.Status);
        Assert.Equal("exterior", large.Buffer.SpaceId);
        large.Buffer.Evaluate(new(WorldFrame.Origin, new(12, 1, -12)), MapRoofMode.Auto);
        Assert.Equal(MapMembershipStatus.Outside, large.Buffer.Status);
    }

    [Fact]
    public void CompletePolicy_PreparesOnlyFootprintsInsideAggregatePrismRectangle()
    {
        var view = PrefabFixtures.Effective(PrefabFixtures.Create()).CompleteView();
        Assert.Empty(MapPrismCoverage.Validate(view, new(MaxContextFaces: 128)));
    }

    static (MapMembershipPreparation Demand, MapRoofEvaluationBuffer Buffer, long Peak) Prepare(int size)
    {
        var fixture = PrefabFixtures.Create();
        fixture.Document.Prefabs = null;
        var patch = PrefabFixtures.Patch("exterior", size, size, 0);
        patch.Records.Add(new MapSpaceDoc("exterior", MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new MapSpaceFootprint("coverage", new("exterior", patch.Key), patch.Key,
            [65, 66, 129, 130], new(MapBoundKind.SupportFloor, "exterior", null), new(MapBoundKind.OpenTop, null, null)));
        fixture.Document.Surfaces.Patches[patch.Key] = patch;
        var surface = fixture.Document.Surfaces.Refs.Single();
        fixture.Document.Surfaces.Refs[0] = surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                new[] { new System.Collections.Generic.KeyValuePair<MapPatchKey, string>(patch.Key, MapSurfaceSemantics.PatchDigest(patch)) })
        };
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(-64, -64), new(64, 64), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(-64, -64, -64, 64, 64, 64), MapSceneChannels.All);
        var scene = NativeSceneFixtures.Open(native, false).Acquire(scope).Scene!;
        Assert.NotNull(scene);
        Assert.Equal(MapSceneStatus.CompleteForScope, MapMembershipPreparation.Measure(scene, out var demand, out _));
        var admission = new MapSceneAdmission(new());
        var result = MapRoofEvaluationBuffer.Prepare(scene, admission);
        Assert.True(result.Buffer is not null, result.Detail);
        return (demand, result.Buffer!, admission.PeakBytes);
    }
}
