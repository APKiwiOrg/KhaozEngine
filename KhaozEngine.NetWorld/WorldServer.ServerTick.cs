using System.Collections.Generic;

namespace KhaozEngine.NetWorld;

public sealed partial class WorldServer
{
    // Slots that sent ClientControlKind.ServerTickCapable this session. Forgotten on join and on leave, so a recycled
    // slot never inherits the hello.
    private readonly HashSet<int> tickedSlots = new();

    /// <summary>The number of <see cref="Tick"/> calls so far: 0 before the first, incremented as the first statement of
    /// <see cref="Tick"/> (before <see cref="OnBeforeTick"/>). Every frame
    /// served by a call carries that call's value, and a client that sent
    /// <see cref="MoveProtocol.ClientControlKind.ServerTickCapable"/> receives it on the wire. Always counted, so a host
    /// reads it in its tick hooks instead of keeping its own frame count.</summary>
    public long ServerTick { get; private set; }

    // Fallback snapshots keep their owning path. The delta sender handles the negotiated delta kinds in place.
    private (MoveProtocol.ServerFrameKind, byte[]) ServedSnapshotFrame(int slot, long netId, int ack, byte[] body)
    {
        if (!tickedSlots.Contains(slot))
            return (MoveProtocol.ServerFrameKind.Snapshot, MoveProtocol.EncodeSnapshotFrame(netId, ack, body));
        return (MoveProtocol.ServerFrameKind.TickedSnapshot,
            MoveProtocol.EncodeTickedSnapshotFrame(ServerTick, netId, ack, body));
    }
}
