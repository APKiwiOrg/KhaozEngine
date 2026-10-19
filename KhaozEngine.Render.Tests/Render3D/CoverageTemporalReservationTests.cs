using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageTemporalReservationTests
{
    [Fact]
    public void Late_debug_view_uses_the_buffer_reserved_before_recording() => AssertLateActivation(true);

    [Fact]
    public void Late_temporal_aa_uses_the_buffer_reserved_before_recording() => AssertLateActivation(false);

    static void AssertLateActivation(bool debugView)
    {
        using var h = new CoverageSceneFixture();
        h.Scene.Post.Pixelated = false;
        h.Begin(); var frame = h.Stage();
        h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette); h.SubmitEquipment();
        Assert.False(h.Scene.TemporalActive);
        Assert.Null(h.Scene.MotionResourcesForTests);
        h.Scene.PrepareFrame(32, 24);
        var batch = h.Scene.CoverageGpuBatchForTests!;
        var current = Assert.IsType<FakeBuffer>(batch.Current);
        var previous = Assert.IsType<FakeBuffer>(batch.Previous);
        Assert.NotSame(current, previous);
        Assert.Equal(current.SizeInBytes, previous.SizeInBytes);
        Assert.True(current.SizeInBytes >= 64 + h.Conformer.Topology.VertexCount * 64);
        Assert.Null(h.Scene.CoverageMotionReserveForTests.Buffer);
        Assert.Null(h.Scene.MotionResourcesForTests);
        if (debugView) h.Scene.DebugView = SceneDebugView.MotionVectors;
        else h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        h.Scene.PrepareFrame(32, 24);
        Assert.Same(current, batch.Current); Assert.Same(previous, batch.Previous);
        Assert.Null(h.Scene.MotionResourcesForTests);
        bool boundMotion = false;
        h.BeforeNonIndexedDraw = draw => boundMotion |= draw.Pipeline is FakePipeline { Request: { } request }
            && request.VertexGlsl == ShaderSources.CoverageModelMotionVert && draw.Sets.Values.Any(bound =>
                bound.Set is FakeResourceSet set && ReferenceEquals(set.Resources[0], current)
                && ReferenceEquals(set.Resources[^1], previous));
        h.Render();
        Assert.True(boundMotion);
        Assert.NotNull(h.Scene.MotionResourcesForTests);
        if (debugView) Assert.True(h.Scene.MotionVectorsViewBuiltForTests);
        else Assert.NotNull(h.Scene.TemporalResolveRendererForTests);
        Assert.Equal(CoveragePresentation.Dispatched, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Same(current, batch.Previous); Assert.Same(previous, batch.Current);
        Assert.False(current.Disposed); Assert.False(previous.Disposed);
        Assert.Null(h.Scene.CoverageMotionReserveForTests.Buffer);
        Assert.DoesNotContain(h.Record.Uploads, upload => ReferenceEquals(upload.Buffer, current) || ReferenceEquals(upload.Buffer, previous));

    }

    [Fact]
    public void Non_temporal_coverage_frames_reuse_the_inactive_reserve()
    {
        var h = new CoverageSceneFixture();
        FakeBuffer? first = null, second = null;
        try
        {
            h.Begin(); h.Render();
            Assert.Null(h.Scene.CoverageMotionReserveForTests.Buffer);
            Assert.Null(h.Scene.MotionResourcesForTests);
            for (int frameIndex = 0; frameIndex < 2; frameIndex++)
            {
                h.Begin(); var frame = h.Stage();
                h.Scene.DrawSkinned(h.HostDraw(frame), h.Palette); h.SubmitEquipment();
                h.Scene.PrepareFrame(32, 24);
                var batch = h.Scene.CoverageGpuBatchForTests!;
                first ??= Assert.IsType<FakeBuffer>(batch.Current);
                second ??= Assert.IsType<FakeBuffer>(batch.Previous);
                Assert.True(ReferenceEquals(batch.Current, first) || ReferenceEquals(batch.Current, second));
                Assert.True(ReferenceEquals(batch.Previous, first) || ReferenceEquals(batch.Previous, second));
                Assert.NotSame(batch.Current, batch.Previous);
                h.Render();
                Assert.Null(h.Scene.MotionResourcesForTests);
                Assert.Null(h.Scene.CoverageMotionReserveForTests.Buffer);
                Assert.False(first.Disposed); Assert.False(second.Disposed);
                Assert.DoesNotContain(h.Record.Uploads, upload => ReferenceEquals(upload.Buffer, first) || ReferenceEquals(upload.Buffer, second));
            }
        }
        finally { h.Dispose(); }
        Assert.True(first!.Disposed); Assert.True(second!.Disposed);
    }
}
