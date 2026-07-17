using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU proof of the starfield BACKGROUND pass contract (see StarfieldRenderer): the far-plane, read-only Equal
    /// depth test means stars paint ONLY true background pixels (where no geometry was drawn), never the geometry
    /// sitting in front of them, and the whole pass is skipped when Background is not Starfield. A coarse RGB golden
    /// grid cannot pin "never on the box" or "the pass did not run", which is why this is a targeted GPU test rather
    /// than a golden. Skipped unless KE_GPU_TESTS=1 (needs a real GPU device).
    /// </summary>
    public sealed class StarfieldGpuTests
    {
        const int W = 128, H = 128;

        // The box's screen area plus an edge margin, matching HdrPipelineGpuTests.Hdr_alpha_marker_survives_tonemap's
        // idiom: everything outside this box is true background given the box + camera framing below, so only
        // stars can land there.
        const int Bx0 = W * 40 / 100, Bx1 = W * 60 / 100, By0 = H * 40 / 100, By1 = H * 60 / 100;

        static float Luma(byte r, byte g, byte b) => 0.299f * r + 0.587f * g + 0.114f * b;

        // Render a small centred box (background all around) under the given background mode. Built directly
        // against Scene3D via Render3DSnapshot, NOT Render3DPreview: Render3DPreview's own defaults force
        // Post.Starfield = false and TransparentBackground = true (Render3DPreview.cs:73-74), so a scene built
        // through it renders BackgroundMode.Solid no matter what and can never show stars.
        static byte[] RenderBoxScene(BackgroundMode background)
        {
            MeshHandle box = default;
            return Render3DSnapshot.Capture(W, H,
                setup: s =>
                {
                    box = s.LoadMesh(MeshPrimitives.Box(0.3f));
                    s.EffectTimeSeconds = 0f;
                    s.Post.Background = background;
                    s.Post.BackgroundColor = new Color(0.02f, 0.03f, 0.06f, 1f);
                    s.Camera.Frame(Vector3.Zero, new Vector3(3.5f, 3.5f, 3.5f));   // small box centre, background around
                },
                drawFrame: s => s.Draw(box, Matrix4x4.Identity),
                frames: 2);
        }

        static float MaxLumaOutsideBox(byte[] rgba)
        {
            float max = 0f;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (x >= Bx0 && x < Bx1 && y >= By0 && y < By1) continue;
                    int i = (y * W + x) * 4;
                    float l = Luma(rgba[i], rgba[i + 1], rgba[i + 2]);
                    if (l > max) max = l;
                }
            return max;
        }

        static int CountBrightOutsideBox(byte[] rgba, float threshold)
        {
            int count = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (x >= Bx0 && x < Bx1 && y >= By0 && y < By1) continue;
                    int i = (y * W + x) * 4;
                    if (Luma(rgba[i], rgba[i + 1], rgba[i + 2]) > threshold) count++;
                }
            return count;
        }

        [GpuFact]
        public void Starfield_paints_background_only_never_the_box_solid_runs_no_pass()
        {
            byte[] solid = RenderBoxScene(BackgroundMode.Solid);
            byte[] star = RenderBoxScene(BackgroundMode.Starfield);

            // Reference clear luma: a corner pixel of the SOLID render, definitely a background pixel that Solid
            // never paints beyond the flat clear colour.
            float clearLuma = Luma(solid[(2 * W + 2) * 4], solid[(2 * W + 2) * 4 + 1], solid[(2 * W + 2) * 4 + 2]);

            // 3) Solid: no pixel outside the box exceeds the clear colour's luma anywhere in the frame, i.e. the
            // background pass did not run at all. This is what makes assertion 1 below meaningful: without this,
            // bright pixels outside the box under Starfield could just as well be some unrelated brightness in the
            // scene (a lighting bug, a stray blend), not proof the starfield pass itself is doing the painting.
            float maxOutsideSolid = MaxLumaOutsideBox(solid);
            Assert.True(maxOutsideSolid <= clearLuma + 1f,
                $"Solid background should stay flat at the clear colour outside the box (clear={clearLuma}, max={maxOutsideSolid})");

            // 1) Starfield: bright star pixels appear outside the box, well above the clear colour. Stars are
            // sparse (~0.8% of a 220x124 cell grid) and cell-sized, so count across the region rather than sampling
            // one arbitrary pixel.
            int starsOutside = CountBrightOutsideBox(star, clearLuma + 50f);
            Assert.True(starsOutside > 5,
                $"starfield should paint bright pixels outside the box, well above the clear colour (count={starsOutside}, clear={clearLuma})");

            // 2) Those bright pixels never land ON the box: the depth-gated star pass only paints pixels where no
            // geometry was drawn, so switching Background must never perturb the box's OWN rendered pixels
            // (whatever its lit colour happens to be). Diff a tight block at dead centre, guaranteed covered by the
            // box given the camera framing above, between the Starfield and Solid renders: byte-identical proves no
            // star landed there, whereas a naive brightness check can't tell a lit box from a star painted over it.
            long boxDiff = 0;
            for (int y = H / 2 - 3; y <= H / 2 + 3; y++)
                for (int x = W / 2 - 3; x <= W / 2 + 3; x++)
                {
                    int i = (y * W + x) * 4;
                    boxDiff += Math.Abs(star[i] - solid[i]) + Math.Abs(star[i + 1] - solid[i + 1]) + Math.Abs(star[i + 2] - solid[i + 2]);
                }
            Assert.Equal(0, boxDiff);
        }
    }
}
