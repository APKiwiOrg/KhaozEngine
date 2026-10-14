using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Bounds the binary32 registration boundary over every permitted whole-metre physics origin.</summary>
internal static class MapRegistrationBounds
{
    internal static MapBox3 Of(PhysicsShape shape, MapEffectiveTransform transform)
    {
        Quaternion orientation = MapBackendPoseAdapter.Pose(transform, WorldFrame.Origin).Orientation;
        PhysicsShape installed = Flatten(shape);
        var (sin, cos) = Math.SinCos(transform.YawRadians * 0.5);
        MapBox3 canonical = MapShapeBounds.Of(shape, new MapDoublePose(new(transform.X, transform.Y, transform.Z), 0, sin, 0, cos));
        var root = new MapDoublePose(new(transform.X, transform.Y, transform.Z), orientation.X, orientation.Y, orientation.Z, orientation.W);
        var poseError = new MapDouble3(PoseError(transform.X, true), PoseError(transform.Y, false), PoseError(transform.Z, true));
        var magnitude = new MapDouble3(RelativeMagnitude(transform.X, poseError.X), RelativeMagnitude(transform.Y, poseError.Y),
            RelativeMagnitude(transform.Z, poseError.Z));
        var leaves = installed is CompoundShape compound ? compound.Children : new[] { new CompoundChild(installed, Pose.Identity) };
        foreach (var leaf in leaves)
        {
            MapDoublePose ideal = root.Compose(leaf.Local);
            MapBox3 nominal = MapShapeBounds.Of(leaf.Shape, ideal);
            MapDouble3 error = Exact(leaf, orientation, transform, nominal) ? default
                : MapFloatGeometryError.Of(leaf.Shape, leaf.Local, ideal, orientation, magnitude, poseError, installed is CompoundShape);
            canonical = canonical.Union(new MapBox3(
                Lower(nominal.MinX, error.X), Lower(nominal.MinY, error.Y), Lower(nominal.MinZ, error.Z),
                Upper(nominal.MaxX, error.X), Upper(nominal.MaxY, error.Y), Upper(nominal.MaxZ, error.Z)));
        }
        return canonical;
    }

    // The installer starts with each outer child pose and rounds at every nested composition. Its intermediate
    // offsets can dwarf the final leaf extent, so an allowance based on the final radius cannot replace this path.
    // Pose.Compose is shared with both static and dynamic compound installation.
    static PhysicsShape Flatten(PhysicsShape shape)
    {
        if (shape is not CompoundShape compound) return shape;
        var leaves = new List<CompoundChild>();
        foreach (var child in compound.Children) Add(child.Shape, child.Local);
        return new CompoundShape(leaves.ToArray());

        void Add(PhysicsShape member, Pose local)
        {
            if (member is CompoundShape nested)
                foreach (var child in nested.Children) Add(child.Shape, local.Compose(child.Local));
            else leaves.Add(new(member, local));
        }
    }

    static double PoseError(double position, bool planar)
    {
        // The nearest origin frame is at most Grid/2 from the origin. This covers both root-pose conversions,
        // including an origin at the opposite world limit. Geometry has its own input-term bounds.
        double ulp = Quantum(Math.Abs(position) + MapWorldBuilder.MaxCoordinateMetres + WorldFrame.Grid);
        bool exactPosition = ulp <= 1 && Aligned(position, ulp);
        // Integer origins and frame anchors are multiples of this quantum. An aligned position survives
        // both subtractions exactly. Otherwise planar registration has two half-ULP roundings. Y first
        // rounds at the world origin, then subtracts the unframed physics origin.
        return exactPosition ? 0 : Math.BitIncrement(planar ? ulp : Math.Abs((double)(float)position - position) + ulp * 0.5);
    }

    static double RelativeMagnitude(double position, double error) =>
        Math.BitIncrement(Math.BitIncrement(Math.Abs(position) + MapWorldBuilder.MaxCoordinateMetres) + error);

    static bool Exact(CompoundChild leaf, Quaternion orientation, MapEffectiveTransform transform, MapBox3 bounds)
    {
        if (orientation != Quaternion.Identity || leaf.Local.Orientation != Quaternion.Identity) return false;
        double radius = Math.Max(Math.Max(Math.Abs(bounds.MinX), Math.Abs(bounds.MaxX)),
            Math.Max(Math.Max(Math.Abs(bounds.MinY), Math.Abs(bounds.MaxY)), Math.Max(Math.Abs(bounds.MinZ), Math.Abs(bounds.MaxZ))));
        double quantum = Quantum(radius + MapWorldBuilder.MaxCoordinateMetres + WorldFrame.Grid);
        return quantum <= 1 && Aligned(transform.X, quantum) && Aligned(transform.Y, quantum) && Aligned(transform.Z, quantum)
            && Aligned(leaf.Local.Position.X, quantum) && Aligned(leaf.Local.Position.Y, quantum) && Aligned(leaf.Local.Position.Z, quantum)
            && ExactGeometry(leaf.Shape, leaf.Local.Orientation, quantum);
    }

    static double Quantum(double magnitude)
    {
        float bound = (float)magnitude;
        if (!float.IsFinite(bound)) throw new MapDocumentException("Registration bounds are not representable.");
        if (bound < magnitude) bound = MathF.BitIncrement(bound);
        return (double)MathF.BitIncrement(bound) - bound;
    }

    static bool Aligned(double value, double quantum) => value / quantum == Math.Truncate(value / quantum);

    // Test the operands, not just the resulting endpoints. Two aligned endpoints can hide a half-grid child
    // centre that rounds during composition. Hull recentering and arbitrary rotations also need the allowance.
    static bool ExactGeometry(PhysicsShape shape, Quaternion orientation, double quantum)
    {
        if (orientation != Quaternion.Identity) return false;
        bool Vector(Vector3 v) => Aligned(v.X, quantum) && Aligned(v.Y, quantum) && Aligned(v.Z, quantum);
        return shape switch
        {
            BoxShape box => Vector(box.HalfExtents),
            SphereShape sphere => Aligned(sphere.Radius, quantum),
            CapsuleShape capsule => Aligned(capsule.Radius, quantum) && Aligned(capsule.Length * 0.5, quantum),
            CylinderShape cylinder => Aligned(cylinder.Radius, quantum) && Aligned(cylinder.Length * 0.5, quantum),
            TriangleMeshShape mesh => Array.TrueForAll(mesh.Vertices, v => Vector(v)),
            CompoundShape compound => Array.TrueForAll(compound.Children,
                child => Vector(child.Local.Position) && ExactGeometry(child.Shape, child.Local.Orientation, quantum)),
            _ => false,
        };
    }

    // Binary64 conversion is outward too. No padding is needed when every binary32 operation is exact.
    static double Lower(double value, double error) => error == 0 ? value : Math.BitDecrement(value - error);
    static double Upper(double value, double error) => error == 0 ? value : Math.BitIncrement(value + error);
}
