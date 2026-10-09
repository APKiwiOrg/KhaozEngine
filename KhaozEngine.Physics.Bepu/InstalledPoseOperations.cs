using System;
using System.Numerics;
using BepuPhysics;
using BepuUtilities;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Operation correspondence only. Each primitive encloses its real expression and
/// binary32 evaluation. These non-affine rounded point boxes never establish finite topology.</summary>
internal static class InstalledPoseOperations
{
    internal static GeometryInterval Add(GeometryInterval a, GeometryInterval b) => a.Add(b).EncloseSingleRounding();
    internal static GeometryInterval Subtract(GeometryInterval a, GeometryInterval b) => a.Subtract(b).EncloseSingleRounding();
    internal static GeometryInterval Multiply(GeometryInterval a, GeometryInterval b) => a.Multiply(b).EncloseSingleRounding();
    internal static GeometryInterval Abs(GeometryInterval value) => !value.IsResolved ? default : GeometryInterval.Enclose(
        value.Lower <= 0 && value.Upper >= 0 ? 0 : Math.Min(Math.Abs(value.Lower), Math.Abs(value.Upper)),
        Math.Max(Math.Abs(value.Lower), Math.Abs(value.Upper)));
    internal static GeometryVector Add(GeometryVector a, GeometryVector b) => new(Add(a.X, b.X), Add(a.Y, b.Y), Add(a.Z, b.Z));
    internal static GeometryVector Subtract(GeometryVector a, GeometryVector b) =>
        new(Subtract(a.X, b.X), Subtract(a.Y, b.Y), Subtract(a.Z, b.Z));
    internal static GeometryVector Scale(GeometryVector a, GeometryInterval b) =>
        new(Multiply(a.X, b), Multiply(a.Y, b), Multiply(a.Z, b));
    internal static GeometryVector Negate(GeometryVector a) => new(Negate(a.X), Negate(a.Y), Negate(a.Z));
    static GeometryInterval Negate(GeometryInterval a) => !a.IsResolved ? default : GeometryInterval.Enclose(-a.Upper, -a.Lower);

    internal static GeometryInterval Dot(GeometryVector a, GeometryVector b)
    {
        GeometryInterval x = Multiply(a.X, b.X), y = Multiply(a.Y, b.Y), z = Multiply(a.Z, b.Z);
        // Wide source uses (x+y)+z. Scalar Vector3.Dot may reduce in another order. Retaining
        // all three pairings also retains skipped intermediate roundings, without assuming FMA.
        return Hull(Hull(Add(Add(x, y), z), Add(Add(x, z), y)), Add(Add(y, z), x));
    }

    internal static GeometryVector Cross(GeometryVector a, GeometryVector b) => new(
        Subtract(Multiply(a.Y, b.Z), Multiply(a.Z, b.Y)),
        Subtract(Multiply(a.Z, b.X), Multiply(a.X, b.Z)),
        Subtract(Multiply(a.X, b.Y), Multiply(a.Y, b.X)));

    internal static GeometryVector Normalize(GeometryVector value)
    {
        // CapsuleTriangleTester.cs:139-147 uses rounded raw edges, cross, Length, reciprocal,
        // Scale and Dot. Normalization has positive scale only after this zero-free proof.
        GeometryInterval squared = Add(Add(value.X.Square().EncloseSingleRounding(),
            value.Y.Square().EncloseSingleRounding()), value.Z.Square().EncloseSingleRounding());
        if (!squared.IsResolved || squared.Lower <= 0) return default;
        GeometryInterval length = squared.Sqrt().EncloseSingleRounding();
        if (!length.IsResolved || length.Lower <= 0) return default;
        GeometryInterval reciprocal = GeometryInterval.Exact(1).Divide(length).EncloseSingleRounding();
        return Scale(value, reciprocal);
    }

    static GeometryInterval Hull(GeometryInterval a, GeometryInterval b) => !a.IsResolved || !b.IsResolved ? default
        : GeometryInterval.Enclose(Math.Min(a.Lower, b.Lower), Math.Max(a.Upper, b.Upper));

