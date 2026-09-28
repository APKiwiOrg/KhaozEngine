using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The confidence and stability target's green channel as step 6 reads it back
    /// (<see cref="TemporalFormats.HistoryConfidence"/>): where the surface the pixel reprojected by moved, the resolve
    /// stores minus one minus the lock, minus three minus the lock where the pixel followed a nearer surface's edge
    /// that moved (the band, <see cref="TemporalResolveTuning.WorldMotionMetres"/>), and minus five minus the lock on
    /// the followed surface itself, and the next frame must read the lock unchanged wherever it keeps the history. A
    /// still pixel whose nearest surface did not move drops a history that carries the band. Same rig and still camera
    /// as <see cref="TemporalResolveLockGpuTests"/>, through
    /// <see cref="TemporalResolveGpuFacts"/>.
    /// </summary>
    public sealed class TemporalStoredLockGpuTests : TemporalResolveGpuFacts
    {
        [GpuFact]
        public void A_lock_stored_where_a_moving_surface_showed_reads_back_unchanged()
        {
            // A held line's lock of 0.8 stored for a moving surface is Q(-1.8) = -1.79980, which reads back as
            // -1 - (-1.79980) = 0.79980 and decays by LockDecay to 0.67480. Its hold, twice that, stays whole, so the
            // history keeps the line's luma against the flat box. A decode that read the moved texel as no lock would
            // store 0 and clip the line to the background. Nothing moves this frame, so the lock is stored plain.
            float stored = Q(-1.8f);
            float lockAfter = -1f - stored - TemporalResolveTuning.LockDecay;
            Assert.Equal(0.67480f, lockAfter, 1e-5);
            HeldLineKeepsItsLock(stored, lockAfter);
        }

        [GpuFact]
        public void A_lock_stored_by_a_band_pixel_reads_back_unchanged_where_its_history_is_kept()
        {
            // The same lock stored by a band pixel is Q(-3.8) = -3.80078, which reads back as -3 - (-3.80078) = 0.80078
            // and decays to 0.67578. The stored depths lie farther than the scene, as a nearer surface's edge finds the
            // band where it was, so every one of them is farther and the history is kept. A decode that read it as a
            // plain moved texel would take 2.80078 as the lock, a whole one, and store 0.875.
            float stored = Q(-3.8f);
            float lockAfter = -3f - stored - TemporalResolveTuning.LockDecay;
            Assert.Equal(0.67578f, lockAfter, 1e-5);
            HeldLineKeepsItsLock(stored, lockAfter, SceneLinear * 1.5f);
        }

        [GpuFact]
        public void A_still_pixel_clear_of_any_edge_drops_the_history_a_band_pixel_stored()
        {
            // The band's history followed a nearer surface's edge over this one. Here the stored depths are the scene's
            // own and nothing in the 3x3 lies farther, so the pixel is the farther surface clear of that edge, and it
            // restarts from the flat background: its colour is the background's, its confidence one sample's, and its
            // lock, with no ridge this frame, 0 stored plain. Keeping it would show the held line's luma.
            float[] zero = Motion((_, _) => Vector2.Zero);
            using var rig = new Rig();
            float bg = Q(0.1f), held = Q(0.55f);
            rig.BeginFrame();
            rig.Fill(Grey((_, _) => bg), Grey((_, _) => bg), zero);
            rig.FillHistory(Grey((x, _) => x == 4 ? held : bg),
                State((x, _) => new Vector2(Q(8f / Max), x == 4 ? Q(-3.8f) : 0f)), SceneLinear);
            rig.Resolve(Uniforms(historyValid: true));

            float[] color = rig.ReadColor(), state = rig.ReadState();
            Assert.Equal(bg, color[At(4, 3) * 4], 1e-3);
            Assert.Equal(1f / Max, state[At(4, 3) * 2], 1e-3);
            Assert.Equal(0f, state[At(4, 3) * 2 + 1], 1e-4);
        }

        // A held line at column 4 over a flat background, its state holding the stored lock and its stored depths at
        // storedDepth, the scene's own unless given, resolved once with no motion: the lock the pixel stores and the
        // line's luma it keeps.
        static void HeldLineKeepsItsLock(float stored, float lockAfter, float? storedDepth = null)
        {
            float[] zero = Motion((_, _) => Vector2.Zero);
            using var rig = new Rig();
            float bg = Q(0.1f), held = Q(0.55f);
            rig.BeginFrame();
            rig.Fill(Grey((_, _) => bg), Grey((_, _) => bg), zero);
            rig.FillHistory(Grey((x, _) => x == 4 ? held : bg),
                State((x, _) => new Vector2(Q(8f / Max), x == 4 ? stored : 0f)), storedDepth ?? SceneLinear);
            rig.Resolve(Uniforms(historyValid: true));

            float accumulated = Q(8f / Max) * Max;
            float c = Weighted(bg), h = Weighted(held);
            float expected = Unweighted(h + (c - h) / (accumulated + 1f));
            float[] color = rig.ReadColor(), state = rig.ReadState();
            Assert.Equal(Q(lockAfter), state[At(4, 3) * 2 + 1], 1e-4);
            Assert.Equal(expected, color[At(4, 3) * 4], 1e-2);
            Assert.Equal(bg, color[At(2, 3) * 4], 1e-3);
        }
    }
}
