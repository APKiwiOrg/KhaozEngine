using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// The flat path remains the existing pre-index reference. Nonuniform evaluation is independently frozen.
internal sealed class FrozenWaterReference
{
    readonly TileWaterTraceTestSnapshot _geometry;
    readonly FrozenTileWaterPrismCoverage _prisms;
    readonly string _world;

    internal FrozenWaterReference(TileWaterTraceTestSnapshot geometry, string world = "water-world")
    {
        _geometry = geometry;
        _prisms = new(geometry);
        _world = world;
    }

    internal MovementCoverageResult Trace(in MovementMediumSweepQuery query, Vector3 origin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        using var counters = FrozenWaterCounterScope.Suppress();
        if (!query.IsValid || !identity.IsValid || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) ||
            !float.IsFinite(origin.Z) || query.Body.CurrentSpace.WorldId != _world)
            return new(MovementAvailability.Invalid, 0, 0, 0, 0, 0, identity);
        var bounds = new TileWaterPathBounds(query, origin);
        foreach (var column in _geometry.Nonuniform)
            if (bounds.Meets(column.Minimum, column.Maximum))
                return TracePrisms(query, origin, identity, spans, contacts);
        return FlatWaterCoverageReference.Trace(_geometry.Columns, _geometry.Boundaries, _geometry.Exterior,
            query, origin, identity, spans, contacts);
    }

    internal MovementCoverageResult TracePrisms(in MovementMediumSweepQuery query, Vector3 origin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
        _prisms.Trace(_geometry.Prisms, _geometry.Faces, _geometry.Ground, _geometry.PointIndex,
            _geometry.PrismStarts, _geometry.FaceStarts, query, origin, identity, spans, contacts);
}
