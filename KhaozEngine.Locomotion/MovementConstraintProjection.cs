using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Projects a displacement candidate against the whole active normal set.
/// This is not clearance. Every candidate must be traced against solids and water before commitment.</summary>
internal static class MovementConstraintProjection
{
    // These bounded-work and arithmetic choices belong in the eventual solver/profile identity.
    internal const uint PolicyVersion = 1;
    internal const int MaximumInputNormals = 16384 + 256;
    internal const int MaximumDistinctNormals = 64;
    const double ArithmeticScale = 64d * 2.2204460492503131e-16;

    internal static MovementAvailability TryProject(Vector3 requested, ReadOnlySpan<Vector3> normals,
        out Vector3 projected)
    {
        projected = default;
        if (!MovementEnvironmentValidation.Finite(requested)) return MovementAvailability.Invalid;
        if (normals.Length > MaximumInputNormals) return MovementAvailability.CapacityExceeded;
        Span<Vector3> unique = stackalloc Vector3[MaximumDistinctNormals];
        int count = 0;
        foreach (Vector3 normal in normals)
        {
            if (!MovementEnvironmentValidation.Unit(normal)) return MovementAvailability.Invalid;
            int position = 0;
            while (position < count && Compare(unique[position], normal) < 0) position++;
            if (position < count && unique[position] == normal) continue;
            if (count == MaximumDistinctNormals) return MovementAvailability.CapacityExceeded;
            for (int i = count; i > position; i--) unique[i] = unique[i - 1];
            unique[position] = normal;
            count++;
        }
        if (count == 0)
        {
            projected = requested;
            return MovementAvailability.Known;
        }

        var desired = new Wide(requested);
        double arithmeticSlack = Math.Max(1d, desired.MaxAbs) * ArithmeticScale;
        if (arithmeticSlack > MovementQueryLease.CoverageSkinMetres) return MovementAvailability.Unresolved;
        Span<Wide> planes = stackalloc Wide[count];
        for (int i = 0; i < count; i++) planes[i] = new Wide(unique[i]);

        Wide best = default;
        double bestCost = desired.LengthSquared;
        if (!Consider(desired, desired, planes, arithmeticSlack, ref best, ref bestCost))
            return MovementAvailability.Unresolved;
        for (int i = 0; i < count; i++)
        {
            Wide normal = planes[i];
            Wide candidate = desired - normal * (Wide.Dot(desired, normal) / normal.LengthSquared);
            if (!Consider(candidate, desired, planes, arithmeticSlack, ref best, ref bestCost))
                return MovementAvailability.Unresolved;
            for (int j = 0; j < i; j++)
            {
                Wide line = Wide.Cross(normal, planes[j]);
                double denominator = line.LengthSquared;
                // Parallel/opposing planes already have their shared plane represented above.
                if (denominator == 0d) continue;
                candidate = line * (Wide.Dot(desired, line) / denominator);
                if (!Consider(candidate, desired, planes, arithmeticSlack, ref best, ref bestCost))
                    return MovementAvailability.Unresolved;
            }
        }

        Vector3 rounded = best.ToVector3();
        if (!MovementEnvironmentValidation.Finite(rounded)) return MovementAvailability.Unresolved;
        Wide roundedWide = new(rounded);
        double rounding = Math.Sqrt((roundedWide - best).LengthSquared);
        double residualAllowance = rounding + arithmeticSlack;
        if (!double.IsFinite(residualAllowance) || residualAllowance > MovementQueryLease.CoverageSkinMetres)
            return MovementAvailability.Unresolved;
        if (!Feasible(roundedWide, planes, residualAllowance)) return MovementAvailability.Unresolved;
        // The allowance only screens numerical candidates. It never authorizes penetration or
        // modifies the complete trace's physical tolerance, even when rounding points slightly inward.
        projected = rounded;
        return MovementAvailability.Known;
    }

    static bool Consider(Wide candidate, Wide desired, ReadOnlySpan<Wide> planes, double slack,
        ref Wide best, ref double bestCost)
    {
        if (!candidate.IsFinite) return false;
        if (!Feasible(candidate, planes, slack)) return true;
        double cost = (candidate - desired).LengthSquared;
        if (!double.IsFinite(cost)) return false;
        if (cost < bestCost)
        {
            best = candidate;
            bestCost = cost;
        }
        return true;
    }

    static bool Feasible(Wide candidate, ReadOnlySpan<Wide> planes, double slack)
    {
        foreach (Wide normal in planes)
            if (Wide.Dot(normal, candidate) < -slack * Math.Sqrt(normal.LengthSquared)) return false;
        return true;
    }

    static int Compare(Vector3 a, Vector3 b)
    {
        int value = a.X.CompareTo(b.X);
        if (value == 0) value = a.Y.CompareTo(b.Y);
        return value != 0 ? value : a.Z.CompareTo(b.Z);
    }

    readonly record struct Wide(double X, double Y, double Z)
    {
        public Wide(Vector3 value) : this(value.X, value.Y, value.Z) { }
        public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
        public double LengthSquared => Dot(this, this);
        public double MaxAbs => Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
        public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
        public static Wide operator -(Wide a, Wide b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Wide operator *(Wide value, double scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
        public static double Dot(Wide a, Wide b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Wide Cross(Wide a, Wide b) =>
            new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
}
