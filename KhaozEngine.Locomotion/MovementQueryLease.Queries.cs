using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    /// <summary>Reads canonical centre-feet membership under the captured scope and lifetime.</summary>
    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body)
    {
        AssertUsable();
        if (!_selectionReady) return NoWaterFact(MovementAvailability.Unresolved);
        if (!body.IsValid || !SameWorld(body.CurrentSpace))
            return NoWaterFact(MovementAvailability.Invalid);
        Vector3 extent = new(body.Radius, body.HalfHeight, body.Radius);
        if (!Witness.Scope.ContainsBounds(body.Centre - extent, body.Centre + extent))
            return NoWaterFact(MovementAvailability.Unresolved);
        try
        {
            AssertCurrent();
            MovementWaterPoint point = _pin.SampleCentreWater(body);
            AssertCurrent();
            if (!point.IsValid) return NoWaterFact(MovementAvailability.Invalid);
            if (point.Availability != MovementAvailability.Known) return NoWaterFact(point.Availability);
            return SameWorld(point.Space)
                ? point : NoWaterFact(MovementAvailability.Invalid);
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return NoWaterFact(MovementAvailability.Stale); }
        catch (ArgumentException) { return NoWaterFact(MovementAvailability.Invalid); }
    }

    void AssertUsable()
    {
        AssertThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    static MovementWaterPoint NoWaterFact(MovementAvailability availability) =>
        new(availability, default, null, false, 0f, null);
}
