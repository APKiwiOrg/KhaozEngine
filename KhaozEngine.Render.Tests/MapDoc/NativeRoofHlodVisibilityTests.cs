using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeRoofHlodVisibilityTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(25, false, 0)]
    [InlineData(30, true, .5f)]
    [InlineData(35, true, 0)]
    [InlineData(40, true, 0)]
    public void HiddenCoverage_MatchesVisibleTransformsMaterialsAndComplement(float distance, bool merged, float dissolve)
    {
        using var fixture = NativePresentationFixtures.Create();
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope), device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        MapDrawRecord[] Draw(MapRoofMode mode)
        {
            var sink = new MapDrawRecorder(); residency.Record(NativeRoofHlodRecordingTests.Frame(mode, distance), sink);
            return sink.Snapshot().Draws.Where(d => d.Resource.Key.Kind == MapRenderKind.Roof).ToArray();
        }
        var visible = Draw(MapRoofMode.AlwaysVisible); var hidden = Draw(MapRoofMode.AlwaysHidden);
        Assert.Equal(visible.Length, hidden.Length); Assert.NotEmpty(hidden);
        Assert.Equal(merged, hidden.Any(d => d.Resource.Key.PartId == "hlod"));
        for (int i = 0; i < hidden.Length; i++)
        {
            Assert.Equal(visible[i] with { Channels = MapDrawChannels.Shadow | MapDrawChannels.Sky | MapDrawChannels.Physical }, hidden[i]);
            Assert.Equal(dissolve, hidden[i].Rigid!.Value.Dissolve);
        }
        if (distance == 30)
        {
            var proxy = Assert.Single(hidden, d => d.Resource.Key.PartId == "hlod");
            Assert.Equal(1, proxy.Rigid!.Value.DissolveComplement);
            Assert.False(proxy.Rigid.Value.InvertShadowDissolve);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(40)]
    public void MixedVisibility_FallsBackToIndividualsWithoutHlodFloor(float distance)
    {
        using var fixture = NativePresentationFixtures.Create(); var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope), new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var frame = NativeRoofHlodRecordingTests.Frame(MapRoofMode.Auto, distance);
        var sink = new MapDrawRecorder(); residency.Record(frame, sink);
        var rows = sink.Snapshot().Draws.Where(d => d.Resource.Key.Kind == MapRenderKind.Roof).ToArray();
        Assert.Equal(4, rows.Length); Assert.DoesNotContain(rows, d => d.Resource.Key.PartId == "hlod");
        Assert.All(rows, d => { Assert.Equal(0, d.Rigid!.Value.Dissolve); Assert.Equal(d.Resource.Key.OwnerId == "roof-b", d.Channels.HasFlag(MapDrawChannels.Colour)); });
        var outside = new MapDrawRecorder(); residency.Record(frame with { Focus = NativeRoofHlodRecordingTests.Frame(distance: 100).Focus }, outside);
        Assert.DoesNotContain(outside.Snapshot().Draws, d => d.Resource.Key.Kind == MapRenderKind.Roof);
    }

    [Fact]
    public void ShadowDisabled_ConjoinsRoofIntentWithLayerPolicy()
    {
        using var fixture = NativePresentationFixtures.Create(); var scheduler = new ResidencyScheduler();
        var state = NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope);
        state = state with { Policy = new(new([new("caps", new HashSet<string> { "roof" }, [], 0, 100, 30, 10, 0, false)], 100),
            state.Policy.AnimatedAssets, state.Policy.Foliage, state.Policy.Water, [], state.Policy.TerrainMaterials) };
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        foreach (float distance in new[] { 0f, 30f, 40f })
        {
            var sink = new MapDrawRecorder(); residency.Record(NativeRoofHlodRecordingTests.Frame(MapRoofMode.AlwaysHidden, distance), sink);
            Assert.DoesNotContain(sink.Snapshot().Draws, d => d.Resource.Key.Kind == MapRenderKind.Roof &&
                (d.Channels & (MapDrawChannels.Colour | MapDrawChannels.Shadow)) != 0);
        }
    }

    [Fact]
    public void AcceptedRoofProxy_ReachesRealShadowOnlySink()
    {
        using var fixture = NativePresentationFixtures.Create(); var scheduler = new ResidencyScheduler();
        using var host = new NativeUploadTestScene(); var sink = new MapScene3DSink(host.Scene);
        using var residency = new MapRenderResidency(NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope), sink, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        host.Scene.Begin(); residency.Record(NativeRoofHlodRecordingTests.Frame(MapRoofMode.AlwaysHidden), sink);
        Assert.Single(host.Scene.QueuedInstancesForTests, d => d.ShadowOnly);
        host.Render();
        Assert.Equal(1, host.Scene.ShadowOnlyInstances);
    }
}
