using System;
using KhaozEngine.Primitives;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>Numeric, dot-separated version comparison for update gating (e.g. "1.2.10" &gt; "1.2.9").</summary>
public static class UpdateVersion
{
    /// <summary>
    /// True when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.
    /// Each dot segment is compared numerically; non-numeric or missing segments count as 0, so
    /// "1.2" and "1.2.0" are equal. Returns false when equal or older.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="current"/> or <paramref name="candidate"/> is null. Both parameters are non-nullable
    /// by contract (no caller in the engine or its consumers passes null), so this is an explicit guard
    /// rather than the incidental <see cref="NullReferenceException"/> the old, non-delegating implementation
    /// threw from an unguarded <c>Split</c> call, since the shared comparer this now delegates to
    /// (<see cref="VersionComparer"/>) is deliberately null-tolerant for its other caller
    /// (<c>KhaozEngine.ServerStatus.VersionOrder</c>). A null argument still fails loudly, it just fails
    /// with the more precise, idiomatic exception type instead of silently comparing as an empty version.
    /// </exception>
    public static bool IsNewer(string current, string candidate)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);

        return VersionComparer.Compare(current, candidate) < 0;
    }
}
