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
    /// differs, since its compiler orders the two programs' arithmetic differently, while the confidence, the lock,
    /// the mark, R8 and depth stay identical. There the fact holds those exact and the colour within
    /// <see cref="TemporalSoftwareCompileDivergenceRule.AbsoluteBound"/> and
    /// <see cref="TemporalSoftwareCompileDivergenceRule.DifferingShareBound"/> of the values compared. Everywhere else
    /// stored colour, confidence, locks, R8 and depth are exact. Hardware NVIDIA Direct3D 11 additionally prints each
    /// production entry and D's paired entries against D. Against D only the non-finite classes of colour and
    /// confidence are asserted. The large-frame case (<c>TemporalEntryIdentityGpuTests.LargeFrames.cs</c>) holds both
    /// entries, and D prime's two, byte-identical across columns 512 and 1024 and row 512 under positive jitter,
    /// under the same software Vulkan colour rule.
    /// </summary>
    public sealed partial class TemporalEntryIdentityGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
    {
        const int W = 320, H = 180, StopFrames = 8, MoverSweep = 12;
        const float Speed = 2f, BladeTexels = 0.375f, MoverTexels = 1f, MoverStep = 3f, MoverHeight = 3f,
            MoverNearer = 4f;
        const ulong MoverKey = 71;

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
            bool softwareVulkan = TemporalDeviceClass.IsLavapipe(fused.Device);
            bool nvidia = TemporalDeviceClass.IsHardwareNvidiaD3D11(fused.Device);
            var oracle = nvidia ? new TemporalNvidiaHistoryOracle(output) : null;
            var dFusedWalk = new PerspectiveFollowLines(W, H, Speed, preset, BladeTexels);
            var dSplitWalk = new PerspectiveFollowLines(W, H, Speed, preset, BladeTexels);
            using var dFused = nvidia ? new TemporalFixture(W, H, s => Setup(s, dFusedWalk, preset, outline)) : null;
            using var dSplit = nvidia ? new TemporalFixture(W, H, s => Setup(s, dSplitWalk, preset, outline)) : null;
            if (dFused is not null && dSplit is not null)
            {
                TemporalTestResolve.Install(dFused, TemporalVisibilityFrozenD.ToD);
                TemporalTestResolve.Install(dSplit, TemporalVisibilityFrozenD.ToD);
                dFused.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Fused;
                dSplit.Scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
            }
            long colourCompared = 0;

            int frames = TemporalFollowLinesRuns.Last + 1 + StopFrames, compared = 0, band = 0, followed = 0,
                movingShare = 0, outlinePixels = 0;
            var colour = new EntryDifference("colour");
            var confidence = new EntryDifference("confidence");
            var locks = new EntryDifference("lock");
            var marks = new EntryDifference("mark", false);
            Vector2 previousJitter = default;
            for (int n = 0; n < frames; n++)
            {
                fused.Frame(Draw(fusedWalk));
                split.Frame(Draw(splitWalk));
                dFused?.Frames(1, Draw(dFusedWalk));
                dSplit?.Frames(1, Draw(dSplitWalk));
                if (!fused.Scene.ResolvedLastRenderForTests) continue;
                Assert.True(split.Scene.ResolvedLastRenderForTests, $"frame {n}: only the fused scene resolved");
                Assert.Equal(TemporalResolveEntry.Fused, fused.Scene.TemporalResolveRendererForTests?.LastEntry);
                Assert.Equal(TemporalResolveEntry.Split, split.Scene.TemporalResolveRendererForTests?.LastEntry);
                // At Native the split records its second pass compiled without the display-sized reconstruction,
                // and the fused resolve keeps it, so this walk also holds that program to the rule's own output.
                string splitProgram = ShaderSources.TemporalAccumulateFragment(
                    TemporalResolvePrecisionPolicy.For(split.Device), upscales: preset != TemporalUpscale.Native);
                if (split.Scene.TemporalResolveRendererForTests!.LastVisibilityDecisionSkipped)
                    splitProgram = TemporalVisibilityPrograms.WithoutDecision(splitProgram);
                Assert.Equal(TemporalVisibilityGatherPolicy.ForBackend(splitProgram, split.Device.Backend),
                    split.Scene.TemporalResolveRendererForTests.LastEntryFragmentsForTests()[1]);
                var fusedRenderer = fused.Scene.TemporalResolveRendererForTests!;
                var splitRenderer = split.Scene.TemporalResolveRendererForTests!;
                Assert.Equal(fusedRenderer.LastVisibilityUniforms.PreviousJitter,
                    splitRenderer.LastVisibilityUniforms.PreviousJitter);
                Assert.Equal(new Vector4(previousJitter, n > 0 ? 1f : 0f, 0f),
                    fusedRenderer.LastVisibilityUniforms.PreviousJitter);
                Assert.Equal(TemporalVisibilityHistoryGpuTests.Read(fused.Device,
                        fusedRenderer.VisibilityHistory.Texture(fused.Scene.TemporalHistory.WriteIndex)),
                    TemporalVisibilityHistoryGpuTests.Read(split.Device,
                        splitRenderer.VisibilityHistory.Texture(split.Scene.TemporalHistory.WriteIndex)));
                previousJitter = new Vector2(fusedRenderer.LastUniforms.Jitter.X, fusedRenderer.LastUniforms.Jitter.Y);
                float[] a = Read(fused, static h => h.Color(h.WriteIndex));
                float[] b = Read(split, static h => h.Color(h.WriteIndex));
                for (int i = 0; i < a.Length; i++) colour.Add(n, a[i], b[i]);
                colourCompared += a.Length;
                outlinePixels = 0;
                for (int i = 0; i < a.Length; i += 4) if (IsOutline(a, i)) outlinePixels++;
                float[] state = Read(fused, static h => h.Confidence(h.WriteIndex));
                float[] other = Read(split, static h => h.Confidence(h.WriteIndex));
                TemporalHistorySample.Exact(n, "production entries depth",
                    Read(fused, static h => h.PreviousDepth(h.WriteIndex)),
                    Read(split, static h => h.PreviousDepth(h.WriteIndex)));
                if (dFused is not null && dSplit is not null)
                {
                    TemporalHistorySample df = TemporalHistorySample.Read(dFused), ds = TemporalHistorySample.Read(dSplit);
                    oracle!.ObserveD(n, df, ds);
                    TemporalHistorySample pf = TemporalHistorySample.Read(fused), ps = TemporalHistorySample.Read(split);
                    oracle.Report(n, "production fused vs D fused", pf, df);
                    oracle.Report(n, "production split vs D split", ps, ds);
                }
                if (!softwareVulkan) TemporalHistorySample.Exact(n, "production entries colour", a, b);
                TemporalHistorySample.Exact(n, "production entries state", state, other);
                for (int i = 0; i < state.Length; i += 4)
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
                double share = TemporalSoftwareCompileDivergenceRule.ShareOf(colour.Values, colourCompared);
                output.WriteLine($"  software Vulkan: colour within {TemporalSoftwareCompileDivergenceRule.AbsoluteBound} "
                    + $"on {share:P3} of values");
                Assert.True(TemporalSoftwareCompileDivergenceRule.Allows(colour.Worst, colour.Values, colourCompared),
                    $"the colour on a software Vulkan device left its bound "
                    + $"({TemporalSoftwareCompileDivergenceRule.AbsoluteBound}, "
                    + $"{TemporalSoftwareCompileDivergenceRule.DifferingShareBound:P2} of values):\n{differences}");
            }
            else Assert.True(colour.Values == 0, $"the entry points wrote different histories:\n{differences}");
            // The band reaches two internal texels beside the followed box, which this walk stores only while
            // upscaling.
            Assert.True(followed > 0 && (band > 0 || preset == TemporalUpscale.Native) && movingShare > 0,
                $"the walk stored {band} band, {followed} followed marks and {movingShare} moving shares");
            return outlinePixels;
        }

        // Uploaded shadow edges isolate the visibility response from shadow-map rasterization. The outlined row
        // runs the production edge pass on both scene inputs before the same release and retained-history assertions.
        [GpuTheory]
        [InlineData(TemporalUpscale.Native, 8, false)]
        [InlineData(TemporalUpscale.Quality, 12, false)]
        [InlineData(TemporalUpscale.Performance, 16, false)]
        [InlineData(TemporalUpscale.Quality, 12, true)]
        public void MovingShadowEntriesKeepVisibilityDecisionsAndHistoryExact(TemporalUpscale preset, int display,
            bool outline)
        {
            var settings = new PixelPostProcessSettings();
            SetupOutline(settings, outline);
            using var fused = new TemporalResolveRig(N, N, display, display, SceneNdc, outline ? settings : null);
            using var split = new TemporalResolveRig(N, N, display, display, SceneNdc, outline ? settings : null);
            using var unlocked = new TemporalResolveRig(N, N, display, display, SceneNdc, outline ? settings : null);
            bool nvidia = TemporalDeviceClass.IsHardwareNvidiaD3D11(fused.Device);
            var oracle = nvidia ? new TemporalNvidiaHistoryOracle(output) : null;
            using var dFused = nvidia ? new TemporalResolveRig(N, N, display, display, SceneNdc,
                outline ? settings : null, TemporalVisibilityFrozenD.ToD) : null;
            using var dSplit = nvidia ? new TemporalResolveRig(N, N, display, display, SceneNdc,
                outline ? settings : null, TemporalVisibilityFrozenD.ToD) : null;
            using GpuRetireQueue retired = GpuRetireQueue.Create(split.Device);
            split.Renderer.SelectEntry(TemporalResolveEntry.Split, retired);
            using GpuRetireQueue? dRetired = dSplit is null ? null : GpuRetireQueue.Create(dSplit.Device);
            dSplit?.Renderer.SelectEntry(TemporalResolveEntry.Split, dRetired!);
            using IGpuTexture a = VisibilitySignal(fused.Device);
            using IGpuTexture b = VisibilitySignal(split.Device);
            using IGpuTexture? da = dFused is null ? null : VisibilitySignal(dFused.Device);
            using IGpuTexture? db = dSplit is null ? null : VisibilitySignal(dSplit.Device);
            (TemporalResolveRig Rig, IGpuTexture Signal)[] entries = dFused is null || dSplit is null
                ? [(fused, a), (split, b)] : [(fused, a), (split, b), (dFused, da!), (dSplit, db!)];
            output.WriteLine($"{fused.Device.Backend} on {fused.Device.Capabilities.DeviceName}, moving shadow {preset}, "
                + $"outline {outline}");
            int[] edges = [8, 8, 2, 8, 0, 8];
            byte[] previous = [];
            Vector2 previousJitter = default;
            int rises = 0, falls = 0;
            int pixel = (display / 2 * display + display / 4) * 4;
            for (int frame = 0; frame < edges.Length; frame++)
            {
                byte[] bytes = Enumerable.Range(0, N * N).Select(i => i % N >= edges[frame] ? (byte)255 : (byte)0).ToArray();
                Vector2 jitter = new(frame % 2 == 0 ? 0.125f : -0.25f, 0.0625f);
                var uniforms = TemporalResolveMath.BuildUniforms(Still, Still, jitter, N, N, display, display, frame > 0);
                foreach ((TemporalResolveRig rig, IGpuTexture signal) in entries)
                {
                    rig.Device.UpdateTexture(signal, bytes, 0, 0, N, N);
                    float[] current = Grey((x, _) => x == 1 ? 0.7f : 0.2f);
                    rig.Fill(current, current, Motion((_, _) => Vector2.Zero));
                    rig.BeginFrame();
                    if (frame > 0)
                        rig.FillHistory(Grey(display, display, (_, _) => 0.4f),
                            States(display, display, (_, _) => Vector2.One), SceneLinear);
                    rig.Resolve(uniforms, signal);
                    Assert.Equal(outline, rig.OutlineBuilt);
                    if (outline)
                    {
                        float[] outlined = rig.ReadColor();
                        int outlinePixels = Enumerable.Range(0, display * display).Count(i => IsOutline(outlined, i * 4));
                        Assert.True(outlinePixels > 0, $"frame {frame}: the outline contributed no history pixels");
                        output.WriteLine($"  {rig.Renderer.LastEntry} frame {frame}: {outlinePixels} outline history pixels");
                    }
                    Assert.Equal(bytes, TemporalVisibilityHistoryGpuTests.Read(rig.Device,
                        rig.Renderer.VisibilityHistory.Texture(rig.History.WriteIndex)));
                    Assert.Equal(new Vector4(previousJitter, frame > 0 ? 1f : 0f, 0f),
                        rig.Renderer.LastVisibilityUniforms.PreviousJitter);
                    if (frame > 0)
                        Assert.Equal(previous, TemporalVisibilityHistoryGpuTests.Read(rig.Device,
                            rig.Renderer.VisibilityHistory.Texture(rig.History.ReadIndex)));
                    rig.History.MarkValidAfterFrame();
                }
                // A matched-jitter control has full history with no lock or visibility signal. It isolates
                // retained weight and display reconstruction from the clipping change caused by lock release.
                float[] controlCurrent = Grey((x, _) => x == 1 ? 0.7f : 0.2f);
                unlocked.Fill(controlCurrent, controlCurrent, Motion((_, _) => Vector2.Zero));
                unlocked.BeginFrame();
                if (frame > 0)
                    unlocked.FillHistory(Grey(display, display, (_, _) => 0.4f),
                        States(display, display, (_, _) => new Vector2(1, 0)), SceneLinear);
                unlocked.Resolve(uniforms);
                unlocked.History.MarkValidAfterFrame();
                TemporalHistorySample pf = TemporalHistorySample.Read(fused), ps = TemporalHistorySample.Read(split);
                if (dFused is not null && dSplit is not null)
                {
                    TemporalHistorySample df = TemporalHistorySample.Read(dFused), ds = TemporalHistorySample.Read(dSplit);
                    oracle!.ObserveD(frame, df, ds);
                    oracle.Report(frame, "production fused vs D fused", pf, df);
                    oracle.Report(frame, "production split vs D split", ps, ds);
                }
                TemporalHistorySample.Exact(frame, "production entries", pf, ps);
                if (frame == 0)
                {
                    Assert.InRange(fused.ReadState()[pixel], 0.01f, 0.15f);
                }
                if (frame == 1)
                {
                    Assert.Equal(1, fused.ReadState()[pixel]);
                    Assert.Equal(0.875f, fused.ReadState()[pixel + 1]);
                }
                if (frame >= 2)
                {
                    float[] state = fused.ReadState();
                    Assert.Equal(0, state[pixel + 1]);
                    Assert.Equal(1, state[pixel]);
                    Assert.Equal(1, unlocked.ReadState()[pixel]);
                    float[] colour = fused.ReadColor(), retained = unlocked.ReadColor();
                    for (int c = 0; c < 4; c++) Assert.Equal(retained[pixel + c], colour[pixel + c]);
                    if (edges[frame] == 8) falls++;
                    else rises++;
                    output.WriteLine($"  frame {frame}: confidence {state[pixel]:R}, lock {state[pixel + 1]:R}");
                }
                previous = bytes;
                previousJitter = jitter;
            }
            Assert.Equal(2, rises);
            Assert.Equal(2, falls);
            output.WriteLine($"  {edges.Length} frames exact in colour and confidence, "
                + "exact locks, marks, R8 bytes, depth and previous jitter");
        }

        static IGpuTexture VisibilitySignal(IGpuDevice device) => device.Factory.CreateTexture(
            GpuTextureDescription.Texture2D(N, N, GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));

        static void Setup(Scene3D s, PerspectiveFollowLines walk, TemporalUpscale preset, bool outline)
        {
            walk.Setup(s, preset);
            SetupOutline(s.Post, outline);
        }

        static void SetupOutline(PixelPostProcessSettings settings, bool outline)
        {
            settings.Outline = outline;
            settings.OutlineColor = OutlineColour;
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
