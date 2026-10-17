using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using Stage = KhaozEngine.Tests.Render3D.CoverageFaceOverlayReference.Stage;

namespace KhaozEngine.Tests.Render3D;

// The deduplicated overlay against the frozen 56-call reference on every bool, maximum bit pattern and executed Edge.
public sealed class CoverageFaceOverlayTests
{
    static readonly double Tiny = Math.ScaleB(1, -687);

    [Fact]
    public void Every_corpus_pair_matches_the_frozen_reference_bit_for_bit()
    {
        var stages = new List<CoverageFaceOverlayReference.Result>();
        foreach (var (name, host, sheet) in Corpus())
        {
            var expected = CoverageFaceOverlayReference.TryMaximum(host, sheet);
            int edges = 0;
            bool succeeded = CoverageFaceOverlay.TryMaximum(host, sheet, ref edges, out double maximum);
            Assert.True(expected.Succeeded == succeeded, name);
            Assert.True(BitConverter.DoubleToInt64Bits(expected.Maximum) == BitConverter.DoubleToInt64Bits(maximum),
                $"{name}: {expected.Maximum:R} != {maximum:R}");
            Assert.True(CoverageFaceOverlay.TryMaximum(host, sheet, out double wrapped) == succeeded, name);
            Assert.Equal(BitConverter.DoubleToInt64Bits(maximum), BitConverter.DoubleToInt64Bits(wrapped));
            // Area, inside and corner refusals happen before any pair Edge, so both execute the same evaluations.
            // Past that stage the reference has repeated 36 Edge values the production overlay reads from its tables.
            int reused = expected.Failure is Stage.Area or Stage.Inside or Stage.CornerAccumulate ? expected.EdgeEvaluations : 20;
            Assert.True(reused == edges, $"{name}: {edges} Edge evaluations, expected {reused} ({expected.EdgeEvaluations} before)");
            if (expected.Succeeded) Assert.Equal(56, expected.EdgeEvaluations);
            stages.Add(expected);
        }
        // The corpus exercises success with real crossings, and every refusal stage reachable from the inputs,
        // including refusals after a finite partial maximum.
        Assert.Contains(stages, r => r.Succeeded && double.IsFinite(r.Maximum) && r.Crossings > 0);
        Assert.Contains(stages, r => r.Succeeded && r.Maximum == double.NegativeInfinity);
        Assert.Contains(stages, r => r.Failure == Stage.Area);
        Assert.Contains(stages, r => r.Failure == Stage.Inside && r.Maximum == double.NegativeInfinity);
        Assert.Contains(stages, r => r.Failure == Stage.Inside && double.IsFinite(r.Maximum));
        Assert.Contains(stages, r => r.Failure == Stage.CornerAccumulate && double.IsFinite(r.Maximum));
    }

    // A direct disjoint pair whose mixed Edge underflows the exact product floor still refuses, so the overlay may not
    // skip a pair on its bounding box alone. Whether an admitted engine transform reaches it remains unexecuted.
    [Fact]
    public void Disjoint_far_sheet_with_an_underflowing_mixed_product_refuses()
    {
        CoverageAffinePoint[] host = [P(.25, Tiny), P(.5, .5), P(.25, .75)];
        var tilt = Matrix4x4.Identity;
        tilt.M31 = float.Epsilon;
        CoverageAffinePoint[] far = [CoverageAffinePoint.Project(new(1, 0, float.Epsilon), tilt), P(2, 0), P(2, 1)];
        Assert.Equal(Math.ScaleB(1, -298), far[0].X[0]);
        Assert.Equal(1, far[0].X[1]);
        Assert.True(host.Max(p => p.X.Value) < far.Min(p => p.X.Value));
        var reference = CoverageFaceOverlayReference.TryMaximum(host, far);
        Assert.Equal((false, Stage.Inside), (reference.Succeeded, reference.Failure));
        Assert.False(CoverageFaceOverlay.TryMaximum(host, far, out double maximum));
        Assert.Equal(double.NegativeInfinity, maximum);

        CoverageAffinePoint[] near = [P(0, 0), P(1, 0), P(0, 1)];
        reference = CoverageFaceOverlayReference.TryMaximum(host, near);
        Assert.True(reference.Succeeded);
        Assert.True(CoverageFaceOverlay.TryMaximum(host, near, out maximum));
        Assert.Equal(BitConverter.DoubleToInt64Bits(reference.Maximum), BitConverter.DoubleToInt64Bits(maximum));
    }

