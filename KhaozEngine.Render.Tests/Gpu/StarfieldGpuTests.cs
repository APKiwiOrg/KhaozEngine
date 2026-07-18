using System;
using System.Collections.Generic;
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

        // Tight block at dead centre, shared by the coverage guard and the box-identity diff below: guaranteed
        // covered by the box given the camera framing in RenderBoxScene, so both checks scan the same footprint.
        const int Cx0 = W / 2 - 3, Cx1 = W / 2 + 3, Cy0 = H / 2 - 3, Cy1 = H / 2 + 3;

        static float Luma(byte r, byte g, byte b) => 0.299f * r + 0.587f * g + 0.114f * b;

        // Render a small centred box (background all around) under the given background mode. Built directly
        // against Scene3D via Render3DSnapshot, NOT Render3DPreview: Render3DPreview's own defaults force
        // Post.Starfield = false and TransparentBackground = true (Render3DPreview.cs:73-74), so a scene built
        // through it renders BackgroundMode.Solid no matter what and can never show stars. transparentBackground
        // defaults to false (today's default). Pass true to observe the background pass's own alpha write, which
        // the default (opaque) blit path hardcodes to 1.0 regardless of source alpha (see BlitFrag).
        static byte[] RenderBoxScene(BackgroundMode background, bool transparentBackground = false)
        {
            MeshHandle box = default;
            return Render3DSnapshot.Capture(W, H,
                setup: s =>
                {
                    box = s.LoadMesh(MeshPrimitives.Box(0.3f));
                    s.EffectTimeSeconds = 0f;
                    s.Post.Background = background;
                    s.Post.BackgroundColor = new Color(0.02f, 0.03f, 0.06f, 1f);
                    s.Post.TransparentBackground = transparentBackground;
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

        // Average luma across the centre block (the same footprint the box-identity diff below scans). Used as a
        // coverage guard: proves the box's own geometry is actually painting that block before the byte-identity
        // diff is trusted to mean anything (see the comment at the call site).
        static float AverageLumaCentreBlock(byte[] rgba)
        {
            float sum = 0f;
            int count = 0;
            for (int y = Cy0; y <= Cy1; y++)
                for (int x = Cx0; x <= Cx1; x++)
                {
                    int i = (y * W + x) * 4;
                    sum += Luma(rgba[i], rgba[i + 1], rgba[i + 2]);
                    count++;
                }
            return sum / count;
        }

        // Median luma across every background pixel (outside the box) whose luma is at or below starThreshold,
        // i.e. the non-star population. Median rather than mean: the sparse bright stars would drag a mean away
        // from the value the overwhelming majority of the background actually sits at, so a mean check could pass
        // even while every non-star pixel individually drifted a little. The median reports exactly that
        // majority value, which is what a BgColor regression (a lane reorder in PackUbo, a hardcoded colour at
        // the Scene3D.cs call site, a dropped Vector4 component) would move.
        static float MedianBackgroundLuma(byte[] rgba, float starThreshold)
        {
            var lumas = new List<float>();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (x >= Bx0 && x < Bx1 && y >= By0 && y < By1) continue;
                    int i = (y * W + x) * 4;
                    float l = Luma(rgba[i], rgba[i + 1], rgba[i + 2]);
                    if (l <= starThreshold) lumas.Add(l);
                }
            lumas.Sort();
            return lumas[lumas.Count / 2];
        }

        // Average alpha across every background pixel (outside the box). Used only under TransparentBackground,
        // where the blit threads the source alpha straight through (outA = s.a) instead of hardcoding 1.0, so it
        // is the only place the background pass's OWN alpha write is observable.
        static float AverageAlphaOutsideBox(byte[] rgba)
        {
            float sum = 0f;
            int count = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (x >= Bx0 && x < Bx1 && y >= By0 && y < By1) continue;
                    int i = (y * W + x) * 4;
                    sum += rgba[i + 3];
                    count++;
                }
            return sum / count;
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

            // 2a) Coverage guard, gating check 2 below: prove the centre block is actually covered by the box's
            // geometry in the Solid render before trusting the byte-identity diff to mean anything. Without this,
            // a regression that made the box render nothing (bad camera framing, LoadMesh returning empty
            // geometry, a Draw call silently no-opping) would leave the centre block as flat background in BOTH
            // the Starfield and Solid renders. boxDiff would still land on exactly 0 and check 2 would pass
            // vacuously, having silently stopped testing "never on the box" at all. The threshold is relative to
            // the same measured clearLuma reference used throughout this test, not a hardcoded raw luma value.
            float avgBoxLumaSolid = AverageLumaCentreBlock(solid);
            Assert.True(avgBoxLumaSolid > clearLuma + 20f,
                $"the centre block should be covered by the box's own geometry, well above the clear colour (avg={avgBoxLumaSolid}, clear={clearLuma}). If this fails, the box is not covering the sampled region any more, which means the byte-identity check below would be comparing two patches of background and would pass without proving anything about stars never landing on the box.");

            // 2) Those bright pixels never land ON the box: the depth-gated star pass only paints pixels where no
            // geometry was drawn, so switching Background must never perturb the box's OWN rendered pixels
            // (whatever its lit colour happens to be). Diff the same tight centre block, guaranteed covered by the
            // box per the coverage guard above, between the Starfield and Solid renders: byte-identical proves no
            // star landed there, whereas a naive brightness check can't tell a lit box from a star painted over it.
            long boxDiff = 0;
            for (int y = Cy0; y <= Cy1; y++)
                for (int x = Cx0; x <= Cx1; x++)
                {
                    int i = (y * W + x) * 4;
                    boxDiff += Math.Abs(star[i] - solid[i]) + Math.Abs(star[i + 1] - solid[i + 1]) + Math.Abs(star[i + 2] - solid[i + 2]);
                }
            Assert.Equal(0, boxDiff);

            // 4) Non-star background pixels still sit at the clear colour. Checks 1-3 above all pass even if
            // PackUbo or the Scene3D.cs call site never got BgColor to the shader at all: Res (which drives the
            // star pattern via hash(cell)) is untouched by that failure, so stars would still appear outside the
            // box, the box's own geometry would still be untouched, and the whole background (bar the stars)
            // would just paint pure black instead of the clear colour. Nothing above samples a non-star
            // background pixel's actual colour, so that regression is invisible without this. Reuse the same
            // "not a star" threshold (clearLuma + 50) as starsOutside above so both checks agree on what counts
            // as a star. (Verified independently to fail under a BgColor-black sabotage: see task-7-report.md.
            // Note check 2 above also incidentally trips on that same sabotage, since the coverage guard only
            // proves the block is MOSTLY covered by the box, not fully, so a few leaked background pixels in the
            // sampled block pick up the corruption too. This check is the one that catches it BY DESIGN.)
            float medianBgLuma = MedianBackgroundLuma(star, clearLuma + 50f);
            Assert.True(Math.Abs(medianBgLuma - clearLuma) <= 1f,
                $"the overwhelming majority of Starfield background pixels should still sit at the clear colour (median={medianBgLuma}, clear={clearLuma})");
        }

        [GpuFact]
        public void Starfield_paints_the_background_opaque_under_transparent_background()
        {
            // Pin StarfieldFrag's own alpha write: `oColor = vec4(BgColor.rgb + vec3(star), 1.0)`, matching the
            // sky pass, unconditionally alpha = 1 whether or not a star landed on that fragment. The default
            // RenderBoxScene render above (TransparentBackground = false) can never observe this: BlitFrag
            // hardcodes outA = 1.0 on that path regardless of the source alpha, so the starfield's own alpha
            // write never survives to the readback there. TransparentBackground = true threads the source alpha
            // straight through instead (outA = s.a), so it is the only way to observe what the background pass
            // itself wrote.
            //
            // This also pins the near-miss recorded in the design doc: TransparentBackground = true combined
            // with the DEFAULT Starfield = true on a raw Scene3D makes the WHOLE background opaque, which would
            // hide whatever the caller composites it over. SpaceGame is saved only because its one 3D path
            // (Render3DPreview) forces Starfield = false in its constructor, not because the combination is
            // itself safe.
            byte[] solidTransparent = RenderBoxScene(BackgroundMode.Solid, transparentBackground: true);
            byte[] starTransparent = RenderBoxScene(BackgroundMode.Starfield, transparentBackground: true);

            // Solid + TransparentBackground paints nothing at background pixels, so they stay at the cleared
            // transparent alpha. Proves the check below isn't just "everything reads opaque" under this path.
            float bgAlphaSolid = AverageAlphaOutsideBox(solidTransparent);
            Assert.True(bgAlphaSolid < 5f,
                $"Solid + TransparentBackground should leave background pixels transparent (avg alpha={bgAlphaSolid})");

            // The starfield pass writes alpha = 1 across the whole background, star or not, so every background
            // pixel must read fully opaque here.
            float bgAlphaStar = AverageAlphaOutsideBox(starTransparent);
            Assert.True(bgAlphaStar > 250f,
                $"Starfield should paint the background fully opaque (avg alpha={bgAlphaStar})");
        }
    }
}
