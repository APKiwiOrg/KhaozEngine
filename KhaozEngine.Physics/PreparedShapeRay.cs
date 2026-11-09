using System;
using System.Numerics;
using static KhaozEngine.Physics.ShapeQueryMath;

namespace KhaozEngine.Physics;

/// <summary>Immutable pose-bound ray data. Retains the released transformed point order outside the frame loop.</summary>
internal sealed class PreparedShapeRay
{
    readonly PhysicsShape _shape;
    readonly ShapeDoublePose _pose;
    readonly ShapeDouble3[]? _points;
    readonly PreparedShapeRay[]? _children;
    internal PreparedShapeRay(PhysicsShape shape, Pose pose) : this(shape, ShapeDoublePose.From(pose)) { }
    PreparedShapeRay(PhysicsShape shape, ShapeDoublePose pose)
    {
        _shape = shape; _pose = pose;
        if (shape is ConvexHullShape hull) _points = Centred(pose, hull.Points);
        else if (shape is TriangleMeshShape mesh) _points = Centred(pose, mesh.Vertices);
        else if (shape is CompoundShape compound)
        {
            _children = new PreparedShapeRay[compound.Children.Length];
            for (int i = 0; i < _children.Length; i++)
                _children[i] = new(compound.Children[i].Shape, pose.Compose(compound.Children[i].Local));
        }
    }
    internal static long Bytes(PhysicsShape shape) => checked(128L + (shape switch
    {
        ConvexHullShape hull => 24L * hull.Points.Length,
        TriangleMeshShape mesh => 24L * mesh.Vertices.Length,
        CompoundShape compound => ChildrenBytes(compound),
        _ => 0,
    }));
    static long ChildrenBytes(CompoundShape compound)
    { long bytes = 32 + 8L * compound.Children.Length; foreach (var child in compound.Children) bytes = checked(bytes + Bytes(child.Shape)); return bytes; }
    internal bool Cast(in ShapeRay ray, out ShapeRayHit hit)
    {
        var (origin, direction) = ShapeRayQueries.Validate(ray);
        hit = default;
        if (!Cast(origin, direction, ray.MaxDistance, out double distance, out var normal, out bool inside)) return false;
        hit = new(distance, Unit(inside ? direction.Scale(-1) : normal), inside); return true;
    }
    bool Cast(ShapeDouble3 origin, ShapeDouble3 direction, double end, out double t, out ShapeDouble3 normal, out bool inside)
    {
        t = 0; normal = default; inside = false;
        if (_children is not null)
        {
            bool any = false;
            foreach (var child in _children)
            {
                if (!child.Cast(origin, direction, any ? Math.Min(end, t) : end, out double ct, out var cn, out bool ci) || any && ct >= t) continue;
                any = true; t = ct; normal = cn; inside = ci;
            }
            return any;
        }
        if (_shape is ConvexHullShape)
        {
            if (!Raycast(_points, origin.Subtract(_pose.Position), direction, end, out t, out normal)) return false;
            inside = Dot(normal, normal) == 0; return true;
        }
        if (_shape is TriangleMeshShape mesh)
            return ShapeRayQueries.CastMesh(origin, direction, 0, end, _points!, mesh.Indices, _pose.Position, out t, out normal);
        return ShapeRayQueries.Cast(origin, direction, 0, end, _shape, _pose, out t, out normal, out inside);
    }
}
