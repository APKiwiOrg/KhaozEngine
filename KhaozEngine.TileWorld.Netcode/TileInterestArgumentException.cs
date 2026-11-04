using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>An interest configuration refusal that preserves ArgumentException catch compatibility.</summary>
public sealed class TileInterestArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its stable reason and original parameter context.</summary>
    public TileInterestArgumentException(TileInterestRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The stable reason the interest configuration was refused.</summary>
    public TileInterestRefusal Reason { get; }

    /// <summary>The configured viewer interest radius in tiles.</summary>
    public float? InterestRadius { get; init; }

    /// <summary>The configured overlap margin in tiles.</summary>
    public float? OverlapMargin { get; init; }
}
