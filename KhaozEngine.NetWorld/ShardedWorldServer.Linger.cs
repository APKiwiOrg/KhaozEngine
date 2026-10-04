using System.Collections.Generic;

namespace KhaozEngine.NetWorld;

public sealed partial class ShardedWorldServer
{
    // Slots whose body lingers after a transport drop, each with the last ServerTick the body is stepped in. Entered
    // by the hold during NetServer.Poll, left only through OnLeave, which also releases the held slot.
    private readonly Dictionary<int, long> lingerUntilBySlot = new();
    // Expired slots collected before any leaves, because OnLeave edits lingerUntilBySlot. Reused across ticks.
    private readonly List<int> lingerScratch = new();

    /// <summary>True while the slot's body lingers after its link dropped (see
    /// <see cref="ShardedWorldServerConfig.DisconnectLingerTicks"/>). The slot stays joined meanwhile.</summary>
    public bool IsLingering(int slot) => lingerUntilBySlot.ContainsKey(slot);

    // NetServer.HoldSlotOnDisconnect, set only when the hook is. NetServer never asks for a connection it closed
    // itself, so kicks, bans, replication restarts and the rate limit kick never reach here. HasBegun stays true after
    // the drain completes, so nothing lingers once a drain has begun.
    private bool HoldOnDisconnect(int slot)
    {
        if (drain.HasBegun || !netIdBySlot.TryGetValue(slot, out long netId)) return false;
        int ticks = config.DisconnectLingerTicks!(slot, netId);
        if (ticks <= 0) return false;
        lingerUntilBySlot[slot] = ServerTick + ticks;
        return true;
    }

    // The Left for a held slot: the connection is gone, the body stays. Everything the client fed is dropped here,
    // after this drain's earlier Data events for the slot were stored, so input sent before the drop never drives
    // the body. It steps on the neutral command and is no longer served.
    private void BeginLinger(int slot)
    {
        commands.Forget(slot);
        deltaReplicator?.Forget(slot);
        deltaCapableSlots.Remove(slot);
        tickedSlots.Remove(slot);
        replication.Left(slot);
    }

    // Runs right after ServerTick advances: a body granted n ticks after tick k leaves at the start of tick k + n + 1.
    private void ExpireLingers()
    {
        if (lingerUntilBySlot.Count == 0) return;
        lingerScratch.Clear();
        foreach (KeyValuePair<int, long> kv in lingerUntilBySlot)
            if (kv.Value < ServerTick) lingerScratch.Add(kv.Key);
        foreach (int slot in lingerScratch) OnLeave(slot);
    }
}
