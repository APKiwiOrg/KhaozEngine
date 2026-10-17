using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;
using Oracle = KhaozEngine.Tests.Render3D.CoverageExactOracle;

namespace KhaozEngine.Tests.Render3D;

// The stack-buffer kernels in CoverageGeometry checked against an independent BigInteger oracle, plus their result bits
// captured from the zero-initialized baseline. Each named case runs once plainly and once after a NaN stack poison.
// Supported results must have the exact sign and contain the exact value. Unsupported products must refuse, and each
// refusal case proves its unsupported product independently instead of forcing every finite input to be accepted.
public sealed class CoverageGeometryExactTests(ITestOutputHelper output)
{
    delegate int DifferenceInto(CoverageCoordinate a, CoverageCoordinate b, Span<double> values);
    delegate bool ProductsInto(ReadOnlySpan<double> a, ReadOnlySpan<double> b, int sign, Span<double> expansion, ref int length);

    sealed record ScalarCase(string Name, Func<CoverageScalar> Evaluate, Func<CoverageExactValue> Exact, int? DesignSign,
        Func<bool>? Refusal);
    sealed record DistanceCase(string Name, CoverageAffinePoint A, CoverageAffinePoint B, float Budget, int? DesignComparison,
        Func<bool>? Refusal = null);
    sealed record AreaInput(CoverageAffinePoint A, CoverageAffinePoint B, CoverageAffinePoint C, Vector3 Axis);
    readonly record struct Golden(bool Refused, int Sign, ulong Estimate, ulong Min, ulong Max);

    static double Pow2(int exponent) => Math.ScaleB(1d, exponent);
    // An odd 53-bit significand, so the product of two such components leaves a nonzero error-free residual.
    static double Odd(int k, int exponent) => Math.ScaleB(1 + k * Pow2(-52), exponent);
    static CoverageCoordinate C(params double[] components) => new(components);
    static CoverageCoordinate Times(double k, CoverageCoordinate c) => C(k * c[0], k * c[1], k * c[2], k * c[3]);
    static CoverageAffinePoint P(CoverageCoordinate x, CoverageCoordinate y, CoverageCoordinate z = default) => new(x, y, z);
    static CoverageAffinePoint P(double x, double y, double z = 0) => CoverageAffinePoint.FromDouble(x, y, z);

    // Bit pattern stored only by memory writes, so an untouched slot keeps it exactly.
    static readonly double Sentinel = BitConverter.UInt64BitsToDouble(0x7FF8_0000_DEAD_BEEF);
    static readonly double Offset = Pow2(40);

    static readonly CoverageCoordinate LineX = C(1.5, 3 * Pow2(-61), Pow2(-119), -Pow2(-181));
    static readonly CoverageCoordinate LineY = C(-.75, 5 * Pow2(-63), -Pow2(-121), Pow2(-183));
    static readonly CoverageCoordinate FlatY = C(.5, Pow2(-75), Pow2(-130));
    // Components 60 binades apart never merge, so each difference of these keeps eight terms.
    static readonly CoverageCoordinate EightA = C(Odd(1, 0), Odd(3, -120), Odd(5, -240), Odd(7, -360));
    static readonly CoverageCoordinate EightB = C(Odd(9, -60), Odd(11, -180), Odd(13, -300), Odd(15, -420));
    static readonly CoverageAffinePoint DenseA = P(C(Odd(1, -60), Odd(3, -180), Odd(5, -300), Odd(7, -420)),
        C(-Odd(25, -60), Odd(27, -180), -Odd(29, -300), Odd(31, -420)), C(Odd(49, -60), -Odd(51, -180), Odd(53, -300), -Odd(55, -420)));
    static readonly CoverageAffinePoint DenseB = P(C(Odd(9, 0), Odd(11, -120), Odd(13, -240), Odd(15, -360)),
        C(Odd(33, -1), Odd(35, -120), -Odd(37, -240), Odd(39, -360)), C(-Odd(57, 1), Odd(59, -120), Odd(61, -240), -Odd(63, -360)));
    static readonly CoverageAffinePoint DenseP = P(C(Odd(17, 1), -Odd(19, -120), Odd(21, -240), -Odd(23, -360)),
        C(-Odd(41, 0), Odd(43, -120), Odd(45, -240), -Odd(47, -360)));

