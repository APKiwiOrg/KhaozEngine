using System;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// Numeric, dot-separated version ordering for the games' <c>x.y.z</c> scheme, so the status evaluator can
/// gate a client against the server's min/latest versions. Each dot segment is compared numerically (so
/// <c>0.7.10</c> is newer than <c>0.7.9</c>, which a string compare gets wrong), a non-numeric or missing
/// segment counts as 0 (so <c>1.2</c> equals <c>1.2.0</c>), and a null/blank string is treated as the empty
/// version (all-zero).
/// </summary>
// NOTE: deliberately mirrors KhaozEngine.Updates.UpdateVersion's numeric-segment rule rather than depending
// on it - pulling the whole delta-update pipeline (+ Platform) into a package clients reference just for the
// poller would violate the "no heavy deps in low packages" layering rule. A future shared version-primitive
// leaf could host both; kept separate for phase 1.
public static class VersionOrder
{
    /// <summary>
    /// Compares two versions numerically segment-by-segment. Returns a negative number when
    /// <paramref name="a"/> orders before <paramref name="b"/>, zero when equal, positive when after.
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

    /// <summary>True when <paramref name="version"/> is strictly older than <paramref name="floor"/>.</summary>
    public static bool IsBelow(string? version, string? floor) => Compare(version, floor) < 0;

    private static string[] Split(string? version) =>
        string.IsNullOrWhiteSpace(version) ? Array.Empty<string>() : version.Split('.');
}
