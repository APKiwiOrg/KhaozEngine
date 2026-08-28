namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// What one swing is rolled against: both parties' net ids, both COMMITTED tiles as they stand after this tick's
/// movement, both healths, and the tick. Everything the engine knows about a hit, which is deliberately everything
/// about the LATTICE and nothing about the NUMBERS.
/// </summary>
/// <param name="AttackerNetId">Who is swinging.</param>
/// <param name="AttackerTile">The tile it is committed to after this tick's movement.</param>
/// <param name="AttackerHealth">Its health as the roll phase found it, before any of this tick's damage lands.</param>
/// <param name="TargetNetId">Who is being swung at.</param>
/// <param name="TargetTile">The tile it is committed to after this tick's movement.</param>
/// <param name="TargetHealth">Its health as the roll phase found it, before any of this tick's damage lands.</param>
/// <param name="Tick">The server tick being resolved.</param>
public readonly record struct TileAttackContext(
    long AttackerNetId,
    TileCoord AttackerTile,
    TileHealth AttackerHealth,
    long TargetNetId,
    TileCoord TargetTile,
    TileHealth TargetHealth,
    long Tick);

/// <summary>
/// What the game decided one swing did. <c>Kind</c> is the game's own vocabulary and the engine NEVER inspects it:
/// it is the hitsplat colour, carried to the client the same way <c>TileProtocol</c>'s game-message kind is carried,
/// as a number this package routes and never opens.
/// <para>The two fields are read INDEPENDENTLY, and the factories below are the only thing holding them together:
/// the engine subtracts <c>Damage</c> and reports <c>Landed</c>, so a hand-built <c>new TileAttackOutcome(false, 50,
/// 0)</c> is a miss that takes 50 health. Nothing enforces the pairing, because the enforcement would have to live
/// in a constructor a record struct cannot make private. Build one through <see cref="Hit"/> or
/// <see cref="Miss"/>.</para>
/// </summary>
/// <param name="Landed">Whether the swing connected. A miss still produces an event and still draws a hitsplat,
/// because a fight with invisible misses reads as a broken fight. This is also the fact a RETALIATION rides: a hit
/// that connected for zero damage still names its attacker on the target's damage record, and a miss does not.</param>
/// <param name="Damage">How much health to subtract, 0 on a miss.</param>
/// <param name="Kind">The game's own hit classification.</param>
public readonly record struct TileAttackOutcome(bool Landed, ushort Damage, byte Kind)
{
    /// <summary>A swing that connected.</summary>
    /// <param name="damage">Health to subtract.</param>
    /// <param name="kind">The game's own hit classification.</param>
    public static TileAttackOutcome Hit(ushort damage, byte kind = 0) => new(true, damage, kind);

    /// <summary>A swing that did not connect. Still an event, still a hitsplat.</summary>
    /// <param name="kind">The game's own hit classification.</param>
    public static TileAttackOutcome Miss(byte kind = 0) => new(false, 0, kind);
}

/// <summary>
/// Where the GAME plugs into the hit pipeline. The engine owns whether a swing is DUE (the cooldown) and whether it
/// is LEGAL (adjacency through <see cref="TileReach"/>). This owns what it DOES.
/// <para>The line is drawn where a second game would disagree. Two games will not disagree about whether a
/// cardinally adjacent attacker on the same plane may swing. They will disagree about every number in the
/// swing.</para>
/// <para>The roll is NEVER predicted by a client, so it needs no cross-head determinism at all, only server-side
/// REPRODUCIBILITY for tests and replays. An implementation that draws from its own RNG gets that from the engine's
/// fixed roll order, which is oldest lock first and net id ascending.</para>
/// </summary>
public interface ITileCombatRules
{
    /// <summary>Rolls one swing. Called once per eligible attacker per tick, in the engine's fixed order, and BEFORE
    /// any of this tick's damage is applied, so no roll can see another roll's result.</summary>
    /// <param name="context">Both parties as the roll phase found them.</param>
    TileAttackOutcome Roll(in TileAttackContext context);

    /// <summary>Ticks this attacker waits after a swing, landed or missed. A seam member rather than a constant
    /// because the attack cadence is exactly the kind of number a feel round moves.
    /// <para>ZERO IS NOT A CADENCE, so answering it falls back rather than swinging every tick: the engine takes the
    /// value the attacker's spawn seeded onto <see cref="TileCombatState.AttackTicks"/>, and if that is zero too it
    /// takes one tick. Both are degenerate-input guards rather than tuning numbers, and a game that means a cadence
    /// answers it here.</para></summary>
    /// <param name="attackerNetId">Who swung.</param>
    byte AttackTicks(long attackerNetId);
}
