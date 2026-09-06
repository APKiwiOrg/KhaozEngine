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
}
