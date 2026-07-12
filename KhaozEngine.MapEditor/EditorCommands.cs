using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>One reversible edit to a <see cref="MapDocument"/>. Commands are the ONLY mutation path the editor
/// uses, so undo is total by construction.</summary>
public interface IEditorCommand
{
    /// <summary>A short human-readable label for the undo/redo menu (developer-facing tooling text).</summary>
    string Label { get; }

    /// <summary>Applies the edit to the document.</summary>
    void Apply(MapDocument doc);

    /// <summary>Reverses the edit, restoring the document to its pre-<see cref="Apply"/> state.</summary>
    void Revert(MapDocument doc);

    /// <summary>True when this command can absorb a newer one of the same gesture (drag coalescing): the pair
    /// collapses to one undo step. Default implementations return false.</summary>
    bool TryMerge(IEditorCommand next);
}

/// <summary>Shared base for the concrete editor commands. Carries the world-rebuild classification the
/// <see cref="EditorDocument"/> reads (<see cref="AffectsWorld"/>) and a no-merge default. Public so game heads
/// can script edits through the concrete commands, but the classification stays internal to the engine.</summary>
public abstract class EditorCommand : IEditorCommand
{
    /// <inheritdoc/>
    public abstract string Label { get; }

    /// <summary>True when applying or reverting this command changes terrain shape or scatter inputs, so the
    /// viewport must rebuild its streamed world. Placement/spawn/region edits are false.</summary>
    internal abstract bool AffectsWorld { get; }

    /// <inheritdoc/>
    public abstract void Apply(MapDocument doc);

    /// <inheritdoc/>
    public abstract void Revert(MapDocument doc);

    /// <inheritdoc/>
    public virtual bool TryMerge(IEditorCommand next) => false;

    private protected static MapPlacement FindPlacement(MapDocument doc, string id)
    {
        foreach (MapPlacement p in doc.Placements)
            if (string.Equals(p.Id, id, StringComparison.Ordinal))
                return p;
        throw new InvalidOperationException($"No placement with id '{id}' in the document.");
    }

    private protected static MapSpawn FindSpawn(MapDocument doc, string id)
    {
        foreach (MapSpawn s in doc.Spawns)
            if (string.Equals(s.Id, id, StringComparison.Ordinal))
                return s;
        throw new InvalidOperationException($"No spawn with id '{id}' in the document.");
    }

    private protected static int IndexOfRegion(MapDocument doc, string name)
    {
        for (int i = 0; i < doc.Regions.Count; i++)
            if (string.Equals(doc.Regions[i].Name, name, StringComparison.Ordinal))
                return i;
        throw new InvalidOperationException($"No region named '{name}' in the document.");
    }

    private protected static void GuardNoPlacement(MapDocument doc, string id)
    {
        foreach (MapPlacement p in doc.Placements)
            if (string.Equals(p.Id, id, StringComparison.Ordinal))
                throw new InvalidOperationException($"A placement with id '{id}' already exists in the document.");
    }

    private protected static void GuardNoSpawn(MapDocument doc, string id)
    {
        foreach (MapSpawn s in doc.Spawns)
            if (string.Equals(s.Id, id, StringComparison.Ordinal))
                throw new InvalidOperationException($"A spawn with id '{id}' already exists in the document.");
    }

