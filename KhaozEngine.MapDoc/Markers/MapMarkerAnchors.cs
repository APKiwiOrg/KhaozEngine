using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Markers;

/// <summary>The caller's marker vocabulary. The engine defines no role and never guesses one.</summary>
public sealed record MapMarkerConsumerRoles(string PlayerStartRole, string SpawnRole, string LandmarkRole);

/// <summary>Caller-owned marker consumer configuration. <see cref="Roles"/> names the player start role used for window
/// anchoring and summaries. <see cref="LegacySpawnSurfaceId"/> is the one Surface owner that unadopted native legacy
/// spawns are read on. Either may be absent, and nothing is defaulted.</summary>
public sealed record MapMarkerConsumerOptions(MapMarkerConsumerRoles? Roles, string? LegacySpawnSurfaceId)
{
    /// <summary>No roles and no Surface owner.</summary>
    public static MapMarkerConsumerOptions None { get; } = new(null, null);

    /// <summary>Refuses blank role names and a blank Surface owner. Absent members stay absent.</summary>
    internal void Require()
    {
        if (Roles is { } roles &&
            (string.IsNullOrWhiteSpace(roles.PlayerStartRole) || string.IsNullOrWhiteSpace(roles.SpawnRole) || string.IsNullOrWhiteSpace(roles.LandmarkRole)))
            throw new MapDocumentException("Marker consumer roles require a nonblank player start, spawn and landmark role.");
        if (LegacySpawnSurfaceId is not null && string.IsNullOrWhiteSpace(LegacySpawnSurfaceId))
            throw new MapDocumentException("The legacy spawn Surface owner must be nonblank when given.");
    }
}

/// <summary>One marker's world XZ, enabled state and role, read from metadata alone for window anchoring.</summary>
public sealed record MapMarkerAnchor(string Id, MapExactXz Position, bool Enabled, string Role);

/// <summary>R6-P6 and R6-P13. Window anchors from root marker metadata and verified prefab bytes. No support, terrain or
/// tile body is read, nothing is resolved and no whole-world identity is certified.</summary>
public static class MapMarkerAnchors
{
    /// <summary>Every marker candidate of <paramref name="manifest"/> after prefab overrides, removals and remaps, through
    /// the shared metadata enumeration. Each XZ passes through its frame once with the same kernel effective resolution
    /// uses, so an anchor matches the resolved marker's XZ. Heights are never read. A prefab missing from
    /// <paramref name="assets"/> refuses.</summary>
    public static IReadOnlyList<MapMarkerAnchor> Read(MapDocument manifest, MapAssetClosure assets)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(assets);
        var anchors = new List<MapMarkerAnchor>();
        foreach (MapMarkerCandidate candidate in MapMarkerExpansion.Capture(manifest, assets))
        {
            MapLocalMarkerDoc marker = candidate.Value;
            var local = new MapExactXz(MapExactValue.FromSingle(marker.Position.X), MapExactValue.FromSingle(marker.Position.Z));
            MapExactXz position = candidate.Frame is { } frame ? new MapResourceTransform(frame).Xz(local) : local;
            anchors.Add(new(marker.Id, position, marker.Enabled, marker.Role));
        }
        return Array.AsReadOnly(anchors.OrderBy(a => a.Id, StringComparer.Ordinal).ToArray());
    }

    /// <summary>The enabled anchor whose role equals <paramref name="role"/> ordinally and lies nearest
    /// <paramref name="centre"/>, by squared distance over binary64 differences, then by ordinal ID. Null when none
    /// matches. A pure function of its inputs.</summary>
    public static MapMarkerAnchor? Select(IReadOnlyList<MapMarkerAnchor> anchors, string role, MapExactXz centre)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(role);
        MapMarkerAnchor? best = null;
        double bestDistance = 0;
        foreach (MapMarkerAnchor anchor in anchors)
        {
            ArgumentNullException.ThrowIfNull(anchor);
            if (!anchor.Enabled || !string.Equals(anchor.Role, role, StringComparison.Ordinal)) continue;
            double dx = anchor.Position.X.ToDouble() - centre.X.ToDouble();
            double dz = anchor.Position.Z.ToDouble() - centre.Z.ToDouble();
            double distance = dx * dx + dz * dz;
            if (best is null || distance < bestDistance ||
                (distance == bestDistance && StringComparer.Ordinal.Compare(anchor.Id, best.Id) < 0))
            {
                best = anchor;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>The tile holding <paramref name="position"/>, flooring exactly so negative coordinates never truncate
    /// toward zero.</summary>
    internal static MapTileCoord TileOf(MapExactXz position, float tileSize)
    {
        if (!(tileSize > 0) || !float.IsFinite(tileSize)) throw new ArgumentOutOfRangeException(nameof(tileSize));
        MapExactValue size = MapExactValue.FromSingle(tileSize);
        return new(checked((int)position.X.Divide(size).Floor()), checked((int)position.Z.Divide(size).Floor()));
    }
}
