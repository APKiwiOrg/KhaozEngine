using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public sealed partial class OrdinaryMovementEnvironment
{
    internal static Vector3 Translate(Vector3 value, Vector3 origin, out double error)
    {
        double x = (double)value.X + origin.X, y = (double)value.Y + origin.Y, z = (double)value.Z + origin.Z;
        Vector3 translated = new((float)x, (float)y, (float)z);
        if (!MovementEnvironmentValidation.Finite(translated)) throw new ArgumentException("The translated coordinates must be finite.");
        error = Math.Sqrt((translated.X - x) * (translated.X - x) + (translated.Y - y) * (translated.Y - y) +
            (translated.Z - z) * (translated.Z - z));
        return translated;
    }

    internal static MovementWaterInterval Translate(in MovementWaterInterval interval, float originY, out double error)
    {
        double lower = (double)interval.LowerY + originY, upper = (double)interval.UpperY + originY,
            surface = (double)interval.NominalSurfaceY + originY;
        float l = (float)lower, u = (float)upper, s = (float)surface;
        error = Math.Max(Math.Abs(l - lower), Math.Max(Math.Abs(u - upper), Math.Abs(s - surface)));
        return new(l, u, s, interval.UpperIsFreeSurface, interval.LowerBoundaryId, interval.UpperBoundaryId);
    }
}
