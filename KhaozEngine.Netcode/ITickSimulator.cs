namespace KhaozEngine.Netcode;

/// <summary>
/// The game's deterministic per-tick step: advances a state by one command over a time delta.
/// Used both to predict forward locally and to replay unacknowledged commands during reconciliation,
/// so the same function must drive both paths.
/// </summary>
public interface ITickSimulator<TState, TCommand>
{
    TState Step(in TState state, in TCommand command, float dt);
}
