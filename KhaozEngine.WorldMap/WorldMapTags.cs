using System;
using System.Collections.Generic;

namespace KhaozEngine.WorldMap;

/// <summary>Shared ordinal all-of tag matching for map symbols and features.</summary>
internal static class WorldMapTags
{
    internal static bool HasAll(IReadOnlyList<string> tags, IReadOnlyList<string> required)
    {
        foreach (string requiredTag in required)
        {
            bool found = false;
            foreach (string tag in tags)
                if (string.Equals(tag, requiredTag, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            if (!found) return false;
        }
        return true;
    }
}
