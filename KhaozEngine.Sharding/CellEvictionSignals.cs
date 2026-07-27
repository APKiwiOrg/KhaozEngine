namespace KhaozEngine.Sharding;

/// <summary>
/// The per-cell inputs an <see cref="ICellEvictionPolicy"/> decides on: how long the cell has gone unattended, how
/// much it holds, how close the nearest player is, and whether the host forbids removing it outright. Produced by
/// the eviction driver once per scan for every live cell, never on a hot path.
/// </summary>
/// <remarks>
/// A policy should treat <see cref="Pinned"/> as absolute: the host reports it for a cell whose removal would
/// destroy state that a cell snapshot does not carry (a joined player's entity, whose state persists on the player
/// record rather than in the cell blob). The mechanism refuses a pinned cell anyway, so a policy that ignores the
/// flag only wastes a snapshot.
/// </remarks>
public readonly struct CellEvictionSignals
{
    /// <param name="coord">The cell these signals describe.</param>
    /// <param name="ownedEntityCount">How many entities the cell authoritatively owns.</param>
    /// <param name="boundPlayerCount">How many connected clients are homed in this cell.</param>
    /// <param name="cellsToNearestBoundPlayer">Chebyshev cell distance to the nearest cell a client is homed in, or <see cref="int.MaxValue"/> when nobody is online.</param>
    /// <param name="pinned">Whether the host forbids removing this cell right now.</param>
    /// <param name="idleSeconds">Seconds the cell has gone with no client homed in it.</param>
    public CellEvictionSignals(CellCoord coord, int ownedEntityCount, int boundPlayerCount,
        int cellsToNearestBoundPlayer, bool pinned, float idleSeconds = 0f)
    {
        Coord = coord;
        OwnedEntityCount = ownedEntityCount;
        BoundPlayerCount = boundPlayerCount;
        CellsToNearestBoundPlayer = cellsToNearestBoundPlayer;
        Pinned = pinned;
        IdleSeconds = idleSeconds;
    }

    /// <summary>The cell these signals describe.</summary>
    public CellCoord Coord { get; }

    /// <summary>How many entities the cell authoritatively owns (index count, ghosts excluded).</summary>
    public int OwnedEntityCount { get; }

    /// <summary>How many connected clients are homed in this cell.</summary>
    public int BoundPlayerCount { get; }

    /// <summary>
    /// Chebyshev cell distance to the nearest cell a client is homed in, so 0 means a player is in this very cell
    /// and 1 means one is in a neighbour. <see cref="int.MaxValue"/> when no client is online at all, which is what
    /// lets an empty server unload its whole world.
    /// </summary>
    public int CellsToNearestBoundPlayer { get; }

    /// <summary>Whether the host forbids removing this cell right now.</summary>
    public bool Pinned { get; }

    /// <summary>Seconds the cell has gone with no client homed in it. Reset to 0 whenever one is.</summary>
    public float IdleSeconds { get; }

    /// <summary>The same signals with <see cref="IdleSeconds"/> replaced. The driver tracks idle time, the host does not.</summary>
    public CellEvictionSignals WithIdleSeconds(float idleSeconds) =>
        new(Coord, OwnedEntityCount, BoundPlayerCount, CellsToNearestBoundPlayer, Pinned, idleSeconds);
}
