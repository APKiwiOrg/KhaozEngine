using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU proof of the shadow depth-pass dirty-skip (Scene3D.ShadowPassSkippedLastFrame). The 2048^2 light-space
    /// depth map persists across frames, so an unchanged static shadow scene reuses it and skips the caster draws;
    /// (a) proves a static second frame skips AND renders pixel-identically to the freshly-rendered first frame,
    /// (b) proves a moved caster re-renders (the shadow moves and the frame is NOT skipped), (c) proves a scene with
    /// an animated skinned caster never skips. Driven through Render3DPreview so the per-frame skip flag can be read
    /// after each render. Gated on KE_GPU_TESTS.
    /// </summary>
    public sealed class ShadowDepthDirtySkipGpuTests
    {
        const int W = 256, H = 200;

        static void ConfigureShadowScene(Scene3D scene)
        {
            scene.Post.Starfield = false;
            scene.Post.Outline = true;
            scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.Quality.Shadows.ShadowFocusRadius = 5f;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            scene.Camera.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));
        }

        static byte[] Read(IGpuDevice gd, Texture2D tex) => GpuReadback.ToRgba(gd, tex.Handle, W, H);

        static long Diff(byte[] a, byte[] b)
        {
            long d = 0;
            for (int i = 0; i < a.Length; i++) d += Math.Abs(a[i] - b[i]);
            return d;
        }

        static int DarkPixels(byte[] px)
        {
            // Shadowed floor pixels are darker than the lit floor; count clearly-dark opaque pixels as a shadow proxy.
            int n = 0;
            for (int p = 0; p < px.Length / 4; p++)
            {
                int r = px[p * 4], g = px[p * 4 + 1], b = px[p * 4 + 2], a = px[p * 4 + 3];
                if (a > 200 && r < 90 && g < 90 && b < 90) n++;
            }
            return n;
        }

        [GpuFact]
        public void Static_second_frame_skips_and_is_pixel_identical()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle tallBox = preview.Scene.LoadMesh(MeshPrimitives.Box(1.4f));

            void DrawStatic(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                s.Draw(tallBox, Matrix4x4.CreateTranslation(-1.2f, 0.7f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
            }

            // Frame 1: first shadow frame - must RENDER the depth pass (no prior map to reuse).
            byte[] img1 = Read(gd, preview.Capture(DrawStatic));
            Assert.False(preview.Scene.ShadowPassSkippedLastFrame, "first shadow frame must render the depth pass");

            // Frame 2: identical static scene - must SKIP (reuse the persistent map) and render identically.
            byte[] img2 = Read(gd, preview.Capture(DrawStatic));
            Assert.True(preview.Scene.ShadowPassSkippedLastFrame,
                "an unchanged static shadow scene must skip the depth pass on the second frame");

            Assert.True(DarkPixels(img1) > 150, $"expected a visible shadow on the floor, got {DarkPixels(img1)} dark pixels");
            Assert.Equal(0, Diff(img1, img2));   // reusing the map must be byte-identical to re-rendering it
        }

        [GpuFact]
        public void Moving_caster_rerenders_and_shadow_moves()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = preview.Scene.LoadMesh(MeshPrimitives.Box(1.2f));

            byte[] Frame(float x) => Read(gd, preview.Capture(s =>
            {
                s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                s.Draw(box, Matrix4x4.CreateTranslation(x, 0.6f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
            }));

            byte[] a = Frame(-1.4f);                 // first frame: renders
            byte[] b = Frame(1.4f);                  // caster moved: must re-render (dirty)
            Assert.False(preview.Scene.ShadowPassSkippedLastFrame, "a moved caster must re-render the shadow map");
            Assert.True(Diff(a, b) > 20000, $"moving the caster must move the shadow (image diff {Diff(a, b)})");

            // A third frame with the caster STILL at 1.4 is now static again, so it skips.
            Frame(1.4f);
            Assert.True(preview.Scene.ShadowPassSkippedLastFrame, "a re-settled static caster must skip again");
        }

        [GpuFact]
        public void Skinned_caster_never_skips()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            var limb = new SkinnedLimb(preview.Scene, radius: 0.4f, length: 2.5f, ringSegments: 8, radialSegments: 8,
                boneCount: 5, ChainConfig.Writhe, Axis.Z);

            void DrawWithLimb(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                limb.Draw(s, Matrix4x4.CreateTranslation(0f, 0.8f, 0f), new Color(0.8f, 0.4f, 0.3f, 1f));
            }

            limb.Update(new Vector3(0f, 0.8f, 0f), Vector3.UnitZ, Vector3.UnitY, 1.0f);
            preview.Capture(DrawWithLimb);
            Assert.False(preview.Scene.ShadowPassSkippedLastFrame, "a shadow scene with a skinned caster renders the first frame");

            // Even with the bone pose held IDENTICAL, a skinned caster forces a re-render (bone palettes are not hashed).
            preview.Capture(DrawWithLimb);
            Assert.False(preview.Scene.ShadowPassSkippedLastFrame,
                "any skinned caster present must force the shadow depth pass to re-render every frame");

            limb.Dispose();
        }
    }
}
