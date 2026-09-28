using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Step 3's check of a history the followed surface's own pixels stored (the followed mark,
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23), on synthetic inputs: a pixel whose nearest surface did not move
    /// in the world drops that history only where it moved on screen more than
    /// <see cref="TemporalResolveTuning.FollowedStillDisplayPixels"/> and one of the nine current texels around its
    /// history position, background aside, moves on screen otherwise than the pixel reprojected, by more than
    /// <see cref="TemporalResolveTuning.FollowedHistoryMotionFraction"/> of that motion. Every state texel holds the
    /// followed mark over a lock of 0.8 and a confidence of 8, and every stored depth is the wall's own, so no other
    /// rule of step 3 drops the history. The confidence tells a kept history, one more than it stored, from a dropped
    /// one, one sample's. Same rig as <see cref="TemporalResolveEdgeCaseGpuTests"/>, through
    /// <see cref="TemporalResolveGpuFacts"/>.
    /// </summary>
    public sealed class TemporalFollowedHistoryGpuTests : TemporalResolveGpuFacts
    {
        const int W = 16, H = 12;
        const float Stored = 8f;

        static readonly Matrix4x4 Lens = Perspective((float)W / H);

        [GpuFact]
        public void A_followed_history_drops_only_where_a_texel_around_it_moves_otherwise_than_the_pixel()
        {
            // The camera pans so the still wall moves 3 pixels left, the motion the motion target writes for it, so
            // the resolve reads the wall as static and each pixel reads history 3 pixels right of it. Around Kept's
            // history position every texel moves with it, as the avatar's pixels do under a camera that eases on after
            // it stops. Beside Dropped's, one texel stays put on screen, as the avatar does while the camera follows it
            // over the ground it uncovers, 3 pixels otherwise, more than half the pixel's motion. Beside Background's
            // one texel is background, the motion sentinel, which is never the followed surface and is left out. Each
            // odd texel lies outside the tested pixel's own 3x3, so the pixel reprojects by the wall's motion.
            var kept = (X: 3, Y: 2);
            var dropped = (X: 3, Y: 6);
            var background = (X: 3, Y: 10);
            float step = 2f * (3f / W) * SceneMetres / Lens.M11;
            TemporalViewInput now = View(Eye, Lens), then = View(Eye - new Vector3(step, 0f, 0f), Lens);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(now, then, Vector2.Zero, W, H, W, H,
                historyValid: true);
            float ndc = NdcDepth(Lens, SceneMetres);
            float wall = TemporalResolveMath.LinearDepth(ndc, u.CurrentDepth);
            Vector2[] motion = CameraMotion(u, now, then, W, H, wall);
            Assert.InRange(-motion[dropped.Y * W + dropped.X].X * W, 2.95f, 3.05f);

            float[] state = Resolve(u, ndc, wall, (x, y) =>
                (x, y) == (dropped.X + 4, dropped.Y) ? Vector2.Zero
                : (x, y) == (background.X + 4, background.Y) ? new Vector2(MotionMath.Sentinel)
                : motion[y * W + x]);

            float cap = MotionCap(3f);
            Assert.True(Stored + 1f < cap, $"the cap {cap} binds");
            Assert.Equal((Stored + 1f) / Max, Confidence(state, kept), 1e-3);
            Assert.Equal(1f / Max, Confidence(state, dropped), 1e-3);
            Assert.Equal((Stored + 1f) / Max, Confidence(state, background), 1e-3);
        }

        [GpuFact]
        public void A_still_pixel_keeps_a_followed_history_beside_a_texel_passing_it_and_drops_a_band_history()
        {
            // The camera is still and a texel beside each tested pixel moves 1 pixel a frame, as a second avatar
            // walking past a stopped one. The followed pixel moved nothing on screen, as the stopped avatar's pixels,
            // so the passer moves otherwise by all of its motion and yet the history is kept. A band history over a
            // still pixel clear of any farther surface drops whatever moves beside it, as TemporalStoredLockGpuTests
            // shows with nothing moving. The passer lies last in each pixel's 3x3, so the flat wall's first texel
            // stays the dilated nearest.
            var followed = (X: 3, Y: 3);
            var band = (X: 10, Y: 7);
            float ndc = NdcDepth(Lens, SceneMetres);
            TemporalResolveUniforms u = TemporalResolveMath.BuildUniforms(View(Eye, Lens), View(Eye, Lens),
                Vector2.Zero, W, H, W, H, historyValid: true);
            float wall = TemporalResolveMath.LinearDepth(ndc, u.CurrentDepth);
            var passer = new Vector2(1f / W, 0f);

            float[] state = Resolve(u, ndc, wall, (x, y) =>
                (x, y) == (followed.X + 1, followed.Y + 1) || (x, y) == (band.X + 1, band.Y + 1) ? passer
                : Vector2.Zero,
                (x, y) => Math.Abs(x - band.X) <= 1 && Math.Abs(y - band.Y) <= 1 ? Q(-3.8f) : Q(-5.8f));

            Assert.Equal((Stored + 1f) / Max, Confidence(state, followed), 1e-3);
            Assert.Equal(1f / Max, Confidence(state, band), 1e-3);
        }

        // One resolve of the flat grey wall at its own depth, with the given motion, over a history whose every state
        // texel holds Stored and the given mark, the followed mark over a lock of 0.8 unless given.
        static float[] Resolve(in TemporalResolveUniforms u, float ndc, float wall, Func<int, int, Vector2> motion,
            Func<int, int, float>? mark = null)
        {
            mark ??= (_, _) => Q(-5.8f);
            float grey = Q(0.3f);
            using var rig = new Rig(W, H, W, H, ndc);
            rig.BeginFrame();
            rig.Fill(Grey(W, H, (_, _) => grey), Grey(W, H, (_, _) => grey), Pairs(W, H, motion));
            rig.FillHistory(Grey(W, H, (_, _) => grey), Pairs(W, H, (x, y) => new Vector2(Q(Stored / Max), mark(x, y))),
                wall);
            rig.Resolve(u);
            return rig.ReadState();
        }

        static float Confidence(float[] state, (int X, int Y) pixel) => state[(pixel.Y * W + pixel.X) * 2];
    }
}
