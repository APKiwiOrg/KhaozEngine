using System;
using System.Security.Cryptography;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Assets;

/// <summary>Logical payload IDs and self-verifying immutable resource addresses share one closed grammar.</summary>
internal static class MapAssetVersionIdentity
{
    internal static string Address(string role, string logicalId, ReadOnlySpan<byte> bytes) =>
        role + "/" + MapPrefabBindingPlanner.Escape(logicalId) + "/" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static bool Matches(string role, string logicalId, string resourceId, ReadOnlySpan<byte> bytes)
    {
        if (StringComparer.Ordinal.Equals(logicalId, resourceId)) return true;
        return TryParse(role, resourceId, out string? parsed) && parsed == logicalId &&
            StringComparer.Ordinal.Equals(resourceId, Address(role, logicalId, bytes));
    }
    internal static string LogicalId(string role, string resourceId) => TryParse(role, resourceId, out string? id) ? id! : resourceId;

    static bool TryParse(string role, string resourceId, out string? logicalId)
    {
        logicalId = null;
        string prefix = role + "/";
        if (!resourceId.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int separator = resourceId.IndexOf('/', prefix.Length);
        if (separator <= prefix.Length || resourceId.Length - separator - 1 != 64) return false;
        foreach (char c in resourceId.AsSpan(separator + 1))
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        string escaped = resourceId[prefix.Length..separator];
        string decoded = Uri.UnescapeDataString(escaped);
        if (!StringComparer.Ordinal.Equals(MapPrefabBindingPlanner.Escape(decoded), escaped)) return false;
        logicalId = decoded;
        return true;
    }
}
