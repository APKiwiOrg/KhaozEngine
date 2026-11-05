using System;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>A collider option refusal that preserves ArgumentOutOfRangeException catch compatibility.</summary>
public sealed class TileColliderOptionsRangeException : ArgumentOutOfRangeException
{
    /// <summary>Creates a refusal with its stable reason and original parameter and value context.</summary>
    public TileColliderOptionsRangeException(TileColliderOptionsRefusal reason, string message, string? paramName,
        object? actualValue) : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The stable reason the collider option was refused.</summary>
    public TileColliderOptionsRefusal Reason { get; }

    /// <summary>The refused WallThickness, WalkSurfaceThickness or BlockedHeight option.</summary>
    public string? OptionName { get; init; }
}
