using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE TWO ENTRY POINTS WRITE THE SAME HISTORY, bit for bit, on the backend and shader compiler under test. Two
    /// scenes render the same perspective follow walk, one forced to the fused entry point and one to the split, and
    /// after every frame the history colour and state each wrote are read back and compared. The walk holds still, walks
    /// and stops, so the followed mark and its keep on the stop, the band beside the followed box at the upscaling
    /// presets, and still blades narrower than a texel on the ground all run through both, and the fact counts the
    /// marks the state stored to show it. Nothing in the shaders is <c>precise</c>, so a
    /// compiler could round the fused pass's inline preparation and the split's stored one differently. This fact is
    /// what shows it does not on a given backend (TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23).
    /// </summary>
    public sealed class TemporalEntryIdentityGpuTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180, StopFrames = 8;
        const float Speed = 2f, BladeTexels = 0.375f;

        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        [InlineData(TemporalUpscale.Performance)]
        public void Both_entry_points_write_the_same_history_on_every_frame(TemporalUpscale preset)
        {
            // A walk each, since a walk holds the meshes its scene loaded.
            var fusedWalk = new PerspectiveFollowLines(W, H, Speed, preset, BladeTexels);
            var splitWalk = new PerspectiveFollowLines(W, H, Speed, preset, BladeTexels);
            using var fused = new TemporalFixture(W, H, s => fusedWalk.Setup(s, preset));
            using var split = new TemporalFixture(W, H, s => splitWalk.Setup(s, preset));
            fused.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Fused;
            split.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
            output.WriteLine($"{fused.Device.Backend} on {fused.Device.Capabilities.DeviceName}, {W}x{H} {preset}");

            int frames = TemporalFollowLinesRuns.Last + 1 + StopFrames, compared = 0, band = 0, followed = 0;
            for (int n = 0; n < frames; n++)
            {
                fused.Frame(Draw(fusedWalk));
                split.Frame(Draw(splitWalk));
                if (!fused.Scene.ResolvedLastRenderForTests) continue;
                Assert.True(split.Scene.ResolvedLastRenderForTests, $"frame {n}: only the fused scene resolved");
                Assert.Equal(TemporalResolveEntry.Fused, fused.Scene.TemporalResolveRendererForTests?.LastEntry);
                Assert.Equal(TemporalResolveEntry.Split, split.Scene.TemporalResolveRendererForTests?.LastEntry);
                Compare(n, "colour", fused, split, static h => h.Color(h.WriteIndex));
                float[] state = Compare(n, "state", fused, split, static h => h.Confidence(h.WriteIndex));
                // The state's second channel stores the lock under a mark: below -2.5 the band's, below -4.5 the
                // followed surface's own (ShaderSources.TemporalAccumulateGlsl, temporalStoreLock).
                for (int i = 1; i < state.Length; i += 2)
                {
                    if (state[i] < -4.5f) followed++;
                    else if (state[i] < -2.5f) band++;
                }
                compared++;
            }
            output.WriteLine($"  {compared} of {frames} frames resolved, every history texel the same on both, "
                + $"{band} band marks and {followed} followed marks stored");
            Assert.True(compared > TemporalFollowLinesRuns.Last, $"only {compared} frames resolved");
            // The band reaches two internal texels beside the followed box, which this walk stores only while
            // upscaling.
            Assert.True(followed > 0 && (band > 0 || preset == TemporalUpscale.Native),
                $"the walk stored {band} band and {followed} followed marks");
        }

        // The walk's last frame is its last step, so the frames after it hold the box and the camera still.
        static Action<Scene3D, int> Draw(PerspectiveFollowLines walk) =>
            (s, n) => walk.Draw(s, Math.Min(n, TemporalFollowLinesRuns.Last), true, true);

        // Both entries' target bit for bit, and the fused one's values.
        static float[] Compare(int frame, string what, TemporalFixture fused, TemporalFixture split,
            Func<TemporalHistory, IGpuTexture> target)
        {
            float[] a = TemporalTextureIo.Read(fused.Device, target(fused.Scene.TemporalHistory));
            float[] b = TemporalTextureIo.Read(split.Device, target(split.Scene.TemporalHistory));
            Assert.Equal(a.Length, b.Length);
            int differ = 0, first = -1;
            float worst = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(a[i]) == BitConverter.SingleToInt32Bits(b[i])) continue;
                if (first < 0) first = i;
                differ++;
                worst = MathF.Max(worst, MathF.Abs(a[i] - b[i]));
            }
            Assert.True(differ == 0, $"frame {frame}: {differ} of {a.Length} history {what} values differ between the "
                + $"entry points, the first at value {first}, the largest by {worst}");
            return a;
        }
    }
}
