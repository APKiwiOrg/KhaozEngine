using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SPLIT'S FIRST PASS STORES THE CURRENT VISIBILITY MASK (plan Task 7f.3, R3-lite). The split's second pass
    /// reads which of a texel's 3x3 lie on its own surface, and whether its own view depth is finite, from the surface
    /// target's fourth channel in place of fetching this frame's 3x3 depths again. So the stored value must be, texel
    /// for texel, the mask the shipped functions compute inline from the same inputs, which is what the fused entry
    /// does. One random frame runs through the split, and its stored masks are read back against an inline evaluation
    /// in a separate program. The frame mixes two surfaces, neighbours at and a few ulps either side of the centre's
    /// tolerance, non-finite and far depths, and background texels, so the masks are partial, full and empty.
    /// </summary>
    public sealed class TemporalVisibilityMaskGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
    {
        const int Iw = 64, Ih = 40, Tile = 8;
        const uint CentreFinite = 512, Full = 1023;

        // Floors on what the frame must exercise. The inline masks of this frame on Metal were 51 empty, 346 full and
        // 2163 partial, and the floors sit near a third of those.
        const int MinEmpty = 15, MinFull = 100, MinPartial = 700;

        [GpuFact]
        public void TheFirstPassStoresTheInlineMaskOfEveryTexel()
        {
            using var rig = new TemporalResolveRig(Iw, Ih, Iw, Ih, SceneNdc);
            using GpuRetireQueue queue = GpuRetireQueue.Create(rig.Device);
            rig.Renderer.SelectEntry(TemporalResolveEntry.Split, queue);
            using IGpuTexture signal = rig.Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(Iw, Ih,
                GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));
            output.WriteLine($"{rig.Device.Backend} on {rig.Device.Capabilities.DeviceName}, {Iw}x{Ih}");

            (float[] ndc, float[] motion) = MakeFrame(new Random(4099));
            var visibility = new byte[Iw * Ih];
            Array.Fill(visibility, (byte)255);
            rig.Device.UpdateTexture(signal, visibility, 0, 0, Iw, Ih);
            var colour = new float[Iw * Ih * 4];
            Array.Fill(colour, 0.5f);
            rig.Fill(colour, colour, motion);
            rig.FillDepth(ndc);
            rig.BeginFrame();
            TemporalResolveUniforms uniforms = TemporalResolveMath.BuildUniforms(Still, Still, default, Iw, Ih, Iw, Ih,
                historyValid: false);
            rig.Resolve(uniforms, signal);
            Assert.Equal(TemporalResolveEntry.Split, rig.Renderer.LastEntry);
            float[] surface = TemporalTextureIo.Read(rig.Device,
                rig.Renderer.SplitResolveForTests!.TargetForTests(1)
                    ?? throw new InvalidOperationException("the split made no surface target"));

            using var probe = new TemporalVisibilityMaskProbe(rig.Device, Iw, Ih);
            float[] inline = probe.Run(ndc, motion, uniforms.CurrentDepth);

            int empty = 0, full = 0, partial = 0, mismatches = 0, notWhole = 0;
            var examples = new List<string>();
            for (int i = 0; i < Iw * Ih; i++)
            {
                float stored = surface[i * 4 + 3], expected = inline[i];
                if (!(stored >= 0f && stored < 1024f && stored == MathF.Floor(stored))) notWhole++;
                uint mask = (uint)expected;
                if (mask == 0) empty++;
                else if (mask == Full) full++;
                else if ((mask & CentreFinite) != 0) partial++;
                if (BitConverter.SingleToInt32Bits(stored) == BitConverter.SingleToInt32Bits(expected)) continue;
                mismatches++;
                if (examples.Count < 16)
                    examples.Add($"texel ({i % Iw}, {i / Iw}): stored {stored:R}, inline {expected:R}, ndc {ndc[i]:R}");
            }
            output.WriteLine($"  inline masks: {empty} empty, {full} full, {partial} partial, of {Iw * Ih}; "
                + $"{mismatches} stored masks differ, {notWhole} stored values not a whole number below 1024");
            foreach (string example in examples) output.WriteLine("  MISMATCH " + example);

            Assert.True(empty >= MinEmpty && full >= MinFull && partial >= MinPartial,
                $"the frame is too plain: {empty} empty, {full} full and {partial} partial masks");
            Assert.True(notWhole == 0, $"{notWhole} stored values are not a whole number below 1024");
            Assert.True(mismatches == 0, $"{mismatches} of {Iw * Ih} stored masks differ from the inline mask");
        }

        // One frame in 8 by 8 tiles. Each tile has a base view depth and a kind: one surface, two surfaces, neighbours
        // at the centre's tolerance within a few ulps, or hostile depths (NaN, both infinities, the far plane, behind
        // it). One texel in twenty is background by the motion sentinel, and as many move.
        static (float[] Ndc, float[] Motion) MakeFrame(Random random)
        {
            var ndc = new float[Iw * Ih];
            var motion = new float[Iw * Ih * 2];
            for (int ty = 0; ty < Ih / Tile; ty++)
                for (int tx = 0; tx < Iw / Tile; tx++)
                {
                    int kind = random.Next(10);
                    double baseDepth = 0.3 * Math.Pow(2.0, random.NextDouble() * 8.0);
                    for (int y = ty * Tile; y < (ty + 1) * Tile; y++)
                        for (int x = tx * Tile; x < (tx + 1) * Tile; x++)
                            ndc[y * Iw + x] = Texel(random, kind, baseDepth);
                }
            for (int i = 0; i < Iw * Ih; i++)
            {
                int m = random.Next(20);
                if (m < 1) motion[i * 2] = 65000f;
                else if (m < 2)
                {
                    motion[i * 2] = ((float)random.NextDouble() - 0.5f) * 0.05f;
                    motion[i * 2 + 1] = ((float)random.NextDouble() - 0.5f) * 0.05f;
                }
            }
            return (ndc, motion);
        }

        static float Texel(Random random, int kind, double baseDepth)
        {
            int roll = random.Next(4);
            if (kind < 3) return Ndc(baseDepth);
            if (kind < 5) return Ndc(roll < 2 ? baseDepth : baseDepth * 1.3);
            if (kind < 9) return roll == 0 ? Ndc(baseDepth) : ToleranceEdge(baseDepth, roll != 1, random.Next(7) - 3);
            return random.Next(6) switch
            {
                0 => float.NaN,
                1 => float.PositiveInfinity,
                2 => float.NegativeInfinity,
                3 => 1f,
                4 => 1.0001f,
                _ => Ndc(baseDepth),
            };
        }

        /// <summary>The NDC depth of a neighbour at the centre's current tolerance: the view depth
        /// <paramref name="baseDepth"/> times one plus or minus <see cref="TemporalResolveTuning.VisibilityDepthTolerance"/>,
        /// moved <paramref name="ulps"/> float steps. The A/B fact's tolerance-edge tiles use it too.</summary>
        internal static float ToleranceEdge(double baseDepth, bool farther, int ulps)
            => Ulps(Ndc(baseDepth * (farther ? 1.0 + TemporalResolveTuning.VisibilityDepthTolerance
                : 1.0 - TemporalResolveTuning.VisibilityDepthTolerance)), ulps);

        // The NDC depth the resolve's perspective depth parameters turn into this view depth.
        static float Ndc(double linear)
        {
            double near = Depth.Y, far = Depth.Z;
            return (float)((far - near * far / linear) / (far - near));
        }

        static float Ulps(float v, int k) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(v) + k);
    }
}
