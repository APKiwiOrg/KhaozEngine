using System;
using System.Numerics;
using BepuPhysics;
using BepuUtilities;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Mutation-published maxima for a selected set of general installations.
/// Geometry and pose scans belong to the writer. Query padding reads only this value.</summary>
internal readonly record struct CapsuleSweepPruningBounds(double Root, double Leaf, double Child,
    double CompositionError, double Local, double TransposeNorm, double PairNorm, double InverseNorm,
    double Axis, double PreimageExcess, bool HasChild)
{
    internal bool IsResolved => TransposeNorm > 0 && InverseNorm > 0 && double.IsFinite(PreimageExcess);

    internal CapsuleSweepPruningBounds Include(CapsuleSweepPruningBounds other) => !IsResolved ? other : !other.IsResolved ? this : new(
        Math.Max(Root, other.Root), Math.Max(Leaf, other.Leaf), Math.Max(Child, other.Child),
        Math.Max(CompositionError, other.CompositionError), Math.Max(Local, other.Local),
        Math.Max(TransposeNorm, other.TransposeNorm), Math.Max(PairNorm, other.PairNorm),
        Math.Max(InverseNorm, other.InverseNorm), Math.Max(Axis, other.Axis), Math.Max(PreimageExcess, other.PreimageExcess),
        HasChild || other.HasChild);

    internal CapsuleSweepPruningBounds WithExcess(Vector3 min, Vector3 max, Vector3 pruningMin, Vector3 pruningMax) =>
        this with { PreimageExcess = Math.Max(PreimageExcess, Math.Max(Excess(min, pruningMin), Excess(pruningMax, max))) };

    internal static bool TryCreate(InstalledSweepGeometry geometry, RigidPose root, RigidPose leaf,
        InstalledPoseOperator map, out CapsuleSweepPruningBounds bounds)
    {
        bounds = default;
        if (!CapsuleSweepPruning.Validate(map, leaf.Orientation)) return false;
        double local = geometry.Curved.HasValue ? Up((double)geometry.Radius + geometry.HalfLength) : 0;
        for (int i = 0; i < geometry.VertexCount; i++) local = Math.Max(local, MaxAbs(geometry.Vertex(i)));
        double norm = 0, pair = 0, inverse = 0, axis = 0;
        for (int row = 0; row < 3; row++)
        {
            FeaturePoint r = row == 0 ? map.RepresentedX : row == 1 ? map.RepresentedY : map.RepresentedZ;
            double x = Magnitude(r.X.Bounds), y = Magnitude(r.Y.Bounds), z = Magnitude(r.Z.Bounds);
            norm = Math.Max(norm, Up(Up(x + y) + z));
            pair = Math.Max(pair, Math.Max(Up(x + y), Math.Max(Up(x + z), Up(y + z))));
            axis = Math.Max(axis, y);
            inverse = Math.Max(inverse, Up(Up(Component(map.InverseTransposeX, row) +
                Component(map.InverseTransposeY, row)) + Component(map.InverseTransposeZ, row)));
        }
        bool child = geometry.LocalPose.Position != Vector3.Zero;
        double h = 0, composition = 0;
        if (child)
        {
            var q = root.Orientation;
            var wide = new QuaternionWide { X = new(q.X), Y = new(q.Y), Z = new(q.Z), W = new(q.W) };
            Vector3Wide.Broadcast(geometry.LocalPose.Position, out var offset);
            QuaternionWide.TransformWithoutOverlap(offset, wide, out var rotated);
            Vector3 actual = new(rotated.X[0], rotated.Y[0], rotated.Z[0]);
            if (!InstalledPoseOperator.Within(actual, 16384)) return false;
            h = MaxAbs(actual);
            // ProveComposition checks that this wide offset is also the scalar
            // offset used by GetWorldPose. Retain outward sums across all exponents.
            composition = Math.Max(Composition(root.Position.X, actual.X, leaf.Position.X),
                Math.Max(Composition(root.Position.Y, actual.Y, leaf.Position.Y), Composition(root.Position.Z, actual.Z, leaf.Position.Z)));
        }
        bounds = new(MaxAbs(root.Position), MaxAbs(leaf.Position), h, composition, local, norm, pair, inverse, axis, 0, child);
        return bounds.IsResolved;
    }

    internal static double MaxAbs(Vector3 value) => Math.Max(Math.Abs((double)value.X), Math.Max(Math.Abs((double)value.Y), Math.Abs((double)value.Z)));
    static double Composition(float root, float offset, float leaf) => Magnitude(GeometryInterval.Exact(root)
        .Add(GeometryInterval.Exact(offset)).Subtract(GeometryInterval.Exact(leaf)));
    static double Excess(Vector3 a, Vector3 b) => Math.Max(0, Up(Math.Max((double)a.X - b.X, Math.Max((double)a.Y - b.Y, (double)a.Z - b.Z))));
    static double Magnitude(GeometryInterval value) => value.IsResolved ? Math.Max(Math.Abs(value.Lower), Math.Abs(value.Upper)) : double.PositiveInfinity;
    static double Component(FeaturePoint point, int row) => Magnitude((row == 0 ? point.X : row == 1 ? point.Y : point.Z).Bounds);
    static double Up(double value) => Math.BitIncrement(value);
}
