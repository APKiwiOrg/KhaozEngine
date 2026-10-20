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
    /// THE PRODUCTION RESOLVE WRITES D'S HISTORY (plan Task 7f.1, proof 3). Three rigs take the same random frames: the
    /// production renderer, a renderer whose release programs carry the frozen D text
    /// (<see cref="TemporalVisibilityFrozenD.ToD"/>) through the internal fragment source hook, and a mutant D that
    /// never releases on visibility. The frames carry random colour with isolated ridges, two surfaces, random motion
    /// with background texels, moving R8 edges and random stored locks, a third of them exactly zero. After every
    /// frame the production and D histories must be byte-identical in colour, state and stored visibility. The
    /// mutant must differ, which shows the hook reaches the compiled program and the frames exercise the release.
    /// This is the one proof that covers the lock gate (R1), the pass-one mask (R3-lite) and the range skip (R4) at
    /// the call site together.
    /// </summary>
    public sealed class TemporalVisibilityResolveAbGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
    {
        const int Iw = 40, Ih = 24, Frames = 6;

        [GpuTheory]
        [InlineData(0, TemporalUpscale.Native)]
        [InlineData(0, TemporalUpscale.Quality)]
        [InlineData(1, TemporalUpscale.Native)]
        [InlineData(1, TemporalUpscale.Quality)]
        public void ProductionResolveWritesTheFrozenDHistoryEveryFrame(int choice, TemporalUpscale preset)
        {
            var entry = (TemporalResolveEntry)choice;
            int dw = preset == TemporalUpscale.Native ? Iw : Iw * 3 / 2, dh = preset == TemporalUpscale.Native ? Ih : Ih * 3 / 2;
            int dRewrites = 0, mutantRewrites = 0;
            using var production = new TemporalResolveRig(Iw, Ih, dw, dh, SceneNdc);
            using var d = new TemporalResolveRig(Iw, Ih, dw, dh, SceneNdc, fragmentSource: source =>
            {
                if (source.Contains(ShaderSources.TemporalVisibilityGlsl, StringComparison.Ordinal)) dRewrites++;
                return TemporalVisibilityFrozenD.ToD(source);
            });
            using var mutant = new TemporalResolveRig(Iw, Ih, dw, dh, SceneNdc, fragmentSource: source =>
            {
                if (source.Contains(ShaderSources.TemporalVisibilityGlsl, StringComparison.Ordinal)) mutantRewrites++;
                return TemporalVisibilityFrozenD.ToMutantD(source, callSite => callSite.Replace(
                    "> VisibilityReleaseThreshold", "> 2.0", StringComparison.Ordinal));
            });
            TemporalResolveRig[] rigs = [production, d, mutant];
            var queues = new GpuRetireQueue[rigs.Length];
            var signals = new IGpuTexture[rigs.Length];
            for (int r = 0; r < rigs.Length; r++)
            {
                queues[r] = GpuRetireQueue.Create(rigs[r].Device);
                rigs[r].Renderer.SelectEntry(entry, queues[r]);
                signals[r] = rigs[r].Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(Iw, Ih,
                    GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
            }
            output.WriteLine($"{production.Device.Backend} on {production.Device.Capabilities.DeviceName}, {entry} {preset}, "
                + $"{Iw}x{Ih} internal, {dw}x{dh} display");
            try
            {
                var random = new Random(entry == TemporalResolveEntry.Fused ? 1709 : 2711);
                int phases = TemporalJitter.PhaseCount(TemporalResolveMath.DisplayOverInternal(dw, dh, Iw, Ih));
                float nearNdc = NdcDepth(Projection, 1.4f), nearLinear = TemporalResolveMath.LinearDepth(nearNdc, Depth);
                long mutantDiffers = 0, zeroLocks = 0, values = 0;
                for (int frame = 0; frame < Frames; frame++)
                {
                    Frame f = MakeFrame(random, frame, dw, dh, SceneNdc, nearNdc, SceneLinear, nearLinear);
                    zeroLocks += f.ZeroLocks;
                    Vector2 jitter = TemporalJitter.Offset(frame, phases);
                    var uniforms = TemporalResolveMath.BuildUniforms(Still, Still, jitter, Iw, Ih, dw, dh, frame > 0);
                    for (int r = 0; r < rigs.Length; r++)
                    {
                        TemporalResolveRig rig = rigs[r];
                        rig.Device.UpdateTexture(signals[r], f.Visibility, 0, 0, Iw, Ih);
                        rig.Fill(f.Colour, f.Colour, f.Motion);
                        rig.FillDepth(f.Ndc);
                        rig.BeginFrame();
                        if (frame > 0) rig.FillHistory(f.HistoryColour, f.HistoryState, (x, y) => f.PreviousDepth[y * Iw + x]);
                        rig.Resolve(uniforms, signals[r]);
                        Assert.Equal(entry, rig.Renderer.LastEntry);
                        Assert.Equal(frame > 0 ? 1f : 0f, rig.Renderer.LastVisibilityUniforms.PreviousJitter.Z);
                    }
                    byte[] colour = Bytes(production.ReadColor()), state = Bytes(production.ReadState());
                    Assert.Equal(colour, Bytes(d.ReadColor()));
                    Assert.Equal(state, Bytes(d.ReadState()));
                    Assert.Equal(Stored(production), Stored(d));
                    float[] productionState = production.ReadState(), mutantState = mutant.ReadState();
                    for (int i = 0; i < productionState.Length; i++)
                        if (BitConverter.SingleToInt32Bits(productionState[i]) != BitConverter.SingleToInt32Bits(mutantState[i]))
                            mutantDiffers++;
                    values += productionState.Length;
                    foreach (TemporalResolveRig rig in rigs) rig.History.MarkValidAfterFrame();
                }
                output.WriteLine($"  {Frames} frames byte-identical to D in colour, state and stored visibility, "
                    + $"{zeroLocks} stored locks exactly zero, the never-releasing mutant differs in {mutantDiffers} of "
                    + $"{values} state values, {dRewrites} D and {mutantRewrites} mutant programs rewritten");
                Assert.True(dRewrites >= 1 && mutantRewrites >= 1, "the rewrite reached no release program");
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

        static byte[] Bytes(float[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

        static byte[] Stored(TemporalResolveRig rig) => TemporalVisibilityHistoryGpuTests.Read(rig.Device,
            rig.Renderer.VisibilityHistory.Texture(rig.History.WriteIndex));
    }
}
