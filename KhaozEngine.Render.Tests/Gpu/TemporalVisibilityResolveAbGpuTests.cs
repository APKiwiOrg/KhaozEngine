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
    /// THE PRODUCTION RESOLVE WRITES D'S HISTORY (plan Task 7f.1, proof 3). Five rigs take the same frames: the
    /// production renderer, a renderer whose every program is the program D shipped
    /// (<see cref="TemporalVisibilityFrozenD.ToD"/>, the first pass and the fused entry's 3x3 loop included) through the
    /// internal fragment source hook, a mutant D that never releases on visibility, and the production programs with
    /// their path bits exported twice (<see cref="TemporalVisibilityProductionPaths.Rewrite"/> and
    /// <see cref="TemporalVisibilityProductionPaths.RewriteKeepingTheDecision"/>). After every frame the production and
    /// D histories must be byte-identical in colour, state, stored visibility and stored depth, except on hardware
    /// NVIDIA Direct3D 11, whose driver compiles unrelated confidence arithmetic differently whenever the visibility
    /// code changes, even on frame 0 where the check does not run. There colour and confidence differences against D
    /// are printed as counts and maximum half steps and not asserted. D's paired entries are diagnostic only. Locks,
    /// R8 and depth stay exact on every device. Both exporting rigs
    /// must write production's locks and the same path words less the slow path's reasons. The mutant must differ,
    /// which shows the hook reaches the compiled program and the frames exercise the release. The exported paths must
    /// reach minimum counts of fast-path and range-skip pixels, so the identity is shown on the production program's
    /// own fast path and skip. This is the one proof that covers the lock gate (R1), the pass-one mask (R3-lite) and
    /// the range skip (R4) at the call site together.
    /// <para><b>D PRIME AND SHIPPED D (Task 7g.3a).</b> The D rig is D prime (<see cref="TemporalVisibilityFrozenD.ToD"/>),
    /// D with production's barrier-ordered lock release, and every rule above compares against it. A sixth rig compiles
    /// the program D shipped (<see cref="TemporalVisibilityFrozenD.ToShippedD"/>), its <c>precise</c> lock release
    /// included. On Metal, which lowered that helper as the barrier form computes, production's history must equal it
    /// byte for byte every frame. Elsewhere the differences are printed only, since WARP and lavapipe divided by three
    /// where the barrier form multiplies by its rounded reciprocal.</para>
    /// <para>Two frame sets run. Random frames carry random colour with isolated ridges, two surfaces, random motion
    /// with background texels, moving R8 edges and random stored locks, a third of them exactly zero. Threshold frames
    /// alternate a uniform R8 level per 6 by 6 tile with a frame whose tile centre has c texels on a shared depth, the
    /// rest behind it, at levels that sum to c L plus or minus a designed amount over the tile's previous level L: 25.5
    /// c, which puts D's true change on 0.1 exactly for even c, one R8 lattice step either side of it, or either side of
    /// the R4 margin <c>0.1 - 2^-13</c>. Every texel there holds a lock and has no motion, so the fast path decides each
    /// tile centre at the release threshold.</para>
    /// <para>Tolerance-edge tiles (review 7f r2, I-1) put one neighbour of the tile centre at the NDC depth of the
    /// centre's view depth times one plus or minus 0.02, moved up to three float steps either way
    /// (<see cref="TemporalVisibilityMaskGpuTests.ToleranceEdge"/>, the mask fact's tolerance texels), so whether it
    /// lies on the centre's surface turns on the last bit of the view depth that pass one now computes. The levels
    /// make that one acceptance bit decide the release: either the sure texels sit 26 R8 steps from L and the edge
    /// texel at L, or they sit 25 steps from L and the edge texel 30 + c beyond it. A view depth one ulp apart from
    /// D's flips the bit, the release and the stored lock. The fact counts the exported paths of the display pixels
    /// centred on those tiles, and the outcomes there show the edge texel accepted on some and rejected on
    /// others.</para>
    /// <para>Large threshold frames (fix round 7f r3) run the same tiles over a 1104x612 internal frame, so columns 512
    /// and 1024 and row 512 lie inside it with tiles centred one texel past each. There the binade rule
    /// (<see cref="TemporalVisibilityFastPath.SlowForBinade"/>) sends the block to D's path under positive jitter on
    /// that axis, and the fast path decides it otherwise. The jitter takes six of the Halton offsets in an order that
    /// gives the designed frames both signs on both axes. Every exported path word whose block fits must carry the
    /// binade bit exactly where the rule predicts it from the mirrored centre.</para>
    /// </summary>
    public sealed class TemporalVisibilityResolveAbGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
    {
        const int Iw = 40, Ih = 24, Frames = 6;
        const int ThresholdIw = 96, ThresholdIh = 60, ThresholdTile = 6, ThresholdSite = 3;
        internal const int LargeIw = 1104, LargeIh = 612;

        // The large frames' Halton phases, frame by frame: the designed frames 1, 3 and 5 take jitter (-, +), (+, -) and
        // (+, +), so each axis meets both signs where the tiles decide.
        internal static readonly int[] LargePhases = [0, 1, 3, 2, 5, 4];

        /// <summary>D here carries the owner-approved precision floor (2026-10-29). Still receivers otherwise
        /// reproduce the binary16 precision flash that the approved floor removes, so floorless colour equality
        /// cannot hold. Every other post-D change is undone, and every visibility assertion stays exact.</summary>
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
                    TemporalResolveMath.LinearDepth(NdcDepth(Projection, 1.4f), Depth)), precisionFloor: true);
            // Floors under what the random frames give: a lock in two of three pixels, most centres off the border.
            Assert.True(paths.FastPath >= 50 && paths.RangeSkips >= 10 && paths.FastComputed >= 10,
                $"frame {Frames - 1} (aggregate): the random frames reached too few production paths: {paths}");
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
            var designs = new int[5];
            var edges = new EdgeOutcomes();
            TemporalVisibilityProductionPaths paths = Run(entry, preset, ThresholdIw, ThresholdIh,
                (frame, dw, dh) => MakeThresholdFrame(random, frame, ThresholdIw, ThresholdIh, dw, dh, levels, designs),
                edges);
            output.WriteLine($"  designed tile centres: {designs[0]} on 0.1 exactly, {designs[1]} a lattice step either "
                + $"side, {designs[2]} below the R4 margin, {designs[3]} above it, {designs[4]} at the current tolerance");
            output.WriteLine($"  display pixels centred on a tolerance-edge tile: {edges.Paths}");
            output.WriteLine($"  their fast-path outcomes: the edge texel accepted on {edges.Accepted} and rejected on "
                + $"{edges.Rejected}, {edges.Unexpected} outcomes neither design gives");
            // Each of the three designed frames holds 160 tile centres off the border: about 57 on 0.1 exactly and 46
            // at the current tolerance.
            Assert.True(paths.FastPath >= 1_000 && paths.RangeSkips >= 200 && paths.FastComputed >= 100
                && paths.FastReleased >= 20 && paths.FastNearThresholdAbove + paths.FastNearThresholdBelow >= 50,
                $"frame {Frames - 1} (aggregate): the threshold frames reached too few production paths at the threshold: {paths}");
            // About 137 tolerance-edge centres over the three frames, one display pixel each at Native and two or more
            // at Quality, and the edge texel's ulps either side of the tolerance give both acceptances. An outcome
            // neither design gives would come from a display pixel the mirror of temporalPixelSite placed on another
            // centre, at a rounding boundary, so a few are let through.
            Assert.True(edges.Paths.FastPath >= 60 && edges.Accepted >= 10 && edges.Rejected >= 10
                && edges.Unexpected * 20 <= edges.Paths.FastPath,
                $"frame {Frames - 1} (aggregate): the tolerance-edge tiles reached too few fast-path decisions either side of the tolerance: "
                + $"{edges.Paths}, accepted {edges.Accepted}, rejected {edges.Rejected}, unexpected {edges.Unexpected}");
        }

        [GpuTheory]
        [InlineData(0, TemporalUpscale.Native)]
        [InlineData(0, TemporalUpscale.Quality)]
        [InlineData(1, TemporalUpscale.Native)]
        [InlineData(1, TemporalUpscale.Quality)]
        public void LargeThresholdFramesWriteTheFrozenDHistoryAcrossPowerOfTwoColumns(int choice, TemporalUpscale preset)
        {
            var entry = (TemporalResolveEntry)choice;
            var random = new Random(entry == TemporalResolveEntry.Fused ? 5501 : 6607);
            var levels = new int[(LargeIw / ThresholdTile) * (LargeIh / ThresholdTile)];
            var designs = new int[5];
            var straddling = new int[5];
            var edges = new EdgeOutcomes();
            var binade = new BinadeOutcomes();
            TemporalVisibilityProductionPaths paths = Run(entry, preset, LargeIw, LargeIh,
                (frame, dw, dh) => MakeThresholdFrame(random, frame, LargeIw, LargeIh, dw, dh, levels, designs, straddling),
                edges, binade, (frame, phases) => TemporalJitter.Offset(LargePhases[frame], phases));
            output.WriteLine($"  designed tile centres: {designs[0]} on 0.1 exactly, {designs[1]} a lattice step either "
                + $"side, {designs[2]} below the R4 margin, {designs[3]} above it, {designs[4]} at the current tolerance");
            output.WriteLine($"  of them centred one texel past column 512 or 1024 or row 512: {straddling[0]} on 0.1 "
                + $"exactly, {straddling[1]} a step either side, {straddling[2]} and {straddling[3]} by the R4 margin, "
                + $"{straddling[4]} at the current tolerance");
            output.WriteLine($"  tolerance-edge outcomes: accepted {edges.Accepted}, rejected {edges.Rejected}, "
                + $"unexpected {edges.Unexpected}, over {edges.Paths}");
            output.WriteLine($"  binade rule: {binade}");
            Assert.True(paths.FastPath >= 100_000 && paths.SlowBinade >= 1_000 && paths.FastReleased >= 1_000,
                $"frame {Frames - 1} (aggregate): the large frames reached too few production paths: {paths}");
            // The mirror of temporalPixelSite can place a pixel at a rounding boundary on the next centre, so a few
            // pixels are let through. The rule itself is exact.
            Assert.True(binade.Apart * 100 <= binade.Checked,
                $"frame {Frames - 1} (aggregate): the binade bit differs from the rule's prediction on too many pixels: {binade}");
            Assert.True(binade.StraddleBinade >= 1_000 && binade.StraddleFast >= 1_000 && binade.StraddleFastPositive >= 100,
                $"frame {Frames - 1} (aggregate): too few decisions beside columns 512 and 1024 and row 512 on either path: {binade}");
            Assert.True(edges.Paths.FastPath >= 1_000 && edges.Accepted >= 100 && edges.Rejected >= 100
                && edges.Unexpected * 20 <= edges.Paths.FastPath,
                $"frame {Frames - 1} (aggregate): the large tolerance-edge tiles reached too few fast-path decisions: {edges.Paths}, accepted "
                + $"{edges.Accepted}, rejected {edges.Rejected}, unexpected {edges.Unexpected}");
        }

        // The display pixels whose block fits, checked against the binade rule from the mirrored centre: how many the
        // rule and the binade bit both send to D's path or both keep on the fast path, those beside column 512 or 1024
        // or row 512, and those beside such a column or row with positive jitter on the other axis, which the rule must
        // keep fast.
        sealed class BinadeOutcomes
        {
            internal long Checked, Binade, Fast, Apart, StraddleBinade, StraddleFast, StraddleFastPositive;

            public override string ToString() => $"{Checked} pixels whose block fits, {Binade} sent to D's path and "
                + $"{Fast} kept fast as predicted, {Apart} apart from the prediction, beside 512 or 1024 {StraddleBinade} "
                + $"sent and {StraddleFast} kept, {StraddleFastPositive} of those kept under positive jitter on the other "
                + "axis";
        }

        static void CountBinade(BinadeOutcomes binade, float[] exported, Vector4 jitter, int iw, int ih, int dw, int dh)
        {
            static bool Named(int centre, int last, float axisJitter)
            {
                for (int k = -1; k <= 1; k++)
                    if (TemporalVisibilityFastPath.SlowForBinade(Math.Clamp(centre + k, 0, last), axisJitter)) return true;
                return false;
            }
            static bool Beside(int centre, int line) => Math.Abs(centre - line) <= 1;
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    int p = y * dw + x;
                    uint path = TemporalVisibilityProductionPaths.PathAt(exported, p);
                    if ((path & (TemporalVisibilityProductionPaths.Fast | TemporalVisibilityProductionPaths.Slow)) == 0
                        || (path & TemporalVisibilityProductionPaths.Reasons) != 0) continue;
                    int cx = Math.Clamp((int)MathF.Floor((x + 0.5f) / dw * iw + jitter.X), 0, iw - 1);
                    int cy = Math.Clamp((int)MathF.Floor((y + 0.5f) / dh * ih + jitter.Y), 0, ih - 1);
                    bool namedX = Named(cx, iw - 1, jitter.X), namedY = Named(cy, ih - 1, jitter.Y);
                    bool reported = (path & TemporalVisibilityProductionPaths.Binade) != 0;
                    binade.Checked++;
                    if (reported != (namedX || namedY)) { binade.Apart++; continue; }
                    bool besideX = Beside(cx, 512) || Beside(cx, 1024), besideY = Beside(cy, 512);
                    if (reported) binade.Binade++;
                    else binade.Fast++;
                    if (!besideX && !besideY) continue;
                    if (reported) binade.StraddleBinade++;
                    else
                    {
                        binade.StraddleFast++;
                        if ((besideX && jitter.Y > 0f) || (besideY && jitter.X > 0f)) binade.StraddleFastPositive++;
                    }
                }
        }

        // The exported paths of the display pixels centred on a tolerance-edge tile, and what each fast-path outcome
        // says of the edge texel: with the sure texels 26 steps out, a release means it was rejected and a range skip
        // that it was accepted, and the other way round with them 25 steps out.
        sealed class EdgeOutcomes
        {
            internal readonly TemporalVisibilityProductionPaths Paths = new();
            internal long Accepted, Rejected, Unexpected;

            internal void Add(uint path, float change, int design)
            {
                if ((path & TemporalVisibilityProductionPaths.Fast) == 0) return;
                bool skipped = (path & TemporalVisibilityProductionPaths.RangeSkip) != 0;
                bool released = (path & TemporalVisibilityProductionPaths.Computed) != 0
                    && MathF.Abs(change) > TemporalResolveTuning.VisibilityReleaseThreshold;
                if (skipped == released) Unexpected++;
                else if (released == (design == EdgePullsBelow)) Rejected++;
                else Accepted++;
            }
        }

        // The five rigs over one frame set. Returns the production paths the exporting rig counted, and gives the
        // tolerance-edge tiles' display pixels to edges.
        TemporalVisibilityProductionPaths Run(TemporalResolveEntry entry, TemporalUpscale preset, int iw, int ih,
            Func<int, int, int, Frame> makeFrame, EdgeOutcomes? edges = null, BinadeOutcomes? binade = null,
            Func<int, int, Vector2>? jitterOf = null, bool precisionFloor = false)
        {
            int dw = preset == TemporalUpscale.Native ? iw : iw * 3 / 2, dh = preset == TemporalUpscale.Native ? ih : ih * 3 / 2;
            Func<string, string> rewriteD = precisionFloor
                ? TemporalVisibilityFrozenD.ToDWithPrecisionFloor : TemporalVisibilityFrozenD.ToD;
            Func<string, string> rewriteShippedD = precisionFloor
                ? TemporalVisibilityFrozenD.ToShippedDWithPrecisionFloor : TemporalVisibilityFrozenD.ToShippedD;
            int dRewrites = 0, mutantRewrites = 0;
            bool prepareRewritten = false;
            using var production = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc);
            bool nvidia = TemporalDeviceClass.IsHardwareNvidiaD3D11(production.Device);
            var oracle = nvidia ? new TemporalNvidiaHistoryOracle(output) : null;
            using var otherD = nvidia ? new TemporalResolveRig(iw, ih, dw, dh, SceneNdc,
                fragmentSource: rewriteD) : null;
            using var d = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc, fragmentSource: source =>
            {
                string rewritten = rewriteD(source);
                if (!ReferenceEquals(rewritten, source)) dRewrites++;
                if (source == ShaderSources.TemporalPrepareFrag && rewritten != source) prepareRewritten = true;
                return rewritten;
            });
            using var mutant = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc, fragmentSource: source =>
            {
                string rewritten = TemporalVisibilityFrozenD.ToMutantD(source, callSite => callSite.Replace(
                    "> VisibilityReleaseThreshold", "> 2.0", StringComparison.Ordinal));
                if (precisionFloor) rewritten = TemporalVisibilityFrozenDLaterChanges.WithPrecisionFloor(rewritten);
                if (!ReferenceEquals(rewritten, source)) mutantRewrites++;
                return rewritten;
            });
            using var exported = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc,
                fragmentSource: TemporalVisibilityProductionPaths.Rewrite);
            using var kept = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc,
                fragmentSource: TemporalVisibilityProductionPaths.RewriteKeepingTheDecision);
            using var shippedD = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc,
                fragmentSource: rewriteShippedD);
            bool metal = production.Device.Backend == GpuBackendKind.MetalNative;
            TemporalResolveRig[] rigs = otherD is null ? [production, d, mutant, exported, kept, shippedD]
                : [production, d, mutant, exported, kept, shippedD, otherD];
            var queues = new GpuRetireQueue[rigs.Length];
            var signals = new IGpuTexture[rigs.Length];
            for (int r = 0; r < rigs.Length; r++)
            {
                queues[r] = GpuRetireQueue.Create(rigs[r].Device);
                rigs[r].Renderer.SelectEntry(ReferenceEquals(rigs[r], otherD)
                    ? entry == TemporalResolveEntry.Fused ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused
                    : entry, queues[r]);
                signals[r] = rigs[r].Device.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)iw, (uint)ih,
                    GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
            }
            output.WriteLine($"{production.Device.Backend} on {production.Device.Capabilities.DeviceName}, {entry} {preset}, "
                + $"{iw}x{ih} internal, {dw}x{dh} display");
            if (nvidia) output.WriteLine("  D fused and split receive the same Frame arrays, uploaded colour, motion, "
                + "depth, history colour, confidence, locks and previous depth, R8 sequence and uniforms in this case");
            var paths = new TemporalVisibilityProductionPaths();
            var keptPaths = new TemporalVisibilityProductionPaths();
            try
            {
                int phases = TemporalJitter.PhaseCount(TemporalResolveMath.DisplayOverInternal(dw, dh, iw, ih));
                long mutantDiffers = 0, zeroLocks = 0, values = 0, storedApart = 0;
                for (int frame = 0; frame < Frames; frame++)
                {
                    Frame f = makeFrame(frame, dw, dh);
                    zeroLocks += f.ZeroLocks;
                    Vector2 jitter = jitterOf?.Invoke(frame, phases) ?? TemporalJitter.Offset(frame, phases);
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
                        Assert.True(uniforms.Equals(rig.Renderer.LastUniforms),
                            $"frame {frame}: the resolve did not receive the shared uniforms");
                        TemporalResolveEntry expected = ReferenceEquals(rig, otherD)
                            ? entry == TemporalResolveEntry.Fused ? TemporalResolveEntry.Split : TemporalResolveEntry.Fused
                            : entry;
                        Assert.True(expected == rig.Renderer.LastEntry, $"frame {frame}: the wrong entry resolved");
                        Assert.True(rig.Renderer.LastVisibilityUniforms.PreviousJitter.Z == (frame > 0 ? 1f : 0f),
                            $"frame {frame}: the previous visibility validity differs");
                    }
                    TemporalHistorySample actual = TemporalHistorySample.Read(production);
                    TemporalHistorySample reference = TemporalHistorySample.Read(d);
                    if (otherD is not null)
                    {
                        TemporalHistorySample opposite = TemporalHistorySample.Read(otherD);
                        oracle!.ObserveD(frame, entry == TemporalResolveEntry.Fused ? reference : opposite,
                            entry == TemporalResolveEntry.Fused ? opposite : reference);
                        oracle.Compare(frame, $"production {entry} vs D {entry}", actual, reference);
                    }
                    else TemporalHistorySample.Exact(frame, "production vs D", actual.WithoutShimmerRecord(),
                        reference.WithoutShimmerRecord());
                    TemporalHistorySample shipped = TemporalHistorySample.Read(shippedD);
                    if (metal)
                        TemporalHistorySample.Exact(frame, "production vs shipped D", actual.WithoutShimmerRecord(),
                            shipped.WithoutShimmerRecord());
                    else
                    {
                        TemporalHistoryDifference apart = TemporalHistoryDifference.Measure(actual.Colour, actual.State,
                            shipped.Colour, shipped.State);
                        bool r8 = actual.Visibility.AsSpan().SequenceEqual(shipped.Visibility);
                        bool depth = MemoryMarshal.AsBytes(actual.Depth.AsSpan())
                            .SequenceEqual(MemoryMarshal.AsBytes(shipped.Depth.AsSpan()));
                        output.WriteLine($"  frame {frame}: production vs shipped D (reported only off Metal): {apart}, "
                            + $"R8 equal {r8}, depth bits equal {depth}");
                    }
                    // The depth this frame stored, read from the write target before the next frame fills the read one.
                    float[] storedDepth = actual.Depth;
                    for (int i = 0; i < storedDepth.Length; i++)
                        if (BitConverter.SingleToInt32Bits(storedDepth[i]) != BitConverter.SingleToInt32Bits(f.PreviousDepth[i]))
                            storedApart++;
                    float[] productionState = actual.State, mutantState = mutant.ReadState();
                    float[] exportedState = exported.ReadState(), keptState = kept.ReadState();
                    // The mutant is D, which stores no shimmer record, so only confidence and lock count.
                    for (int i = 0; i < productionState.Length; i++)
                        if (i % 4 < 2
                            && BitConverter.SingleToInt32Bits(productionState[i]) != BitConverter.SingleToInt32Bits(mutantState[i]))
                            mutantDiffers++;
                    // Both exporting rigs decide as production does: every stored lock is production's.
                    for (int p = 0; p < productionState.Length / 4; p++)
                    {
                        int locked = BitConverter.SingleToInt32Bits(productionState[p * 4 + 1]);
                        if (locked != BitConverter.SingleToInt32Bits(exportedState[p * 4 + 1]))
                            Assert.Fail($"frame {frame} pixel {p}: the path export stored lock {exportedState[p * 4 + 1]:R}, "
                                + $"production {productionState[p * 4 + 1]:R}");
                        if (locked != BitConverter.SingleToInt32Bits(keptState[p * 4 + 1]))
                            Assert.Fail($"frame {frame} pixel {p}: the export keeping the decision stored lock "
                                + $"{keptState[p * 4 + 1]:R}, production {productionState[p * 4 + 1]:R}");
                    }
                    float[] exportedColour = exported.ReadColor(), keptColour = kept.ReadColor();
                    for (int p = 0; p < exportedColour.Length / 4; p++)
                    {
                        uint full = TemporalVisibilityProductionPaths.PathAt(exportedColour, p);
                        uint plain = TemporalVisibilityProductionPaths.PathAt(keptColour, p);
                        float fullChange = TemporalVisibilityProductionPaths.ChangeAt(exportedColour, p);
                        float plainChange = TemporalVisibilityProductionPaths.ChangeAt(keptColour, p);
                        if ((full & ~TemporalVisibilityProductionPaths.Reasons) != plain
                            || BitConverter.SingleToInt32Bits(fullChange) != BitConverter.SingleToInt32Bits(plainChange))
                            Assert.Fail($"frame {frame} pixel {p}: the full export wrote path {full} and change "
                                + $"{fullChange:R}, the export keeping the decision {plain} and {plainChange:R}");
                    }
                    if (frame > 0)
                    {
                        paths.Add(exportedColour);
                        keptPaths.Add(keptColour);
                        if (edges is not null && f.EdgeSites is { } sites)
                            CountEdgeSites(edges, exportedColour, sites, uniforms.Jitter, iw, ih, dw, dh);
                        if (binade is not null) CountBinade(binade, exportedColour, uniforms.Jitter, iw, ih, dw, dh);
                    }
                    values += productionState.Length;
                    foreach (TemporalResolveRig rig in rigs) rig.History.MarkValidAfterFrame();
                }
                oracle?.Complete();
                output.WriteLine($"  {Frames} frames {(nvidia ? "exact in locks, R8 and depth, colour and confidence reported above" : "byte-identical to D in colour, state, R8 and depth")}, "
                    + (metal ? "byte-identical to shipped D in colour, state, R8 and depth, " : "shipped D reported above, ")
                    + $"{zeroLocks} stored locks exactly zero, the never-releasing mutant differs in {mutantDiffers} of "
                    + $"{values} state values, {dRewrites} D and {mutantRewrites} mutant programs rewritten"
                    + (entry == TemporalResolveEntry.Split ? $", the first pass {(prepareRewritten ? "" : "not ")}as D" : ""));
                output.WriteLine($"  {storedApart} stored depths apart from the depth uploaded as last frame's, so the "
                    + "compared read is this frame's store");
                output.WriteLine($"  production paths over frames 1 to {Frames - 1}: {paths}");
                output.WriteLine($"  the export keeping the decision: {keptPaths}");
                Assert.True(keptPaths.Pixels == paths.Pixels && keptPaths.NotReached == paths.NotReached
                    && keptPaths.GatedByLock == paths.GatedByLock && keptPaths.GatedOtherwise == paths.GatedOtherwise
                    && keptPaths.FastPath == paths.FastPath && keptPaths.SlowPath == paths.SlowPath
                    && keptPaths.SlowBinade == paths.SlowBinade
                    && keptPaths.RangeSkips == paths.RangeSkips && keptPaths.ChangeComputed == paths.ChangeComputed
                    && keptPaths.FastComputed == paths.FastComputed && keptPaths.Released == paths.Released,
                    $"frame {Frames - 1}: the export keeping the decision counted other paths than the full export");
                Assert.True(storedApart > 0, $"frame {Frames - 1}: every stored depth equals the uploaded one, so the read may not be this "
                    + "frame's store");
                Assert.True(dRewrites >= 1 && mutantRewrites >= 1, $"frame {Frames - 1}: the rewrite reached no release program");
                Assert.True(entry == TemporalResolveEntry.Fused || prepareRewritten,
                    $"frame {Frames - 1}: the D rig compiled the split's first pass as production");
                Assert.True(mutantDiffers > 0, $"frame {Frames - 1}: the never-releasing mutant wrote the production history, so the frames "
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

        // EdgeSites holds, per internal texel, which tolerance-edge design is centred there, or zero.
        internal sealed record Frame(float[] Colour, float[] Motion, float[] Ndc, byte[] Visibility, float[] HistoryColour,
            float[] HistoryState, float[] PreviousDepth, int ZeroLocks, int[]? EdgeSites = null);

        const int EdgePullsBelow = 1, EdgePullsAbove = 2;

        // Every display pixel whose centre texel, by temporalPixelSite's arithmetic, is a tolerance-edge tile centre.
        static void CountEdgeSites(EdgeOutcomes edges, float[] exported, int[] sites, Vector4 jitter, int iw, int ih,
            int dw, int dh)
        {
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    int cx = Math.Clamp((int)MathF.Floor((x + 0.5f) / dw * iw + jitter.X), 0, iw - 1);
                    int cy = Math.Clamp((int)MathF.Floor((y + 0.5f) / dh * ih + jitter.Y), 0, ih - 1);
                    int design = sites[cy * iw + cx];
                    if (design == 0) continue;
                    int p = y * dw + x;
                    edges.Paths.AddPixel(exported, p);
                    edges.Add(TemporalVisibilityProductionPaths.PathAt(exported, p),
                        TemporalVisibilityProductionPaths.ChangeAt(exported, p), design);
                }
        }

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
            var historyState = new float[dw * dh * 4];
            int zeroLocks = 0;
            for (int p = 0; p < dw * dh; p++)
            {
                float grey = (float)random.NextDouble() * 0.8f + 0.1f;
                historyColour[p * 4] = grey;
                historyColour[p * 4 + 1] = grey;
                historyColour[p * 4 + 2] = grey * 0.9f;
                historyColour[p * 4 + 3] = 1f;
                historyState[p * 4] = (float)random.NextDouble();
                int lockKind = random.Next(3);
                historyState[p * 4 + 1] = lockKind == 0 ? 0f : lockKind == 1 ? 1f : (float)random.NextDouble();
                if (lockKind == 0) zeroLocks++;
            }
            return new Frame(colour, motion, ndc, visibility, historyColour, historyState, previousDepth, zeroLocks);
        }

        // One threshold frame. Even frames give every 6 by 6 tile one new R8 level. Odd frames keep each tile at its
        // previous level L except the 3x3 around the tile centre: c of it, the centre always among them, sit on the
        // shared depth at levels summing to c L + delta, and the rest sit behind at random levels. delta is 25.5 c for
        // even c, so the true change is 0.1 exactly, or that one step either side, or the R4 margin's floor or the step
        // past it, with either sign. Two tiles in seven are tolerance-edge tiles instead (ToleranceEdgeSite). Grey
        // colour, no motion, the previous depth on the shared surface and a full lock everywhere, so every centre away
        // from the border meets the fast path and its decision is the threshold's. designs counts the centres by kind,
        // 4 for a tolerance-edge tile.
        internal static Frame MakeThresholdFrame(Random random, int frame, int w, int h, int dw, int dh, int[] levels, int[] designs,
            int[]? straddling = null)
        {
            int tiles = w / ThresholdTile;
            float behindNdc = NdcDepth(Projection, SceneMetres * 1.5f);
            var colour = new float[w * h * 4];
            for (int i = 0; i < w * h; i++) (colour[i * 4], colour[i * 4 + 1], colour[i * 4 + 2], colour[i * 4 + 3]) = (0.5f, 0.5f, 0.5f, 1f);
            var ndc = new float[w * h];
            Array.Fill(ndc, SceneNdc);
            var visibility = new byte[w * h];
            var previousDepth = new float[w * h];
            Array.Fill(previousDepth, SceneLinear);
            int[]? edgeSites = frame % 2 == 0 ? null : new int[w * h];
            for (int t = 0; t < levels.Length; t++)
            {
                int x0 = t % tiles * ThresholdTile, y0 = t / tiles * ThresholdTile;
                if (frame % 2 == 0) levels[t] = 40 + random.Next(161);
                for (int y = y0; y < y0 + ThresholdTile; y++)
                    for (int x = x0; x < x0 + ThresholdTile; x++)
                        visibility[y * w + x] = (byte)levels[t];
                if (frame % 2 == 0) continue;
                bool inside = x0 + ThresholdSite <= w - 3 && y0 + ThresholdSite <= h - 3;
                int siteX = x0 + ThresholdSite, siteY = y0 + ThresholdSite;
                bool straddles = straddling is not null && inside
                    && (Math.Abs(siteX - 512) <= 1 || Math.Abs(siteX - 1024) <= 1 || Math.Abs(siteY - 512) <= 1);
                if (random.Next(7) < 2)
                {
                    (int[] edgeLevels, bool[] sure, int edge, int design) = ToleranceEdgeSite(random, levels[t]);
                    if (inside) designs[4]++;
                    if (straddles) straddling![4]++;
                    edgeSites![(y0 + ThresholdSite) * w + x0 + ThresholdSite] = design;
                    bool farther = random.Next(2) == 0;
                    float edgeNdc = TemporalVisibilityMaskGpuTests.ToleranceEdge(SceneLinear, farther, random.Next(7) - 3);
                    // A nearer edge texel is the 3x3's nearest, so the centre expects its depth: the tile's previous
                    // depth follows, or every history texel would lie past the previous tolerance.
                    if (!farther)
                    {
                        float edgeLinear = TemporalResolveMath.LinearDepth(edgeNdc, Depth);
                        for (int y = y0; y < y0 + ThresholdTile; y++)
                            for (int x = x0; x < x0 + ThresholdTile; x++)
                                previousDepth[y * w + x] = edgeLinear;
                    }
                    for (int k = 0; k < 9; k++)
                    {
                        int i = (y0 + ThresholdSite - 1 + k / 3) * w + x0 + ThresholdSite - 1 + k % 3;
                        if (k == edge)
                        {
                            visibility[i] = (byte)edgeLevels[k];
                            ndc[i] = edgeNdc;
                        }
                        else if (sure[k]) visibility[i] = (byte)edgeLevels[k];
                        else
                        {
                            visibility[i] = (byte)random.Next(256);
                            ndc[i] = behindNdc;
                        }
                    }
                    continue;
                }
                (int[] site, bool[] accepted, int kind) = ThresholdSiteLevels(random, levels[t]);
                if (inside) designs[kind]++;
                if (straddles) straddling![kind]++;
                for (int k = 0; k < 9; k++)
                {
                    int i = (y0 + ThresholdSite - 1 + k / 3) * w + x0 + ThresholdSite - 1 + k % 3;
                    visibility[i] = (byte)(accepted[k] ? site[k] : random.Next(256));
                    if (!accepted[k]) ndc[i] = behindNdc;
                }
            }
            var historyColour = new float[dw * dh * 4];
            var historyState = new float[dw * dh * 4];
            for (int p = 0; p < dw * dh; p++)
            {
                (historyColour[p * 4], historyColour[p * 4 + 1], historyColour[p * 4 + 2], historyColour[p * 4 + 3])
                    = (0.5f, 0.5f, 0.5f, 1f);
                historyState[p * 4] = 0.5f;
                historyState[p * 4 + 1] = 1f;
            }
            return new Frame(colour, new float[w * h * 2], ndc, visibility, historyColour, historyState, previousDepth, 0,
                edgeSites);
        }

        // A tolerance-edge tile centre's 3x3 over the previous level L, rows first: c sure texels on the shared depth,
        // the centre among them, one edge texel at the current tolerance and the rest behind. With the edge texel
        // rejected the mean moves 26 R8 steps (0.102, a release) or 25 (0.098, inside the R4 margin), and accepted it
        // moves 26 c / (c + 1) steps, below 25.5 for every c up to 8, or (26 c + 30) / (c + 1), above 25.5 for every c.
        // So the one acceptance bit decides the release either way, with either sign.
        static (int[] Levels, bool[] Sure, int Edge, int Design) ToleranceEdgeSite(Random random, int previousLevel)
        {
            int count = 1 + random.Next(8);
            int design = random.Next(2) == 0 ? EdgePullsBelow : EdgePullsAbove;
            int sign = random.Next(2) == 0 ? -1 : 1;
            int[] order = [0, 1, 2, 3, 5, 6, 7, 8];
            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            var sure = new bool[9];
            sure[4] = true;
            for (int i = 0; i < count - 1; i++) sure[order[i]] = true;
            int edge = order[count - 1];
            var levels = new int[9];
            int sureStep = design == EdgePullsBelow ? 26 : 25;
            for (int k = 0; k < 9; k++) if (sure[k]) levels[k] = previousLevel + sign * sureStep;
            levels[edge] = design == EdgePullsBelow ? previousLevel : previousLevel + sign * (30 + count);
            return (levels, sure, edge, design);
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
    }
}
