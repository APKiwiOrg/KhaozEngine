using System;
using System.Numerics;
using KhaozEngine.Ecs;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The replicated vertical movement state of a player entity: the part of <see cref="KhaozEngine.Locomotion.MoveState"/>
/// beyond the <see cref="ReplicatedPosition"/> (which carries the 3D position). It rides alongside
/// <see cref="ReplicatedPosition"/> so the vertical axis survives a sharded cell handoff (handoff transfers
/// registered components) and reaches the client, where it forms the authoritative reconciliation basis. Registered
/// in <see cref="MoveProtocol.CreateRegistry"/> as type id <see cref="MoveProtocol.MovementTypeId"/>; not
/// interpolated (remotes render from <see cref="ReplicatedPosition"/>; only the local owner uses this, exactly).
/// </summary>
public struct MovementState : IComponent
{
    /// <summary>Vertical velocity (m/s, positive up).</summary>
    public float VerticalVelocity;

    /// <summary>True while resting on the ground this tick.</summary>
    public bool Grounded;

    /// <summary>Seconds since last grounded (coyote-time accounting).</summary>
    public float TimeSinceGrounded;

    /// <summary>Seconds of jump-buffer remaining (jump-buffer accounting).</summary>
    public float JumpBufferRemaining;

    /// <summary>True while the player is surface-swimming (mirrors <see cref="KhaozEngine.Locomotion.MoveState.Swimming"/>).
    /// Replicated alongside the vertical axis so the local owner reconciles it AND remote clients read it to drive the
    /// swim animation state (Task 3 derives the remote swim pose from this bit rather than re-querying water). Added on
    /// the wire in generation 3 (<see cref="MoveProtocol.WireProtocolVersion"/>); a mismatched peer is rejected at
    /// connect by the always-on <see cref="WireGenerationAuthenticator"/>.</summary>
    public bool Swimming;

    /// <summary>The authoritative teleport epoch (see <see cref="PlayerMoveState.TeleportEpoch"/>): a monotonic
    /// counter the server bumps only at teleport sites, replicated to the local owner alongside the vertical axis so
    /// its prediction cuts on an advance. Added on the wire in generation 4
    /// (<see cref="MoveProtocol.WireProtocolVersion"/>); a mismatched peer is rejected at connect by the always-on
    /// <see cref="WireGenerationAuthenticator"/>.</summary>
    public uint TeleportEpoch;

    /// <summary>The signed step-climb rate (<see cref="KhaozEngine.Locomotion.MoveState.ClimbRate"/>) quantized to a
    /// single byte at the FIXED wire scale <see cref="ClimbRateQuantum"/> (0.05 m/s per unit, range +/-6.35 m/s), so
    /// the climb signal reaches remote observers, not just the local owner. Decoded rate =
    /// <c>ClimbRateQ * ClimbRateQuantum</c>. <b>0 means "not climbing" - the climb FLAG is implicit in the rate</b>: a
    /// sub-0.05 m/s climb quantizes to 0 (its per-frame bob is sub-millimetre, below perception - the honest dead-zone).
    /// The fixed scale is deliberately NOT <see cref="KhaozEngine.Locomotion.MoveTuning.MaxStepClimbSpeed"/> (per-consumer tuning the codec
    /// cannot see), which keeps the codec consumer-agnostic. Added on the wire in generation 5
    /// (<see cref="MoveProtocol.WireProtocolVersion"/>); a mismatched peer is rejected at connect by the always-on
    /// <see cref="WireGenerationAuthenticator"/>.</summary>
    public sbyte ClimbRateQ;

