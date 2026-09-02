namespace KhaozEngine.NetWorld;

// The counters forwarded straight off the inner NetServer, gathered here so the whole session-inbox health surface
// reads in one place instead of trailing the session plumbing in ShardedWorldServer.cs. #267 wired the pending-connection
// pair through. #786 put the drop counter beside it, because a game could see a refused connect but not a dropped
// session event on the same inbox. Split out by concern, as ShardedWorldServer.Pickups.cs is, and because ShardedWorldServer.cs
// sits on the file-size ratchet.
public sealed partial class ShardedWorldServer
{
    /// <summary>Connections accepted but holding no slot yet: connected, Hello not yet answered. A handful at any
    /// instant is a join in flight. A number that climbs and stays there is a flood, or peers that connect and never
    /// say Hello.</summary>
    public int PendingConnectionCount => net.PendingConnectionCount;

    /// <summary>Total connects refused because <see cref="ShardedWorldServerConfig.MaxPendingConnections"/> was
    /// already reached. 0 with no cap configured, and 0 under normal traffic with one.</summary>
    public long RefusedPendingConnectionCount => net.RefusedPendingConnectionCount;

    /// <summary>Total session events dropped because the undrained inbox hit
    /// <see cref="ShardedWorldServerConfig.MaxQueuedEvents"/>. Non-zero means this host is not draining as
    /// contracted (a stall) or a peer is flooding, and under normal operation it stays 0. A LEFT event never counts
    /// here: it is enqueued as terminal, sits outside the cap, and is never what an eviction throws away, because
    /// nothing re-announces a departure and losing one would strand the player slot.</summary>
    public long DroppedEventCount => net.DroppedEventCount;
}
