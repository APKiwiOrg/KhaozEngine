namespace KhaozEngine.Netcode;

/// <summary>
/// The game's deterministic per-tick step: advances a state by one command over a time delta.
/// Used both to predict forward locally and to replay unacknowledged commands during reconciliation,
/// so the same function must drive both paths.
/// </summary>
public interface ITickSimulator<TState, TCommand>
{
    /// <summary>Advances <paramref name="state"/> by one <paramref name="command"/> over <paramref name="dt"/> seconds.</summary>
    /// <param name="state">The state to advance.</param>
    /// <param name="command">The command applied this tick.</param>
    /// <param name="dt">Fixed timestep in seconds.</param>
    /// <returns>The advanced state.</returns>
    TState Step(in TState state, in TCommand command, float dt);
}
