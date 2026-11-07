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
            var b = PropQueryGeometry.MeasureBounds(shape, pose).Values;
            if (!(b.MinX <= b.MaxX && b.MinY <= b.MaxY && b.MinZ <= b.MaxZ) ||
                !double.IsFinite(b.MinX) || !double.IsFinite(b.MinY) || !double.IsFinite(b.MinZ) ||
                !double.IsFinite(b.MaxX) || !double.IsFinite(b.MaxY) || !double.IsFinite(b.MaxZ))
            {
                bool empty = b.MinX > b.MaxX || b.MinY > b.MaxY || b.MinZ > b.MaxZ;
                string field = !double.IsFinite(b.MinX) ? "bounds.MinX" : !double.IsFinite(b.MinY) ? "bounds.MinY" :
                    !double.IsFinite(b.MinZ) ? "bounds.MinZ" : !double.IsFinite(b.MaxX) ? "bounds.MaxX" :
                    !double.IsFinite(b.MaxY) ? "bounds.MaxY" : "bounds.MaxZ";
                double actual = !double.IsFinite(b.MinX) ? b.MinX : !double.IsFinite(b.MinY) ? b.MinY :
                    !double.IsFinite(b.MinZ) ? b.MinZ : !double.IsFinite(b.MaxX) ? b.MaxX : !double.IsFinite(b.MaxY) ? b.MaxY : b.MaxZ;
                throw new MapDocumentException(empty ? MapDocumentRefusal.BoundsInvalid : MapDocumentRefusal.ValueNonfinite,
                    "Shape bounds are empty or not finite.")
                { FieldPath = empty ? "bounds" : field, ActualNumber = empty ? null : actual };
            }
            return new(b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ);
        }
        catch (NotSupportedException error)
        {
            throw new MapDocumentException(MapDocumentRefusal.PhysicsShapeUnsupported, error.Message)
            { ShapeType = shape.GetType() };
        }
    }
}