    private protected static void GuardNoRegion(MapDocument doc, string name)
    {
        foreach (MapRegion r in doc.Regions)
            if (string.Equals(r.Name, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"A region named '{name}' already exists in the document.");
    }

    /// <summary>Rejects a duplicate terrain feature name before <see cref="RenameFeatureCommand.Apply"/> mutates
    /// anything. Features are index-addressed (see <see cref="RenameFeatureCommand"/>), so the scan excludes the
    /// renaming feature's own index (renaming to its current name, or the empty-clearing case with another
    /// unnamed feature already present, both stay legal). A null or empty name never collides since an unnamed
    /// feature carries no key to clash on.</summary>
    private protected static void GuardNoFeatureName(MapDocument doc, string? name, int exceptIndex)
    {
        if (string.IsNullOrEmpty(name)) return;
        List<MapFeature> features = doc.Terrain.Features;
        for (int i = 0; i < features.Count; i++)
            if (i != exceptIndex && string.Equals(features[i].Name, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"A terrain feature named '{name}' already exists in the document.");
    }

    /// <summary>Rejects a duplicate exclusion name before <see cref="RenameExclusionCommand.Apply"/> mutates
    /// anything. Exclusions are index-addressed (see <see cref="RenameExclusionCommand"/>), so the scan excludes
    /// the renaming exclusion's own index (renaming to its current name, or the empty-clearing case with another
    /// unnamed exclusion already present, both stay legal). A null or empty name never collides since an unnamed
    /// exclusion carries no key to clash on.</summary>
    private protected static void GuardNoExclusionName(MapDocument doc, string? name, int exceptIndex)
    {
        if (string.IsNullOrEmpty(name)) return;
        List<MapExclusion> exclusions = doc.Exclusions;
        for (int i = 0; i < exclusions.Count; i++)
            if (i != exceptIndex && string.Equals(exclusions[i].Name, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"An exclusion named '{name}' already exists in the document.");
    }

    /// <summary>Coerces an empty rename target to null: features and exclusions store an optional Name where
    /// empty means unnamed (<see cref="MapDoc.MapFeature.Name"/>, <see cref="MapExclusion.Name"/>), and the
    /// stored value must never be the empty string, only null or non-empty, so a clear-to-empty rename does not
    /// persist as a bloating empty name key.</summary>
    private protected static string? NormalizeName(string name) => string.IsNullOrEmpty(name) ? null : name;
}

// ---- placements ------------------------------------------------------------------------------------------

/// <summary>Appends an authored placement to the document. Absorbs a same-id <see cref="MovePlacementCommand"/> that
/// immediately follows (place-and-adjust): the placed prop can be dragged into position within the same gesture and
/// the whole thing stays ONE undo step whose <see cref="Revert"/> removes the placement, restoring the pre-place
/// document byte for byte.</summary>
public sealed class AddPlacementCommand : EditorCommand
{
    readonly MapPlacement _placement;

    /// <summary>Creates the command for the given placement (added on <see cref="Apply"/>).</summary>
    public AddPlacementCommand(MapPlacement placement) =>
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));

    /// <inheritdoc/>
    public override string Label => "Add placement";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.Placements.Add(_placement);

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Placements.Remove(_placement);

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        // Fold a same-id move into the Add: the placed prop's final position becomes part of the Add itself, so
        // place-and-adjust is one undo step and Revert still just removes the placement.
        if (next is MovePlacementCommand m && string.Equals(m.Id, _placement.Id, StringComparison.Ordinal))
        {
            _placement.X = m.NewX;
            _placement.Z = m.NewZ;
            _placement.Y = m.NewY;
            return true;
        }
        return false;
    }
}

/// <summary>Removes the placement with the given id, capturing the removed item and its index so
/// <see cref="Revert"/> restores it at its original position.</summary>
public sealed class RemovePlacementCommand : EditorCommand
{
    readonly string _id;
    MapPlacement? _removed;
    int _index = -1;

    /// <summary>Creates the command for the placement id to remove.</summary>
    public RemovePlacementCommand(string id) =>
        _id = id ?? throw new ArgumentNullException(nameof(id));

    /// <inheritdoc/>
    public override string Label => "Remove placement";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapPlacement p = FindPlacement(doc, _id);
        _index = doc.Placements.IndexOf(p);
        _removed = p;
        doc.Placements.RemoveAt(_index);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (_removed is null) throw new InvalidOperationException("Revert called before Apply.");
        doc.Placements.Insert(_index, _removed);
    }
}

/// <summary>Moves a placement to a new XZ (and optional Y). Successive moves of the same placement coalesce
/// into one undo step (drag coalescing).</summary>
public sealed class MovePlacementCommand : EditorCommand
{
    readonly string _id;
    float _newX, _newZ;
    float? _newY;
    float _oldX, _oldZ;
    float? _oldY;
    bool _captured;

    /// <summary>Creates the command moving placement <paramref name="id"/> to (<paramref name="newX"/>,
    /// <paramref name="newZ"/>) with an optional new Y (null = ground-snap).</summary>
    public MovePlacementCommand(string id, float newX, float newZ, float? newY)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _newX = newX;
        _newZ = newZ;
        _newY = newY;
    }

    /// <summary>The moved placement's id, so <see cref="AddPlacementCommand.TryMerge"/> can match a same-id move.</summary>
    internal string Id => _id;
    /// <summary>The target X this move sets, exposed so an absorbing Add can fold in the final position.</summary>
    internal float NewX => _newX;
    /// <summary>The target Z this move sets, exposed so an absorbing Add can fold in the final position.</summary>
    internal float NewZ => _newZ;
    /// <summary>The target Y this move sets (null = ground-snap), exposed for an absorbing Add.</summary>
    internal float? NewY => _newY;

    /// <inheritdoc/>
    public override string Label => "Move placement";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapPlacement p = FindPlacement(doc, _id);
        if (!_captured) { _oldX = p.X; _oldZ = p.Z; _oldY = p.Y; _captured = true; }
        p.X = _newX;
        p.Z = _newZ;
        p.Y = _newY;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        MapPlacement p = FindPlacement(doc, _id);
        p.X = _oldX;
        p.Z = _oldZ;
        p.Y = _oldY;
    }

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is MovePlacementCommand m && string.Equals(m._id, _id, StringComparison.Ordinal))
        {
            _newX = m._newX;
            _newZ = m._newZ;
            _newY = m._newY;
            return true;
        }
        return false;
    }
}

