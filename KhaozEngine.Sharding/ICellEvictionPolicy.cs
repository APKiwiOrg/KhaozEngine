namespace KhaozEngine.Sharding;

/// <summary>
/// Decides WHICH live cells are worth unloading. The engine owns the eviction mechanism (snapshot, durable save,
/// removal) and the game owns this call, because what makes a cell disposable is a game question: a world of static
/// resource nodes can unload aggressively, one where NPCs keep simulating off-screen cannot.
/// </summary>
/// <remarks>
/// Called once per live cell per eviction scan (a few seconds apart by default), never per tick, so a policy may do
/// real work. It must be a pure decision: the driver runs the guards, the snapshot and the store write, and a cell
/// it says yes to is still kept if any of those refuse. The shipped default is
/// <see cref="IdleCellEvictionPolicy"/>.
/// </remarks>
public interface ICellEvictionPolicy
{
    /// <summary>True when the cell described by <paramref name="signals"/> should be persisted and unloaded.</summary>
    bool ShouldEvict(in CellEvictionSignals signals);
}

/// <summary>
/// The shipped default: unload a cell that no client is homed in, that no client is within
/// <see cref="KeepRadius"/> cells of, and that has been in that state for <see cref="IdleSeconds"/>. The radius is
/// what keeps a player's own cell and the ring of neighbours mirroring border ghosts into it loaded, so an unload
/// can never pull entities out from under a client's area of interest. On an empty server every cell qualifies, so
/// the world unloads itself.
/// </summary>
public sealed class IdleCellEvictionPolicy : ICellEvictionPolicy
{
    /// <summary>How long a cell must go unattended before it is worth unloading. Default 300 seconds.</summary>
    public float IdleSeconds { get; init; } = 300f;

    /// <summary>
    /// Cells within this Chebyshev distance of any client's home cell are never unloaded. Default 2: one ring for
    /// the ghost neighbours feeding a client's area of interest, plus one of slack so a player walking at a cell
    /// boundary does not thrash a neighbour in and out.
    /// </summary>
    public int KeepRadius { get; init; } = 2;

    /// <inheritdoc />
    public bool ShouldEvict(in CellEvictionSignals signals) =>
        !signals.Pinned
        && signals.BoundPlayerCount == 0
        && signals.CellsToNearestBoundPlayer > KeepRadius
        && signals.IdleSeconds >= IdleSeconds;
}
