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
public readonly record struct TileActorSpawn(ushort MaxHealth, byte AttackTicks, TileDirection Facing);
