using KhaozEngine.Primitives;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// Numeric, dot-separated version ordering for the games' <c>x.y.z</c> scheme, so the status evaluator can
/// gate a client against the server's min/latest versions. Each dot segment is compared numerically (so
/// <c>0.7.10</c> is newer than <c>0.7.9</c>, which a string compare gets wrong), a non-numeric or missing
/// segment counts as 0 (so <c>1.2</c> equals <c>1.2.0</c>), and a null/blank string is treated as the empty
/// version (all-zero).
/// </summary>
// NOTE: this used to be an independent copy of KhaozEngine.Updates.UpdateVersion's numeric-segment rule,
// kept separate rather than depending on it because pulling the whole delta-update pipeline (+ Platform)
// into a package clients reference just for the poller would violate the "no heavy deps in low packages"
// layering rule. Both now delegate to the shared KhaozEngine.Primitives.VersionComparer leaf instead, so
// the rule lives in exactly one place and cannot drift. This type stays a thin, source-compatible wrapper
// so existing callers (in-engine and consumers) are unaffected.
public static class VersionOrder
{
    /// <summary>
    /// Compares two versions numerically segment-by-segment. Returns a negative number when
    /// <paramref name="a"/> orders before <paramref name="b"/>, zero when equal, positive when after.
    /// </summary>
    public static int Compare(string? a, string? b) => VersionComparer.Compare(a, b);

    /// <summary>True when <paramref name="version"/> is strictly older than <paramref name="floor"/>.</summary>
    public static bool IsBelow(string? version, string? floor) => Compare(version, floor) < 0;
}
