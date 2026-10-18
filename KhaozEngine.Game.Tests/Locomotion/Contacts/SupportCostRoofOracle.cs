using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Tests.Physics;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>The roof over a cost scene's footprint disc, read in binary64 from the installed polygon faces. A roof is
/// a downward facing face whose lowest point over the disc lies above the support height. The cost scenes install no
/// curved surface overhead, so curved leaves are not read.</summary>
internal static class SupportCostRoofOracle
{
    /// <summary>The highest feet at which a capsule of <paramref name="tuning"/>'s radius and height stays the contact
    /// skin below every roof point over the disc at <paramref name="axis"/>, or null when nothing is overhead. The
    /// capsule's top is rounded, so at a horizontal distance rho from the axis it lies
    /// <c>radius - sqrt(radius^2 - rho^2)</c> below its apex.</summary>
    internal static double? Limit(SupportFootprintScene scene, Vector2 axis, double footRadius, double supportHeight,
        in MoveTuning tuning)
    {
        double capsule = tuning.CapsuleRadius;
        if (!(footRadius < capsule))
            throw new ArgumentOutOfRangeException(nameof(footRadius), "The footprint must lie inside the capsule.");
        double? apex = null;
        foreach (OracleFace face in scene.Faces)
        {
            if (!(face.Normal.Y < 0)) continue;
            var region = new Region(face, axis.X, axis.Y, footRadius);
            if (region.Least(0) is not double lowest || !(lowest > supportHeight)) continue;
            double highest = region.Least(capsule)!.Value;
            if (apex is not double current || highest < current) apex = highest;
        }
        return apex is double top ? top - 2 * (double)tuning.CapsuleHalfHeight - ShellMotion.ContactSkin : null;
    }

    // Where a face's XZ projection meets the disc. Least(capsule) is the least of the face's height plus the
    // capsule's cap drop over that region, the highest apex the capsule may have beneath it. With capsule 0 it is
    // the least roof height. The sum is convex, so its least value lies at the unconstrained minimum along the
    // plane's fall, at a stationary point or end of an edge clipped to the disc, or at the arc point the plane falls
    // toward. Every candidate lies in the region, so the least candidate is exact.
    readonly struct Region(OracleFace face, double cx, double cz, double radius)
    {
        double Height(double x, double z)
        {
            OracleVector v = face.Vertices[0], n = face.Normal;
            return v.Y - (n.X * (x - v.X) + n.Z * (z - v.Z)) / n.Y;
        }

        internal double? Least(double capsule)
        {
            OracleVector[] v = face.Vertices;
            OracleVector n = face.Normal;
            var candidates = new List<(double X, double Z)>();
            // The plane's height falls fastest along (n.X, n.Z) / n.Y, at grade g.
            double gx = n.X / n.Y, gz = n.Z / n.Y, g = Math.Sqrt(gx * gx + gz * gz);
            double ux = g > 0 ? gx / g : 0, uz = g > 0 ? gz / g : 0;
            double free = capsule > 0 ? capsule * g / Math.Sqrt(1 + g * g) : 0;
            if (free <= radius) candidates.Add((cx + free * ux, cz + free * uz));
            candidates.Add((cx + radius * ux, cz + radius * uz));
            candidates.RemoveAll(p => !Inside(v, p.X, p.Z));
            for (int i = 0; i < v.Length; i++)
            {
                OracleVector a = v[i], b = v[(i + 1) % v.Length];
                double ex = b.X - a.X, ez = b.Z - a.Z, fx = a.X - cx, fz = a.Z - cz;
                double qa = ex * ex + ez * ez, qb = 2 * (fx * ex + fz * ez), qc = fx * fx + fz * fz;
                double discriminant = qb * qb - 4 * qa * (qc - radius * radius);
                if (qa == 0 || discriminant < 0) continue;
                double root = Math.Sqrt(discriminant);
                double low = Math.Max(0, (-qb - root) / (2 * qa)), high = Math.Min(1, (-qb + root) / (2 * qa));
                if (low > high) continue;
                var stops = new List<double> { low, high };
                if (capsule > 0)
                {
                    // Along the edge the height changes by slope per unit s, and the squared distance is
                    // qa s^2 + qb s + qc. Squaring the stationary condition gives a quadratic in s.
                    double slope = -(n.X * ex + n.Z * ez) / n.Y, k = 4 * slope * slope, c2 = capsule * capsule;
                    double sa = 4 * qa * qa + k * qa, sb = 4 * qa * qb + k * qb, sc = qb * qb - k * c2 + k * qc;
                    double sd = sb * sb - 4 * sa * sc;
                    if (sa > 0 && sd >= 0)
                    {
                        double rd = Math.Sqrt(sd);
                        foreach (double s in new[] { (-sb - rd) / (2 * sa), (-sb + rd) / (2 * sa) })
                            if (s > low && s < high) stops.Add(s);
                    }
                }
                foreach (double s in stops) candidates.Add((a.X + s * ex, a.Z + s * ez));
            }
            double? least = null;
            foreach ((double x, double z) in candidates)
            {
                double rho2 = (x - cx) * (x - cx) + (z - cz) * (z - cz);
                double y = Height(x, z) + (capsule > 0 ? capsule - Math.Sqrt(capsule * capsule - rho2) : 0);
                if (least is not double current || y < current) least = y;
            }
            return least;
        }
    }

    // Inside or on a convex polygon's XZ projection, whichever way it winds.
    static bool Inside(OracleVector[] v, double x, double z)
    {
        int sign = 0;
        for (int i = 0; i < v.Length; i++)
        {
            OracleVector a = v[i], b = v[(i + 1) % v.Length];
            double cross = (b.X - a.X) * (z - a.Z) - (b.Z - a.Z) * (x - a.X);
            int side = Math.Sign(cross);
            if (side == 0) continue;
            if (sign == 0) sign = side;
            else if (side != sign) return false;
        }
        return true;
    }
}
