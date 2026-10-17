using System;
using System.Numerics;
using KhaozEngine.Tests.Physics;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>The roof over a cost scene's footprint disc, read in binary64 from the installed polygon faces. A roof is
/// a downward facing face whose lowest point over the disc lies above the support height. The cost scenes install no
/// curved surface overhead, so curved leaves are not read.</summary>
internal static class SupportCostRoofOracle
{
    /// <summary>The lowest roof point over the disc at <paramref name="axis"/>, or null when nothing is
    /// overhead.</summary>
    internal static double? Lowest(SupportFootprintScene scene, Vector2 axis, double radius, double supportHeight)
    {
        double? lowest = null;
        foreach (OracleFace face in scene.Faces)
        {
            if (!(face.Normal.Y < 0) || LowestOverDisc(face, axis.X, axis.Y, radius) is not double height ||
                !(height > supportHeight))
                continue;
            if (lowest is not double current || height < current) lowest = height;
        }
        return lowest;
    }

    // A plane's least height over the convex region where the face's projection meets the disc lies on that region's
    // boundary: a vertex inside the disc, an edge crossing the circle, or the circle point the plane falls toward.
    static double? LowestOverDisc(OracleFace face, double cx, double cz, double radius)
    {
        OracleVector[] v = face.Vertices;
        OracleVector n = face.Normal;
        double Height(double x, double z) => v[0].Y - (n.X * (x - v[0].X) + n.Z * (z - v[0].Z)) / n.Y;
        bool InDisc(double x, double z) => (x - cx) * (x - cx) + (z - cz) * (z - cz) <= radius * radius;
        double? lowest = null;
        void Offer(double x, double z)
        {
            double y = Height(x, z);
            if (lowest is not double current || y < current) lowest = y;
        }
        for (int i = 0; i < v.Length; i++)
        {
            OracleVector a = v[i], b = v[(i + 1) % v.Length];
            if (InDisc(a.X, a.Z)) Offer(a.X, a.Z);
            double dx = b.X - a.X, dz = b.Z - a.Z, fx = a.X - cx, fz = a.Z - cz;
            double qa = dx * dx + dz * dz, qb = 2 * (fx * dx + fz * dz), qc = fx * fx + fz * fz - radius * radius;
            double discriminant = qb * qb - 4 * qa * qc;
            if (qa == 0 || discriminant < 0) continue;
            double root = Math.Sqrt(discriminant);
            foreach (double t in new[] { (-qb - root) / (2 * qa), (-qb + root) / (2 * qa) })
                if (t >= 0 && t <= 1) Offer(a.X + t * dx, a.Z + t * dz);
        }
        // The plane's height falls fastest along (n.X, n.Z) / n.Y.
        double gx = n.X / n.Y, gz = n.Z / n.Y, g = Math.Sqrt(gx * gx + gz * gz);
        double px = g > 0 ? cx + radius * gx / g : cx, pz = g > 0 ? cz + radius * gz / g : cz;
        if (Inside(v, px, pz)) Offer(px, pz);
        if (Inside(v, cx, cz)) Offer(cx, cz);
        return lowest;
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
