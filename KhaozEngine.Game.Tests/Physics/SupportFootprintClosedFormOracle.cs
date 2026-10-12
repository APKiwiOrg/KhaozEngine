using System;
using System.Collections.Generic;
using KhaozEngine.Physics;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using P = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;
using Part = KhaozEngine.Tests.Physics.SupportFootprintCurvedProofOracle.InstalledPart;

namespace KhaozEngine.Tests.Physics;

/// <summary>Independent closed-form references over rational installed inputs and 112-bit integer-root
/// enclosures. Production transforms, scalar arithmetic and predicate owners are never called.</summary>
internal static class SupportFootprintClosedFormOracle
{
    internal readonly record struct Witness(SupportWitnessRole Role, P Position, P Normal, B? Contribution);
    internal readonly record struct Reference(B? Maximum, IReadOnlyList<Witness> Witnesses);
    static B Exact(double value) => R.From(value);
    static B Root(B value) => new(B.Root(value.Lower).Lower, B.Root(value.Upper).Upper);
    static B Square(B value) => value * value;
    static B Abs(B value) => value.Lower >= R.Zero ? value : -value;
    static B Min(B a, B b) => a.Middle <= b.Middle ? a : b;
    static B Max(B a, B b) => a.Middle >= b.Middle ? a : b;
    static B Clamp(B a, B half) => Min(half, Max(-half, a));
    static P Add(P a, P b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    static P Scale(P a, B b) => new(a.X * b, a.Y * b, a.Z * b);
    static P Up => new(R.Zero, R.One, R.Zero);

    internal static Reference Measure(Part part, in SupportFootprintRequest request)
    {
        var witnesses = new List<Witness>();
        double axisX = request.Axis.X, axisZ = request.Axis.Y;
        (P Position, P Normal)? axis = Maximum(part, request.Axis.X, request.Axis.Y, R.Zero);
        (P Position, P Normal)? footprint = Maximum(part, request.Axis.X, request.Axis.Y, R.From(request.Radius));
        Witness Attained((P Position, P Normal) point, SupportWitnessRole role)
        {
            P p = point.Position, n = point.Normal;
            B? contribution = n.Y.Lower > R.Zero ? Min(p.Y, p.Y +
                (n.X * (p.X - Exact(axisX)) + n.Z * (p.Z - Exact(axisZ))) / n.Y) : null;
            return new(role, p, n, contribution);
        }
        if (axis is { } a) witnesses.Add(Attained(a, SupportWitnessRole.Axis));
        if (footprint is { } f) witnesses.Add(Attained(f, SupportWitnessRole.Footprint));
        return new(footprint?.Position.Y, witnesses);
    }

    static (P Position, P Normal)? Maximum(Part part, double x, double z, R disc)
    {
        P centre = part.Centre, axis = part.Axis;
        B radius = part.Radius, half = part.Half, dx = Exact(x) - centre.X, dz = Exact(z) - centre.Z;
        bool upright = axis.X.Upper == R.Zero && axis.X.Lower == R.Zero &&
            axis.Z.Upper == R.Zero && axis.Z.Lower == R.Zero;
        bool round = part.Tangent.Surface == OracleSurface.Round;
        if (round && (part.Half == R.Zero || upright))
        {
            B distance = Root(Square(dx) + Square(dz)), nearest = Max(distance - (B)disc, R.Zero);
            if (nearest.Lower > radius.Upper) return null;
            B dy = Root(Square(radius) - Square(nearest)), factor = distance.Upper == R.Zero ? R.Zero : nearest / distance;
            var radial = new P(dx * factor, dy, dz * factor);
            if (upright) centre = centre with { Y = centre.Y + half };
            return (Add(centre, radial), Scale(radial, (B)R.One / radius));
        }
        if (upright)
        {
            bool side = part.Tangent.Surface == OracleSurface.CylinderSide;
            B sign = Exact(part.Tangent.Surface == OracleSurface.CylinderTop ? 1 : -1);
            if (!side && (axis.Y * sign).Upper <= R.Zero) return null;
            B distance = Root(Square(dx) + Square(dz));
            if (distance.Lower > (radius + (B)disc).Upper || (side && (distance + (B)disc).Upper < radius.Lower)) return null;
            B factor = distance.Upper == R.Zero ? R.Zero : side ? radius / distance : Min(R.One, radius / distance);
            var p = new P(centre.X + dx * factor, centre.Y + half, centre.Z + dz * factor);
            P n = Up;
            if (side)
            {
                n = distance.Upper == R.Zero ? new P(R.One, R.Zero, R.Zero) : new P(dx / distance, R.Zero, dz / distance);
                if (distance.Upper == R.Zero) p = p with { X = centre.X + radius };
            }
            return (p, n);
        }
        if (axis.Y.Lower != R.Zero || axis.Y.Upper != R.Zero)
            throw new InvalidOperationException("The closed-form oracle requires an upright or horizontal source.");
        B length = Root(Square(axis.X) + Square(axis.Z));
        var e = new P(axis.X / length, R.Zero, axis.Z / length);
        var f = new P(-e.Z, R.Zero, e.X);
        B parallel = dx * e.X + dz * e.Z, across = dx * f.X + dz * f.Z;
        if (round)
        {
            B t = Clamp(parallel, half);
            P core = Add(centre, Scale(e, t));
            B gap = t == parallel ? R.Zero : parallel - t, distance = Root(Square(gap) + Square(across));
            B nearest = Max(distance - (B)disc, R.Zero);
            if (nearest.Lower > radius.Upper) return null;
            B height = Root(Square(radius) - Square(nearest)), fraction = distance.Upper == R.Zero ? R.Zero : nearest / distance;
            var radial = new P((Exact(x) - core.X) * fraction, height, (Exact(z) - core.Z) * fraction);
            return (Add(core, radial), Scale(radial, (B)R.One / radius));
        }
        bool cylinderSide = part.Tangent.Surface == OracleSurface.CylinderSide;
        B endSign = Exact(part.Tangent.Surface == OracleSurface.CylinderTop ? 1 : -1);
        B along = cylinderSide ? Clamp(parallel, half) : endSign * half;
        B gapToEnd = cylinderSide && along == parallel ? R.Zero : parallel - along;
        B square = (B)disc.Square() - Square(gapToEnd);
        if (square.Upper < R.Zero) return null;
        B lateral = Max(Abs(across) - Root(square), R.Zero);
        if (lateral.Lower > radius.Upper) return null;
        if (across.Upper < R.Zero) lateral = -lateral;
        B y = Root(Square(radius) - Square(lateral));
        P radialPoint = Add(Scale(f, lateral), Scale(Up, y));
        return (Add(Add(centre, Scale(e, along)), radialPoint),
            cylinderSide ? Scale(radialPoint, (B)R.One / radius) : Scale(e, endSign));
    }
}