    static readonly (CoverageAffinePoint A, CoverageAffinePoint B, CoverageAffinePoint P)[] SwapEdges =
    [
        (P(C(1, Pow2(-90)), C(0)), P(0, 0), P(C(.5), C(.25, -Pow2(-110)))),
        (P(C(2, Pow2(-80)), C(1, Pow2(-70))), P(C(2, Pow2(-80)), C(1)), P(3, -1)),
        (P(1, 1), P(0, 0), P(2, 2)),
    ];

    static readonly AreaInput AreaEarly = new(P(0, 0, 0), P(1, 0, 0), P(0, 1, 0), Vector3.UnitZ);
    static readonly AreaInput AreaTilted = new(P(C(Offset), C(Offset), C(Offset)),
        P(C(Offset, Pow2(-20)), C(Offset, Pow2(-21)), C(Offset, -Pow2(-22))),
        P(C(Offset, -Pow2(-21)), C(Offset, Pow2(-20), Pow2(-90)), C(Offset, Pow2(-23))), new(.5f, -.25f, 1));
    static readonly AreaInput AreaZeroAxis = AreaTilted with { Axis = Vector3.UnitZ };
    // c - a is exactly twice b - a, so every normal component is exactly zero while the interval prefilter straddles.
    static readonly AreaInput AreaCollinear = new(P(C(Offset, Pow2(-30)), C(Offset, -Pow2(-31)), C(Offset, Pow2(-32))),
        P(C(Offset, Pow2(-30), Pow2(-20)), C(Offset, -Pow2(-31), 3 * Pow2(-21)), C(Offset, Pow2(-32), -Pow2(-19))),
        P(C(Offset, Pow2(-30), Pow2(-19)), C(Offset, -Pow2(-31), 3 * Pow2(-20)), C(Offset, Pow2(-32), -Pow2(-18))), Vector3.One);
    // c.X equals a.X, so the second normal component is a short -dz2 while the first holds many product terms.
    static readonly AreaInput AreaStaleTail = new(P(C(Offset), C(Offset, Pow2(-150)), C(Offset, -Pow2(-160))),
        P(C(Offset, 1), C(Offset, Pow2(-20), 3 * Pow2(-90)), C(Offset, -5 * Pow2(-21), 7 * Pow2(-95))),
        P(C(Offset), C(Offset, 5 * Pow2(-21), -7 * Pow2(-95)), C(Offset, Pow2(-20), 3 * Pow2(-85))), Vector3.One);
    // The first axis succeeds with -2^-652. The second axis multiplies 2^-600 by 2^-600, below the exact-product floor.
    static readonly AreaInput AreaSecondAxisRefusal = new(P(0, 0, 0), P(Pow2(-600), 1, Pow2(-600)),
        P(Pow2(-600), 1 + Pow2(-52), Pow2(-600)), new(1, 1, 0));

    static ScalarCase Edge(string name, CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint p, int? sign,
        Func<bool>? refusal = null) => new($"edge.{name}", () => CoverageGeometry.Edge(a, b, p), () => Oracle.Orient(a, b, p), sign, refusal);
    static ScalarCase Dot(string name, CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint c, CoverageAffinePoint d, int? sign) =>
        new($"dot.{name}", () => CoverageGeometry.Dot(a, b, c, d), () => Oracle.Dot(a, b, c, d), sign, null);
    static ScalarCase Area(string name, AreaInput i, int? sign, Func<bool>? refusal = null) => new($"area.{name}",
        () => CoverageGeometry.ProjectedArea(i.A, i.B, i.C, i.Axis), () => Oracle.ProjectedArea(i.A, i.B, i.C, i.Axis), sign, refusal);
    static ScalarCase Difference(string name, CoverageCoordinate a, CoverageCoordinate b, int? sign) =>
        new($"difference.{name}", () => CoverageGeometry.Difference(a, b), () => Oracle.Difference(a, b), sign, null);

