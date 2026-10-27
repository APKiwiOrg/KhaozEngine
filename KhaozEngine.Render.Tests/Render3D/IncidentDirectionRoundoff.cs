using System;
using System.Numerics;

namespace KhaozEngine.Tests.Render3D;

// Independent double evaluation and forward-error bound for the float inverse supplied to the ray helper.
internal static class IncidentDirectionRoundoff
{
    internal readonly record struct Estimate(Vector3 Direction, float Error);

    internal static Estimate Evaluate(Matrix4x4 inverse, Vector2 ndc)
    {
        const double U = 1d / (1 << 24);
        const double Gamma7 = 7d * U / (1d - 7d * U);
        const double Gamma8 = 8d * U / (1d - 8d * U);
        double[,] m = {
            { inverse.M11, inverse.M12, inverse.M13, inverse.M14 },
            { inverse.M21, inverse.M22, inverse.M23, inverse.M24 },
            { inverse.M31, inverse.M32, inverse.M33, inverse.M34 },
            { inverse.M41, inverse.M42, inverse.M43, inverse.M44 },
        };
        double[,] point = new double[2, 3], error = new double[2, 3];
        for (int end = 0; end < 2; end++)
        {
            double[] input = [ndc.X, ndc.Y, end * 0.5d, 1d];
            double[] q = new double[4], e = new double[4];
            for (int channel = 0; channel < 4; channel++)
                for (int row = 0; row < 4; row++)
                {
                    double product = input[row] * m[row, channel];
                    q[channel] += product;
                    e[channel] += Gamma7 * Math.Abs(product);
                }
            for (int channel = 0; channel < 3; channel++)
            {
                point[end, channel] = q[channel] / q[3];
                // Four multiplies and three adds cost at most gamma_7, even without FMA.
                // Propagate numerator and denominator error through division, then its one rounding.
                double denominator = Math.Abs(q[3]) - e[3];
                error[end, channel] = (e[channel] + Math.Abs(point[end, channel]) * e[3]
                    + U * (Math.Abs(q[channel]) + e[channel])) / denominator;
            }
        }
        double[] delta = new double[3];
        double lengthSquared = 0d, errorSquared = 0d;
        for (int channel = 0; channel < 3; channel++)
        {
            delta[channel] = point[1, channel] - point[0, channel];
            double endpointError = error[0, channel] + error[1, channel];
            double e = endpointError + U * (Math.Abs(delta[channel]) + endpointError);
            lengthSquared += delta[channel] * delta[channel];
            errorSquared += e * e;
        }
        double length = Math.Sqrt(lengthSquared), deltaError = Math.Sqrt(errorSquared);
        // Normalizing perturbed d costs <= 2|error|/(|d|-|error|). Length and divide cost
        // at most gamma_8. The final sqrt(3)*u covers rounding this double reference to Vector3.
        double bound = 2d * deltaError / (length - deltaError) + Gamma8 + Math.Sqrt(3d) * U;
        return new Estimate(new Vector3((float)(delta[0] / length), (float)(delta[1] / length),
            (float)(delta[2] / length)), MathF.BitIncrement((float)bound));
    }
}
