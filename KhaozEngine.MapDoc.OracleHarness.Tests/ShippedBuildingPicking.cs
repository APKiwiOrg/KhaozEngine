using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;
using static KhaozEngine.Tests.MapDocOracle.ShippedBuildingInputs;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Measured legacy visual picking versus native interaction picking, deferred to named R11 acceptance.</summary>
internal static class ShippedBuildingPicking
{
    internal static object Compare(ShippedBuildingInputs inputs, JsonElement leaf, TileObject source,
        TileObjectArchetype archetype, MapPlacementGeometry native)
    {
        var metadata = leaf.GetProperty("metadata");
        var bounds = metadata.GetProperty("renderBounds");
        float units = Number(metadata, "sourceUnitsToMetres");
        // The private generator measures these original mesh bounds, independently of the native candidate.
        Vector3 min = Vector(bounds.GetProperty("min")) * units, max = Vector(bounds.GetProperty("max")) * units;
        Vector3 anchor = TileObjectPlacement.AnchorPosition(inputs.World, archetype, source);
        float yaw = TileObjectPlacement.YawRadians(archetype, source.Rotation);
        var matrix = TileObjectPlacement.LocalToWorld(inputs.World, archetype, source);
        var vertices = (from x in new[] { min.X, max.X }
                        from y in new[] { min.Y, max.Y }
                        from z in new[] { min.Z, max.Z }
                        select Vector3.Transform(new Vector3(x, y, z), matrix)).ToArray();
        var oldBounds = new MapBox3(vertices.Min(v => v.X), vertices.Min(v => v.Y), vertices.Min(v => v.Z),
            vertices.Max(v => v.X), vertices.Max(v => v.Y), vertices.Max(v => v.Z));
        var newBounds = native.Envelope.Bounds;
        var union = oldBounds.Union(newBounds);
        var centres = new[] { Centre(oldBounds), Centre(newBounds), Centre(union) }.Distinct().ToArray();
        float length = (float)Math.Max(union.MaxX - union.MinX, Math.Max(union.MaxY - union.MinY, union.MaxZ - union.MinZ)) + 2;
        var queries = new List<object>();
        foreach (var centre in centres)
            foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            {
                var origin = centre + axis * length;
                var direction = -axis;
                float maxDistance = length * 2;
                // This is the released TileObjectRaycast model-box test, using its shared placement and ray seams.
                bool oldHit = RayMath.IntersectObbY(origin, direction, anchor, yaw, min, max, out float oldDistance) && oldDistance < maxDistance;
                bool newHit = MapShapeQueries.Raycast(new(origin, direction, maxDistance), native.Envelope.Shape,
                    native.Envelope.WorldPose, out double newDistance, out _);
                queries.Add(new
                {
                    origin,
                    direction,
                    maxDistance,
                    source = new { hit = oldHit, distance = oldHit ? (double?)oldDistance : null },
                    native = new { hit = newHit, distance = newHit ? (double?)newDistance : null },
                    hitChanged = oldHit != newHit,
                    distanceDelta = oldHit && newHit ? (double?)(newDistance - oldDistance) : null,
                });
            }
        return new
        {
            sourceId = source.Id,
            acceptance = "deferred R11 acceptance",
            sourceBounds = oldBounds,
            nativeBounds = newBounds,
            boundsDelta = new MapBox3(newBounds.MinX - oldBounds.MinX, newBounds.MinY - oldBounds.MinY, newBounds.MinZ - oldBounds.MinZ,
                newBounds.MaxX - oldBounds.MaxX, newBounds.MaxY - oldBounds.MaxY, newBounds.MaxZ - oldBounds.MaxZ),
            sourceLocalBounds = new { min, max },
            sourcePlacement = matrix,
            queries,
            native.Envelope.RaiseMetres,
            native.Envelope.DerivedFromCollider,
        };
    }

    static Vector3 Centre(MapBox3 box) => new((float)((box.MinX + box.MaxX) * 0.5),
        (float)((box.MinY + box.MaxY) * 0.5), (float)((box.MinZ + box.MaxZ) * 0.5));
}
