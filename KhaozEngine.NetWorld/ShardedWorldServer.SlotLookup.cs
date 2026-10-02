using System.Collections.Generic;

namespace KhaozEngine.NetWorld;

public sealed partial class ShardedWorldServer
{
    private readonly Dictionary<long, int> slotByNetId = new();

    /// <summary>Resolves a joined player's net id to its current connection slot. Unknown ids, departed players
    /// and non-player entities do not resolve. Updated alongside <see cref="TryGetPlayerNetId"/> on join and leave,
    /// so a departed id cannot resolve to a recycled slot.</summary>
    /// <remarks>Call on the host thread, like <see cref="TryGetPlayerNetId"/>. Uses a dictionary lookup.</remarks>
    /// <param name="netId">The player entity's net id.</param>
    /// <param name="slot">The player's connection slot, or 0 when the id has no joined player.</param>
    /// <returns>True when the id belongs to a joined player. Check this result because slot 0 is valid.</returns>
    public bool TryGetSlot(long netId, out int slot) => slotByNetId.TryGetValue(netId, out slot);
}
