using System;
using System.Text;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Conservative allocation demand of the released per-instance shape and envelope kernels.</summary>
internal static class MapPlacementPreparation
{
    internal static long InstanceBytes(MapResolvedPlacement placement, MapAssetShapes shapes, float scale)
    {
        // Geometry/envelope objects, digest buffers and strings, plus registration's temporary flattened children.
        long bytes = checked(2048L + 8L * (Encoding.UTF8.GetByteCount(placement.PlacementId) +
            (long)Encoding.UTF8.GetByteCount(shapes.AssetId)) + RegistrationBytes(shapes.Collider));
        if (MathF.Abs(scale - 1f) >= 1e-6f)
            bytes = checked(bytes + ScaleBytes(shapes.Collider) + ScaleBytes(shapes.Selection));
        return bytes;
    }

    static long ScaleBytes(PhysicsShape? shape) => checked(shape switch
    {
        null => 0,
        ConvexHullShape hull => 192L + 12L * hull.Points.Length,
        // Uniform scaling reuses mesh indices. Only vertex output is copied.
        TriangleMeshShape mesh => 192L + 12L * mesh.Vertices.Length,
        CompoundShape compound => 192L + 64L * compound.Children.Length + Children(compound, ScaleBytes),
        _ => 128L,
    });

    // Flattening copies child poses. Each leaf's float-error bounds also evaluate bounded scalar-polynomial
    // scratch arrays, independent of its vertex count. Admit those temporaries before registration bounds run.
    static long RegistrationBytes(PhysicsShape? shape) => shape switch
    {
        null => 0,
        CompoundShape compound => checked(512L + 256L * compound.Children.Length + Children(compound, RegistrationBytes)),
        _ => 32768L,
    };

    internal static long SweepBytes(PhysicsShape shape) => checked(shape switch
    {
        // Includes temporary box corners or circumscribed cylinder caps and the doubled hull output.
        BoxShape => 512L,
        ConvexHullShape hull => 192L + 24L * hull.Points.Length,
        TriangleMeshShape mesh => 192L + 24L * mesh.Vertices.Length,
        CylinderShape => 256L + 72L * MapInteractionEnvelope.CapSides,
        CompoundShape compound => 192L + 64L * compound.Children.Length + Children(compound, s => SweepBytes(s!)),
        _ => 128L,
    });

    static long Children(CompoundShape compound, Func<PhysicsShape?, long> demand)
    {
        long bytes = 0;
        foreach (var child in compound.Children) bytes = checked(bytes + demand(child.Shape));
        return bytes;
    }
}
