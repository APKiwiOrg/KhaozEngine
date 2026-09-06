using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>GPU coverage for the per-frame extension point on tile-world snapshots. Skipped unless
/// KE_GPU_TESTS is set.</summary>
public sealed class TileWorldSnapshotDrawFrameGpuTests
{
    const int Width = 160;
    const int Height = 120;

    static readonly Vector3 Eye = new(30f, 18f, 6f);
    static readonly Vector3 Target = new(12f, 0f, -11f);

    [GpuFact]
    public void Perspective_draw_frame_runs_after_begin_and_adds_a_mesh_to_every_frame()
    {
        TileWorldDocument doc = TileRenderTestData.GreyboxWorld();
        byte[] baseline = Capture(doc);
        MeshHandle box = default;
        int calls = 0;

        byte[] withBox = Capture(
            doc,
            configureScene: scene =>
            {
                ConfigureBackground(scene);
                box = scene.LoadMesh(MeshPrimitives.Box(4f));
                scene.DrawOverlayMesh(box, Matrix4x4.Identity);
            },
            drawFrame: scene =>
            {
                Assert.Equal(0, scene.OverlayMeshDrawCount);
                calls++;
                scene.Draw(box, Matrix4x4.CreateTranslation(Target + new Vector3(0f, 2f, 0f)),
                    new Color(1f, 0.15f, 0.08f, 1f));
            });

        Assert.Equal(TileWorldSnapshot.CaptureFrames, calls);
        Assert.False(baseline.AsSpan().SequenceEqual(withBox));
    }

    static byte[] Capture(
        TileWorldDocument doc,
        Action<Scene3D>? configureScene = null,
        Action<Scene3D>? drawFrame = null) =>
        TileWorldSnapshot.CapturePerspective(
            doc,
            TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight),
            Eye,
            Target,
            Width,
            Height,
            configureScene: configureScene ?? ConfigureBackground,
            drawFrame: drawFrame);

    static void ConfigureBackground(Scene3D scene)
    {
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.BackgroundColor = new Color(0.08f, 0.09f, 0.12f, 1f);
    }
}
