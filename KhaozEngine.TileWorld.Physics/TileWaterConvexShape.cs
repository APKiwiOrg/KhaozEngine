using System;
using System.Collections.Generic;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Certified centre-sphere paths against a vertical-core-expanded convex water piece or face.</summary>
internal sealed class TileWaterConvexShape
{
    internal readonly record struct Vector(I X, I Y, I Z)
    {
        internal static Vector Point(double x, double y, double z) => new(I.Point(x), I.Point(y), I.Point(z));
        public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector operator *(Vector a, I scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
        internal static I Dot(Vector a, Vector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        internal I SquaredLength => X.Square() + Y.Square() + Z.Square();
        internal static Vector Cross(Vector a, Vector b) => new(a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        internal double Width => Math.Sqrt(Squared(X.High - X.Low) + Squared(Y.High - Y.Low) + Squared(Z.High - Z.Low));
        static double Squared(double value) => value * value;
    }

    internal readonly record struct Plane(Vector Normal, I Offset, bool HeightSensitive = false)
    {
        internal static Plane Point(double x, double y, double z, double offset) => new(Vector.Point(x, y, z), I.Point(offset));
        internal I Expanded(I core, double heightError, double horizontalError)
        {
            double uncertainty = (I.Point(HeightSensitive ? heightError : 0) +
                (I.Point(Math.Abs(Normal.X.Low)) + I.Point(Math.Abs(Normal.Z.Low))) * I.Point(horizontalError)).High;
            return Offset + I.Point(Math.Abs(Normal.Y.Low)) * core + new I(-uncertainty, uncertainty);
        }
        internal Plane Opposite => new(default(Vector) - Normal, I.Point(0) - Offset, HeightSensitive);
    }

    readonly record struct Basis(int A, int B = -1, int C = -1);
    internal readonly record struct Range(bool Resolved, bool Hit, double Enter, double Exit, double MinimumSquared,
        double Error, Vector WitnessStart, Vector WitnessDrive)
    {
        internal Vector Witness(double fraction) => WitnessStart + WitnessDrive * I.Point(fraction);
    }

    readonly Plane[] _planes;
    readonly Basis[] _bases;

    internal TileWaterConvexShape(Plane[] planes)
    {
        _planes = planes;
        var bases = new List<Basis> { new(-1) };
        for (int a = 0; a < planes.Length; a++)
        {
            if (!TileWaterLinearRank.Independent(planes, a, -1, -1)) continue;
            bases.Add(new(a));
            for (int b = a + 1; b < planes.Length; b++)
            {
                if (!TileWaterLinearRank.Independent(planes, a, b, -1)) continue;
                bases.Add(new(a, b));
                for (int c = b + 1; c < planes.Length; c++)
                    if (TileWaterLinearRank.Independent(planes, a, b, c)) bases.Add(new(a, b, c));
            }
        }
        _bases = bases.ToArray();
    }

    internal bool Contains(Vector point)
    {
        foreach (Plane plane in _planes)
            if (Vector.Dot(plane.Normal, point).High > plane.Offset.Low) return false;
        return true;
    }

    internal Range Intersect(Vector start, Vector drive, I core, float radius, double enter = 0, double exit = 1,
        double heightError = 0, double horizontalError = 0)
    {
        double first = double.PositiveInfinity, last = double.NegativeInfinity, nearest = double.PositiveInfinity;
        double error = start.Width + drive.Width;
        Vector witnessStart = default, witnessDrive = default;
        I radiusSquared = I.Point(radius).Square();
        Span<I> lambda = stackalloc I[3];
        Span<I> velocity = stackalloc I[3];
        foreach (Basis basis in _bases)
        {
            if (!Project(basis, start, drive, core, heightError, horizontalError, lambda, velocity, out Vector q, out Vector v))
                return new(false, false, 0, 0, 0, double.PositiveInfinity, default, default);
            double low = enter, high = exit;
            int active = basis.A < 0 ? 0 : basis.B < 0 ? 1 : basis.C < 0 ? 2 : 3;
            bool feasible = true;
            for (int i = 0; i < active && feasible; i++)
                feasible = Clip(I.Point(0) - lambda[i], I.Point(0) - velocity[i], ref low, ref high);
            for (int i = 0; i < _planes.Length && feasible; i++)
            {
                Plane plane = _planes[i];
                feasible = Clip(Vector.Dot(plane.Normal, q) - plane.Expanded(core, heightError, horizontalError), Vector.Dot(plane.Normal, v), ref low, ref high);
            }
            if (!feasible) continue;
            error = Math.Max(error, q.Width + v.Width);
            Vector offset = start - q, motion = drive - v;
            I a = motion.SquaredLength, b = Vector.Dot(offset, motion);
            I vertex = a.Low > 0 ? (I.Point(0) - b) / a : new I(low, high);
            I closest = new(Math.Clamp(vertex.Low, low, high), Math.Clamp(vertex.High, low, high));
            I minimum = (offset + motion * closest).SquaredLength;
            nearest = Math.Min(nearest, Math.Max(0, minimum.Low));
            if (minimum.Low > radiusSquared.High) continue;
            I whole = (offset + motion * new I(low, high)).SquaredLength;
            if (whole.High <= radiusSquared.Low)
            {
                if (low < first) { first = low; witnessStart = q; witnessDrive = v; }
                last = Math.Max(last, high);
                continue;
            }
            if (a.Low <= 0)
            {
                I bound = (offset + motion * new I(low, high)).SquaredLength;
                nearest = Math.Min(nearest, Math.Max(0, bound.Low));
                if (bound.Low > radiusSquared.High) continue;
                error = Math.Max(error, Math.Sqrt(a.High) * (high - low));
            }
            else
            {
                if (!(a.Low > 0)) return new(false, false, 0, 0, 0, double.PositiveInfinity, default, default);
                Vector atVertex = offset + motion * vertex;
                I remaining = radiusSquared - atVertex.SquaredLength;
                if (remaining.High < 0) continue;
                I reach = (remaining / a).Sqrt();
                I lower = vertex - reach, upper = vertex + reach;
                double length = Math.Sqrt(Vector.Dot(drive, drive).High);
                double uncertainty = Math.Max(Math.Clamp(lower.High, low, high) - Math.Clamp(lower.Low, low, high),
                    Math.Clamp(upper.High, low, high) - Math.Clamp(upper.Low, low, high));
                low = Math.Max(low, lower.Low);
                high = Math.Min(high, upper.High);
                error = Math.Max(error, uncertainty * length);
                if (low > high) continue;
            }
            if (low < first) { first = low; witnessStart = q; witnessDrive = v; }
            last = Math.Max(last, high);
        }
        return new(double.IsFinite(error), first <= last, first, last, nearest, error, witnessStart, witnessDrive);
    }

    bool Project(Basis basis, Vector start, Vector drive, I core, double heightError, double horizontalError,
        Span<I> lambda, Span<I> velocity,
        out Vector q, out Vector v)
    {
        q = start;
        v = drive;
        if (basis.A < 0) return true;
        Plane a = _planes[basis.A];
        I rhsA = Vector.Dot(a.Normal, start) - a.Expanded(core, heightError, horizontalError), speedA = Vector.Dot(a.Normal, drive);
        I aa = Vector.Dot(a.Normal, a.Normal);
        if (basis.B < 0)
        {
            lambda[0] = rhsA / aa;
            velocity[0] = speedA / aa;
        }
        else
        {
            Plane b = _planes[basis.B];
            I rhsB = Vector.Dot(b.Normal, start) - b.Expanded(core, heightError, horizontalError), speedB = Vector.Dot(b.Normal, drive);
            Vector cross = Vector.Cross(a.Normal, b.Normal);
            if (basis.C < 0)
            {
                I bb = Vector.Dot(b.Normal, b.Normal), ab = Vector.Dot(a.Normal, b.Normal), determinant = Vector.Dot(cross, cross);
                if (!(determinant.Low > 0)) return false;
                lambda[0] = (bb * rhsA - ab * rhsB) / determinant;
                lambda[1] = (aa * rhsB - ab * rhsA) / determinant;
                velocity[0] = (bb * speedA - ab * speedB) / determinant;
                velocity[1] = (aa * speedB - ab * speedA) / determinant;
            }
            else
            {
                Plane c = _planes[basis.C];
                Vector bc = Vector.Cross(b.Normal, c.Normal), ca = Vector.Cross(c.Normal, a.Normal);
                I determinant = Vector.Dot(a.Normal, bc);
                if (determinant.Low <= 0 && determinant.High >= 0) return false;
                q = (bc * a.Expanded(core, heightError, horizontalError) + ca * b.Expanded(core, heightError, horizontalError) + cross * c.Expanded(core, heightError, horizontalError)) * (I.Point(1) / determinant);
                v = default;
                Vector difference = start - q;
                lambda[0] = Vector.Dot(difference, bc) / determinant;
                lambda[1] = Vector.Dot(difference, ca) / determinant;
                lambda[2] = Vector.Dot(difference, cross) / determinant;
                velocity[0] = Vector.Dot(drive, bc) / determinant;
                velocity[1] = Vector.Dot(drive, ca) / determinant;
                velocity[2] = Vector.Dot(drive, cross) / determinant;
                return true;
            }
            q -= b.Normal * lambda[1];
            v -= b.Normal * velocity[1];
        }
        q -= a.Normal * lambda[0];
        v -= a.Normal * velocity[0];
        return double.IsFinite(q.X.Low) && double.IsFinite(q.Y.Low) && double.IsFinite(q.Z.Low) &&
            double.IsFinite(v.X.Low) && double.IsFinite(v.Y.Low) && double.IsFinite(v.Z.Low);
    }

    static bool Clip(I constant, I slope, ref double enter, ref double exit)
    {
        // Fractions are nonnegative. This lower affine enclosure includes every feasible exact projection.
        if (slope.Low == 0) return constant.Low <= 0;
        I crossing = I.Point(-constant.Low) / I.Point(slope.Low);
        if (slope.Low > 0) exit = Math.Min(exit, crossing.High);
        else enter = Math.Max(enter, crossing.Low);
        return enter <= exit;
    }

    internal static double Mid(I value) => value.Low + (value.High - value.Low) * 0.5;
}
