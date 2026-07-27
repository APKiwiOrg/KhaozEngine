using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.MapDoc;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapEditor;

/// <summary>The viewport's authoring overlays: the translucent ground fills that make an otherwise-invisible
/// exclusion, scatter override, region, terrain feature, or biome-band edge findable while editing. The
/// document-to-draw-list step is the pure, headless-tested <see cref="MapEditorScene.ComputeOverlayDrawList"/>,
/// and only the per-entry GPU submission lives in <see cref="MapEditorScene.DrawOverlays"/>.</summary>
public partial class MapEditorScene
{
    // Viewport overlay fills: a translucent ground disc/rect/fan per exclusion (red-ish), scatter override (orange),
    // and region (blue-ish), and a small marker disc at each terrain-feature center (amber). The selected element's
    // fill brightens (see Tint). The scatter override orange sits between the exclusion red and the feature amber but
    // stays clearly distinct from both (a lower green than the amber marker, a warmer hue than the red exclusion).
    static readonly Color ExclusionOverlayColor = new(0.9f, 0.22f, 0.16f, 0.26f);
    static readonly Color ScatterOverrideOverlayColor = new(0.98f, 0.52f, 0.1f, 0.28f);
    static readonly Color RegionOverlayColor = new(0.2f, 0.5f, 0.95f, 0.26f);
    static readonly Color FeatureOverlayColor = new(0.96f, 0.76f, 0.22f, 0.55f);
    // The selected biome band's world-Z edge lines: a bright magenta, distinct from every fill hue above (its blue
    // is high like the region's, but its red is far higher, so it never reads as a region). A thin line reads
    // faint, so the alpha runs higher than the translucent area fills.
    static readonly Color BiomeBandOverlayColor = new(0.85f, 0.3f, 0.95f, 0.6f);
    /// <summary>Half-thickness (m, along world Z) of a biome-band edge line drawn as a thin overlay quad. Wide
    /// enough to read as a line at typical camera distances, far narrower than the band widths it delimits.</summary>
    const float BiomeBandLineHalfDepth = 0.4f;
    /// <summary>World-space lift (m) added above the sampled ground height when seating an overlay fill. Overlays
    /// never z-fight the terrain regardless: the debug-fill pass runs depth-disabled after post, so the fills
    /// composite on top of the scene rather than depth-testing against it. The lift only keeps the fill geometry a
    /// touch above the sampled surface.</summary>
    const float OverlayLift = 0.1f;
    /// <summary>RGB scale applied to a selected overlay's fill so it reads brighter than its neighbours.</summary>
    const float OverlaySelectBrighten = 1.6f;
    /// <summary>Alpha multiplier applied to a selected overlay's fill (clamped to 1) so it also firms up.</summary>
    const float OverlaySelectAlphaBoost = 1.7f;

    // This scene's overlay draw buffer, cleared and refilled by ComputeOverlayDrawList once per DrawOverlays
    // call (the TreeView.VisibleRows per-instance precedent): a per-call List<T> would litter Gen0 with a
    // per-frame allocation for a value nobody keeps past that same frame's GPU submission.
    readonly List<OverlayDraw> _overlayDrawList = new();

