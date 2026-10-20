using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE PRODUCTION RESOLVE WRITES D'S HISTORY (plan Task 7f.1, proof 3). Four rigs take the same frames: the
    /// production renderer, a renderer whose every program is the program D shipped
    /// (<see cref="TemporalVisibilityFrozenD.ToD"/>, the first pass and the fused entry's 3x3 loop included) through the
    /// internal fragment source hook, a mutant D that never releases on visibility, and the production programs with
    /// their path bits exported (<see cref="TemporalVisibilityProductionPaths"/>). After every frame the production and
    /// D histories must be byte-identical in colour, state and stored visibility, and the exporting rig must write
    /// production's locks. The mutant must differ, which shows the hook reaches the compiled program and the frames
    /// exercise the release. The exported paths must reach minimum counts of fast-path and range-skip pixels, so the
    /// identity is shown on the production program's own fast path and skip. This is the one proof that covers the
    /// lock gate (R1), the pass-one mask (R3-lite) and the range skip (R4) at the call site together.
    /// <para>Two frame sets run. Random frames carry random colour with isolated ridges, two surfaces, random motion
    /// with background texels, moving R8 edges and random stored locks, a third of them exactly zero. Threshold frames
    /// alternate a uniform R8 level per 6 by 6 tile with a frame whose tile centre has c texels on a shared depth, the
    /// rest behind it, at levels that sum to c L plus or minus a designed amount over the tile's previous level L: 25.5
    /// c, which puts D's true change on 0.1 exactly for even c, one R8 lattice step either side of it, or either side of
    /// the R4 margin <c>0.1 - 2^-13</c>. Every texel there holds a lock and has no motion, so the fast path decides each
    /// tile centre at the release threshold.</para>
    /// </summary>
    public sealed class TemporalVisibilityResolveAbGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
    {
        const int Iw = 40, Ih = 24, Frames = 6;
        const int ThresholdIw = 96, ThresholdIh = 60, ThresholdTile = 6, ThresholdSite = 3;

        [GpuTheory]
        [InlineData(0, TemporalUpscale.Native)]
        [InlineData(0, TemporalUpscale.Quality)]
        [InlineData(1, TemporalUpscale.Native)]
        [InlineData(1, TemporalUpscale.Quality)]
        public void ProductionResolveWritesTheFrozenDHistoryEveryFrame(int choice, TemporalUpscale preset)
        {
            var entry = (TemporalResolveEntry)choice;
            var random = new Random(entry == TemporalResolveEntry.Fused ? 1709 : 2711);
            TemporalVisibilityProductionPaths paths = Run(entry, preset, Iw, Ih,
                (frame, dw, dh) => MakeFrame(random, frame, dw, dh, SceneNdc, NdcDepth(Projection, 1.4f), SceneLinear,
                    TemporalResolveMath.LinearDepth(NdcDepth(Projection, 1.4f), Depth)));
            // Floors under what the random frames give: a lock in two of three pixels, most centres off the border.
            Assert.True(paths.FastPath >= 50 && paths.RangeSkips >= 10 && paths.FastComputed >= 10,
                $"the random frames reached too few production paths: {paths}");
        }

        [GpuTheory]
        [InlineData(0, TemporalUpscale.Native)]
        [InlineData(0, TemporalUpscale.Quality)]
        [InlineData(1, TemporalUpscale.Native)]
        [InlineData(1, TemporalUpscale.Quality)]
        public void ThresholdTilesWriteTheFrozenDHistoryAtTheReleaseThreshold(int choice, TemporalUpscale preset)
        {
            var entry = (TemporalResolveEntry)choice;
            var random = new Random(entry == TemporalResolveEntry.Fused ? 3307 : 4409);
            var levels = new int[(ThresholdIw / ThresholdTile) * (ThresholdIh / ThresholdTile)];
            var designs = new int[4];
            TemporalVisibilityProductionPaths paths = Run(entry, preset, ThresholdIw, ThresholdIh,
                (frame, dw, dh) => MakeThresholdFrame(random, frame, dw, dh, levels, designs));
            output.WriteLine($"  designed tile centres: {designs[0]} on 0.1 exactly, {designs[1]} a lattice step either "
                + $"side, {designs[2]} below the R4 margin, {designs[3]} above it");
            // Each of the three designed frames holds 160 tile centres off the border, half of them on 0.1 exactly.
            Assert.True(paths.FastPath >= 1_000 && paths.RangeSkips >= 200 && paths.FastComputed >= 100
                && paths.FastReleased >= 20 && paths.FastNearThresholdAbove + paths.FastNearThresholdBelow >= 50,
                $"the threshold frames reached too few production paths at the threshold: {paths}");
        }

        // The four rigs over one frame set. Returns the production paths the exporting rig counted.
        TemporalVisibilityProductionPaths Run(TemporalResolveEntry entry, TemporalUpscale preset, int iw, int ih,
            Func<int, int, int, Frame> makeFrame)
        {
            int dw = preset == TemporalUpscale.Native ? iw : iw * 3 / 2, dh = preset == TemporalUpscale.Native ? ih : ih * 3 / 2;
            int dRewrites = 0, mutantRewrites = 0;
            bool prepareRewritten = false;
            using var production = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc);
            using var d = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc, fragmentSource: source =>
            {
                string rewritten = TemporalVisibilityFrozenD.ToD(source);
                if (!ReferenceEquals(rewritten, source)) dRewrites++;
                if (source == ShaderSources.TemporalPrepareFrag && rewritten != source) prepareRewritten = true;
                return rewritten;
            });
            using var mutant = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc, fragmentSource: source =>
            {
                string rewritten = TemporalVisibilityFrozenD.ToMutantD(source, callSite => callSite.Replace(
                    "> VisibilityReleaseThreshold", "> 2.0", StringComparison.Ordinal));
                if (!ReferenceEquals(rewritten, source)) mutantRewrites++;
                return rewritten;
            });
            using var exported = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc,
                fragmentSource: TemporalVisibilityProductionPaths.Rewrite);
            TemporalResolveRig[] rigs = [production, d, mutant, exported];
            var queues = new GpuRetireQueue[rigs.Length];
            var signals = new IGpuTexture[rigs.Length];
            for (int r = 0; r < rigs.Length; r++)
            {
                queues[r] = GpuRetireQueue.Create(rigs[r].Device);
                rigs[r].Renderer.SelectEntry(entry, queues[r]);
                signals[r] = rigs[r].Device.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)iw, (uint)ih,
                    GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
            }
            output.WriteLine($"{production.Device.Backend} on {production.Device.Capabilities.DeviceName}, {entry} {preset}, "
                + $"{iw}x{ih} internal, {dw}x{dh} display");
            var paths = new TemporalVisibilityProductionPaths();
            try
            {
                int phases = TemporalJitter.PhaseCount(TemporalResolveMath.DisplayOverInternal(dw, dh, iw, ih));
                long mutantDiffers = 0, zeroLocks = 0, values = 0;
                for (int frame = 0; frame < Frames; frame++)
                {
                    Frame f = makeFrame(frame, dw, dh);
                    zeroLocks += f.ZeroLocks;
                    Vector2 jitter = TemporalJitter.Offset(frame, phases);
                    var uniforms = TemporalResolveMath.BuildUniforms(Still, Still, jitter, iw, ih, dw, dh, frame > 0);
                    for (int r = 0; r < rigs.Length; r++)
                    {
                        TemporalResolveRig rig = rigs[r];
                        rig.Device.UpdateTexture(signals[r], f.Visibility, 0, 0, (uint)iw, (uint)ih);
                        rig.Fill(f.Colour, f.Colour, f.Motion);
                        rig.FillDepth(f.Ndc);
                        rig.BeginFrame();
                        if (frame > 0) rig.FillHistory(f.HistoryColour, f.HistoryState, (x, y) => f.PreviousDepth[y * iw + x]);
                        rig.Resolve(uniforms, signals[r]);
                        Assert.Equal(entry, rig.Renderer.LastEntry);
                        Assert.Equal(frame > 0 ? 1f : 0f, rig.Renderer.LastVisibilityUniforms.PreviousJitter.Z);
                    }
                    byte[] colour = Bytes(production.ReadColor()), state = Bytes(production.ReadState());
                    Assert.Equal(colour, Bytes(d.ReadColor()));
                    Assert.Equal(state, Bytes(d.ReadState()));
                    Assert.Equal(Stored(production), Stored(d));
                    float[] productionState = production.ReadState(), mutantState = mutant.ReadState();
                    float[] exportedState = exported.ReadState();
                    for (int i = 0; i < productionState.Length; i++)
                        if (BitConverter.SingleToInt32Bits(productionState[i]) != BitConverter.SingleToInt32Bits(mutantState[i]))
                            mutantDiffers++;
                    // The exporting rig decides as production does: every stored lock is production's.
                    for (int p = 0; p < productionState.Length / 2; p++)
                        Assert.True(BitConverter.SingleToInt32Bits(productionState[p * 2 + 1])
                            == BitConverter.SingleToInt32Bits(exportedState[p * 2 + 1]),
                            $"frame {frame} pixel {p}: the path export stored lock {exportedState[p * 2 + 1]:R}, "
                            + $"production {productionState[p * 2 + 1]:R}");
                    if (frame > 0) paths.Add(exported.ReadColor());
                    values += productionState.Length;
                    foreach (TemporalResolveRig rig in rigs) rig.History.MarkValidAfterFrame();
                }
                output.WriteLine($"  {Frames} frames byte-identical to D in colour, state and stored visibility, "
                    + $"{zeroLocks} stored locks exactly zero, the never-releasing mutant differs in {mutantDiffers} of "
                    + $"{values} state values, {dRewrites} D and {mutantRewrites} mutant programs rewritten"
                    + (entry == TemporalResolveEntry.Split ? $", the first pass {(prepareRewritten ? "" : "not ")}as D" : ""));
                output.WriteLine($"  production paths over frames 1 to {Frames - 1}: {paths}");
                Assert.True(dRewrites >= 1 && mutantRewrites >= 1, "the rewrite reached no release program");
                Assert.True(entry == TemporalResolveEntry.Fused || prepareRewritten,
                    "the D rig compiled the split's first pass as production");
                Assert.True(mutantDiffers > 0, "the never-releasing mutant wrote the production history, so the frames "
                    + "do not exercise the visibility release");
            }
            finally
            {
                for (int r = 0; r < rigs.Length; r++)
                {
                    signals[r].Dispose();
                    queues[r].Dispose();
                }
            }
            return paths;
        }

        sealed record Frame(float[] Colour, float[] Motion, float[] Ndc, byte[] Visibility, float[] HistoryColour,
            float[] HistoryState, float[] PreviousDepth, int ZeroLocks);

        // One frame's random inputs. Visibility carries a vertical edge that moves three texels a frame, a
        // horizontal band that moves one, and noise. A nearer box moves with its own motion across the far wall.
        static Frame MakeFrame(Random random, int frame, int dw, int dh, float farNdc, float nearNdc, float farLinear,
            float nearLinear)
        {
            var colour = new float[Iw * Ih * 4];
            var motion = new float[Iw * Ih * 2];
            var ndc = new float[Iw * Ih];
            var visibility = new byte[Iw * Ih];
            var previousDepth = new float[Iw * Ih];
            int boxX = 6 + 2 * frame, boxY = 7;
            for (int y = 0; y < Ih; y++)
                for (int x = 0; x < Iw; x++)
                {
                    int i = y * Iw + x;
                    bool box = x >= boxX && x < boxX + 9 && y >= boxY && y < boxY + 8;
                    float grey = (float)random.NextDouble() * 0.6f + 0.1f;
                    if (random.Next(12) == 0) grey = 0.95f;
                    colour[i * 4] = grey;
                    colour[i * 4 + 1] = grey * (0.8f + 0.4f * (float)random.NextDouble());
                    colour[i * 4 + 2] = grey;
                    colour[i * 4 + 3] = 1f;
                    ndc[i] = box ? nearNdc : farNdc;
                    previousDepth[i] = random.Next(10) == 0 ? farLinear * 1.3f : box ? nearLinear : farLinear;
                    int kind = random.Next(20);
                    Vector2 m = box ? new Vector2(-2f / Iw, 0f)
                        : kind < 11 ? Vector2.Zero
                        : kind < 15 ? new Vector2((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * 0.6f / new Vector2(Iw, Ih)
                        : kind < 18 ? new Vector2(random.Next(5) - 2, random.Next(5) - 2) / new Vector2(Iw, Ih)
                        : kind < 19 ? new Vector2((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * 6f / new Vector2(Iw, Ih)
                        : new Vector2(65000f, 0f);
                    motion[i * 2] = m.X;
                    motion[i * 2 + 1] = m.Y;
                    bool lit = x < 5 + 3 * frame ^ (y >= 3 + frame && y < 9 + frame);
                    visibility[i] = random.Next(10) == 0 ? (byte)random.Next(256) : lit ? (byte)255 : (byte)0;
                }
            var historyColour = new float[dw * dh * 4];
            var historyState = new float[dw * dh * 2];
            int zeroLocks = 0;
            for (int p = 0; p < dw * dh; p++)
            {
                float grey = (float)random.NextDouble() * 0.8f + 0.1f;
                historyColour[p * 4] = grey;
                historyColour[p * 4 + 1] = grey;
                historyColour[p * 4 + 2] = grey * 0.9f;
                historyColour[p * 4 + 3] = 1f;
                historyState[p * 2] = (float)random.NextDouble();
                int lockKind = random.Next(3);
                historyState[p * 2 + 1] = lockKind == 0 ? 0f : lockKind == 1 ? 1f : (float)random.NextDouble();
                if (lockKind == 0) zeroLocks++;
            }
            return new Frame(colour, motion, ndc, visibility, historyColour, historyState, previousDepth, zeroLocks);
        }

        // One threshold frame. Even frames give every 6 by 6 tile one new R8 level. Odd frames keep each tile at its
        // previous level L except the 3x3 around the tile centre: c of it, the centre always among them, sit on the
        // shared depth at levels summing to c L + delta, and the rest sit behind at random levels. delta is 25.5 c for
        // even c, so the true change is 0.1 exactly, or that one step either side, or the R4 margin's floor or the step
        // past it, with either sign. Grey colour, no motion, the previous depth on the shared surface and a full lock
        // everywhere, so every centre away from the border meets the fast path and its decision is the threshold's.
        // designs counts the centres by kind.
        static Frame MakeThresholdFrame(Random random, int frame, int dw, int dh, int[] levels, int[] designs)
        {
            const int W = ThresholdIw, H = ThresholdIh, Tiles = ThresholdIw / ThresholdTile;
            float behindNdc = NdcDepth(Projection, SceneMetres * 1.5f);
            var colour = new float[W * H * 4];
            for (int i = 0; i < W * H; i++) (colour[i * 4], colour[i * 4 + 1], colour[i * 4 + 2], colour[i * 4 + 3]) = (0.5f, 0.5f, 0.5f, 1f);
            var ndc = new float[W * H];
            Array.Fill(ndc, SceneNdc);
            var visibility = new byte[W * H];
            var previousDepth = new float[W * H];
            Array.Fill(previousDepth, SceneLinear);
            for (int t = 0; t < levels.Length; t++)
            {
                int x0 = t % Tiles * ThresholdTile, y0 = t / Tiles * ThresholdTile;
                if (frame % 2 == 0) levels[t] = 40 + random.Next(161);
                for (int y = y0; y < y0 + ThresholdTile; y++)
                    for (int x = x0; x < x0 + ThresholdTile; x++)
                        visibility[y * W + x] = (byte)levels[t];
                if (frame % 2 == 0) continue;
                (int[] site, bool[] accepted, int kind) = ThresholdSiteLevels(random, levels[t]);
                bool inside = x0 + ThresholdSite <= W - 3 && y0 + ThresholdSite <= H - 3;
                if (inside) designs[kind]++;
                for (int k = 0; k < 9; k++)
                {
                    int i = (y0 + ThresholdSite - 1 + k / 3) * W + x0 + ThresholdSite - 1 + k % 3;
                    visibility[i] = (byte)(accepted[k] ? site[k] : random.Next(256));
                    if (!accepted[k]) ndc[i] = behindNdc;
                }
            }
            var historyColour = new float[dw * dh * 4];
            var historyState = new float[dw * dh * 2];
            for (int p = 0; p < dw * dh; p++)
            {
                (historyColour[p * 4], historyColour[p * 4 + 1], historyColour[p * 4 + 2], historyColour[p * 4 + 3])
                    = (0.5f, 0.5f, 0.5f, 1f);
                historyState[p * 2] = 0.5f;
                historyState[p * 2 + 1] = 1f;
            }
            return new Frame(colour, new float[W * H * 2], ndc, visibility, historyColour, historyState, previousDepth, 0);
        }

        // The tile centre's 3x3 levels over the previous level L, rows first, and which of them sit on the shared depth.
        // Kind 0 puts the true change on 0.1 exactly, 1 a lattice step either side, 2 and 3 below and above the R4
        // margin.
        static (int[] Levels, bool[] Accepted, int Kind) ThresholdSiteLevels(Random random, int previousLevel)
        {
            int count, delta, kind;
            int pick = random.Next(10);
            if (pick < 5)
            {
                kind = 0;
                count = 2 * (1 + random.Next(4));
                delta = count * 51 / 2;
            }
            else if (pick < 7)
            {
                kind = 1;
                count = 2 * (1 + random.Next(4));
                delta = count * 51 / 2 + (random.Next(2) == 0 ? -1 : 1);
            }
            else
            {
                count = 1 + random.Next(9);
                int below = (int)Math.Floor((0.1 - 1.0 / 8192.0) * 255.0 * count);
                kind = random.Next(2) == 0 ? 2 : 3;
                delta = below + (kind - 2);
            }
            if (random.Next(2) == 0) delta = -delta;
            var accepted = new bool[9];
            accepted[4] = true;
            int[] order = [0, 1, 2, 3, 5, 6, 7, 8];
            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            for (int i = 0; i < count - 1; i++) accepted[order[i]] = true;
            int total = count * previousLevel + delta, low = (int)Math.Floor(total / (double)count);
            int extra = total - low * count;
            var levels = new int[9];
            int given = 0;
            for (int k = 0; k < 9; k++)
                if (accepted[k]) levels[k] = low + (given++ < extra ? 1 : 0);
            return (levels, accepted, kind);
        }

        static byte[] Bytes(float[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

        static byte[] Stored(TemporalResolveRig rig) => TemporalVisibilityHistoryGpuTests.Read(rig.Device,
            rig.Renderer.VisibilityHistory.Texture(rig.History.WriteIndex));
    }
}
