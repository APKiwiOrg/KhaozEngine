using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageTemporalReservationTests
{
    [Fact]
    public void Late_debug_view_uses_the_buffer_reserved_before_recording() => AssertLateActivation(debugView: true);

    [Fact]
    public void Late_temporal_aa_uses_the_buffer_reserved_before_recording() => AssertLateActivation(debugView: false);

    static void AssertLateActivation(bool debugView)
    {
        using var h = new CoverageSceneFixture();
        h.Scene.Post.Pixelated = false;
        h.Begin();
        var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
        h.SubmitEquipment();
        Assert.False(h.Scene.TemporalActive);
        Assert.Null(h.Scene.MotionResourcesForTests);
        h.Scene.PrepareFrame();
        int vertices = h.Conformer.Topology.VertexCount;
        var reserved = h.Scene.CoverageMotionReserveForTests;
        Assert.True(reserved.List >= vertices);
        Assert.Equal((uint)vertices, reserved.Gpu);
        var buffer = Assert.IsType<FakeBuffer>(reserved.Buffer);
        Assert.Equal((uint)vertices * 12, buffer.SizeInBytes);
        Assert.Contains(buffer, h.Factory.Buffers);
        Assert.False(buffer.Disposed);
        Assert.Null(h.Scene.MotionResourcesForTests);
        Assert.False(h.Scene.TemporalActive);

        if (debugView) h.Scene.DebugView = SceneDebugView.MotionVectors;
        else h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        Assert.True(h.Scene.TemporalActive);
        Assert.Null(h.Scene.MotionResourcesForTests);
        h.Scene.PrepareFrame();
        Assert.Same(buffer, h.Scene.CoverageMotionReserveForTests.Buffer);
        h.Render();

        Assert.True(h.Scene.TemporalActive);
        Assert.NotNull(h.Scene.MotionResourcesForTests);
        if (debugView) Assert.True(h.Scene.MotionVectorsViewBuiltForTests);
        else Assert.NotNull(h.Scene.TemporalResolveRendererForTests);
        Assert.Equal(CoveragePresentation.Covered, h.Scene.GetCoverageOutcome(frame).Presentation);
        var after = h.Scene.CoverageMotionReserveForTests;
        Assert.Equal(reserved.List, after.List);
        Assert.Equal(reserved.Gpu, after.Gpu);
        Assert.Same(buffer, after.Buffer);
        Assert.False(buffer.Disposed);
        var upload = Assert.Single(h.Record.Uploads, u => ReferenceEquals(u.Buffer, buffer));
        Assert.Equal(h.Expected.Select(v => v.Position).ToArray(), MemoryMarshal.Cast<byte, Vector3>(upload.Data).ToArray());
        Assert.Contains(h.Record.Bindings, b => b.VertexBuffers.TryGetValue(2, out var bound) && ReferenceEquals(bound, buffer));
    }

    [Fact]
    public void Non_temporal_coverage_frames_reuse_the_inactive_reserve()
    {
        var h = new CoverageSceneFixture();
        FakeBuffer? buffer = null;
        try
        {
            h.Begin();
            h.Render();
            Assert.Null(h.Scene.CoverageMotionReserveForTests.Buffer);
            Assert.Null(h.Scene.MotionResourcesForTests);

            h.Begin();
            var frame = h.Stage();
            h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
            h.SubmitEquipment();
            h.Scene.PrepareFrame();
            var reserved = h.Scene.CoverageMotionReserveForTests;
            buffer = Assert.IsType<FakeBuffer>(reserved.Buffer);
            h.Render();
            Assert.Same(buffer, h.Scene.CoverageMotionReserveForTests.Buffer);
            Assert.Null(h.Scene.MotionResourcesForTests);
            Assert.DoesNotContain(h.Record.Uploads, u => ReferenceEquals(u.Buffer, buffer));
            Assert.DoesNotContain(h.Record.Bindings, b => b.VertexBuffers.TryGetValue(2, out var bound) && ReferenceEquals(bound, buffer));

            h.Begin();
            frame = h.Stage();
            h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette);
            h.SubmitEquipment();
            int buffers = h.Factory.Buffers.Count;
            h.Scene.PrepareFrame();
            var reused = h.Scene.CoverageMotionReserveForTests;
            Assert.Equal(buffers, h.Factory.Buffers.Count);
            Assert.Equal(reserved.List, reused.List);
            Assert.Equal(reserved.Gpu, reused.Gpu);
            Assert.Same(buffer, reused.Buffer);
            h.Render();
            Assert.Same(buffer, h.Scene.CoverageMotionReserveForTests.Buffer);
            Assert.False(buffer.Disposed);
            Assert.Null(h.Scene.MotionResourcesForTests);
            Assert.DoesNotContain(h.Record.Uploads, u => ReferenceEquals(u.Buffer, buffer));
            Assert.DoesNotContain(h.Record.Bindings, b => b.VertexBuffers.TryGetValue(2, out var bound) && ReferenceEquals(bound, buffer));
        }
        finally { h.Dispose(); }
        Assert.True(buffer!.Disposed);
    }
}
