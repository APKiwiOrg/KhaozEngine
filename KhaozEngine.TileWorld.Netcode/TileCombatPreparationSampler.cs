using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>The presentation stage of an authoritative attack intention, never a combat outcome.</summary>
public enum TileCombatPreparationStage
{
    /// <summary>The scheduled preparation has not started.</summary>
    Hold,
    /// <summary>The attacker is preparing, before the final strike interval.</summary>
    Prepare,
    /// <summary>The final strike interval is in progress.</summary>
    Strike,
    /// <summary>The intended impact tick has arrived. Presentation must wait for the server's outcome.</summary>
    AwaitingOutcome
}

/// <summary>A pure presentation sample with normalized progress inside its stage.</summary>
/// <param name="Stage">The interval containing the sampled time.</param>
/// <param name="Progress">Zero for Hold, one for AwaitingOutcome, otherwise progress from zero to one.</param>
public readonly record struct TileCombatPreparationSample(TileCombatPreparationStage Stage, float Progress);

/// <summary>Samples explicit preparation boundaries without predicting damage, emitting events or looping attacks.</summary>
public static class TileCombatPreparationSampler
{
    /// <summary>Samples a schedule at fractional server time. Non-finite time yields Hold with zero progress.
    /// A lead equal to the strike duration starts directly at Strike. Times at or after impact await an outcome.</summary>
    /// <param name="preparation">A valid authoritative schedule.</param>
    /// <param name="serverTick">Fractional authoritative presentation time.</param>
    /// <returns>The current stage and its normalized progress. Pose mapping and recovery belong to the consumer.</returns>
    /// <exception cref="ArgumentException">The schedule has invalid identity or timing, regardless of sample time.</exception>
    public static TileCombatPreparationSample Sample(in TileCombatPreparation preparation, double serverTick)
    {
        // Use the same validity contract as an active wire record, with its own start as the header tick.
        // Sample time is independent: callers must be able to sample before preparation or after impact.
        if (!TileProtocol.ValidPreparationRecord(preparation, preparation.PrepareTick))
            throw new ArgumentException("The preparation must have valid identities and ordered timing within its cadence.", nameof(preparation));
        if (!double.IsFinite(serverTick) || serverTick < 0d)
            return new(TileCombatPreparationStage.Hold, 0f);
        if (serverTick >= (double)long.MaxValue)
            return new(TileCombatPreparationStage.AwaitingOutcome, 1f);
        // Comparing integer boundaries after converting them to double can shift a stage above 2^53.
        // Split the supplied time instead. Only the small within-stage difference becomes floating point.
        long wholeTick = (long)serverTick;
        if (wholeTick < preparation.PrepareTick) return new(TileCombatPreparationStage.Hold, 0f);
        if (wholeTick >= preparation.ImpactTick) return new(TileCombatPreparationStage.AwaitingOutcome, 1f);
        double fraction = serverTick - wholeTick;
        long strikeTick = preparation.StrikeTick;
        if (wholeTick < strikeTick)
            return new(TileCombatPreparationStage.Prepare,
                (float)((wholeTick - preparation.PrepareTick + fraction) / (strikeTick - preparation.PrepareTick)));
        return new(TileCombatPreparationStage.Strike,
            (float)((wholeTick - strikeTick + fraction) / preparation.StrikeTicks));
    }
}
