namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// What a <see cref="TileActorSpawner"/> builds from: engine-shaped data with ONE game-shaped hole. Everything here
/// is about the LATTICE and the TICK, which is the line this package draws. Everything a game would disagree about
/// (the accuracy roll, the max hit, the defence, what the thing is CALLED) is on the other side of it, reachable
/// through <see cref="Kind"/> and through <c>ITileCombatRules</c>.
/// </summary>
public sealed record TileActorDefinition
{
    /// <summary>A stable content id, for the game's own lookup and for a log line. The engine never parses it.</summary>
    public required string Id { get; init; }

    /// <summary>Starting and maximum health, written onto <see cref="TileHealth"/> at spawn.</summary>
    public required ushort MaxHealth { get; init; }

    /// <summary>Walk or run: which entry of the server's <see cref="TileStepTicks"/> this actor's steps take.
    /// Written onto the actor's move state at spawn (<see cref="TileActorSpawn.Mode"/>), so it is live from the
    /// first tick and then rides the command stream. A head that latches a different mode on one actor keeps it:
    /// this is the cadence an actor STARTS at, not one restated over it every tick.
    /// <para>A MODE rather than a tick count, and that is a consequence of there being exactly ONE actor simulator.
    /// <see cref="TileStepTicks"/> is a property of a <see cref="TileMoveSimulator"/> instance rather than of a
    /// state, so a per-definition tick count would need a per-definition simulator. The mode is the cadence knob
    /// this package already has, it rides the command stream the way a player's run toggle does, and a world that
    /// one day needs a third actor cadence adds a second actor simulator and a pick.</para></summary>
    public TileMoveMode StepMode { get; init; } = TileMoveMode.Walk;

    /// <summary>Ticks between swings, written onto <see cref="TileCombatState.AttackTicks"/> at spawn. Zero means an
    /// actor that never swings, which is a legitimate content decision (a critter, a training dummy).</summary>
    public byte AttackTicks { get; init; } = 10;

    /// <summary>How far from home an idle actor may wander, in tiles. Read by <c>TileWanderBehaviour</c>.</summary>
    public int WanderRadius { get; init; } = 4;

    /// <summary>How far from home an actor may be dragged before it BREAKS: drops its target, walks home, and
    /// restores to full on arrival. Sized against the actor simulator's path radius, since a leash longer than that
    /// window is a walk home the pathfinder cannot plan in one go.</summary>
    public int LeashRadius { get; init; } = 10;

    /// <summary>Ticks between the moment its actor is gone and the moment the spawner builds a new one.</summary>
    public int RespawnDelayTicks { get; init; } = 40;

    /// <summary>The one GAME-shaped hole: an opaque number the game reads to attach its own content. The engine
    /// never inspects it, the same way <c>TileProtocol</c>'s game-message kind is a number the engine routes and
    /// never opens. The engine must not learn what a goblin is.</summary>
    public int Kind { get; init; }
}