/// <summary>Sets a placement's yaw. Successive rotations of the same placement coalesce.</summary>
public sealed class RotatePlacementCommand : EditorCommand
{
    readonly string _id;
    float _newYaw;
    float _oldYaw;
    bool _captured;

    /// <summary>Creates the command rotating placement <paramref name="id"/> to <paramref name="newYaw"/>.</summary>
    public RotatePlacementCommand(string id, float newYaw)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _newYaw = newYaw;
    }

    /// <inheritdoc/>
    public override string Label => "Rotate placement";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapPlacement p = FindPlacement(doc, _id);
        if (!_captured) { _oldYaw = p.Yaw; _captured = true; }
        p.Yaw = _newYaw;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => FindPlacement(doc, _id).Yaw = _oldYaw;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is RotatePlacementCommand r && string.Equals(r._id, _id, StringComparison.Ordinal))
        {
            _newYaw = r._newYaw;
            return true;
        }
        return false;
    }
}

/// <summary>Sets a placement's uniform scale. Successive scalings of the same placement coalesce.</summary>
public sealed class ScalePlacementCommand : EditorCommand
{
    readonly string _id;
    float _newScale;
    float _oldScale;
    bool _captured;

    /// <summary>Creates the command scaling placement <paramref name="id"/> to <paramref name="newScale"/>.</summary>
    public ScalePlacementCommand(string id, float newScale)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _newScale = newScale;
    }

    /// <inheritdoc/>
    public override string Label => "Scale placement";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapPlacement p = FindPlacement(doc, _id);
        if (!_captured) { _oldScale = p.Scale; _captured = true; }
        p.Scale = _newScale;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => FindPlacement(doc, _id).Scale = _oldScale;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is ScalePlacementCommand s && string.Equals(s._id, _id, StringComparison.Ordinal))
        {
            _newScale = s._newScale;
            return true;
        }
        return false;
    }
}

/// <summary>Renames a placement. Placements are keyed by id, so the id-carrying selection follows the rename.
/// The target id must be unique: <see cref="Apply"/> throws (before it mutates) if a placement already carries
/// the new id, so a rejected rename lands no undo step. Renames never coalesce (no merge).</summary>
public sealed class RenamePlacementCommand : EditorCommand
{
    readonly string _oldId;
    readonly string _newId;

    /// <summary>Creates the command renaming placement <paramref name="oldId"/> to <paramref name="newId"/>.</summary>
    public RenamePlacementCommand(string oldId, string newId)
    {
        _oldId = oldId ?? throw new ArgumentNullException(nameof(oldId));
        _newId = newId ?? throw new ArgumentNullException(nameof(newId));
    }

    /// <inheritdoc/>
    public override string Label => "Rename placement";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        GuardNoPlacement(doc, _newId);   // reject a duplicate target before touching the source
        FindPlacement(doc, _oldId).Id = _newId;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => FindPlacement(doc, _newId).Id = _oldId;
}

// ---- spawns ----------------------------------------------------------------------------------------------

/// <summary>Appends an NPC spawn marker to the document. Absorbs a same-id <see cref="MoveSpawnCommand"/> that
/// immediately follows (place-and-adjust), so a just-placed spawn can be dragged into position within the same
/// gesture and the whole thing stays ONE undo step whose <see cref="Revert"/> removes the spawn.</summary>
public sealed class AddSpawnCommand : EditorCommand
{
    readonly MapSpawn _spawn;

    /// <summary>Creates the command for the given spawn (added on <see cref="Apply"/>).</summary>
    public AddSpawnCommand(MapSpawn spawn) =>
        _spawn = spawn ?? throw new ArgumentNullException(nameof(spawn));

    /// <inheritdoc/>
    public override string Label => "Add spawn";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.Spawns.Add(_spawn);

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Spawns.Remove(_spawn);

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        // Fold a same-id move into the Add: the placed spawn's final position becomes part of the Add itself, so
        // place-and-adjust is one undo step and Revert still just removes the spawn.
        if (next is MoveSpawnCommand m && string.Equals(m.Id, _spawn.Id, StringComparison.Ordinal))
        {
            _spawn.X = m.NewX;
            _spawn.Z = m.NewZ;
            return true;
        }
        return false;
    }
}

/// <summary>Removes the spawn with the given id, restoring it at its original index on revert.</summary>
public sealed class RemoveSpawnCommand : EditorCommand
{
    readonly string _id;
    MapSpawn? _removed;
    int _index = -1;

    /// <summary>Creates the command for the spawn id to remove.</summary>
    public RemoveSpawnCommand(string id) =>
        _id = id ?? throw new ArgumentNullException(nameof(id));

    /// <inheritdoc/>
    public override string Label => "Remove spawn";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapSpawn s = FindSpawn(doc, _id);
        _index = doc.Spawns.IndexOf(s);
        _removed = s;
        doc.Spawns.RemoveAt(_index);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (_removed is null) throw new InvalidOperationException("Revert called before Apply.");
        doc.Spawns.Insert(_index, _removed);
    }
}

