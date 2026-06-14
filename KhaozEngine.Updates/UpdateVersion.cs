using System;

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
    public static bool IsNewer(string current, string candidate)
    {
        string[] currentParts = current.Split('.');
        string[] candidateParts = candidate.Split('.');
        int maxLen = Math.Max(currentParts.Length, candidateParts.Length);

        for (int i = 0; i < maxLen; i++)
        {
            int c = i < currentParts.Length && int.TryParse(currentParts[i], out int cv) ? cv : 0;
            int r = i < candidateParts.Length && int.TryParse(candidateParts[i], out int rv) ? rv : 0;
            if (r > c) return true;
            if (r < c) return false;
        }

        return false;
    }
}
