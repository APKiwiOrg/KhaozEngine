using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>The deepest point at which a subject penetrates an obstacle: how far inside the obstacle the point
/// sits, in the obstacle's local units, and where the point is in world space.</summary>
public readonly record struct ClearancePenetration(float Depth, Vector3 WorldPoint);

/// <summary>Placed-pair queries for the clearance check. A subject's <see cref="ClearanceSamples"/> and an
/// obstacle's <see cref="ClearanceSolid"/> are each built once in their own local frame, then probed under any
/// placement, so one build serves every instance of a piece.</summary>
public static class ClearanceProbe
{
    /// <summary>Move every subject point to world space by <paramref name="subjectToWorld"/>, then into the
    /// obstacle's frame by the inverse of <paramref name="obstacleToWorld"/>, and measure its
    /// <see cref="ClearanceSolid.DepthAt"/>. Returns the deepest point whose depth is greater than
    /// <paramref name="tolerance"/>, with its world position, or null when none is. Throws
    /// <see cref="ArgumentException"/> when <paramref name="obstacleToWorld"/> is not invertible.</summary>
    public static ClearancePenetration? Deepest(ClearanceSamples subject, Matrix4x4 subjectToWorld,
        ClearanceSolid obstacle, Matrix4x4 obstacleToWorld, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(obstacle);
        if (!Matrix4x4.Invert(obstacleToWorld, out Matrix4x4 worldToObstacle))
            throw new ArgumentException("The obstacle transform is not invertible.", nameof(obstacleToWorld));

        ClearancePenetration? deepest = null;
        float best = tolerance;
        var points = subject.Points;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 world = Vector3.Transform(points[i], subjectToWorld);
            float depth = obstacle.DepthAt(Vector3.Transform(world, worldToObstacle));
            if (depth > best)
            {
                best = depth;
                deepest = new ClearancePenetration(depth, world);
            }
        }

        return deepest;
    }

    /// <summary>The world xz extent of a local box under <paramref name="toWorld"/>: the eight corners of
    /// <paramref name="localMin"/>..<paramref name="localMax"/> transformed, with x in <c>X</c> and z in
    /// <c>Y</c> of the returned vectors. A cheap broad phase before <see cref="Deepest"/>.</summary>
    public static (Vector2 Min, Vector2 Max) PlanarBounds(Vector3 localMin, Vector3 localMax, Matrix4x4 toWorld)
    {
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? localMin.X : localMax.X,
                (i & 2) == 0 ? localMin.Y : localMax.Y,
                (i & 4) == 0 ? localMin.Z : localMax.Z);
            Vector3 world = Vector3.Transform(corner, toWorld);
            var planar = new Vector2(world.X, world.Z);
            min = Vector2.Min(min, planar);
            max = Vector2.Max(max, planar);
        }

        return (min, max);
    }
}
