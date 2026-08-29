using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The CLIENT's half of the combat target seam: the same net id space <see cref="TileEntityTargets"/> answers on the
/// server, resolved out of what this client actually holds. A remote comes off
/// <see cref="TileWorldClient.TryGetLatestRemoteTile(long, out TileCoord)"/> and the local player comes off its own
/// prediction.
/// <para>THE HONEST READ, never the delayed one. <c>TryGetRemoteTile</c> answers off the delayed render timeline the
/// bodies ride, which is the right read for an overlay drawn ON a body and the wrong one for a RULE: it is the truth
/// from a moment that has already passed, so a reach question asked of it is wrong by construction. This resolver
/// feeds the simulator, which is rules, so it takes the sibling read R0 landed for exactly this.</para>
/// <para>The LOCAL branch has exactly one live consumer today, and it is not an oversight. This client simulates
/// only its own entity, so <c>target == LocalNetId</c> can arise in one way: an <see cref="TileCommandKind.Attack"/>
/// naming the player's own net id. The server resolves that through its own map and stands (because the target IS
/// the attacker, the one case that stands inside a footprint instead of stepping off it, never because a footprint
/// interior is in reach), so answering it here is what makes the client's replay agree instead of clearing a lock
/// the server still holds.</para>
/// <para>The two heads still resolve one target to slightly DIFFERENT tiles, and the residue is accepted. What is
/// left after the honest read is the one-way latency no client can see, so a client predicting its approach to a
/// moving monster can still path toward a tile the server has just left. That is not a new class of disagreement: it
/// is the "two heads saw different blockers" case the reconcile snap already exists for, and the first step of an
/// approach is almost always identical on both heads because two tiles a step apart usually share a first step.</para>
/// </summary>
public sealed class TileRemoteTargets : ITileTargets
{
    readonly TileWorldClient client;

    /// <summary>Binds the resolver to the client whose view it reads.</summary>
    /// <param name="client">The client holding the snapshots. Held rather than copied, so the resolver sees the
    /// newest applied state on every call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    public TileRemoteTargets(TileWorldClient client) =>
        this.client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc/>
    public bool TryGetFootprint(long target, out TileRect footprint, out int plane)
    {
        footprint = default;
        plane = 0;
        TileCoord tile;
        if (target != 0 && target == client.LocalNetId) tile = client.Prediction.PredictedState.Tile;
        else if (!client.TryGetLatestRemoteTile(target, out tile)) return false;
        footprint = new TileRect(tile.X, tile.Z, 1, 1);
        plane = tile.Plane;
        return true;
    }
}