    /// <summary>The per-entity horizontal speed multiplier (<see cref="KhaozEngine.Locomotion.MoveState.SpeedScale"/>:
    /// haste, slow, root) quantized to a single byte as the OFFSET FROM 1 at the fixed wire scale
    /// <see cref="SpeedScaleQuantum"/>. Decoded scale = <c>1 + SpeedScaleQ * SpeedScaleQuantum</c>, so <b>0 - the
    /// <c>default</c> every unboosted player carries every tick - is exactly 1.0</b>, with no rounding. That offset
    /// encoding is the point: a plain <c>q * quantum</c> like <see cref="ClimbRateQ"/> would make an absent or
    /// default-constructed component decode to a speed of 0, freezing the player.
    /// <para>Server-authored ONLY: it is set through <c>ShardedWorldServer.SetSpeedScale</c> /
    /// <c>WorldServer.SetSpeedScale</c> and never derived from anything a client sends, so a hostile client cannot
    /// grant itself a multiplier. It must ride the wire (rather than living only on the sim-local
    /// <see cref="KhaozEngine.Locomotion.MoveState"/>) because <see cref="PlayerMoveState.From"/> rebuilds the client's
    /// reconcile basis from the replicated components alone: a scale absent from here would reset on every correction
    /// and the pending command window would replay at the wrong speed.</para>
    /// Added on the wire in generation 6 (<see cref="MoveProtocol.WireProtocolVersion"/>). A mismatched peer is
    /// rejected at connect by the always-on <see cref="WireGenerationAuthenticator"/>.</summary>
    public sbyte SpeedScaleQ;

    /// <summary>The world-X component of the CARRIED airborne horizontal velocity
    /// (<see cref="KhaozEngine.Locomotion.MoveState.HorizontalVelocity"/>, m/s) quantized to a <see cref="short"/> at
    /// the fixed wire scale <see cref="HorizontalVelocityQuantum"/>. Decoded velocity =
    /// <c>q * HorizontalVelocityQuantum</c>, so <b>0 - the <c>default</c> a pre-momentum state and every grounded
    /// player carries - is exactly 0 m/s</b>. A plain scaled encoding is right here where
    /// <see cref="SpeedScaleQ"/> needed an offset-from-1 one, and for the same underlying reason: the zero default has
    /// to mean the harmless thing. For a multiplier that is 1 (unmodified), and for a carried velocity it is 0
    /// (carrying nothing), which degrades a state that never went through a step to the old instant-to-target model
    /// rather than to a phantom drift.
    /// <para>It must ride the wire because <see cref="PlayerMoveState.From"/> rebuilds the client's reconcile basis
    /// from the replicated components ALONE. A carried velocity absent from there does not merely lag: it resets to
    /// the struct default on EVERY correction, so a client corrected mid-flight drops its arc to zero, rebuilds a new
    /// one from whatever the command happens to be, and replays the whole pending window on that. This is exactly the
    /// failure <see cref="SpeedScaleQ"/> was added to fix, one field along.</para>
    /// Added on the wire in generation 7 (<see cref="MoveProtocol.WireProtocolVersion"/>). A mismatched peer is
    /// rejected at connect by the always-on <see cref="WireGenerationAuthenticator"/>.</summary>
    public short HorizontalVelocityXQ;

    /// <summary>The world-Z component of the carried airborne horizontal velocity (m/s), encoded exactly as
    /// <see cref="HorizontalVelocityXQ"/> is and added on the wire in the same generation 7. The two axes are
    /// quantized INDEPENDENTLY rather than as a packed direction plus magnitude, so the decoded SPEED can sit up to
    /// about 0.003 m/s off the encoded one (each axis rounds on its own). That is well inside the drift budget the
    /// quantum was chosen for, and it keeps the codec a pair of plain scalars with nothing to get wrong at the
    /// boundaries: no normalisation to renormalise, and no direction to lose when the magnitude is zero.</summary>
    public short HorizontalVelocityZQ;

