using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
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
    /// upscaling presets, still blades narrower than a texel on the ground, and a keyed pole crossing the view fast,
    /// whose share the ground beside it takes (the moving share), all run through both. The fact counts the marks and
    /// the moving shares the state stored to show it. It compares every frame of the walk and reports the colour, the
    /// confidence, the lock under the same mark and the mark apart, so a rounding difference in the values reads apart
    /// from a rule that decided otherwise. Through packHalf2x16 the values differed on every Vulkan device it ran on,
    /// and the locks with them, so both entries round in integer steps (<c>temporalHalf</c>). This fact is what shows
    /// they agree on a given backend: bit for bit on Metal, on a Tesla T4 on Direct3D 11 and Vulkan, and on WARP
    /// (TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23). On a software Vulkan device (Mesa llvmpipe) the colour
    /// differs, since its compiler orders the two programs' arithmetic differently, while the confidence, the lock
    /// and the mark stay identical. There the fact holds those three exact and the colour within
    /// <see cref="SoftwareColourBound"/> and <see cref="SoftwareColourShare"/>.
    /// </summary>
    public sealed class TemporalEntryIdentityGpuTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180, StopFrames = 8, MoverSweep = 12;
        const float Speed = 2f, BladeTexels = 0.375f, MoverTexels = 1f, MoverStep = 3f, MoverHeight = 3f,
            MoverNearer = 4f;
        const ulong MoverKey = 71;

        // The colour on a software Vulkan device: llvmpipe (LLVM 20.1.2) measured its largest difference 0.0049 at
        // Native, 0.0154 at Quality and 0.0439 at Performance, over 0.010, 0.052 and 0.118 percent of the values
        // compared. The bounds keep about 1.4 and 2 times that.
        const float SoftwareColourBound = 0.0625f, SoftwareColourShare = 0.0025f;

        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        [InlineData(TemporalUpscale.Performance)]
        public void Both_entry_points_write_the_same_history_on_every_frame(TemporalUpscale preset)
            => Compare(preset, outline: false);

        /// <summary>With the edge outline on, both entry points read the outlined images the pass ahead of the
        /// resolve writes, so they still write the same history. The outline is magenta, a colour nothing else in the
        /// walk has, and the same walk with the outline off is the control: the history must hold the outline's
        /// colour where the control holds next to none of it, so the fact shows the pass drew.</summary>
        [GpuFact]
        public void Both_entry_points_write_the_same_history_with_the_edge_outline_on()
        {
            int without = Compare(TemporalUpscale.Quality, outline: false);
            int with = Compare(TemporalUpscale.Quality, outline: true);
            output.WriteLine($"  outline-coloured history pixels on the last frame: {with} with the outline, "
                + $"{without} without");
            Assert.True(with >= MinOutlinePixels && with >= OutlineOverControl * Math.Max(1, without),
                $"the outline left {with} outline-coloured history pixels against {without} without it");
        }

        // The outline's colour and how much of it the history must hold over the control. First estimates, printed.
        static readonly Color OutlineColour = new(1f, 0f, 1f, 1f);
        const int MinOutlinePixels = 100, OutlineOverControl = 4;

        // A history pixel in the outline's colour: red and blue each well above green.
        static bool IsOutline(float[] rgba, int i) => rgba[i] > rgba[i + 1] + 0.3f && rgba[i + 2] > rgba[i + 1] + 0.3f;

        // The walk forced to each entry point. Returns the outline-coloured pixels of the fused history the last frame
        // wrote.
        int Compare(TemporalUpscale preset, bool outline)
        {
            // A walk each, since a walk holds the meshes its scene loaded.
            var fusedWalk = new PerspectiveFollowLines(W, H, Speed, preset, BladeTexels);
            var splitWalk = new PerspectiveFollowLines(W, H, Speed, preset, BladeTexels);
            using var fused = new TemporalFixture(W, H, s => Setup(s, fusedWalk, preset, outline));
            using var split = new TemporalFixture(W, H, s => Setup(s, splitWalk, preset, outline));
            fused.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Fused;
            split.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
            output.WriteLine($"{fused.Device.Backend} on {fused.Device.Capabilities.DeviceName}, {W}x{H} {preset}"
                + (outline ? ", the edge outline on" : ""));
            bool softwareVulkan = fused.Device.Backend == GpuBackendKind.VulkanNative
                && fused.Device.Diagnostics.SoftwareAdapter == true;
            long colourCompared = 0;

            int frames = TemporalFollowLinesRuns.Last + 1 + StopFrames, compared = 0, band = 0, followed = 0,
                movingShare = 0, outlinePixels = 0;
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
                // At Native the split records its second pass compiled without the display-sized reconstruction,
                // and the fused resolve keeps it, so this walk also holds that program to the rule's own output.
                Assert.Equal(ShaderSources.TemporalAccumulateFragment(TemporalResolvePrecisionPolicy.For(split.Device),
                        upscales: preset != TemporalUpscale.Native),
                    split.Scene.TemporalResolveRendererForTests!.LastEntryFragmentsForTests()[1]);
                float[] a = Read(fused, static h => h.Color(h.WriteIndex));
                float[] b = Read(split, static h => h.Color(h.WriteIndex));
                for (int i = 0; i < a.Length; i++) colour.Add(n, a[i], b[i]);
                colourCompared += a.Length;
                outlinePixels = 0;
                for (int i = 0; i < a.Length; i += 4) if (IsOutline(a, i)) outlinePixels++;
                float[] state = Read(fused, static h => h.Confidence(h.WriteIndex));
                float[] other = Read(split, static h => h.Confidence(h.WriteIndex));
                for (int i = 0; i < state.Length; i += 2)
                {
                    confidence.Add(n, state[i], other[i]);
                    if (state[i] == TemporalResolveTuning.MovingShareConfidence) movingShare++;
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
            output.WriteLine($"  {compared} of {frames} frames resolved, {band} band marks, {followed} followed marks "
                + $"and {movingShare} moving shares stored by the fused entry");
            string differences = string.Join("\n", new[] { colour, confidence, locks, marks }.Select(d => "  " + d));
            output.WriteLine(differences);
            Assert.True(compared > TemporalFollowLinesRuns.Last, $"only {compared} frames resolved");
            Assert.Equal(outline, fused.Scene.TemporalOutlineBuiltForTests);
            Assert.Equal(outline, split.Scene.TemporalOutlineBuiltForTests);
            Assert.True(confidence.Values + locks.Values + marks.Values == 0,
                $"the entry points stored different state:\n{differences}");
            if (softwareVulkan)
            {
                double share = (double)colour.Values / colourCompared;
                output.WriteLine($"  software Vulkan: colour within {SoftwareColourBound} on {share:P3} of values");
                Assert.True(colour.Worst <= SoftwareColourBound && share <= SoftwareColourShare,
                    $"the colour on a software Vulkan device left its bound ({SoftwareColourBound}, "
                    + $"{SoftwareColourShare:P2} of values):\n{differences}");
            }
            else Assert.True(colour.Values == 0, $"the entry points wrote different histories:\n{differences}");
            // The band reaches two internal texels beside the followed box, which this walk stores only while
            // upscaling.
            Assert.True(followed > 0 && (band > 0 || preset == TemporalUpscale.Native) && movingShare > 0,
                $"the walk stored {band} band, {followed} followed marks and {movingShare} moving shares");
            return outlinePixels;
        }

        static void Setup(Scene3D s, PerspectiveFollowLines walk, TemporalUpscale preset, bool outline)
        {
            walk.Setup(s, preset);
            s.Post.Outline = outline;
            s.Post.OutlineColor = OutlineColour;
        }

        // The walk's last frame is its last step, so the frames after it hold the box and the camera still. The pole
        // keeps moving through them.
        static Action<Scene3D, int> Draw(PerspectiveFollowLines walk) => (s, n) =>
        {
            walk.Draw(s, Math.Min(n, TemporalFollowLinesRuns.Last), true, true);
            DrawMover(s, walk, n);
        };

        // A keyed pole an internal texel wide where the box stands, MoverNearer metres nearer the camera, sweeping
        // across the view and back at MoverStep internal texels a frame there. The resolve reprojects the ground
        // beside it by the ground's own motion and gives its history the pole's share (narrowMoving).
        static void DrawMover(Scene3D s, PerspectiveFollowLines walk, int n)
        {
            int phase = n % (2 * MoverSweep);
            float across = (Math.Min(phase, 2 * MoverSweep - phase) - MoverSweep / 2f) * MoverStep * walk.TexelMetres;
            float width = MoverTexels * walk.TexelMetres;
            Vector3 foot = walk.Walk.Foot(Math.Min(n, TemporalFollowLinesRuns.Last)) - GroundStage.Forward * MoverNearer
                + GroundStage.Right * (1f + across);
            s.Draw(new RigidInstanceDraw(walk.Walk.Stage.Box,
                walk.Walk.Stage.Standing(foot, new Vector3(width, MoverHeight, width)))
            {
                Tint = new Color(0.1f, 0.1f, 0.12f, 1f),
                Motion = MotionKey.From(MoverKey),
            });
        }

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