    static bool BelowExactProductFloor(CoverageExactValue product) => product.Abs.CompareTo(Oracle.MinExactProduct) < 0;

    static readonly ScalarCase[] ScalarCases =
    [
        Edge("collinear_four_term_exact_zero", P(LineX, LineY), P(Times(2, LineX), Times(2, LineY)), P(Times(3, LineX), Times(3, LineY)), 0),
        Edge("tail_decides_sign", P(0, 0), P(1, 1), P(C(1, Pow2(-200)), C(1)), -1),
        Edge("after_swap_on_x", SwapEdges[0].A, SwapEdges[0].B, SwapEdges[0].P, -1),
        Edge("after_swap_on_y_tie", SwapEdges[1].A, SwapEdges[1].B, SwapEdges[1].P, 1),
        Edge("after_swap_exact_zero", SwapEdges[2].A, SwapEdges[2].B, SwapEdges[2].P, 0),
        Edge("empty_factor_horizontal_edge", P(C(-1, Pow2(-70)), FlatY), P(C(2, Pow2(-71)), FlatY), P(C(.5), C(3, -Pow2(-60))), 1),
        Edge("empty_factors_point_on_start", P(LineX, LineY), P(4, -1), P(LineX, LineY), 0),
        Edge("dense_eight_term_differences", DenseA, DenseB, DenseP, null),
        // Single-term inputs against zero, so each difference is one double and the product is the exact product.
        Edge("single_term_underflow_refusal", P(0, 0), P(1e-160, 0), P(0, 1e-160), 1,
            () => BelowExactProductFloor(Oracle.Difference(C(1e-160), default) * Oracle.Difference(C(1e-160), default))),
        // Some term of any expansion of 1 + 2^-1000 holds its lowest set bit, so it is below 2^(-1000 + 53), and its
        // product with the single term 2^-30 is below 2^-969.
        Edge("tail_component_underflow_refusal", P(0, 0), P(C(1, Pow2(-1000)), C(0)), P(0, Pow2(-30)), 1,
            () => Oracle.Difference(C(1, Pow2(-1000)), default).LowestBit + 53 - 30 <= -969),
        Edge("product_overflow_refusal", P(-1e200, 0), P(1e200, 0), P(0, 1e200), 1,
            () => (Oracle.Difference(C(1e200), C(-1e200)) * Oracle.Difference(C(1e200), default)).CompareTo(Oracle.Overflow) >= 0),
        Dot("four_term", P(LineX, LineY), P(C(2, Pow2(-60)), C(-1, Pow2(-90))), P(C(.5, -Pow2(-70)), C(.25)),
            P(C(-3, Pow2(-100)), C(7, -Pow2(-55))), null),
        Dot("dense_eight_term_differences", DenseA, DenseB, DenseA, DenseP, null),
        Area("interval_sign_early_return", AreaEarly, 1),
        Area("fallback_tilted_axis", AreaTilted, 1),
        Area("fallback_zero_axis_components", AreaZeroAxis, 1),
        Area("exact_zero_with_straddling_bounds", AreaCollinear, 0),
        Area("stale_normal_tail_between_axes", AreaStaleTail, null),
        Area("second_axis_refusal_after_first_success", AreaSecondAxisRefusal, -1,
            () => BelowExactProductFloor(Oracle.Exact(Pow2(-600)) * Oracle.Exact(Pow2(-600)))),
        Difference("empty_four_term", LineX, LineX, 0),
        Difference("eight_components", EightA, EightB, 1),
        Difference("tail_decides_sign", C(1, Pow2(-80)), C(1, Pow2(-81)), 1),
        Difference("reordered_components_cancel", C(1, Pow2(-60)), C(Pow2(-60), 1), 0),
        Difference("subnormal_headroom_tail", C(-1, Pow2(-500), -Pow2(-1000)), C(-1, Pow2(-500)), -1),
    ];

