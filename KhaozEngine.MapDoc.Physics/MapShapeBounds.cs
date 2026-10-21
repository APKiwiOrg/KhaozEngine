global using MapDouble3 = KhaozEngine.Physics.ShapeDouble3;
global using MapDoublePose = KhaozEngine.Physics.ShapeDoublePose;
using System;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Tight scalar-double world bounds under the physics seam's shape and compound-pose conventions.</summary>
public static class MapShapeBounds
{
    public static MapBox3 Of(PhysicsShape shape, Pose pose) => Of(shape, MapDoublePose.From(pose));

    internal static MapBox3 Of(PhysicsShape shape, MapDoublePose pose)
    {
        try
        {
            var b = PropQueryGeometry.Bounds(shape, pose);
            return new(b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ);
        }
        catch (Exception error) when (error is NotSupportedException || error.GetType() == typeof(ArgumentException))
        {
            throw new MapDocumentException(error.Message);
        }
    }
}
