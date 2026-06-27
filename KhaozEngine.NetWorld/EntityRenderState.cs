using System.Numerics;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>One renderable entity as the client sees it: its net id, world position, and whether it is the
/// local (predicted) player. The render-free contract a sample renders a capsule from.</summary>
public readonly struct EntityRenderState
{
    public EntityRenderState(NetId id, Vector3 position, bool isLocal)
    {
        Id = id;
        Position = position;
        IsLocal = isLocal;
    }

    /// <summary>The entity's network identity (stable server/client).</summary>
    public NetId Id { get; }

    /// <summary>World position to render the capsule at.</summary>
    public Vector3 Position { get; }

    /// <summary>True for the local player (predicted + reconciled); false for replicated remotes.</summary>
    public bool IsLocal { get; }
}
