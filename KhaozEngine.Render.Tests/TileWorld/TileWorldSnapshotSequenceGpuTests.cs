using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldSnapshotSequenceGpuTests
{
    const int Width = 100;
    const int Height = 75;
    static readonly Vector3 Eye = new(30f, 18f, 6f);
    static readonly Vector3 Target = new(12f, 0f, -11f);

    [GpuFact]
    public void Moving_camera_reuses_the_scene_and_draws_a_region_outside_the_initial_ring()
    {
        TileWorldDocument doc = TileRenderTestData.GreyboxWorld();
        var far = new RegionCoord(8, 0);
        doc.GetOrCreateRegion(far);
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetUnderlay(far.OriginX + x, z, 0, TileRenderTestData.Dirt);
        Vector3 offset = new(far.OriginX * doc.TileSize, 0f, 0f);
        Scene3D? shared = null;
        int configured = 0;
        var cameraCalls = new List<int>();
        var delivered = new List<int>();
        var captures = new List<Render3DCapture>();
        TileWorldSnapshot.CapturePerspectiveSequence(doc, TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight),
            Eye, Target, Width, Height, frames: 3, onFrame: (frame, capture) =>
            {
                delivered.Add(frame);
                captures.Add(capture);
            }, configureScene: scene =>
            {
                configured++;
                shared = scene;
                Configure(scene);
            }, drawFrame: (scene, frame) =>
            {
                Assert.Same(shared, scene);
                FlyCamera3D camera = Assert.IsType<FlyCamera3D>(scene.CameraOverride);
                Assert.Equal(frame == 2 ? Eye + offset : Eye, camera.Position);
            }, cameraFrame: frame =>
            {
                cameraCalls.Add(frame);
                return frame == 2 ? (Eye + offset, Target + offset) : (Eye, Target);
            }, warmupFrames: 1);

        Assert.Equal(1, configured);
        Assert.Equal(new[] { 0, 1, 2 }, cameraCalls);
        Assert.Equal(new[] { 1, 2 }, delivered);
        byte[] first = Single(doc, Eye, Target);
        byte[] second = Single(doc, Eye + offset, Target + offset);
        Assert.Equal(first, captures[0].Rgba);
        Assert.Equal(second, captures[1].Rgba);
        Assert.False(first.AsSpan().SequenceEqual(second));
    }

    [GpuFact]
    public void A_coincident_per_frame_camera_stops_before_rendering_that_frame()
    {
        TileWorldDocument doc = TileRenderTestData.GreyboxWorld();
        int delivered = 0;
        ArgumentException error = Assert.Throws<ArgumentException>(() => TileWorldSnapshot.CapturePerspectiveSequence(
            doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight),
            Eye, Target, Width, Height, frames: 2, onFrame: (_, _) => delivered++, configureScene: Configure,
            cameraFrame: frame => frame == 0 ? (Eye, Target) : (Target, Target)));
        Assert.Equal("cameraFrame", error.ParamName);
        Assert.Equal(1, delivered);
    }

    static byte[] Single(TileWorldDocument doc, Vector3 eye, Vector3 target) =>
        TileWorldSnapshot.CapturePerspective(doc, TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight), eye, target, Width, Height,
            configureScene: Configure);

    static void Configure(Scene3D scene)
    {
        // A camera moved after Begin and a fresh camera can latch different automatic render origins.
        // Compare the two capture paths in the same coordinate frame so floating-point rebasing cannot drift.
        scene.RenderOrigin = Vector3.Zero;
        scene.Post.Starfield = false;
        scene.Post.RenderWidth = Width;
        scene.Post.RenderHeight = Height;
    }
}
