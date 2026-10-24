using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofCellAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CellInventory_DisposalReleasesRoutingAndCanonicalProducts(bool roof)
    {
        var closure = NativeRenderAssetFixtures.Placements();
        using var fixture = NativeSceneFixtures.SeparatedPlacements(closure, 1, new(8, 0, 8, 0, 1), new(16, 0, 8, 0, 1));
        if (roof)
            fixture.Document.BuildingIntent = new(1, [], [], [], [])
            {
                Roofs = fixture.Document.Placements.Select(p =>
                    new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, p.Id)).ToArray()
            };
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(NativePresentationFixtures.Scope).Scene!;
        var policy = new MapPlacementPolicy([new("selected", new HashSet<string> { "prop-a" }, [10, 20], 4, 100, 30, 10, 0, true)], 100);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        // Two small scene copies, one 17-KiB empty membership precharge and cell tables fit 64 KiB.
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 65536));
        using var active = admission.Enter();
        var inventory = MapPlacementCellInventory.Capture(scene, routing);
        var cell = Assert.Single(inventory.Cells);
        Assert.Equal(new[] { "sparse-0", "sparse-1" }, cell.Members.Select(p => p.PlacementId));
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.True(admission.RetainedBytes > 0);
        var owner = Assert.IsAssignableFrom<IDisposable>(inventory);
        owner.Dispose(); owner.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
