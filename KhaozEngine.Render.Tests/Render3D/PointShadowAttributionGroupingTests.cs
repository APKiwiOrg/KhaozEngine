using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class PointShadowAttributionGroupingTests
{
    [Fact]
    public void Fixed_motion_words_follow_the_existing_mesh_and_interior_scatter_without_temporal_output()
    {
        SceneInstances.Instance[] input = [
            new(new RigidInstanceDraw(new(0, 1), Matrix4x4.Identity) { Motion = MotionKey.From(11) }),
            new(new RigidInstanceDraw(new(1, 1), Matrix4x4.Identity) { Motion = MotionKey.From(22) }),
            new(new RigidInstanceDraw(new(0, 1), Matrix4x4.Identity) { Motion = MotionKey.From(33), CutInsideInteriors = true })];
        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        ulong[] keys = new ulong[3];
        Scene3D.GroupInstances(input, data, runs, interiorCuts: new InteriorCutInstancePartition(), pointMotionKeys: keys);
        Assert.Equal(new ulong[] { 33, 11, 22 }, keys);
        Assert.Equal(3, data.Count); Assert.Equal(2, runs.Count);
        ulong[] insufficient = [99];
        Scene3D.GroupInstances(input, data, runs, interiorCuts: new InteriorCutInstancePartition(), pointMotionKeys: insufficient);
        Assert.Equal(99UL, insufficient[0]); Assert.Equal(3, data.Count); Assert.Equal(2, runs.Count);
    }

    [Fact]
    public void New_grouping_high_water_latches_motion_capacity_without_growing_or_changing_rendering()
    {
        using var f = new ShadowStageProbeFixture(); using var control = new ShadowStageProbeFixture();
        f.Render(ShadowStageProbeMode.Off); f.Render(ShadowStageProbeMode.Off);
        control.Render(ShadowStageProbeMode.Off); control.Render(ShadowStageProbeMode.Off);
        var capture = new PointShadowAttributionCapture(Guid.NewGuid(), new(8, 8, 64, 1, 128, 64, 32, 32));
        void Queue(Scene3D s) => s.Draw(f.Mesh, Matrix4x4.Identity);
        var result = f.Render(ShadowStageProbeMode.Counts, extraQueue: Queue, attribution: capture);
        control.Render(ShadowStageProbeMode.Counts, extraQueue: Queue);
        Assert.Equal(PointAttributionCapacity.MotionKeys, capture.Overflow);
        Assert.Equal(result.Request.Sequence, capture.OverflowSequence);
        Assert.Null(result.PointAttribution);
        Assert.True(capture.MotionKeys.IsEmpty);
        Assert.Equal(f.Order(), control.Order()); Assert.Equal(f.Signatures(), control.Signatures());
    }

    [Fact]
    public void Warmed_parallel_grouping_with_owned_scratch_allocates_nothing()
    {
        SceneInstances.Instance[] input = [new(new RigidInstanceDraw(new(0, 1), Matrix4x4.Identity) { Motion = MotionKey.From(71) })];
        var data = new List<ModelRenderer.InstanceData>(); var runs = new List<Scene3D.MeshRun>();
        var lookup = new Dictionary<(int, int), int>();
        var cursors = new List<uint>();
        var capture = new PointShadowAttributionCapture(Guid.NewGuid(), new(2, 2, 2, 2, 2, 2, 2, 2));
        void Group() => Scene3D.GroupInstances(input, data, runs, lookup, writeCursorScratch: cursors, pointMotionKeys: capture.MotionKeys);
        Group();
        AllocationMeasurement.NoPerCallAllocation("owned point MotionKey grouping", Group);
        Assert.Equal(71UL, capture.MotionKeys[0]);
    }
}
