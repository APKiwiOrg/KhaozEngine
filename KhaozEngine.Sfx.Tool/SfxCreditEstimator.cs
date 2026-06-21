using System;

namespace KhaozEngine.Sfx;

/// <summary>
/// Approximate ElevenLabs credit cost for a generation, using the published API list rates. Account tier
/// multipliers may apply, so this is a planning estimate, not a bill. Reference: ElevenLabs "How much does it
/// cost to generate sound effects" (API rates).
/// </summary>
public static class SfxCreditEstimator
{
    /// <summary>Credits when the API auto-picks the duration.</summary>
    public const int AutoDurationCredits = 100;
    /// <summary>Credits per second when the duration is set explicitly.</summary>
    public const int CreditsPerSecond = 20;

    /// <summary>Estimated credits to generate one entry.</summary>
    public static int Estimate(SfxEntry entry) =>
        entry.DurationSeconds is { } d ? (int)Math.Ceiling(d * CreditsPerSecond) : AutoDurationCredits;
}
