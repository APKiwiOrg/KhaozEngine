using System;
using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>An upright capsule in absolute metres. Half-height includes both rounded ends.</summary>
public readonly struct MovementBody
{
    public Vector3 Centre { get; }
    public float Radius { get; }
    public float HalfHeight { get; }

    public MovementBody(Vector3 centre, float radius, float halfHeight)
    {
        if (!IsFinite(centre))
            throw new ArgumentOutOfRangeException(nameof(centre), "Centre must be finite.");
        if (!float.IsFinite(radius) || radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be finite and positive.");
        if (!float.IsFinite(halfHeight) || halfHeight < radius)
            throw new ArgumentOutOfRangeException(nameof(halfHeight), "Half-height must be finite and at least the radius.");
        Centre = centre;
        Radius = radius;
        HalfHeight = halfHeight;
    }

    internal void Validate(string parameterName)
    {
        if (Radius <= 0f)
            throw new ArgumentException("A default movement body is invalid.", parameterName);
    }

    internal static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