    /// <summary>SIM-LOCAL ascent-EWMA storage (NOT replicated, NOT migrated): the sharded head's per-entity, tick-to-tick
    /// slot for <see cref="KhaozEngine.Locomotion.MoveState.ClimbRateEwma"/> (the exponentially-weighted moving average of
    /// the actually-applied per-tick rise over a paced stair run). It exists ONLY because the sharded per-cell step
    /// (<see cref="PlayerMovementSystem"/>) reconstructs a fresh <see cref="KhaozEngine.Locomotion.MoveState"/> from this
    /// component every tick (unlike the single-<see cref="World"/> <see cref="WorldServer"/>, which keeps the full
    /// <see cref="PlayerMoveState"/> per slot), so without a persistence slot the ascent EWMA would restart from 0 every
    /// tick and the exported <see cref="ClimbRateQ"/> would never converge to the achieved rise. It is DELIBERATELY absent
    /// from the movement codec (see <see cref="MoveProtocol.CreateRegistry"/>): it rides no wire (both heads derive it
    /// deterministically from the identical <see cref="KhaozEngine.Locomotion.CharacterMovement"/> step, so replicating it
    /// is redundant and would only widen the built-in wire) and is not part of the
    /// <see cref="KhaozEngine.Replication.ReplicationChannels.Migrate"/> capture, so it
    /// RESETS to 0 across a shard handoff (the entity is reconstructed via the codec, not copied wholesale) - an acceptable
    /// few-tick re-converge transient at a cell crossing, since the wire <see cref="ClimbRateQ"/> itself survives the
    /// crossing. Set at spawn/teleport to 0 via <see cref="From"/> (a fresh climb state). <c>default</c> is 0
    /// (byte-identical to a pre-feature state). The single-<see cref="World"/> head never reads this field.</summary>
    public float ClimbRateEwma;

    /// <summary>SIM-LOCAL commanded-velocity storage (NOT replicated, NOT migrated): the sharded head's per-entity slot
    /// for <see cref="KhaozEngine.Locomotion.MoveState.CommandedVelocity"/>, the unconstrained horizontal velocity the
    /// step asked for this tick. It exists for the same reason <see cref="ClimbRateEwma"/> does - the sharded per-cell step
    /// (<see cref="PlayerMovementSystem"/>) reconstructs a fresh <see cref="KhaozEngine.Locomotion.MoveState"/> from
    /// this component every tick, so a step OUTPUT has nowhere else to survive to the end of
    /// <see cref="ShardedWorldServer.Tick"/>, where the movement-anomaly check reads it back through
    /// <see cref="PlayerMoveState.From"/>. The single-<see cref="World"/> <see cref="WorldServer"/> never reads this
    /// field: it holds the whole <see cref="PlayerMoveState"/> per slot and reads the step's own output directly.
    /// <para>DELIBERATELY absent from the movement codec (see <see cref="MoveProtocol.CreateRegistry"/>): it is a
    /// server-side anti-cheat input that no client has any use for, so replicating it would widen the built-in wire
    /// for nothing. It is therefore always <c>(0,0)</c> on a client, and <c>(0,0)</c> across a shard handoff - which is
    /// the SAFE direction, since zero reads as "commanded nothing" and so as no denial, never as a spurious one.</para>
    /// <see cref="PlayerMovementSystem"/> writes it every tick, and explicitly zeroes it for an entity its cell sim
    /// skipped (a <see cref="KhaozEngine.Sharding.Ghost"/> or a <see cref="KhaozEngine.Sharding.Migrating"/> entity),
    /// so a skipped tick cannot leave a stale
    /// velocity behind for the anomaly check to measure a motionless entity against.</summary>
    public Vector2 CommandedVelocity;

    /// <summary>Fixed wire scale for <see cref="ClimbRateQ"/> (m/s per quantum unit): 0.05, giving +/-6.35 m/s over an
    /// <see cref="sbyte"/> at 0.05 m/s resolution. Consumer-agnostic (independent of any consumer's
    /// <see cref="KhaozEngine.Locomotion.MoveTuning.MaxStepClimbSpeed"/>), so the codec round-trips the same for every game.</summary>
    public const float ClimbRateQuantum = 0.05f;

    /// <summary>Quantizes a signed climb rate (m/s) to the wire <see cref="sbyte"/>: rounded to the nearest
    /// <see cref="ClimbRateQuantum"/> and clamped to the symmetric +/-127 range (leaving -128 unused). A sub-quantum
    /// rate rounds to 0 (the implicit not-climbing dead-zone).</summary>
    public static sbyte QuantizeClimbRate(float rate) =>
        (sbyte)Math.Clamp((int)MathF.Round(rate / ClimbRateQuantum), -127, 127);

    /// <summary>Decodes a wire <see cref="ClimbRateQ"/> back to a signed climb rate (m/s): <c>q * ClimbRateQuantum</c>.
    /// 0 decodes to exactly 0 (not climbing).</summary>
    public static float DecodeClimbRate(sbyte q) => q * ClimbRateQuantum;

