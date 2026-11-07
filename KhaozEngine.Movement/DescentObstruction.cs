using System;
using System.Collections.Generic;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Exact intersection of an obstruction face with the ground and body-height half-spaces.</summary>
internal static class DescentObstruction
{
    readonly struct Plane(ReadOnlySpan<PhysicsEvidencePoint> points)
    {
        readonly DescentNumber _origin = DescentGeometry.Height(points, new(0, 0));
        readonly DescentNumber _x = DescentGeometry.Height(points, new(1, 0)) - DescentGeometry.Height(points, new(0, 0));
        readonly DescentNumber _z = DescentGeometry.Height(points, new(0, 1)) - DescentGeometry.Height(points, new(0, 0));
        internal DescentNumber At(DescentPoint point) => _origin + _x * point.X + _z * point.Z;
    }

    internal static bool Intersects(ReadOnlySpan<PhysicsEvidencePoint> face, ReadOnlySpan<PhysicsEvidencePoint> bed,
        List<DescentPoint> overlap, double lower, double upper)
    {
        var obstacle = new Plane(face);
        var ground = new Plane(bed);
        var low = new DescentNumber(lower);
        var high = new DescentNumber(upper);
        var clipped = Clip(overlap, point => obstacle.At(point) - ground.At(point));
        clipped = Clip(clipped, point => obstacle.At(point) - low);
        clipped = Clip(clipped, point => high - obstacle.At(point));
        return clipped.Count != 0;
    }

    static List<DescentPoint> Clip(List<DescentPoint> input, Func<DescentPoint, DescentNumber> distance)
    {
        var output = new List<DescentPoint>();
        if (input.Count == 0) return output;
        var previous = input[^1];
        var last = distance(previous);
        foreach (var point in input)
        {
            var current = distance(point);
            if ((last.Sign < 0) != (current.Sign < 0))
                output.Add(DescentPoint.Mix(previous, point, last / (last - current)));
            if (current.Sign >= 0) output.Add(point);
            previous = point;
            last = current;
        }
        return output;
    }
}
