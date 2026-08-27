namespace KhaozEngine.TileWorld.Netcode;

/// <summary>What a <see cref="TileActorIntent"/> asks for.</summary>
public enum TileActorIntentKind : byte
{
    /// <summary>Keep doing what you are doing. An actor walking a route keeps walking it.</summary>
    Idle = 0,

    /// <summary>Walk to <see cref="TileActorIntent.Tile"/>. Clears any combat target, exactly as a player's own
    /// walk does, because a walk is how anything on this lattice disengages.</summary>
    WalkTo = 1,

    /// <summary>Lock onto <see cref="TileActorIntent.Target"/> and chase it. Re-issuing it every tick costs nothing,
    /// because the follow is the stepper's.</summary>
    Attack = 2,

    /// <summary>Drop the target and go home. The engine turns this into a walk to the spawner's home tile and
    /// restores the actor to full health when it ARRIVES, not when it breaks.
    /// <para>DROPPING THE TARGET DROPS THE DAMAGE RECORD WITH IT (<see cref="TileCombatState.LastDamagedBy"/> and
    /// <see cref="TileCombatState.LastDamagedTick"/>), because a break that left it set would have a retaliating
    /// behaviour re-acquire the same attacker on the first tick the actor was back inside its leash.</para></summary>
    Break = 3,
}

/// <summary>
/// What a behaviour decided this tick. A small tagged value, and the boundary is drawn at exactly one place: an
/// intent names a TILE or a TARGET and never a route, a step, a facing or a tick. Everything about HOW the actor
/// gets there stays inside the stepper both heads run, so an actor can never move in a way a player could not.
/// </summary>
/// <param name="Kind">What this intent asks for.</param>
/// <param name="Tile">The destination for <see cref="TileActorIntentKind.WalkTo"/>, otherwise ignored.</param>
/// <param name="Target">The net id for <see cref="TileActorIntentKind.Attack"/>, otherwise 0.</param>
public readonly record struct TileActorIntent(TileActorIntentKind Kind, TileCoord Tile, long Target)
{
    /// <summary>Keep doing what you are doing.</summary>
    public static TileActorIntent Idle => new(TileActorIntentKind.Idle, default, 0L);

    /// <summary>Walk to a tile.</summary>
    /// <param name="tile">The destination.</param>
    public static TileActorIntent WalkTo(TileCoord tile) => new(TileActorIntentKind.WalkTo, tile, 0L);

    /// <summary>Lock onto an entity and chase it.</summary>
    /// <param name="netId">The target's net id.</param>
    public static TileActorIntent Attack(long netId) => new(TileActorIntentKind.Attack, default, netId);

    /// <summary>Drop the target and go home.</summary>
    public static TileActorIntent Break => new(TileActorIntentKind.Break, default, 0L);
}

/// <summary>
/// The read-only view a behaviour decides from, handed in by the engine. Every tile on it is a TICK-START tile: the
/// actor's own and its target's as they stood before anything moved this tick, so no actor's decision can depend on
/// another entity having already moved and the ECS iteration order cannot reach a decision.
/// <para>The target's tile comes from the tick's own snapshot rather than from a read through to the world, and that
/// is what makes the guarantee true rather than intended: the snapshot is taken before this pass runs, so a
/// behaviour asking how far away its target is gets the same answer whatever order the actors were decided in.</para>
/// </summary>
/// <param name="NetId">The actor's net id.</param>
/// <param name="Tile">The actor's committed tile as of the start of this tick.</param>
/// <param name="Home">Its spawner's authored tile, and the origin the leash and the wander radius are measured
/// from. An actor spawned without a spawner is handed the tile it was BORN on, captured once at its spawn: a home
/// re-read from the actor's own current tile every tick makes the leash unfireable and the wander an unbounded
/// random walk.</param>
/// <param name="Definition">What it was built from.</param>
/// <param name="Health">Its health right now.</param>
/// <param name="CombatTarget">The net id it is locked onto, 0 when it is not fighting.</param>
/// <param name="TargetTile">Its target's committed tile as of the start of this tick, resolved through the SAME
/// per-tick snapshot the follow inside the movement pass and a player's own Attack acceptance resolve through, so
/// every reader of one tick agrees about where a target is. Default when <paramref name="TargetResolved"/> is
/// false.</param>
/// <param name="TargetResolved">False when the actor holds no target, and false when it holds one this tick's
/// snapshot does not answer for, which is what a dead, despawned or mid-handoff target reads as. That is the same
/// answer the follow acts on, so a behaviour that breaks off on it agrees with the stepper rather than fighting
/// it.</param>
/// <param name="LastDamagedBy">The net id that last landed damage on it, 0 when nothing has.</param>
/// <param name="LastDamagedTick">The tick that damage landed on.</param>
/// <param name="Walking">True while it has a live route or a step in flight. A behaviour that re-rolled a
/// destination every tick would never arrive at one, so this is the field that stops it.</param>
/// <param name="Tick">The server tick being decided.</param>
/// <param name="Rng">Its own deterministic stream for this tick. Copy it to a local and draw from the copy.</param>
public readonly record struct TileActorContext(
    long NetId,
    TileCoord Tile,
    TileCoord Home,
    TileActorDefinition Definition,
    TileHealth Health,
    long CombatTarget,
    TileCoord TargetTile,
    bool TargetResolved,
    long LastDamagedBy,
    long LastDamagedTick,
    bool Walking,
    long Tick,
    TileActorRandom Rng);

/// <summary>
/// The one seam a game plugs an actor's decisions into. The engine owns the tick scheduling and the movement, this
/// owns the decisions, and that is the whole contract.
/// <para>A behaviour instance is SHARED by every actor that uses it, exactly as a
/// <see cref="TileMoveSimulator"/> is, so an implementation that keeps per-actor state has to key it and prune it
/// itself. The default (<see cref="TileWanderBehaviour"/>) deliberately keeps none, which is what
/// <see cref="TileActorRandom"/> exists to make possible.</para>
/// <para>A game that wants different behaviours per monster writes ONE implementation that dispatches on
/// <see cref="TileActorContext.Definition"/>'s <see cref="TileActorDefinition.Kind"/>, rather than the host holding
/// a behaviour per definition: the engine must not learn what a goblin is.</para>
/// </summary>
public interface ITileActorBehaviour
{
    /// <summary>Decides what one actor does this tick.</summary>
    /// <param name="context">The tick-start view of that actor.</param>
    TileActorIntent Decide(in TileActorContext context);
}
