namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// How fast a step commits. The mode is a two-value SELECTOR, not a speed: the tick count each value maps to is
/// configuration the game hands the engine (<see cref="TileStepTicks"/>), never a constant in here. A tile world's feel
/// lives entirely in that cadence, so a game that wants a slower walk or a third gear changes its numbers rather
/// than the engine, and two games on the same package can disagree about what running means.
/// <para>Carried as a <see cref="byte"/> on purpose. It rides on the wire, on the predicted state and on the
/// replicated component, and every one of those wants a fixed one-byte field rather than a machine-width enum.</para>
/// </summary>
public enum TileMoveMode : byte
{
    /// <summary>The slower rate, one tile per <see cref="TileStepTicks.Walk"/> ticks. The default a fresh state
    /// starts in, so a player who never touches the run toggle walks.</summary>
    Walk = 0,

    /// <summary>The faster rate, one tile per <see cref="TileStepTicks.Run"/> ticks. Nothing here caps or meters
    /// it: a run energy budget is game rules, and belongs above this package.</summary>
    Run = 1,
}
