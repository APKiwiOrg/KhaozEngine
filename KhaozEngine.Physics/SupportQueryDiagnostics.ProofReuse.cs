using System;

namespace KhaozEngine.Physics;

/// <summary>Live owned capacities and maximum per-session capacities, in deterministic bytes rather than allocations.</summary>
internal readonly record struct SupportProofReuseRetention(int Keys, int Bytes, int PeakKeys, int PeakBytes);

internal static partial class SupportQueryDiagnostics
{
    [ThreadStatic] static SupportProofReuseRetention t_retention;

    internal static SupportProofReuseRetention Retention => t_retention;

    internal static void ProofReuseRetention(int keyDelta, int byteDelta, int sessionKeys, int sessionBytes)
    {
        // Storage outlives counter enablement changes. Always balance its acquisition and release.
        t_retention = new(t_retention.Keys + keyDelta, t_retention.Bytes + byteDelta,
            Math.Max(t_retention.PeakKeys, sessionKeys), Math.Max(t_retention.PeakBytes, sessionBytes));
    }

    internal static void MergeProofReuseRetention(in SupportProofReuseRetention retention)
    {
        if (!t_enabled) return;
        t_retention = new(t_retention.Keys + retention.Keys, t_retention.Bytes + retention.Bytes,
            Math.Max(t_retention.PeakKeys, retention.PeakKeys), Math.Max(t_retention.PeakBytes, retention.PeakBytes));
    }
}
