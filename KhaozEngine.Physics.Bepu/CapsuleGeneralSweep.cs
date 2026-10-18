using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Family dispatch for an installation, under its owner's selected read interval.</summary>
internal static class CapsuleGeneralSweep
{
    internal static CapsuleSweepResult Sweep(Shapes shapes, TypedIndex root, RigidPose pose,
        InstalledSweepGeometry geometry, in CapsuleQueryDimensions capsule, Vector3 centre,
        Vector3 displacement, QueryFilter filter, ref CapsuleSweepWork work, CapsuleSweepScratch scratch)
    {
        if (filter.Layers != 0) return default;
        if (CapsuleSweepGeometry.TryReadIdentityBox(shapes, root, pose, out _))
        {
            if (!work.Primitive()) return default;
            return CapsuleBoxSweep.Sweep(shapes, root, pose, centre, capsule.Radius, capsule.Length * 0.5f,
                displacement, CapsuleGeneralSweepPolicy.MaximumErrorMetres, CapsuleBoxFractionSweep.MaximumCells);
        }
        if (!CapsuleSweepGeometry.TryReadLeaf(shapes, root, pose, geometry, 0, out InstalledSweepLeaf leaf)) return default;
        return CapsuleConvexSweep.Sweep(leaf, centre, capsule.Radius, capsule.Length * 0.5f, displacement,
            CapsuleGeneralSweepPolicy.MaximumErrorMetres, ref work, scratch);
    }
}
