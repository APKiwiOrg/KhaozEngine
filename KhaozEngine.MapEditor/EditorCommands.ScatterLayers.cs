using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

// ---- scatter layers (terrain-scatter affecting) ----------------------------------------------------------

/// <summary>Appends a named procedural scatter layer. Layer names are unique-required (the validator), so
/// <see cref="Apply"/> rejects a duplicate name before it mutates anything (the add-guard idiom shared with
/// companion layers and regions), leaving no undo step on a reject. Scatter layers feed the streamed prop
/// field, so this affects the world.</summary>
public sealed class AddScatterLayerCommand : EditorCommand
{
    readonly MapScatterLayer _layer;

    /// <summary>Creates the command for the given scatter layer (added on <see cref="Apply"/>).</summary>
    public AddScatterLayerCommand(MapScatterLayer layer) =>
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));

    /// <inheritdoc/>
    public override string Label => "Add scatter layer";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        GuardNoScatterLayerName(doc, _layer.Name, -1);   // reject a duplicate name before touching the list
        ApplyAppend(doc.ScatterLayers, _layer);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => RevertAppend(doc.ScatterLayers);
}

/// <summary>Removes the scatter layer with the given name, restoring it at its original index on revert. A
/// scatter layer that a companion layer hosts, or that an exclusion / scatter-override layer filter names,
/// cannot be removed without orphaning those references (the standard validator's unknown-layer rules would
/// reject the resulting document): so this command REJECTS a referenced removal in <see cref="Apply"/> BEFORE it
/// mutates anything, throwing an <see cref="InvalidOperationException"/> that lists every referencing element
/// (validator-consistent wording). The editor surfaces that message rather than the operator saving a broken
/// document later. The reject-before-mutate order means a rejected removal lands no undo step and leaves the
/// document byte-for-byte unchanged (the guard idiom). Renaming, by contrast, CASCADES its references
/// (<see cref="RenameScatterLayerCommand"/>), so renames stay lossless while removals stay safe. Affects the
/// streamed world.</summary>
public sealed class RemoveScatterLayerCommand : EditorCommand
{
    readonly string _name;
    MapScatterLayer? _removed;
    int _index = -1;

    /// <summary>Creates the command for the scatter layer name to remove.</summary>
    public RemoveScatterLayerCommand(string name) =>
        _name = name ?? throw new ArgumentNullException(nameof(name));

    /// <inheritdoc/>
    public override string Label => "Remove scatter layer";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        List<MapEditorScatterLayerReference> refs = ScatterLayerReferences(doc, _name);
        if (refs.Count > 0)
            throw new MapEditorCommandException(MapDocumentRefusal.EditorScatterLayerReferenced,
                $"Cannot remove scatter layer '{_name}': it is still referenced by {string.Join(", ", refs.Select(r => r.Description))}. Retarget or remove those first.")
            {
                DocumentId = doc.Id,
                SubjectKind = "scatter layer",
                EntityId = _name,
                LayerId = _name,
                FieldPath = "ScatterLayers",
                SourceIds = refs.Select(r => r.SourceId).ToArray()
            };
        _index = IndexOfScatterLayer(doc, _name);
        _removed = doc.ScatterLayers[_index];
        doc.ScatterLayers.RemoveAt(_index);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (_removed is null) throw new InvalidOperationException("Revert called before Apply.");
        doc.ScatterLayers.Insert(_index, _removed);
    }
}

/// <summary>Replaces the scatter layer with a given name with a new whole value (the <see cref="EditFeatureCommand"/>
/// idiom, extended to a class with nested lists). The caller supplies a DEEP clone with the changed field plus the
/// live value for revert, so the command holds two independent instances and neither aliases the document's other
/// (nested Rules / Kinds must be copied, not shared, else a scrub of the clone would mutate the captured old value).
/// The Name is NOT edited through here (it is the lookup key and stays fixed): a rename goes through
/// <see cref="RenameScatterLayerCommand"/>. Successive edits of the same-named layer coalesce into one undo step
/// (scrub coalescing). Affects the streamed world (scatter inputs change).</summary>
public sealed partial class EditScatterLayerCommand : EditorCommand
{
    readonly string _name;
    MapScatterLayer _newValue;
    readonly MapScatterLayer _oldValue;

