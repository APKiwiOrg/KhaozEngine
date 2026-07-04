using System;
using KhaozEngine.Ecs;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;

namespace MmoServerSample;

/// <summary>A 2D world position - the one replicated/migrated gameplay component in this reference server.</summary>
public struct Position : IComponent
{
    public float X;
    public float Y;
}

/// <summary>A client's per-tick movement input: a position delta to apply to its player.</summary>
public readonly record struct MoveCommand(float Dx, float Dy);

/// <summary>A static server-owned world resource (e.g. an ore vein). Non-player cell state that must survive a restart.</summary>
public struct ResourceNode : IComponent
{
    public int Amount;
}

/// <summary>
/// A server-assigned appearance/behaviour discriminator on a non-player entity — which model a client draws for it
/// (goblin, merchant, ore vein, …). This is a CONSUMER extension component: it is registered at an id at/above
/// <see cref="ReplicationRegistry.FirstExtensionTypeId"/>, so it is length-prefixed on the wire and an older client
/// that never registered <see cref="Creature"/> simply skips it (keeps running, just can't tell the kind apart).
/// Players carry NO <see cref="Creature"/>, so a client tells an NPC from a player by its presence.
/// </summary>
public struct Creature : IComponent
{
    /// <summary>The consumer's model/kind id (0 = unspecified). Game-defined.</summary>
    public int Kind;
}

/// <summary>
/// A hidden server-only threat/aggro counter on an NPC. Registered <c>Persist | Migrate</c> (NOT Replicate): the mob
/// keeps its grudge across a cell handoff and a server restart, but the value never reaches ANY client - it is never
/// on the replication wire. Demonstrates decoupling persisted+migrated server state from replicated state.
/// </summary>
public struct AggroCounter : IComponent
{
    /// <summary>Accumulated threat (server-authoritative, never replicated).</summary>
    public int Value;
}

/// <summary>
/// A player's private stat - here exact HP. Registered <c>Default | OwnerOnly</c> (replicate + persist + migrate,
/// but owner-scoped on the wire): it is replicated ONLY to the client whose player this is, never to another player
/// who has it in area-of-interest, closing the map-hack surface where a component would leak private state to
/// observers. It still persists and migrates like any owned state.
/// </summary>
public struct PrivateStats : IComponent
{
    /// <summary>Exact health, visible only to the owning client.</summary>
    public int Health;
}

/// <summary>Shared wire helpers so the server and its clients agree on encodings.</summary>
public static class MmoProtocol
{
    /// <summary>Type id of the <see cref="Creature"/> discriminator — a consumer extension id (>= the floor), so
    /// older clients skip it instead of failing (see <see cref="ReplicationRegistry.FirstExtensionTypeId"/>).</summary>
    public const ushort CreatureTypeId = ReplicationRegistry.FirstExtensionTypeId;

    /// <summary>Type id of the hidden <see cref="AggroCounter"/> (Persist|Migrate, never replicated).</summary>
    public const ushort AggroCounterTypeId = ReplicationRegistry.FirstExtensionTypeId + 1;

    /// <summary>Type id of the owner-only <see cref="PrivateStats"/> (Default|OwnerOnly).</summary>
    public const ushort PrivateStatsTypeId = ReplicationRegistry.FirstExtensionTypeId + 2;

