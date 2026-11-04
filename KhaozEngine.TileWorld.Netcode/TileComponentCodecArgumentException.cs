using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>A tile component encoder refusal preserving ArgumentException catch compatibility.</summary>
public sealed class TileComponentCodecArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its stable reason and original parameter context.</summary>
    public TileComponentCodecArgumentException(TileComponentCodecRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The stable reason encoding was refused.</summary>
    public TileComponentCodecRefusal Reason { get; }

    /// <summary>The supplied route's step count, for RouteWriteLimitExceeded.</summary>
    public int? ActualCount { get; init; }

    /// <summary>The route's wire step limit, for RouteWriteLimitExceeded.</summary>
    public int? MaximumCount { get; init; }

    /// <summary>The supplied instance payload's byte count, for InstanceWriteLimitExceeded.</summary>
    public int? ActualByteCount { get; init; }

    /// <summary>The instance payload's byte limit, for InstanceWriteLimitExceeded.</summary>
    public int? MaximumByteCount { get; init; }
}