    /// <summary>Creates the command replacing scatter layer <paramref name="name"/> with <paramref name="newValue"/>,
    /// capturing <paramref name="oldValue"/> for revert. Both must carry the same Name as <paramref name="name"/>
    /// (the edit never renames), so the same-name lookup still resolves after apply.</summary>
    public EditScatterLayerCommand(string name, MapScatterLayer newValue, MapScatterLayer oldValue)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _newValue = newValue ?? throw new ArgumentNullException(nameof(newValue));
        _oldValue = oldValue ?? throw new ArgumentNullException(nameof(oldValue));
    }

    /// <inheritdoc/>
    public override string Label => "Edit scatter layer";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.ScatterLayers[IndexOfScatterLayer(doc, _name)] = _newValue;

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.ScatterLayers[IndexOfScatterLayer(doc, _name)] = _oldValue;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is EditScatterLayerCommand e && string.Equals(e._name, _name, StringComparison.Ordinal))
        {
            _newValue = e._newValue;
            return true;
        }
        return false;
    }
}

/// <summary>Renames a scatter layer, CASCADING the rename through every element that references it (a companion
/// layer's HostLayer, and any exclusion / scatter-override explicit layer filter that names it), so the document
/// stays valid and no reference is silently orphaned. Cascading is chosen over rejecting a referenced rename
/// because it is friendly and lossless: the operator renames "trees" to "forest" and the companion that hosts it
/// follows automatically. <see cref="Revert"/> reverses the whole cascade (the layer and every reference move
/// back). Since a null "all layers" filter names no specific layer, it is left untouched by the cascade. The
/// target name must be unique among scatter layers: <see cref="Apply"/> throws (before it mutates) if another
/// scatter layer already carries the new name, so a rejected rename lands no undo step (the guard idiom). A
/// chained rename coalesces (the next command's old name matches this one's current new name, the same-name-pair
/// a per-keystroke commit produces), the <see cref="RenameRegionCommand"/> merge idiom. Renaming keeps the same
/// props streaming (references still resolve to the same layer), so it does not itself force a world rebuild:
/// the editor separately follows the rename with the visibility key remap (<see cref="EditorVisibility.RenameLayer"/>),
/// which is view-only and not part of the document.</summary>
public sealed class RenameScatterLayerCommand : EditorCommand
{
    readonly string _oldName;
    string _newName;

    /// <summary>Creates the command renaming scatter layer <paramref name="oldName"/> to <paramref name="newName"/>.</summary>
    public RenameScatterLayerCommand(string oldName, string newName)
    {
        _oldName = oldName ?? throw new ArgumentNullException(nameof(oldName));
        _newName = newName ?? throw new ArgumentNullException(nameof(newName));
    }

    /// <inheritdoc/>
    public override string Label => "Rename scatter layer";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        GuardNoScatterLayerName(doc, _newName, IndexOfScatterLayer(doc, _oldName));   // reject a duplicate target before the cascade mutates anything
        Retarget(doc, _oldName, _newName);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => Retarget(doc, _newName, _oldName);

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is RenameScatterLayerCommand r && string.Equals(r._oldName, _newName, StringComparison.Ordinal))
        {
            _newName = r._newName;
            return true;
        }
        return false;
    }

    // Renames the layer itself and every reference to it (companion host, explicit exclusion / override filters)
    // from `from` to `to`. Symmetric (its own inverse with the endpoints swapped), so Revert is Retarget reversed.
    static void Retarget(MapDocument doc, string from, string to)
    {
        doc.ScatterLayers[IndexOfScatterLayer(doc, from)].Name = to;
        foreach (MapCompanionLayer c in doc.CompanionLayers)
            if (string.Equals(c.HostLayer, from, StringComparison.Ordinal)) c.HostLayer = to;
        foreach (MapExclusion e in doc.Exclusions) ReplaceInLayers(e.Layers, from, to);
        foreach (MapScatterOverrideDoc o in doc.ScatterOverrides) ReplaceInLayers(o.Layers, from, to);
    }

    static void ReplaceInLayers(List<string>? layers, string from, string to)
    {
        if (layers is null) return;   // a null "all layers" filter names no specific layer
        for (int i = 0; i < layers.Count; i++)
            if (string.Equals(layers[i], from, StringComparison.Ordinal)) layers[i] = to;
    }
}
