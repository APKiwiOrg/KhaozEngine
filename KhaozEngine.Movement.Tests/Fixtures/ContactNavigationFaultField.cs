using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Tests.Movement.Fixtures;

internal sealed class ContactNavigationFaultField(bool hold = false) : IMovementWaterField
{
    readonly IMovementWaterField _inner = MovementWaterField.Level("world", -3, -4);
    readonly Dictionary<Vector3, int> _stationary = [];
    internal bool Refused { get; private set; }
    public string SemanticIdentity => "navigation-fault-field";
    public long Generation => 0;
    public MovementWaterExtent Extent => _inner.Extent;
    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) =>
        _inner.SampleCentreWater(body, physicsOrigin);

    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        bool refuse = false;
        if (hold && query.Delta == Vector3.Zero)
        {
            _stationary.TryGetValue(query.Body.Centre, out int count);
            _stationary[query.Body.Centre] = count + 1;
            refuse = count >= 4;
        }
        else if (!hold && !Refused && query.Delta.X > 0 &&
            MathF.Abs(query.Body.Centre.X - (-0.075f + 2f / 30f)) < 0.0001f &&
            MathF.Abs(query.Body.Centre.Z - 0.125f) < 0.0001f)
            refuse = true;
        if (refuse)
        {
            Refused = true;
            return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, identity);
        }
        return _inner.TraceWater(query, physicsOrigin, identity, spans, contacts);
    }
}
