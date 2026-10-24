using System;

namespace KhaozEngine.Physics;

/// <summary>Live owned capacities and maximum per-session capacities, in deterministic bytes rather than allocations.</summary>
internal readonly record struct SupportProofReuseRetention(long Keys, long Bytes, long PeakKeys, long PeakBytes);

internal static partial class SupportQueryDiagnostics
{
    [ThreadStatic] static SupportProofReuseRetention t_retention;

    internal static SupportProofReuseRetention Retention => t_retention;

    /// <summary>Restores recorder ownership and observed peaks while preserving the current physical balance.
    /// The saved count array is owned by its suspended recorder and is never copied or merged.</summary>
    internal static void Restore(in ThreadState saved) => Exchange(saved with
    {
        Retention = saved.Retention with { Keys = t_retention.Keys, Bytes = t_retention.Bytes },
    });

    internal static void ProofReuseRetention(int keyDelta, int byteDelta, int sessionKeys, int sessionBytes)
    {
        // Storage outlives counter enablement changes. Always balance its acquisition and release.
        t_retention = new(checked(t_retention.Keys + keyDelta), checked(t_retention.Bytes + byteDelta),
            Math.Max(t_retention.PeakKeys, sessionKeys), Math.Max(t_retention.PeakBytes, sessionBytes));
    }

    internal static void MergeProofReuseRetention(in SupportProofReuseRetention retention, bool hasCompleteLifetimes = true)
    {
        if (!t_enabled) return;
        t_retention = new(checked(t_retention.Keys + retention.Keys), checked(t_retention.Bytes + retention.Bytes),
            Math.Max(t_retention.PeakKeys, retention.PeakKeys), Math.Max(t_retention.PeakBytes, retention.PeakBytes));
        t_hasCompleteLifetimes &= hasCompleteLifetimes;
    }
}
