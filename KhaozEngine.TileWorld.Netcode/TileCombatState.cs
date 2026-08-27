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

    /// <summary>Net id of the last entity to land damage on this one, 0 when nothing has. What a retaliating
    /// behaviour reads.</summary>
    public long LastDamagedBy;

    /// <summary>The server tick that damage landed on, so a behaviour can age it out rather than retaliating
    /// against something that hit it a minute ago.</summary>
    public long LastDamagedTick;
}
