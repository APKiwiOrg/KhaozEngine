namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Simulator knobs both heads must agree on, or their replays diverge. Separated from
/// <see cref="TileStepTicks"/> because these feed the PATHFINDER rather than the clock, and a game usually
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

    /// <summary>Longest route a single click may produce, in steps. A pathfinder result longer than this is
    /// TRUNCATED to its first <see cref="MaxRouteSteps"/> tiles, so the player walks as far as one click allows and
    /// clicks again, which is the rule the tile stack is modelled on.
    /// <para>This is where the route cap is ENFORCED, and it is enforced in the simulator rather than on the wire
    /// on purpose: both heads truncate the same pathfinder result identically, so the client predicts the walk the
    /// server runs and <see cref="TileRoute.End"/> names the tile the walk actually ends on. A cap applied only at
    /// the encoder would leave the two heads walking to different destinations, with the owner told a new wrong one
    /// on every snapshot.</para>
    /// <para>Capped by <see cref="TileProtocol.MaxRouteSteps"/>, which is the wire's own limit and the default here.
    /// A larger value is refused at construction rather than at the first long click, because the encoder refuses a
    /// route it cannot carry and that refusal would otherwise land inside a server tick.</para></summary>
    public int MaxRouteSteps { get; init; } = TileProtocol.MaxRouteSteps;
}
