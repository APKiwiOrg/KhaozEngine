using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Independent feature equations. Each face, edge and corner has its own Voronoi region.
// This does not use the producer's slab partition or represented-root enclosure.
internal readonly record struct TileWaterCoverageOracle(Vector3 Minimum, Vector3 Maximum)
{
    internal double DistanceSquared(Vector3 centre, float radius, float halfHeight)
    {
        double core = (double)halfHeight - radius;
        double dx = Math.Max(Math.Max(Minimum.X - centre.X, centre.X - Maximum.X), 0);
        double dz = Math.Max(Math.Max(Minimum.Z - centre.Z, centre.Z - Maximum.Z), 0);
        double axialLow = centre.Y - core, axialHigh = centre.Y + core;
        double dy = Math.Max(Math.Max(Minimum.Y - axialHigh, axialLow - Maximum.Y), 0);
        return dx * dx + dy * dy + dz * dz;
    }

    internal bool Overlaps(Vector3 centre, float radius, float halfHeight) =>
        DistanceSquared(centre, radius, halfHeight) <= (double)radius * radius;

    internal (double Enter, double Exit)? Path(Vector3 centre, Vector3 delta, float radius, float halfHeight)
    {
        double core = (double)halfHeight - radius;
        double[] low = [Minimum.X, Minimum.Y - core, Minimum.Z];
        double[] high = [Maximum.X, Maximum.Y + core, Maximum.Z];
        double[] start = [centre.X, centre.Y, centre.Z], drive = [delta.X, delta.Y, delta.Z];
        var hits = new List<double>();
        if (Overlaps(centre, radius, halfHeight)) hits.Add(0);
        if (Overlaps(centre + delta, radius, halfHeight)) hits.Add(1);
        for (int sx = -1; sx <= 1; sx++)
            for (int sy = -1; sy <= 1; sy++)
                for (int sz = -1; sz <= 1; sz++)
                {
                    int[] side = [sx, sy, sz];
                    double a = 0, b = 0, c = -(double)radius * radius;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (side[axis] == 0) continue;
                        double offset = start[axis] - (side[axis] < 0 ? low[axis] : high[axis]);
                        a += drive[axis] * drive[axis];
                        b += 2 * offset * drive[axis];
                        c += offset * offset;
                    }
                    if (a == 0) continue;
                    double discriminant = b * b - 4 * a * c;
                    if (discriminant < 0) continue;
                    double root = Math.Sqrt(discriminant);
                    foreach (double t in new[] { (-b - root) / (2 * a), (-b + root) / (2 * a) })
                    {
                        if (t < 0 || t > 1) continue;
                        bool valid = true;
                        for (int axis = 0; axis < 3; axis++)
                        {
                            double p = start[axis] + t * drive[axis];
                            valid &= side[axis] switch
                            {
                                -1 => p <= low[axis],
                                1 => p >= high[axis],
                                _ => p >= low[axis] && p <= high[axis]
                            };
                        }
                        if (valid) hits.Add(t);
                    }
                }
        if (hits.Count == 0) return null;
        hits.Sort();
        return (hits[0], hits[^1]);
    }
}
