using System.Collections.Frozen;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>Checks mutable layer selections without boxing common set enumerators on the frame path.</summary>
internal static class TilePropLayerMembership
{
    public static bool Matches(FrozenSet<string> expected, IReadOnlySet<string> current)
    {
        if (expected.Count != current.Count) return false;
        // Query the ordinal snapshot, preserving validation even when the caller uses a different comparer.
        if (current is HashSet<string> mutable)
        {
            foreach (string id in mutable)
                if (!expected.Contains(id)) return false;
            return true;
        }
        if (current is FrozenSet<string> frozen)
        {
            foreach (string id in frozen)
                if (!expected.Contains(id)) return false;
            return true;
        }
        return expected.SetEquals(current);
    }
}
