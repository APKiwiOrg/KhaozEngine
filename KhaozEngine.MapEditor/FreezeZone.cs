using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>Freezes the WHOLE zone's procedural scatter into authored placements, then strips every procedural input
/// so the document becomes placements-only. On <see cref="Apply"/> it bakes every scatter layer AND every companion
/// layer across the document bounds into explicit <see cref="MapPlacement"/>s (frozen Y, tagged <c>baked</c> plus the
/// source layer name so the diff stays reviewable), and then REMOVES all scatter layers, companion layers,
/// exclusions, and scatter overrides. No covering exclusions are added: once no scatter layer survives there is
/// nothing left to re-scatter over the frozen props, so the exclusions the per-rect
/// <see cref="BakeRegionCommand"/> needs are unnecessary here. This is the terminal form of the bake: after it the
/// zone has no procedural generation at all.
///
/// <para>Parity with live generation is by construction: the bake reuses the exact runtime calls
/// (<see cref="MapRuntime.BuildScatterConfig"/> for each scatter layer and
/// <see cref="MapRuntime.BuildCompanionConfig"/> for each companion layer, fed to
/// <see cref="PropScatter.Generate"/> and <see cref="PropScatter.GenerateCompanions"/>), so the frozen props equal
/// what the streamed world produces for the same document. The document's exclusions and scatter overrides are
/// APPLIED during generation (they shape what exists, because <see cref="MapRuntime.BuildScatterConfig"/> attaches
/// them) and only removed afterwards. Companion layers ring their host scatter layer's placements, so they are baked
/// in the SAME pass from the same host generation, before the hosts are removed, exactly as the runtime rings hosts
/// per chunk (<c>Scene3DChunkSink</c>). Generation is deterministic and tiling-invariant, so the whole-bounds bake
/// equals the union of the runtime's per-chunk generation, and two freezes of the same document produce identical
/// placement lists (order included).</para>
///
/// <para>The generated placements and the removed collections are captured on the FIRST apply and reused verbatim on
/// every redo, so an Apply/Revert/Apply cycle is byte-identical (the <see cref="BakeRegionCommand"/> capture-once
/// contract, extended to the whole document). Undo restores the removed scatter layers, companion layers,
/// exclusions, and overrides exactly, and removes the added placements, so the document deep-equals its pre-freeze
/// state. Affects the streamed world (it changes generation inputs everywhere), so the viewport must rebuild the
/// whole world.</para></summary>
public sealed class FreezeZoneCommand : EditorCommand
{
    readonly MapDocRegistry? _registry;

    // Captured on the first Apply, then reused verbatim on every redo (never regenerated).
    List<MapPlacement>? _baked;

    // The four scatter-input collections detached on Apply, restored on Revert. Captured every Apply so a redo
    // re-detaches the freshly-restored collections and a revert puts the exact same list instances back.
    List<MapScatterLayer>? _removedScatterLayers;
    List<MapCompanionLayer>? _removedCompanionLayers;
    List<MapExclusion>? _removedExclusions;
    List<MapScatterOverrideDoc>? _removedOverrides;

    // Index the baked block was appended at (the placement count before appending). Re-derived every Apply, so
    // under LIFO undo it always matches the contiguous baked block Revert must remove.
    int _insertIndex;

    /// <summary>Creates the whole-zone freeze command. <paramref name="registry"/> interprets the document's terrain
    /// features when building the field to sample against, matching the editor default when null
    /// (<see cref="MapDocRegistry.CreateDefault"/>).</summary>
    public FreezeZoneCommand(MapDocRegistry? registry = null) => _registry = registry;

    /// <inheritdoc/>
    public override string Label => "Freeze zone";

    internal override bool AffectsWorld => true;

