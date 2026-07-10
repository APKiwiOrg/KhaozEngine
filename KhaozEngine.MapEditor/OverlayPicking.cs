using System;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>GPU-free containment pick over the editor's ground overlays: it maps a terrain (x, z) point to the
/// exclusion / region / feature marker under it, so Select mode can pick those otherwise-unpickable authoring
/// shapes with the mouse. Exclusions and regions test the point against their <see cref="IArea2D"/>
/// (<see cref="MapShapeDoc.ToArea"/>); a feature is a small disc of <see cref="FeatureMarkerRadius"/> at its
/// center, matching the marker the viewport draws so the pickable region and the drawn marker never drift.
/// <para>Priority is primary: a feature under the point beats an exclusion beats a region even when the point also
/// lies inside the lower-priority shapes (features, then exclusions, then regions). Within one category the shape
/// whose center is nearest the point wins, a deterministic tiebreak for overlapping same-category shapes. The
/// returned id matches the outline keys: a feature / exclusion list index rendered as a string, or a region
/// name.</para></summary>
internal static class OverlayPicking
{
    /// <summary>Pick disc radius (m) around a terrain feature's center, matching the drawn feature marker.</summary>
    internal const float FeatureMarkerRadius = 1.5f;

    /// <summary>One overlay pick outcome: the selected element's <paramref name="Kind"/> and its outline id, or
    /// <see cref="SelectionKind.None"/> with an empty id when the point lies over no overlay.</summary>
    internal readonly record struct OverlayPickResult(SelectionKind Kind, string Id);

    /// <summary>Picks the overlay shape under a terrain point, honouring the feature &gt; exclusion &gt; region
    /// priority (primary) with a nearest-center tiebreak within a category. Returns false (and a
    /// <see cref="SelectionKind.None"/> result) when the point lies over nothing.</summary>
    internal static bool Pick(MapDocument doc, float x, float z, out OverlayPickResult result)
    {
        ArgumentNullException.ThrowIfNull(doc);

        if (TryFeature(doc, x, z, out result)) return true;
        if (TryExclusion(doc, x, z, out result)) return true;
        if (TryRegion(doc, x, z, out result)) return true;
        result = new OverlayPickResult(SelectionKind.None, "");
        return false;
    }

    // Nearest feature whose marker disc (radius FeatureMarkerRadius) contains the point, by center distance.
    static bool TryFeature(MapDocument doc, float x, float z, out OverlayPickResult result)
    {
        const float r2 = FeatureMarkerRadius * FeatureMarkerRadius;
        int best = -1;
        float bestDist = 0f;
        for (int i = 0; i < doc.Terrain.Features.Count; i++)
        {
            if (!FeatureGeometry.TryCenter(doc.Terrain.Features[i], out float cx, out float cz)) continue;
            float dx = x - cx, dz = z - cz, d = dx * dx + dz * dz;
            if (d <= r2 && (best < 0 || d < bestDist)) { best = i; bestDist = d; }
        }
        if (best < 0) { result = default; return false; }
        result = new OverlayPickResult(SelectionKind.Feature, best.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    // Nearest exclusion whose shape area contains the point, by shape-center distance.
    static bool TryExclusion(MapDocument doc, float x, float z, out OverlayPickResult result)
    {
        int best = -1;
        float bestDist = 0f;
        for (int i = 0; i < doc.Exclusions.Count; i++)
        {
            if (!Contains(doc.Exclusions[i].Shape, x, z, out float d)) continue;
            if (best < 0 || d < bestDist) { best = i; bestDist = d; }
        }
        if (best < 0) { result = default; return false; }
        result = new OverlayPickResult(SelectionKind.Exclusion, best.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    // Nearest region whose shape area contains the point, by shape-center distance.
    static bool TryRegion(MapDocument doc, float x, float z, out OverlayPickResult result)
    {
        MapRegion? best = null;
        float bestDist = 0f;
        foreach (MapRegion region in doc.Regions)
        {
            if (!Contains(region.Shape, x, z, out float d)) continue;
            if (best is null || d < bestDist) { best = region; bestDist = d; }
        }
        if (best is null) { result = default; return false; }
        result = new OverlayPickResult(SelectionKind.Region, best.Name);
        return true;
    }

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
