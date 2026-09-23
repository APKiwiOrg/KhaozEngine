using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Cascaded-shadow-map (CSM) GPU coverage: the committed wide-scene golden (a caster far BEYOND the old single
    /// 16-unit footprint still casts - the defect the cascades fix), the hand-off golden (the scene whose blend band
    /// <see cref="CascadeHandoffBlendGoldenTests"/> asserts, since the grid cannot), and a moving-camera regression
    /// guard (a fixed world point stays shadowed when the camera moves, since the cascades re-fit around the camera
    /// focus - the old single map slid its coverage box, dropping the shadow). Skipped unless KE_GPU_TESTS=1.
    /// </summary>
    public sealed class CascadeShadowGpuTests
    {
        const int W = 480, H = 320;

        // A wide, angled ground with a tall caster far out past the pre-cascade 16-unit map, so the cascades (outer
        // reach 130) still shadow it. Committed golden - a regression that shrinks coverage back to a single near map
        // (dropping the far shadow) moves it. Baked on all three backends (metal/direct3d11/vulkan).
        [GpuFact]
        public void Golden3D_CascadeWide()
        {
            MeshHandle floor = default, nearBox = default, farBox = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(300f, 0.1f));
                    nearBox = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                    farBox = scene.LoadMesh(MeshPrimitives.Box(1.6f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;   // default 3 cascades, near distance 16
                    scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
                    // Look at z=14 so the focus (~near origin) puts the far box (z=44, ~30 units out, WAY past the
                    // 16-unit near map) into an outer cascade, and both boxes must drop a shadow.
                    scene.Camera.Azimuth = 0.5f;
                    scene.Camera.Elevation = 0.7f;
                    scene.Camera.Frame(new Vector3(0f, 0f, 22f), new Vector3(20f, 6f, 52f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 22f), new Color(0.60f, 0.61f, 0.63f, 1f));
                    scene.Draw(nearBox, Matrix4x4.CreateScale(1f, 1.6f, 1f) * Matrix4x4.CreateTranslation(0f, 1f, 4f),
                        new Color(0.2f, 0.75f, 0.25f, 1f));
                    scene.Draw(farBox, Matrix4x4.CreateScale(1f, 2.0f, 1f) * Matrix4x4.CreateTranslation(0f, 1.6f, 44f),
                        new Color(0.85f, 0.35f, 0.15f, 1f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_cascade_wide", rgba, W, H);
        }

        // Cross-cascade hand-off golden over CascadeHandoffScene: a perspective camera looks down a row of boxes
        // receding in view depth (6 to 44 units), with ShadowNearDistance 8 pulling the cascade 0-to-1 hand-off onto
        // visibly shadowed ground in the lower middle of the frame, and the default ShadowCascadeBlend 0.15 pinned
        // explicit. What this grid does NOT see is the blend itself. The golden audit rendered this scene with the
        // blend at 0 and the grid moved by 0.0080 at worst, under even the 0.01 tolerance: 148 pixels change by more
        // than 16/255 (at most 0.21), and the 32x18 average dilutes that away
        // (docs/design/GOLDEN-TEST-AUDIT-2026-09-23.md section 3). The blend band's own net is the in-session A/B in
        // CascadeHandoffBlendGoldenTests. This row catches region-scale changes to the scene's cascaded shadows.
        // Baked per backend family through the CI bake workflow.
        [GpuFact]
        public void Golden3D_CascadeHandoff()
        {
            FlyCamera3D fly = CascadeHandoffScene.Camera();
            byte[] rgba = CascadeHandoffScene.Capture(fly, CascadeHandoffScene.DefaultBlend);

            // Framing guard, before the golden compare: re-fit the cascades on the CPU exactly as
            // Scene3D.ComputeShadowCascades does and require a healthy count of visible ground points inside
            // cascade 0's outer blend band with cascade 1 also covering them - the points the shader cross-fades.
            // If a camera/settings tweak slides the band off the visible ground, this trips with a clear message
            // instead of the golden failing obliquely. Measured 412 of 1200 sampled points in-band as authored.
            int inBand = CascadeHandoffScene.CountVisibleGroundPointsInCascade0BlendBand(fly,
                CascadeHandoffScene.DefaultBlend);
            Assert.True(inBand >= 100,
                $"only {inBand} sampled ground points sit in cascade 0's blend band with cascade 1 cover " +
                "(expected ~412); the cascade 0-to-1 hand-off no longer crosses the visible ground, so this " +
                "scene stopped exercising the cross-cascade blend. Re-frame the camera or the shadow distances.");

            GoldenCompare.AssertOrUpdate("scene3d_cascade_handoff", rgba,
                CascadeHandoffScene.W, CascadeHandoffScene.H);
        }

        // Moving-box regression guard: a fixed world ground point under a fixed caster must stay shadowed whether the
        // camera looks from one side or the other. Each cascade re-fits to its slice of the camera frustum every
        // frame, so a fixed point can change WHICH cascade shadows it as the camera moves - but it must never fall OUT
        // of shadow (the old single map's coverage box slid off the point). No committed golden: a same-session
        // invariant, so it runs on every backend.
        [GpuFact]
        public void Cascade_FixedWorldPoint_StaysShadowed_AsCameraMoves()
        {
            Vector3 light = Vector3.Normalize(new Vector3(0f, -0.5f, 0.87f));  // shallow: travels toward +z, long shadow
            // The caster and the ground probe are FIXED in the world. Only the camera differs between renders.
            var casterXform = Matrix4x4.CreateScale(1.4f, 2.2f, 1.4f) * Matrix4x4.CreateTranslation(0f, 1.5f, 0f);
            var probe = new Vector3(0f, 0f, 2.4f);      // ground solidly inside the caster's long +z shadow
            var litRef = new Vector3(-6f, 0f, -2f);     // open lit ground, off the shadow

            float shadowedA = ShadowRatioAt(light, casterXform, probe, litRef, camAz: 0.6f, camEl: 0.95f, camTarget: new Vector3(0f, 0f, 5f));
            float shadowedB = ShadowRatioAt(light, casterXform, probe, litRef, camAz: 2.3f, camEl: 0.95f, camTarget: new Vector3(0f, 0f, 5f));

            Assert.True(shadowedA < 0.8f, $"probe not shadowed from camera A (ratio {shadowedA:0.###}); scene/camera changed?");
            Assert.True(shadowedB < 0.8f, $"probe not shadowed from camera B (ratio {shadowedB:0.###}) - the shadow moved/dropped when the camera turned (the pre-cascade sliding-box defect)");
        }

        // Render the fixed world with a given camera and return the probe's luminance as a fraction of the lit-ground
        // luminance (a value well below 1 means the probe is in shadow).
        static float ShadowRatioAt(Vector3 light, Matrix4x4 casterXform, Vector3 probe, Vector3 litRef,
            float camAz, float camEl, Vector3 camTarget)
        {
            MeshHandle floor = default, caster = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(60f, 0.1f));
                    caster = scene.LoadMesh(MeshPrimitives.Box(1f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
                    scene.Post.LightDirection = light;
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Camera.Azimuth = camAz;
                    scene.Camera.Elevation = camEl;
                    scene.Camera.Frame(camTarget, new Vector3(14f, 6f, 14f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity, new Color(0.60f, 0.61f, 0.63f, 1f));
                    scene.Draw(caster, casterXform, new Color(0.2f, 0.75f, 0.25f, 1f));
                },
                frames: 2);

            var cam = new IsoCamera3D { Azimuth = camAz, Elevation = camEl };
            cam.Frame(camTarget, new Vector3(14f, 6f, 14f));
            cam.AspectRatio = (float)W / H;
            float lit = GroundLum(rgba, cam, litRef);
            float at = GroundLum(rgba, cam, probe);
            return lit > 1e-3f ? at / lit : 1f;
        }

        static float GroundLum(byte[] rgba, IsoCamera3D cam, Vector3 world)
        {
            if (!cam.WorldToScreen(world, W, H, out Vector2 p)) return 0f;
            int px = (int)(p.X + 0.5f), py = (int)(p.Y + 0.5f);
            long r = 0, g = 0, b = 0; int n = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = px + dx, y = py + dy;
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    int i = (y * W + x) * 4;
                    r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; n++;
                }
            if (n == 0) return 0f;
            float rf = r / (255f * n), gf = g / (255f * n), bf = b / (255f * n);
            return 0.299f * rf + 0.587f * gf + 0.114f * bf;
        }
    }
}
