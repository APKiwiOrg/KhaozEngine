using System;

namespace KhaozEngine.Movement;

/// <summary>Independent per-ability admission, route and exact reach limits. Default is invalid.</summary>
/// <param name="TriggerRange">Finite positive exact 3D shape distance in metres, checked only at admission.</param>
/// <param name="MaximumPathLength">Finite positive horizontal XZ route length in metres, checked on admission
/// and every replan. Slope rise does not consume this budget. It is not surface arc length or total pursuit travel.</param>
/// <param name="StopDistance">Finite nonnegative exact 3D shape reach in metres, with no hidden tolerance.</param>
public readonly record struct GroundPursuitSettings(float TriggerRange, float MaximumPathLength, float StopDistance)
{
    internal void Validate()
    {
        if (!float.IsFinite(TriggerRange) || TriggerRange <= 0)
            throw new ArgumentOutOfRangeException(nameof(TriggerRange));
        if (!float.IsFinite(MaximumPathLength) || MaximumPathLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumPathLength));
        if (!float.IsFinite(StopDistance) || StopDistance < 0)
            throw new ArgumentOutOfRangeException(nameof(StopDistance));
    }
}
