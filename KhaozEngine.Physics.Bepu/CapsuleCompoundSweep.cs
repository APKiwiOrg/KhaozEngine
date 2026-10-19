using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace KhaozEngine.Physics.Bepu;

/// <summary>One flat installed compound, with no publication until every raw child completes.</summary>
internal static class CapsuleCompoundSweep
{
    internal static bool TryCapture(Shapes shapes, TypedIndex root, out InstalledSweepGeometry? geometry)
    {
        geometry = null;
        if (shapes is null || !root.Exists || root.Type != default(Compound).TypeId) return false;
        ref Compound compound = ref shapes.GetShape<Compound>(root.Index);
        if (!compound.Children.Allocated || compound.Children.Length is < 1 or > CapsuleGeneralSweepPolicy.MaximumChildren) return false;
        var children = new InstalledSweepGeometry[compound.Children.Length];
        for (int i = 0; i < children.Length; i++)
        {
            var child = compound.Children[i];
            int type = child.ShapeIndex.Type;
            // ShapeFactory flattens seam nesting. Never recurse through an installed
            // compound or a mesh child, whose installation remains unsupported.
            if (!child.ShapeIndex.Exists || (type != default(Box).TypeId && type != default(ConvexHull).TypeId &&
                type != default(Sphere).TypeId && type != default(Capsule).TypeId && type != default(Cylinder).TypeId) ||
                !InstalledPoseOperator.TryCreate(child.LocalPose, out _) ||
                !CapsuleSweepGeometry.TryCapture(shapes, child.ShapeIndex, out var native) || native is null) return false;
            children[i] = new(native, root, child.LocalPose, i);
        }
        geometry = new(shapes, root, children);
        return true;
    }

    internal static CapsuleSweepResult Sweep(Shapes shapes, TypedIndex root, RigidPose pose,
        InstalledSweepGeometry geometry, in CapsuleQueryDimensions capsule, Vector3 centre,
        Vector3 displacement, QueryFilter filter, ref CapsuleSweepWork work, CapsuleSweepScratch scratch)
    {
        if (work.Exhausted || filter.Layers != 0 || geometry.Children is not { } children ||
            children.Length is < 1 or > CapsuleGeneralSweepPolicy.MaximumChildren ||
            !geometry.Matches(shapes, root) || !geometry.TryPose(pose, out _)) return default;
        for (int i = 0; i < children.Length; i++)
        {
            var child = children[i];
            if (!child.TryPose(pose, out var map)) return Refuse(ref work);
            scratch.Pending[i] = new(child, child.LeafPose, map);
        }
        bool hit = false;
        float lower = float.PositiveInfinity, upper = float.PositiveInfinity, error = 0;
        // At most 64 leaves. Prove all of them rather than introducing a second
        // pruning policy. An early Hit(0) cannot hide an unsupported or exhausted child.
        for (int i = 0; i < children.Length; i++)
        {
            CapsuleSweepResult result = CapsuleGeneralSweep.SweepLeaf(scratch.Pending[i], capsule, centre, displacement, ref work, scratch);
            if (!result.IsComplete) return Refuse(ref work);
            error = MathF.Max(error, result.CertifiedErrorMetres);
            if (result.Status != CapsuleSweepStatus.Hit) continue;
            if (result.ImpactDistance is not float impact) return Refuse(ref work);
            hit = true; lower = MathF.Min(lower, result.ClearThroughDistance); upper = MathF.Min(upper, impact);
        }
        if (hit)
        {
            double span = (double)upper - lower;
            float enclosure = (float)span;
            if (enclosure < span) enclosure = MathF.BitIncrement(enclosure);
            error = MathF.Max(error, enclosure);
            return error <= CapsuleGeneralSweepPolicy.MaximumErrorMetres ? new(CapsuleSweepStatus.Hit, lower, upper, error) : default;
        }
        float length = (float)Math.Sqrt((double)displacement.X * displacement.X +
            (double)displacement.Y * displacement.Y + (double)displacement.Z * displacement.Z);
        return new(CapsuleSweepStatus.Clear, length, null, error);
    }

    static CapsuleSweepResult Refuse(ref CapsuleSweepWork work) { work.Exhausted = true; return default; }
}
