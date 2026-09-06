namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// What <see cref="TileWorldServer.SpawnActor"/> needs to build ONE actor entity: the numbers that go on its
/// components and nothing else. Deliberately smaller than <c>TileActorDefinition</c>, which is what a SPAWNER is
/// authored from and which additionally carries the leash, the wander radius and the respawn delay: those are
/// facts about a spawn POINT rather than about an entity, and the door does not need them.
/// </summary>
/// <param name="MaxHealth">Both the ceiling and the starting health. Must be above zero, refused at the door
/// otherwise, because an actor with no health is dead the tick it exists.</param>
/// <param name="AttackTicks">The swing cadence written onto <see cref="TileCombatState.AttackTicks"/>. Zero is
/// legal and means an actor that never swings on its own, which is a spawner-shaped decision rather than a broken
/// one.</param>
/// <param name="Facing">Which way the body starts facing. Cosmetic until it takes its first step.</param>
/// <param name="Mode">The cadence the actor is STANDING at, written onto its move state at spawn rather than
/// carried by a command. That is what makes a definition's <see cref="TileActorDefinition.StepMode"/> live from the
/// first tick without spending a latch on the spawn tick: the actor pass falls back to
/// <see cref="TileCommand.Continue"/> at the mode the actor already holds, so the cadence rides the state until
/// something deliberately replaces it. Defaults to <see cref="TileMoveMode.Walk"/>, which is what
/// <see cref="TileMoveState.At"/> would have written anyway.</param>
public readonly record struct TileActorSpawn(ushort MaxHealth, byte AttackTicks, TileDirection Facing,
    TileMoveMode Mode = TileMoveMode.Walk)
{
    /// <summary>The registered collision topology this actor moves over. Register a non-default key through
    /// <see cref="TileActorHost.RegisterTraversalProfile"/> before spawning. Kept outside the positional record
    /// shape so existing construction and deconstruction remain source compatible.</summary>
    public TileActorTraversalProfile TraversalProfile { get; init; } = TileActorTraversalProfile.Default;
}
