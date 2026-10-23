using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofHlodLodTests
{
    [Theory]
    [InlineData(0, 0, -1)]
    [InlineData(10, 0, 1)]
    [InlineData(15, 1, -1)]
    [InlineData(20, 1, 2)]
    [InlineData(25, 2, -1)]
    public void HiddenMultipartRoof_RetainsEveryTierAndLodComplement(float distance, int near, int far)
    {
        var closure = NativeRenderAssetFixtures.Placements(); var scene = NativeMapPlacementTests.Scene(closure);
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = new MapPlacementPolicy([new("caps", new HashSet<string> { "cap-a" }, [10, 20], 4, 10000, 1000, 10, 0, true)], 10000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var buffer = MapRoofEvaluationBuffer.Prepare(scene).Buffer!;
        var indices = buffer.Roofs.Select((r, i) => (r.RoofPlacementId, i)).ToDictionary(r => r.RoofPlacementId, r => r.i);
        var pose = scene.Transforms["Placement:roof-a"];
        var frame = NativeMapPlacementTests.Frame with
        {
            RoofMode = MapRoofMode.AlwaysHidden,
            Focus = new(WorldFrame.Origin, new((float)pose.X + distance, 0, (float)pose.Z))
        };
        buffer.Evaluate(frame.Observer, frame.RoofMode);
        var sink = new MapDrawRecorder(); batch.Register(NativeMapPlacementTests.Stamp, sink);
        batch.RecordRoofs(frame, buffer, indices, sink);
        var rows = sink.Snapshot().Draws.Where(d => d.Resource.Key.OwnerId == "roof-a").ToArray();
        Assert.Equal(far < 0 ? new[] { near } : new[] { near, far }, rows.Select(d => d.Resource.Key.Lod).Distinct());
        Assert.Equal(far < 0 ? 2 : 4, rows.Length);
        Assert.All(rows, d => Assert.Equal(MapDrawChannels.Shadow | MapDrawChannels.Sky | MapDrawChannels.Physical, d.Channels));
        Assert.All(rows, d => Assert.Equal(far < 0 ? 0 : .5f, d.Rigid!.Value.Dissolve, 5));
        if (far >= 0) Assert.Equal(new[] { 0f, 0f, 1f, 1f }, rows.Select(d => d.Rigid!.Value.DissolveComplement));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1000, false)]
    public void HardSwapAndEmptyProxy_KeepTheReleasedFallback(float weld, bool proxy)
    {
        using var fixture = NativePresentationFixtures.Create(); var scheduler = new ResidencyScheduler();
        var state = NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope);
        state = state with
        {
            Policy = new(new([new("caps", new HashSet<string> { "roof" }, [], 0, 100, 30, 0, weld, true)], 100),
            state.Policy.AnimatedAssets, state.Policy.Foliage, state.Policy.Water, [], state.Policy.TerrainMaterials)
        };
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var sink = new MapDrawRecorder(); residency.Record(NativeRoofHlodRecordingTests.Frame(distance: 30), sink);
        var rows = sink.Snapshot().Draws.Where(d => d.Resource.Key.Kind == MapRenderKind.Roof).ToArray();
        Assert.Equal(proxy ? 1 : 4, rows.Length); Assert.Equal(proxy, rows.Any(d => d.Resource.Key.PartId == "hlod"));
        Assert.All(rows, d => Assert.Equal(0, d.Rigid!.Value.Dissolve));
    }
}
