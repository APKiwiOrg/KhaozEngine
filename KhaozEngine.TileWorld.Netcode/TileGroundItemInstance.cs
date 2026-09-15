using KhaozEngine.Ecs;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>A dropped stack's ITEM INSTANCE: the identity that survives being dropped and picked up, and the
/// bytes that say what makes this one different from another of the same item. A SIBLING of
/// <see cref="TileGroundItem"/> rather than five more fields on it.</summary>
/// <remarks>
/// Opaque, exactly as <see cref="TileGroundItem"/> is opaque: an engine that knows what an item IS is an engine
/// a game cannot bring its own item model to. This holds a meaning-free <see cref="long"/> and meaning-free
/// bytes, so the tile netcode carries an instance across the wire without a dependency on the packages that
/// define one, and a game that has no instances never sees this type at all.
/// <para>A SIBLING because <see cref="TileGroundItem"/>'s codec writes twenty bytes with no declared length, so
/// a client built against that protocol consumes twenty bytes and then reads the next component's type id.
/// Widening it would make every already-shipped client misparse the rest of the entity. A new extension id is
/// length prefixed by <c>SnapshotWriter</c> precisely so a registry that never registered it can skip it, which
/// is what makes this additive on a live wire.</para>
/// <para>The component is seated only when there IS an instance, so the drop a kill usually leaves behind
/// carries nothing and costs nothing. See <see cref="TileWorldServer.SpawnGroundItem(TileCoord, int, int, long, long, System.ReadOnlySpan{byte})"/>
/// and the package README's ground-items section.</para>
/// </remarks>
public struct TileGroundItemInstance : IComponent
{
    /// <summary>The instance's identity, opaque to the engine. NEVER 0 on anything a server seats: a drop with
    /// no instance carries no component rather than a component carrying 0, which is what keeps a plain drop
    /// free on the wire and lets a reader treat presence as the question it asks.</summary>
    public long InstanceId;

    /// <summary>The instance's payload, opaque to the engine, at most
    /// <see cref="TileProtocol.MaxInstancePayloadBytes"/> bytes. The PUBLIC view of the instance rather than the
    /// whole of it: what is durable is the full payload, and what replicates is the part every viewer may see.
    /// <para>Never mutated in place. The server seats its own copy of what a caller handed it, a decoded one is
    /// the reader's own array, and a game that wants to change an instance spawns the drop again. May be null on
    /// a hand-built value, which the codec writes as empty.</para></summary>
    public byte[] Payload;
}
