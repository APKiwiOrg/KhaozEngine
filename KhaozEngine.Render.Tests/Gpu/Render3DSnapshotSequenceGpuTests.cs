using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Render3D.Render3DSnapshot;

namespace KhaozEngine.Tests.Gpu;

public sealed class Render3DSnapshotSequenceGpuTests
{
    const int Width = 80;
    const int Height = 60;

    [GpuFact]
    public void Sequence_reuses_one_scene_and_reads_each_requested_frame_into_owned_pixels()
    {
        int setupCalls = 0;
        Scene3D? shared = null;
        MeshHandle marker = default;
        var drawn = new List<int>();
        var delivered = new List<int>();
        var captures = new List<Render3DCapture>();
        byte[]? firstCopy = null;

        CaptureSequence(Width, Height, scene =>
        {
            setupCalls++;
            shared = scene;
            Configure(scene);
            marker = scene.LoadMesh(MeshPrimitives.Box(0.1f));
            scene.DrawOverlayMesh(marker, Matrix4x4.Identity);
        }, (scene, frame) =>
        {
            Assert.Same(shared, scene);
            Assert.Equal(0, scene.OverlayMeshDrawCount);
            scene.DrawOverlayMesh(marker, Matrix4x4.Identity);
            drawn.Add(frame);
            scene.Post.BackgroundColor = frame switch
            {
                2 => new Color(1f, 0f, 0f, 1f),
                3 => new Color(0f, 1f, 0f, 1f),
                _ => new Color(0f, 0f, 1f, 1f),
            };
        }, frames: 5, onFrame: (frame, capture) =>
        {
            Assert.Equal(frame, drawn[^1]);
            delivered.Add(frame);
            captures.Add(capture);
            firstCopy ??= (byte[])capture.Rgba.Clone();
        }, warmupFrames: 2);

        Assert.Equal(1, setupCalls);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, drawn);
        Assert.Equal(new[] { 2, 3, 4 }, delivered);
        for (int i = 0; i < 3; i++)
        {
            Render3DCapture capture = captures[i];
            Assert.Equal(Width, capture.Width);
            Assert.Equal(Height, capture.Height);
            Assert.Equal(Width * Height * 4, capture.Rgba.Length);
            Assert.False(GpuBackendSelector.IsRetired(capture.Backend));
            Assert.Equal(captures[0].Backend, capture.Backend);
            Assert.True(capture.Rgba[i] > 240, $"frame {i + 2} did not read its own background");
            Assert.True(capture.Rgba[(i + 1) % 3] < 10);
        }
        Assert.NotSame(captures[0].Rgba, captures[1].Rgba);
        Assert.Equal(firstCopy, captures[0].Rgba);
    }

    [GpuFact]
    public void Warmup_history_survives_readbacks_and_final_pixels_match_a_continuous_legacy_capture()
    {
        Scene3D? shared = null;
        MeshHandle box = default;
        var diagnostics = new List<TemporalDiagnostics>();
        Render3DCapture last = default;
        CaptureSequence(Width, Height, scene =>
        {
            shared = scene;
            SetupTemporal(scene, out box);
        }, (scene, _) => scene.Draw(box, Matrix4x4.Identity), frames: 5,
            onFrame: (_, capture) =>
            {
                diagnostics.Add(shared!.LastTemporalDiagnostics);
                last = capture;
            }, warmupFrames: 2);

        Assert.Equal(3, diagnostics.Count);
        Assert.All(diagnostics, d => Assert.True(d.HistoryValid));
        Assert.Equal(diagnostics[0].FrameIndex + 1, diagnostics[1].FrameIndex);
        Assert.Equal(diagnostics[1].FrameIndex + 1, diagnostics[2].FrameIndex);
        MeshHandle legacyBox = default;
        Render3DCapture legacy = Render3DSnapshot.CaptureWithBackend(Width, Height,
            scene => SetupTemporal(scene, out legacyBox), scene => scene.Draw(legacyBox, Matrix4x4.Identity), frames: 5);
        Assert.Equal(legacy.Rgba, last.Rgba);
    }

    [GpuFact]
    public void A_frame_consumer_exception_stops_the_sequence_and_allows_another_capture()
    {
        var failure = new InvalidOperationException("consumer stopped");
        int draws = 0;
        int reads = 0;
        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
            CaptureSequence(Width, Height, Configure, (_, _) => draws++, frames: 4,
                onFrame: (_, _) => { reads++; throw failure; }, warmupFrames: 1));
        Assert.Same(failure, actual);
        Assert.Equal(2, draws);
        Assert.Equal(1, reads);
        Assert.Equal(Width * Height * 4, Render3DSnapshot.Capture(Width, Height, Configure, _ => { }).Length);
    }

    [GpuFact]
    public void All_warmup_frames_render_without_delivering_pixels()
    {
        int draws = 0;
        CaptureSequence(Width, Height, Configure, (_, _) => draws++, frames: 2,
            onFrame: (_, _) => Assert.Fail("warm-up must not read back"), warmupFrames: 2);
        Assert.Equal(2, draws);
    }

    static void Configure(Scene3D scene)
    {
        scene.Post.Starfield = false;
        scene.Post.RenderWidth = Width;
        scene.Post.RenderHeight = Height;
    }

    static void SetupTemporal(Scene3D scene, out MeshHandle box)
    {
        Configure(scene);
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        scene.Post.Temporal.Upscale = TemporalUpscale.Native;
        scene.Camera.Frame(Vector3.Zero, new Vector3(3f));
        box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
    }
}
