using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofHlodPolicyTests
{
    internal static MapRenderSourceState State(ResidencyFixture fixture, params MapSceneScope[] windows)
    {
        var state = fixture.State(windows);
        return state with { Policy = new(new([new("caps", new HashSet<string> { "roof" }, [], 0, 100, 30, 10, 0, true)], 100),
            new HashSet<string>(), state.Policy.Foliage, state.Policy.Water, [], new Dictionary<ushort, string>()) };
    }
    internal static MapSceneScope Window(float x) => NativePresentationFixtures.Scope with
    {
        DrawBounds = new(x - .25, 0, 2, x + .25, 5, 3),
        Surfaces = NativePresentationFixtures.Scope.Surfaces with { LocalMin = new(x - .25f, -.5f), LocalMax = new(x + .25f, 0) }
    };

    [Fact]
    public void PartialWindows_AcquireIdenticalCompleteRoofMembershipBeforeDecode()
    {
        using var fixture = NativePresentationFixtures.Create();
        var state = State(fixture, Window(0), Window(8));
        var cells = state.Windows.Select(window =>
        {
            var scene = state.Source.Acquire(window).Scene!;
            var route = MapPlacementRouting.Create(scene, state.Policy.Placements, state.Policy.AnimatedAssets).Routing!;
            return Assert.Single(MapPlacementCellInventory.Capture(scene, route).Cells);
        }).ToArray();
        Assert.All(cells, cell => Assert.Equal(new[] { "roof-a", "roof-b" }, cell.Members.Select(p => p.PlacementId)));
        Assert.Equal(cells[0].Scene.Scope.Surfaces.Digest, cells[1].Scene.Scope.Surfaces.Digest);
        foreach (var cell in cells)
        {
            var roofs = MapRoofEvaluationBuffer.Prepare(cell.Scene).Buffer!;
            roofs.Evaluate(new(WorldFrame.Origin, new(1, 1, -1)), MapRoofMode.Auto);
            Assert.False(Assert.Single(roofs.Roofs, r => r.RoofPlacementId == "roof-a").ColourVisible);
            Assert.True(Assert.Single(roofs.Roofs, r => r.RoofPlacementId == "roof-b").ColourVisible);
            roofs.Evaluate(new(WorldFrame.Origin, new(9, 1, -1)), MapRoofMode.Auto);
            Assert.True(Assert.Single(roofs.Roofs, r => r.RoofPlacementId == "roof-a").ColourVisible);
            Assert.False(Assert.Single(roofs.Roofs, r => r.RoofPlacementId == "roof-b").ColourVisible);
        }
    }
}
