using System;
using System.Numerics;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// One body's DRAWN position, chasing the tile the simulation has already committed it to. A step commits
/// <see cref="TileMoveState.Tile"/> when it STARTS (see that type), so the target is a staircase that jumps a whole
/// tile at every commit, and this closes the remaining gap continuously: the gap HALVES every
/// <see cref="HalfLifeSeconds"/>, for ever, rather than being crossed on a schedule tied to the step.
/// <para>That "for ever" is the whole design. Anything that crosses the step on a schedule of its own has to
/// finish, and finishing before the next commit is a REST GAP: the body arrives, stands, and the next commit
/// starts it again, which reads as a metronome at run cadence however the schedule is tuned. Measured through the
/// real client wiring, a 0.1 s crossing window at a 1/6 s tick and a two-tick running step spent 157 of a
/// 220-frame route drawing the body at bit-identical positions, in runs of 14 frames. A chase has no schedule to
/// finish, so the body is still closing when the next tile commits and the motion never rests mid route.</para>
/// <para>EXPONENTIAL, so it is frame-rate independent by construction rather than by clamping: each advance
/// multiplies the remaining gap by <c>2^(-dt / halfLife)</c>, and the exponent is additive, so two frames of half a
/// dt land where one frame of dt does and 30 fps agrees with 144 fps at the same wall-clock instant. It is also
/// first order, which is what makes it monotone: the gap is scaled by a factor in (0, 1], so the drawn point moves
/// toward the target and can never pass it, whatever the frame rate and whatever the target does.</para>
/// <para>STATEFUL, which is why it is a class and why one is constructed PER BODY. <see cref="TilePresenter"/>
/// stays the pure pose mapper it always was and is handed <see cref="Drawn"/>. The local player's chase lives on
/// <see cref="TileWorldClient"/> beside the prediction layer, each remote's lives beside that remote's
/// interpolation, and both are built with the one
/// <see cref="TileWorldClientConfig.ChaseHalfLifeSeconds"/>, so the local body and every remote share one curve by
/// construction. A game with a body of its own to draw (a pet, a follower, a mount) builds one of these against
/// <see cref="TileWorldClient.ChaseHalfLifeSeconds"/> and gets the same feel for free.</para>
/// <para>Everything here is PRESENTATION. Nothing on the simulation path reads it, nothing writes back into a
/// <see cref="TileMoveState"/>, and the half life is deliberately NOT part of the client-server determinism
/// contract: two clients drawing at different half lives still replay byte-identically.</para>
/// </summary>
public sealed class TileChase
{
    /// <summary>The engine's default half life in SECONDS, and the number
    /// <see cref="TileWorldClientConfig.ChaseHalfLifeSeconds"/> starts at.
    /// <para>Sized against the RUN, because that is the cadence the metronome was reported at. At a 1/6 s tick and
    /// <see cref="TileStepTicks"/> of walk 4 / run 2, a running step is 0.333 s, which is 4.8 half lives: the gap
    /// is still 3.7 per cent of its post-commit size when the next tile commits, so the body is visibly closing
    /// the whole way and there is no rest gap to read as a beat. A walking step is 0.667 s, twice that, so a walk
    /// arrives and settles inside its step, which is the plant a slow step should have.</para>
    /// <para>What it costs is stated in the same breath, because it is the invariant: the steady-state lag is
    /// speed times this over ln 2, so 0.15 tiles walking (1.5 tiles per second) and 0.30 running (3.0). Both are
    /// well inside the half tile a full-step linear glide averages, which is the constant slide the playtest
    /// rejected, and the peak gap right after a commit is 1.04 tiles against that glide's 1.00.</para></summary>
    public const float DefaultHalfLifeSeconds = 0.07f;

    /// <summary>Below this much remaining gap, in TILES, an advance lands the body EXACTLY on its target instead of
    /// scaling the gap again. A thousandth of a tile is a millimetre on a one metre tile, so the snap is invisible,
    /// and it is what makes "standing still" mean bit-identical frames rather than an asymptote that keeps
    /// twitching in the low bits for ever. At <see cref="DefaultHalfLifeSeconds"/> a stopped body reaches it about
    /// ten half lives (0.7 s) after its last commit.</summary>
    public const float SettleTiles = 1e-3f;

