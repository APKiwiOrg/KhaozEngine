using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapClusterOriginOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OffsetGeometry_OriginOwnedCellRetainsIndividualsAndProxy(bool centeredNeighbor)
    {
        using var fixture = ResidencyFixture.Placement(NativeRenderAssetFixtures.OffsetPlacements());
        fixture.Document.TileSize = 1;
        fixture.Document.Placements = [new MapPlacement
        { Id = "B", AssetId = "prop-b", Kind = "prop", X = 63, Y = 0, Z = 10, NumericId = 2 }];
        if (centeredNeighbor) fixture.Document.Placements.Add(new MapPlacement
        { Id = "A", AssetId = "prop-a", Kind = "prop", X = 10, Y = 0, Z = 10, NumericId = 1 });
        var window = ResidencyFixture.Window(65, 66) with
        {
            DrawBounds = new(65, -500, 9, 66, 500, 11),
            MotionPaddingMetres = 0
        };
        var state = fixture.State(window);
        state = state with
        {
            Policy = new(new([new MapPlacementLayer("selected", new HashSet<string> { "prop-a", "prop-b" },
                new float[] { 10, 20 }, 4, 300, 30, 10, 0, true)], 300), state.Policy.AnimatedAssets,
                state.Policy.Foliage, state.Policy.Water, state.Policy.MaterialResourceIds, state.Policy.TerrainMaterials)
        };
        Assert.Equal(new[] { "B" }, state.Source.Acquire(window).Scene!.Placements.Select(p => p.PlacementId));
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);

        string[] expected = centeredNeighbor ? ["A", "B"] : ["B"];
        var plan = Assert.Single(residency.AcceptedPlans);
        Assert.Equal(expected, Assert.Single(plan.Inventory.Placements.Clusters).RepresentedIds);
        Assert.Equal(expected, plan.Inventory.Placements.Inventory.Select(p => p.PlacementId));
        var individuals = plan.Resources.Where(r => r.Reference.Key.OwnerId == "B").ToArray();
        Assert.Equal(6, individuals.Length);
        Assert.All(individuals, r => Assert.All(Assert.IsType<MapRigidResource>(r.Payload).Mesh.Vertices,
            v => Assert.InRange(v.Position.X, 2, 3)));
        var near = Draw(residency, 32, 32);
        Assert.Equal(expected, near.Draws.Select(d => d.Resource.Key.OwnerId).Distinct().Order().ToArray());
        Assert.DoesNotContain(near.Draws, d => d.Resource.Key.PartId == "hlod");

        var proxyDraw = Assert.Single(Draw(residency, 100, 10).Draws);
        Assert.Equal("cluster/selected/0/0/0", proxyDraw.Resource.Key.OwnerId);
        Assert.Equal("hlod", proxyDraw.Resource.Key.PartId);
        var proxy = Assert.Single(plan.Resources, r => r.Reference == proxyDraw.Resource);
        var mesh = Assert.IsType<MapRigidResource>(proxy.Payload).Mesh;
        Assert.Equal(centeredNeighbor ? 12 : 6, mesh.Vertices.Length);
        Assert.Contains(mesh.Vertices, v => proxyDraw.Anchor.X + v.Position.X == 65);
        Assert.Contains(mesh.Vertices, v => proxyDraw.Anchor.X + v.Position.X == 66);
    }

    static MapDrawSet Draw(MapRenderResidency residency, float x, float z)
    {
        var sink = new MapDrawRecorder(); var focus = new MapFramePoint(WorldFrame.Origin, new(x, 0, z));
        residency.Record(new(WorldFrame.Origin, focus, MapRoofMode.AlwaysVisible, 0, 0, focus), sink);
        return sink.Snapshot();
    }
}