    static readonly DistanceCase[] DistanceCases =
    [
        new("equal_budget", P(0, 0, 0), P(3, 4, 0), 5f, 0),
        new("equal_budget_four_term_tails", P(C(Pow2(20), Pow2(-40), Pow2(-100)), C(-Pow2(20), Pow2(-70)), C(.5, Pow2(-80))),
            P(C(Pow2(20) + 3, Pow2(-40), Pow2(-100)), C(-Pow2(20) + 4, Pow2(-70)), C(.5, Pow2(-80))), 5f, 0),
        new("one_float_ulp_over", P(0, 0, 0), P(3, 4, 0), MathF.BitDecrement(5f), 1),
        new("tail_over_budget", P(0, 0, 0), P(C(3), C(4, Pow2(-120))), 5f, 1),
        new("tail_under_budget", P(0, 0, 0), P(C(3), C(4, -Pow2(-120))), 5f, -1),
        new("negative_budget", P(0, 0, 0), P(0, 0, 0), -1f, null),
        new("nan_budget", P(0, 0, 0), P(0, 0, 0), float.NaN, null),
        new("infinite_budget", P(0, 0, 0), P(0, 0, 0), float.PositiveInfinity, null),
        new("identical_points_zero_budget", P(LineX, LineY, LineX), P(LineX, LineY, LineX), 0f, 0),
        new("distinct_points_zero_budget", P(0, 0, 0), P(0, Pow2(-400), 0), 0f, 1),
        // The X square succeeds first, then the single-term Y square falls below the exact-product floor.
        new("mid_loop_underflow_refusal", P(0, 0, 0), P(1, 1e-170, 0), 2f, -1,
            () => BelowExactProductFloor(Oracle.Exact(1e-170) * Oracle.Exact(1e-170))),
        new("dense_eight_term_within", DenseA, DenseB, 4f, -1),
        new("dense_eight_term_over", DenseA, DenseB, 2f, 1),
    ];

    // Captured from this suite's capture lines on the unchanged zero-initialized kernels at 0c59e4299. Zero signs follow IEEE
    // negation and round-to-nearest sums, and refusals are compared by class because NaN bits are not contractual.
    static readonly Dictionary<string, Golden> ScalarGoldens = new()
    {
        ["area.exact_zero_with_straddling_bounds"] = G(0, 0x0000000000000000, 0x0000000000000000, 0x0000000000000000),
        ["area.fallback_tilted_axis"] = G(1, 0x3D76800000000000, 0x3D767FFFFFFFFFFF, 0x3D76800000000001),
        ["area.fallback_zero_axis_components"] = G(1, 0x3D74000000000000, 0x3D73FFFFFFFFFFFF, 0x3D74000000000001),
        ["area.interval_sign_early_return"] = G(1, 0x3FF0000000000000, 0x3FF0000000000000, 0x3FF0000000000000),
        ["area.second_axis_refusal_after_first_success"] = Refused,
        ["area.stale_normal_tail_between_axes"] = G(1, 0x3EB8000740000000, 0x3EB800073FFFFFFF, 0x3EB8000740000001),
        ["difference.eight_components"] = G(1, 0x3FF0000000000001, 0x3FF0000000000000, 0x3FF0000000000002),
        ["difference.empty_four_term"] = G(0, 0x0000000000000000, 0x0000000000000000, 0x0000000000000000),
        ["difference.reordered_components_cancel"] = G(0, 0x0000000000000000, 0x0000000000000000, 0x0000000000000000),
        ["difference.subnormal_headroom_tail"] = G(-1, 0x8170000000000000, 0x8170000000000000, 0x8170000000000000),
        ["difference.tail_decides_sign"] = G(1, 0x3AE0000000000000, 0x3AE0000000000000, 0x3AE0000000000000),
        ["dot.dense_eight_term_differences"] = G(1, 0x3FF800000000000F, 0x3FF800000000000E, 0x3FF8000000000010),
        ["dot.four_term"] = G(-1, 0xC00B800000000000, 0xC00B800000000001, 0xC00B7FFFFFFFFFFF),
        ["edge.after_swap_exact_zero"] = G(0, 0x8000000000000000, 0x8000000000000000, 0x8000000000000000),
        ["edge.after_swap_on_x"] = G(-1, 0xBFD0000000000000, 0xBFD0000000000001, 0xBFCFFFFFFFFFFFFF),
        ["edge.after_swap_on_y_tie"] = G(1, 0x3B90000000000000, 0x3B8FFFFFFFFFFFFF, 0x3B90000000000001),
        ["edge.collinear_four_term_exact_zero"] = G(0, 0x0000000000000000, 0x0000000000000000, 0x0000000000000000),
        ["edge.dense_eight_term_differences"] = G(-1, 0xC000000000000032, 0xC000000000000033, 0xC000000000000031),
        ["edge.empty_factor_horizontal_edge"] = G(1, 0x401E000000000000, 0x401DFFFFFFFFFFFF, 0x401E000000000001),
        ["edge.empty_factors_point_on_start"] = G(0, 0x0000000000000000, 0x0000000000000000, 0x0000000000000000),
        ["edge.product_overflow_refusal"] = Refused,
        ["edge.single_term_underflow_refusal"] = Refused,
        ["edge.tail_component_underflow_refusal"] = Refused,
        ["edge.tail_decides_sign"] = G(-1, 0xB370000000000000, 0xB370000000000000, 0xB370000000000000),
    };
    static readonly Dictionary<string, bool> DistanceGoldens = new()
    {
        ["dense_eight_term_over"] = false,
        ["dense_eight_term_within"] = true,
        ["distinct_points_zero_budget"] = false,
        ["equal_budget"] = true,
        ["equal_budget_four_term_tails"] = true,
        ["identical_points_zero_budget"] = true,
        ["infinite_budget"] = false,
        ["mid_loop_underflow_refusal"] = false,
        ["nan_budget"] = false,
        ["negative_budget"] = false,
        ["one_float_ulp_over"] = false,
        ["tail_over_budget"] = false,
        ["tail_under_budget"] = true,
    };

