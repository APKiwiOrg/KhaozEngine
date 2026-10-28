using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed partial class TemporalEntryIdentityGpuTests
    {
        /// <summary>
        /// BOTH ENTRIES WRITE THE SAME HISTORY ACROSS THE POWER-OF-TWO COLUMNS (review 7g r1, I-2). The large threshold
        /// frames of <see cref="TemporalVisibilityResolveAbGpuTests"/>, their 1104x612 internal frame, 6 by 6 tiles and
        /// Halton order, reach columns 512 and 1024 and row 512 under positive jitter on each axis. One frame set feeds
        /// four rigs: the production fused and split entries, and D prime's fused and split entries
        /// (<see cref="TemporalVisibilityFrozenD.ToD"/>). After every frame the two production histories must be
        /// byte-identical in colour, state, stored R8 and stored depth, and so must D prime's two. Hardware NVIDIA
        /// Direct3D 11 is not excepted, since both entries of one program take the same inputs. On a software Vulkan
        /// device (<see cref="TemporalDeviceClass.IsLavapipe"/>) each pair's colour is held to
        /// <see cref="TemporalSoftwareCompileDivergenceRule"/> over all frames and printed, as the follow walk's is, and its
        /// confidence, lock, mark, R8 and depth stay exact. Production drift and swing take the same half-float-step
        /// and differing-share rule as the follow walk. D prime's complete state remains exact.
        /// </summary>
        [GpuTheory]
        [InlineData(TemporalUpscale.Native)]
        [InlineData(TemporalUpscale.Quality)]
        public void Both_entry_points_write_the_same_history_across_power_of_two_columns(TemporalUpscale preset)
        {
            const int iw = TemporalVisibilityResolveAbGpuTests.LargeIw, ih = TemporalVisibilityResolveAbGpuTests.LargeIh;
            int dw = preset == TemporalUpscale.Native ? iw : iw * 3 / 2, dh = preset == TemporalUpscale.Native ? ih : ih * 3 / 2;
            using var fused = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc);
            using var split = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc);
            using var dFused = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc, fragmentSource: TemporalVisibilityFrozenD.ToD);
            using var dSplit = new TemporalResolveRig(iw, ih, dw, dh, SceneNdc, fragmentSource: TemporalVisibilityFrozenD.ToD);
            TemporalResolveRig[] rigs = [fused, split, dFused, dSplit];
            TemporalResolveEntry[] entries = [TemporalResolveEntry.Fused, TemporalResolveEntry.Split,
                TemporalResolveEntry.Fused, TemporalResolveEntry.Split];
            var queues = new GpuRetireQueue[rigs.Length];
            var signals = new IGpuTexture[rigs.Length];
            for (int r = 0; r < rigs.Length; r++)
            {
                queues[r] = GpuRetireQueue.Create(rigs[r].Device);
                rigs[r].Renderer.SelectEntry(entries[r], queues[r]);
                signals[r] = rigs[r].Device.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)iw, (uint)ih,
                    GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
            }
            output.WriteLine($"{fused.Device.Backend} on {fused.Device.Capabilities.DeviceName}, {preset}, {iw}x{ih} internal, "
                + $"{dw}x{dh} display, production and D prime each fused against split on one frame set");
            bool softwareVulkan = TemporalDeviceClass.IsLavapipe(fused.Device);
            var productionColour = new EntryDifference("production fused vs split colour");
            var dColour = new EntryDifference("D prime fused vs split colour");
            var productionState = new TemporalEntryStateComparison(softwareVulkan);
            // D prime stores no shimmer record, so its zero blue and alpha channels remain exact too.
            var dState = new TemporalEntryStateComparison(softwareVulkan: false);
            long colourCompared = 0;
            try
            {
                var random = new Random(5501);
                var levels = new int[(iw / 6) * (ih / 6)];
                var designs = new int[5];
                var straddling = new int[5];
                int phases = TemporalJitter.PhaseCount(TemporalResolveMath.DisplayOverInternal(dw, dh, iw, ih));
                int frames = TemporalVisibilityResolveAbGpuTests.LargePhases.Length;
                bool positiveX = false, positiveY = false;
                for (int frame = 0; frame < frames; frame++)
                {
                    TemporalVisibilityResolveAbGpuTests.Frame f = TemporalVisibilityResolveAbGpuTests.MakeThresholdFrame(
                        random, frame, iw, ih, dw, dh, levels, designs, straddling);
                    Vector2 jitter = TemporalJitter.Offset(TemporalVisibilityResolveAbGpuTests.LargePhases[frame], phases);
                    // The odd frames carry the designed tile centres.
                    if (frame % 2 == 1)
                    {
                        positiveX |= jitter.X > 0f;
                        positiveY |= jitter.Y > 0f;
                    }
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
                        Assert.True(entries[r] == rig.Renderer.LastEntry, $"frame {frame}: the wrong entry resolved");
                    }
                    TemporalHistorySample pf = TemporalHistorySample.Read(fused), ps = TemporalHistorySample.Read(split);
                    TemporalHistorySample df = TemporalHistorySample.Read(dFused), ds = TemporalHistorySample.Read(dSplit);
                    if (softwareVulkan)
                    {
                        SoftwareVulkanPair(frame, "production fused vs split", pf, ps, productionColour, productionState);
                        SoftwareVulkanPair(frame, "D prime fused vs split", df, ds, dColour, dState);
                        colourCompared += pf.Colour.Length;
                    }
                    else
                    {
                        TemporalHistorySample.Exact(frame, "production fused vs split", pf, ps);
                        TemporalHistorySample.Exact(frame, "D prime fused vs split", df, ds);
                    }
                    foreach (TemporalResolveRig rig in rigs) rig.History.MarkValidAfterFrame();
                }
                if (softwareVulkan)
                    foreach (EntryDifference pair in new[] { productionColour, dColour })
                    {
                        double share = TemporalSoftwareCompileDivergenceRule.ShareOf(pair.Values, colourCompared);
                        output.WriteLine($"  software Vulkan: {pair}, within "
                            + $"{TemporalSoftwareCompileDivergenceRule.AbsoluteBound} on {share:P3} of values");
                        Assert.True(TemporalSoftwareCompileDivergenceRule.Allows(pair.Worst, pair.Values, colourCompared),
                            $"frame {frames - 1}: the colour on a software Vulkan device left its bound "
                            + $"({TemporalSoftwareCompileDivergenceRule.AbsoluteBound}, "
                            + $"{TemporalSoftwareCompileDivergenceRule.DifferingShareBound:P2} of values): {pair}");
                    }
                productionState.AssertComplete(output, $"{preset} large production");
                dState.AssertComplete(output, $"{preset} large D prime");
                output.WriteLine($"  {frames} frames byte-identical across the entries in "
                    + (softwareVulkan ? "confidence, locks, marks, R8 and depth" : "colour, state, R8 and depth")
                    + $", production and D prime. Designed tile centres {string.Join(", ", designs)}, of them beside "
                    + $"column 512 or 1024 or row 512 {string.Join(", ", straddling)}");
                Assert.True(straddling.Sum() >= 100,
                    $"frame {frames - 1}: only {straddling.Sum()} designed tile centres lie beside columns 512 and 1024 and row 512");
                Assert.True(positiveX && positiveY, $"frame {frames - 1}: the designed frames lack positive jitter on an axis");
            }
            finally
            {
                for (int r = 0; r < rigs.Length; r++)
                {
                    signals[r].Dispose();
                    queues[r].Dispose();
                }
            }
        }

        // A software Vulkan pair: confidence, lock, mark, R8 and depth exact, colour and record gathered for bounds.
        static void SoftwareVulkanPair(int frame, string what, TemporalHistorySample a, TemporalHistorySample b,
            EntryDifference colour, TemporalEntryStateComparison state)
        {
            Assert.Equal(a.Colour.Length, b.Colour.Length);
            for (int i = 0; i < a.Colour.Length; i++) colour.Add(frame, a.Colour[i], b.Colour[i]);
            state.Compare(frame, what + " state", a.State, b.State);
            TemporalHistorySample.Exact(frame, what + " R8", a.Visibility, b.Visibility);
            TemporalHistorySample.Exact(frame, what + " depth", a.Depth, b.Depth);
        }
    }
}