    /// <summary>Replicated-component registry shared by server and client (must match on both ends).</summary>
    public static ReplicationRegistry CreateRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Position>(
            typeId: 1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Position { X = br.ReadSingle(), Y = br.ReadSingle() },
            lerp: (a, b, t) => new Position { X = a.X + (b.X - a.X) * t, Y = a.Y + (b.Y - a.Y) * t });
        r.Register<ResourceNode>(
            typeId: 2,
            write: (n, bw) => bw.Write(n.Amount),
            read: br => new ResourceNode { Amount = br.ReadInt32() });
        // Consumer extension component: an NPC/creature kind, registered above the reserved floor so it replicates
        // to clients that know it and is transparently skipped by clients that don't.
        r.Register<Creature>(
            typeId: CreatureTypeId,
            write: (c, bw) => bw.Write(c.Kind),
            read: br => new Creature { Kind = br.ReadInt32() });
        // Hidden server-only aggro: Persist|Migrate but NOT Replicate. Survives handoff + restart, never on the wire.
        r.Register<AggroCounter>(
            typeId: AggroCounterTypeId,
            write: (a, bw) => bw.Write(a.Value),
            read: br => new AggroCounter { Value = br.ReadInt32() },
            channels: ReplicationChannels.Persist | ReplicationChannels.Migrate);
        // Owner-only private stat: Default (replicate+persist+migrate) + OwnerOnly, so it reaches only the owner.
        r.Register<PrivateStats>(
            typeId: PrivateStatsTypeId,
            write: (s, bw) => bw.Write(s.Health),
            read: br => new PrivateStats { Health = br.ReadInt32() },
            channels: ReplicationChannels.Default | ReplicationChannels.OwnerOnly);
        return r;
    }

    /// <summary>Reads an entity's <see cref="Position"/> for the shard host's border/handoff/AoI math.</summary>
    public static bool PositionAccessor(World world, Entity entity, out float x, out float y)
    {
        if (world.TryGet(entity, out Position p)) { x = p.X; y = p.Y; return true; }
        x = y = 0f;
        return false;
    }

    /// <summary>Encodes a chat line as a client-to-server game message, reusing the engine's generic game-message codec
    /// (<see cref="MoveProtocol.EncodeGameMessage"/>) with <see cref="MmoServer.ChatMessageKind"/> and a UTF-8 payload.
    /// The server decodes it with <see cref="MoveProtocol.TryDecodeGameMessage"/>, demuxed ahead of the move - it can
    /// never alias a move (see MoveProtocol's aliasing contract). This is what a turn-key consumer expresses as
    /// <c>WorldClient.SendGameMessage(MmoServer.ChatMessageKind, utf8, reliability)</c>.</summary>
    public static byte[] EncodeChat(string text) =>
        MoveProtocol.EncodeGameMessage(MmoServer.ChatMessageKind,
            System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty));

    /// <summary>Encodes a client move command: <c>[seq:int][dx:float][dy:float]</c>.</summary>
    public static byte[] EncodeMove(int seq, MoveCommand command)
    {
        var bytes = new byte[12];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), seq);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), command.Dx);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 4), command.Dy);
        return bytes;
    }

    /// <summary>Decodes a client move command. False if the payload is malformed (hostile-safe).</summary>
    public static bool TryDecodeMove(ReadOnlySpan<byte> data, out int seq, out MoveCommand command)
    {
        if (data.Length >= 12)
        {
            seq = BitConverter.ToInt32(data.Slice(0, 4));
            command = new MoveCommand(BitConverter.ToSingle(data.Slice(4, 4)), BitConverter.ToSingle(data.Slice(8, 4)));
            return true;
        }
        seq = -1;
        command = default;
        return false;
    }

    // Replication-ack frame: [marker:0xA0][appliedSeq:int] = 5 bytes, distinct in length from the 12-byte move so the
    // receive path demuxes them without aliasing. The client sends it after applying each delta; the server feeds the
    // seq to AoiDeltaReplicator.Acknowledge to advance that client's delta baseline (a dropped ack self-heals).
    private const byte AckMarker = 0xA0;
    private const int AckSize = 5;

    /// <summary>Encodes a replication ack carrying the client's <see cref="ClientReplicationView.LastAppliedSeq"/>.</summary>
    public static byte[] EncodeAck(int appliedSeq)
    {
        var bytes = new byte[AckSize];
        bytes[0] = AckMarker;
        BitConverter.TryWriteBytes(bytes.AsSpan(1, 4), appliedSeq);
        return bytes;
    }

    /// <summary>Decodes a replication ack written by <see cref="EncodeAck"/>. False for a move (different length).</summary>
    public static bool TryDecodeAck(ReadOnlySpan<byte> data, out int appliedSeq)
    {
        if (data.Length == AckSize && data[0] == AckMarker)
        {
            appliedSeq = BitConverter.ToInt32(data.Slice(1, 4));
            return true;
        }
        appliedSeq = -1;
        return false;
    }
}
