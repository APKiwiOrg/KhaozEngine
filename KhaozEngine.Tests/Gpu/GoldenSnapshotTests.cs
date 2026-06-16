using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Gated GPU image-regression net. Renders a FIXED asymmetric 3D scene and a FIXED 2D scene to CPU RGBA via
    /// the headless snapshot helpers, downsamples to a coarse grid, and compares to committed reference grids
    /// with a per-channel tolerance. Catches shader/UBO/blend/winding/orientation regressions that a headless
    /// geometry test cannot. Skipped unless KE_GPU_TESTS=1 (needs a Metal device).
    /// </summary>
    public sealed class GoldenSnapshotTests
    {
        const int W = 480, H = 320;

        [GpuFact]
        public void Golden3D_FixedAsymmetricScene()
        {
            MeshHandle floor = default, sphere = default, box = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(6f, 0.1f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.6f));
                    box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                    // Fixed framing of an asymmetric region so an orientation flip moves content visibly.
                    scene.Camera.Frame(new Vector3(0.4f, 0.4f, -0.2f), new Vector3(5f, 3f, 5f));
                },
                drawFrame: scene =>
                {
                    // Tile floor under everything.
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                    // Red shiny sphere off to one side and raised.
                    scene.Draw(sphere,
                        Matrix4x4.CreateTranslation(-1.4f, 0.6f, 0.9f),
                        new Vector4(0.85f, 0.12f, 0.12f, 1f),
                        Material.Shiny(0.8f));
                    // Green matte box on the other side, distinct position.
                    scene.Draw(box,
                        Matrix4x4.CreateTranslation(1.3f, 0.45f, -1.1f),
                        new Vector4(0.15f, 0.75f, 0.2f, 1f));
                    // Debug ring on the ground, off-centre so it breaks symmetry.
                    scene.DebugCircle(new Vector3(0.2f, 0.02f, 1.6f), Vector3.UnitY, 1.1f,
                        new Vector4(0.9f, 0.85f, 0.2f, 1f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d", rgba, W, H);
        }

        [GpuFact]
        public void Golden2D_FixedScene()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Vector4(0.07f, 0.08f, 0.11f, 1f), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                SpriteFont font = ctx.LoadFont("/System/Library/Fonts/Supplemental/Arial.ttf", 48f);
                ctx.Batch.Begin();
                ctx.Batch.Draw(white, new Vector4(40, 40, 180, 90), new Vector4(0.85f, 0.2f, 0.2f, 1f));
                ctx.Batch.Draw(white, new Vector4(260, 150, 150, 120), new Vector4(0.2f, 0.7f, 0.3f, 1f));
                ctx.Batch.Draw(white, new Vector4(120, 220, 110, 70), new Vector4(0.25f, 0.4f, 0.9f, 0.9f));
                ctx.Batch.DrawString(font, "KE", new Vector2(60, 200), new Vector4(0.95f, 0.95f, 0.4f, 1f));
                ctx.Batch.End();
            });

            GoldenCompare.AssertOrUpdate("scene2d", rgba, W, H);
        }
    }
}
