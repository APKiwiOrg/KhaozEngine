using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeRoofHlodRecordingTests
{
    internal static MapFrameState Frame(MapRoofMode mode = MapRoofMode.AlwaysVisible, float distance = 40) =>
        NativePresentationFixtures.Frame(mode: mode) with { Focus = new(WorldFrame.Origin, new(32 + distance, 0, 32)) };

    [Fact]
    public void RoofCohorts_EmitOnlyFromRoofPassAndShareOnePublicationLifetime()
    {
        using var fixture = NativePresentationFixtures.Create();
        var state = NativeRoofHlodPolicyTests.State(fixture, NativeRoofHlodPolicyTests.Window(0), NativeRoofHlodPolicyTests.Window(8));
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Single(device.Live.Values, k => k.Kind == MapRenderKind.Roof && k.PartId == "hlod");
        var frame = Frame();
        foreach (var plan in residency.AcceptedPlans)
        {
            Assert.Equal(new[] { "roof-a", "roof-b" }, Assert.Single(plan.Inventory.Placements.Clusters).RepresentedIds);
            var ordinary = new MapDrawRecorder(); plan.Register(ordinary); plan.Inventory.Placements.Record(frame, ordinary);
            Assert.DoesNotContain(ordinary.Snapshot().Draws, d => d.Resource.Key.Kind == MapRenderKind.Roof);
            var roofs = new MapDrawRecorder(); plan.Register(roofs); NativeMapRoofs.Record(plan, frame, roofs);
            Assert.Equal(MapRenderKind.Roof, Assert.Single(roofs.Snapshot().Draws).Resource.Key.Kind);
            Assert.Equal("hlod", Assert.Single(roofs.Snapshot().Draws).Resource.Key.PartId);
        }
        var combined = new MapDrawRecorder(); residency.Record(frame, combined);
        Assert.Single(combined.Snapshot().Draws, d => d.Resource.Key.Kind == MapRenderKind.Roof);
        int uploads = device.Begins;
        residency.SetWindows([state.Windows[1]]); ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(uploads, device.Begins);
        Assert.Single(device.Live.Values, k => k.Kind == MapRenderKind.Roof && k.PartId == "hlod");
        residency.SetWindows([]); ResidencyFixture.Complete(residency, scheduler);
        Assert.Empty(device.Live); Assert.Equal(uploads, device.Releases);
    }
    [Fact]
    public void HeldAndCancelledReplacement_KeepsAcceptedRoofMetadataPairedWithItsProxy()
    {
        using var fixture = NativePresentationFixtures.Create();
        var state = NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope);
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var accepted = Assert.Single(residency.AcceptedPlans);
        var frame = Frame(MapRoofMode.Auto);
        var first = new MapDrawRecorder(); residency.Record(frame, first);
        var before = first.Snapshot().Draws.Where(d => d.Resource.Key.Kind == MapRenderKind.Roof).ToArray();
        Assert.Equal(4, before.Length); Assert.DoesNotContain(before, d => d.Resource.Key.PartId == "hlod");
        var intent = fixture.Document.BuildingIntent!;
        fixture.Document.BuildingIntent = new(1, intent.RoofLinks.Select(l => l with { AboveY = .5f }).ToArray(), intent.RoofGroups, intent.Lights, intent.Markers)
        { Roofs = intent.Roofs, Storeys = intent.Storeys };
        var changed = NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope);
        residency.RequestReplacement(changed);
        var held = new MapDrawRecorder(); residency.Record(frame, held);
        Assert.Same(accepted, Assert.Single(residency.AcceptedPlans));
        Assert.Equal(before, held.Snapshot().Draws.Where(d => d.Resource.Key.Kind == MapRenderKind.Roof));
        // Cancel before the held worker enters. Its metadata must never become the accepted generation.
        residency.RequestReplacement(state); ResidencyFixture.Complete(residency, scheduler);
        var cancelled = new MapDrawRecorder(); residency.Record(frame, cancelled);
        Assert.Equal(before.Select(d => (d.Resource.Key, d.Channels, d.Rigid)), cancelled.Snapshot().Draws
            .Where(d => d.Resource.Key.Kind == MapRenderKind.Roof).Select(d => (d.Resource.Key, d.Channels, d.Rigid)));
        residency.RequestReplacement(changed); ResidencyFixture.Complete(residency, scheduler);
        var current = new MapDrawRecorder(); residency.Record(frame, current);
        var proxy = Assert.Single(current.Snapshot().Draws, d => d.Resource.Key.Kind == MapRenderKind.Roof);
        Assert.Equal("hlod", proxy.Resource.Key.PartId); Assert.True(proxy.Channels.HasFlag(MapDrawChannels.Colour));
    }

}
