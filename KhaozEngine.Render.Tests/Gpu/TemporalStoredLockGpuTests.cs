using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The confidence and stability target's green channel as step 6 reads it back
    /// (<see cref="TemporalFormats.HistoryConfidence"/>): where the pixel's dilated nearest surface moved, the resolve
    /// stores minus one minus the lock, and the next frame must read the lock unchanged. Same rig and still camera as
    /// <see cref="TemporalResolveLockGpuTests"/>, through <see cref="TemporalResolveGpuFacts"/>.
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
            float[] zero = Motion((_, _) => Vector2.Zero);
            using var rig = new Rig();
            float bg = Q(0.1f), held = Q(0.55f), stored = Q(-1.8f);
            rig.BeginFrame();
            rig.Fill(Grey((_, _) => bg), Grey((_, _) => bg), zero);
            rig.FillHistory(Grey((x, _) => x == 4 ? held : bg),
                State((x, _) => new Vector2(Q(8f / Max), x == 4 ? stored : 0f)), SceneLinear);
            rig.Resolve(Uniforms(historyValid: true));

            float lockAfter = -1f - stored - TemporalResolveTuning.LockDecay;
            Assert.Equal(0.67480f, lockAfter, 1e-5);
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
