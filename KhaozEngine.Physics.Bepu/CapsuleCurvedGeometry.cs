using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Actual installed round dimensions and the public cylinder's base-offset wrapper.</summary>
internal static class CapsuleCurvedGeometry
{
    internal static bool TryCapture(Shapes shapes, TypedIndex root, out InstalledSweepGeometry? geometry)
    {
        geometry = null;
        if (!TryRead(shapes, root, out var shape, out var local, out var family, out float radius, out float half)) return false;
        geometry = new(shapes, root, shape, local, family, radius, half);
        return true;
    }

    internal static bool Matches(InstalledSweepGeometry geometry, Shapes shapes, TypedIndex root) =>
        TryRead(shapes, root, out var shape, out var local, out var family, out float radius, out float half) &&
        shape == geometry.Shape && local.Position == geometry.LocalPose.Position && local.Orientation == geometry.LocalPose.Orientation &&
        family == geometry.Curved && radius == geometry.Radius && half == geometry.HalfLength;

    static unsafe bool TryRead(Shapes shapes, TypedIndex root, out TypedIndex shape, out RigidPose local,
        out SupportCurvedShape family, out float radius, out float half)
    {
        shape = root; local = new(Vector3.Zero, Quaternion.Identity);
        family = default; radius = half = 0;
        if (shapes is null || !root.Exists) return false;
        bool wrapper = root.Type == default(Compound).TypeId;
        if (wrapper)
        {
            ref Compound compound = ref shapes.GetShape<Compound>(root.Index);
            if (!compound.Children.Allocated || compound.Children.Length != 1) return false;
            var child = compound.Children[0];
            shape = child.ShapeIndex; local = child.LocalPose;
            if (!shape.Exists || shape.Type != default(Cylinder).TypeId || local.Orientation != Quaternion.Identity) return false;
        }
        if (shape.Type != default(Sphere).TypeId && shape.Type != default(Capsule).TypeId && shape.Type != default(Cylinder).TypeId) return false;
        shapes[shape.Type].GetShapeData(shape.Index, out void* data, out int size);
        if (data == null) return false;
        if (shape.Type == default(Sphere).TypeId && size >= sizeof(Sphere))
        { family = SupportCurvedShape.Sphere; radius = ((Sphere*)data)->Radius; }
        else if (shape.Type == default(Capsule).TypeId && size >= sizeof(Capsule))
        { family = SupportCurvedShape.Capsule; radius = ((Capsule*)data)->Radius; half = ((Capsule*)data)->HalfLength; }
        else if (shape.Type == default(Cylinder).TypeId && size >= sizeof(Cylinder))
        { family = SupportCurvedShape.Cylinder; radius = ((Cylinder*)data)->Radius; half = ((Cylinder*)data)->HalfLength; }
        else return false;
        float ceiling = RepresentedGeometryTransforms.MaximumLocalMagnitude;
        return float.IsFinite(radius) && radius > 0 && radius <= ceiling && float.IsFinite(half) && half >= 0 && half <= ceiling &&
            (family != SupportCurvedShape.Cylinder || half > 0) && (!wrapper || local.Position == new Vector3(0, half, 0));
    }
}
