using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>World-space collider. Equal endpoints describe a sphere.</summary>
public readonly record struct ColliderCapsule(Vector3 Start, Vector3 End, float Radius);

/// <summary>World forces, resets and substep budget for a chain simulation.</summary>
public sealed record ChainSimulationOptions
{
    /// <summary>Fixed step duration, from 0.0001 s through 1/30 s.</summary>
    public float MaxStepSeconds { get; init; } = 1f / 120;
    public int MaxStepsPerFrame { get; init; } = 8;
    public float TeleportDistance { get; init; } = 2;
    public float LongestGapSeconds { get; init; } = 0.5f;
    public float HoldBlendSeconds { get; init; } = 0.25f;
    public Vector3 Gravity { get; init; } = new(0, -9.81f, 0);

    internal void Validate()
    {
        if (!(MaxStepSeconds is >= 0.0001f and <= 1f / 30))
            throw new ArgumentOutOfRangeException(nameof(MaxStepSeconds));
        if (MaxStepsPerFrame is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(MaxStepsPerFrame));
        if (!float.IsFinite(TeleportDistance) || TeleportDistance < 0)
            throw new ArgumentOutOfRangeException(nameof(TeleportDistance));
        if (!float.IsFinite(LongestGapSeconds) || LongestGapSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(LongestGapSeconds));
        if (!float.IsFinite(HoldBlendSeconds) || HoldBlendSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(HoldBlendSeconds));
        if (!ChainMath.IsFinite(Gravity))
            throw new ArgumentException("World gravity must be finite.", nameof(Gravity));
    }
}