    /// <summary>Submits the exclusion / region / feature overlay fills to the Scene3D debug-fill pass. The
    /// doc-to-draw-list step is the pure, headless-tested <see cref="ComputeOverlayDrawList"/>. Only the per-entry
    /// GPU submission (a debug disc / quad / fan) lives here. No-op until the field exists (world built).</summary>
    void DrawOverlays(Scene3D scene)
    {
        if (_controller.Field is not { } field) return;
        foreach (OverlayDraw o in ComputeOverlayDrawList(
                     _document.Doc, _document.Selection, field.SampleHeight, _options.ShowOverlays, _visibility,
                     _overlayDrawList))
        {
            switch (o.Shape)
            {
                case OverlayShape.Disc:
                    scene.DebugFilledCircle(o.Center, Vector3.UnitY, o.Radius, o.Color);
                    break;
                case OverlayShape.Rect:
                    scene.DebugFilledQuad(o.Center, o.HalfExtents, o.Color);
                    break;
                case OverlayShape.Polygon:
                    if (o.Rim is { Count: >= 3 } rim) scene.DebugFilledFan(o.Center, rim, o.Color);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>Turns the document's exclusions, scatter overrides, regions, and terrain features into a flat list of
    /// ground-plane overlay fills: each authoring shape becomes a disc / rect / polygon fill (exclusions red-ish,
    /// scatter overrides orange, regions blue-ish) and each terrain feature a small amber marker disc at its center,
    /// all lifted a small epsilon above
    /// the sampled ground so they clear the terrain. The overlay whose element matches <paramref name="selection"/>
    /// is flagged and brightened. <paramref name="sampleHeight"/> supplies the ground height at an (x, z). A
    /// <c>null</c> shape, a polygon with fewer than three points, or a feature whose center cannot be derived (an
    /// unknown custom type) is skipped, and so is any element <paramref name="visibility"/> hides (its group is off
    /// or it is individually hidden), so a hidden overlay is not drawn. Leaves the buffer empty when
    /// <paramref name="showOverlays"/> is false. Pure over its inputs (no GPU, no scene state), so the whole
    /// computation is headless-testable. <see cref="DrawOverlays"/> submits the result untested.
    /// <para><paramref name="into"/> is the caller-owned result buffer (the <see cref="TreeView.VisibleRows"/>
    /// reuse pattern, with the buffer at the call site instead of behind the API): it is cleared at entry,
    /// filled, and returned, so a per-frame caller passes one long-lived list and pays no per-call allocation,
    /// while a caller that wants independent results simply passes a fresh list per call.</para></summary>
    internal static List<OverlayDraw> ComputeOverlayDrawList(
        MapDocument doc, EditorSelection selection, Func<float, float, float> sampleHeight, bool showOverlays,
        EditorVisibility visibility, List<OverlayDraw> into)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(sampleHeight);
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(into);

        List<OverlayDraw> list = into;
        list.Clear();
        if (!showOverlays) return list;

        int selectedExclusion = selection.Kind == SelectionKind.Exclusion ? SelectedIndex(selection.Id) : -1;
        int selectedScatterOverride = selection.Kind == SelectionKind.ScatterOverride ? SelectedIndex(selection.Id) : -1;
        int selectedFeature = selection.Kind == SelectionKind.Feature ? SelectedIndex(selection.Id) : -1;

        for (int i = 0; i < doc.Exclusions.Count; i++)
        {
            if (!visibility.IsElementVisible(SelectionKind.Exclusion, Index(i))) continue;   // hidden: no overlay
            AddShapeOverlay(list, doc.Exclusions[i].Shape, OverlayCategory.Exclusion, ExclusionOverlayColor,
                selected: i == selectedExclusion, sampleHeight);
        }

        for (int i = 0; i < doc.ScatterOverrides.Count; i++)
        {
            if (!visibility.IsElementVisible(SelectionKind.ScatterOverride, Index(i))) continue;   // hidden: no overlay
            AddShapeOverlay(list, doc.ScatterOverrides[i].Shape, OverlayCategory.ScatterOverride, ScatterOverrideOverlayColor,
                selected: i == selectedScatterOverride, sampleHeight);
        }

        foreach (MapRegion region in doc.Regions)
        {
            if (!visibility.IsElementVisible(SelectionKind.Region, region.Name)) continue;   // hidden: no overlay
            bool selected = selection.Kind == SelectionKind.Region &&
                            string.Equals(selection.Id, region.Name, StringComparison.Ordinal);
            AddShapeOverlay(list, region.Shape, OverlayCategory.Region, RegionOverlayColor, selected, sampleHeight);
        }

        IReadOnlyList<MapFeature> features = doc.Terrain.Features;
        for (int i = 0; i < features.Count; i++)
        {
            if (!visibility.IsElementVisible(SelectionKind.Feature, Index(i))) continue;   // hidden: no marker
            if (!FeatureGeometry.TryCenter(features[i], out float fx, out float fz)) continue;   // unknown type: no marker
            bool selected = i == selectedFeature;
            var center = new Vector3(fx, sampleHeight(fx, fz) + OverlayLift, fz);
            list.Add(new OverlayDraw(OverlayCategory.Feature, OverlayShape.Disc, center,
                OverlayPicking.FeatureMarkerRadius, Vector2.Zero, rim: null, Tint(FeatureOverlayColor, selected), selected));
        }

        // The selected biome band's finite Start/End edges, as full-width ground lines across the doc's X extent at
        // those world-Z positions (a band is a world-Z slice, not a placed shape - see TerrainField.ShapeAt, which
        // blends bands by z only). A band carries no viewport geometry of its own and its order is meaningless, so
        // ONLY the current selection draws, and an open edge (null Start/End) draws nothing. Not gated on the
        // visibility system: bands have no visibility toggle (they are outline-only, never independently drawn).
        if (selection.Kind == SelectionKind.BiomeBand)
        {
            int selectedBand = SelectedIndex(selection.Id);
            List<MapBiomeBand> bands = doc.Terrain.Biomes;
            if (selectedBand >= 0 && selectedBand < bands.Count)
            {
                MapBiomeBand band = bands[selectedBand];
                AddBandEdgeLine(list, doc.Bounds, band.Start, sampleHeight);
                AddBandEdgeLine(list, doc.Bounds, band.End, sampleHeight);
            }
        }
        return list;
    }

    // One finite biome-band edge as a full-width ground line across the doc's X extent at world-Z `edge`. A null or
    // infinite edge (an open, unbounded band edge) draws nothing. The line is a thin rect quad centered on the doc's
    // X midpoint, seated at the ground height sampled there (a thin line needs one sample, like a feature marker).
    static void AddBandEdgeLine(List<OverlayDraw> list, MapBounds bounds, float? edge, Func<float, float, float> sampleHeight)
    {
        if (edge is not { } z || float.IsInfinity(z)) return;
        float cx = (bounds.MinX + bounds.MaxX) * 0.5f;
        float halfWidth = MathF.Abs(bounds.MaxX - bounds.MinX) * 0.5f;
        var center = new Vector3(cx, sampleHeight(cx, z) + OverlayLift, z);
        var half = new Vector2(halfWidth, BiomeBandLineHalfDepth);
        // Always the current selection, so the base color is drawn directly (no Tint pass): there is no unselected
        // band line to contrast against.
        list.Add(new OverlayDraw(OverlayCategory.BiomeBand, OverlayShape.Rect, center, 0f, half,
            rim: null, BiomeBandOverlayColor, selected: true));
    }

    // An index-keyed element id (feature / exclusion), matching the selection and outline id encoding.
    static string Index(int i) => i.ToString(CultureInfo.InvariantCulture);

    // Turn one authoring shape into its overlay fill, at ground height plus the lift epsilon. Disc -> a ground disc,
    // rect -> a ground quad at the rect's midpoint, polygon (>= 3 points) -> a fan from the point centroid with each
    // rim vertex sampled at its own ground height. A null shape or a degenerate polygon adds nothing.
    static void AddShapeOverlay(List<OverlayDraw> list, MapShapeDoc? shape, OverlayCategory category,
        Color baseColor, bool selected, Func<float, float, float> sampleHeight)
    {
        Color color = Tint(baseColor, selected);
        switch (shape)
        {
            case DiscShapeDoc d:
            {
                var center = new Vector3(d.CenterX, sampleHeight(d.CenterX, d.CenterZ) + OverlayLift, d.CenterZ);
                list.Add(new OverlayDraw(category, OverlayShape.Disc, center, d.Radius,
                    Vector2.Zero, rim: null, color, selected));
                break;
            }
            case RectShapeDoc r:
            {
                float cx = (r.MinX + r.MaxX) * 0.5f, cz = (r.MinZ + r.MaxZ) * 0.5f;
                var center = new Vector3(cx, sampleHeight(cx, cz) + OverlayLift, cz);
                var half = new Vector2(MathF.Abs(r.MaxX - r.MinX) * 0.5f, MathF.Abs(r.MaxZ - r.MinZ) * 0.5f);
                list.Add(new OverlayDraw(category, OverlayShape.Rect, center, 0f, half, rim: null, color, selected));
                break;
            }
            case PolygonShapeDoc p when p.Points.Count >= 3:
            {
                var rim = new List<Vector3>(p.Points.Count);
                float sx = 0f, sz = 0f;
                foreach (float[] pt in p.Points)
                {
                    float px = pt.Length > 0 ? pt[0] : 0f, pz = pt.Length > 1 ? pt[1] : 0f;
                    sx += px; sz += pz;
                    rim.Add(new Vector3(px, sampleHeight(px, pz) + OverlayLift, pz));
                }
                float cx = sx / p.Points.Count, cz = sz / p.Points.Count;
                var center = new Vector3(cx, sampleHeight(cx, cz) + OverlayLift, cz);
                list.Add(new OverlayDraw(category, OverlayShape.Polygon, center, 0f, Vector2.Zero, rim, color, selected));
                break;
            }
            default:
                break;   // null shape or a polygon with fewer than three points: no overlay
        }
    }

    // A selected overlay reads brighter: scale RGB up (clamped at 1.0, alpha preserved) then firm up that alpha,
    // so the highlighted shape stands out against its unselected neighbours without an unclamped channel
    // overshooting past white. Unselected returns the base color.
    static Color Tint(Color baseColor, bool selected) => selected
        ? baseColor.ScaleRgbClamped(OverlaySelectBrighten).WithAlpha(MathF.Min(1f, baseColor.A * OverlaySelectAlphaBoost))
        : baseColor;

    // The list index a feature / exclusion selection id encodes, or -1 when it is not a valid non-negative index.
    static int SelectedIndex(string id) =>
        int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ? index : -1;
}

/// <summary>Which document collection a viewport <see cref="OverlayDraw"/> came from. Drives its base fill color
/// (exclusions red-ish, scatter overrides orange, regions blue-ish, features amber).</summary>
internal enum OverlayCategory
{
    /// <summary>A scatter exclusion shape.</summary>
    Exclusion,
    /// <summary>A scatter override shape (a region-scoped density / kind tweak).</summary>
    ScatterOverride,
    /// <summary>A named, game-interpreted region shape.</summary>
    Region,
    /// <summary>A terrain feature's center marker.</summary>
    Feature,
    /// <summary>A biome band's world-Z edge line (a full-width line at Start or End).</summary>
    BiomeBand,
}

/// <summary>Which <see cref="Scene3D"/> debug-fill primitive draws an <see cref="OverlayDraw"/>.</summary>
internal enum OverlayShape
{
    /// <summary>A flat ground disc (<see cref="Scene3D.DebugFilledCircle"/>).</summary>
    Disc,
    /// <summary>A flat ground quad (<see cref="Scene3D.DebugFilledQuad(System.Numerics.Vector3, System.Numerics.Vector2, Color)"/>).</summary>
    Rect,
    /// <summary>A flat ground triangle fan (<see cref="Scene3D.DebugFilledFan"/>).</summary>
    Polygon,
}

/// <summary>One computed viewport overlay fill that makes an exclusion, region, or terrain feature visible: a
/// ground-plane translucent shape lifted a small epsilon above the terrain. A pure value produced by
/// <see cref="MapEditorScene.ComputeOverlayDrawList"/> and submitted to <see cref="Scene3D"/> untested, so the
/// doc-to-draw-list computation is fully headless-testable.</summary>
internal readonly struct OverlayDraw
{
    /// <summary>Which document collection this overlay came from (drives the base color).</summary>
    public readonly OverlayCategory Category;
    /// <summary>Which debug-fill primitive draws it.</summary>
    public readonly OverlayShape Shape;
    /// <summary>The fill center in world space, already lifted the overlay epsilon above the sampled ground. For a
    /// <see cref="OverlayShape.Polygon"/> this is the fan hub at the point centroid.</summary>
    public readonly Vector3 Center;
    /// <summary>The radius for a <see cref="OverlayShape.Disc"/> (the shape radius, or the fixed marker radius for a
    /// feature), and zero for the other shapes.</summary>
    public readonly float Radius;
    /// <summary>The half-extents (X along world X, Y along world Z) for a <see cref="OverlayShape.Rect"/>, and zero for
    /// the other shapes.</summary>
    public readonly Vector2 HalfExtents;
    /// <summary>The ground-height rim ring for a <see cref="OverlayShape.Polygon"/> (each vertex sampled at its own
    /// terrain height), and null for the other shapes.</summary>
    public readonly IReadOnlyList<Vector3>? Rim;
    /// <summary>The RGBA fill color, already brightened when <see cref="Selected"/>.</summary>
    public readonly Color Color;
    /// <summary>True when this overlay's element is the current selection, so it is drawn brighter.</summary>
    public readonly bool Selected;

    /// <summary>Creates an overlay-draw record from its already-computed fields.</summary>
    public OverlayDraw(OverlayCategory category, OverlayShape shape, Vector3 center, float radius,
        Vector2 halfExtents, IReadOnlyList<Vector3>? rim, Color color, bool selected)
    {
        Category = category;
        Shape = shape;
        Center = center;
        Radius = radius;
        HalfExtents = halfExtents;
        Rim = rim;
        Color = color;
        Selected = selected;
    }
}
