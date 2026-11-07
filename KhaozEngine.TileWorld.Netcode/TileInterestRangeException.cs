using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>A footprint interest refusal that preserves ArgumentOutOfRangeException catch compatibility.</summary>
public sealed class TileInterestRangeException : ArgumentOutOfRangeException
{
    /// <summary>Creates a refusal with its stable reason and original parameter and value context.</summary>
    public TileInterestRangeException(TileInterestRefusal reason, string? paramName, object? actualValue, string message)
        : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The stable reason the footprint was refused.</summary>
    public TileInterestRefusal Reason { get; }

    /// <summary>The query radius required to serve the supplied footprint, in tiles.</summary>
    public float? RequiredQueryRadius { get; init; }

    /// <summary>The configured viewer interest radius in tiles.</summary>
    public float? InterestRadius { get; init; }

    /// <summary>The configured overlap margin in tiles.</summary>
    public float? OverlapMargin { get; init; }

    /// <summary>The supplied square footprint's edge length in tiles.</summary>
    public int? FootprintSize { get; init; }
}
