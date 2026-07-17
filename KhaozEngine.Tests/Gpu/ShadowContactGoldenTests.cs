using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Property "golden" for the ShadowMap tier's contact shadow: the cast shadow of a thin, foot/leg-like caster
    /// must CONNECT to the caster's base (no peter-panning) while the lit ground stays acne-free. Guards the fix that
    /// added the normal-offset bias (<see cref="ShadowSettings.ShadowNormalOffset"/>) and dropped the depth biases an
    /// order of magnitude: with the old defaults the shadow detached from the feet by ~8 texels at 45deg (much worse
    /// grazing), and zeroing the bias instead speckled the ground with self-shadow acne. Asserts two geometric
    /// invariants (not committed pixels, so it runs on every backend - the "Golden" in the name enrols it in the
    /// cross-platform GPU matrix): (1) the shadow starts within a few shadow texels of the caster's ground contact,
    /// and (2) the lit ground carries no acne. Skipped unless KE_GPU_TESTS=1. Uses the DEFAULT shadow bias settings,
    /// so a regression that bumps the depth bias back up (re-detaching the shadow) trips here.
    /// </summary>
    public sealed class ShadowContactGoldenTests
    {
        const int W = 720, H = 480;

        // Steep near-top-down camera (from +X+Z) so the caster's dark side faces foreshorten and never occlude the
        // +X ground contact line the scan reads.
        const float CamAz = 0.55f, CamEl = 1.28f;
        static readonly Vector3 CamTarget = new(0.55f, 0f, 0f);
        static readonly Vector3 CamSize = new(5.0f, 2.5f, 4.0f);

        const float Radius = 16f;         // the DEFAULT focus radius: the ortho depth range = 4*16 = 64 world units,
                                          // where the old 0.004 constant bias put ~0.25 world units of peter-panning.
        const float ThinX = 0.12f;        // a thin slab (12 cm along the light travel): a character foot/leg, where the
                                          // second-depth trick's front-to-back margin is too small to hide a big bias.
        const float FootX = ThinX * 0.5f; // the +X (shadow-side) base edge = the true feet-contact x.

        static readonly Color GroundCol = new(0.60f, 0.60f, 0.62f, 1f);
        static readonly Color SlabGreen = new(0.15f, 0.85f, 0.20f, 1f);

        [GpuFact]
        public void Golden3D_ShadowMap_ContactConnectsAtFeet_NoAcne()
        {
            // Key light travelling toward +X and down at 45deg (shadow falls toward +X).
            Vector3 light = Vector3.Normalize(new Vector3(MathF.Cos(MathF.PI / 4f), -MathF.Sin(MathF.PI / 4f), 0f));

            MeshHandle floor = default, slab = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(48f, 0.1f));
                    slab = scene.LoadMesh(MeshPrimitives.Box(1f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
                    scene.Post.LightDirection = light;
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Post.Quality.Shadows.ShadowNearDistance = Radius;
                    // Bias knobs are LEFT AT DEFAULTS on purpose: this test guards the shipped defaults.
                    scene.Camera.Azimuth = CamAz;
                    scene.Camera.Elevation = CamEl;
                    scene.Camera.Frame(CamTarget, CamSize);
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), GroundCol);
                    scene.Draw(slab, Matrix4x4.CreateScale(ThinX, 1f, 0.8f) * Matrix4x4.CreateTranslation(0f, 0.5f, 0f), SlabGreen);
                },
                frames: 2);

            var cam = new IsoCamera3D { Azimuth = CamAz, Elevation = CamEl };
            cam.Frame(CamTarget, CamSize);
            cam.AspectRatio = (float)W / H;

            // Lit reference: ground on the LIT (-X) side, away from the caster and its shadow.
            float litLum = AvgGroundLum(rgba, cam, new[] { new Vector3(-3.0f, 0f, 0f), new Vector3(-3.5f, 0f, 1.0f), new Vector3(-3.0f, 0f, -1.0f) });
            Assert.True(litLum > 0.30f, $"lit ground too dark to run the test (litLum {litLum:0.###}); scene/camera changed?");
            float shadowThresh = 0.62f * litLum;

            // 1) CONTACT: scan the ground along z=0 from just past the foot outward. The first ground texel below the
            //    shadow threshold is the shadow start. The gap to the foot must be within a few shadow texels.
            float texelWorld = ShadowMapMath.TexelWorldSize(Radius, ShadowMapRendererResolution());
            float shadowStartX = float.NaN;
            for (float x = FootX + 0.01f; x <= FootX + 2.5f; x += 0.005f)
            {
                if (!SampleGround(rgba, cam, new Vector3(x, 0f, 0f), out float lum, out bool ground) || !ground) continue;
                if (lum < shadowThresh) { shadowStartX = x; break; }
            }
            Assert.False(float.IsNaN(shadowStartX), "no cast shadow found on the ground; the ShadowMap tier did not render a contact shadow");
            float gapWorld = shadowStartX - FootX;
            float gapTexels = gapWorld / texelWorld;
            Assert.True(gapTexels <= 4f,
                $"shadow peter-pans: it starts {gapWorld:0.###} world units ({gapTexels:0.#} shadow texels) from the caster's feet " +
                $"(threshold 4 texels). The old defaults detached it by ~8 texels here. Did the depth bias regress up or ShadowNormalOffset drop?");

            // 2) NO ACNE: sample a grid of LIT ground (-X side, off-shadow Z lanes). Almost none may be spuriously dark.
            int total = 0, dark = 0;
            float acneThresh = 0.75f * litLum;   // a genuinely lit texel is ~litLum; acne pulls it well below
            for (float gx = -4.0f; gx <= -0.7f; gx += 0.2f)
                for (float gz = -3f; gz <= 3f; gz += 0.4f)
                {
                    if (!SampleGround(rgba, cam, new Vector3(gx, 0f, gz), out float lum, out bool ground) || !ground) continue;
                    total++;
                    if (lum < acneThresh) dark++;
                }
            Assert.True(total > 100, $"sampled too few lit-ground texels ({total}); scene/camera changed?");
            float darkFrac = (float)dark / total;
            Assert.True(darkFrac < 0.05f,
                $"lit ground shows self-shadow acne: {dark}/{total} ({darkFrac:P0}) lit texels are spuriously dark " +
                $"(threshold 5%). Did ShadowNormalOffset drop or the depth bias go to zero?");
        }

        // The renderer clamps the shadow map to its allocated resolution, which defaults to the settings' 2048 here.
        static int ShadowMapRendererResolution() => new ShadowSettings().ShadowMapResolution;

        static float AvgGroundLum(byte[] rgba, IsoCamera3D cam, Vector3[] pts)
        {
            float sum = 0; int n = 0;
            foreach (var p in pts)
                if (SampleGround(rgba, cam, p, out float lum, out bool ground) && ground) { sum += lum; n++; }
            return n == 0 ? 0f : sum / n;
        }

        // Project a ground world point, average a 3x3 window, report luminance (0..1) and whether the pixel is GROUND
        // (the neutral-albedo tile, lit or shadowed) rather than the green caster. The caster albedo is green so every
        // caster face is green-dominant, which the neutral ground never is.
        static bool SampleGround(byte[] rgba, IsoCamera3D cam, Vector3 world, out float lum, out bool ground)
        {
            lum = 0; ground = false;
            if (!cam.WorldToScreen(world, W, H, out Vector2 p)) return false;
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
            if (n == 0) return false;
            float rf = r / (255f * n), gf = g / (255f * n), bf = b / (255f * n);
            ground = !(gf > rf + 0.06f && gf > bf);   // green-dominant => caster, not ground
            lum = 0.299f * rf + 0.587f * gf + 0.114f * bf;
            return true;
        }
    }
}
