using System;

#nullable enable

namespace KhaozEngine.Primitives;

/// <summary>
/// Numeric, dot-separated <c>x.y.z</c> version comparison, the one shared core behind every version gate
/// in the engine (auto-update, out-of-band server status). Each dot segment is compared numerically, so
/// <c>0.7.10</c> orders after <c>0.7.9</c> (a plain string compare gets this wrong), a missing or
/// non-numeric segment counts as 0 (so <c>1.2</c> equals <c>1.2.0</c>), and a null or blank string is
/// treated as the empty version (all-zero), so it never throws on a missing value.
/// <para>
/// Zero-dependency leaf math: pure, allocation-light (one array per side), BCL only. Consolidated from two
/// independent copies (<c>KhaozEngine.Updates.UpdateVersion</c> and <c>KhaozEngine.ServerStatus.VersionOrder</c>
/// deliberately mirrored the same rule to avoid a heavier cross-package dependency). Both now delegate here
/// so the rule cannot drift again.
/// </para>
/// </summary>
public static class VersionComparer
{
    /// <summary>
    /// Compares two versions segment-by-segment. Returns a negative number when <paramref name="a"/>
    /// orders before <paramref name="b"/>, zero when equal, positive when after.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        string[] left = Split(a);
        string[] right = Split(b);
        int maxLen = Math.Max(left.Length, right.Length);

        for (int i = 0; i < maxLen; i++)
        {
            int l = i < left.Length && int.TryParse(left[i], out int lv) ? lv : 0;
            int r = i < right.Length && int.TryParse(right[i], out int rv) ? rv : 0;
            if (l != r)
            {
                return l < r ? -1 : 1;
            }
        }

        return 0;
    }

    static string[] Split(string? version) =>
        string.IsNullOrWhiteSpace(version) ? Array.Empty<string>() : version.Split('.');
}