    public static TheoryData<string> ScalarCaseNames => Names(ScalarCases.Select(c => c.Name));
    public static TheoryData<string> DistanceCaseNames => Names(DistanceCases.Select(c => c.Name));
    static TheoryData<string> Names(IEnumerable<string> names)
    {
        var data = new TheoryData<string>();
        foreach (string name in names) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ScalarCaseNames))]
    public void Scalar_kernel_matches_the_exact_oracle_and_the_initialized_baseline(string name)
    {
        ScalarCase c = ScalarCases.Single(s => s.Name == name);
        CoverageScalar result = c.Evaluate();
        output.WriteLine($"capture scalar {name} {Describe(result)}");
        CoverageExactValue exact = c.Exact();
        if (c.DesignSign is int designed) Assert.Equal(designed, exact.Sign);
        if (c.Refusal is null)
        {
            Assert.True(result.IsFinite, $"{name} refused a supported case");
            Assert.Equal(exact.Sign, result.Sign);
            Assert.True(Oracle.Contains(result.Bounds, exact), $"{name}: [{result.Bounds.Min:R}, {result.Bounds.Max:R}] excludes {exact}");
        }
        else
        {
            Assert.True(c.Refusal(), $"{name} does not meet its independent unsupported-product condition");
            AssertRefused(result, name);
        }
        Assert.True(ScalarGoldens.TryGetValue(name, out Golden golden), $"{name} has no captured baseline");
        AssertGolden(golden, result, name);
        PoisonStack();
        AssertGolden(golden, c.Evaluate(), $"{name} after stack poison");
    }

    [Theory]
    [MemberData(nameof(DistanceCaseNames))]
    public void Squared_distance_budget_matches_the_exact_comparison_and_the_initialized_baseline(string name)
    {
        DistanceCase c = DistanceCases.Single(s => s.Name == name);
        bool result = CoverageGeometry.SquaredDistanceWithinBudget(c.A, c.B, c.Budget);
        output.WriteLine($"capture distance {name} result={result}");
        if (!float.IsFinite(c.Budget) || c.Budget < 0)
        {
            Assert.Null(c.DesignComparison);
            Assert.False(result);
        }
        else
        {
            CoverageExactValue budget = Oracle.Exact(c.Budget);
            int comparison = Oracle.SquaredDistance(c.A, c.B).CompareTo(budget * budget);
            Assert.Equal(c.DesignComparison, comparison);
            if (c.Refusal is null) Assert.Equal(comparison <= 0, result);
            else
            {
                Assert.True(comparison <= 0 && c.Refusal(), $"{name} is not an in-budget unsupported product");
                Assert.False(result);
            }
        }
        Assert.True(DistanceGoldens.TryGetValue(name, out bool golden), $"{name} has no captured baseline");
        Assert.Equal(golden, result);
        PoisonStack();
        Assert.Equal(golden, CoverageGeometry.SquaredDistanceWithinBudget(c.A, c.B, c.Budget));
    }

    [Fact]
    public void Dense_cases_carry_eight_term_differences()
    {
        Span<double> values = stackalloc double[8];
        Assert.Equal(8, PrivateDifference(EightA, EightB, values));
        foreach ((CoverageCoordinate a, CoverageCoordinate b) in new[] { (DenseB.X, DenseA.X), (DenseB.Y, DenseA.Y),
            (DenseB.Z, DenseA.Z), (DenseP.X, DenseA.X), (DenseP.Y, DenseA.Y) })
            Assert.Equal(8, PrivateDifference(a, b, values));
    }

    [Fact]
    public void Swap_cases_take_the_after_branch_and_negate_the_reversed_edge_exactly()
    {
        foreach (var (a, b, p) in SwapEdges)
        {
            Assert.True(Oracle.After(a, b));
            Assert.False(Oracle.After(b, a));
            CoverageScalar forward = CoverageGeometry.Edge(a, b, p), reversed = CoverageGeometry.Edge(b, a, p);
            Assert.Equal(-reversed.Sign, forward.Sign);
            Assert.Equal(Bits(-reversed.Estimate), Bits(forward.Estimate));
            Assert.Equal(Bits(-reversed.Bounds.Max), Bits(forward.Bounds.Min));
            Assert.Equal(Bits(-reversed.Bounds.Min), Bits(forward.Bounds.Max));
        }
    }

    [Fact]
    public void Projected_area_cases_reach_their_intended_paths()
    {
        CoverageScalarBounds early = Prefilter(AreaEarly);
        Assert.True(early.IsFinite && (early.Min > 0 || early.Max < 0));
        foreach (AreaInput fallback in new[] { AreaTilted, AreaZeroAxis, AreaCollinear, AreaStaleTail, AreaSecondAxisRefusal })
        {
            CoverageScalarBounds bound = Prefilter(fallback);
            Assert.True(bound.IsFinite && bound.Min <= 0 && bound.Max >= 0 && bound != CoverageIntervals.Zero, $"{bound} does not fall back");
        }
        Assert.Equal(0, AreaZeroAxis.Axis.X);
        Assert.Equal(0, AreaZeroAxis.Axis.Y);

        var (stale, staleFailure) = AxisWork(AreaStaleTail);
        Assert.Equal(-1, staleFailure);
        Assert.True(stale[0] > stale[1] && stale[1] > 0, $"axis normal counts {string.Join(", ", stale)}");

        var (refusal, refusedAxis) = AxisWork(AreaSecondAxisRefusal);
        Assert.Equal(1, refusedAxis);
        Assert.True(refusal[0] > 0);
    }

    [Fact]
    public void Grow_writes_only_its_new_prefix_and_keeps_the_exact_sum()
    {
        double[] expansion = Enumerable.Repeat(Sentinel, 6).ToArray();
        int length = 0;
        GrowStep(expansion, ref length, 0, 0);
        GrowStep(expansion, ref length, 1, 1);
        GrowStep(expansion, ref length, Pow2(-80), 2);
        GrowStep(expansion, ref length, Odd(3, -160), 3);
        // Cancelling the head shrinks the prefix and leaves slot 2 stale and outside the tracked length.
        GrowStep(expansion, ref length, -1, 2);
        GrowStep(expansion, ref length, Odd(5, 40), 3);
    }

    [Fact]
    public void Private_difference_writes_only_its_length_prefix_from_zero()
    {
        DifferencePrefix(EightA, EightB, 8);
        DifferencePrefix(LineX, LineX, 0);
        DifferencePrefix(LineX, LineY, null);
    }

    [Fact]
    public void Products_grow_only_the_output_prefix_and_refuse_without_further_writes()
    {
        double[] a = [Odd(1, 0), Odd(3, -70)], b = [Odd(5, -10), -Odd(7, -90)];
        double[] expansion = Enumerable.Repeat(Sentinel, 12).ToArray();
        int length = 0;
        Assert.True(PrivateProducts(a, b, -1, expansion, ref length));
        Assert.InRange(length, 1, 2 * a.Length * b.Length);
        AssertPrefix(expansion, length, -(CoverageExactValue.Sum(a) * CoverageExactValue.Sum(b)));
        // Each pair grows at most two values from length 0, so nothing is written at or above 2|a||b|.
        Assert.All(expansion[(2 * a.Length * b.Length)..], v => Assert.Equal(Bits(Sentinel), Bits(v)));

        // The first pair is supported. The second pair's product is below 2^-969, so the refusal follows two Grow calls.
        double[] head = [1, Pow2(-1000)], factor = [Pow2(-30)];
        expansion = Enumerable.Repeat(Sentinel, 12).ToArray();
        length = 0;
        Assert.False(PrivateProducts(head, factor, 1, expansion, ref length));
        Assert.Equal(1, length);
        AssertPrefix(expansion, length, CoverageExactValue.FromDouble(Pow2(-30)));
        Assert.All(expansion[2..], v => Assert.Equal(Bits(Sentinel), Bits(v)));
    }

    static void GrowStep(double[] expansion, ref int length, double value, int expectedLength)
    {
        ulong[] before = expansion.Select(Bits).ToArray();
        CoverageExactValue sum = CoverageExactValue.Sum(expansion.AsSpan(0, length)) + CoverageExactValue.FromDouble(value);
        int previous = length;
        CoverageGeometry.Grow(expansion, ref length, value);
        Assert.Equal(expectedLength, length);
        Assert.InRange(length, 0, previous + 1);
        AssertPrefix(expansion, length, sum);
        for (int i = length; i < expansion.Length; i++) Assert.Equal(before[i], Bits(expansion[i]));
    }

    static void DifferencePrefix(CoverageCoordinate a, CoverageCoordinate b, int? expectedLength)
    {
        double[] values = Enumerable.Repeat(Sentinel, 16).ToArray();
        int length = PrivateDifference(a, b, values);
        if (expectedLength is int expected) Assert.Equal(expected, length);
        Assert.InRange(length, 0, 8);
        AssertPrefix(values, length, Oracle.Difference(a, b));
        // Slots in [length, 8) may hold values this call wrote before a later cancellation shrank the prefix. They are
        // never read. Eight Grow calls from length 0 never write at index 8 or above.
        Assert.All(values[8..], v => Assert.Equal(Bits(Sentinel), Bits(v)));
    }

    static void AssertPrefix(double[] values, int length, CoverageExactValue exact)
    {
        Assert.All(values[..length], v => Assert.True(double.IsFinite(v) && v != 0, $"prefix component {v:R}"));
        Assert.Equal(0, CoverageExactValue.Sum(values.AsSpan(0, length)).CompareTo(exact));
    }

    // Path witness only. It mirrors the ProjectedArea interval prefilter and never supplies an expected value.
    static CoverageScalarBounds Prefilter(AreaInput i)
    {
        var a = CoverageBoundedVector.From(i.A);
        return CoverageBoundedVector.Dot(CoverageBoundedVector.Cross(CoverageBoundedVector.From(i.B) - a, CoverageBoundedVector.From(i.C) - a),
            CoverageBoundedVector.From(i.Axis));
    }

    // Path witness only. It replays the per-axis normal products through the private kernels to report each axis's
    // normal length and the first refusing axis, or -1 when every axis succeeds.
    static (int[] Counts, int FailedAxis) AxisWork(AreaInput input)
    {
        double[] differences = new double[48], normal = new double[256], expansion = new double[1536];
        int[] lengths = new int[6], counts = [-1, -1, -1];
        for (int i = 0; i < 3; i++)
        {
            lengths[i] = PrivateDifference(Coordinate(input.B, i), Coordinate(input.A, i), differences.AsSpan(i * 8, 8));
            lengths[i + 3] = PrivateDifference(Coordinate(input.C, i), Coordinate(input.A, i), differences.AsSpan((i + 3) * 8, 8));
        }
        int length = 0;
        for (int i = 0; i < 3; i++)
        {
            double direction = i switch { 0 => input.Axis.X, 1 => input.Axis.Y, _ => input.Axis.Z };
            if (direction == 0) continue;
            int j = (i + 1) % 3, k = (i + 2) % 3, count = 0;
            if (!PrivateProducts(differences.AsSpan(j * 8, lengths[j]), differences.AsSpan((k + 3) * 8, lengths[k + 3]), 1, normal, ref count)
                || !PrivateProducts(differences.AsSpan(k * 8, lengths[k]), differences.AsSpan((j + 3) * 8, lengths[j + 3]), -1, normal, ref count))
                return (counts, i);
            counts[i] = count;
            if (!PrivateProducts(normal.AsSpan(0, count), new[] { direction }, 1, expansion, ref length)) return (counts, i);
        }
        return (counts, -1);

        static CoverageCoordinate Coordinate(CoverageAffinePoint p, int i) => i switch { 0 => p.X, 1 => p.Y, _ => p.Z };
    }

    static readonly DifferenceInto PrivateDifference = Kernel<DifferenceInto>("Difference", typeof(CoverageCoordinate),
        typeof(CoverageCoordinate), typeof(Span<double>));
    static readonly ProductsInto PrivateProducts = Kernel<ProductsInto>("Products", typeof(ReadOnlySpan<double>),
        typeof(ReadOnlySpan<double>), typeof(int), typeof(Span<double>), typeof(int).MakeByRefType());
    static T Kernel<T>(string name, params Type[] parameters) where T : Delegate =>
        (typeof(CoverageGeometry).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, parameters)
            ?? throw new MissingMethodException(nameof(CoverageGeometry), name)).CreateDelegate<T>();

    // Writes NaN over a frame-sized stack region the next kernel frame is likely, not guaranteed, to reuse. It reads
    // only slots it wrote, and the escaping span keeps the fill observable.
    [MethodImpl(MethodImplOptions.NoInlining), SkipLocalsInit]
    static double PoisonStack()
    {
        Span<double> poison = stackalloc double[4096];
        poison.Fill(double.NaN);
        return Observe(poison);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static double Observe(ReadOnlySpan<double> written) => written[^1];

    static ulong Bits(double value) => BitConverter.DoubleToUInt64Bits(value);

    // NaN sign and payload are not part of the refusal contract, so a refusal is checked by class, not by bits.
    static bool IsRefused(CoverageScalar result) =>
        double.IsNaN(result.Estimate) && double.IsNaN(result.Bounds.Min) && double.IsNaN(result.Bounds.Max);

    static string Describe(CoverageScalar result) => IsRefused(result) ? $"refused sign={result.Sign}"
        : $"sign={result.Sign} estimate=0x{Bits(result.Estimate):X16} min=0x{Bits(result.Bounds.Min):X16} max=0x{Bits(result.Bounds.Max):X16}";

    static void AssertRefused(CoverageScalar result, string name)
    {
        Assert.True(IsRefused(result), $"{name} did not refuse: {Describe(result)}");
        Assert.Equal(0, result.Sign);
    }

    static void AssertGolden(Golden golden, CoverageScalar result, string name)
    {
        if (golden.Refused)
        {
            AssertRefused(result, name);
            return;
        }
        Assert.True(golden == new Golden(false, result.Sign, Bits(result.Estimate), Bits(result.Bounds.Min), Bits(result.Bounds.Max)),
            $"{name}: {Describe(result)} differs from the captured baseline");
    }

    static Golden G(int sign, ulong estimate, ulong min, ulong max) => new(false, sign, estimate, min, max);
    static Golden Refused => new(true, 0, 0, 0, 0);
}