    /// <summary>True when <paramref name="doc"/> has any procedural scatter to freeze (at least one scatter layer or
    /// companion layer). When false the command is a no-op the caller should NOT execute, so it lands no phantom
    /// undo entry: a document with no scatter or companion layers is already in the placements-only terminal state
    /// the freeze produces. Exclusions or overrides with no scatter layer to bind to have no runtime effect, so they
    /// do not by themselves make the freeze worth running.</summary>
    public static bool HasWork(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return doc.ScatterLayers.Count > 0 || doc.CompanionLayers.Count > 0;
    }

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_baked is null) _baked = Bake(doc, _registry ?? MapDocRegistry.CreateDefault());

        // Detach the four scatter-input collections, leaving a placements-only document. The detached list
        // instances are held for an exact Revert (restoring them by reference deep-equals the pre-freeze content).
        _removedScatterLayers = doc.ScatterLayers;
        _removedCompanionLayers = doc.CompanionLayers;
        _removedExclusions = doc.Exclusions;
        _removedOverrides = doc.ScatterOverrides;
        doc.ScatterLayers = new List<MapScatterLayer>();
        doc.CompanionLayers = new List<MapCompanionLayer>();
        doc.Exclusions = new List<MapExclusion>();
        doc.ScatterOverrides = new List<MapScatterOverrideDoc>();

        _insertIndex = doc.Placements.Count;
        foreach (MapPlacement p in _baked) doc.Placements.Add(p);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_baked is null || _removedScatterLayers is null || _removedCompanionLayers is null
            || _removedExclusions is null || _removedOverrides is null)
            throw new InvalidOperationException("Revert called before Apply.");

        // The baked block is contiguous at the tail under LIFO undo (everything applied after this command is
        // already reverted), so a single range removal drops exactly the props this command added.
        doc.Placements.RemoveRange(_insertIndex, _baked.Count);
        doc.ScatterLayers = _removedScatterLayers;
        doc.CompanionLayers = _removedCompanionLayers;
        doc.Exclusions = _removedExclusions;
        doc.ScatterOverrides = _removedOverrides;
    }

    // Generates the frozen placements for the whole document, in a fixed deterministic order (every scatter layer in
    // document order, then every companion layer in document order, each in generation order). Reuses the runtime
    // generation calls, so the output equals live generation for the same document (exclusions and overrides shape
    // it, being attached by BuildScatterConfig, and companions ring their host layer's generated placements). Runs
    // against the pre-freeze document, before any collection is removed.
    static List<MapPlacement> Bake(MapDocument doc, MapDocRegistry registry)
    {
        TerrainField field = MapRuntime.BuildField(doc, registry);
        var area = new RectArea(doc.Bounds.MinX, doc.Bounds.MinZ, doc.Bounds.MaxX, doc.Bounds.MaxZ);

        // Every id already in the document is taken, and each id assigned is added, so a batch never collides with
        // itself, with a prior bake's baked- ids, or across the scatter and companion passes.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapPlacement p in doc.Placements) taken.Add(p.Id);

        var baked = new List<MapPlacement>();

        // Host scatter passes: generate each scatter layer over the whole bounds and freeze its placements, keeping
        // the generated host lists to ring companions from without re-generating (parity: same host set the runtime
        // rings per chunk, unioned).
        var hostsByLayer = new Dictionary<string, IReadOnlyList<PropPlacement>>(StringComparer.Ordinal);
        foreach (MapScatterLayer layer in doc.ScatterLayers)
        {
            ScatterConfig config = MapRuntime.BuildScatterConfig(doc, layer.Name);
            IReadOnlyList<PropPlacement> hosts = PropScatter.Generate(field, config, area);
            hostsByLayer[layer.Name] = hosts;
            AppendFrozen(baked, hosts, layer.Name, taken);
        }

        // Companion passes: ring each companion layer's host scatter layer's placements. A companion whose host is
        // not a declared scatter layer is an authoring error the runtime also rejects (Scene3DChunkSink /
        // ViewportWorld), so surface it the same way rather than silently baking nothing.
        foreach (MapCompanionLayer cl in doc.CompanionLayers)
        {
            if (!hostsByLayer.TryGetValue(cl.HostLayer, out IReadOnlyList<PropPlacement>? hosts))
                throw new MapDocumentException(
                    $"companion layer '{cl.Name}' names unknown host scatter layer '{cl.HostLayer}' in map '{doc.Id}'.");
            CompanionConfig config = MapRuntime.BuildCompanionConfig(doc, cl.Name);
            IReadOnlyList<PropPlacement> companions = PropScatter.GenerateCompanions(field, hosts, config);
            AppendFrozen(baked, companions, cl.Name, taken);
        }

        return baked;
    }

    // Converts a generated run into authored placements with document-unique ids (baked-<source>-N, N advancing past
    // any taken id), an explicit frozen Y so a later re-snap cannot drift it, the baked tag, and the source layer
    // name as a second tag so a reviewer can see which layer each frozen prop came from.
    static void AppendFrozen(List<MapPlacement> baked, IReadOnlyList<PropPlacement> generated, string source,
        HashSet<string> taken)
    {
        int n = 1;
        foreach (PropPlacement s in generated)
        {
            string id;
            do { id = "baked-" + source + "-" + n.ToString(CultureInfo.InvariantCulture); n++; }
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
                Tags = { "baked", source },
            });
        }
    }
}
