using System.Numerics;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The AIM half of <see cref="TileWorldClient"/>: which point a drawn body looks at when it holds a lock, and which
/// resolver answers it. Presentation only, and it is the one place the client turns a target id into a yaw.
/// <para>The rules are untouched. <see cref="TileMoveState.Facing"/> is still the cardinal side the two footprints
/// touch on, which is exactly right for reach and up to 18 degrees off as a DRAWN yaw the moment either body is
/// bigger than one tile: a one-tile body beside a 2x2 points at the column it touches rather than at the body, and
/// so does the 2x2. What is added here is the continuous aim over the same lock, so nothing on the wire, in the
/// follow or in the reach rules moves.</para>
/// </summary>
public sealed partial class TileWorldClient
{
    // The OBJECT space, the resolver the head handed in, null on a head with no interactions wired. A game's
    // TryGetAimPoint override rides on this one, which is why the aim reads it rather than computing a centre.
    readonly ITileTargets? objectTargets;
    // The ENTITY space on the NEWEST snapshot, which is the read the reach rules already make (see
    // TileRemoteTargets), and the one the local body's own lock is resolved through.
    readonly ITileTargets entityTargets;
    // The ENTITY space on the DELAYED timeline, which is the capture the remote bodies are DRAWN from. A remote
    // aimed off the newest snapshot would point at where its target is now while both bodies are drawn where they
    // were InterpolationDelayTicks ago, so a chase would draw the attacker leading its target.
    readonly ITileTargets delayedTargets;

    /// <summary>
    /// Where a presented state LOOKS, or false to keep its tile facing. The lock the state already carries is the
    /// whole rule: a <see cref="TileMoveState.CombatTarget"/>, or an <see cref="TileMoveState.InteractTarget"/>
    /// whose route has run out, which is a body standing at what it walked to rather than one still walking there.
    /// <para>A body MID STEP keeps its step facing, because the step's own direction is what its legs are doing. A
    /// body with no lock, and one whose target stopped resolving, keep the tile facing too, so a stale lock points
    /// nowhere new.</para>
    /// </summary>
    /// <param name="state">The presented state, a remote's delayed sample or the local rendered state.</param>
    /// <param name="delayed">True for a body drawn off the delayed timeline, which resolves its target there too.</param>
    /// <param name="aim">Where to look, in tile units on the lattice (x, z).</param>
    bool TryResolveAim(in TileMoveState state, bool delayed, out Vector2 aim)
    {
        aim = default;
        if (state.IsStepping) return false;
        if (state.CombatTarget != 0L) return TryResolveEntityAim(state.CombatTarget, delayed, out aim);
        if (state.InteractTarget == 0L || !state.Route.IsIdle) return false;
        return state.InteractDomain == TileInteractionDomain.Entity
            ? TryResolveEntityAim(state.InteractTarget, delayed, out aim)
            : objectTargets is not null && objectTargets.TryGetAimPoint(state.InteractTarget, out aim, out _);
    }

    // An entity target, on the timeline the LOOKING body is drawn on. The local player is the one id with no delayed
    // capture, because it is drawn off its own prediction rather than off the remote timeline, so it answers from
    // the newest read on both paths.
    bool TryResolveEntityAim(long target, bool delayed, out Vector2 aim) =>
        delayed && target != LocalNetId
            ? delayedTargets.TryGetAimPoint(target, out aim, out _)
            : entityTargets.TryGetAimPoint(target, out aim, out _);

    /// <summary>
    /// The DELAYED sibling of <see cref="TileRemoteTargets"/>: a remote's footprint off the same sample its body is
    /// drawn from, rather than off the newest applied snapshot. Presentation only, so it is private and never
    /// reaches the simulator: a RULE asked of the delayed read is wrong by construction, which is the whole reason
    /// the public resolver takes the honest one.
    /// </summary>
    sealed class DelayedRemoteTargets : ITileTargets
    {
        readonly TileWorldClient client;

        public DelayedRemoteTargets(TileWorldClient client) => this.client = client;

        public bool TryGetFootprint(long target, out TileRect footprint, out int plane) =>
            client.TryGetRemoteFootprint(target, out footprint, out plane);
    }
}
