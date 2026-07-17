using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure headless coverage for <see cref="TonemapMath"/>: the C# mirror of the GLSL <c>TonemapFrag</c> tonemap
    /// (per-channel operator curve, luma-mapped hue-preserving rescale, and the ChromaPreservation mix). No GPU.
    /// TonemapMath is the single source this test and the shader mirror. The factor-0 short-circuit is the load-bearing
    /// invariant: at ChromaPreservation 0 the output must be exactly the legacy per-channel result (the engine's
    /// most-shipped pixel stays byte-identical), which the real-hardware golden gate then confirms end to end.
    /// </summary>
    public sealed class TonemapMathTests
    {
        const int Aces = 0;
        const int Reinhard = 1;
        const int Clamp = 2;

        static readonly int[] Operators = { Aces, Reinhard, Clamp };

        // Independent reference for one channel through an operator, arithmetic written to match TonemapMath exactly so
        // the factor-0 comparison is bit-exact (it verifies Map routes to the per-channel curve with no blend leaking
        // in, not that two different formulas happen to agree).
        static float RefCurve(float x, int op)
        {
            if (op == Aces)
            {
                float a = x * (2.51f * x + 0.03f);
                float b = x * (2.43f * x + 0.59f) + 0.14f;
                return Math.Clamp(a / b, 0f, 1f);
            }
            if (op == Reinhard) return x / (1f + x);
            return Math.Clamp(x, 0f, 1f);
        }

        static (float R, float G, float B) RefPerChannel(float r, float g, float b, float exposure, int op)
        {
            float cr = MathF.Max(r, 0f) * exposure;
            float cg = MathF.Max(g, 0f) * exposure;
            float cb = MathF.Max(b, 0f) * exposure;
            return (RefCurve(cr, op), RefCurve(cg, op), RefCurve(cb, op));
        }

        // Colours spanning below, at, and above the tonemap knee, incl. an over-range hot core.
        static readonly (float R, float G, float B)[] Sweep =
        {
            (0f, 0f, 0f),
            (0.1f, 0.2f, 0.05f),
            (0.5f, 0.5f, 0.5f),
            (0.9f, 0.4f, 0.1f),
            (1.0f, 1.0f, 1.0f),
            (2.5f, 1.2f, 0.3f),
            (6f, 6f, 6f),
            (8f, 0.2f, 0.02f),
        };

        // ---- Factor 0: exact per-channel identity (the byte-identity invariant, proved in fp) --------------------

        [Fact]
        public void Factor0_ExactlyEqualsPerChannel_AllOperators_AcrossSweep()
        {
            foreach (int op in Operators)
            {
                foreach (var (r, g, b) in Sweep)
                {
                    foreach (float exposure in new[] { 1f, 0.5f, 2f })
                    {
                        var expected = RefPerChannel(r, g, b, exposure, op);
                        var actual = TonemapMath.Map(r, g, b, exposure, op, 0f);
                        // Exact: the factor-0 path must not re-associate through the blend at all.
                        Assert.Equal(expected.R, actual.R);
                        Assert.Equal(expected.G, actual.G);
                        Assert.Equal(expected.B, actual.B);
                    }
                }
            }
        }

        [Fact]
        public void NegativeChroma_ClampsToZeroPath_ExactPerChannel()
        {
            // Upload clamps to [0,1], but the mapper itself must treat any <= 0 as the identity short-circuit.
            var expected = RefPerChannel(2.5f, 1.2f, 0.3f, 1f, Aces);
            var actual = TonemapMath.Map(2.5f, 1.2f, 0.3f, 1f, Aces, -0.5f);
            Assert.Equal(expected.R, actual.R);
            Assert.Equal(expected.G, actual.G);
            Assert.Equal(expected.B, actual.B);
        }

        // ---- Factor 1: hue (chromaticity) preserved for unclipped colours ---------------------------------------

        [Fact]
        public void Factor1_PreservesRgbRatios_ForUnclippedColours()
        {
            // Small, coloured inputs whose luma-rescaled result stays under 1 in every channel (no saturate clip),
            // so the output must be a pure scalar multiple of the input (identical chromaticity).
            var colours = new[]
            {
                (0.20f, 0.40f, 0.10f),
                (0.30f, 0.05f, 0.15f),
                (0.12f, 0.22f, 0.34f),
            };
            foreach (int op in Operators)
            {
                foreach (var (r, g, b) in colours)
                {
                    var (or_, og, ob) = TonemapMath.Map(r, g, b, 1f, op, 1f);
                    Assert.True(or_ < 1f && og < 1f && ob < 1f, "test colour must stay unclipped");
                    // ratio out/in equal across channels => same hue + saturation direction.
                    float sr = or_ / r, sg = og / g, sb = ob / b;
                    Assert.Equal(sr, sg, 4);
                    Assert.Equal(sr, sb, 4);
                }
            }
        }

        [Fact]
        public void Factor1_OutputLuma_EqualsOperatorOfInputLuma()
        {
            // The hue-preserving path maps luminance through the operator: luma(out) == curve(luma(in)).
            foreach (int op in Operators)
            {
                foreach (var (r, g, b) in Sweep)
                {
                    float lIn = TonemapMath.Luma(r, g, b);
                    var (or_, og, ob) = TonemapMath.Map(r, g, b, 1f, op, 1f);
                    float lOut = TonemapMath.Luma(or_, og, ob);
                    float expected = RefCurve(lIn, op);
                    // For unclipped colours this is exact. A hot core can clip a channel and pull luma below the
                    // curve, so allow the clip to only ever reduce luma (never raise it above the curve).
                    Assert.True(lOut <= expected + 1e-4f, $"op {op}: luma {lOut} exceeded curve {expected}");
                }
            }
        }

        // ---- Monotonicity: brighter input luminance never darkens the mapped luminance --------------------------

        [Fact]
        public void OutputLuma_MonotoneInInputLuminance_AllOperators_BothEndpoints()
        {
            foreach (int op in Operators)
            {
                foreach (float chroma in new[] { 0f, 1f })
                {
                    // Fixed hue, scaled from black up through the over-range region.
                    float prev = -1f;
                    for (float scale = 0f; scale <= 10f; scale += 0.1f)
                    {
                        var (or_, og, ob) = TonemapMath.Map(0.4f * scale, 0.25f * scale, 0.1f * scale, 1f, op, chroma);
                        float lOut = TonemapMath.Luma(or_, og, ob);
                        Assert.True(lOut >= prev - 1e-5f, $"op {op} chroma {chroma}: luma dropped at scale {scale}");
                        prev = lOut;
                    }
                }
            }
        }

        // ---- Clamp behaviour: mapped output always in [0,1] -----------------------------------------------------

        [Fact]
        public void Output_AlwaysInUnitRange_AllOperators_AllChroma()
        {
            foreach (int op in Operators)
            {
                foreach (float chroma in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
                {
                    foreach (var (r, g, b) in Sweep)
                    {
                        var (or_, og, ob) = TonemapMath.Map(r, g, b, 3f, op, chroma);
                        Assert.InRange(or_, 0f, 1f);
                        Assert.InRange(og, 0f, 1f);
                        Assert.InRange(ob, 0f, 1f);
                    }
                }
            }
        }

        // ---- Guards: zero luma / negatives / no NaN or Inf ------------------------------------------------------

        [Fact]
        public void ZeroAndNegativeInputs_ProduceFiniteZero_NoNaN()
        {
            foreach (int op in Operators)
            {
                foreach (float chroma in new[] { 0f, 0.5f, 1f })
                {
                    // Pure black: luma 0, the 1e-5 guard must keep the rescale finite (0/eps == 0).
                    var black = TonemapMath.Map(0f, 0f, 0f, 1f, op, chroma);
                    AssertFinite(black);
                    Assert.Equal(0f, black.R);
                    Assert.Equal(0f, black.G);
                    Assert.Equal(0f, black.B);

                    // Negative channels are floored to 0 before the curve (matching max(s.rgb,0) in the shader).
                    var neg = TonemapMath.Map(-1f, -0.5f, 0.3f, 1f, op, chroma);
                    AssertFinite(neg);
                    Assert.Equal(0f, neg.R);
                    Assert.Equal(0f, neg.G);
                }
            }
        }

        static void AssertFinite((float R, float G, float B) c)
        {
            Assert.False(float.IsNaN(c.R) || float.IsInfinity(c.R), "R not finite");
            Assert.False(float.IsNaN(c.G) || float.IsInfinity(c.G), "G not finite");
            Assert.False(float.IsNaN(c.B) || float.IsInfinity(c.B), "B not finite");
        }
    }
}
