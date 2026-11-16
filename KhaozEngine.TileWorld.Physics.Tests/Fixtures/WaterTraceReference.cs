using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

internal static class WaterTraceReference
{
    [ThreadStatic] internal static int ConvexIntersectionsForTests;

    internal static MovementCoverageResult Trace(TileWaterTraceTestSnapshot field,
        in MovementMediumSweepQuery query, Vector3 origin, MovementQueryIdentity identity,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        ConvexIntersectionsForTests = 0;
        if (!query.IsValid || !identity.IsValid || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) ||
            !float.IsFinite(origin.Z) || query.Body.CurrentSpace.WorldId != "water-world")
            return new(MovementAvailability.Invalid, 0, 0, 0, 0, 0, identity);
        var bounds = new FrozenTileWaterPathBounds(query, origin);
        foreach (var column in field.Nonuniform)
            if (bounds.Meets(column.Minimum, column.Maximum))
            {
                var result = new FrozenWaterReference(field).TracePrisms(query, origin, identity, spans, contacts);
                ConvexIntersectionsForTests = FrozenTileWaterConvexShape.EvaluationsForTests;
                return result;
            }
        return FlatWaterCoverageReference.Trace(field.Columns, field.Boundaries, field.Exterior, query,
            origin, identity, spans, contacts);
    }
}
