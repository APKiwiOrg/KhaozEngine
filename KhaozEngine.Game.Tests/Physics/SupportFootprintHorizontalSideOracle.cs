using System;
using KhaozEngine.Physics;
using Range = KhaozEngine.Tests.Physics.SupportFootprintOracle.Range;

namespace KhaozEngine.Tests.Physics;

/// <summary>Numerical horizontal-side solve over the finite strip. Exact rational fixture references
/// are derived separately from installed sources.</summary>
internal static class SupportFootprintHorizontalSideOracle
{
    internal static (OracleVector Position, OracleVector Normal)? Maximum(OracleTangent part,
        double x, double z, double r, double slope)
    {
        OracleVector axis = part.Axis;
        double u = Math.Sqrt(axis.X * axis.X + axis.Z * axis.Z), radius = part.Radius;
        OracleVector horizontal = new(axis.X / u, 0, axis.Z / u);
        OracleVector across = new(-horizontal.Z, 0, horizontal.X), up = new(0, u, 0);
        OracleVector delta = part.Centre - new OracleVector(x, part.Centre.Y, z);
        double parallel = OracleVector.Dot(delta, horizontal), perpendicular = OracleVector.Dot(delta, across);
        double t = Math.Clamp(-parallel / u, -part.HalfLength, part.HalfLength);
        double gap = parallel + t * u, square = r * r - gap * gap;
        if (square < 0) return null;
        double extent = Math.Sqrt(square), limit = Math.Sqrt(Math.Max(0, 1 - slope * slope / (u * u)));
        double first = Math.Max(-limit, (-extent - perpendicular) / radius);
        double last = Math.Min(limit, (extent - perpendicular) / radius);
        if (first > last) return null;
        double s = Math.Clamp(0, first, last);
        (OracleVector Position, OracleVector Normal) At(double lateral)
        {
            OracleVector normal = up * Math.Sqrt(Math.Max(0, 1 - lateral * lateral)) + across * lateral;
            return (part.Centre + axis * t + normal * radius, normal);
        }
        bool InDisc(OracleVector p) => (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z) <= r * r;
        var point = At(s);
        // The finite strip has an exact nearest axial coordinate. Move a rounded circle endpoint
        // inward by representable steps rather than losing its repeated quartic root or extending D.
        while (!InDisc(point.Position))
        {
            double next = perpendicular + radius * s > 0 ? Math.BitDecrement(s) : Math.BitIncrement(s);
            if (next < first || next > last || next == s) return null;
            s = next;
            point = At(s);
        }
        return point.Normal.Y >= slope ? point : null;
    }

    internal static (double Lower, double Upper) PerPointSupremum(OracleTangent part,
        in SupportFootprintRequest request)
    {
        if (Maximum(part, request.Axis.X, request.Axis.Y, request.Radius, 0) is not { } maximum ||
            maximum.Normal.Y <= 0) return (double.NegativeInfinity, double.NegativeInfinity);
        static Range Abs(Range value) => value.Low >= 0 ? value : value.High <= 0 ? -value :
            new(0, Math.Max(-value.Low, value.High));
        static Range Positive(Range value) => new(Math.Max(0, value.Low), Math.Max(0, value.High));
        Range ax = part.Axis.X, az = part.Axis.Z, u = (ax.Square + az.Square).Sqrt;
        Range dx = (Range)part.Centre.X - request.Axis.X, dz = (Range)part.Centre.Z - request.Axis.Y;
        Range along = (dx * ax + dz * az) / u, across = (-dx * az + dz * ax) / u;
        Range gap = Positive(Abs(along) - u * part.HalfLength), r = request.Radius;
        Range extent = (r.Square - gap.Square).Sqrt, offset = Abs(across);
        Range lateral = Positive(offset - extent), radius = part.Radius;
        Range height = (radius.Square - lateral.Square).Sqrt;
        Range y = (Range)part.Centre.Y + u * height;
        Range tangent = (Range)part.Centre.Y + (radius.Square - offset * lateral) / (u * height);
        // Independent outward double intervals enclose the closed-form per-point reading. The fixture
        // uses rational integer-root enclosures and a point/normal tangent for its separate reference.
        return (Math.Min(y.Low, tangent.Low), Math.Min(y.High, tangent.High));
    }
}
