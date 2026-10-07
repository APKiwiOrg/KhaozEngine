using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Closed-path fraction proof against one installed identity box under its owner's read gate.
/// No world candidate coverage, distance encoding or accepted placement is implied.</summary>
internal static class CapsuleBoxFractionSweep
{
    internal const uint PolicyVersion = 1;
    internal const int MaximumCells = 256;
    internal const int MaximumDepth = 48;

    internal static bool TrySweep(Shapes shapes, TypedIndex shape, RigidPose pose, Vector3 centre,
        float radius, float halfCylinderLength, Vector3 displacement, double maximumFractionWidth,
        int maximumCells, out double lower, out double? upper)
    {
        lower = default;
        upper = null;
        if (!double.IsFinite(maximumFractionWidth) || maximumFractionWidth <= 0 || maximumFractionWidth > 1 ||
            maximumCells <= 0 || maximumCells > MaximumCells ||
            !float.IsFinite(radius) || radius <= 0 || !float.IsFinite(halfCylinderLength) || halfCylinderLength < 0 ||
            !CapsuleSweepGeometry.TryReadIdentityBox(shapes, shape, pose, out _)) return false;

        GeometrySign initial = At(0);
        if (initial == GeometrySign.Unresolved) return false;
        if (initial is GeometrySign.Negative or GeometrySign.Zero)
        {
            upper = 0;
            return true;
        }

        Span<Cell> pending = stackalloc Cell[MaximumDepth + 1];
        pending[0] = new(0, 1, 0);
        int count = 1, processed = 0;
        while (count > 0)
        {
            if (processed++ == maximumCells) return false;
            Cell cell = pending[--count];
            GeometryVector start = CapsuleSweepPath.Point(centre, displacement, cell.From);
            GeometryVector end = CapsuleSweepPath.Point(centre, displacement, cell.To);
            if (!start.IsResolved || !end.IsResolved) return false;
            // Each path coordinate is affine. The endpoint hull contains the complete closed cell.
            GeometryVector region = new(Hull(start.X, end.X), Hull(start.Y, end.Y), Hull(start.Z, end.Z));
            GeometrySign coverage = CapsuleBoxPointWitness.ClassifyEnclosure(shapes, shape, pose,
                region, radius, halfCylinderLength);
            if (coverage == GeometrySign.Positive) continue;
            if (cell.To - cell.From <= maximumFractionWidth &&
                At(cell.To) is GeometrySign.Negative or GeometrySign.Zero)
            {
                // Left-first traversal proved every prior cell separated. The upper point is a
                // collision witness, not a placement, and the remaining cell encloses the first event.
                lower = cell.From;
                upper = cell.To;
                return true;
            }
            if (cell.Depth == MaximumDepth) return false;
            double middle = cell.From + (cell.To - cell.From) * 0.5;
            if (!(middle > cell.From && middle < cell.To)) return false;
            pending[count++] = new(middle, cell.To, cell.Depth + 1);
            pending[count++] = new(cell.From, middle, cell.Depth + 1);
        }
        lower = 1;
        return true;

        GeometrySign At(double fraction) => CapsuleBoxPointWitness.ClassifyEnclosure(shapes, shape, pose,
            CapsuleSweepPath.Point(centre, displacement, fraction), radius, halfCylinderLength);
    }

    static GeometryInterval Hull(GeometryInterval a, GeometryInterval b) =>
        GeometryInterval.Enclose(Math.Min(a.Lower, b.Lower), Math.Max(a.Upper, b.Upper));

    readonly record struct Cell(double From, double To, int Depth);
}