    internal static CoverageAffinePoint P(double x, double y, double z = 0) => CoverageAffinePoint.FromDouble(x, y, z);

    static IEnumerable<(string Name, CoverageAffinePoint[] Host, CoverageAffinePoint[] Sheet)> Corpus()
    {
        CoverageAffinePoint[] unit = [P(0, 0, .3), P(1, 0, .1), P(0, 1, .2)];
        var tilt = Matrix4x4.Identity;
        tilt.M31 = float.Epsilon;
        var tilted = CoverageAffinePoint.Project(new(1, 0, float.Epsilon), tilt);
        double huge = 1.3e154, offset = 1e140;
        var bases = new (string, CoverageAffinePoint[], CoverageAffinePoint[])[]
        {
            ("overlap", unit, [P(.2, .2), P(1.2, .2, .05), P(.2, 1.2, .1)]),
            ("disjoint", unit, [P(3, 3), P(4, 3), P(3, 4)]),
            ("sheet-contains-host", unit, [P(-2, -2), P(3, -2, .1), P(-2, 3, .2)]),
            ("host-contains-sheet", [P(-2, -2, .4), P(3, -2, .1), P(-2, 3, .2)], [P(0, 0), P(.5, 0, .1), P(0, .5, .05)]),
            ("crossing", [P(0, 0, .2), P(2, 0, .3), P(1, 2, .1)], [P(0, 1.25), P(2, 1.25, .1), P(1, -.75, .05)]),
            ("near-clean", unit, [P(0, 0), P(1, 0), P(0, 1)]),
            ("shared-edge", unit, [P(1, 0), P(1, 1, .1), P(0, 1, .2)]),
            ("shared-vertex", unit, [P(1, 0), P(2, 0, .1), P(2, 1, .2)]),
            ("collinear-opposite", [P(0, 0, .1), P(2, 0, .2), P(0, 1, .3)], [P(1, 0), P(3, 0, .1), P(2, -1, .2)]),
            ("collinear-overlap", [P(0, 0, .1), P(2, 0, .2), P(0, 1, .3)], [P(1, 0), P(3, 0, .1), P(2, 1, .2)]),
            ("corner-on-edge", [P(.5, 0, .2), P(1.5, 1, .1), P(.5, 1, .3)], [P(0, 0), P(2, 0), P(1, -1)]),
            ("signed-zero", [P(-0.0, 0, .1), P(1, -0.0, .2), P(0, 1, .3)], [P(-0.0, -0.0), P(1, 0, .1), P(0, 1, .2)]),
            ("guard-edge-magnitudes", [P(Math.ScaleB(1, -432), 0, .1), P(1, Math.ScaleB(1, -433), .2), P(0, 1, .3)], [P(0, 0), P(1, 0), P(0, 1)]),
            ("degenerate-host", [P(0, 0), P(1, 1), P(2, 2)], [P(0, 0), P(1, 0), P(0, 1)]),
            ("degenerate-sheet", unit, [P(0, 0), P(.5, .5), P(1, 1)]),
            ("subnormal-host", [P(double.Epsilon, 0, .1), P(1, 0, .2), P(0, 1, .3)], [P(0, 0), P(1, 0), P(0, 1)]),
            ("overflowing-mixed", [P(-huge, -huge), P(-huge + offset, -huge), P(-huge, -huge + offset)], [P(0, 0), P(huge, 0), P(0, huge)]),
            ("far-underflow", [P(.25, Tiny), P(.5, .5), P(.25, .75)], [tilted, P(2, 0), P(2, 1)]),
            // One included corner accumulates before the next inside test underflows.
            ("partial-underflow", [P(1.75, 0, .3), P(1.5, Tiny, .2), P(1.75, .5, .1)], [tilted, P(2, 0), P(2, 1)]),
            // Two zero-height corners accumulate before the extreme third height overflows its interval.
            ("partial-overflow", [P(0, 0), P(1, 0), P(0, 1, double.MaxValue)], [P(-4, -4), P(4, -4), P(0, 4)]),
        };
        foreach (var (name, host, sheet) in bases)
        {
            yield return (name, host, sheet);
            yield return (name + ":host-reversed", Reverse(host), sheet);
            yield return (name + ":sheet-reversed", host, Reverse(sheet));
            yield return (name + ":both-reversed", Reverse(host), Reverse(sheet));
            yield return (name + ":host-rotated", [host[1], host[2], host[0]], sheet);
        }

        static CoverageAffinePoint[] Reverse(CoverageAffinePoint[] t) => [t[0], t[2], t[1]];
    }
}
