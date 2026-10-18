using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using BepuUtilities;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Closed A-first traversal of mutation-proved world bounds. Every intersecting raw
/// triangle completes, including those after Hit(0). No partial traversal result escapes.</summary>
internal static class CapsuleMeshSweep
{
    internal static CapsuleSweepResult Sweep(Shapes shapes, TypedIndex root, RigidPose pose,
        InstalledSweepGeometry geometry, in CapsuleQueryDimensions capsule, Vector3 centre,
        Vector3 displacement, QueryFilter filter, ref CapsuleSweepWork work, CapsuleSweepScratch scratch) =>
        SweepBounded(shapes, root, pose, geometry, capsule, centre, displacement, filter, ref work, scratch,
            CapsuleGeneralSweepPolicy.MaximumNodes, CapsuleGeneralSweepPolicy.MaximumLeaves, CapsuleGeneralSweepPolicy.MaximumPending);

    internal static CapsuleSweepResult SweepBounded(Shapes shapes, TypedIndex root, RigidPose pose,
        InstalledSweepGeometry geometry, in CapsuleQueryDimensions capsule, Vector3 centre,
        Vector3 displacement, QueryFilter filter, ref CapsuleSweepWork work, CapsuleSweepScratch scratch,
        int maximumNodes, int maximumLeaves, int maximumPending)
    {
        if (work.Exhausted || geometry.Mesh is not { } installed || !geometry.Matches(shapes, root) ||
            !geometry.TryPose(pose, out var map) || maximumNodes <= 0 || maximumNodes > CapsuleGeneralSweepPolicy.MaximumNodes ||
            maximumLeaves <= 0 || maximumLeaves > CapsuleGeneralSweepPolicy.MaximumLeaves ||
            maximumPending <= 0 || maximumPending > CapsuleGeneralSweepPolicy.MaximumPending ||
            !CapsuleSweepPruning.TryAperture(centre, capsule.Radius, capsule.Length * 0.5f, displacement, out Vector3 min, out Vector3 max)) return default;
        ref Mesh mesh = ref shapes.GetShape<Mesh>(root.Index);
        int index = 0, count = 0, nodes = 0, leaves = 0;
        bool hit = false;
        float lower = float.PositiveInfinity, upper = float.PositiveInfinity, error = 0;
        while (true)
        {
            if (index < 0)
            {
                int triangle = Tree.Encode(index);
                if ((uint)triangle >= installed.Count || leaves == maximumLeaves || !installed.TriangleMatches(mesh, triangle)) return Refuse(ref work);
                leaves++;
                InstalledSweepLeaf leaf = new(installed.Triangle(triangle), pose, map);
                CapsuleSweepResult result = CapsuleTriangleSweep.Sweep(leaf, centre, capsule.Radius, capsule.Length * 0.5f,
                    displacement, filter, CapsuleGeneralSweepPolicy.MaximumErrorMetres, ref work, scratch);
                if (!result.IsComplete) return Refuse(ref work);
                error = MathF.Max(error, result.CertifiedErrorMetres);
                if (result.Status == CapsuleSweepStatus.Hit)
                {
                    if (result.ImpactDistance is not float impact) return Refuse(ref work);
                    hit = true; lower = MathF.Min(lower, result.ClearThroughDistance); upper = MathF.Min(upper, impact);
                }
                if (count == 0) break;
                index = scratch.MeshPending[--count];
                continue;
            }
            if (nodes == maximumNodes || !installed.ReadNode(mesh, index, out Node node)) return Refuse(ref work);
            nodes++;
            var a = installed.Bounds(index, 0);
            bool visitA = BoundingBox.Intersects(min, max, a.Min, a.Max), visitB = false;
            if (installed.Count > 1)
            {
                var b = installed.Bounds(index, 1);
                visitB = BoundingBox.Intersects(min, max, b.Min, b.Max);
            }
            if (visitA)
            {
                if (visitB)
                {
                    if (count == maximumPending) return Refuse(ref work);
                    scratch.MeshPending[count++] = node.B.Index;
                }
                index = node.A.Index;
            }
            else if (visitB) index = node.B.Index;
            else
            {
                if (count == 0) break;
                index = scratch.MeshPending[--count];
            }
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
