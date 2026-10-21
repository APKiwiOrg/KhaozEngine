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
        /// byte-identical in colour, state, stored R8 and stored depth, and so must D prime's two. No device is
        /// excepted, hardware NVIDIA Direct3D 11 included, since both entries of one program take the same inputs.
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
                    TemporalHistorySample.Exact(frame, "production fused vs split", TemporalHistorySample.Read(fused),
                        TemporalHistorySample.Read(split));
                    TemporalHistorySample.Exact(frame, "D prime fused vs split", TemporalHistorySample.Read(dFused),
                        TemporalHistorySample.Read(dSplit));
                    foreach (TemporalResolveRig rig in rigs) rig.History.MarkValidAfterFrame();
                }
                output.WriteLine($"  {frames} frames byte-identical across the entries in colour, state, R8 and depth, "
                    + $"production and D prime. Designed tile centres {string.Join(", ", designs)}, of them beside "
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
    }
}
