namespace KhaozEngine.TileWorld.Netcode;

/// <summary>A scheduled intention to attack. It carries no predicted damage or confirmed outcome.</summary>
/// <param name="AttackerNetId">The combatant preparing.</param>
/// <param name="TargetNetId">The intended target.</param>
/// <param name="AttackId">Monotonically increasing identity within the attacker's lifetime, starting at one.</param>
/// <param name="Revision">The deadline revision of this attempt, starting at one.</param>
/// <param name="PresentationKey">The game's public motion identity captured for this attempt.</param>
/// <param name="PrepareTick">The authoritative tick at which preparation starts.</param>
/// <param name="ImpactTick">The authoritative tick at which the server must recheck and resolve the attempt.</param>
/// <param name="StrikeTicks">Ticks reserved for the final strike.</param>
/// <param name="CadenceTicks">The captured number of ticks between continuing impacts.</param>
public readonly record struct TileCombatPreparation(
    long AttackerNetId, long TargetNetId, ulong AttackId, uint Revision,
    uint PresentationKey, long PrepareTick, long ImpactTick,
    byte StrikeTicks, byte CadenceTicks)
{
    /// <summary>The authoritative tick at which the final strike starts.</summary>
    public long StrikeTick => ImpactTick - StrikeTicks;
}

/// <summary>Why a scheduled attempt ended without a combat roll. Numeric values are the preparation wire contract.</summary>
public enum TileCombatPreparationEndReason : byte
{
    /// <summary>No cancellation. Reserved for a resolved terminal record.</summary>
    None = 0,
    /// <summary>The attacker stopped holding a combat lock.</summary>
    Disengaged = 1,
    /// <summary>The combat lock now names a different target.</summary>
    TargetChanged = 2,
    /// <summary>The presentation key, timing or cadence changed.</summary>
    ProfileChanged = 3,
    /// <summary>A participant died, despawned or otherwise became unavailable.</summary>
    ParticipantUnavailable = 4,
    /// <summary>The rules no longer permit this attack.</summary>
    PermissionRevoked = 5,
    /// <summary>The target was not in legal reach at impact.</summary>
    IllegalReach = 6,
    /// <summary>A participant's teleport epoch changed.</summary>
    Teleport = 7,
    /// <summary>The game's profile has invalid timing.</summary>
    InvalidProfile = 8,
    /// <summary>The server no longer has combat rules with which to resolve the attack.</summary>
    RulesUnavailable = 9
}

/// <summary>A confirmed combat outcome paired with the preparation identity that produced it.</summary>
/// <param name="ImpactTick">The authoritative resolution tick.</param>
/// <param name="AttackId">The resolved attempt's identity.</param>
/// <param name="Revision">The resolved attempt's deadline revision.</param>
/// <param name="PresentationKey">The motion identity captured by that attempt.</param>
/// <param name="Outcome">The server's actual combat result.</param>
public readonly record struct PreparedCombatEvent(
    long ImpactTick, ulong AttackId, uint Revision, uint PresentationKey,
    TileCombatEvent Outcome);

/// <summary>An attempt that ended without a combat roll, retaining its original deadline for identification.</summary>
/// <param name="ServerTick">The authoritative cancellation tick.</param>
/// <param name="AttackerNetId">The combatant whose attempt ended.</param>
/// <param name="TargetNetId">The attempt's intended target.</param>
/// <param name="AttackId">The cancelled attempt's identity.</param>
/// <param name="Revision">The cancelled deadline revision.</param>
/// <param name="PresentationKey">The motion identity captured by that attempt.</param>
/// <param name="ImpactTick">The impact deadline that was cancelled.</param>
/// <param name="Reason">The reason the attempt ended.</param>
public readonly record struct CombatPreparationEnded(
    long ServerTick, long AttackerNetId, long TargetNetId, ulong AttackId,
    uint Revision, uint PresentationKey, long ImpactTick,
    TileCombatPreparationEndReason Reason);
