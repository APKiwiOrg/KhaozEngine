using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>Freezes a scatter layer's procedural output over a rectangular region into authored placements. On
/// <see cref="Apply"/> it runs <see cref="PropScatter.Generate"/> over the region with the layer's
/// built <see cref="ScatterConfig"/> against the document-built <see cref="TerrainField"/>, converts each result
/// to an authored <see cref="MapPlacement"/> (id <c>baked-&lt;layer&gt;-N</c> made unique against every existing
/// placement id, kind from the scatter placement's kit id, an explicit frozen Y, and a <c>baked</c> tag), and adds
/// a rect exclusion covering the region filtered to that layer so the frozen props are not re-scattered on top of
/// themselves. Affects the streamed world (an exclusion changes scatter inputs).
///
/// <para>The generated placements and the exclusion are captured on the FIRST apply and reused on every redo, so
/// an Apply/Revert/Apply cycle is byte-identical. <see cref="PropScatter"/> is deterministic given field + config +
/// region, but an earlier command replayed before this one on redo could have changed the field, so capturing on
/// first apply is the safe contract rather than regenerating.</para></summary>
public sealed class BakeRegionCommand : EditorCommand
{
    readonly RectArea _region;
    readonly string _layerName;
    readonly MapDocRegistry? _registry;

    // Captured on the first Apply, then reused verbatim on every redo (never regenerated).
    List<MapPlacement>? _baked;
    MapExclusion? _exclusion;

    /// <summary>Creates the command baking scatter layer <paramref name="layerName"/> over <paramref name="region"/>.
    /// <paramref name="registry"/> interprets the document's terrain features when building the field to sample
    /// against; null uses <see cref="MapDocRegistry.CreateDefault"/> (the editor default).</summary>
    public BakeRegionCommand(RectArea region, string layerName, MapDocRegistry? registry = null)
    {
        _region = region;
        _layerName = layerName ?? throw new ArgumentNullException(nameof(layerName));
        _registry = registry;
    }

    /// <inheritdoc/>
    public override string Label => "Bake region";

    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_baked is null) Capture(doc);
        foreach (MapPlacement p in _baked!) doc.Placements.Add(p);
        doc.Exclusions.Add(_exclusion!);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_baked is null || _exclusion is null)
            throw new InvalidOperationException("Revert called before Apply.");
        doc.Exclusions.Remove(_exclusion);
        foreach (MapPlacement p in _baked) doc.Placements.Remove(p);
    }

    // Generates the scatter over the region against the pre-bake document (before the exclusion is added), converts
    // it to authored placements with document-unique ids, and builds the covering exclusion. Runs once.
    void Capture(MapDocument doc)
    {
        MapDocRegistry registry = _registry ?? MapDocRegistry.CreateDefault();
        TerrainField field = MapRuntime.BuildField(doc, registry);
        ScatterConfig config = MapRuntime.BuildScatterConfig(doc, _layerName);
        IReadOnlyList<PropPlacement> scatter = PropScatter.Generate(field, config, _region);

        // Every id already in the document is taken; each id we assign is added so a batch never collides with
        // itself or with a prior bake's baked- ids.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapPlacement p in doc.Placements) taken.Add(p.Id);

        var baked = new List<MapPlacement>(scatter.Count);
        int n = 1;
        foreach (PropPlacement s in scatter)
        {
            string id;
            do { id = "baked-" + _layerName + "-" + n.ToString(CultureInfo.InvariantCulture); n++; }
            while (!taken.Add(id));

            baked.Add(new MapPlacement
            {
                Id = id,
                Kind = s.Id,
                X = s.X,
                Z = s.Z,
                Y = s.Y,          // explicit: the frozen ground height, so a later re-snap cannot drift it
                Yaw = s.Yaw,
                Scale = s.Scale,
                Tags = { "baked" },
            });
        }

        _baked = baked;
        _exclusion = new MapExclusion
        {
            Shape = new RectShapeDoc { MinX = _region.MinX, MinZ = _region.MinZ, MaxX = _region.MaxX, MaxZ = _region.MaxZ },
            Layers = new List<string> { _layerName },
        };
    }
}
