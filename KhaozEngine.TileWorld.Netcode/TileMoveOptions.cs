namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Simulator knobs both heads must agree on, or their replays diverge. Separated from
/// <see cref="TileStepTicks"/> because these two feed the PATHFINDER rather than the clock, and a game usually
/// sets them once for the whole world while it tunes its step cadence by feel.
/// </summary>
public sealed record TileMoveOptions
{
    /// <summary>Footprint edge of a moving agent, in tiles. 1 is a player. Every tile of the footprint must be
    /// able to take a step for the step to be legal, so raising this narrows what the same map allows.</summary>
    public int AgentSize { get; init; } = 1;

    /// <summary>Half width of the pathfinder's search window, in tiles. A goal outside the window is treated as
    /// unreachable and walked toward instead, so this is the distance a single click can carry a player.</summary>
    public int MaxPathRadius { get; init; } = TilePathfinder.DefaultMaxRadius;
}
