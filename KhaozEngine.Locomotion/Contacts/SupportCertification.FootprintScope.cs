using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>The caller's certified scope and the inclusive contribution band. A published contribution that
/// meets the band must take every term that can decide it from inside <see cref="Bounds"/>.</summary>
internal readonly record struct FootprintScope(SupportFootprintBounds Bounds, double BandLower, double BandUpper)
{
    internal bool Meets(double lower, double upper) => upper >= BandLower && lower <= BandUpper;
}

internal static partial class SupportCertification
{
    // A term decides a minimum when its lower bound can reach the minimum's upper bound. Terms that are
    // proved higher never decide it, so admitted whole polygons may extend beyond the scope.
    static void RequirePlaneInScope(Vector2 axis, double lower, double upper, in SupportFootprintBounds scope)
    {
        if (!(axis.X >= scope.Minimum.X && axis.X <= scope.Maximum.X && axis.Y >= scope.Minimum.Z &&
              axis.Y <= scope.Maximum.Z && lower >= scope.Minimum.Y && upper <= scope.Maximum.Y))
            throw new NotSupportedException("A required footprint cap is outside the admitted scope.");
    }

    static void RequireWitnessInScope(in SupportFootprintWitness witness, in SupportFootprintBounds scope)
    {
        Vector3 point = witness.Position;
        float error = witness.PositionErrorMetres;
        if (!(LowerBound(point.X, error) >= scope.Minimum.X && UpperBound(point.X, error) <= scope.Maximum.X &&
              LowerBound(point.Y, error) >= scope.Minimum.Y && UpperBound(point.Y, error) <= scope.Maximum.Y &&
              LowerBound(point.Z, error) >= scope.Minimum.Z && UpperBound(point.Z, error) <= scope.Maximum.Z))
            throw new NotSupportedException("A required footprint witness is outside the admitted scope.");
    }
}