/// <summary>Moves a spawn to a new XZ. Successive moves of the same spawn coalesce.</summary>
public sealed class MoveSpawnCommand : EditorCommand
{
    readonly string _id;
    float _newX, _newZ;
    float _oldX, _oldZ;
    bool _captured;

    /// <summary>Creates the command moving spawn <paramref name="id"/> to (<paramref name="newX"/>,
    /// <paramref name="newZ"/>).</summary>
    public MoveSpawnCommand(string id, float newX, float newZ)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _newX = newX;
        _newZ = newZ;
    }

    /// <summary>The moved spawn's id, so <see cref="AddSpawnCommand.TryMerge"/> can match a same-id move.</summary>
    internal string Id => _id;
    /// <summary>The target X this move sets, exposed so an absorbing Add can fold in the final position.</summary>
    internal float NewX => _newX;
    /// <summary>The target Z this move sets, exposed so an absorbing Add can fold in the final position.</summary>
    internal float NewZ => _newZ;

    /// <inheritdoc/>
    public override string Label => "Move spawn";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapSpawn s = FindSpawn(doc, _id);
        if (!_captured) { _oldX = s.X; _oldZ = s.Z; _captured = true; }
        s.X = _newX;
        s.Z = _newZ;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        MapSpawn s = FindSpawn(doc, _id);
        s.X = _oldX;
        s.Z = _oldZ;
    }

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is MoveSpawnCommand m && string.Equals(m._id, _id, StringComparison.Ordinal))
        {
            _newX = m._newX;
            _newZ = m._newZ;
            return true;
        }
        return false;
    }
}

/// <summary>Toggles a spawn's enabled flag.</summary>
public sealed class SetSpawnEnabledCommand : EditorCommand
{
    readonly string _id;
    readonly bool _enabled;
    bool _old;
    bool _captured;

    /// <summary>Creates the command setting spawn <paramref name="id"/>'s enabled flag to
    /// <paramref name="enabled"/>.</summary>
    public SetSpawnEnabledCommand(string id, bool enabled)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _enabled = enabled;
    }

    /// <inheritdoc/>
    public override string Label => "Set spawn enabled";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapSpawn s = FindSpawn(doc, _id);
        if (!_captured) { _old = s.Enabled; _captured = true; }
        s.Enabled = _enabled;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => FindSpawn(doc, _id).Enabled = _old;
}

/// <summary>Renames a spawn. Spawns are keyed by id, so the id-carrying selection follows the rename. The target
/// id must be unique: <see cref="Apply"/> throws (before it mutates) if a spawn already carries the new id, so a
/// rejected rename lands no undo step. Renames never coalesce (no merge).</summary>
public sealed class RenameSpawnCommand : EditorCommand
{
    readonly string _oldId;
    readonly string _newId;

    /// <summary>Creates the command renaming spawn <paramref name="oldId"/> to <paramref name="newId"/>.</summary>
    public RenameSpawnCommand(string oldId, string newId)
    {
        _oldId = oldId ?? throw new ArgumentNullException(nameof(oldId));
        _newId = newId ?? throw new ArgumentNullException(nameof(newId));
    }

    /// <inheritdoc/>
    public override string Label => "Rename spawn";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        GuardNoSpawn(doc, _newId);   // reject a duplicate target before touching the source
        FindSpawn(doc, _oldId).Id = _newId;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => FindSpawn(doc, _newId).Id = _oldId;
}

// ---- exclusions (terrain-shape affecting) ----------------------------------------------------------------

/// <summary>Appends a scatter exclusion shape. Affects the streamed world (scatter inputs change).</summary>
public sealed class AddExclusionCommand : EditorCommand
{
    readonly MapExclusion _exclusion;

    /// <summary>Creates the command for the given exclusion (added on <see cref="Apply"/>).</summary>
    public AddExclusionCommand(MapExclusion exclusion) =>
        _exclusion = exclusion ?? throw new ArgumentNullException(nameof(exclusion));

    /// <inheritdoc/>
    public override string Label => "Add exclusion";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.Exclusions.Add(_exclusion);

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Exclusions.Remove(_exclusion);
}

/// <summary>Removes the exclusion at the given index, restoring it at that index on revert. Affects the
/// streamed world.</summary>
public sealed class RemoveExclusionCommand : EditorCommand
{
    readonly int _index;
    MapExclusion? _removed;

    /// <summary>Creates the command for the exclusion list index to remove.</summary>
    public RemoveExclusionCommand(int index) => _index = index;

    /// <inheritdoc/>
    public override string Label => "Remove exclusion";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        _removed = doc.Exclusions[_index];
        doc.Exclusions.RemoveAt(_index);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (_removed is null) throw new InvalidOperationException("Revert called before Apply.");
        doc.Exclusions.Insert(_index, _removed);
    }
}

