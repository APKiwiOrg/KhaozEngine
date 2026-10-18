using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageReferenceArithmeticTests
{
    [Fact]
    public void Arithmetic_pins_binary32_rounding_and_signed_zero()
    {
        var f = new CoverageReferenceArithmetic();
        Assert.Equal(1f, f.Add(1f, MathF.ScaleB(1, -24)));
        foreach (var (a, b) in new[] { (1.25f, -.75f), (float.MaxValue, 2f), (16777216f, -16777216f),
            (MathF.BitDecrement(2), MathF.BitIncrement(1)), (-0f, 0f), (-9f, -3f) })
        {
            Bits(a + b, f.Add(a, b));
            Bits(a - b, f.Sub(a, b));
            Bits(a * b, f.Mul(a, b));
            Bits(a / b, f.Div(a, b));
            Bits(MathF.Sqrt(a), f.Sqrt(a));
            Bits(MathF.Abs(a), f.Abs(a));
            Bits(-a, f.Neg(a));
            Bits(MathF.Min(a, b), f.Min(a, b));
            Bits(MathF.Max(a, b), f.Max(a, b));
            Assert.Equal(a < b, f.Less(a, b));
            Assert.Equal(a <= b, f.LessOrEqual(a, b));
            Assert.Equal(a > b, f.Greater(a, b));
            Assert.Equal(a >= b, f.GreaterOrEqual(a, b));
            Assert.Equal(a == b, f.Equal(a, b));
            Assert.Equal(a != b, f.NotEqual(a, b));
            Assert.Equal(float.IsFinite(a), f.IsFinite(a));
        }
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(f.Max(-0f, 0f)));
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(f.Min(0f, -0f)));
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(f.Clamp(-0f, 0f, 1f)));
        Assert.True(f.Equal(-0f, 0f));
        Assert.Equal(0, f.EventCount);
    }

    [Fact]
    public void Trace_counts_flushes_and_keeps_the_first_site_until_reset()
    {
        var f = new CoverageReferenceArithmetic();
        float tiny = MathF.ScaleB(1, -130);
        f.At(CoverageExactnessPhase.Conformation, 7, 1);
        Bits(0, f.Mul(tiny, 1));
        Assert.Equal(new CoverageExactnessSite(CoverageExactnessPhase.Conformation, 7,
            CoverageExactnessOperation.Multiply, BitConverter.SingleToUInt32Bits(tiny)), f.FirstSite);
        Assert.Equal(1, f.FirstSlot);
        Assert.Equal(1, f.EventCount);
        f.At(CoverageExactnessPhase.NormalGather, 3);
        Bits(0, f.Mul(MathF.ScaleB(1, -63), MathF.ScaleB(1, -64)));
        Assert.Equal(2, f.EventCount);
        Assert.Equal(CoverageExactnessPhase.Conformation, f.FirstSite.Phase);
        f.Reset();
        Assert.Equal((0, default(CoverageExactnessSite), -1), (f.EventCount, f.FirstSite, f.FirstSlot));
    }

    [Fact]
    public void Smallest_normal_is_flushed_and_zero_keeps_its_sign()
    {
        var f = new CoverageReferenceArithmetic();
        float smallest = MathF.ScaleB(1, -126);
        foreach (float value in new[] { smallest, -smallest, MathF.ScaleB(1, -130), -MathF.ScaleB(1, -130) })
        {
            f.Reset();
            uint zero = BitConverter.SingleToUInt32Bits(value) & 0x80000000u;
            Assert.Equal(zero, BitConverter.SingleToUInt32Bits(f.Mul(value, 1)));
            Assert.Equal(1, f.EventCount);
            Assert.Equal(BitConverter.SingleToUInt32Bits(value), f.FirstSite.ValueBits);
        }
        foreach (float value in new[] { 0f, -0f, MathF.BitIncrement(smallest), -MathF.BitIncrement(smallest) })
        {
            f.Reset();
            Bits(value, f.Mul(value, 1));
            Assert.Equal(0, f.EventCount);
        }
        f.Reset();
        Bits(0f, f.Mul(MathF.ScaleB(1, -63), MathF.ScaleB(1, -63)));
        Assert.Equal(1, f.EventCount);
        f.Reset();
        Bits(-0f, f.Mul(-MathF.ScaleB(1, -63), MathF.ScaleB(1, -63)));
        Assert.Equal(1, f.EventCount);
    }

    [Fact]
    public void Every_home_operation_consumes_flushed_operands_and_flushes_its_result()
    {
        float tiny = MathF.ScaleB(1, -130);
        var f = new CoverageReferenceArithmetic();
        Bits(0, f.Add(tiny, tiny));
        Assert.Equal(2, f.EventCount);
        Bits(-0f, f.Sub(-tiny, tiny));
        Bits(0, f.Mul(tiny, 2));
        Bits(0, f.Div(tiny, 1));
        Bits(-0f, f.Sqrt(-tiny));
        Bits(0, f.Abs(-tiny));
        Bits(-0f, f.Neg(tiny));
        Bits(-0f, f.Min(tiny, -tiny));
        Bits(0, f.Max(-tiny, tiny));
        Bits(-0f, f.Clamp(-tiny, 0, 1));
        Assert.True(f.Equal(-tiny, 0));
        Assert.False(f.NotEqual(tiny, 0));
        Assert.False(f.Less(-tiny, 0));
        Assert.True(f.LessOrEqual(tiny, 0));
        Assert.False(f.Greater(tiny, 0));
        Assert.True(f.GreaterOrEqual(-tiny, 0));
        Assert.True(f.IsFinite(tiny));
        f.Reset();
        Bits(0, f.Div(MathF.ScaleB(1, -63), MathF.ScaleB(1, 64)));
        Assert.Equal(1, f.EventCount);
    }

    [Fact]
    public void Predicate_inputs_and_rounded_values_are_traced()
    {
        var f = new CoverageReferenceArithmetic();
        _ = f.Edge(new(MathF.ScaleB(1, -130), 0, float.Epsilon), Vector3.UnitX, Vector3.UnitY);
        Assert.Equal(CoverageExactnessOperation.Predicate, f.FirstSite.Operation);
        Assert.Equal(BitConverter.SingleToUInt32Bits(MathF.ScaleB(1, -130)), f.FirstSite.ValueBits);
        f.Reset();
        var determinant = f.Edge(Vector3.Zero, new(MathF.ScaleB(1, -70), 0, 0), new(0, MathF.ScaleB(1, -70), 0));
        Bits(0, determinant.Value);
        Assert.Equal(1, determinant.Sign);
        Assert.Equal(1, f.EventCount);
        Assert.Equal(CoverageExactnessOperation.Predicate, f.FirstSite.Operation);
        f.Reset();
        _ = f.Dot(Vector3.Zero, new(MathF.ScaleB(1, -70), 0, 0), Vector3.Zero, new(MathF.ScaleB(1, -70), 0, 0));
        Assert.Equal(1, f.EventCount);
        f.Reset();
        _ = f.Edge(new(0, 0, float.Epsilon), Vector3.UnitX, Vector3.UnitY);
        Assert.Equal(0, f.EventCount);
        _ = f.CrossingParameter(Vector3.Zero, Vector3.UnitX, new(0, 1, 0), new(1, -MathF.ScaleB(1, -130), 0));
        Assert.Equal(CoverageExactnessOperation.CrossingSide, f.FirstSite.Operation);
    }

    [Fact]
    public void Grid_cell_compare_count_matches_truncating_cell()
    {
        var f = new CoverageReferenceArithmetic();
        foreach (var (min, max) in new[] { (-3f, 5f), (0f, 1f), (2f, 2f) })
        {
            var values = Enumerable.Range(0, 17).SelectMany(k =>
            {
                float boundary = k * (max - min) / 16 + min;
                return new[] { boundary, MathF.BitDecrement(boundary), MathF.BitIncrement(boundary) };
            }).Concat(new[] { min - 1, max + 1, float.NaN, float.NegativeInfinity, float.PositiveInfinity });
            foreach (float value in values)
                Assert.Equal(Math.Clamp((int)(((value - min) / (max - min)) * 16), 0, 15),
                    KhaozEngine.Render3D.CoverageReferenceSupport.Cell(f, value, min, max));
        }
    }

    [Fact]
    public void Device_path_members_are_binary32_kernels()
    {
        Assert.NotNull(typeof(CoverageReferenceArithmetic).GetCustomAttribute<Binary32ArithmeticAttribute>());
        foreach (var (type, names) in new[]
        {
            (typeof(CoverageReferenceMath), "Dot3 Dot2 Cross Point Linear Height EdgeHeight Sub3 Equal3 IsFinite3 IsFiniteVertex ReadVertex"),
            (typeof(CoverageReferenceConstructor), "ValidateSource AliasesEqual Interpolate ValidClearance IsActive Conform Position Edge TryVertex Direction Sum Value Magnitude"),
            (typeof(CoverageReferenceCorrection), "PrepareFace FinishFace GatherGroup CheckFullTarget TransitionGroup StoreTransition"),
            (typeof(CoverageReferenceNormals), "ValidateVertex ValidateFace UpdateVertex Gather TryBasis Unit Combine Divide Moved Changed"),
            (typeof(KhaozEngine.Render3D.CoverageReferenceSupport), "TryHeight TryBoundary OverlayMaximum OverlayPair OverlayInside OverlayAccumulate Bounds Cell"),
            (typeof(CoverageReferenceSkinning), "TrySkinVertex"),
            (typeof(CoverageFrames), "Refuse"),
            (typeof(CoverageTally), "Note")
        })
            foreach (string name in names.Split(' '))
            {
                var methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)
                    .Where(m => m.Name == name).ToArray();
                Assert.NotEmpty(methods);
                Assert.All(methods, m => Assert.NotNull(m.GetCustomAttribute<Binary32KernelAttribute>()
                    ?? type.GetCustomAttribute<Binary32KernelAttribute>()));
            }
        var succeeded = typeof(CoverageTally).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Where(m => m.Name == "Succeeded");
        foreach (var method in succeeded)
            Assert.Equal(method.GetParameters()[2].ParameterType == typeof(ReadOnlySpan<int>),
                method.IsDefined(typeof(Binary32KernelAttribute)));
    }

    static void Bits(float expected, float actual) => Assert.Equal(BitConverter.SingleToUInt32Bits(expected), BitConverter.SingleToUInt32Bits(actual));
}