    /// <summary>Fixed wire scale for <see cref="SpeedScaleQ"/> (multiplier per quantum unit): 1/16. Deliberately an
    /// exact NEGATIVE POWER OF TWO, not the 0.05-style decimal <see cref="ClimbRateQuantum"/> uses, because unlike a
    /// climb rate this value multiplies the position delta on BOTH heads every tick: an inexact quantum makes
    /// <c>1 + q*quantum</c> land a hair off the round number for the sim and for a root
    /// (<c>1 - 20*0.05f</c> is <c>-1.5e-8</c>, a slow reverse crawl, not a stop). At 1/16 every representable scale is
    /// an exact float, so 1.0, 0.0, 0.5, 0.75, 1.5, 2, 5 all land dead on and the server and client agree bit-exactly
    /// by construction. The cost is 6.25% granularity: a requested 1.1x resolves to 1.125x.</summary>
    public const float SpeedScaleQuantum = 1f / 16f;

    /// <summary>The largest replicable speed multiplier (8x), the clamp <see cref="QuantizeSpeedScale"/> applies. Far
    /// beyond any sane movement buff and comfortably inside the <see cref="sbyte"/> range, so the encoding keeps
    /// headroom rather than running to its edge. A server-only NPC is not bound by this (it rides no wire), only what
    /// replicates is.</summary>
    public const float MaxSpeedScale = 8f;

    /// <summary>Quantizes a speed multiplier to the wire <see cref="sbyte"/>: clamped to <c>[0, MaxSpeedScale]</c>,
    /// expressed as the offset from 1, and rounded to the nearest <see cref="SpeedScaleQuantum"/>. A scale of exactly
    /// 1 yields exactly 0 (the unmodified default).</summary>
    public static sbyte QuantizeSpeedScale(float scale)
    {
        float clamped = Math.Clamp(float.IsNaN(scale) ? 1f : scale, 0f, MaxSpeedScale);
        return (sbyte)Math.Clamp((int)MathF.Round((clamped - 1f) / SpeedScaleQuantum), -127, 127);
    }

    /// <summary>Decodes a wire <see cref="SpeedScaleQ"/> back to a speed multiplier: <c>1 + q * SpeedScaleQuantum</c>,
    /// floored at 0. 0 decodes to exactly 1 (unmodified). The floor is defence-in-depth against a corrupt or hostile
    /// frame: a <c>q</c> below -16 would otherwise decode NEGATIVE and drive the character backwards against its own
    /// command. There is no matching ceiling - an out-of-range high value is merely fast, not inverted.</summary>
    public static float DecodeSpeedScale(sbyte q) => MathF.Max(0f, 1f + q * SpeedScaleQuantum);

    /// <summary>Fixed wire scale for <see cref="HorizontalVelocityXQ"/> / <see cref="HorizontalVelocityZQ"/> (m/s per
    /// quantum unit): 1/256, giving 0.0039 m/s resolution and a <see cref="short"/> reach of +/-127.996 m/s, of which
    /// <see cref="MaxHorizontalSpeed"/> is the clamp actually applied. Deliberately an exact NEGATIVE POWER OF TWO for
    /// <see cref="SpeedScaleQuantum"/>'s reason rather than the 0.05-style decimal <see cref="ClimbRateQuantum"/>
    /// uses, and the reason bites harder here than it does there. A climb rate is a PRESENTATION signal: it drives a
    /// render glide, nothing reads it back into the simulation, so a decimal quantum landing a hair off the round
    /// number is invisible. A carried velocity is SIMULATION state on both heads. The client decodes it into its
    /// reconcile basis and flies the pending command window on it while the server flies its own copy, so both must
    /// multiply by bit-identical values or the two arcs separate. At 1/256 the decode <c>q * quantum</c> is EXACT (a
    /// power-of-two multiply only shifts the exponent), so a 30 m/s takeoff comes back as exactly 30 and both heads
    /// hold the same float. A decimal quantum rounds inside the multiply itself, putting the decoded velocity a hair
    /// off the grid the encoder aimed at.
    /// <para>That hair is the difference between this field and every other quantized one on this component: a
    /// carried velocity FEEDS THE NEXT TICK, so unlike a per-tick input its error does not wash out on the following
    /// frame. It compounds for the length of the flight.</para></summary>
    public const float HorizontalVelocityQuantum = 1f / 256f;