/// <summary>Replaces the shape of the exclusion at a given index with a new value (parameter scrub or kind
/// conversion). The caller supplies both the new and old shape, cloned with the changed field (the
/// <see cref="EditFeatureCommand"/> idiom). Successive edits of the same index coalesce (scrub coalescing).
/// Affects the streamed world (scatter inputs change).</summary>
public sealed class EditExclusionShapeCommand : EditorCommand
{
    readonly int _index;
    MapShapeDoc _newShape;
    readonly MapShapeDoc _oldShape;

    /// <summary>Creates the command replacing exclusion <paramref name="index"/>'s shape with
    /// <paramref name="newShape"/>, capturing <paramref name="oldShape"/> for revert.</summary>
    public EditExclusionShapeCommand(int index, MapShapeDoc newShape, MapShapeDoc oldShape)
    {
        _index = index;
        _newShape = newShape ?? throw new ArgumentNullException(nameof(newShape));
        _oldShape = oldShape ?? throw new ArgumentNullException(nameof(oldShape));
    }

    /// <inheritdoc/>
    public override string Label => "Edit exclusion shape";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.Exclusions[_index].Shape = _newShape;

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Exclusions[_index].Shape = _oldShape;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is EditExclusionShapeCommand e && e._index == _index)
        {
            _newShape = e._newShape;
            return true;
        }
        return false;
    }
}

/// <summary>Renames the exclusion at a given index. Exclusions are index-addressed (no independent id, the same
/// idiom as <see cref="RenameFeatureCommand"/>), so this targets the list position rather than an old-name
/// lookup. The caller supplies both the new and old name (empty for unnamed), and empty is normalized to null
/// so a cleared name never persists as a bloating empty name key. Successive renames of the same index coalesce
/// into one undo step (a text field committed on every keystroke stays one undo). The target name must be
/// unique among named exclusions: <see cref="Apply"/> throws (before it mutates) if another exclusion already
/// carries the new name, so a rejected rename lands no undo step (the <see cref="RenameRegionCommand"/> guard
/// idiom). Renaming does not change the exclusion's shape or layer filter, so this does not affect the streamed
/// world.</summary>
public sealed class RenameExclusionCommand : EditorCommand
{
    readonly int _index;
    string _newName;
    readonly string _oldName;

    /// <summary>Creates the command renaming the exclusion at <paramref name="index"/> to
    /// <paramref name="newName"/>, capturing <paramref name="oldName"/> for revert.</summary>
    public RenameExclusionCommand(int index, string newName, string oldName)
    {
        _index = index;
        _newName = newName ?? throw new ArgumentNullException(nameof(newName));
        _oldName = oldName ?? throw new ArgumentNullException(nameof(oldName));
    }

    /// <inheritdoc/>
    public override string Label => "Rename exclusion";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        string? normalized = NormalizeName(_newName);
        GuardNoExclusionName(doc, normalized, _index);   // reject a duplicate target before touching the source
        doc.Exclusions[_index].Name = normalized;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Exclusions[_index].Name = NormalizeName(_oldName);

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is RenameExclusionCommand r && r._index == _index)
        {
            _newName = r._newName;
            return true;
        }
        return false;
    }
}

/// <summary>Replaces the layer filter of the exclusion at a given index (parameter scrub, the
/// <see cref="EditExclusionShapeCommand"/> idiom). Null Layers means the exclusion applies to every scatter
/// layer including any added later, an exact document semantic that must be preserved: the GUI's "All layers"
/// toggle is the only control that produces null. Turning that toggle off materializes the current full
/// explicit list, and checking every known layer back on by hand afterwards does NOT re-collapse to null, only
/// the All toggle does, so the explicit list stays explicit even once it covers every layer. An empty explicit
/// list (every box unchecked) is legal and means the exclusion applies to nothing. Successive edits of the same
/// index coalesce (checkbox-drag coalescing). Affects the streamed world: which layers a region excludes
/// changes scatter output. An unknown layer name is not rejected here: the caller relies on the standard
/// document validator on save (<see cref="MapDocumentValidator"/>) to catch it, the same invariant every other
/// layer-filter field already relies on. Both lists are deep-copied at construction and again on every
/// <see cref="Apply"/>/<see cref="Revert"/>, so the command, the document, and the caller's own list each hold
/// an independent instance and none can alias another.</summary>
public sealed class EditExclusionLayersCommand : EditorCommand
{
    readonly int _index;
    List<string>? _newLayers;
    readonly List<string>? _oldLayers;

    /// <summary>Creates the command replacing exclusion <paramref name="index"/>'s layer filter with
    /// <paramref name="newLayers"/>, capturing <paramref name="oldLayers"/> for revert. Both lists are copied
    /// (<c>?.ToList()</c>) rather than stored by reference, so a caller mutating its own list after construction
    /// cannot reach back into the command.</summary>
    public EditExclusionLayersCommand(int index, List<string>? newLayers, List<string>? oldLayers)
    {
        _index = index;
        _newLayers = newLayers?.ToList();
        _oldLayers = oldLayers?.ToList();
    }

