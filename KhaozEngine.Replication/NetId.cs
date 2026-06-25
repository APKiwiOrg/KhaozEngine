using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// Network identity of a replicated entity: stable across the wire, the same on server and client even though
/// the underlying <see cref="Entity"/> handles differ. The server assigns it; the client keys its
/// <see cref="ClientReplicationView"/> map on it.
/// </summary>
public struct NetId : IComponent
{
    public NetId(int value) { Value = value; }

    /// <summary>The wire-stable id.</summary>
    public int Value;
}
