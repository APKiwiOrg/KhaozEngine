using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;

namespace KhaozEngine.TileWorld.Physics;

// Extending only Y eliminates the axial segment. Each slab interval has one squared
// distance quadratic. Its roots and slab crossings carry directed arithmetic enclosures.
internal static class TileCapsuleBoxPath
{
    internal readonly record struct Range(bool Hit, double Enter, double Exit, double MinimumSquared, double ErrorMetres);
    readonly record struct Cut(double Value, I Enclosure);

    internal static Range Intersect(Vector3 minimum, Vector3 maximum, in MovementMediumSweepQuery query,
        double enter = 0, double exit = 1)
    {
        if (!Potential(minimum, maximum, query, enter, exit))
            return new(false, double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity, 0);
        I core = I.Point(query.Body.HalfHeight) - I.Point(query.Body.Radius);
        Span<I> low = stackalloc I[3] { I.Point(minimum.X), I.Point(minimum.Y) - core, I.Point(minimum.Z) };
        Span<I> high = stackalloc I[3] { I.Point(maximum.X), I.Point(maximum.Y) + core, I.Point(maximum.Z) };
        Span<I> start = stackalloc I[3] { I.Point(query.Body.Centre.X), I.Point(query.Body.Centre.Y), I.Point(query.Body.Centre.Z) };
        Span<I> drive = stackalloc I[3] { I.Point(query.Delta.X), I.Point(query.Delta.Y), I.Point(query.Delta.Z) };
        Span<Cut> cuts = stackalloc Cut[8];
        int count = 2;
        cuts[0] = new(enter, I.Point(enter));
        cuts[1] = new(exit, I.Point(exit));
        double error = core.High - core.Low;
        for (int axis = 0; axis < 3; axis++)
            error = Math.Max(error, low[axis].High - low[axis].Low + high[axis].High - high[axis].Low);
        double length = Math.Sqrt((double)query.Delta.X * query.Delta.X + (double)query.Delta.Y * query.Delta.Y + (double)query.Delta.Z * query.Delta.Z);
        for (int axis = 0; axis < 3; axis++)
        {
            if (drive[axis].Low == 0) continue;
            Insert((low[axis] - start[axis]) / drive[axis], enter, exit, cuts, ref count, ref error, length);
            Insert((high[axis] - start[axis]) / drive[axis], enter, exit, cuts, ref count, ref error, length);
        }
        double first = double.PositiveInfinity, last = double.NegativeInfinity, nearest = double.PositiveInfinity;
        I radius = I.Point(query.Body.Radius), squaredRadius = radius.Square();
        for (int i = 0; i < count; i++)
        {
            I distance = Distance(minimum, maximum, query, I.Point(cuts[i].Value));
            nearest = Math.Min(nearest, distance.Low);
            if (distance.Low <= squaredRadius.High)
            {
                first = Math.Min(first, Math.Max(enter, cuts[i].Enclosure.Low));
                last = Math.Max(last, Math.Min(exit, cuts[i].Enclosure.High));
            }
        }
        Span<I> offset = stackalloc I[3], velocity = stackalloc I[3];
        for (int i = 0; i + 1 < count; i++)
        {
            double a = Math.Max(enter, cuts[i].Enclosure.Low), b = Math.Min(exit, cuts[i + 1].Enclosure.High);
            double middle = cuts[i].Value + (cuts[i + 1].Value - cuts[i].Value) * 0.5;
            I speedSquared = I.Point(0), dot = I.Point(0);
            int active = 0, moving = -1;
            for (int axis = 0; axis < 3; axis++)
            {
                I p = start[axis] + I.Point(middle) * drive[axis];
                bool below = p.Low < low[axis].High, above = p.High > high[axis].Low;
                bool outside = below || above;
                offset[axis] = outside ? p - (below ? low[axis] : high[axis]) : I.Point(0);
                velocity[axis] = outside ? drive[axis] : I.Point(0);
                error = Math.Max(error, p.High - p.Low + offset[axis].High - offset[axis].Low);
                if (outside) { active++; moving = axis; }
                speedSquared += velocity[axis].Square();
                dot += offset[axis] * velocity[axis];
            }
            double closest = speedSquared.High == 0 ? middle : Math.Clamp(middle - Mid(dot / speedSquared), a, b);
            I minimumSquared = Distance(minimum, maximum, query, I.Point(closest));
            if (speedSquared.Low > 0)
            {
                I vertexOffset = (I.Point(0) - dot) / speedSquared;
                if (vertexOffset.High >= a - middle && vertexOffset.Low <= b - middle)
                {
                    I norm = I.Point(0);
                    for (int axis = 0; axis < 3; axis++) norm += offset[axis].Square();
                    minimumSquared = norm - dot.Square() / speedSquared;
                }
            }
            nearest = Math.Min(nearest, Math.Max(0, minimumSquared.Low));
            I roots;
            if (speedSquared.High == 0)
            {
                if (Distance(minimum, maximum, query, I.Point(middle)).Low > squaredRadius.High) continue;
                roots = new(a, b);
            }
            else if (active == 1)
            {
                I plane = start[moving] + I.Point(middle) * drive[moving];
                plane = plane.Low < low[moving].High ? low[moving] : high[moving];
                I t0 = (plane - radius - start[moving]) / drive[moving];
                I t1 = (plane + radius - start[moving]) / drive[moving];
                roots = new(Math.Min(t0.Low, t1.Low), Math.Max(t0.High, t1.High));
                error = Math.Max(error, Math.Max(t0.High - t0.Low, t1.High - t1.Low) * length);
            }
            else
            {
                I vertex = (I.Point(0) - dot) / speedSquared;
                I residual = I.Point(0) - squaredRadius;
                for (int axis = 0; axis < 3; axis++) residual += (offset[axis] + vertex * velocity[axis]).Square();
                if (residual.Low > 0) continue;
                I reach = ((I.Point(0) - residual) / speedSquared).Sqrt();
                I t0 = I.Point(middle) + vertex - reach, t1 = I.Point(middle) + vertex + reach;
                roots = new(t0.Low, t1.High);
                error = Math.Max(error, Math.Max(t0.High - t0.Low, t1.High - t1.Low) * length);
            }
            a = Math.Max(a, roots.Low);
            b = Math.Min(b, roots.High);
            if (a > b) continue;
            first = Math.Min(first, a);
            last = Math.Max(last, b);
        }
        // Independent coordinate minima are also a lower bound on the full distance.
        // In particular a stationary face at exactly one radius proves tangency even
        // when an unconstrained quadratic minimum loses precision by cancellation.
        I independent = I.Point(0);
        for (int axis = 0; axis < 3; axis++)
        {
            I a = start[axis] + I.Point(enter) * drive[axis], b = start[axis] + I.Point(exit) * drive[axis];
            independent += Outside(new(Math.Min(a.Low, b.Low), Math.Max(a.High, b.High)), low[axis], high[axis]).Square();
        }
        nearest = Math.Max(nearest, independent.Low);
        return new(first <= last, first, last, nearest, error);
    }

