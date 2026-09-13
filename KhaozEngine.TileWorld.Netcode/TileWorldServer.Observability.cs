namespace KhaozEngine.TileWorld.Netcode;

public sealed partial class TileWorldServer
{
    /// <summary>Connections accepted but holding no slot yet: connected, Hello not yet answered. A handful at any
    /// instant is a join in flight. A number that climbs and stays there is a flood, or peers that connect and never
    /// say Hello.</summary>
    public int PendingConnectionCount => net.PendingConnectionCount;

    /// <summary>Total connects refused because <see cref="TileWorldServerConfig.MaxPendingConnections"/> was already
    /// reached. 0 with no cap configured, and 0 under normal traffic with one.</summary>
    public long RefusedPendingConnectionCount => net.RefusedPendingConnectionCount;

    /// <summary>How many of <paramref name="slot"/>'s commands are buffered and not yet applied, 0 for a slot this
    /// server does not hold. The input backlog: a healthy one-per-tick sender sits at 0 or 1, and a value that climbs
    /// and stays there is that player's every click applied that many ticks late, which a stalled client that bursts
    /// its missed ticks used to cause for the rest of a session (the client now sends one command per stalled frame
    /// instead, see <c>TileWorldClient.Tick</c>). The diagnostics reading to put beside a client's
    /// <c>TileWorldClient.PendingCommandCount</c>.</summary>
    /// <param name="slot">The player's connection slot.</param>
    public int InputDepth(int slot) => commands.Depth(slot);
}
