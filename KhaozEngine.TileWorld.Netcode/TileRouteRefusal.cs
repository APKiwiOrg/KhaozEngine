namespace KhaozEngine.TileWorld.Netcode;

/// <summary>The stable reason a route step or route access was refused.</summary>
public enum TileRouteRefusal
{
    /// <summary>Refuses deriving a direction between non-adjacent planar tile coordinates.</summary>
    TilesNotAdjacent,
    /// <summary>Refuses reading the next tile of an idle route.</summary>
    IdleHasNoNext,
    /// <summary>Refuses reading the destination of a route with no tiles.</summary>
    EmptyHasNoEnd,
}
