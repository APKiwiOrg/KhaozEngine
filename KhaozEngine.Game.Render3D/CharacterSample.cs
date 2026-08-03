using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Game
{
    /// <summary>One visible entity's movement sample for <see cref="ReplicatedCharacterAnimators"/>, one per frame.
    /// Deliberately engine-neutral (no netcode type): a networked game maps its per-entity render state
    /// (e.g. <c>KhaozEngine.NetWorld.EntityRenderState</c>) to this in a tiny loop, so the bridge stays usable by any
    /// game and the <c>Game.Render3D</c> package keeps its layering (no dependency on a netcode package).
    ///
    /// The only universally-available signal is <see cref="Position"/> over time, so by default the bridge DERIVES
    /// planar speed, vertical velocity, and facing from successive samples (averaged over a short window - see
    /// <see cref="CharacterAnimatorTuning.VelocityWindowSeconds"/> - so a plateauing position stream does not strobe
    /// the state). For the local player (whose exact movement the client already knows) pass the exact-movement
    /// constructor so <see cref="HasMovement"/> is set and the grounded flag + vertical velocity are taken verbatim
    /// instead of derived; the fullest constructor additionally takes the exact planar speed
    /// (<see cref="CharacterSample.PlanarSpeed"/>) so the locomotion state is driven by the clean commanded speed, not
    /// finite-differenced from the render position (no walk&lt;-&gt;idle flicker on a decel-to-stop).
    ///
    /// Facing is derived from the position delta by default, but a sample may carry an EXPLICIT server-authoritative
    /// facing yaw (<see cref="FacingYaw"/>, set via the position+facing constructor or <see cref="WithFacingYaw"/>).
    /// When present it overrides the derived heading and turns the character in place even while stationary - for a
    /// server-owned NPC tracking a target at melee range, a turret, a mount, or a player standing still and turning,
    /// whose facing the server knows every tick but a position delta cannot reveal at rest.</summary>
    public readonly struct CharacterSample
    {
        // Full-field constructor (private) backing WithFacingYaw / WithDowned / WithSector: copies every field and
        // overrides one. Keeping it private avoids a public 14-arg overload; the public constructors below stay the
        // documented surface.
        CharacterSample(long id, Vector3 position, bool isLocal, bool hasMovement, bool grounded, float verticalVelocity,
            bool swimming, float climbRate, bool hasPlanarSpeed, float planarSpeed, float? facingYaw, float stepCumulativeY,
            bool downed, MoveSector sector)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = hasMovement;
            Grounded = grounded;
            VerticalVelocity = verticalVelocity;
            Swimming = swimming;
            ClimbRate = climbRate;
            HasPlanarSpeed = hasPlanarSpeed;
            PlanarSpeed = planarSpeed;
            FacingYaw = facingYaw;
            StepCumulativeY = stepCumulativeY;
            Downed = downed;
            Sector = sector;
        }

        /// <summary>Position-only sample: speed, vertical velocity, grounded, and swimming are all derived from the
        /// position delta vs the previous frame (swimming cannot actually be derived from position, so it reads false -
        /// pass an exact-movement constructor to animate swimming).</summary>
        public CharacterSample(long id, Vector3 position, bool isLocal = false)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = false;
            Grounded = false;
            VerticalVelocity = 0f;
            Swimming = false;
            ClimbRate = 0f;
            HasPlanarSpeed = false;
            PlanarSpeed = 0f;
            FacingYaw = null;
            StepCumulativeY = 0f;
            Downed = false;
            Sector = MoveSector.Forward;
        }

        /// <summary>Sample with exact movement (the local player, or any entity whose replicated <c>MovementState</c>
        /// is available): <see cref="Grounded"/>, <see cref="VerticalVelocity"/>, <see cref="Swimming"/>, and
        /// <see cref="ClimbRate"/> are used as given instead of being derived. <paramref name="swimming"/> and
        /// <paramref name="climbRate"/> default to the non-swimming / not-climbing values so a pre-swim / pre-glide
        /// caller is unchanged.</summary>
        public CharacterSample(long id, Vector3 position, bool isLocal, bool grounded, float verticalVelocity, bool swimming = false, float climbRate = 0f, float stepCumulativeY = 0f)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = true;
            Grounded = grounded;
            VerticalVelocity = verticalVelocity;
            Swimming = swimming;
            ClimbRate = climbRate;
            HasPlanarSpeed = false;
            PlanarSpeed = 0f;
            FacingYaw = null;
            StepCumulativeY = stepCumulativeY;
            Downed = false;
            Sector = MoveSector.Forward;
        }

        /// <summary>Fullest sample (the local player): exact <see cref="Grounded"/>, <see cref="VerticalVelocity"/>,
        /// AND exact planar <paramref name="planarSpeed"/> (m/s). The planar speed drives the locomotion state and the
        /// clip-speed sync DIRECTLY instead of being finite-differenced from the rendered position. Pass the clean
        /// commanded speed (<c>WorldClient.LocalHorizontalSpeed</c> / <c>ClientPrediction.PredictedHorizontalSpeed</c>):
        /// it is computed only on the prediction's commanded path, so it does not carry the reconciliation render offset
        /// and does not strobe walk&lt;-&gt;idle when the player decelerates to a stop (where the rendered position, even
        /// after the C1 smoothing fix, settles with a tiny residual sag). Facing still follows the derived heading
        /// (planar speed is magnitude-only). A negative value is treated as zero. <paramref name="stepCumulativeY"/> is the
        /// local player's discrete-step accumulator (<c>ClientPrediction.StepCumulativeY</c> via
        /// <c>EntityRenderState.StepCumulativeY</c>); the bridge diffs it to ease an isolated step the continuous glide
        /// renders raw. Defaults to 0 (no mesh offset), so a caller that never supplies it is unchanged.</summary>
        public CharacterSample(long id, Vector3 position, bool isLocal, bool grounded, float verticalVelocity, float planarSpeed, bool swimming = false, float climbRate = 0f, float stepCumulativeY = 0f)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = true;
            Grounded = grounded;
            VerticalVelocity = verticalVelocity;
            Swimming = swimming;
            ClimbRate = climbRate;
            HasPlanarSpeed = true;
            PlanarSpeed = planarSpeed;
            FacingYaw = null;
            StepCumulativeY = stepCumulativeY;
            Downed = false;
            Sector = MoveSector.Forward;
        }

        /// <summary>Position + EXPLICIT facing sample: the position drives the derived planar speed / air state as the
        /// position-only constructor does, but the facing yaw is taken as authoritative (see <see cref="FacingYaw"/>)
        /// instead of derived from the heading. The simplest way to face a purely position-streamed entity where the
        /// server owns the facing (a turret, an NPC standing still at melee range). <paramref name="facingYaw"/> is a
        /// world yaw in radians about +Y (0 faces +Z; <see cref="CharacterAnimatorTuning.FacingYawOffset"/> still
        /// composes). To attach explicit facing to an EXACT-movement sample (grounded / vertical / planar speed /
        /// swimming) instead, build that sample and call <see cref="WithFacingYaw"/>.</summary>
        public CharacterSample(long id, Vector3 position, float facingYaw, bool isLocal = false)
        {
            Id = id;
            Position = position;
            IsLocal = isLocal;
            HasMovement = false;
            Grounded = false;
            VerticalVelocity = 0f;
            Swimming = false;
            ClimbRate = 0f;
            HasPlanarSpeed = false;
            PlanarSpeed = 0f;
            FacingYaw = facingYaw;
            StepCumulativeY = 0f;
            Downed = false;
            Sector = MoveSector.Forward;
        }

        /// <summary>Stable per-entity key (e.g. <c>NetId.Value</c>, 64-bit since 10.0.0). Identifies the brain across frames.</summary>
        public long Id { get; }

        /// <summary>World position this frame (the only signal every netcode surfaces for every entity).</summary>
        public Vector3 Position { get; }

        /// <summary>True for the local (predicted) player, false for replicated remotes. Forwarded to the pose for
        /// the consumer (e.g. tinting / debug), never changes the brain's behaviour.</summary>
        public bool IsLocal { get; }

        /// <summary>True when this sample carries exact <see cref="Grounded"/> + <see cref="VerticalVelocity"/>
        /// (use them instead of deriving).</summary>
        public bool HasMovement { get; }

        /// <summary>Exact grounded flag (only meaningful when <see cref="HasMovement"/>).</summary>
        public bool Grounded { get; }

        /// <summary>Exact vertical velocity, m/s positive up (only meaningful when <see cref="HasMovement"/>).</summary>
        public float VerticalVelocity { get; }

        /// <summary>Exact surface-swimming flag (only meaningful when <see cref="HasMovement"/>). When set, the brain
        /// plays the tread <see cref="LocomotionState.SwimIdle"/> or forward <see cref="LocomotionState.Swim"/> clip
        /// (from the planar speed) instead of a ground/air state - swim wins over grounded/vertical. Sourced from the
        /// replicated <c>MovementState.Swimming</c> bit (via <c>EntityRenderState.Swimming</c>), never derived from
        /// position (a swimmer glides horizontally like a walker, so position cannot distinguish the two).</summary>
        public bool Swimming { get; }

        /// <summary>Exact signed step-climb rate in m/s (only meaningful when <see cref="HasMovement"/>): +ascending a
        /// paced stair run, -descending stepped risers, 0 not on a step climb. The presentation smoother in
        /// <see cref="ReplicatedCharacterAnimators.Update"/> glides the drawn feet up/down the stair slope iff this is
        /// non-zero, feeding it forward directly (never estimating climb from a position delta). Sourced from the sim's
        /// own <c>MoveState.ClimbRate</c> (local: predicted; remote: the decoded, nearest-sampled replicated
        /// <c>MovementState.ClimbRateQ</c>, via <c>EntityRenderState.ClimbRate</c>). 0 on every position-only sample.</summary>
        public float ClimbRate { get; }

        /// <summary>True when this sample carries an exact planar <see cref="PlanarSpeed"/> to use for the locomotion
        /// state instead of deriving it from the position delta.</summary>
        public bool HasPlanarSpeed { get; }

        /// <summary>Exact planar (ground-plane) speed, m/s (only meaningful when <see cref="HasPlanarSpeed"/>). Drives
        /// the idle/walk/run state and the clip-speed sync; facing uses the derived heading UNLESS <see cref="FacingYaw"/>
        /// is supplied.</summary>
        public float PlanarSpeed { get; }

        /// <summary>Optional EXPLICIT facing yaw (world radians about +Y, 0 faces +Z), or null to derive facing from the
        /// position delta as before. When set, <see cref="ReplicatedCharacterAnimators.Update"/> aims the character at
        /// this yaw plus <see cref="CharacterAnimatorTuning.FacingYawOffset"/> through the existing
        /// <see cref="CharacterAnimatorTuning.YawSmoothing"/> smoothing, so it turns in place even while stationary and
        /// WINS over the position-derived heading even while moving (server authority over derivation). Null on every
        /// existing constructor, so a caller that never supplies it behaves exactly as before. Sourced from whatever
        /// yaw the game replicates for the entity (e.g. a per-entity facing component on a server-owned NPC).</summary>
        public float? FacingYaw { get; }

        /// <summary>The local player's discrete-step accumulator (only meaningful when <see cref="HasMovement"/> and
        /// <see cref="IsLocal"/>): the session-monotonic running sum of committed isolated-step vertical impulses (from
        /// <c>ClientPrediction.StepCumulativeY</c> via <c>EntityRenderState.StepCumulativeY</c>). The bridge DIFFS it
        /// frame-to-frame to pick up each new isolated step-up/step-down EXACTLY ONCE and ease it with a render-time-
        /// decaying MESH offset (<see cref="CharacterAnimatorTuning.StepSmoothingRate"/>), softening the mini-teleport pop
        /// the continuous glide (which renders such singles raw) leaves. 0 on remotes (their singles are softened by
        /// position interpolation) and on every position-only sample, so no offset accumulates there.</summary>
        public float StepCumulativeY { get; }

        /// <summary>True when the game considers this entity DOWNED (dead / knocked out) this frame, so the bridge shows
        /// a downed pose instead of locomotion. The engine knows NOTHING about HP or death rules: a networked game
        /// DERIVES this client-side from state it already replicates (e.g. <c>hp &lt;= 0</c> off a replicated Hp
        /// component) with no wire change and sets it per frame. While set, <see cref="ReplicatedCharacterAnimators"/>
        /// suppresses locomotion (idle/walk/run, air, swim, and stacked action one-shots) for this entity and either
        /// plays the entity's baked <see cref="LocomotionState.Downed"/> clip once holding its final frame, or - with no
        /// such clip - collapses the body procedurally to prone over
        /// <see cref="CharacterAnimatorTuning.DownedCollapseSeconds"/> and settles it at ground level. Clearing it
        /// (respawn) returns the entity to normal locomotion. A respawn usually teleports, so pair the clear with
        /// <see cref="ReplicatedCharacterAnimators.SnapRenderHeight"/> for a crisp cut (no glide from the corpse
        /// position, no prone residual). Defaults false on every constructor, so a sample never marked downed renders
        /// exactly as before. Set it orthogonally on ANY sample shape via <see cref="WithDowned"/>.</summary>
        public bool Downed { get; }

        /// <summary>Returns a copy of this sample carrying an explicit <paramref name="facingYaw"/> (world radians about
        /// +Y, 0 faces +Z), preserving every other field. The orthogonal way to add server-authoritative facing to ANY
        /// sample shape - position-only, exact-movement, or the fullest exact-speed sample - since facing is independent
        /// of the movement/speed/swim data. <see cref="CharacterAnimatorTuning.FacingYawOffset"/> still composes on top.</summary>
        public CharacterSample WithFacingYaw(float facingYaw) =>
            new CharacterSample(Id, Position, IsLocal, HasMovement, Grounded, VerticalVelocity, Swimming, ClimbRate, HasPlanarSpeed, PlanarSpeed, facingYaw, StepCumulativeY, Downed, Sector);

        /// <summary>Returns a copy of this sample with <see cref="Downed"/> set to <paramref name="downed"/>, preserving
        /// every other field. The orthogonal way to mark ANY sample shape downed - position-only, exact-movement, the
        /// fullest exact-speed sample, or one already carrying an explicit facing - since the downed flag is independent
        /// of the movement/speed/swim/facing data. Mirrors <see cref="WithFacingYaw"/>. A networked game derives the
        /// argument from its own replicated state (e.g. <c>e.Hp &lt;= 0</c>) in the same per-frame loop that builds the
        /// sample.</summary>
        public CharacterSample WithDowned(bool downed) =>
            new CharacterSample(Id, Position, IsLocal, HasMovement, Grounded, VerticalVelocity, Swimming, ClimbRate, HasPlanarSpeed, PlanarSpeed, FacingYaw, StepCumulativeY, downed, Sector);

        /// <summary>Which directional sector this frame's movement falls in RELATIVE TO THE FACING the character is
        /// holding, or <see cref="MoveSector.Forward"/> (the default on every constructor) when the game does not
        /// classify it. Only <see cref="MoveSector.Reverse"/> currently changes anything, and only for a consumer that
        /// opted in via <see cref="CharacterAnimatorTuning.ReverseLocomotionOnReverseSector"/>: the move clip then plays
        /// BACKWARDS at the speed-matched rate, so a backpedal strides backwards instead of moonwalking. The locomotion
        /// STATE is picked from the speed magnitude regardless, so a reverse walk is still the walk clip.
        ///
        /// <para>The sector is the game's to derive, because only the game knows what the character is facing. A game
        /// running the engine's own movement already has it: <c>CharacterMovement.Sector</c> answers it from the
        /// camera-relative move command (the same predicate the sim charges
        /// <c>MoveTuning.BackpedalSpeedScale</c> with, which is why this is the engine's sector type rather than a
        /// second bool that would drift from it). For a REPLICATED remote, whose command axis never crosses the wire,
        /// classify the render-position delta against the replicated facing instead - the same 135 degree wedge,
        /// measured against <c>EntityRenderState.FacingYaw</c>. A character that turns to face wherever it walks is
        /// <see cref="MoveSector.Forward"/> by construction and never needs this.</para></summary>
        public MoveSector Sector { get; }

        /// <summary>Returns a copy of this sample with <see cref="Sector"/> set to <paramref name="sector"/>, preserving
        /// every other field. The orthogonal way to classify ANY sample shape - position-only, exact-movement, the
        /// fullest exact-speed sample, or one already carrying an explicit facing - since the sector is independent of
        /// the movement/speed/swim/facing data. Mirrors <see cref="WithFacingYaw"/> and <see cref="WithDowned"/>.</summary>
        public CharacterSample WithSector(MoveSector sector) =>
            new CharacterSample(Id, Position, IsLocal, HasMovement, Grounded, VerticalVelocity, Swimming, ClimbRate, HasPlanarSpeed, PlanarSpeed, FacingYaw, StepCumulativeY, Downed, sector);
    }
}