    static double Mid(I value) => value.Low + (value.High - value.Low) * 0.5;

    // Bounds reject distant candidates only. Every retained candidate uses the rounded-box quadratic.
    static bool Potential(Vector3 min, Vector3 max, in MovementMediumSweepQuery query, double enter, double exit)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            I a = I.Point(query.Body.Centre[axis]) + I.Point(enter) * I.Point(query.Delta[axis]);
            I b = I.Point(query.Body.Centre[axis]) + I.Point(exit) * I.Point(query.Delta[axis]);
            I reach = I.Point(axis == 1 ? query.Body.HalfHeight : query.Body.Radius);
            if ((I.Point(Math.Min(a.Low, b.Low)) - reach).Low > max[axis] ||
                (I.Point(Math.Max(a.High, b.High)) + reach).High < min[axis]) return false;
        }
        return true;
    }

    static void Insert(I at, double enter, double exit, Span<Cut> cuts, ref int count, ref double error, double length)
    {
        error = Math.Max(error, (at.High - at.Low) * length);
        double value = Math.Clamp(Mid(at), enter, exit);
        if (value <= enter || value >= exit) return;
        int index = count;
        while (index > 0 && cuts[index - 1].Value > value) { cuts[index] = cuts[index - 1]; index--; }
        cuts[index] = new(value, at);
        count++;
    }

    internal static double DistanceSquared(Vector3 minimum, Vector3 maximum, in MovementMediumSweepQuery query, double fraction) =>
        Distance(minimum, maximum, query, I.Point(fraction)).Low;

    static I Distance(Vector3 minimum, Vector3 maximum, in MovementMediumSweepQuery query, I fraction)
    {
        I core = I.Point(query.Body.HalfHeight) - I.Point(query.Body.Radius);
        I x = I.Point(query.Body.Centre.X) + fraction * I.Point(query.Delta.X);
        I y = I.Point(query.Body.Centre.Y) + fraction * I.Point(query.Delta.Y);
        I z = I.Point(query.Body.Centre.Z) + fraction * I.Point(query.Delta.Z);
        return Outside(x, I.Point(minimum.X), I.Point(maximum.X)).Square() +
            Outside(y, I.Point(minimum.Y) - core, I.Point(maximum.Y) + core).Square() +
            Outside(z, I.Point(minimum.Z), I.Point(maximum.Z)).Square();
    }

    static I Outside(I point, I low, I high)
    {
        I below = low - point, above = point - high;
        return new(Math.Max(0, Math.Max(below.Low, above.Low)), Math.Max(0, Math.Max(below.High, above.High)));
    }

    internal static Vector3 Normal(TileWaterBoundary face, in MovementMediumSweepQuery query, double fraction)
    {
        double core = (double)query.Body.HalfHeight - query.Body.Radius;
        double x = query.Body.Centre.X + fraction * query.Delta.X;
        double y = query.Body.Centre.Y + fraction * query.Delta.Y;
        double z = query.Body.Centre.Z + fraction * query.Delta.Z;
        double dx = x - Math.Clamp(x, face.Minimum.X, face.Maximum.X);
        double dy = y - Math.Clamp(y, face.Minimum.Y - core, face.Maximum.Y + core);
        double dz = z - Math.Clamp(z, face.Minimum.Z, face.Maximum.Z);
        // Water pushes outward on its face even when the capsule centre is on its wet side.
        if (face.Normal.X != 0) dx = face.Normal.X * Math.Abs(dx);
        if (face.Normal.Y != 0) dy = face.Normal.Y * Math.Abs(dy);
        if (face.Normal.Z != 0) dz = face.Normal.Z * Math.Abs(dz);
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return length == 0 ? face.Normal : new((float)(dx / length), (float)(dy / length), (float)(dz / length));
    }
}
