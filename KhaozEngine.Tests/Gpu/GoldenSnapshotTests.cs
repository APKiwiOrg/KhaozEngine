using System;
using System.Numerics;
using Xunit;
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

        // Bundled libre font (copied next to the test assembly), so the 2D golden's glyph input is identical on
        // macOS / Windows / Linux runners. A hard-coded OS system-font path would only exist on one platform.
        static readonly string FontPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

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
        public void Golden3D_FilledOverlay()
        {
            MeshHandle floor = default, box = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(6f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                    // Same fixed asymmetric framing as the line golden so a flip moves content visibly.
                    scene.Camera.Frame(new Vector3(0.4f, 0.4f, -0.2f), new Vector3(5f, 3f, 5f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                    // An opaque box the fill must NOT wash out where it doesn't overlap.
                    scene.Draw(box, Matrix4x4.CreateTranslation(1.3f, 0.45f, -1.1f),
                        new Vector4(0.15f, 0.75f, 0.2f, 1f));

                    // Translucent green ground tile: alpha < 1 so the floor reads THROUGH it (blend assertion).
                    scene.DebugFilledQuad(new Vector3(-1.0f, 0.045f, 0.6f), halfSize: 1.2f,
                        new Vector4(0.30f, 0.85f, 0.45f, 0.45f));
                    // Translucent magenta ground disc off to the other side.
                    scene.DebugFilledCircle(new Vector3(1.4f, 0.045f, 1.4f), Vector3.UnitY, 1.0f,
                        new Vector4(0.85f, 0.25f, 0.8f, 0.45f), segments: 40);
                    // A crisp outline ON TOP of the filled tile: locks draw order (fill under line).
                    scene.DebugCircle(new Vector3(-1.0f, 0.05f, 0.6f), Vector3.UnitY, 1.0f,
                        new Vector4(1f, 1f, 0.3f, 1f), segments: 40);
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_fill", rgba, W, H);
        }

        [GpuFact]
        public void Golden3D_TexturedMesh()
        {
            // Deterministic 64x64 checkerboard (8x8 cells) in two contrasting colours.
            const int TexN = 64, Cell = 8;
            var checker = new byte[TexN * TexN * 4];
            for (int y = 0; y < TexN; y++)
                for (int x = 0; x < TexN; x++)
                {
                    bool a = ((x / Cell) + (y / Cell)) % 2 == 0;
                    int i = (y * TexN + x) * 4;
                    checker[i + 0] = (byte)(a ? 235 : 30);
                    checker[i + 1] = (byte)(a ? 70 : 200);
                    checker[i + 2] = (byte)(a ? 40 : 220);
                    checker[i + 3] = 255;
                }

            MeshHandle plane = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    // Texture API: a valid handle textures the mesh; an invalid/default handle falls back to
                    // untextured without throwing. Both asserted inline (Scene3D needs a device, so this rides the
                    // gated golden rather than a separate headless test).
                    Scene3D.TextureHandle tex = scene.LoadTexture(checker, TexN, TexN);
                    Assert.True(tex.IsValid);
                    Assert.False(Scene3D.TextureHandle.Invalid.IsValid);

                    // Invalid handle into LoadMesh => untextured fallback, no throw.
                    MeshHandle fallback = scene.LoadMesh(MeshPrimitives.Box(0.2f), Scene3D.TextureHandle.Invalid);
                    Assert.NotEqual(default, fallback);

                    plane = scene.LoadMesh(MeshPrimitives.Plane(3f, 3f), tex);
                    // Fixed top-ish framing so the checker fills the view deterministically.
                    scene.Camera.Frame(new Vector3(0f, 0f, 0f), new Vector3(2.6f, 4.2f, 2.6f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(plane, Matrix4x4.Identity);
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_textured", rgba, W, H);
        }

        [GpuFact]
        public void Golden2D_FixedScene()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Vector4(0.07f, 0.08f, 0.11f, 1f), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                SpriteFont font = ctx.LoadFont(FontPath, 48f);
                ctx.Batch.Begin();
                ctx.Batch.Draw(white, new Vector4(40, 40, 180, 90), new Vector4(0.85f, 0.2f, 0.2f, 1f));
                ctx.Batch.Draw(white, new Vector4(260, 150, 150, 120), new Vector4(0.2f, 0.7f, 0.3f, 1f));
                ctx.Batch.Draw(white, new Vector4(120, 220, 110, 70), new Vector4(0.25f, 0.4f, 0.9f, 0.9f));
                ctx.Batch.DrawString(font, "KE", new Vector2(60, 200), new Vector4(0.95f, 0.95f, 0.4f, 1f));
                ctx.Batch.End();
            });

            GoldenCompare.AssertOrUpdate("scene2d", rgba, W, H);
        }

        [GpuFact]
        public void Golden2D_Primitives()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Vector4(0.06f, 0.07f, 0.10f, 1f), ctx =>
            {
                using var prim = new PrimitiveRenderer(ctx);
                ctx.Batch.Begin();

                // Filled rect + outline rect on top of it.
                prim.DrawFilledRect(ctx.Batch, new KhaozEngine.Windowing.Rect(30, 30, 130, 80), new Vector4(0.20f, 0.45f, 0.85f, 1f));
                prim.DrawRect(ctx.Batch, new KhaozEngine.Windowing.Rect(30, 30, 130, 80), new Vector4(0.95f, 0.95f, 0.95f, 1f), 3f);

                // A couple of diagonal lines (rotated quads).
                prim.DrawLine(ctx.Batch, new Vector2(40, 130), new Vector2(180, 210), new Vector4(0.95f, 0.35f, 0.2f, 1f), 4f);
                prim.DrawLine(ctx.Batch, new Vector2(40, 210), new Vector2(180, 130), new Vector4(0.2f, 0.9f, 0.4f, 1f), 4f);

                // Circle outline + ring, distinct radii.
                prim.DrawCircle(ctx.Batch, new Vector2(280, 80), 45f, new Vector4(0.9f, 0.8f, 0.2f, 1f), segments: 40, thickness: 2f);
                prim.DrawRing(ctx.Batch, new Vector2(400, 80), 50f, 6f, new Vector4(0.85f, 0.3f, 0.85f, 1f));

                // Filled circle.
                prim.DrawFilledCircle(ctx.Batch, new Vector2(280, 200), 42f, new Vector4(0.3f, 0.7f, 0.9f, 1f));

                // Vertical gradient panel.
                prim.DrawVerticalGradient(ctx.Batch, new KhaozEngine.Windowing.Rect(360, 150, 90, 110),
                    new Vector4(0.9f, 0.9f, 0.95f, 1f), new Vector4(0.15f, 0.1f, 0.3f, 1f), bands: 16);

                // Progress bar near the bottom.
                prim.DrawProgressBar(ctx.Batch, new KhaozEngine.Windowing.Rect(40, 280, 400, 24), 0.62f,
                    new Vector4(0.2f, 0.8f, 0.35f, 1f), new Vector4(0.15f, 0.15f, 0.18f, 1f),
                    new Vector4(0.8f, 0.8f, 0.85f, 1f), 2f);

                ctx.Batch.End();
            });

            GoldenCompare.AssertOrUpdate("scene2d_primitives", rgba, W, H);
        }
    }
}
