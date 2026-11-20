using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>An analytic slab of closed water: the level field's geometry with a ceiling at its top instead of a free
/// surface. It implements only <see cref="IMovementWaterField"/>, so no capture certificate can answer for it.</summary>
internal sealed class ClosedLevelWaterField(string worldId, float ceilingY, float floorY) : IMovementWaterField
{
    readonly IMovementWaterField _slab = MovementWaterField.Level(worldId, ceilingY, floorY);
    public string SemanticIdentity => "closed/" + _slab.SemanticIdentity;
    public long Generation => 0;
    public MovementWaterExtent Extent => _slab.Extent;

    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin)
    {
        MovementWaterPoint point = _slab.SampleCentreWater(body, physicsOrigin);
        return point.Interval is { } open
            ? new(point.Availability, point.Space, point.Domain, point.InWater, point.SpeedScale, Closed(open))
            : point;
    }

    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        MovementCoverageResult result = _slab.TraceWater(query, physicsOrigin, identity, spans, contacts);
        for (int i = 0; i < result.ContactsWritten; i++)
        {
            MovementDomainContact c = contacts[i];
            contacts[i] = new(c.Domain, c.Space, Closed(c.Interval), c.IntervalColumnXZ, c.Normal, c.Fraction,
                c.BoundaryId, c.CoverageRegionHandle, c.Overlap, c.SpanOverlap);
        }
        return result;
    }

    static MovementWaterInterval Closed(MovementWaterInterval open) => new(open.LowerY, open.UpperY,
        open.NominalSurfaceY, false, open.LowerBoundaryId, open.UpperBoundaryId);
}
