using System;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>GPU-free containment pick over the editor's ground overlays: it maps a terrain (x, z) point to the
/// exclusion / scatter override / region / feature marker under it, so Select mode can pick those
/// otherwise-unpickable authoring shapes with the mouse. Exclusions, scatter overrides, and regions test the point
/// against their <see cref="IArea2D"/> (<see cref="MapShapeDoc.ToArea"/>), and a feature is a small disc of
/// <see cref="FeatureMarkerRadius"/> at its center, matching the marker the viewport draws so the pickable region
/// and the drawn marker never drift.
/// <para>Priority is primary: a feature under the point beats an exclusion beats a scatter override beats a region
/// even when the point also lies inside the lower-priority shapes. The scatter override sits between exclusion and
/// region because overrides are rarer and usually larger than exclusions (so the more specific exclusion should win
/// where they overlap) yet more specific than a broad gameplay region (so the override should win over the region
/// it sits inside). Within one category the shape whose center is nearest the point wins, a deterministic tiebreak
/// for overlapping same-category shapes. The returned id matches the outline keys: a feature / exclusion / scatter
/// override list index rendered as a string, or a region name.</para></summary>
internal static class OverlayPicking
{
    /// <summary>Pick disc radius (m) around a terrain feature's center, matching the drawn feature marker.</summary>
    internal const float FeatureMarkerRadius = 1.5f;

    /// <summary>One overlay pick outcome: the selected element's <paramref name="Kind"/> and its outline id, or
    /// <see cref="SelectionKind.None"/> with an empty id when the point lies over no overlay.</summary>
    internal readonly record struct OverlayPickResult(SelectionKind Kind, string Id);

    /// <summary>Picks the overlay shape under a terrain point, honouring the feature &gt; exclusion &gt; scatter
    /// override &gt; region priority (primary) with a nearest-center tiebreak within a category. Returns false (and a
    /// <see cref="SelectionKind.None"/> result) when the point lies over nothing.
    /// <para><paramref name="visible"/>, when supplied, filters out unpickable overlays: a feature / exclusion /
    /// scatter override / region for which <c>visible(kind, id)</c> is false is skipped (the editor hides it, so a
    /// hidden overlay is not selectable by clicking, though the outline still selects it). Null means every overlay
    /// is pickable.</para></summary>
    internal static bool Pick(MapDocument doc, float x, float z, out OverlayPickResult result,
        Func<SelectionKind, string, bool>? visible = null)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (TryFeature(doc, x, z, visible, out result)) return true;
        if (TryExclusion(doc, x, z, visible, out result)) return true;
        if (TryScatterOverride(doc, x, z, visible, out result)) return true;
        if (TryRegion(doc, x, z, visible, out result)) return true;
        result = new OverlayPickResult(SelectionKind.None, "");
        return false;
    }

    // Nearest feature whose marker disc (radius FeatureMarkerRadius) contains the point, by center distance.
    static bool TryFeature(MapDocument doc, float x, float z, Func<SelectionKind, string, bool>? visible,
        out OverlayPickResult result)
    {
        const float r2 = FeatureMarkerRadius * FeatureMarkerRadius;
        int best = -1;
        float bestDist = 0f;
        for (int i = 0; i < doc.Terrain.Features.Count; i++)
        {
            if (!Pickable(visible, SelectionKind.Feature, i)) continue;
            if (!FeatureGeometry.TryCenter(doc.Terrain.Features[i], out float cx, out float cz)) continue;
            float dx = x - cx, dz = z - cz, d = dx * dx + dz * dz;
            if (d <= r2 && (best < 0 || d < bestDist)) { best = i; bestDist = d; }
        }
        if (best < 0) { result = default; return false; }
        result = new OverlayPickResult(SelectionKind.Feature, best.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    // Nearest exclusion whose shape area contains the point, by shape-center distance.
    static bool TryExclusion(MapDocument doc, float x, float z, Func<SelectionKind, string, bool>? visible,
        out OverlayPickResult result)
    {
        int best = -1;
        float bestDist = 0f;
        for (int i = 0; i < doc.Exclusions.Count; i++)
        {
            if (!Pickable(visible, SelectionKind.Exclusion, i)) continue;
            if (!Contains(doc.Exclusions[i].Shape, x, z, out float d)) continue;
            if (best < 0 || d < bestDist) { best = i; bestDist = d; }
        }
        if (best < 0) { result = default; return false; }
        result = new OverlayPickResult(SelectionKind.Exclusion, best.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    // Nearest scatter override whose shape area contains the point, by shape-center distance (mirrors TryExclusion).
    static bool TryScatterOverride(MapDocument doc, float x, float z, Func<SelectionKind, string, bool>? visible,
        out OverlayPickResult result)
    {
        int best = -1;
        float bestDist = 0f;
        for (int i = 0; i < doc.ScatterOverrides.Count; i++)
        {
            if (!Pickable(visible, SelectionKind.ScatterOverride, i)) continue;
            if (!Contains(doc.ScatterOverrides[i].Shape, x, z, out float d)) continue;
            if (best < 0 || d < bestDist) { best = i; bestDist = d; }
        }
        if (best < 0) { result = default; return false; }
        result = new OverlayPickResult(SelectionKind.ScatterOverride, best.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    // Nearest region whose shape area contains the point, by shape-center distance.
    static bool TryRegion(MapDocument doc, float x, float z, Func<SelectionKind, string, bool>? visible,
        out OverlayPickResult result)
    {
        MapRegion? best = null;
        float bestDist = 0f;
        foreach (MapRegion region in doc.Regions)
        {
            if (visible is not null && !visible(SelectionKind.Region, region.Name)) continue;
            if (!Contains(region.Shape, x, z, out float d)) continue;
            if (best is null || d < bestDist) { best = region; bestDist = d; }
        }
        if (best is null) { result = default; return false; }
        result = new OverlayPickResult(SelectionKind.Region, best.Name);
        return true;
    }

    // Whether an index-keyed overlay (feature / exclusion) is pickable under the optional visibility filter.
    static bool Pickable(Func<SelectionKind, string, bool>? visible, SelectionKind kind, int index) =>
        visible is null || visible(kind, index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // True when a shape's XZ area contains the point, out its squared center distance for the nearest tiebreak. A
    // null shape or a shape with no derivable center contains nothing.
    static bool Contains(MapShapeDoc? shape, float x, float z, out float centerDistSq)
    {
        centerDistSq = 0f;
        if (shape is null || !shape.ToArea().Contains(x, z)) return false;
        if (ShapeGeometry.TryCenter(shape, out float cx, out float cz))
        {
            float dx = x - cx, dz = z - cz;
            centerDistSq = dx * dx + dz * dz;
        }
        return true;
    }
}
