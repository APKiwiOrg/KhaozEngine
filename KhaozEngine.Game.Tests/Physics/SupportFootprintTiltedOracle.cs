using System;
using KhaozEngine.Physics;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using P = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;

namespace KhaozEngine.Tests.Physics;

/// <summary>Independent rational readings for tilted witnesses. Numerical locators identify
/// strata, then exact inputs and unique certified root brackets enclose their attained points.</summary>
internal static class SupportFootprintTiltedOracle
{
    internal readonly record struct Certificate(P Position, P Normal);
    static B E(double value) => R.From(value);
    static B Root(B value)
    {
        if (value.Lower < R.Zero) throw new InvalidOperationException("Oracle root domain is not certified.");
        return new(B.Root(value.Lower).Lower, B.Root(value.Upper).Upper);
    }
    static P Add(P a, P b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    static P Scale(P a, B b) => new(a.X * b, a.Y * b, a.Z * b);
    static B Dot(P a, P b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static B Min(B a, B b) => new(a.Lower < b.Lower ? a.Lower : b.Lower, a.Upper < b.Upper ? a.Upper : b.Upper);
    static B Square(B a) => new(a.Lower <= R.Zero && a.Upper >= R.Zero ? R.Zero :
        (a.Lower.Square() < a.Upper.Square() ? a.Lower.Square() : a.Upper.Square()),
        a.Lower.Square() > a.Upper.Square() ? a.Lower.Square() : a.Upper.Square());

    internal static Certificate Enclose(SupportFootprintCurvedProofOracle.InstalledPart part,
        FootprintOracleWitness locator, in SupportFootprintRequest request, bool restricted = true)
    {
        bool axisRole = locator.Role == SupportWitnessRole.Axis;
        if (part.Tangent.Surface is OracleSurface.CylinderTop or OracleSurface.CylinderBottom)
            return Cap(part, locator, request);
        P axis = part.Axis.Y.Upper < R.Zero ? Scale(part.Axis, -R.One) : part.Axis;
        P delta = Add(new(E(request.Axis.X), part.Centre.Y, E(request.Axis.Y)), Scale(part.Centre, -R.One));
        if (part.Tangent.Surface == OracleSurface.Round)
        {
            // A hemisphere-certified nearest point on an end sphere also satisfies the
            // core segment's endpoint optimality condition. It is the capsule maximum.
            // Test both source ends, including normals too close to a seam for a locator.
            foreach (int sign in new[] { -1, 1 })
            {
                B end = E(sign) * (B)part.Half;
                P centre = Add(part.Centre, Scale(axis, end));
                B dx = E(request.Axis.X) - centre.X, dz = E(request.Axis.Y) - centre.Z;
                B distance = Root(Square(dx) + Square(dz));
                if (!axisRole && distance.Lower < R.From(request.Radius) && distance.Upper > R.From(request.Radius))
                    continue;
                B factor = axisRole ? E(1) : distance.Upper <= R.From(request.Radius) ? E(0) :
                    (distance - E(request.Radius)) / distance;
                dx *= factor; dz *= factor;
                B square = (B)part.Radius.Square() - Square(dx) - Square(dz);
                if (square.Lower < R.Zero) continue;
                B dy = Root(square);
                P radial = new(dx, dy, dz), normal = Scale(radial, (B)R.One / (B)part.Radius);
                B hemisphere = Dot(normal, axis);
                if (part.Half != R.Zero && (sign > 0 ? hemisphere.Lower < R.Zero : hemisphere.Upper > R.Zero)) continue;
                if (!axisRole && restricted && normal.Y.Lower < R.From(request.CosMaxSlope)) continue;
                return new(Add(centre, radial), normal);
            }
        }
        B k = axis.Y, u = Root(Square(axis.X) + Square(axis.Z));
        P e = new(axis.X / u, E(0), axis.Z / u), f = new(-e.Z, E(0), e.X);
        P w = new(-k * e.X, u, -k * e.Z);
        B p = -Dot(delta, e), q = -Dot(delta, f), radius = part.Radius, disc = E(request.Radius);
        double parameter = OracleVector.Dot(locator.Normal, f.Middle);
        bool finiteEnd = false;
        B finite = E(0);
        bool boundary = !axisRole && restricted && u.Lower >= R.From(request.CosMaxSlope) &&
            Math.Abs(locator.Normal.Y - request.CosMaxSlope) < 1e-10;
        B z;
        if (axisRole) z = -q / radius;
        else if (boundary)
        {
            z = Root(E(1) - Square(E(request.CosMaxSlope) / u));
            if (parameter < 0) z = -z;
        }
        else if (q.Lower == R.Zero && q.Upper == R.Zero || k.Lower == R.Zero && k.Upper == R.Zero) z = E(0);
        else
        {
            B Value(B a) => a / Root(E(1) - Square(a)) + k * (q + radius * a) /
                Root(Square(disc) - Square(q + radius * a));
            z = IsolateSide(q, radius, disc, k, Value);
            B axial = (radius * k * Root(E(1) - Square(z)) - p +
                Root(Square(disc) - Square(q + radius * z))) / u;
            if (axial.Lower > part.Half || axial.Upper < -part.Half)
            {
                finiteEnd = true;
                finite = axial.Lower > part.Half ? (B)part.Half : -(B)part.Half;
                B parallel = p + u * finite;
                B crestDistance = Square(parallel - radius * k) + Square(q);
                B EndValue(B a) => Square(parallel - radius * k * Root(E(1) - Square(a))) +
                    Square(q + radius * a) - Square(disc);
                B EndDerivative(B a) => E(2) * radius * (q + radius * a) +
                    E(2) * radius * k * a * (parallel - radius * k * Root(E(1) - Square(a))) / Root(E(1) - Square(a));
                z = crestDistance.Upper <= Square(disc).Lower ? E(0) : Isolate(parameter, EndValue, EndDerivative);
            }
        }
        B v = boundary ? E(request.CosMaxSlope) / u : Root(E(1) - Square(z));
        B centreT = (radius * k * v - p) / u;
        B t = axisRole ? centreT : finiteEnd ? finite :
            Min(part.Half, centreT + Root(Square(disc) - Square(q + radius * z)) / u);
        if (t.Lower < -part.Half || t.Upper > part.Half)
            throw new InvalidOperationException("Oracle finite side domain is not certified.");
        P n = Add(Scale(w, v), Scale(f, z));
        if (boundary) n = n with { Y = E(request.CosMaxSlope) };
        return new(Add(Add(part.Centre, Scale(axis, t)), Scale(n, radius)), n);
    }

    static Certificate Cap(SupportFootprintCurvedProofOracle.InstalledPart part, FootprintOracleWitness locator,
        in SupportFootprintRequest request)
    {
        P normal = Scale(part.Axis, E(part.Tangent.Surface == OracleSurface.CylinderTop ? 1 : -1));
        P centre = Add(part.Centre, Scale(normal, part.Half));
        if (locator.Role == SupportWitnessRole.Axis)
            return new(SupportFootprintCurvedProofOracle.CapPoint(part, R.From(request.Axis.X), R.From(request.Axis.Y)), normal);
        B k = normal.Y, u = Root(Square(normal.X) + Square(normal.Z));
        P e = new(normal.X / u, E(0), normal.Z / u), f = new(-e.Z, E(0), e.X);
        P w = new(-k * e.X, u, -k * e.Z);
        P delta = Add(centre, new(-E(request.Axis.X), -centre.Y, -E(request.Axis.Y)));
        B p = Dot(delta, e), q = Dot(delta, f), radius = part.Radius, disc = E(request.Radius);
        OracleVector radial = (locator.Position - centre.Middle) * (1 / part.Tangent.Radius);
        double parameter = OracleVector.Dot(radial, f.Middle), vertical = OracleVector.Dot(radial, w.Middle);
        B z, v;
        if (Square(p - radius * k).Upper + Square(q).Upper <= Square(disc).Lower) { z = E(0); v = E(1); }
        else if (Math.Abs(radial.Length - 1) < 1e-6)
        {
            int rim = vertical < 0 ? -1 : 1;
            B Value(B a) => Square(p - radius * k * E(rim) * Root(E(1) - Square(a))) + Square(q + radius * a) - Square(disc);
            B Derivative(B a) => E(2) * radius * (q + radius * a) + E(2) * radius * k * E(rim) * a *
                (p - radius * k * E(rim) * Root(E(1) - Square(a))) / Root(E(1) - Square(a));
            z = Isolate(parameter, Value, Derivative); v = E(rim) * Root(E(1) - Square(z));
        }
        else
        {
            z = -q / radius;
            v = Min(Root(E(1) - Square(z)), (p + disc) / (radius * k));
        }
        return new(Add(centre, Scale(Add(Scale(w, v), Scale(f, z)), radius)), normal);
    }

    static B IsolateSide(B q, B radius, B disc, B k, Func<B, B> value)
    {
        B left = (-disc - q) / radius, right = (disc - q) / radius;
        R low = left.Upper > -R.One ? left.Upper : -R.One;
        R high = right.Lower < R.One ? right.Lower : R.One;
        R inset = (high - low) * R.From(Math.ScaleB(1, -80));
        low += inset; high -= inset;
        // Exact rational endpoint signs and the positive derivative of z/v+k*lateral/extent
        // certify the root. The inset only locates a strict bracket inside the closed domain.
        if (k.Lower <= R.Zero || value(low).Upper >= R.Zero || value(high).Lower <= R.Zero)
            throw new InvalidOperationException("Oracle side root has no common smooth bracket.");
        while (high - low > R.From(1e-30))
        {
            R middle = (low + high) / new R(2, 1);
            B at = value(middle);
            if (at.Lower > R.Zero) high = middle;
            else if (at.Upper < R.Zero) low = middle;
            else break;
        }
        return new(low, high);
    }

    static B Isolate(double locator, Func<B, B> value, Func<B, B> derivative)
    {
        R centre = R.From(locator), width = R.From(1e-15);
        B bracket = default;
        int direction = 0;
        for (int attempt = 0; attempt < 48; attempt++, width *= new R(2, 1))
        {
            bracket = new(centre - width, centre + width);
            try
            {
                B a = value(bracket.Lower), b = value(bracket.Upper), slope = derivative(bracket);
                direction = a.Upper < R.Zero && b.Lower > R.Zero && slope.Lower > R.Zero ? 1 :
                    a.Lower > R.Zero && b.Upper < R.Zero && slope.Upper < R.Zero ? -1 : 0;
                if (direction != 0) break;
            }
            catch (InvalidOperationException) { }
        }
        if (direction == 0) throw new InvalidOperationException("Oracle root has no unique existence bracket.");
        // This only sizes the independent test reading. The bracket's sign and derivative
        // certificates enclose the root before and after every rational bisection.
        while (bracket.Upper - bracket.Lower > R.From(1e-30))
        {
            R middle = (bracket.Lower + bracket.Upper) / new R(2, 1);
            B at = value(middle);
            int sign = at.Lower > R.Zero ? 1 : at.Upper < R.Zero ? -1 : 0;
            if (sign == 0) break;
            bracket = sign == direction ? new(bracket.Lower, middle) : new(middle, bracket.Upper);
        }
        return bracket;
    }

    internal static (P Position, P Normal) Crest(SupportFootprintCurvedProofOracle.InstalledPart part, bool cap)
    {
        P axis = part.Axis;
        B square = axis.X * axis.X + axis.Z * axis.Z;
        B u = new(B.Root(square.Lower).Lower, B.Root(square.Upper).Upper);
        var w = new P(-axis.Y * axis.X / u, u, -axis.Y * axis.Z / u);
        B h = part.Half, r = part.Radius;
        return (new(part.Centre.X + axis.X * h + w.X * r,
            part.Centre.Y + axis.Y * h + w.Y * r, part.Centre.Z + axis.Z * h + w.Z * r), cap ? axis : w);
    }
}
