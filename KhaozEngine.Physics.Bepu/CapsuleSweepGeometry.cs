using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Reads installed sweep geometry while the caller holds its owner's query gate.
/// Registry, index and pose must come from that same live owner and selected candidate.</summary>
internal static class CapsuleSweepGeometry
{
    internal static unsafe bool TryReadIdentityBox(Shapes shapes, TypedIndex shape, RigidPose pose, out Box box)
    {
        box = default;
        if (shapes is null || !shape.Exists || shape.Type != default(Box).TypeId ||
            pose.Orientation != Quaternion.Identity || !Finite(pose.Position)) return false;
        shapes[shape.Type].GetShapeData(shape.Index, out void* data, out int size);
        if (data == null || size < sizeof(Box)) return false;
        Box value = *(Box*)data;
        if (!Positive(value.HalfWidth) || !Positive(value.HalfHeight) || !Positive(value.HalfLength)) return false;
        box = value;
        return true;
    }

    internal static unsafe bool TryCapture(Shapes shapes, TypedIndex root, out InstalledSweepGeometry? geometry)
    {
        geometry = null;
        if (!TryReadIdentityBox(shapes, root, new RigidPose(Vector3.Zero, Quaternion.Identity), out Box box)) return false;
        Vector3 half = new(box.HalfWidth, box.HalfHeight, box.HalfLength);
        if (!InstalledPoseOperator.Within(half, RepresentedGeometryTransforms.MaximumLocalMagnitude)) return false;
        geometry = new(shapes, root, half);
        return true;
    }

    internal static bool TryReadLeaf(Shapes shapes, TypedIndex root, RigidPose parent,
        InstalledSweepGeometry geometry, int leafIndex, out InstalledSweepLeaf leaf)
    {
        leaf = default;
        if (leafIndex != 0 || !ReferenceEquals(shapes, geometry.Owner) || (root.Type != geometry.Root.Type || root.Index != geometry.Root.Index) ||
            !geometry.TryPose(parent, out InstalledPoseOperator map) ||
            !TryReadIdentityBox(shapes, root, new RigidPose(Vector3.Zero, Quaternion.Identity), out Box box) ||
            new Vector3(box.HalfWidth, box.HalfHeight, box.HalfLength) != geometry.Half) return false;
        leaf = new(geometry, parent, map);
        return true;
    }

    static bool Positive(float value) => float.IsFinite(value) && value > 0f;
    static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
