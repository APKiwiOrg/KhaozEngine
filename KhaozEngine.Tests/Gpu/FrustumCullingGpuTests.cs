using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU-level proofs for camera-frustum culling (Task 1): (a) off/on parity - culling is pixel-neutral, and
    /// (b) the shadow depth pass is NOT camera-culled, so an off-screen caster still throws an on-screen shadow.
    /// Invariant tests (no committed golden grid): they assert relationships between two renders, so they run on
    /// any backend without a baked reference. Gated on KE_GPU_TESTS like the goldens.
    /// </summary>
    public sealed class FrustumCullingGpuTests
    {
        const int W = 480, H = 320;

        // Render a scene with a grid of boxes, some inside the view and many far outside it, capturing the
        // drawn/culled counters via the callback. Culling is toggled by the caller.
        static byte[] RenderGrid(bool culling, out int drawn, out int culled)
        {
            int d = 0, c = 0;
            MeshHandle box = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    box = scene.LoadMesh(MeshPrimitives.Box(0.6f));
                    scene.FrustumCulling = culling;
                    scene.Post.Starfield = false;
                    scene.Post.BackgroundColor = new Color(0.08f, 0.10f, 0.14f, 1f);
                    // Frame a small central region so the far ring of boxes is off-frustum.
                    scene.Camera.Frame(Vector3.Zero, new Vector3(5f, 4f, 5f));
                },
                drawFrame: scene =>
                {
                    // A 9x9 grid of boxes spanning +/-40 world units; only the central few are in view.
                    for (int gx = -4; gx <= 4; gx++)
                        for (int gz = -4; gz <= 4; gz++)
                            scene.Draw(box, Matrix4x4.CreateTranslation(gx * 10f, 0f, gz * 10f),
                                new Color(0.8f, 0.5f, 0.2f, 1f));
                    d = scene.DrawnInstances; c = scene.CulledInstances;
                },
                frames: 2);
            drawn = d; culled = c;
            return rgba;
        }

        [GpuFact]
        public void Culling_off_and_on_are_pixel_identical_and_counters_report_the_win()
        {
            byte[] off = RenderGrid(culling: false, out int drawnOff, out int culledOff);
            byte[] on = RenderGrid(culling: true, out int drawnOn, out int culledOn);

            // Off: nothing culled, every one of the 81 instances drawn.
            Assert.Equal(0, culledOff);
            Assert.Equal(81, drawnOff);

            // On: the far ring is provably outside the frustum, so many are culled and fewer drawn.
            Assert.True(culledOn > 0, "expected some boxes culled with culling on");
            Assert.Equal(81, drawnOn + culledOn);
            Assert.True(drawnOn < drawnOff, "culling on should draw fewer instances");

            // Pixel-neutral by construction: culling only removes geometry the camera cannot see, so the two
            // images must be byte-identical.
            Assert.Equal(off.Length, on.Length);
            Assert.True(BytesEqual(off, on), "culling on/off produced different pixels (not pixel-neutral)");
        }

        [GpuFact]
        public void Offscreen_caster_is_camera_culled_yet_still_writes_the_shadow_map()
        {
            // Requirement 5: the shadow depth pass must NOT use the camera frustum. A caster placed well outside the
            // camera view is (a) culled from the VISIBLE pass, but (b) must still be rendered into the light-space
            // shadow depth map so its shadow lands wherever the light throws it. Prove it directly on the shadow map:
            // rendering the same scene with vs without the off-frustum caster must add near-depth (caster) texels to
            // the map. If the shadow pass had been camera-culled too, the two maps would be identical.
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            Scene3D scene = preview.Scene;

            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(12f, 0.1f));
            MeshHandle caster = scene.LoadMesh(MeshPrimitives.Box(1.4f));
            scene.FrustumCulling = true;
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.Quality.Shadows.ShadowFocusRadius = 10f;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            // Frame a small region near the origin; the caster at x=-10 sits outside this camera frustum.
            scene.Camera.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(5f, 4f, 5f));
            var casterPos = Matrix4x4.CreateTranslation(-10f, 0.9f, 0f);

            // Without the caster: the shadow map holds only the floor (which is receive-only splatless model here,
            // but a flat tile casting on itself contributes little); record its near-texel count.
            preview.Capture(s => s.Draw(floor, Matrix4x4.Identity));
            int nearNoCaster = NearTexels(scene.DebugReadShadowMap(out _, out _));

            // With the off-frustum caster: it is culled from the visible pass (CulledInstances counts it) yet the
            // shadow pass renders it, adding a cluster of near-depth texels to the map.
            preview.Capture(s => { s.Draw(floor, Matrix4x4.Identity); s.Draw(caster, casterPos, new Color(0.15f, 0.75f, 0.2f, 1f)); });
            int culled = scene.CulledInstances;
            int nearWithCaster = NearTexels(scene.DebugReadShadowMap(out _, out _));

            Assert.True(culled >= 1, "the off-screen caster should have been camera-culled from the visible pass");
            Assert.True(nearWithCaster > nearNoCaster + 20,
                $"the culled caster must still write the shadow map: near texels {nearNoCaster} -> {nearWithCaster}");
        }

        // Count shadow-map texels that hold a NEAR (caster-written) depth. The map clears to 1.0 (far); a caster
        // writes a value < 1, so counting sub-1 texels measures caster coverage in the light-space depth map.
        static int NearTexels(float[] depth)
        {
            int n = 0;
            for (int i = 0; i < depth.Length; i++) if (depth[i] < 0.999f) n++;
            return n;
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
