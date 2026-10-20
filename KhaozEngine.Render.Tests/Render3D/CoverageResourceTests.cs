using System;
using System.Numerics;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageResourceTests
{
    [Fact]
    public void Upload_rejects_prior_mutation_and_isolates_later_mutation()
    {
        using var h = new CoverageSceneFixture();
        h.HostMesh.Vertices[0].Position = new(99);
        h.RigidMesh.Vertices[0].Position = new(99);
        int buffers = h.Factory.Buffers.Count;
        Assert.Throws<System.IO.InvalidDataException>(() => h.Scene.LoadSkinnedMesh(h.HostMesh));
        Assert.Equal(buffers, h.Factory.Buffers.Count);
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(frame).Presentation);
        h.AssertCoverageDispatch(frame);
    }

    [Fact]
    public void Material_slices_borrow_independent_ranges_of_one_surface()
    {
        using var h = new CoverageSceneFixture(seams: true);
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.Scene.DrawSkinned(h.HostDraw(frame, slice: 1), h.Palette);
        h.Outline(h.HostDraw(frame));
        h.Outline(h.HostDraw(frame, slice: 1));
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(frame).Presentation);
        var first = frame.Preparation!.GpuReservation;
        Assert.Equal(h.Conformer.Topology.VertexCount, first.VertexCount);
        Assert.Equal(1, h.Scene.DispatchedHostPreparationCount);
        h.AssertCanonicalDraws(frame);
    }

    [Fact]
    public void Missing_host_material_submission_selects_fallback()
    {
        using var h = new CoverageSceneFixture(seams: true);
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.UncoveredFallback, h.Scene.GetCoverageOutcome(frame).Presentation);
    }
    [Fact]
    public void Same_mesh_equipment_instances_keep_distinct_frozen_stage_identities()
    {
        using var h = new CoverageSceneFixture();
        using var second = h.Scene.CreateHostConformer(h.HostMesh.Skeleton!, h.Host);
        second.ReserveSupports([h.Support]);
        h.Begin();
        var first = h.Stage();
        Matrix4x4 world = Matrix4x4.CreateTranslation(3, 0, 0);
        var draws = new RigidInstanceDraw[1];
        Assert.True(h.Scene.TryStageCoverageEquipment(MotionKey.From(20), 1, 2, h.Support,
            [new RigidInstanceDraw(h.RigidHandle, world)], draws, out var failure), failure.ToString());
        var commands = h.CommandPoses;
        commands[0] = commands[0] with
        {
            Topology = second.Topology,
            EquipmentInstanceId = 2,
            EquipmentWorld = world,
            CoverageWorld = h.Support.CoverageLocal * world
        };
        Assert.True(second.TryPrepare(h.Palette, world, commands, MotionKey.From(20),
            CoverageFallback.UncoveredCurrentPose, out var other, out failure), failure.ToString());
        h.Scene.DrawSkinned(h.HostDraw(first), h.Palette);
        h.Scene.DrawSkinned(new SkinnedInstanceDraw(h.Host[0], world) { Motion = MotionKey.From(20), Coverage = other }, h.Palette);
        h.SubmitEquipment();
        h.Scene.Draw(draws[0]);
        h.Render();
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(first).Presentation);
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(other).Presentation);
        Assert.Equal(2, h.Scene.DispatchedHostPreparationCount);
        Assert.NotEqual(first.Preparation!.GpuReservation.CurrentBase, other.Preparation!.GpuReservation.CurrentBase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_equipment_material_slice_needs_actual_submission(bool omitSecond)
    {
        using var h = new CoverageSceneFixture();
        var source = h.Support.Source;
        int count = source.Positions.Length;
        Vector3[] positions = [.. source.Positions.ToArray(), .. source.Positions.ToArray()];
        var vertices = Enumerable.Range(0, 2).SelectMany(slice => Enumerable.Range(0, count)
            .Select(v => new CoverageSourceVertex(0, slice, v, -1, slice, v))).ToArray();
        var triangles = source.Triangles.Select((t, i) =>
        {
            int slice = i < 4 ? 0 : 1, offset = slice * count;
            return new CoverageSourceTriangle(0, slice, slice == 0 ? i : i - 4, -1, slice, t.A + offset, t.B + offset, t.C + offset);
        }).ToArray();
        var split = new CoverageSourceTopology("synthetic-split-plate", CoverageFixture.Hash, positions, vertices, triangles);
        var support = CoverageSupportSurface.Compile(split, [new(0, 0, 0, 4)], Matrix4x4.Identity);
        var handles = new MeshHandle[2];
        for (int slice = 0; slice < 2; slice++)
        {
            int material = slice;
            var indices = triangles.Where(t => t.MaterialSlice == material).SelectMany(t =>
                new[] { (uint)(t.A - material * count), (uint)(t.B - material * count), (uint)(t.C - material * count) }).ToArray();
            handles[slice] = h.Scene.LoadMesh(new GltfMesh(source.Positions.ToArray().Select(p =>
                new ModelVertex(p, Vector3.UnitZ, Vector4.One)).ToArray(), indices));
        }
        h.Conformer.ReserveSupports([support]);
        h.Begin();
        var frozen = new RigidInstanceDraw[2];
        Assert.True(h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, support,
            [new(handles[0], Matrix4x4.Identity), new(handles[1], Matrix4x4.Identity)], frozen, out var failure), failure.ToString());
        var commands = h.CommandPoses;
        commands[0] = commands[0] with { Support = support };
        Assert.True(h.Conformer.TryPrepare(h.Palette, Matrix4x4.Identity, commands, MotionKey.From(10),
            CoverageFallback.UncoveredCurrentPose, out var frame, out failure), failure.ToString());
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.Scene.Draw(frozen[0]);
        if (!omitSecond) h.Scene.Draw(frozen[1]);
        h.Render();
        Assert.Equal(omitSecond ? CoveragePresentation.UncoveredFallback : CoveragePresentation.Dispatched,
            h.Scene.GetCoverageOutcome(frame).Presentation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Temporal_previous_positions_are_reserved_before_recording(bool activatesThisFrame)
    {
        using var h = new CoverageSceneFixture();
        h.Scene.ForceTemporalForTests = !activatesThisFrame;
        for (int i = 0; i < 2; i++)
        {
            h.Begin();
            h.Scene.DrawSkinned(h.UncoveredDraw(), h.Palette);
            h.Render();
        }
        h.Scene.ForceTemporalForTests = true;
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.Scene.DrawSkinned(h.UncoveredDraw(), h.Palette);
        h.SubmitEquipment();
        h.Scene.PrepareFrame();
        var reserved = h.Scene.CoverageGpuBatchForTests!;
        var submittedCurrent = reserved.Current;
        var buffer = Assert.IsType<KhaozEngine.Tests.Gpu.FakeBuffer>(reserved.Previous);
        uint bytes = (uint)(16 + (h.Conformer.Topology.SourceVertexCount + h.Conformer.Topology.VertexCount) * 16) * 4;
        Assert.Equal(bytes, buffer.SizeInBytes);
        Assert.NotSame(reserved.Current, buffer);
        int createdBeforeRecording = h.Factory.Buffers.Count;
        Assert.InRange(h.Factory.Buffers.IndexOf(buffer), 0, createdBeforeRecording - 1);
        h.Render();
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Same(buffer, h.Scene.CoverageGpuBatchForTests!.Current);
        Assert.Same(submittedCurrent, h.Scene.CoverageGpuBatchForTests.Previous);
        Assert.False(buffer.Disposed);
        h.AssertCoverageDispatch(frame);
    }

    [Fact]
    public void Temporal_reserve_grows_by_retirement_and_disposes_with_the_scene()
    {
        var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        h.Render();
        var first = Assert.IsType<KhaozEngine.Tests.Gpu.FakeBuffer>(h.Scene.CoverageGpuBatchForTests!.Previous);
        using var second = h.Scene.CreateHostConformer(h.HostMesh.Skeleton!, h.Host);
        second.ReserveSupports([h.Support]);
        h.Begin();
        frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        h.Scene.PrepareFrame();
        var grown = Assert.IsType<KhaozEngine.Tests.Gpu.FakeBuffer>(h.Scene.CoverageGpuBatchForTests!.Previous);
        Assert.NotSame(first, grown);
        Assert.Equal(first.SizeInBytes * 2, grown.SizeInBytes);
        Assert.False(first.Disposed);
        h.Dispose();
        Assert.True(first.Disposed);
        Assert.True(grown.Disposed);
    }

    [Fact]
    public void Host_beyond_bone_capacity_refuses_before_gpu_allocation()
    {
        using var h = new CoverageSceneFixture();
        int buffers = h.Factory.Buffers.Count;
        var error = Assert.Throws<System.IO.InvalidDataException>(() => h.LoadHost(SkinningMath.MaxBonesPerDraw + 1, out _));
        Assert.Contains("unsupported skin", error.Message);
        Assert.Equal(buffers, h.Factory.Buffers.Count);
    }
}
