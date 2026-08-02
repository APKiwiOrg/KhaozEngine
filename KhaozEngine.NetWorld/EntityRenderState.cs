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
/// so the replicated <c>MovementState.Swimming</c> bit is the only signal a remote's swim animation can ride. The same
/// argument applies one field along to <see cref="FacingYaw"/>: a heading derived from a position delta cannot turn a
/// stationary character at all, which is the whole reason facing became authoritative state.</summary>
public readonly struct EntityRenderState
{
    public EntityRenderState(NetId id, Vector3 position, bool isLocal)
        : this(id, position, isLocal, null, false, 0f, false, 0f)
    {
    }

    public EntityRenderState(NetId id, Vector3 position, bool isLocal, string? displayName)
        : this(id, position, isLocal, displayName, false, 0f, false, 0f)
    {
    }

    public EntityRenderState(NetId id, Vector3 position, bool isLocal, string? displayName, bool grounded, float verticalVelocity)
        : this(id, position, isLocal, displayName, grounded, verticalVelocity, false, 0f)
    {
    }

    public EntityRenderState(NetId id, Vector3 position, bool isLocal, string? displayName, bool grounded, float verticalVelocity, bool swimming)
        : this(id, position, isLocal, displayName, grounded, verticalVelocity, swimming, 0f)
    {
    }

    public EntityRenderState(NetId id, Vector3 position, bool isLocal, string? displayName, bool grounded, float verticalVelocity, bool swimming, float climbRate)
        : this(id, position, isLocal, displayName, grounded, verticalVelocity, swimming, climbRate, 0f)
    {
    }

    public EntityRenderState(NetId id, Vector3 position, bool isLocal, string? displayName, bool grounded, float verticalVelocity, bool swimming, float climbRate, float stepCumulativeY)
        : this(id, position, isLocal, displayName, grounded, verticalVelocity, swimming, climbRate, stepCumulativeY, 0f)
    {
    }

    public EntityRenderState(NetId id, Vector3 position, bool isLocal, string? displayName, bool grounded, float verticalVelocity, bool swimming, float climbRate, float stepCumulativeY, float landingImpactSpeed)
        : this(id, position, isLocal, displayName, grounded, verticalVelocity, swimming, climbRate, stepCumulativeY, landingImpactSpeed, 0f)
    {
    }

    public EntityRenderState(NetId id, Vector3 position, bool isLocal, string? displayName, bool grounded, float verticalVelocity, bool swimming, float climbRate, float stepCumulativeY, float landingImpactSpeed, float facingYaw)
    {
        Id = id;
        Position = position;
        IsLocal = isLocal;
        DisplayName = displayName;
        Grounded = grounded;
        VerticalVelocity = verticalVelocity;
        Swimming = swimming;
        ClimbRate = climbRate;
        StepCumulativeY = stepCumulativeY;
        LandingImpactSpeed = landingImpactSpeed;
        FacingYaw = facingYaw;
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

    /// <summary>The entity's signed step-climb rate this frame (m/s; +ascending, -descending, 0 not on a step climb;
    /// local: predicted <c>MoveState.ClimbRate</c>; remote: the decoded replicated <c>MovementState.ClimbRateQ</c>,
    /// nearest-sampled to the same delayed render time as the interpolated position). Feed it into
    /// <c>KhaozEngine.Game.CharacterSample.ClimbRate</c> so the presentation smoother glides the drawn feet up/down the
    /// stair slope from the sim's own fact instead of estimating climb state from a position delta. 0 (the default when
    /// a remote has no replicated movement yet) reads as not-climbing, so the smoother renders raw.</summary>
    public float ClimbRate { get; }

    /// <summary>The LOCAL player's client-local, session-monotonic running sum of DISCRETE-STEP vertical impulses (from
    /// <c>ClientPrediction.StepCumulativeY</c>): a mesh smoother DIFFS it across frames to ease an isolated step-up/step-down
    /// the continuous glide (<see cref="ClimbRate"/>) renders raw. Feed it into
    /// <c>KhaozEngine.Game.CharacterSample.StepCumulativeY</c>. Always 0 for REMOTES (the discrete-step impulse rides no
    /// wire - a remote's single step is softened by its existing 2-tick position interpolation), so a remote accumulates no
    /// mesh offset. 0 on a position-only sample too.</summary>
    public float StepCumulativeY { get; }

    /// <summary>The LOCAL player's PREDICTED landing impact this frame (m/s, non-negative, from
    /// <c>MoveState.LandingImpactSpeed</c>): the downward speed the predicted landing erased on the tick it landed, and 0
    /// on every other tick. It lets client presentation react on the PREDICTED landing tick - a land effect, a camera
    /// dip, an impact sound scaled by severity - instead of a round trip later. It holds for the frames of that one
    /// predicted tick and returns to 0 on the next one, so a consumer that must fire exactly once should edge-detect it
    /// rather than sample it. Authoritative damage stays server-side (<c>WorldServer.OnAfterTick</c> /
    /// <c>ShardedWorldServer.OnAfterTick</c>): this is presentation, and it is predicted, so a correction can retract it.
    /// <para>Always 0 for REMOTES: the latch is deliberately absent from the movement codec, so nothing a remote receives
    /// carries it. A consumer that wants remote landing effects derives them from the replicated <see cref="Grounded"/>
    /// transition (false -&gt; true) it already receives, with <see cref="VerticalVelocity"/> for the severity.</para>
    /// 0 on a position-only sample too.</summary>
    public float LandingImpactSpeed { get; }

    /// <summary>The entity's AUTHORITATIVE heading this frame (radians, in <c>MoveCommand.CameraYaw</c>'s convention:
    /// 0 faces world -Z, positive swings toward -X). Local: the PREDICTED <c>MoveState.FacingYaw</c>, un-quantized, so
    /// a turn appears on the frame the input was sent rather than a round trip later. Remote: the decoded replicated
    /// <c>MovementState.FacingYawQ</c>, discrete-sampled to the same delayed render time as the interpolated position
    /// (as <see cref="Grounded"/> and <see cref="VerticalVelocity"/> are), so a remote's flags, heading and feet never
    /// skew apart. Feed it into a model's yaw instead of deriving facing from a position delta: that derivation cannot
    /// turn a STATIONARY character at all, and it reads a fast diagonal or a slope walk as a turn that never happened.
    /// <para>Remotes receive it DISCRETE-SAMPLED, one value per snapshot, so a fast turn arrives as a few steps rather
    /// than a sweep. Presentation may smooth it, and an exponential ease toward the sampled value is the usual choice.
    /// The sim-side <c>MoveTuning.FacingTurnSpeed</c> is the authoritative rate limit and this is the signal it
    /// produces, so a smoother here should ease toward the value and never invent a heading of its own.</para>
    /// 0 (the default when a remote has no replicated movement yet, and on a position-only sample) is a legal heading,
    /// facing -Z, rather than a sentinel.</summary>
    public float FacingYaw { get; }
}
