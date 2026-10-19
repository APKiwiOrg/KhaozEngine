using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Diagnostics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[CollectionDefinition("CoverageCapabilities", DisableParallelization = true)]
public sealed class CoverageCapabilitiesCollection;

[Collection("CoverageCapabilities")]
public sealed class CoverageGpuStagingTests
{
    [Fact]
    public void Begin_latches_origin_before_opening_the_arena()
    {
        using var h = new CoverageSceneFixture();
        var observedBeginOrder = new List<string>();
        h.Scene.CameraOverride = new BeginCamera(h.Scene.Camera, () =>
        {
            observedBeginOrder.Add("LatchOrigin");
            Assert.False(h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
                [new(h.RigidHandle, Matrix4x4.Identity)], new RigidInstanceDraw[1], out var failure));
            Assert.Equal(CoverageFailureCode.StaleFrame, failure.Code);
        });
        h.Begin();
        _ = h.Stage();
        observedBeginOrder.Add("OpenCoverageArena");
        Assert.Equal(new[] { "LatchOrigin", "OpenCoverageArena" }, observedBeginOrder);
    }

    [Fact]
    public void Accepted_staging_copies_inputs_without_constructing_vertices()
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        var staged = h.Scene.GetCoverageOutcome(frame);
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        var frozenPalette = (Matrix4x4[])h.Palette.Clone();
        h.Palette[0] = Matrix4x4.CreateScale(99);
        h.Render();
        Assert.Equal(CoveragePresentation.Pending, staged.Presentation);
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Equal(1, h.Scene.DispatchedHostPreparationCount);
        Assert.Equal(frozenPalette, frame.Preparation!.Palette);
        var batch = h.Scene.CoverageGpuBatchForTests!;
        var uploaded = Assert.Single(h.Record.Uploads, u => ReferenceEquals(u.Buffer, batch.FrameData));
        var paletteWords = MemoryMarshal.Cast<byte, uint>(uploaded.Data).Slice((int)frame.Preparation.GpuReservation.FrameBase + 16, 32).ToArray();
        var frozenWords = MemoryMarshal.Cast<Matrix4x4, uint>(frozenPalette).ToArray();
        Assert.Equal(frozenWords, paletteWords);
        h.AssertCoverageDispatch(frame);
    }

    public static IEnumerable<object[]> EquipmentAttributeInputs()
    {
        foreach (string lane in new[] { "normal", "color", "uv", "tangent" })
            foreach (var (value, accepted) in new[] { (float.NaN, false), (float.PositiveInfinity, false),
                (float.NegativeInfinity, false), (0f, true), (float.MaxValue, true), (-float.MaxValue, true) })
                yield return new object[] { lane, value, accepted };
    }

    [Theory]
    [MemberData(nameof(EquipmentAttributeInputs))]
    public void Equipment_staging_preserves_cold_finite_lane_eligibility(string lane, float value, bool accepted)
    {
        using var h = new CoverageSceneFixture();
        var vertices = h.RigidMesh.Vertices.ToArray();
        SetLane(value);
        var mesh = new GltfMesh(vertices, h.RigidMesh.Indices32.ToArray());
        Assert.Equal(h.RigidMesh.Vertices.Select(v => v.Position), mesh.Vertices.Select(v => v.Position));
        Assert.Equal(h.RigidMesh.Indices32, mesh.Indices32);
        var handle = h.Scene.LoadMesh(mesh);
        // Caller array edits after upload cannot change the eligibility of the bytes already uploaded.
        SetLane(accepted ? float.NaN : 0);
        var sentinel = new RigidInstanceDraw(h.RigidHandle, Matrix4x4.CreateTranslation(77, 0, 0));
        var frozen = new[] { sentinel };
        h.Begin();
        bool staged = h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
            [new(handle, Matrix4x4.Identity)], frozen, out var failure);
        Assert.Equal(accepted, staged);
        Assert.Empty(h.Scene.QueuedInstancesForTests);
        if (accepted)
        {
            Assert.Equal(default, failure);
            Assert.NotNull(frozen[0].Coverage.Stage);
            Assert.Equal(handle, frozen[0].Mesh);
        }
        else
        {
            Assert.Equal(new CoverageFailure(CoverageFailureCode.IdentityMismatch, null, 1, 1, 0, 0), failure);
            Assert.Equal(sentinel, frozen[0]);
            Assert.Null(frozen[0].Coverage.Stage);
            // A failed admission did not claim this actor, instance or command in the staging registry.
            Assert.True(h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
                [new(h.RigidHandle, Matrix4x4.Identity)], frozen, out failure), failure.ToString());
            Assert.NotNull(frozen[0].Coverage.Stage);
        }

        void SetLane(float input)
        {
            switch (lane)
            {
                case "normal": vertices[^1].Normal.X = input; break;
                case "color": vertices[^1].Color.X = input; break;
                case "uv": vertices[^1].Uv.X = input; break;
                case "tangent": vertices[^1].Tangent.X = input; break;
            }
        }
    }

    [Fact]
    public void Equipment_finite_lane_admission_keeps_other_attribute_components_eligible()
    {
        using var h = new CoverageSceneFixture();
        var vertices = h.RigidMesh.Vertices.ToArray();
        vertices[^1].Normal.Y = vertices[^1].Color.Y = vertices[^1].Uv.Y = vertices[^1].Tangent.Y = float.NaN;
        var handle = h.Scene.LoadMesh(new GltfMesh(vertices, h.RigidMesh.Indices32.ToArray()));
        var frozen = new RigidInstanceDraw[1];
        h.Begin();
        Assert.True(h.Scene.TryStageCoverageEquipment(MotionKey.From(10), 1, 1, h.Support,
            [new(handle, Matrix4x4.Identity)], frozen, out var failure), failure.ToString());
        Assert.NotNull(frozen[0].Coverage.Stage);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Capability_failure_is_reported_once_and_uses_vertex_skinning(bool compute, bool failBuild)
    {
        var sink = new InMemorySink();
        using var manager = new LogManager(new LoggerOptions { Synchronous = true, Sinks = { sink } });
        using var logging = Log.Use(manager);
        using var h = new CoverageSceneFixture(supportsCompute: compute, failCoverageBuild: failBuild);
        h.Scene.UseGpuSkinning = false;
        for (int frameNumber = 0; frameNumber < 2; frameNumber++)
        {
            h.Begin();
            var frame = h.Stage();
            h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
            h.Outline(h.HostDraw(frame));
            h.SubmitEquipment();
            h.Render();
            var result = h.Scene.GetCoverageOutcome(frame);
            Assert.Equal(CoveragePresentation.UncoveredFallback, result.Presentation);
            Assert.Equal(CoverageFailureCode.UnsupportedAdapter, result.Failure.Code);
            Assert.Equal(0, h.Scene.DispatchedHostPreparationCount);
            Assert.Null(h.Scene.CoverageGpuBatchForTests);
            Assert.Equal(0, h.Scene.OrdinaryCpuSkinnedVerticesForTests);
            h.AssertFallbackDraws(frame);
            h.AssertLegacyOutline(h.Scene.CoverageFallbackSliceForTests(frame, 0));
        }
        h.Begin();
        h.Scene.DrawSkinned(new SkinnedInstanceDraw(h.Host[0], Matrix4x4.Identity), h.Palette);
        h.Render();
        Assert.Equal(0, h.Scene.OrdinaryCpuSkinnedVerticesForTests);
        Assert.Equal(1, h.Scene.UncoveredGpuDrawsForTests);
        Assert.Single(sink.Entries, e => e.Message.Contains("coverage capability", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(failBuild ? 1 : 0, h.Capture.CoverageBuildAttempts);
        if (failBuild) Assert.True(h.Capture.CoverageShadersDisposed);
    }

    [Fact]
    public void Shared_uploads_and_actor_arenas_are_reserved_before_recording()
    {
        using var h = new CoverageSceneFixture();
        using var second = h.Scene.CreateHostConformer(h.HostMesh.Skeleton!, h.Host);
        second.ReserveSupports([h.Support]);
        Assert.Same(h.Conformer.Gpu.Host, second.Gpu.Host);
        Assert.Same(h.Conformer.Gpu.Host.Upload, second.Gpu.Host.Upload);
        Assert.Equal(h.Conformer.Gpu.Reservation.StaticBase, second.Gpu.Reservation.StaticBase);
        Assert.Equal(h.Conformer.Gpu.SupportOffsets, second.Gpu.SupportOffsets);
        Assert.NotEqual(h.Conformer.Gpu.Reservation.WorkBase, second.Gpu.Reservation.WorkBase);
        Assert.NotEqual(h.Conformer.Gpu.Reservation.CurrentBase, second.Gpu.Reservation.CurrentBase);
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        h.Render();
        h.Begin();
        frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        h.Scene.PrepareFrame(32, 24);
        int buffers = h.Factory.Buffers.Count, sets = h.Factory.ResourceSets.Count;
        h.Render();
        Assert.Equal(buffers, h.Factory.Buffers.Count);
        Assert.Equal(sets, h.Factory.ResourceSets.Count);
        Assert.Equal(1, h.Capture.CoverageBuildAttempts);
    }

    [Fact]
    public void Failed_support_acquisition_preserves_equipment_identity_and_live_buffers()
    {
        using var f = new CoverageAttachmentFixture();
        var previous = f.Load();
        var replacement = f.Load("replacement");
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([previous], out _));
        var batch = f.Scene.CoverageGpuBatchForTests!;
        int buffers = f.Factory.Buffers.Count;
        f.Factory.FailBufferCreationAfter(2);
        Assert.False(binding.TrySetEquipment([replacement], out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, failure.Code);
        Assert.Same(batch, f.Scene.CoverageGpuBatchForTests);
        Assert.False(((KhaozEngine.Tests.Gpu.FakeBuffer)batch.Current).Disposed);
        Assert.All(f.Factory.Buffers.Skip(buffers), buffer => Assert.True(buffer.Disposed));
        f.Factory.ThrowOnBufferCreate = 0;
        f.Scene.Begin();
        Assert.True(binding.TryPrepare(f.Skeleton, f.Palette, Matrix4x4.Identity, f.Template, out var frame, out failure), failure.ToString());
        Assert.Equal(previous.InstanceId, Assert.Single(f.Adapter.Commands).EquipmentInstanceId);
        Assert.Same(previous.Coverage.Supports[0], Assert.Single(f.Adapter.Commands).Support);
        f.Submit(frame, binding.PreparedEquipmentDraws.ToArray());
        f.Render();
        Assert.Equal(CoveragePresentation.Dispatched, f.Scene.GetCoverageOutcome(frame).Presentation);
    }

    [Fact]
    public void Palette_escape_uses_one_outward_world_envelope_for_the_view()
    {
        using var h = new CoverageSceneFixture();
        h.Palette[0] = h.Palette[1] = Matrix4x4.CreateTranslation(100, 0, 0);
        h.Begin();
        var frame = h.Stage(Matrix4x4.CreateTranslation(2, 3, 4));
        var bound = frame.Preparation!.WorldBounds;
        Assert.True(bound.Min.X > 90);
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        h.Render();
        var sphere = frame.Preparation.Sphere;
        for (int corner = 0; corner < 8; corner++)
        {
            double x = ((corner & 1) == 0 ? bound.Min.X : bound.Max.X) - (double)sphere.Center.X;
            double y = ((corner & 2) == 0 ? bound.Min.Y : bound.Max.Y) - (double)sphere.Center.Y;
            double z = ((corner & 4) == 0 ? bound.Min.Z : bound.Max.Z) - (double)sphere.Center.Z;
            Assert.True(sphere.Radius >= Math.Sqrt(x * x + y * y + z * z));
        }
        Assert.Equal(1, h.Scene.CoverageSphereEvaluationsForTests);
    }

    sealed class BeginCamera(IIsoCamera3D inner, Action latch) : IIsoCamera3D, IRenderOriginAware
    {
        public Matrix4x4 View => inner.View;
        public Matrix4x4 Projection => inner.Projection;
        public Matrix4x4 ViewProjection => inner.ViewProjection;
        public Matrix4x4 AbsoluteViewProjection => inner.ViewProjection;
        public Vector3 Eye => inner.Eye;
        public Vector3 Forward => inner.Forward;
        public Vector3 RenderOrigin { get; set; }
        public void BeginFrame() => latch();
        public bool WorldToScreen(Vector3 world, int width, int height, out Vector2 pixel) =>
            inner.WorldToScreen(world, width, height, out pixel);
    }
}
