using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU coverage of the textured, depth-interleaved billboard path
    /// (<see cref="Scene3D.DrawBillboard(Scene3D.TextureHandle,Vector3,float,Vector4,Color,BillboardBlend)"/>).
    /// Backend-agnostic: asserts relative pixel relationships (which colour dominates the centre, brighter/darker)
    /// rather than absolute values, so it needs no committed per-backend golden (the committed image-regression
    /// golden is <c>scene3d_texbillboard</c> in <see cref="GoldenSnapshotTests"/>). Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class TexturedBillboardGpuTests
    {
        const int W = 128, H = 128;

        // A 2x1 "sprite sheet": left cell red, right cell green. Each cell is solid so a sub-rect samples one colour.
        static byte[] TwoCellSheet()
        {
            var px = new byte[2 * 1 * 4];
            px[0] = 235; px[1] = 25; px[2] = 25; px[3] = 255;  // texel 0 (u in [0,0.5)) = red
            px[4] = 25; px[5] = 220; px[6] = 35; px[7] = 255;  // texel 1 (u in [0.5,1]) = green
            return px;
        }

        static readonly Vector4 LeftCell = new(0f, 0f, 0.5f, 1f);
        static readonly Vector4 RightCell = new(0.5f, 0f, 1f, 1f);

        static void CleanBackground(Scene3D scene)
        {
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
        }

        static (long r, long g, long b) Centre(byte[] rgba)
        {
            long r = 0, g = 0, b = 0;
            for (int y = H / 2 - 8; y < H / 2 + 8; y++)
                for (int x = W / 2 - 8; x < W / 2 + 8; x++)
                {
                    int i = (y * W + x) * 4;
                    r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2];
                }
            return (r, g, b);
        }

        [GpuFact]
        public void TexturedBillboard_samples_the_selected_sheet_cell()
        {
            Scene3D.TextureHandle sheet = default;
            void Setup(Scene3D scene)
            {
                sheet = scene.LoadTexture(TwoCellSheet(), 2, 1);
                CleanBackground(scene);
                scene.Camera.Frame(Vector3.Zero, new Vector3(2.4f, 2.4f, 2.4f));
            }

            byte[] left = Render3DSnapshot.Capture(W, H, Setup,
                drawFrame: scene => scene.DrawBillboard(sheet, Vector3.Zero, 1.4f, LeftCell, Color.White), frames: 1);
            byte[] right = Render3DSnapshot.Capture(W, H, Setup,
                drawFrame: scene => scene.DrawBillboard(sheet, Vector3.Zero, 1.4f, RightCell, Color.White), frames: 1);

            var (lr, lg, _) = Centre(left);
            var (rr, rg, _) = Centre(right);
            Assert.True(lr > lg * 3, $"left cell should read red: r={lr} g={lg}");
            Assert.True(rg > rr * 3, $"right cell should read green: r={rr} g={rg}");
        }

        [GpuFact]
        public void TexturedBillboard_tint_multiplies_the_sampled_texel()
        {
            Scene3D.TextureHandle sheet = default;
            void Setup(Scene3D scene)
            {
                sheet = scene.LoadTexture(TwoCellSheet(), 2, 1);
                CleanBackground(scene);
                scene.Camera.Frame(Vector3.Zero, new Vector3(2.4f, 2.4f, 2.4f));
            }

            // Red cell tinted green -> red*green ~ 0: the quad should be near-black, far dimmer than untinted red.
            byte[] untinted = Render3DSnapshot.Capture(W, H, Setup,
                drawFrame: scene => scene.DrawBillboard(sheet, Vector3.Zero, 1.4f, LeftCell, Color.White), frames: 1);
            byte[] tinted = Render3DSnapshot.Capture(W, H, Setup,
                drawFrame: scene => scene.DrawBillboard(sheet, Vector3.Zero, 1.4f, LeftCell, new Color(0f, 1f, 0f, 1f)), frames: 1);

            var (ur, _, _) = Centre(untinted);
            var (tr, tg, tb) = Centre(tinted);
            Assert.True(ur > (tr + tg + tb) * 2 + 1, $"green tint should kill the red texel: untintedR={ur} tinted=({tr},{tg},{tb})");
        }

        [GpuFact]
        public void TexturedBillboard_additive_is_brighter_than_alpha_over_a_lit_mesh()
        {
            MeshHandle box = default;
            Scene3D.TextureHandle sheet = default;
            Vector3 fwd = default;
            void Setup(Scene3D scene)
            {
                box = scene.LoadMesh(MeshPrimitives.Box(1.6f));
                sheet = scene.LoadTexture(TwoCellSheet(), 2, 1);
                CleanBackground(scene);
                scene.Camera.Frame(Vector3.Zero, new Vector3(4f, 4f, 4f));
                fwd = scene.Camera.Forward;
            }

            // A half-transparent red quad IN FRONT of the box (toward the eye = -forward): additive adds to the
            // lit box, alpha blends toward the quad colour. Over a bright mesh, additive reads brighter overall.
            byte[] alpha = Render3DSnapshot.Capture(W, H, Setup,
                drawFrame: scene =>
                {
                    scene.Draw(box, Matrix4x4.Identity, new Color(0.6f, 0.6f, 0.6f, 1f));
                    scene.DrawBillboard(sheet, -fwd * 1.6f, 0.6f, LeftCell, new Color(1f, 1f, 1f, 0.5f), BillboardBlend.Alpha);
                }, frames: 1);
            byte[] additive = Render3DSnapshot.Capture(W, H, Setup,
                drawFrame: scene =>
                {
                    scene.Draw(box, Matrix4x4.Identity, new Color(0.6f, 0.6f, 0.6f, 1f));
                    scene.DrawBillboard(sheet, -fwd * 1.6f, 0.6f, LeftCell, new Color(1f, 1f, 1f, 0.5f), BillboardBlend.Additive);
                }, frames: 1);

            var (ar, ag, ab) = Centre(alpha);
            var (dr, dg, db) = Centre(additive);
            Assert.True(dr + dg + db > ar + ag + ab, $"additive should be brighter than alpha: add={dr + dg + db} alpha={ar + ag + ab}");
        }

        [GpuFact]
        public void TexturedBillboard_in_front_of_a_mesh_occludes_it()
        {
            MeshHandle box = default;
            Scene3D.TextureHandle sheet = default;
            Vector3 fwd = default;
            void Setup(Scene3D scene)
            {
                box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                sheet = scene.LoadTexture(TwoCellSheet(), 2, 1);
                CleanBackground(scene);
                scene.Camera.Frame(Vector3.Zero, new Vector3(4f, 4f, 4f));
                fwd = scene.Camera.Forward;
            }

            // Green box at origin; a RED billboard between it and the camera (-forward, toward the eye).
            byte[] rgba = Render3DSnapshot.Capture(W, H, Setup,
                drawFrame: scene =>
                {
                    scene.Draw(box, Matrix4x4.Identity, new Color(0.1f, 0.8f, 0.15f, 1f));
                    scene.DrawBillboard(sheet, -fwd * 1.6f, 0.8f, LeftCell, Color.White);
                }, frames: 1);

            var (r, g, _) = Centre(rgba);
            Assert.True(r > g * 2, $"a billboard in front should occlude the box (centre reads red): r={r} g={g}");
        }

        [GpuFact]
        public void Mesh_occludes_a_textured_billboard_behind_it()
        {
            MeshHandle box = default;
            Scene3D.TextureHandle sheet = default;
            Vector3 fwd = default;
            void Setup(Scene3D scene)
            {
                box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                sheet = scene.LoadTexture(TwoCellSheet(), 2, 1);
                CleanBackground(scene);
                scene.Camera.Frame(Vector3.Zero, new Vector3(4f, 4f, 4f));
                fwd = scene.Camera.Forward;
            }

            // Green box at origin; a RED billboard BEHIND it (+forward, away from the eye): the box must hide it.
            byte[] rgba = Render3DSnapshot.Capture(W, H, Setup,
                drawFrame: scene =>
                {
                    scene.Draw(box, Matrix4x4.Identity, new Color(0.1f, 0.8f, 0.15f, 1f));
                    scene.DrawBillboard(sheet, fwd * 1.6f, 0.8f, LeftCell, Color.White);
                }, frames: 1);

            var (r, g, _) = Centre(rgba);
            Assert.True(g > r * 2, $"the box should occlude the billboard behind it (centre reads green): r={r} g={g}");
        }

        [GpuFact]
        public void Invalid_texture_handle_draws_nothing()
        {
            MeshHandle box = default;
            Vector3 fwd = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                    CleanBackground(scene);
                    scene.Camera.Frame(Vector3.Zero, new Vector3(4f, 4f, 4f));
                    fwd = scene.Camera.Forward;
                },
                drawFrame: scene =>
                {
                    scene.Draw(box, Matrix4x4.Identity, new Color(0.1f, 0.8f, 0.15f, 1f));
                    // Invalid handle in front of the box: must be a no-op, so the box still reads green.
                    scene.DrawBillboard(Scene3D.TextureHandle.Invalid, -fwd * 1.6f, 0.8f, LeftCell, Color.White);
                }, frames: 1);

            var (r, g, _) = Centre(rgba);
            Assert.True(g > r * 2, $"invalid-texture billboard should draw nothing, box stays green: r={r} g={g}");
        }

        [GpuFact]
        public void Begin_clears_the_textured_billboard_queue()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            using var preview = new Render3DPreview(ctx.GpuDevice, W, H);

            Scene3D.TextureHandle sheet = preview.Scene.LoadTexture(TwoCellSheet(), 2, 1);
            preview.Scene.DrawBillboard(sheet, Vector3.Zero, 1f, LeftCell, Color.White);
            preview.Scene.DrawBillboard(sheet, Vector3.One, 1f, RightCell, Color.White);
            Assert.Equal(2, preview.Scene.TexturedBillboardCount);

            preview.Capture(_ => { }); // Begin() clears, then the empty draw adds nothing
            Assert.Equal(0, preview.Scene.TexturedBillboardCount);
        }
    }
}
