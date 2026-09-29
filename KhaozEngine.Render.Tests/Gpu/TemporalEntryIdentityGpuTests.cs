using System;
using System.Linq;
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
    /// after every frame the history colour and state each wrote are read back and compared. The walk holds still,
    /// walks and stops, so the followed mark and its keep on the stop, the band beside the followed box at the
    /// upscaling presets, and still blades narrower than a texel on the ground all run through both, and the fact
    /// counts the marks the state stored to show it. Nothing in the shaders is <c>precise</c>, so a compiler could
    /// round the fused pass's inline preparation and the split's stored one differently. This fact is what shows it
    /// does not on a given backend (TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23). It compares every frame of the
    /// walk and reports the colour, the confidence, the lock under the same mark and the mark apart, so a rounding
    /// difference in the values reads apart from a rule that decided otherwise.
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
            var colour = new EntryDifference("colour");
            var confidence = new EntryDifference("confidence");
            var locks = new EntryDifference("lock");
            var marks = new EntryDifference("mark", false);
            for (int n = 0; n < frames; n++)
            {
                fused.Frame(Draw(fusedWalk));
                split.Frame(Draw(splitWalk));
                if (!fused.Scene.ResolvedLastRenderForTests) continue;
                Assert.True(split.Scene.ResolvedLastRenderForTests, $"frame {n}: only the fused scene resolved");
                Assert.Equal(TemporalResolveEntry.Fused, fused.Scene.TemporalResolveRendererForTests?.LastEntry);
                Assert.Equal(TemporalResolveEntry.Split, split.Scene.TemporalResolveRendererForTests?.LastEntry);
                float[] a = Read(fused, static h => h.Color(h.WriteIndex));
                float[] b = Read(split, static h => h.Color(h.WriteIndex));
                for (int i = 0; i < a.Length; i++) colour.Add(n, a[i], b[i]);
                float[] state = Read(fused, static h => h.Confidence(h.WriteIndex));
                float[] other = Read(split, static h => h.Confidence(h.WriteIndex));
                for (int i = 0; i < state.Length; i += 2)
                {
                    confidence.Add(n, state[i], other[i]);
                    // The second channel stores the lock under a mark (temporalStoreLock): a mark that differs is a
                    // rule that decided otherwise, a lock under the same mark that differs is arithmetic.
                    int mark = Mark(state[i + 1]);
                    if (mark == Mark(other[i + 1])) locks.Add(n, state[i + 1], other[i + 1]);
                    else marks.Add(n, mark, Mark(other[i + 1]));
                    if (mark == 3) followed++;
                    else if (mark == 2) band++;
                }
                compared++;
            }
            output.WriteLine($"  {compared} of {frames} frames resolved, {band} band marks and {followed} followed "
                + "marks stored by the fused entry");
            string differences = string.Join("\n", new[] { colour, confidence, locks, marks }.Select(d => "  " + d));
            output.WriteLine(differences);
            Assert.True(compared > TemporalFollowLinesRuns.Last, $"only {compared} frames resolved");
            Assert.True(colour.Values + confidence.Values + locks.Values + marks.Values == 0,
                $"the entry points wrote different histories:\n{differences}");
            // The band reaches two internal texels beside the followed box, which this walk stores only while
            // upscaling.
            Assert.True(followed > 0 && (band > 0 || preset == TemporalUpscale.Native),
                $"the walk stored {band} band and {followed} followed marks");
        }

        // The walk's last frame is its last step, so the frames after it hold the box and the camera still.
        static Action<Scene3D, int> Draw(PerspectiveFollowLines walk) =>
            (s, n) => walk.Draw(s, Math.Min(n, TemporalFollowLinesRuns.Last), true, true);

        static float[] Read(TemporalFixture fixture, Func<TemporalHistory, IGpuTexture> target) =>
            TemporalTextureIo.Read(fixture.Device, target(fixture.Scene.TemporalHistory));

        // The mark a stored lock lies under: 0 none, 1 moved, 2 the band, 3 the followed surface's own.
        static int Mark(float stored) => stored < -4.5f ? 3 : stored < -2.5f ? 2 : stored < -0.5f ? 1 : 0;

        // The half float's steps between two values it holds, zero for the two zeros.
        static int HalfSteps(float a, float b) => Math.Abs(Ordinal(a) - Ordinal(b));

        static int Ordinal(float v)
        {
            short bits = BitConverter.HalfToInt16Bits((Half)v);
            return bits < 0 ? -(bits & 0x7fff) : bits;
        }

        // Every value that differs between the entries over the walk: how many, on how many frames from which, and
        // the largest difference, in value and in half-float steps.
        sealed class EntryDifference(string what, bool halfFloat = true)
        {
            public int Values, Frames, FirstFrame = -1, WorstSteps;
            public float Worst;
            int _lastFrame = -1;

            public void Add(int frame, float a, float b)
            {
                if (BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b)) return;
                Values++;
                if (frame != _lastFrame) Frames++;
                _lastFrame = frame;
                if (FirstFrame < 0) FirstFrame = frame;
                Worst = MathF.Max(Worst, MathF.Abs(a - b));
                if (halfFloat) WorstSteps = Math.Max(WorstSteps, HalfSteps(a, b));
            }

            public override string ToString() => Values == 0 ? $"{what}: none differ"
                : $"{what}: {Values} values differ on {Frames} frames from frame {FirstFrame}, the largest by {Worst}"
                    + (halfFloat ? $" ({WorstSteps} half-float steps)" : " levels");
        }
    }
}
