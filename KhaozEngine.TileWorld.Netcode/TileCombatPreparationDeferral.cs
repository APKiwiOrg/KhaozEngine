namespace KhaozEngine.TileWorld.Netcode;

/// <summary>The bounded wait an attempt may spend past its impact tick for legal reach.</summary>
internal static class TileCombatPreparationDeferral
{
    /// <summary>Ticks an attempt may wait past its impact for legal reach.</summary>
    internal static byte BoundTicks(in TileCombatPreparation attempt) => attempt.StrikeTicks;

    /// <summary>
    /// True when a record served at <paramref name="serverTick"/> is still active. Callers guarantee
    /// <c>serverTick &gt;= 0</c> and <c>ImpactTick &gt; PrepareTick &gt;= 0</c>, so the subtraction cannot overflow.
    /// </summary>
    internal static bool IsLive(in TileCombatPreparation attempt, long serverTick) =>
        serverTick - attempt.ImpactTick < BoundTicks(attempt);
}