    /// <inheritdoc/>
    public override string Label => "Edit exclusion layers";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.Exclusions[_index].Layers = _newLayers?.ToList();

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Exclusions[_index].Layers = _oldLayers?.ToList();

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is EditExclusionLayersCommand e && e._index == _index)
        {
            _newLayers = e._newLayers;
            return true;
        }
        return false;
    }
}

// ---- regions (game-interpreted markers, not terrain-affecting) --------------------------------------------

/// <summary>Appends a named region marker. Regions are game-interpreted, so this does not affect the
/// streamed world.</summary>
public sealed class AddRegionCommand : EditorCommand
{
    readonly MapRegion _region;

    /// <summary>Creates the command for the given region (added on <see cref="Apply"/>).</summary>
    public AddRegionCommand(MapRegion region) =>
        _region = region ?? throw new ArgumentNullException(nameof(region));

    /// <inheritdoc/>
    public override string Label => "Add region";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.Regions.Add(_region);

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Regions.Remove(_region);
}

/// <summary>Removes the region with the given name, restoring it at its original index on revert.</summary>
public sealed class RemoveRegionCommand : EditorCommand
{
    readonly string _name;
    MapRegion? _removed;
    int _index = -1;

    /// <summary>Creates the command for the region name to remove.</summary>
    public RemoveRegionCommand(string name) =>
        _name = name ?? throw new ArgumentNullException(nameof(name));

    /// <inheritdoc/>
    public override string Label => "Remove region";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        _index = IndexOfRegion(doc, _name);
        _removed = doc.Regions[_index];
        doc.Regions.RemoveAt(_index);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (_removed is null) throw new InvalidOperationException("Revert called before Apply.");
        doc.Regions.Insert(_index, _removed);
    }
}

/// <summary>Renames a region. Regions are keyed by name, so the id-carrying selection follows the rename. The
/// target name must be unique: <see cref="Apply"/> throws (before it mutates) if a region already carries the new
/// name, so a rejected rename lands no undo step. A chained rename, where the next command's old name matches
/// this one's current new name (the same-name-pair a per-keystroke commit produces), coalesces into this command
/// instead of pushing its own step: previously every keystroke of a region rename landed its own undo entry.</summary>
public sealed class RenameRegionCommand : EditorCommand
{
    readonly string _oldName;
    string _newName;

    /// <summary>Creates the command renaming the region <paramref name="oldName"/> to
    /// <paramref name="newName"/>.</summary>
    public RenameRegionCommand(string oldName, string newName)
    {
        _oldName = oldName ?? throw new ArgumentNullException(nameof(oldName));
        _newName = newName ?? throw new ArgumentNullException(nameof(newName));
    }

    /// <inheritdoc/>
    public override string Label => "Rename region";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        GuardNoRegion(doc, _newName);   // reject a duplicate target before touching the source
        doc.Regions[IndexOfRegion(doc, _oldName)].Name = _newName;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Regions[IndexOfRegion(doc, _newName)].Name = _oldName;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is RenameRegionCommand r && string.Equals(r._oldName, _newName, StringComparison.Ordinal))
        {
            _newName = r._newName;
            return true;
        }
        return false;
    }
}

/// <summary>Replaces the shape of the region with a given name (parameter scrub or kind conversion). The caller
/// supplies both the new and old shape, cloned with the changed field (the <see cref="EditFeatureCommand"/>
/// idiom). Successive edits of the same region coalesce (scrub coalescing). Regions are game-interpreted, so
/// this does not affect the streamed world.</summary>
public sealed class EditRegionShapeCommand : EditorCommand
{
    readonly string _name;
    MapShapeDoc _newShape;
    readonly MapShapeDoc _oldShape;

    /// <summary>Creates the command replacing region <paramref name="name"/>'s shape with
    /// <paramref name="newShape"/>, capturing <paramref name="oldShape"/> for revert.</summary>
    public EditRegionShapeCommand(string name, MapShapeDoc newShape, MapShapeDoc oldShape)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _newShape = newShape ?? throw new ArgumentNullException(nameof(newShape));
        _oldShape = oldShape ?? throw new ArgumentNullException(nameof(oldShape));
    }

    /// <inheritdoc/>
    public override string Label => "Edit region shape";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.Regions[IndexOfRegion(doc, _name)].Shape = _newShape;

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Regions[IndexOfRegion(doc, _name)].Shape = _oldShape;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is EditRegionShapeCommand r && string.Equals(r._name, _name, StringComparison.Ordinal))
        {
            _newShape = r._newShape;
            return true;
        }
        return false;
    }
}

// ---- terrain globals (terrain-shape affecting) -----------------------------------------------------------

