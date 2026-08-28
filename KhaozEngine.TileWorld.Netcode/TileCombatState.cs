using KhaozEngine.Ecs;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The attacker-side half of a fight, and the half no client ever sees: the swing cadence the game supplied, how
/// many ticks are left before the next swing, and who last hurt this entity and when.
/// <para>Registered on <see cref="KhaozEngine.Replication.ReplicationChannels.Migrate"/> ALONE, so it follows an
/// entity across a region boundary and costs a viewer nothing. That is the channel combination
/// <c>ShardHost</c> describes for a mob's server-only state, and it is why an actor's per-viewer cost has no line
/// for this component. It is also where a threat table would eventually live, which is what
/// <see cref="LastDamagedBy"/> and <see cref="LastDamagedTick"/> are already carrying the seed of.</para>
/// <para>The TARGET is deliberately NOT here. It lives on <see cref="TileMoveState"/>, because the follow has to
/// run inside the one stepper both heads share and that stepper sees the state and the command and nothing
/// else.</para>
/// </summary>
public struct TileCombatState : IComponent
{
    /// <summary>Ticks between swings, from <c>ITileCombatRules.AttackTicks</c>. A cadence rather than a speed, so
    /// two heads on different frame clocks cannot disagree about when a swing is due.</summary>
    public byte AttackTicks;

    /// <summary>Ticks still to wait. Counts down once per tick regardless of range and FLOORS at zero, so an
    /// attacker that spent the wait walking swings on the first tick both conditions hold rather than starting the
    /// cadence again on arrival. That is OSRS, and it is what stops a chase from also being a cooldown reset.</summary>
    public byte CooldownRemaining;

    /// <summary>Net id of the last entity whose swing LANDED on this one, 0 when nothing has. A hit that connected
    /// for ZERO counts, because the ruling that asks for a counterattack asks for one when a hit lands rather than
    /// when damage does, and a blocked zero is the ordinary outcome of a bad accuracy roll against good defence.
    /// What a retaliating behaviour reads, and what a <see cref="TileActorIntentKind.Break"/> clears: an actor that
    /// broke off a fight must not be handed the same attacker back by the retaliation rule the moment it is home
    /// again.</summary>
    public long LastDamagedBy;

    /// <summary>The server tick that hit landed on, so a behaviour can age it out rather than retaliating against
    /// something that hit it a minute ago. Written with <see cref="LastDamagedBy"/> and under the same rule, so the
    /// pair is always one answer rather than two.</summary>
    public long LastDamagedTick;

    /// <summary>The server tick a combat event last TOUCHED this entity, in either direction: a swing it made or a
    /// swing made at it, whether that swing landed or missed. Zero when none ever has.
    /// <para>Separate from <see cref="LastDamagedTick"/> because the two answer different questions and widening one
    /// of them would break the other. A retaliation wants to know who hurt it, so a miss must not move that record.
    /// The combat LOGOUT window wants to know whether a fight is happening, which the spec states as "a combat event
    /// touched them", and a player being swung at and missed is exactly the player that rule exists to stop escaping
    /// by pulling the plug. Read by <c>TileWorldServer</c>'s own in-combat test and by nothing else, which is why it
    /// is not on <see cref="TileActorContext"/>: it is a session fact rather than a threat fact, and for the same
    /// reason a <see cref="TileActorIntentKind.Break"/> does not clear it.</para>
    /// <para>Tick zero is indistinguishable from never, exactly as <see cref="LastDamagedTick"/> is, and for the
    /// same reason: both are a bare tick index against a zero-based clock.</para></summary>
    public long LastCombatTick;

    /// <summary>The <see cref="TileMoveState.CombatTarget"/> the combat pass last saw on this entity, so a CHANGE is
    /// detectable without a second pass. Server-only bookkeeping, never a rules input.</summary>
    public long TargetSeen;

    /// <summary>The tick <see cref="TargetSeen"/> was first seen, which is what the roll order is taken on: oldest
    /// lock first, net id breaking the tie. The same shape and the same reasoning <c>ResolveActions</c> uses for its
    /// own <c>(IssuedTick, slot)</c> sort, because two attackers coming ready on one tick is a gameplay decision
    /// rather than a detail.</summary>
    public long TargetSinceTick;
}
