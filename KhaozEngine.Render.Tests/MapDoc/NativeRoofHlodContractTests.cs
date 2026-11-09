using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("AllocSensitive")]
public sealed class NativeRoofHlodContractTests
{
    [Fact]
    public void WarmRoofFrames_AllocateNothingAndNeverRebuildOrUpload()
    {
        using var fixture = NativePresentationFixtures.Create(); var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope), device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var frames = new[] { NativeRoofHlodRecordingTests.Frame(MapRoofMode.Auto, 0), NativeRoofHlodRecordingTests.Frame(MapRoofMode.AlwaysVisible, 30),
            NativeRoofHlodRecordingTests.Frame(MapRoofMode.AlwaysHidden, 40) };
        var sink = new Counter();
        void RecordFrames() { foreach (var frame in frames) residency.Record(frame, sink); }
        RecordFrames(); RecordFrames();
        int uploads = device.Begins, releases = device.Releases;
        long bytes = AllocationMeasurement.Measure("native roof HLOD frames", RecordFrames);
        Assert.Equal(0, bytes); Assert.True(sink.Writes > 0);
        Assert.Equal(uploads, device.Begins); Assert.Equal(releases, device.Releases);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("pose")]
    [InlineData("anchor")]
    [InlineData("member")]
    public void ForgedRoofRequest_RefusesBeforeLowerMerge(string forgery)
    {
        using var fixture = NativePresentationFixtures.Create(); var state = NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope);
        var scene = state.Source.Acquire(state.Windows[0]).Scene!;
        var routing = MapPlacementRouting.Create(scene, state.Policy.Placements, state.Policy.AnimatedAssets).Routing!;
        var cells = MapPlacementCellInventory.Capture(scene, routing);
        using var input = MapPreparedInputLease.LoadSelected(state.Closure, ["roof", "object", "low", "explicit"], [], state.Extents, new(new())).Input!;
        using var batch = NativeMapPlacements.Prepare(scene, input, state.Policy.Placements, routing, cells);
        var request = Assert.Single(batch.Clusters).Request;
        var instances = request.Instances.ToArray(); var key = request.Key; var anchor = request.Anchor;
        if (forgery == "role") key = key with { Cohort = 0 };
        if (forgery == "pose") instances[0] = instances[0] with { LocalTransform = System.Numerics.Matrix4x4.Identity };
        if (forgery == "anchor") anchor = new(anchor.X + 1, anchor.Y, anchor.Z);
        if (forgery == "member") instances = instances.Take(1).ToArray();
        var attack = new NativePropClusterRequest(key, request.Generation, request.Area, request.Layer, instances, anchor);
        int entries = 0;
        var error = Record.Exception(() => NativeMapPlacements.PrepareCluster(scene, routing, attack,
            value => { entries++; return PropClusterRenderer.BuildNativeCpu(value); }));
        Assert.NotNull(error); Assert.Equal(0, entries);
    }

    [Fact]
    public void CanonicalRoofPreparation_ReservesBeforeDecodeAndRefusesExhaustedAdmission()
    {
        using var fixture = NativePresentationFixtures.Create(); var state = NativeRoofHlodPolicyTests.State(fixture, NativePresentationFixtures.Scope);
        var scene = state.Source.Acquire(state.Windows[0]).Scene!;
        var routing = MapPlacementRouting.Create(scene, state.Policy.Placements, state.Policy.AnimatedAssets).Routing!;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1)); using var activation = admission.Enter();
        var error = Assert.Throws<MapSceneCapacityException>(() => MapPlacementCellInventory.Capture(scene, routing));
        Assert.Equal(nameof(MapSceneLimits.MaxPreparedBytes), error.LimitName);
        Assert.InRange(admission.RetainedBytes, 0, 1);
    }
    sealed class Counter : IMapDrawSink
    {
        internal int Writes;
        public void Register(MapPreparedResource resource) { }
        public void Write(in MapDrawRecord draw) => Writes++;
    }
}
