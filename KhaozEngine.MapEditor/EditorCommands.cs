using System;
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
}

// ---- placements ------------------------------------------------------------------------------------------

/// <summary>Appends an authored placement to the document.</summary>
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

/// <summary>Appends an NPC spawn marker to the document.</summary>
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
/// name, so a rejected rename lands no undo step. Renames never coalesce (no merge).</summary>
public sealed class RenameRegionCommand : EditorCommand
{
    readonly string _oldName;
    readonly string _newName;

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
