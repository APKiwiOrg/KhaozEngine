using System.Collections.Generic;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Scaled and unscaled placements can share both complete shapes and individual backing arrays.</summary>
internal sealed class MapPresentationShapeInventory
{
    readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    internal long Shape(PhysicsShape? shape)
    {
        if (shape is null || !_seen.Add(shape)) return 0;
        return checked(shape switch
        {
            ConvexHullShape hull => 128L + Array(hull.Points, 12),
            TriangleMeshShape mesh => 128L + Array(mesh.Vertices, 12) + Array(mesh.Indices, 4),
            CompoundShape compound => 128L + Array(compound.Children, 64) + Children(compound),
            _ => 64L,
        });
    }
    long Array(System.Array values, int element) => _seen.Add(values) ? checked(element * (long)values.Length) : 0;
    long Children(CompoundShape shape)
    { long bytes = 0; foreach (var child in shape.Children) bytes = checked(bytes + Shape(child.Shape)); return bytes; }
}
