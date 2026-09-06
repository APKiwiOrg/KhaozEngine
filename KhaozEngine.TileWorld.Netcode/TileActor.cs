using KhaozEngine.Ecs;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Marks an entity as a server-owned ACTOR rather than a player: same components, same stepper, same lattice, no
/// connection behind it. <see cref="TileMovementSystem"/> reads this tag to select the registered actor
/// traversal profile. Every actor simulator keeps the leash-sized actor options while the player simulator keeps
/// the click-sized ones.
/// <para>ECS-only, and deliberately on NO replication channel. A client tells a monster from a player by a
/// discriminator the GAME registers above <see cref="TileProtocol.FirstGameTypeId"/>, not by this tag, because what
/// a monster IS belongs to the game. One consequence has to be handled rather than assumed: a cell handoff rebuilds
/// an entity from its Migrate capture, so a migrated actor arrives WITHOUT this tag and its profile, exactly as it
/// arrives without its <see cref="PendingTileCommand"/>. <c>TileActorHost</c> restores both every tick for every live actor, which is
/// the same immunity step 1 of the tick body already gives every player, and it is why the host iterates its own net
/// id list rather than an ECS query over this tag.</para>
/// </summary>
public struct TileActor : IComponent
{
    /// <summary>The registered collision topology this actor moves over. Zero is
    /// <see cref="TileActorTraversalProfile.Default"/>.</summary>
    public TileActorTraversalProfile TraversalProfile;
}
