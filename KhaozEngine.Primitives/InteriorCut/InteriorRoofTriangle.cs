using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>Clip a triangle in XZ, retaining linear height, including vertical and degenerate faces.</summary>
internal static class InteriorRoofTriangle
{
    internal static float Minimum(Vector3 a, Vector3 b, Vector3 c, float x0, float z0, float x1, float z1)
    {
        Span<Point> polygon = stackalloc Point[8];
        Span<Point> scratch = stackalloc Point[8];
        polygon[0] = new(a.X, a.Y, a.Z);
        polygon[1] = new(b.X, b.Y, b.Z);
        polygon[2] = new(c.X, c.Y, c.Z);
        int count = Clip(polygon[..3], scratch, x0, xAxis: true, lower: true);
        count = Clip(scratch[..count], polygon, x1, xAxis: true, lower: false);
        count = Clip(polygon[..count], scratch, z0, xAxis: false, lower: true);
        count = Clip(scratch[..count], polygon, z1, xAxis: false, lower: false);
        double minimum = double.PositiveInfinity;
        foreach (Point point in polygon[..count]) minimum = Math.Min(minimum, point.Y);
        float result = (float)minimum;
        // A rounded-up float would violate the conservative bound before centimetre packing.
        return result > minimum ? MathF.BitDecrement(result) : result;
    }

    static int Clip(ReadOnlySpan<Point> input, Span<Point> output, double edge, bool xAxis, bool lower)
    {
        if (input.IsEmpty) return 0;
        int count = 0;
        Point previous = input[^1];
        double p = Coordinate(previous);
        bool previousInside = Inside(p);
        foreach (Point current in input)
        {
            double q = Coordinate(current);
            bool currentInside = Inside(q);
            if (previousInside != currentInside)
            {
                double t = (edge - p) / (q - p);
                output[count++] = new Point(
                    xAxis ? edge : previous.X + t * (current.X - previous.X),
                    previous.Y + t * (current.Y - previous.Y),
                    xAxis ? previous.Z + t * (current.Z - previous.Z) : edge);
            }
            if (currentInside) output[count++] = current;
            previous = current;
            p = q;
            previousInside = currentInside;
        }
        return count;

        double Coordinate(Point point) => xAxis ? point.X : point.Z;
        bool Inside(double coordinate) => lower ? coordinate >= edge : coordinate <= edge;
    }

    readonly record struct Point(double X, double Y, double Z);
}