/// <summary>Edits the terrain's global settings. Named for terrain-wide globals so later globals can join it, but
/// v1 carries the water level only. Successive edits coalesce into one undo step (scrub coalescing). Affects the
/// streamed world: scatter honours the water level (underwater candidates skip), so a change forces a wholesale
/// rebuild. The water surface itself derives live from the document, so it also updates on the same edit.</summary>
public sealed class EditTerrainCommand : EditorCommand
{
    float _newWaterLevel;
    readonly float _oldWaterLevel;

    /// <summary>Creates the command setting the water level to <paramref name="newWaterLevel"/>, capturing
    /// <paramref name="oldWaterLevel"/> for revert.</summary>
    public EditTerrainCommand(float newWaterLevel, float oldWaterLevel)
    {
        _newWaterLevel = newWaterLevel;
        _oldWaterLevel = oldWaterLevel;
    }

    /// <inheritdoc/>
    public override string Label => "Edit terrain";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.Terrain.WaterLevel = _newWaterLevel;

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Terrain.WaterLevel = _oldWaterLevel;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        // Terrain is a singleton, so any two terrain edits of the same gesture coalesce (no id/index to match).
        if (next is EditTerrainCommand t)
        {
            _newWaterLevel = t._newWaterLevel;
            return true;
        }
        return false;
    }
}

// ---- terrain features (terrain-shape affecting) ----------------------------------------------------------

/// <summary>Appends a terrain feature. Affects the streamed world (terrain shape changes).</summary>
public sealed class AddFeatureCommand : EditorCommand
{
    readonly MapFeature _feature;

    /// <summary>Creates the command for the given feature (added on <see cref="Apply"/>).</summary>
    public AddFeatureCommand(MapFeature feature) =>
        _feature = feature ?? throw new ArgumentNullException(nameof(feature));

    /// <inheritdoc/>
    public override string Label => "Add terrain feature";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.Terrain.Features.Add(_feature);

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Terrain.Features.Remove(_feature);
}

/// <summary>Removes the terrain feature at the given index, restoring it at that index on revert. Affects the
/// streamed world.</summary>
public sealed class RemoveFeatureCommand : EditorCommand
{
    readonly int _index;
    MapFeature? _removed;

    /// <summary>Creates the command for the feature list index to remove.</summary>
    public RemoveFeatureCommand(int index) => _index = index;

    /// <inheritdoc/>
    public override string Label => "Remove terrain feature";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        _removed = doc.Terrain.Features[_index];
        doc.Terrain.Features.RemoveAt(_index);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (_removed is null) throw new InvalidOperationException("Revert called before Apply.");
        doc.Terrain.Features.Insert(_index, _removed);
    }
}

/// <summary>Replaces the terrain feature at a given index with a new value (parameter scrub). The caller
/// supplies both the new and old feature. Successive edits of the same index coalesce (scrub coalescing).
/// Affects the streamed world.</summary>
public sealed class EditFeatureCommand : EditorCommand
{
    readonly int _index;
    MapFeature _newValue;
    readonly MapFeature _oldValue;

    /// <summary>Creates the command replacing feature <paramref name="featureIndex"/> with
    /// <paramref name="newValue"/>, capturing <paramref name="oldValue"/> for revert.</summary>
    public EditFeatureCommand(int featureIndex, MapFeature newValue, MapFeature oldValue)
    {
        _index = featureIndex;
        _newValue = newValue ?? throw new ArgumentNullException(nameof(newValue));
        _oldValue = oldValue ?? throw new ArgumentNullException(nameof(oldValue));
    }

    /// <inheritdoc/>
    public override string Label => "Edit terrain feature";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.Terrain.Features[_index] = _newValue;

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Terrain.Features[_index] = _oldValue;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is EditFeatureCommand f && f._index == _index)
        {
            _newValue = f._newValue;
            return true;
        }
        return false;
    }
}

/// <summary>Renames the terrain feature at a given index. Features are index-addressed (no independent id,
/// unlike placements/spawns/regions which carry their own id or name), so this targets the list position rather
/// than an old-name lookup. The caller supplies both the new and old name (empty for unnamed), and empty is
/// normalized to null so a cleared name never persists as a bloating empty name key. Successive renames of the
/// same index coalesce into one undo step (the <see cref="EditFeatureCommand"/> scrub-coalescing idiom, e.g. a
/// text field committed on every keystroke). The target name must be unique among named features: <see
/// cref="Apply"/> throws (before it mutates) if another feature already carries the new name, so a rejected
/// rename lands no undo step (the <see cref="RenameRegionCommand"/> guard idiom). Renaming does not change the
/// terrain shape, so this does not affect the streamed world.</summary>
public sealed class RenameFeatureCommand : EditorCommand
{
    readonly int _index;
    string _newName;
    readonly string _oldName;

