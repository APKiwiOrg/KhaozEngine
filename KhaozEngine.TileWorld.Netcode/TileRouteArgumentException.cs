using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>A route adjacency refusal that preserves ArgumentException catch compatibility.</summary>
public sealed class TileRouteArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its stable reason and original parameter context.</summary>
    public TileRouteArgumentException(TileRouteRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The stable reason the route step was refused.</summary>
    public TileRouteRefusal Reason { get; }

    /// <summary>The supplied tile the refused step would leave.</summary>
    public TileCoord? From { get; init; }

    /// <summary>The supplied tile the refused step would enter.</summary>
    public TileCoord? To { get; init; }
}