    readonly float halfLife;
    Vector2 drawn;
    bool placed;

    /// <summary>Builds a chase at a half life in SECONDS.</summary>
    /// <param name="halfLifeSeconds">Seconds for the remaining gap to halve. Zero draws the body on its committed
    /// tile the instant the tile commits, which is the strictest reading of the invariant and what a game asks for
    /// when it wants no visual truth gap at all.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="halfLifeSeconds"/> is negative, infinite or
    /// not a number. An infinite half life is refused rather than read as "never move": a body that never reaches
    /// its tile is not a slower feel, it is a broken one, and a caller who wrote it meant something finite.
    /// </exception>
    public TileChase(float halfLifeSeconds = DefaultHalfLifeSeconds)
    {
        if (!float.IsFinite(halfLifeSeconds) || halfLifeSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(halfLifeSeconds), halfLifeSeconds,
                "A chase half life is a finite number of seconds, zero or longer.");
        halfLife = halfLifeSeconds;
    }

    /// <summary>Seconds for the remaining gap to halve.</summary>
    public float HalfLifeSeconds => halfLife;

    /// <summary>Where the body is DRAWN, in TILE units on the tile lattice (the same units
    /// <see cref="TileMoveState.Position"/> is in, so <see cref="TilePresenter.PoseAt"/> takes it verbatim).
    /// <see cref="Vector2.Zero"/> until the first <see cref="Advance"/> or <see cref="SnapTo"/> places it.</summary>
    public Vector2 Drawn => drawn;

    /// <summary>True once a target has placed the body. False on a chase nothing has advanced yet, which is the
    /// only state in which <see cref="Drawn"/> means nothing.</summary>
    public bool IsPlaced => placed;

    /// <summary>Puts the body ON <paramref name="target"/> this instant, with no pursuit across the move. Every
    /// DISCONTINUITY calls this: a teleport (an authoritative epoch advance), a hard snap, the prediction seed, and
    /// a remote first seen or seen again more than one step from where it was. Chasing across one of those would
    /// slide the avatar over every tile in the gap, which is the one thing a lattice body must never be drawn
    /// doing.</summary>
    /// <param name="target">The tile-space point to place the body on.</param>
    public void SnapTo(Vector2 target)
    {
        drawn = target;
        placed = true;
    }

    /// <summary>
    /// Closes <paramref name="dt"/> seconds of the gap to <paramref name="target"/> and returns the new
    /// <see cref="Drawn"/>. The first call PLACES the body rather than chasing from the origin, so a body's first
    /// frame is never a slide in from tile (0, 0).
    /// </summary>
    /// <param name="target">Where the body is trying to be: the committed tile's centre in tile units, plus
    /// whatever correction the drawing path folds in. See <see cref="TileWorldClient"/> for the local player's
    /// composition and why the correction goes into the TARGET rather than onto the result.</param>
    /// <param name="dt">Seconds since the previous advance. Zero and negative move nothing, which is the honest
    /// answer for a frame in which no time passed.</param>
    /// <returns>The new drawn position, the same value <see cref="Drawn"/> reads.</returns>
    public Vector2 Advance(Vector2 target, float dt)
    {
        if (!placed)
        {
            SnapTo(target);
            return drawn;
        }
        if (!(dt > 0f)) return drawn;
        Vector2 gap = drawn - target;
        // Zero half life is the instant draw, and the settle is what turns the asymptote into an actual rest. Both
        // land the body EXACTLY on the target, so a standing body's frames are bit-identical and a caller may
        // compare two poses for equality without an epsilon.
        if (halfLife <= 0f || gap.LengthSquared() <= SettleTiles * SettleTiles)
        {
            drawn = target;
            return drawn;
        }
        // The gap SCALED, never the position lerped: the target is the fixed point of the expression, so the drawn
        // point converges onto it exactly rather than onto whatever a (1 - factor) lerp rounds to, and the factor
        // being in (0, 1] is the no-overshoot proof.
        drawn = target + gap * MathF.Pow(2f, -dt / halfLife);
        return drawn;
    }
}
