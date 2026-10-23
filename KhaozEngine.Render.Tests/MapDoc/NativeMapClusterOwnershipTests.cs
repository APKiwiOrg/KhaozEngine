using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapClusterOwnershipTests
{
    [Fact]
    public void OverlappingWindows_ShareOneCompleteCellProxyUploadAndDraw()
    {
        using var fixture = Fixture(10);
        var a = Window(9, 11); var b = Window(9.5f, 12);
        var state = State(fixture, a, b);
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Single(device.Live.Values, k => k.PartId == "hlod");
        Assert.Equal("cluster/selected/0/0/0", Assert.Single(Draw(residency).Draws).Resource.Key.OwnerId);
        var proxies = residency.AcceptedPlans.SelectMany(p => p.Resources).Where(r => r.Reference.Key.PartId == "hlod").ToArray();
        Assert.Equal(2, proxies.Length);
        Assert.True(residency.TryGetHandle(proxies[0].Reference, out var first));
        Assert.True(residency.TryGetHandle(proxies[1].Reference, out var second));
        Assert.Equal(first, second);
        int uploads = device.Begins;

        residency.SetWindows([b]); ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(uploads, device.Begins); Assert.Equal(0, device.Releases);
        Assert.Single(Draw(residency).Draws);
        residency.SetWindows([]); ResidencyFixture.Complete(residency, scheduler);
        Assert.Empty(device.Live); Assert.Equal(uploads, device.Releases);
    }

    [Fact]
    public void PartialCellReplacement_RefusesBeforeUploadAndKeepsAcceptedProxy()
    {
        using var fixture = Fixture(10, 20, 30);
        var state = State(fixture, Window(0, 64));
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var acceptedState = residency.DesiredState;
        var accepted = residency.CurrentTransition.AcceptedWitness;
        var draws = Draw(residency).Draws;
        Assert.Equal("cluster/selected/0/0/0", Assert.Single(draws).Resource.Key.OwnerId);
        int uploads = device.Begins;
        var a = Window(9, 21); var b = Window(19, 31);
        Assert.Equal(new[] { "prop-0", "prop-1" }, state.Source.Acquire(a).Scene!.Placements.Select(p => p.PlacementId));
        Assert.Equal(new[] { "prop-1", "prop-2" }, state.Source.Acquire(b).Scene!.Placements.Select(p => p.PlacementId));

        residency.RequestReplacement(state with { Windows = [a, b] }); scheduler.RunAll();
        Assert.Equal(MapSceneStatus.Invalid, residency.Update().Transition.Status);
        Assert.Equal(accepted, residency.CurrentTransition.AcceptedWitness);
        Assert.Same(acceptedState, residency.DesiredState);
        Assert.Equal(draws, Draw(residency).Draws);
        Assert.Equal(uploads, device.Begins); Assert.Equal(0, device.Releases);
        Assert.Equal(MapSceneStatus.Invalid, residency.Update().Transition.Status);
        Assert.Equal(0, residency.Lifetime.WorkerCount);
    }

    static ResidencyFixture Fixture(params float[] x)
    {
        var fixture = ResidencyFixture.Placement(NativeRenderAssetFixtures.Placements());
        fixture.Document.TileSize = 1;
        fixture.Document.Placements = x.Select((value, i) => new MapPlacement
        { Id = "prop-" + i, AssetId = "prop-a", Kind = "prop", X = value, Y = 0, Z = 10, NumericId = i + 1 }).ToList();
        return fixture;
    }
    static MapSceneScope Window(float min, float max)
    {
        var scope = ResidencyFixture.Window(min, max);
        return scope with
        { DrawBounds = new(min, -500, 9, max, 500, 11), Surfaces = scope.Surfaces with { LocalMin = new(min, 9), LocalMax = new(max, 11) } };
    }
    static MapRenderSourceState State(ResidencyFixture fixture, params MapSceneScope[] windows)
    {
        var state = fixture.State(windows);
        return state with
        {
            Policy = new(NativeMapClusterCellTests.Policy(300), state.Policy.AnimatedAssets,
                state.Policy.Foliage, state.Policy.Water, state.Policy.MaterialResourceIds, state.Policy.TerrainMaterials)
        };
    }
    static MapDrawSet Draw(MapRenderResidency residency)
    {
        var sink = new MapDrawRecorder(); var focus = new MapFramePoint(WorldFrame.Origin, new(100, 0, 10));
        residency.Record(new(WorldFrame.Origin, focus, MapRoofMode.AlwaysVisible, 0, 0, focus), sink);
        return sink.Snapshot();
    }
}
