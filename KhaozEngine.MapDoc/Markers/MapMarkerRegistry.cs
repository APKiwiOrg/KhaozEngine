using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Markers;

/// <summary>A half-open XZ rectangle in world metres. Minima are inclusive and maxima exclusive on both axes, including
/// negative coordinates. Bounds must be finite with minima not above maxima. A zero width or depth holds no point.</summary>
public readonly record struct MapMarkerRect(double MinX, double MinZ, double MaxX, double MaxZ);

/// <summary>One marker inside a rectangle with the caller's point-membership answer at its exact position. Every
/// membership status is retained, refusals included. A rectangle filter makes no claim of complete space membership.</summary>
public sealed record MapMarkerMapPoint(MapEffectiveMarker Marker, MapMembershipResult Space);

/// <summary>An immutable lookup over effective markers. It owns no document store. Every list is in ordinal ID order.
/// <see cref="All"/> is the exhaustive view that validation and persistence keys use, and enabled filtering never
/// discards it. IDs and names are ordinal, case-sensitive and unique. Exact points and ordered tags are kept as given.</summary>
public sealed class MapMarkerRegistry
{
    static readonly IReadOnlyList<MapEffectiveMarker> None = Array.AsReadOnly(Array.Empty<MapEffectiveMarker>());
    readonly Dictionary<string, MapEffectiveMarker> _byId = new(StringComparer.Ordinal);
    readonly Dictionary<string, MapEffectiveMarker> _byName = new(StringComparer.Ordinal);
    readonly Dictionary<string, Views> _byRole;
    readonly Dictionary<string, Views> _byTag;
    readonly Lazy<MapMarkerSpatialIndex> _index;

    public IReadOnlyList<MapEffectiveMarker> All { get; }
    public IReadOnlyList<MapEffectiveMarker> Enabled { get; }

    public MapMarkerRegistry(IReadOnlyList<MapEffectiveMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var copies = new MapEffectiveMarker[markers.Count];
        for (int i = 0; i < copies.Length; i++)
        {
            MapEffectiveMarker marker = markers[i] ?? throw new ArgumentException("Marker entries must not be null.", nameof(markers));
            if (string.IsNullOrEmpty(marker.Id) || string.IsNullOrEmpty(marker.Name) || marker.Role is null || marker.Tags is null || marker.Tags.Any(t => t is null))
                throw new ArgumentException($"Marker '{marker.Id}' requires an ID, name, role and non-null tags.", nameof(markers));
            // Detached tags, so a caller's list cannot change a published registry.
            copies[i] = marker with { Tags = Array.AsReadOnly(marker.Tags.ToArray()) };
        }
        Array.Sort(copies, (a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        foreach (MapEffectiveMarker marker in copies)
        {
            if (!_byId.TryAdd(marker.Id, marker)) throw new ArgumentException($"Duplicate marker ID '{marker.Id}'.", nameof(markers));
            if (!_byName.TryAdd(marker.Name, marker))
                throw new ArgumentException($"Markers '{_byName[marker.Name].Id}' and '{marker.Id}' have duplicate marker name '{marker.Name}'.", nameof(markers));
        }
        All = Array.AsReadOnly(copies);
        Enabled = Array.AsReadOnly(copies.Where(m => m.Enabled).ToArray());
        _byRole = Index(copies.Select(m => (m.Role, m)));
        // A repeated tag on one marker lists that marker once. The marker's own tags stay as authored.
        _byTag = Index(copies.SelectMany(m => m.Tags.Distinct(StringComparer.Ordinal).Select(t => (t, m))));
        _index = new(() => new MapMarkerSpatialIndex(copies));
    }

    public MapEffectiveMarker? FindById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _byId.GetValueOrDefault(id);
    }

    public MapEffectiveMarker? FindByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.GetValueOrDefault(name);
    }

    /// <summary>Markers whose role equals <paramref name="role"/> ordinally. Roles are caller vocabulary.</summary>
    public IReadOnlyList<MapEffectiveMarker> ByRole(string role, bool enabledOnly) => Lookup(_byRole, role, enabledOnly);

    /// <summary>Markers carrying <paramref name="tag"/>, each listed once.</summary>
    public IReadOnlyList<MapEffectiveMarker> ByTag(string tag, bool enabledOnly) => Lookup(_byTag, tag, enabledOnly);

    /// <summary>Markers whose exact XZ lies in the half-open <paramref name="rect"/>, in ID order. Candidates come from
    /// the registry's spatial index and pass an exact test before <paramref name="membership"/> runs, once for each
    /// returned marker. The caller delegates to point membership in a view acquired from the same effective world.</summary>
    public IReadOnlyList<MapMarkerMapPoint> InRect(MapMarkerRect rect, bool enabledOnly, Func<MapEffectiveMarker, MapMembershipResult> membership) =>
        InRect(rect, enabledOnly, membership, out _);

    internal IReadOnlyList<MapMarkerMapPoint> InRect(MapMarkerRect rect, bool enabledOnly, Func<MapEffectiveMarker, MapMembershipResult> membership,
        out MapMarkerQueryWork work)
    {
        ArgumentNullException.ThrowIfNull(membership);
        if (!double.IsFinite(rect.MinX) || !double.IsFinite(rect.MinZ) || !double.IsFinite(rect.MaxX) || !double.IsFinite(rect.MaxZ))
            throw new ArgumentException("Marker rectangle bounds must be finite.", nameof(rect));
        if (rect.MinX > rect.MaxX || rect.MinZ > rect.MaxZ)
            throw new ArgumentException("Marker rectangle minima must not exceed its maxima.", nameof(rect));
        if (rect.MinX == rect.MaxX || rect.MinZ == rect.MaxZ)
        {
            work = new(0, 0);
            return Array.AsReadOnly(Array.Empty<MapMarkerMapPoint>());
        }
        IReadOnlyList<MapEffectiveMarker> candidates = _index.Value.Query(rect, enabledOnly, out work);
        var result = new MapMarkerMapPoint[candidates.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = new(candidates[i], membership(candidates[i]) ?? throw new InvalidOperationException($"Membership of marker '{candidates[i].Id}' returned null."));
        return Array.AsReadOnly(result);
    }

    static IReadOnlyList<MapEffectiveMarker> Lookup(Dictionary<string, Views> index, string key, bool enabledOnly)
    {
        ArgumentNullException.ThrowIfNull(key);
        return index.TryGetValue(key, out Views? views) ? enabledOnly ? views.Enabled : views.All : None;
    }

    // Inputs arrive in ID order, so each grouped list keeps it.
    static Dictionary<string, Views> Index(IEnumerable<(string Key, MapEffectiveMarker Marker)> pairs) =>
        pairs.GroupBy(p => p.Key, p => p.Marker, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new Views(g.ToArray()), StringComparer.Ordinal);

    sealed class Views(MapEffectiveMarker[] markers)
    {
        internal IReadOnlyList<MapEffectiveMarker> All { get; } = Array.AsReadOnly(markers);
        internal IReadOnlyList<MapEffectiveMarker> Enabled { get; } = Array.AsReadOnly(markers.Where(m => m.Enabled).ToArray());
    }
}
