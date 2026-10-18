using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>The scalar binary32 inventory with deterministic signed-zero flushing. Each flushed operand or
/// result counts once, and the trace retains the first value's original bits. The trace is diagnostic only.</summary>
[Binary32Arithmetic]
internal sealed class CoverageReferenceArithmetic
{
    CoverageExactnessPhase phase;
    int element, slot = -1;
    internal int EventCount { get; private set; }
    internal CoverageExactnessSite FirstSite { get; private set; }
    internal int FirstSlot { get; private set; } = -1;

    internal void Reset()
    {
        EventCount = 0;
        FirstSite = default;
        FirstSlot = -1;
        phase = default;
        element = 0;
        slot = -1;
    }

    internal void At(CoverageExactnessPhase phase, int element, int slot = -1)
    { this.phase = phase; this.element = element; this.slot = slot; }

    internal float Add(float a, float b)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.Add;
        a = Flush(a, op); b = Flush(b, op);
        return Flush(a + b, op);
    }

    internal float Sub(float a, float b)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.Subtract;
        a = Flush(a, op); b = Flush(b, op);
        return Flush(a - b, op);
    }

    internal float Mul(float a, float b)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.Multiply;
        a = Flush(a, op); b = Flush(b, op);
        return Flush(a * b, op);
    }

    internal float Div(float a, float b)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.Divide;
        a = Flush(a, op); b = Flush(b, op);
        return Flush(a / b, op);
    }

    internal float Min(float a, float b)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.Min;
        a = Flush(a, op); b = Flush(b, op);
        return Flush(MathF.Min(a, b), op);
    }

    internal float Max(float a, float b)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.Max;
        a = Flush(a, op); b = Flush(b, op);
        return Flush(MathF.Max(a, b), op);
    }

    internal float Sqrt(float a)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.SquareRoot;
        a = Flush(a, op);
        return Flush(MathF.Sqrt(a), op);
    }

    internal float Abs(float a)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.Abs;
        a = Flush(a, op);
        return Flush(MathF.Abs(a), op);
    }

    internal float Neg(float a)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.Negate;
        a = Flush(a, op);
        return Flush(-a, op);
    }

    internal float Clamp(float value, float min, float max)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.Clamp;
        value = Flush(value, op); min = Flush(min, op); max = Flush(max, op);
        return Flush(Math.Clamp(value, min, max), op);
    }

    internal float Read(float value) => Flush(value, CoverageExactnessOperation.Store);

    internal bool Less(float a, float b)
    {
        a = Flush(a, CoverageExactnessOperation.Compare); b = Flush(b, CoverageExactnessOperation.Compare);
        return a < b;
    }

    internal bool LessOrEqual(float a, float b)
    {
        a = Flush(a, CoverageExactnessOperation.Compare); b = Flush(b, CoverageExactnessOperation.Compare);
        return a <= b;
    }

    internal bool Greater(float a, float b)
    {
        a = Flush(a, CoverageExactnessOperation.Compare); b = Flush(b, CoverageExactnessOperation.Compare);
        return a > b;
    }

    internal bool GreaterOrEqual(float a, float b)
    {
        a = Flush(a, CoverageExactnessOperation.Compare); b = Flush(b, CoverageExactnessOperation.Compare);
        return a >= b;
    }

    internal bool Equal(float a, float b)
    {
        a = Flush(a, CoverageExactnessOperation.Compare); b = Flush(b, CoverageExactnessOperation.Compare);
        return a == b;
    }

    internal bool NotEqual(float a, float b)
    {
        a = Flush(a, CoverageExactnessOperation.Compare); b = Flush(b, CoverageExactnessOperation.Compare);
        return a != b;
    }

    internal bool IsFinite(float value) => IsFinite(ref value);

    internal bool IsFinite(ref float value)
    {
        value = Flush(value, CoverageExactnessOperation.FiniteTest);
        return (BitConverter.SingleToUInt32Bits(value) & 0x7f800000u) != 0x7f800000u;
    }

    internal CoverageReferenceScalar Edge(Vector3 a, Vector3 b, Vector3 p)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.Predicate;
        a = PredicatePoint(a, op); b = PredicatePoint(b, op); p = PredicatePoint(p, op);
        var result = CoverageReferencePredicates.Edge(a, b, p);
        return new(result.Sign, Flush(result.Value, op));
    }

    internal CoverageReferenceScalar Dot(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.Predicate;
        a = PredicatePoint(a, op); b = PredicatePoint(b, op); c = PredicatePoint(c, op); d = PredicatePoint(d, op);
        var result = CoverageReferencePredicates.Dot(a, b, c, d);
        return new(result.Sign, Flush(result.Value, op));
    }

    internal float CrossingParameter(Vector3 s, Vector3 t, Vector3 a, Vector3 b)
    {
        const CoverageExactnessOperation op = CoverageExactnessOperation.CrossingSide;
        s = PredicatePoint(s, op); t = PredicatePoint(t, op); a = PredicatePoint(a, op); b = PredicatePoint(b, op);
        if (!CoverageReferencePredicates.TryScaledSides(s, t, a, b, out float x, out float y)) return float.NaN;
        x = Flush(x, op); y = Flush(y, op);
        return Div(x, Sub(x, y));
    }

    Vector3 PredicatePoint(Vector3 value, CoverageExactnessOperation op) => new(Flush(value.X, op), Flush(value.Y, op), value.Z);

    float Flush(float value, CoverageExactnessOperation operation)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value), magnitude = bits & 0x7fffffffu;
        // Include 2^-126 to make before-rounding and after-rounding hardware tininess modes equivalent.
        if (magnitude == 0 || magnitude > 0x00800000u) return value;
        if (EventCount == 0) { FirstSite = new(phase, element, operation, bits); FirstSlot = slot; }
        EventCount++;
        return BitConverter.UInt32BitsToSingle(bits & 0x80000000u);
    }
}
