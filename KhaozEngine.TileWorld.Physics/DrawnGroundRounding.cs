using System;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Operation-by-operation binary32 error bounds for the shared drawn-height expression.</summary>
internal static class DrawnGroundRounding
{
    readonly record struct Value(I Ideal, double Error) : IDrawnGroundNumber<Value>
    {
        public static Value From(float value) => new(I.Point(value), 0);
        bool Zero => Ideal.Low == 0 && Ideal.High == 0 && Error == 0;
        internal double Magnitude => Math.Max(Math.Abs(Ideal.Low), Math.Abs(Ideal.High));
        public static Value operator +(Value a, Value b)
        {
            if (a.Zero) return b;
            if (b.Zero) return a;
            return Rounded(a.Ideal + b.Ideal, (I.Point(a.Error) + I.Point(b.Error)).High);
        }
        public static Value operator -(Value a, Value b) => a + new Value(I.Point(0) - b.Ideal, b.Error);
        public static Value operator *(Value a, Value b)
        {
            if (a.Zero || b.Zero) return From(0);
            I error = I.Point(a.Magnitude) * I.Point(b.Error) + I.Point(b.Magnitude) * I.Point(a.Error) +
                I.Point(a.Error) * I.Point(b.Error);
            return Rounded(a.Ideal * b.Ideal, error.High);
        }
        public static Value Divide(Value value, float divisor) => Rounded(value.Ideal / I.Point(divisor),
            (I.Point(value.Error) / I.Point(Math.Abs(divisor))).High);
        public static Value Clamp(Value value, float minimum, float maximum) =>
            new(new I(Math.Clamp(value.Ideal.Low, minimum, maximum), Math.Clamp(value.Ideal.High, minimum, maximum)), value.Error);
        static Value Rounded(I ideal, double inherited)
        {
            if (inherited == 0 && ideal.Low == ideal.High)
                return new(ideal, Math.Abs((double)(float)ideal.Low - ideal.Low));
            double magnitude = (I.Point(Math.Max(Math.Abs(ideal.Low), Math.Abs(ideal.High))) + I.Point(inherited)).High;
            return new(ideal, (I.Point(inherited) + I.Point(HalfSpacing(magnitude))).High);
        }
    }

    internal static double Arithmetic(DrawnGroundTriangle triangle)
    {
        Value coordinate = new(new I(0, 1), 0);
        var (b, c) = DrawnGroundEvaluation.Weights(coordinate, coordinate, triangle);
        // Clamping toward the ideal triangle's vertex range cannot increase the expression error.
        return DrawnGroundEvaluation.Height(b, c, triangle).Error;
    }

    internal static double Coordinate(float minimum, float maximum, int tile, float size)
    {
        double magnitude = Math.Max(Math.Abs((double)minimum), Math.Abs((double)maximum));
        I error = I.Point(HalfSpacing(magnitude)) + I.Point(size) * I.Point(HalfSpacing(magnitude / size));
        // Subtraction from the floor is exact by Sterbenz except in the negative unit cell. Integer-to-float
        // conversion at a coarse address is bounded independently, rather than assumed exact.
        if (tile == -1) error += I.Point(size) * I.Point(HalfSpacing(1));
        error += I.Point(size) * I.Point(Math.Abs((double)(float)tile - tile));
        return error.High;
    }

    internal static double HalfSpacing(double magnitude)
    {
        if (magnitude == 0) return 0;
        float upper = (float)Math.Abs(magnitude);
        if (upper < magnitude) upper = MathF.BitIncrement(upper);
        return ((double)MathF.BitIncrement(upper) - upper) * 0.5;
    }
}