    internal static bool ProveComposition(in RigidPose child, in RigidPose root, in RigidPose world)
    {
        if (!InstalledPoseOperator.TryCreate(root, out InstalledPoseOperator rootOperator) ||
            !InstalledPoseOperator.TryCreate(child, out _) || !InstalledPoseOperator.TryCreate(world, out _)) return false;
        Quaternion a = child.Orientation, b = root.Orientation;
        GeometryInterval ax = GeometryInterval.Exact(a.X), ay = GeometryInterval.Exact(a.Y);
        GeometryInterval az = GeometryInterval.Exact(a.Z), aw = GeometryInterval.Exact(a.W);
        GeometryInterval bx = GeometryInterval.Exact(b.X), by = GeometryInterval.Exact(b.Y);
        GeometryInterval bz = GeometryInterval.Exact(b.Z), bw = GeometryInterval.Exact(b.W);
        // QuaternionEx.cs:49-52 and QuaternionWide.cs:468-471, in their actual source order.
        GeometryInterval x = Subtract(Add(Add(Multiply(aw, bx), Multiply(ax, bw)), Multiply(az, by)), Multiply(ay, bz));
        GeometryInterval y = Subtract(Add(Add(Multiply(aw, by), Multiply(ay, bw)), Multiply(ax, bz)), Multiply(az, bx));
        GeometryInterval z = Subtract(Add(Add(Multiply(aw, bz), Multiply(az, bw)), Multiply(ay, bx)), Multiply(ax, by));
        GeometryInterval w = Subtract(Subtract(Subtract(Multiply(aw, bw), Multiply(ax, bx)), Multiply(ay, by)), Multiply(az, bz));
        if (!InstalledPoseOperator.Contains(x, world.Orientation.X) || !InstalledPoseOperator.Contains(y, world.Orientation.Y) ||
            !InstalledPoseOperator.Contains(z, world.Orientation.Z) || !InstalledPoseOperator.Contains(w, world.Orientation.W)) return false;
        // Child offsets have their own finite 2048 m input bound. The 64 m vertex cap is not a
        // proof of an offset. QuaternionEx's doubled-factor placements are equivalent here:
        // doubling any admitted binary32 component is exact, finite, and multiplication commutes.
        GeometryVector position = Add(rootOperator.RoundedDirection(FeaturePoint.Exact(child.Position).Bounds),
            FeaturePoint.Exact(root.Position).Bounds);
        if (!Contains(position, world.Position)) return false;
        var wideA = Broadcast(a); var wideB = Broadcast(b);
        QuaternionWide.ConcatenateWithoutOverlap(wideA, wideB, out QuaternionWide wideOrientation);
        Vector3Wide.Broadcast(child.Position, out Vector3Wide offset);
        QuaternionWide.TransformWithoutOverlap(offset, wideB, out Vector3Wide rotated);
        Vector3Wide.Broadcast(root.Position, out Vector3Wide translation);
        Vector3Wide.Add(rotated, translation, out Vector3Wide widePosition);
        for (int i = 0; i < Vector<float>.Count; i++)
        {
            // The captured GetWorldPose result is the data. A differing represented continuation
            // would require another leaf operator, so this bounded slice conservatively refuses it.
            if (wideOrientation.X[i] != world.Orientation.X || wideOrientation.Y[i] != world.Orientation.Y ||
                wideOrientation.Z[i] != world.Orientation.Z || wideOrientation.W[i] != world.Orientation.W ||
                widePosition.X[i] != world.Position.X || widePosition.Y[i] != world.Position.Y ||
                widePosition.Z[i] != world.Position.Z) return false;
        }
        return true;
    }

    static QuaternionWide Broadcast(Quaternion value) => new()
    {
        X = new Vector<float>(value.X),
        Y = new Vector<float>(value.Y),
        Z = new Vector<float>(value.Z),
        W = new Vector<float>(value.W),
    };

    internal static bool Contains(GeometryVector bounds, Vector3 value) => InstalledPoseOperator.Contains(bounds.X, value.X) &&
        InstalledPoseOperator.Contains(bounds.Y, value.Y) && InstalledPoseOperator.Contains(bounds.Z, value.Z);
}
