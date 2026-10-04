using System.Collections.Generic;

namespace KhaozEngine.NetWorld;

public sealed partial class ShardedWorldServer
{
    // Slots that sent ClientControlKind.ServerTickCapable this session. Forgotten on join and on leave, so a recycled
    // slot never inherits the hello.
    private readonly HashSet<int> tickedSlots = new();

    /// <summary>The number of <see cref="Tick"/> calls so far: 0 before the first, incremented as the first statement of
    /// <see cref="Tick"/> (before <see cref="OnBeforeTick"/>), whether or not any cell's accumulator steps. Every frame
    /// served by a call carries that call's value, and a client that sent
    /// <see cref="MoveProtocol.ClientControlKind.ServerTickCapable"/> receives it on the wire. Always counted, so a host
    /// reads it in its tick hooks instead of keeping its own frame count.</summary>
    public long ServerTick { get; private set; }

    // The served frame kind and framing for one slot: plain for a slot without the hello, ticked with this call's
    // ServerTick for one with it. A format 2 slot never reaches here, so its frames stay unticked.
    private (MoveProtocol.ServerFrameKind, byte[]) ServedFrame(int slot, MoveProtocol.ServerFrameKind kind, long netId,
        int ack, byte[] body)
    {
        if (!tickedSlots.Contains(slot)) return (kind, MoveProtocol.EncodeSnapshotFrame(netId, ack, body));
        MoveProtocol.ServerFrameKind ticked = kind == MoveProtocol.ServerFrameKind.Delta
            ? MoveProtocol.ServerFrameKind.TickedDelta
            : MoveProtocol.ServerFrameKind.TickedSnapshot;
        return (ticked, MoveProtocol.EncodeTickedSnapshotFrame(ServerTick, netId, ack, body));
    }
}
