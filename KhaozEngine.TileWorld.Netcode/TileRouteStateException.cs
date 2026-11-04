using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>A route access refusal that preserves InvalidOperationException catch compatibility.</summary>
public sealed class TileRouteStateException : InvalidOperationException
{
    /// <summary>Creates a refusal with its stable reason.</summary>
    public TileRouteStateException(TileRouteRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The stable reason the route access was refused.</summary>
    public TileRouteRefusal Reason { get; }

    /// <summary>The next-tile index when an idle route's next tile was requested.</summary>
    public int? Index { get; init; }

    /// <summary>The number of tiles in the refused route.</summary>
    public int? TileCount { get; init; }
}
