namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Game-owned preparation timing and public presentation identity for an attack.</summary>
/// <param name="LeadTicks">Ticks from preparation start to impact, at least one and no greater than cadence.</param>
/// <param name="StrikeTicks">Ticks in the final strike, at least one and no greater than the lead.</param>
/// <param name="PresentationKey">An opaque game-owned key identifying the motion, including visible equipment.</param>
public readonly record struct TileCombatPreparationProfile(
    byte LeadTicks, byte StrikeTicks, uint PresentationKey);

/// <summary>Supplies the game's current preparation profile without deciding damage or legal reach.</summary>
public interface ITileCombatPreparationRules
{
    /// <summary>Reads an attacker's profile from current admitted state. The read must be deterministic and
    /// side-effect free. The engine compares its timing and key when deciding whether an attempt still applies.</summary>
    /// <param name="attackerNetId">The combatant whose preparation is being considered.</param>
    TileCombatPreparationProfile ProfileFor(long attackerNetId);
}
