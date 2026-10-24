using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofContributionAdmissionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(.001f)]
    public void SequentialFullCohortContributions_FitTheirLivePeak(float weld)
    {
        var closure = NativeRenderAssetFixtures.Placements();
        using var fixture = NativeSceneFixtures.SeparatedPlacements(closure, 1,
            Enumerable.Range(0, 12).Select(i => new MapEffectiveTransform(8 + i * .1, 0, 8, 0, 1)).ToArray());
        fixture.Document.BuildingIntent = new(1, [], [], [], [])
        {
            Roofs = fixture.Document.Placements.Select(p =>
                new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, p.Id)).ToArray()
        };
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(NativePresentationFixtures.Scope).Scene!;
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = new MapPlacementPolicy([new("caps", new HashSet<string> { "prop-a" }, [10, 20], 4, 100, 30, 10, weld, true)], 100);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        // 72 detached parts at <=1024 bytes, two simultaneous <=73-KiB merge envelopes,
        // <=32 KiB canonical policy and <=48 KiB remaining tables, sources and copy overlap.
        // The independent 320-KiB bound fits live work but not twelve accumulated merge envelopes.
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 327680));
        using var active = admission.Enter();
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var build = Assert.Single(batch.Clusters);
        Assert.Equal(12, build.RepresentedIds.Count);
        Assert.Equal(24, build.BorrowMesh.TriangleCount);
        Assert.All(Enumerable.Range(0, 12), i => Assert.Equal(2, build.BorrowContribution(i).TriangleCount));
        Assert.Equal(0, admission.WorkspaceBytes);
        batch.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
