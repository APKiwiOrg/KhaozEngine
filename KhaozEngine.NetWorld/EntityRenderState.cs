using System.Numerics;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>One renderable entity as the client sees it: its net id, world position, whether it is the local
/// (predicted) player, its display name (when replicated), and its grounded flag + vertical velocity + swimming flag.
/// The render-free contract a sample renders a capsule from - and, given a <see cref="DisplayName"/>, a nameplate above.
///
/// <see cref="Grounded"/> + <see cref="VerticalVelocity"/> + <see cref="Swimming"/> are the EXACT movement state,
/// sourced from prediction for the local player and from the replicated <c>MovementState</c> for remotes (it rides
/// the wire alongside position). A replicated-animator bridge should feed them into
/// <c>KhaozEngine.Game.CharacterSample</c> for EVERY entity, not just the local one: a remote's vertical motion is
/// mostly terrain-following, so deriving "airborne" from its position delta misfires (the faster it moves over a
/// slope, the more it looks like falling) - the replicated flags are authoritative and free of that error. Swim in
/// particular is impossible to derive from position at all (a swimming character glides horizontally like a walker),
/// so the replicated <c>MovementState.Swimming</c> bit is the only signal a remote's swim animation can ride.</summary>
public readonly struct EntityRenderState
{
    public EntityRenderState(NetId id, Vector3 position, bool isLocal)
        : this(id, position, isLocal, null, false, 0f, false)
    {
    }

    public EntityRenderState(NetId id, Vector3 position, bool isLocal, string? displayName)
        : this(id, position, isLocal, displayName, false, 0f, false)
    {
    }

    public EntityRenderState(NetId id, Vector3 position, bool isLocal, string? displayName, bool grounded, float verticalVelocity)
        : this(id, position, isLocal, displayName, grounded, verticalVelocity, false)
    {
    }

    public EntityRenderState(NetId id, Vector3 position, bool isLocal, string? displayName, bool grounded, float verticalVelocity, bool swimming)
    {
        Id = id;
        Position = position;
        IsLocal = isLocal;
        DisplayName = displayName;
        Grounded = grounded;
        VerticalVelocity = verticalVelocity;
        Swimming = swimming;
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

    /// <summary>The entity's exact grounded flag this frame (local: predicted; remote: replicated
    /// <c>MovementState</c>). Defaults to grounded when a remote has no replicated movement yet.</summary>
    public bool Grounded { get; }

    /// <summary>The entity's exact vertical velocity (m/s, positive up; local: predicted; remote: replicated
    /// <c>MovementState</c>). 0 when unavailable.</summary>
    public float VerticalVelocity { get; }

    /// <summary>True while the entity is surface-swimming (local: predicted; remote: replicated
    /// <c>MovementState.Swimming</c>). Feed it into <c>KhaozEngine.Game.CharacterSample</c> so the animator plays the
    /// swim/tread clips. Defaults to false (a land character) when a remote has no replicated movement yet.</summary>
    public bool Swimming { get; }
}
