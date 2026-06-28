using System.Numerics;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>One renderable entity as the client sees it: its net id, world position, whether it is the local
/// (predicted) player, and (when replicated) its display name. The render-free contract a sample renders a capsule
/// from - and, given a <see cref="DisplayName"/>, a nameplate above.</summary>
public readonly struct EntityRenderState
{
    public EntityRenderState(NetId id, Vector3 position, bool isLocal)
        : this(id, position, isLocal, null)
    {
    }

    public EntityRenderState(NetId id, Vector3 position, bool isLocal, string? displayName)
    {
        Id = id;
        Position = position;
        IsLocal = isLocal;
        DisplayName = displayName;
    }

    /// <summary>The entity's network identity (stable server/client).</summary>
    public NetId Id { get; }

    /// <summary>World position to render the capsule at.</summary>
    public Vector3 Position { get; }

    /// <summary>True for the local player (predicted + reconciled); false for replicated remotes.</summary>
    public bool IsLocal { get; }

    /// <summary>The replicated display name to render above this entity, or <c>null</c> when the entity carries no
    /// <see cref="PlayerIdentity"/>. A consumer projects the head position and draws this string (see
    /// <c>KhaozEngine.Render3D.WorldLabel</c>).</summary>
    public string? DisplayName { get; }
}
