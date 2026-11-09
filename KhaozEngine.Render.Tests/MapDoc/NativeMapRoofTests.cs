using System;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapRoofTests
{
    [Fact]
    public void RoofFourChannels_PreservePhysicalAndSky()
    {
        using var fixture = NativePresentationFixtures.Create(); using var plan = NativePresentationFixtures.Plan(fixture);
        var visible = NativePresentationFixtures.Draw(plan, NativePresentationFixtures.Frame(mode: MapRoofMode.AlwaysVisible));
        var hidden = NativePresentationFixtures.Draw(plan, NativePresentationFixtures.Frame());
        var roof = Assert.Single(hidden.Draws, d => d.Resource.Key.OwnerId == "roof-a" && d.Resource.Key.PartId == "part/0");
        Assert.Equal(new[] { false, true, false, true }, new[] { roof.Channels.HasFlag(MapDrawChannels.Colour),
            roof.Channels.HasFlag(MapDrawChannels.Shadow), roof.Channels.HasFlag(MapDrawChannels.Camera), roof.Channels.HasFlag(MapDrawChannels.Sky) });
        Assert.True(roof.Channels.HasFlag(MapDrawChannels.Physical)); Assert.False(roof.Channels.HasFlag(MapDrawChannels.Selection));
        Assert.Equal(Assert.Single(visible.Draws, d => d.Resource == roof.Resource), roof with { Channels = (MapDrawChannels)63 });
        Assert.Equal(visible.Resources, hidden.Resources);
        Assert.All(hidden.Draws.Where(d => d.Resource.Key.OwnerId == "shelter"), d => Assert.True(d.Channels.HasFlag(MapDrawChannels.Colour)));
        var ordinary = new MapDrawRecorder(); plan.Register(ordinary); plan.Inventory.Placements.Record(NativePresentationFixtures.Frame(), ordinary);
        Assert.DoesNotContain(ordinary.Snapshot().Draws, d => d.Resource.Key.Kind == MapRenderKind.Roof);
    }

    [Fact]
    public void PlanRoofBufferChargesTheSharedAdmissionBeforeAllocation()
    {
        using var fixture = NativePresentationFixtures.Create(); var state = fixture.State(NativePresentationFixtures.Scope);
        var scene = state.Source.Acquire(NativePresentationFixtures.Scope).Scene!;
        var independent = new MapSceneAdmission(new());
        Assert.NotNull(MapRoofEvaluationBuffer.Prepare(scene, independent).Buffer);
        long roofBytes = independent.RetainedBytes; Assert.True(roofBytes > 0);
        using var cache = new MapRenderAssetCache();
        using var input = cache.Acquire(state.Closure, scene.Placements.Select(p => p.AssetId).Distinct().ToArray(), [], state.Extents, new(new())).Input!;
        var shared = new MapSceneAdmission(new(MaxPreparedBytes: roofBytes));
        var refused = Assert.Throws<MapSceneSourceException>(() => MapRenderPlan.Prepare(state.Source, scene, input, state.Policy, 1, shared, null));
        Assert.Equal(MapSceneStatus.CapacityExceeded, refused.Status);
        Assert.Equal(0, shared.RetainedBytes);
        Assert.Equal(0, shared.WorkspaceBytes);
        // Refusal leaves the caller's selected input alive.
        Assert.NotEmpty(input.Assets.ById);
    }

    [Fact]
    public void AcceptedHlod_RoofModesAndRoomsChangeOnlyRoofChannels()
    {
        using var fixture = NativePresentationFixtures.Create(true);
        var state = fixture.State(NativePresentationFixtures.Scope);
        state = state with
        {
            Policy = new(NativeMapPlacementTests.Policy(true), new System.Collections.Generic.HashSet<string>(),
            state.Policy.Foliage, state.Policy.Water, [], new System.Collections.Generic.Dictionary<ushort, string>())
        };
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var plan = Assert.Single(residency.AcceptedPlans);
        int uploads = device.Begins, publications = device.Publications;
        var builds = plan.Inventory.Placements.Clusters.ToArray(); var resources = plan.Resources.ToArray();
        foreach (float distance in new[] { 30f, 40f })
        {
            var frame = NativePresentationFixtures.Frame() with { Focus = new(KhaozEngine.Primitives.WorldFrame.Origin, new(32 + distance, 0, 32)) };
            var baseline = NativePresentationFixtures.Draw(plan, frame with { RoofMode = MapRoofMode.AlwaysVisible });
            Assert.Contains(baseline.Draws, d => d.Resource.Key.PartId == "hlod");
            foreach (var (observer, mode, a, b) in new[] { (1f, MapRoofMode.Auto, false, true), (9f, MapRoofMode.Auto, true, false),
                (1f, MapRoofMode.AlwaysHidden, false, false), (9f, MapRoofMode.AlwaysVisible, true, true) })
            {
                var current = NativePresentationFixtures.Draw(plan, frame with { Observer = NativePresentationFixtures.Frame(observer).Observer, RoofMode = mode });
                Assert.Equal(baseline.Draws.Where(d => d.Resource.Key.Kind != MapRenderKind.Roof), current.Draws.Where(d => d.Resource.Key.Kind != MapRenderKind.Roof));
                foreach (var (id, colour) in new[] { ("roof-a", a), ("roof-b", b) })
                {
                    var rows = current.Draws.Where(d => d.Resource.Key.OwnerId == id).ToArray(); Assert.Equal(2, rows.Length);
                    Assert.All(rows, d => { Assert.Equal(colour, d.Channels.HasFlag(MapDrawChannels.Colour)); Assert.True(d.Channels.HasFlag(MapDrawChannels.Shadow)); Assert.Equal(0, d.Rigid!.Value.Dissolve); });
                    Assert.Equal(rows.Length, rows.Select(d => d.Resource).Distinct().Count());
                    Assert.Equal(baseline.Draws.Where(d => d.Resource.Key.OwnerId == id).Select(d => d.Resource), rows.Select(d => d.Resource));
                }
                Assert.Equal(resources, plan.Resources); Assert.Equal(builds, plan.Inventory.Placements.Clusters);
                residency.Update(); Assert.Equal(uploads, device.Begins); Assert.Equal(publications, device.Publications);
            }
            foreach (var build in builds)
            { Assert.DoesNotContain(build.RepresentedIds, id => id.StartsWith("roof", StringComparison.Ordinal)); Assert.All(build.MergedMesh.Vertices, v => Assert.True(v.Position.Y < 3)); }
        }
    }
}