    /// <summary>The largest replicable horizontal speed PER AXIS (127 m/s), the clamp
    /// <see cref="QuantizeHorizontalVelocity"/> applies. The <see cref="short"/> reaches +/-127.996 m/s at
    /// <see cref="HorizontalVelocityQuantum"/> and the clamp sits at the round 127 below it, so the encoding keeps
    /// headroom rather than running to its edge, exactly as <see cref="MaxSpeedScale"/> does. It covers a default
    /// <see cref="KhaozEngine.Locomotion.MoveTuning.RunSpeed"/> at the full <see cref="MaxSpeedScale"/> (96 m/s) with
    /// room to spare. Being a per-axis bound it lets a diagonal arc carry up to about 179 m/s of total speed, which is
    /// the safe direction: the clamp exists to keep the encoding honest, not to cap what a game may fly at. A
    /// server-only NPC is not bound by this at all (it rides no wire), only what replicates is.</summary>
    public const float MaxHorizontalSpeed = 127f;

    /// <summary>Quantizes one axis of a carried horizontal velocity (m/s) to the wire <see cref="short"/>: NaN reads
    /// as 0, the value is CLAMPED to <c>[-MaxHorizontalSpeed, MaxHorizontalSpeed]</c> rather than wrapped, and the
    /// result is rounded to the nearest <see cref="HorizontalVelocityQuantum"/>. Both guards matter more here than on
    /// a per-tick field. A NaN that reached the carry would not corrupt one frame but strand the character
    /// permanently, since this value feeds the next tick's resolve. And an unclamped cast that overflowed the
    /// <see cref="short"/> would REVERSE a hurtling arc rather than cap it, turning an out-of-range speed into a
    /// character flung backwards. The final clamp to the symmetric +/-32767 range leaves -32768 unused, as
    /// <see cref="QuantizeClimbRate"/> leaves -128.</summary>
    public static short QuantizeHorizontalVelocity(float v)
    {
        float clamped = Math.Clamp(float.IsNaN(v) ? 0f : v, -MaxHorizontalSpeed, MaxHorizontalSpeed);
        return (short)Math.Clamp((int)MathF.Round(clamped / HorizontalVelocityQuantum), -32767, 32767);
    }

    /// <summary>Decodes one axis of a wire carried velocity back to m/s: <c>q * HorizontalVelocityQuantum</c>, exact
    /// at the power-of-two quantum. 0 decodes to exactly 0 (carrying nothing). There is deliberately no floor or
    /// ceiling on the way out, unlike <see cref="DecodeSpeedScale"/>: there a corrupt low value decodes NEGATIVE and
    /// drives the character backwards against its own command, so the read side has to defend against it, while here
    /// every representable value IS a legitimate velocity and a negative one simply means travelling the other way.
    /// The worst a corrupt frame can produce is a fast arc in an odd direction, which the server's own step and the
    /// anomaly check both already measure.</summary>
    public static float DecodeHorizontalVelocity(short q) => q * HorizontalVelocityQuantum;

    /// <summary>The vertical part of a full <see cref="PlayerMoveState"/> (the position is in
    /// <see cref="ReplicatedPosition"/>).</summary>
    public static MovementState From(in PlayerMoveState state) => new()
    {
        VerticalVelocity = state.Move.VerticalVelocity,
        Grounded = state.Move.Grounded,
        TimeSinceGrounded = state.Move.TimeSinceGrounded,
        JumpBufferRemaining = state.Move.JumpBufferRemaining,
        Swimming = state.Move.Swimming,
        TeleportEpoch = state.TeleportEpoch,
        ClimbRateQ = QuantizeClimbRate(state.Move.ClimbRate),
        SpeedScaleQ = QuantizeSpeedScale(state.Move.SpeedScale),
        // The carried airborne arc, one axis each (MoveState.HorizontalVelocity is an XZ Vector2, so its Y is world Z).
        HorizontalVelocityXQ = QuantizeHorizontalVelocity(state.Move.HorizontalVelocity.X),
        HorizontalVelocityZQ = QuantizeHorizontalVelocity(state.Move.HorizontalVelocity.Y),
    };
}
