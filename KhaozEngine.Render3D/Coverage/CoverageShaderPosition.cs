using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

// Bounds the declared shader sink and its world.x addition. All sums use absolute input magnitudes.
internal static class CoverageShaderPosition
{
    const double U = 1.0 / 8388608, N = 1.1754943508222875e-38;
    internal static double Sum(double a, double b) => Math.BitIncrement(a + b);
    static double Product(double a, double b) => Math.BitIncrement(a * b);
    static double Divide(double a, double b) => Math.BitIncrement(a / b);

    internal static double Bound(in Matrix4x4 model, in MeshBounds bounds, double attributes, double instance)
    {
        double sum = Sum(attributes, instance);
        if (!double.IsFinite(sum)) return double.PositiveInfinity;
        // At most eight inputs and seven additions. Sixteen N includes input and intermediate flushing.
        double sinkMagnitude = Divide(Sum(sum, 16 * N), 1 - 8 * U);
        if (sinkMagnitude > float.MaxValue) return double.PositiveInfinity;
        double term = Sum(Product(Product(sinkMagnitude, (double)1e-30f), 1 + U), N);
        double world = ModelMagnitude(model, bounds, 0);
        double addMagnitude = Sum(world, term);
        if (addMagnitude > float.MaxValue) return double.PositiveInfinity;
        // Either operand and the result may flush. The final addition's error propagates once.
        double addition = Divide(Sum(Product(U, addMagnitude), 3 * N), 1 - U);
        return Sum(term, addition);
    }

    internal static double ModelMagnitude(in Matrix4x4 model, in MeshBounds bounds, int coordinate) =>
        ModelMagnitude(model, bounds, coordinate, out _);
    internal static double ModelError(in Matrix4x4 model, in MeshBounds bounds, int coordinate)
    { _ = ModelMagnitude(model, bounds, coordinate, out double error); return error; }
    static double ModelMagnitude(in Matrix4x4 model, in MeshBounds bounds, int coordinate, out double error)
    {
        Span<double> input = stackalloc double[4]
        {
            Math.Max(Math.Abs((double)bounds.Min.X), Math.Abs((double)bounds.Max.X)),
            Math.Max(Math.Abs((double)bounds.Min.Y), Math.Abs((double)bounds.Max.Y)),
            Math.Max(Math.Abs((double)bounds.Min.Z), Math.Abs((double)bounds.Max.Z)), 1,
        };
        Span<float> coefficients = coordinate switch
        {
            0 => stackalloc float[4] { model.M11, model.M21, model.M31, model.M41 },
            1 => stackalloc float[4] { model.M12, model.M22, model.M32, model.M42 },
            _ => stackalloc float[4] { model.M13, model.M23, model.M33, model.M43 },
        };
        double sum = 0, denormal = 0;
        for (int i = 0; i < 4; i++)
        {
            double c = Math.Abs((double)coefficients[i]), value = input[i];
            sum = Sum(sum, Product(c, value));
            denormal = Sum(denormal, Sum(Product(value, c < N ? c : 0), Product(c, Math.Min(value, N))));
        }
        error = Divide(Sum(Product(7 * U, sum), Sum(denormal, 7 * N)), 1 - 7 * U);
        return Sum(sum, error);
    }
}
