using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The engine's DEFAULT actor behaviour: wander, leash, retaliate, chase and stand-your-ground, parameterised
/// entirely by the definition. Shipping a default alongside the seam is deliberate. An engine with only the seam means the first
/// monster in the first game is a hundred lines of pathfind-and-leash the second game rewrites, which is the
/// fit-failure the engine-first rule exists to prevent, and an engine with only the default blocks the second
/// monster the day it needs to do anything else.
/// <para>Stateless, and that is load-bearing: one instance answers for every actor on the server, so any per-actor
/// memory here would need a prune keyed on actors that died. The randomness comes from
/// <see cref="TileActorContext.Rng"/>, which the engine derives fresh per actor per tick, so a replay of the same
/// server from the same seed produces the same wander with nothing stored anywhere.</para>
/// <para>The five rules, in the order they are asked, and the ORDER is the design. The leash outranks a live target,
/// so a monster dragged far enough breaks off whatever it was doing. A held target outranks a new attacker, which is
/// FIRST ATTACKER WINS and is the simplest rule that is not an aggro table. An incoming lock outranks wandering:
/// an actor something has targeted stands its ground instead of drifting away from the fight. Wandering is what is
/// left.</para>
/// </summary>
public sealed class TileWanderBehaviour : ITileActorBehaviour
{
    readonly TileCollisionMap map;
    readonly int meanPauseTicks;
    readonly int retaliateWindowTicks;

    /// <summary>Builds the default behaviour over the map its wander destinations must be standable on.</summary>
    /// <param name="map">The baked collision map, the same one the server steps over.</param>
    /// <param name="meanPauseTicks">Mean ticks an idle actor waits before it picks somewhere to wander to. A per
    /// tick roll rather than a countdown, so the behaviour keeps no per-actor state: the pause is randomised with
    /// this as its mean rather than bounded inside a band, and <see cref="TileActorContext.Walking"/> is what stops
    /// an actor re-rolling a destination it is still walking to. It is a mean on OPEN GROUND and a floor anywhere
    /// else: a destination that comes back blocked, outside the baked map or equal to the tile the actor is already
    /// on is dropped and re-rolled next tick, so on a cluttered map the pauses observed are longer than this.</param>
    /// <param name="retaliateWindowTicks">How recent a LANDED hit has to be to provoke a counterattack, so an
    /// actor does not retaliate against something that hit it a minute ago. A hit that landed for zero counts and a
    /// miss does not, which is the rule <see cref="TileCombatState.LastDamagedBy"/> itself carries.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    public TileWanderBehaviour(TileCollisionMap map, int meanPauseTicks = 12, int retaliateWindowTicks = 40)
    {
        this.map = map ?? throw new ArgumentNullException(nameof(map));
        this.meanPauseTicks = Math.Max(1, meanPauseTicks);
        this.retaliateWindowTicks = Math.Max(0, retaliateWindowTicks);
    }

    /// <inheritdoc/>
    public TileActorIntent Decide(in TileActorContext context)
    {
        TileActorDefinition definition = context.Definition;

        // LEASH, first, because it outranks everything including a live target. Full restore happens on ARRIVAL
        // rather than here, so a monster dragged out and abandoned is not instantly healthy where it was left.
        if (Chebyshev(context.Tile, context.Home) > definition.LeashRadius) return TileActorIntent.Break;

        // CHASE. One value, re-issued, costing the behaviour nothing per tick, because the follow is the stepper's.
        if (context.CombatTarget != 0L) return TileActorIntent.Attack(context.CombatTarget);

        // RETALIATE. Reached only when no target is held, which IS the first-attacker-wins rule.
        if (context.LastDamagedBy != 0L && context.Tick - context.LastDamagedTick <= retaliateWindowTicks)
            return TileActorIntent.Attack(context.LastDamagedBy);

        // STAND YOUR GROUND. Something is locked onto this actor and coming for it, so walking away is over: the
        // route in flight is cancelled and no new wander starts while the lock holds. BELOW retaliate, so an actor
        // the attacker has already hit answers back rather than waiting politely, and ABOVE wander, which is what
        // makes the wait hold. What a player reads from this is "it saw me": the attack click freezes the monster
        // where it is instead of letting it drift one more walk cycle before the first blow lands.
        if (context.TargetedBy != 0L) return TileActorIntent.Stand;

        // WANDER.
        if (context.Walking || definition.WanderRadius <= 0) return TileActorIntent.Idle;
        TileActorRandom rng = context.Rng;
        if (rng.Next(meanPauseTicks) != 0) return TileActorIntent.Idle;

        var goal = new TileCoord(
            context.Home.X + rng.Next(-definition.WanderRadius, definition.WanderRadius + 1),
            context.Home.Z + rng.Next(-definition.WanderRadius, definition.WanderRadius + 1),
            context.Home.Plane);
        // A destination nobody can stand on is dropped rather than walked toward, because the pathfinder's
        // nearest-reachable fallback would walk the actor to the edge of the obstruction and look like it was stuck
        // on it. Rolling again next tick is free.
        if (goal.Equals(context.Tile) || !map.HasRegion(goal.Region)
            || TileCollision.IsBlocked(map, goal.X, goal.Z, goal.Plane))
            return TileActorIntent.Idle;
        return TileActorIntent.WalkTo(goal);
    }

    // In LONG, for the reason TileWorldServer.GoalInRange is: nothing bounds a tile's X and Z, so an int
    // subtraction of two coordinates int.MinValue apart wraps and Math.Abs throws. The comparison the caller makes
    // is against a radius, so a saturating cast is the honest narrowing.
    static long Chebyshev(TileCoord a, TileCoord b) =>
        Math.Max(Math.Abs((long)a.X - b.X), Math.Abs((long)a.Z - b.Z));
}