    /// <summary>Creates the command renaming the terrain feature at <paramref name="index"/> to
    /// <paramref name="newName"/>, capturing <paramref name="oldName"/> for revert.</summary>
    public RenameFeatureCommand(int index, string newName, string oldName)
    {
        _index = index;
        _newName = newName ?? throw new ArgumentNullException(nameof(newName));
        _oldName = oldName ?? throw new ArgumentNullException(nameof(oldName));
    }

    /// <inheritdoc/>
    public override string Label => "Rename terrain feature";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        string? normalized = NormalizeName(_newName);
        GuardNoFeatureName(doc, normalized, _index);   // reject a duplicate target before touching the source
        doc.Terrain.Features[_index].Name = normalized;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Terrain.Features[_index].Name = NormalizeName(_oldName);

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is RenameFeatureCommand r && r._index == _index)
        {
            _newName = r._newName;
            return true;
        }
        return false;
    }
}

/// <summary>Moves a terrain feature from one list position to another. Terrain features fold in list order
/// (<see cref="MapDoc.MapRuntime.BuildField"/> runs each feature's height modifier on the height the prior
/// feature produced), so where two features cover the same ground the LAST one in the list dominates the
/// overlap. Reordering is therefore how the author picks the winner between overlapping features (a lake and a
/// flatten over the same clearing, say): move the feature that should win to a later position. <see cref="Revert"/>
/// moves it back. Affects the streamed world (terrain shape changes), and never coalesces (no merge).</summary>
public sealed class ReorderFeatureCommand : EditorCommand
{
    readonly int _fromIndex;
    readonly int _toIndex;

    /// <summary>Creates the command moving the feature at <paramref name="fromIndex"/> to
    /// <paramref name="toIndex"/> in the terrain feature list.</summary>
    public ReorderFeatureCommand(int fromIndex, int toIndex)
    {
        _fromIndex = fromIndex;
        _toIndex = toIndex;
    }

    /// <inheritdoc/>
    public override string Label => "Reorder terrain feature";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => Move(doc, _fromIndex, _toIndex);

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => Move(doc, _toIndex, _fromIndex);

    // A list move: pull the feature out at `from` and re-insert it at `to`. Its own inverse (remove at `to`,
    // insert at `from`), so Revert is just Move with the endpoints swapped.
    static void Move(MapDocument doc, int from, int to)
    {
        MapFeature feature = doc.Terrain.Features[from];
        doc.Terrain.Features.RemoveAt(from);
        doc.Terrain.Features.Insert(to, feature);
    }
}

/// <summary>Moves a scatter exclusion from one list position to another. Unlike <see cref="ReorderFeatureCommand"/>,
/// this does NOT affect the streamed world, so <see cref="AffectsWorld"/> stays false: exclusions combine as a pure
/// set union (a scatter candidate is masked when it falls inside ANY exclusion), so the list ORDER never changes
/// which ground is excluded. Marking it true would force a full viewport world rebuild on every reorder for a
/// change the scatter cannot observe. Both indices
/// are range-guarded (non-negative in the constructor, in-range against the live list at apply time, each with a
/// precise <see cref="ArgumentOutOfRangeException"/>). <see cref="Revert"/> moves it back (self-inverse), and it
/// never coalesces (no merge).</summary>
public sealed class ReorderExclusionCommand : EditorCommand
{
    readonly int _fromIndex;
    readonly int _toIndex;

    /// <summary>Creates the command moving the exclusion at <paramref name="fromIndex"/> to
    /// <paramref name="toIndex"/> in the exclusion list. Both must be non-negative.</summary>
    public ReorderExclusionCommand(int fromIndex, int toIndex)
    {
        if (fromIndex < 0) throw new ArgumentOutOfRangeException(nameof(fromIndex), fromIndex, "Exclusion index must be non-negative.");
        if (toIndex < 0) throw new ArgumentOutOfRangeException(nameof(toIndex), toIndex, "Exclusion index must be non-negative.");
        _fromIndex = fromIndex;
        _toIndex = toIndex;
    }

    /// <inheritdoc/>
    public override string Label => "Reorder exclusion";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => Move(doc, _fromIndex, _toIndex);

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => Move(doc, _toIndex, _fromIndex);

    // A list move (remove at `from`, re-insert at `to`), its own inverse. Range-guards both endpoints against the
    // live list up front so a bad index is a precise ArgumentOutOfRangeException, not an opaque list throw.
    static void Move(MapDocument doc, int from, int to)
    {
        int count = doc.Exclusions.Count;
        if (from >= count) throw new ArgumentOutOfRangeException(nameof(from), from, $"Exclusion index is out of range (count {count}).");
        if (to >= count) throw new ArgumentOutOfRangeException(nameof(to), to, $"Exclusion index is out of range (count {count}).");
        MapExclusion exclusion = doc.Exclusions[from];
        doc.Exclusions.RemoveAt(from);
        doc.Exclusions.Insert(to, exclusion);
    }
}
