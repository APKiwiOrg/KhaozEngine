using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Primitives;
using Xunit;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using System.Numerics;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofExitFadeTests
{
    [Theory]
    [InlineData(30, 90, 0)]
    [InlineData(30, 95, .5f)]
    [InlineData(95, 90, 0)]
    [InlineData(95, 95, .5f)]
    [InlineData(95, 99, .9f)]
    public void AcceptedProxy_UsesOrdinaryExitCoverageBeforeHlodComplement(float hlod, float distance, float dissolve)
    {
        using var fixture = NativePresentationFixtures.Create(); var scheduler = new ResidencyScheduler();
        var state = NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope);
        state = state with
        {
            Policy = new(new([new("caps", new HashSet<string> { "roof" }, [], 0, 100, hlod, 10, 0, true)], 100),
            state.Policy.AnimatedAssets, state.Policy.Foliage, state.Policy.Water, [], state.Policy.TerrainMaterials)
        };
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        foreach (var mode in new[] { MapRoofMode.AlwaysVisible, MapRoofMode.AlwaysHidden })
        {
            var sink = new MapDrawRecorder(); residency.Record(NativeRoofHlodRecordingTests.Frame(mode, distance), sink);
            var proxy = Assert.Single(sink.Snapshot().Draws, d => d.Resource.Key.Kind == MapRenderKind.Roof && d.Resource.Key.PartId == "hlod");
            Assert.Equal(dissolve, proxy.Rigid!.Value.Dissolve, 5); Assert.Equal(0, proxy.Rigid.Value.DissolveComplement);
            var end = new MapDrawRecorder(); residency.Record(NativeRoofHlodRecordingTests.Frame(mode, 100), end);
            Assert.DoesNotContain(end.Snapshot().Draws, d => d.Resource.Key.Kind == MapRenderKind.Roof);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedOrUnavailableProxy_AppliesPlacementExitFadeToColourAndOutlines(bool empty)
    {
        using var fixture = NativePresentationFixtures.Create(); var scheduler = new ResidencyScheduler();
        var state = NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope);
        state = state with
        {
            Policy = new(new([new("caps", new HashSet<string> { "roof" }, [], 0, 100, 30, 10, empty ? 1000 : 0, true)], 100),
            state.Policy.AnimatedAssets, state.Policy.Foliage, state.Policy.Water, [], state.Policy.TerrainMaterials)
        };
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var frame = NativePresentationFixtures.Frame(9, empty ? MapRoofMode.AlwaysVisible : MapRoofMode.Auto) with { Focus = new(WorldFrame.Origin, new(95, 0, 2.5f)) };
        var sink = new MapDrawRecorder(); residency.Record(frame, sink);
        var rows = sink.Snapshot().Draws.Where(d => d.Resource.Key.OwnerId == "roof-a").ToArray();
        Assert.Equal(2, rows.Length); Assert.All(rows, d => Assert.Equal(.5f, d.Rigid!.Value.Dissolve, 5));
        var plan = Assert.Single(residency.AcceptedPlans); var outline = new MapDrawRecorder(); plan.Register(outline);
        plan.Inventory.Placements.RecordRoofOutline("roof-a", frame, outline);
        Assert.Equal(2, outline.Snapshot().Draws.Count); Assert.All(outline.Snapshot().Draws, d => Assert.Equal(.5f, d.Rigid!.Value.Dissolve, 5));
    }
    [Fact]
    public void NativeEmitter_ComposesPlacementExitBeforeLodCoverage()
    {
        var rows = new[] { new NativePropInstance("cap", 1, "cap", Matrix4x4.Identity, default) };
        var tiers = new Dictionary<string, IReadOnlyList<NativePropLod>>
        {
            ["cap"] = new[] {
            new NativePropLod(0, new[] { new MeshHandle(1) }), new NativePropLod(1, new[] { new MeshHandle(2) }) }
        };
        var layer = PropLayer.PlacementLayer(rows, tiers, 100, new float[] { 95 }, 10, true, 10);
        var draws = new List<RigidInstanceDraw>();
        PropRenderer.EmitNative(layer, new(95, 0, 0), draws, static (result, _, draw) => result.Add(draw));
        Assert.Equal(new[] { .75f, .25f }, draws.Select(d => d.Dissolve));
        Assert.Equal(new[] { 0f, 1f }, draws.Select(d => d.DissolveComplement));
    }

}
