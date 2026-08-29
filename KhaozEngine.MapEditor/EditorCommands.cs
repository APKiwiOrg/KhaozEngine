using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

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
public abstract partial class EditorCommand : IEditorCommand
{
    /// <inheritdoc/>
    public abstract string Label { get; }

    /// <summary>True when applying or reverting this command changes terrain shape or scatter inputs, so the
    /// viewport must rebuild its streamed world. Placement/spawn/region edits are false.</summary>
    internal abstract bool AffectsWorld { get; }

    /// <summary>The world-space region this command's edit can change, or null when the edit's reach is not a
    /// bounded rect (so the viewport must rebuild the WHOLE streamed world). The base returns null: the feature
    /// commands narrow it to a single feature's <see cref="FeatureGeometry.TryFootprint"/> disc, and the
    /// exclusion / scatter-override commands narrow it to their shape's AABB padded by the jitter-aware margin
    /// they capture at Apply (<see cref="ShapeGeometry.BoundsMarginFor"/>: a pointwise scatter-only reach, but
    /// scatter tests membership at the JITTERED candidate position while chunk assignment uses the cell centre,
    /// so the shape bounds must grow by the doc's largest layer jitter). Read by the document's
    /// pending-rebuild-region accumulation (<see cref="EditorDocument"/>) and only meaningful while
    /// <see cref="AffectsWorld"/> is true. Every other <see cref="AffectsWorld"/> command (terrain scalars, biome
    /// bands, scatter layers, companions) keeps the null default: a terrain scalar or biome band's reach spans
    /// the whole doc (a biome band is bounded only in its world-Z-range slice, a possible future narrowing),
    /// and a scatter-layer or companion-layer edit changes generation rules read across every loaded chunk.
    /// </summary>
    internal virtual RectArea? DirtyRegion => null;

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

    private protected static MapPlayerSpawn FindPlayerSpawn(MapDocument doc, string id)
    {
        foreach (MapPlayerSpawn s in doc.PlayerSpawns)
            if (string.Equals(s.Id, id, StringComparison.Ordinal))
                return s;
        throw new InvalidOperationException($"No player spawn with id '{id}' in the document.");
    }

    private protected static void GuardNoPlayerSpawn(MapDocument doc, string id)
    {
        foreach (MapPlayerSpawn s in doc.PlayerSpawns)
            if (string.Equals(s.Id, id, StringComparison.Ordinal))
                throw new InvalidOperationException($"A player spawn with id '{id}' already exists in the document.");
    }

    /// <summary>Deep-copies a player spawn, including a FRESH Tags list, so a command that owns the copy never
    /// aliases the caller's list (nor the reverse). Guards against the round-5 shared-list mutation trap.</summary>
    private protected static MapPlayerSpawn ClonePlayerSpawn(MapPlayerSpawn s) =>
        new MapPlayerSpawn
        {
            Id = s.Id,
            X = s.X,
            Z = s.Z,
            Yaw = s.Yaw,
            Enabled = s.Enabled,
            Tags = new List<string>(s.Tags),
        };

    /// <summary>Rejects a duplicate region name, on the add path (which passes -1, having nothing to exclude) and
    /// on the rename path, where <paramref name="exceptIndex"/> excludes the renaming region's own index (resolved
    /// by its current name at the call site) so a collapsed self-rename stays legal.</summary>
    private protected static void GuardNoRegion(MapDocument doc, string name, int exceptIndex)
    {
        for (int i = 0; i < doc.Regions.Count; i++)
            if (i != exceptIndex && string.Equals(doc.Regions[i].Name, name, StringComparison.Ordinal))
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

    /// <summary>Rejects a duplicate scatter override name before <see cref="RenameScatterOverrideCommand.Apply"/>
    /// mutates anything. Scatter overrides are index-addressed (see <see cref="RenameScatterOverrideCommand"/>),
    /// so the scan excludes the renaming override's own index (renaming to its current name, or the
    /// empty-clearing case with another unnamed override already present, both stay legal). A null or empty name
    /// never collides since an unnamed override carries no key to clash on.</summary>
    private protected static void GuardNoScatterOverrideName(MapDocument doc, string? name, int exceptIndex)
    {
        if (string.IsNullOrEmpty(name)) return;
        List<MapScatterOverrideDoc> overrides = doc.ScatterOverrides;
        for (int i = 0; i < overrides.Count; i++)
            if (i != exceptIndex && string.Equals(overrides[i].Name, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"A scatter override named '{name}' already exists in the document.");
    }

    /// <summary>Coerces an empty rename target to null: features, exclusions, and scatter overrides store an
    /// optional Name where empty means unnamed (<see cref="MapDoc.MapFeature.Name"/>, <see cref="MapExclusion.Name"/>,
    /// <see cref="MapScatterOverrideDoc.Name"/>), and the stored value must never be the empty string, only null
    /// or non-empty, so a clear-to-empty rename does not persist as a bloating empty name key.</summary>
    private protected static string? NormalizeName(string? name) => string.IsNullOrEmpty(name) ? null : name;

    private protected static int IndexOfScatterLayer(MapDocument doc, string name)
    {
        for (int i = 0; i < doc.ScatterLayers.Count; i++)
            if (string.Equals(doc.ScatterLayers[i].Name, name, StringComparison.Ordinal))
                return i;
        throw new InvalidOperationException($"No scatter layer named '{name}' in the document.");
    }

    private protected static int IndexOfCompanionLayer(MapDocument doc, string name)
    {
        for (int i = 0; i < doc.CompanionLayers.Count; i++)
            if (string.Equals(doc.CompanionLayers[i].Name, name, StringComparison.Ordinal))
                return i;
        throw new InvalidOperationException($"No companion layer named '{name}' in the document.");
    }

    /// <summary>Rejects a duplicate scatter layer name. <paramref name="exceptIndex"/> excludes the renaming
    /// layer's own index (resolved by its current name at the call site), so a collapsed self-rename stays
    /// legal.</summary>
    private protected static void GuardNoScatterLayerName(MapDocument doc, string name, int exceptIndex)
    {
        for (int i = 0; i < doc.ScatterLayers.Count; i++)
            if (i != exceptIndex && string.Equals(doc.ScatterLayers[i].Name, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"A scatter layer named '{name}' already exists in the document.");
    }

    /// <summary>Rejects a duplicate companion layer name. <paramref name="exceptIndex"/> excludes the renaming
    /// layer's own index (resolved by its current name at the call site), so a collapsed self-rename stays
    /// legal.</summary>
    private protected static void GuardNoCompanionLayerName(MapDocument doc, string name, int exceptIndex)
    {
        for (int i = 0; i < doc.CompanionLayers.Count; i++)
            if (i != exceptIndex && string.Equals(doc.CompanionLayers[i].Name, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"A companion layer named '{name}' already exists in the document.");
    }

    /// <summary>Lists the document elements that reference the scatter layer named <paramref name="name"/>: a
    /// companion layer hosting it, or an exclusion / scatter-override whose explicit layer filter names it (a
    /// null "all layers" filter is not a named reference and is skipped). Each entry reads validator-consistent
    /// (host layer / layer filter), so a <see cref="RemoveScatterLayerCommand"/> rejection message lists exactly
    /// what the standard validator's unknown-layer rules would flag if the layer vanished under them.</summary>
    private protected static List<string> ScatterLayerReferences(MapDocument doc, string name)
    {
        var refs = new List<string>();
        foreach (MapCompanionLayer c in doc.CompanionLayers)
            if (string.Equals(c.HostLayer, name, StringComparison.Ordinal))
                refs.Add($"companion layer '{c.Name}' (host layer)");
        for (int i = 0; i < doc.Exclusions.Count; i++)
            if (doc.Exclusions[i].Layers is { } ls && ls.Contains(name))
                refs.Add($"exclusion[{i}] (layer filter)");
        for (int i = 0; i < doc.ScatterOverrides.Count; i++)
            if (doc.ScatterOverrides[i].Layers is { } ls && ls.Contains(name))
                refs.Add($"scatter override[{i}] (layer filter)");
        return refs;
    }
}

/// <summary>A command that carries a view-only visibility maintenance side effect (<see cref="Effect"/>): the
/// per-element hide set follows the element the command moves, removes, or renames, across execute / undo / redo.
/// Implemented by the reorder, remove, and rename commands and consumed by the <see cref="EditorDocument"/> command
/// events (<see cref="EditorDocument.CommandApplied"/> / <c>CommandUndone</c> / <c>CommandRedone</c>). Internal: the
/// effect is engine-private editor plumbing, never part of the reversible document, so a game head scripting a
/// command never sees or supplies it. The property is read at event time, not cached, so a command whose fields
/// mutate after construction (a merged <see cref="RenameRegionCommand"/>) always reports its live effect.</summary>
internal interface IVisibilityEffect
{
    /// <summary>The forward visibility maintenance this command implies (applied on execute / redo, inverted on
    /// undo). Read live, so it reflects any post-construction field mutation (region rename merge).</summary>
    VisibilityOp Effect { get; }
}

/// <summary>Which of the three visibility maintenance shapes a <see cref="VisibilityOp"/> carries.</summary>
internal enum VisibilityOpKind
{
    /// <summary>A list reorder: <see cref="EditorVisibility.RemapIndex"/> forward, the swapped remap inverted.</summary>
    Reorder,
    /// <summary>A list delete: <see cref="EditorVisibility.RemoveIndex"/> forward, <see cref="EditorVisibility.InsertIndex"/> inverted.</summary>
    RemoveAt,
    /// <summary>A key rename: <see cref="EditorVisibility.RenameKey"/> forward, the swapped rename inverted.</summary>
    Rename,
}

/// <summary>One unit of view-only visibility maintenance a command implies, mapping a document mutation onto the
/// editor's per-element hide set so a hide stays glued to its element. Applied forward on execute / redo
/// (<see cref="ApplyForward"/>) and inverted on undo (<see cref="ApplyInverse"/>), mirroring the command's own
/// Apply / Revert. Constructed through the three factories (<see cref="Reorder"/>, <see cref="RemoveAt"/>,
/// <see cref="Rename"/>). The index shapes leave the key fields null and the rename shape leaves the index fields
/// zero.</summary>
internal readonly struct VisibilityOp
{
    readonly VisibilityOpKind _kind;
    readonly SelectionKind _selectionKind;
    readonly int _from;
    readonly int _to;
    readonly string? _oldKey;
    readonly string? _newKey;

    VisibilityOp(VisibilityOpKind kind, SelectionKind selectionKind, int from, int to, string? oldKey, string? newKey)
    {
        _kind = kind;
        _selectionKind = selectionKind;
        _from = from;
        _to = to;
        _oldKey = oldKey;
        _newKey = newKey;
    }

    /// <summary>A reorder of <paramref name="kind"/> moving the element at <paramref name="from"/> to
    /// <paramref name="to"/>.</summary>
    public static VisibilityOp Reorder(SelectionKind kind, int from, int to) =>
        new VisibilityOp(VisibilityOpKind.Reorder, kind, from, to, null, null);

    /// <summary>A delete of the element at <paramref name="index"/> of <paramref name="kind"/>.</summary>
    public static VisibilityOp RemoveAt(SelectionKind kind, int index) =>
        new VisibilityOp(VisibilityOpKind.RemoveAt, kind, index, 0, null, null);

    /// <summary>A rename of <paramref name="kind"/> from <paramref name="oldKey"/> to <paramref name="newKey"/>.</summary>
    public static VisibilityOp Rename(SelectionKind kind, string oldKey, string newKey) =>
        new VisibilityOp(VisibilityOpKind.Rename, kind, 0, 0, oldKey, newKey);

    /// <summary>Applies the maintenance in the forward direction (on execute / redo) to <paramref name="visibility"/>.</summary>
    public void ApplyForward(EditorVisibility visibility)
    {
        switch (_kind)
        {
            case VisibilityOpKind.Reorder: visibility.RemapIndex(_selectionKind, _from, _to); break;
            case VisibilityOpKind.RemoveAt: visibility.RemoveIndex(_selectionKind, _from); break;
            case VisibilityOpKind.Rename: visibility.RenameKey(_selectionKind, _oldKey!, _newKey!); break;
        }
    }

    /// <summary>Applies the inverse maintenance (on undo) to <paramref name="visibility"/>, mirroring the command's
    /// own Revert: a reorder swaps its endpoints, a remove becomes an insert, and a rename swaps its keys.</summary>
    public void ApplyInverse(EditorVisibility visibility)
    {
        switch (_kind)
        {
            case VisibilityOpKind.Reorder: visibility.RemapIndex(_selectionKind, _to, _from); break;
            case VisibilityOpKind.RemoveAt: visibility.InsertIndex(_selectionKind, _from); break;
            case VisibilityOpKind.Rename: visibility.RenameKey(_selectionKind, _newKey!, _oldKey!); break;
        }
    }
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
    public override void Apply(MapDocument doc) => ApplyAppend(doc.Placements, _placement);

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => RevertAppend(doc.Placements);

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
public sealed class RenamePlacementCommand : EditorCommand, IVisibilityEffect
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
    VisibilityOp IVisibilityEffect.Effect => VisibilityOp.Rename(SelectionKind.Placement, _oldId, _newId);

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
    public override void Apply(MapDocument doc) => ApplyAppend(doc.Spawns, _spawn);

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => RevertAppend(doc.Spawns);

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

/// <summary>Sets a spawn's archetype id (which NPC kind the game spawns). The Archetype row is a free-typed
/// <see cref="KhaozEngine.Gui.TextRow"/> like the rename rows, committing on every keystroke, so successive
/// same-id sets coalesce (<see cref="TryMerge"/>) into one undo step that restores the pre-retype archetype,
/// mirroring <see cref="SetPlayerSpawnYawCommand"/>.</summary>
public sealed class SetSpawnArchetypeCommand : EditorCommand
{
    readonly string _id;
    string _newArchetypeId;
    string _oldArchetypeId = "";
    bool _captured;

    /// <summary>Creates the command setting spawn <paramref name="id"/>'s archetype id to
    /// <paramref name="newArchetypeId"/>.</summary>
    public SetSpawnArchetypeCommand(string id, string newArchetypeId)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _newArchetypeId = newArchetypeId ?? throw new ArgumentNullException(nameof(newArchetypeId));
    }

    /// <inheritdoc/>
    public override string Label => "Set spawn archetype";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapSpawn s = FindSpawn(doc, _id);
        if (!_captured) { _oldArchetypeId = s.ArchetypeId; _captured = true; }
        s.ArchetypeId = _newArchetypeId;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => FindSpawn(doc, _id).ArchetypeId = _oldArchetypeId;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is SetSpawnArchetypeCommand a && string.Equals(a._id, _id, StringComparison.Ordinal))
        {
            _newArchetypeId = a._newArchetypeId;
            return true;
        }
        return false;
    }
}

/// <summary>Renames a spawn. Spawns are keyed by id, so the id-carrying selection follows the rename. The target
/// id must be unique: <see cref="Apply"/> throws (before it mutates) if a spawn already carries the new id, so a
/// rejected rename lands no undo step. Renames never coalesce (no merge).</summary>
public sealed class RenameSpawnCommand : EditorCommand, IVisibilityEffect
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
    VisibilityOp IVisibilityEffect.Effect => VisibilityOp.Rename(SelectionKind.Spawn, _oldId, _newId);

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        GuardNoSpawn(doc, _newId);   // reject a duplicate target before touching the source
        FindSpawn(doc, _oldId).Id = _newId;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => FindSpawn(doc, _newId).Id = _oldId;
}

// ---- player spawns ---------------------------------------------------------------------------------------

/// <summary>Appends a player start marker to the document. Player spawns are game-interpreted (which one a game
/// uses at runtime is game code's concern), so like NPC spawns this does not affect the procedural world build.
/// Absorbs a same-id <see cref="MovePlayerSpawnCommand"/> that immediately follows (place-and-adjust), so a
/// just-placed player spawn can be dragged into position within the same gesture and the whole thing stays ONE
/// undo step whose <see cref="Revert"/> removes the spawn. The incoming spawn is deep-copied at construction
/// (fresh Tags list), so the command never aliases the caller's mutable state.</summary>
public sealed class AddPlayerSpawnCommand : EditorCommand
{
    readonly MapPlayerSpawn _spawn;

    /// <summary>Creates the command for the given player spawn (a deep copy is added on <see cref="Apply"/>).</summary>
    public AddPlayerSpawnCommand(MapPlayerSpawn spawn) =>
        _spawn = ClonePlayerSpawn(spawn ?? throw new ArgumentNullException(nameof(spawn)));

    /// <inheritdoc/>
    public override string Label => "Add player spawn";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => ApplyAppend(doc.PlayerSpawns, _spawn);

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => RevertAppend(doc.PlayerSpawns);

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        // Fold a same-id move into the Add: the placed spawn's final position becomes part of the Add itself, so
        // place-and-adjust is one undo step and Revert still just removes the spawn.
        if (next is MovePlayerSpawnCommand m && string.Equals(m.Id, _spawn.Id, StringComparison.Ordinal))
        {
            _spawn.X = m.NewX;
            _spawn.Z = m.NewZ;
            return true;
        }
        return false;
    }
}

/// <summary>Removes the player spawn with the given id, restoring it at its original index on revert.</summary>
public sealed class RemovePlayerSpawnCommand : EditorCommand
{
    readonly string _id;
    MapPlayerSpawn? _removed;
    int _index = -1;

    /// <summary>Creates the command for the player spawn id to remove.</summary>
    public RemovePlayerSpawnCommand(string id) =>
        _id = id ?? throw new ArgumentNullException(nameof(id));

    /// <inheritdoc/>
    public override string Label => "Remove player spawn";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapPlayerSpawn s = FindPlayerSpawn(doc, _id);
        _index = doc.PlayerSpawns.IndexOf(s);
        _removed = s;
        doc.PlayerSpawns.RemoveAt(_index);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (_removed is null) throw new InvalidOperationException("Revert called before Apply.");
        doc.PlayerSpawns.Insert(_index, _removed);
    }
}

/// <summary>Moves a player spawn to a new XZ. Successive moves of the same spawn coalesce.</summary>
public sealed class MovePlayerSpawnCommand : EditorCommand
{
    readonly string _id;
    float _newX, _newZ;
    float _oldX, _oldZ;
    bool _captured;

    /// <summary>Creates the command moving player spawn <paramref name="id"/> to (<paramref name="newX"/>,
    /// <paramref name="newZ"/>).</summary>
    public MovePlayerSpawnCommand(string id, float newX, float newZ)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _newX = newX;
        _newZ = newZ;
    }

    /// <summary>The moved spawn's id, so <see cref="AddPlayerSpawnCommand.TryMerge"/> can match a same-id move.</summary>
    internal string Id => _id;
    /// <summary>The target X this move sets, exposed so an absorbing Add can fold in the final position.</summary>
    internal float NewX => _newX;
    /// <summary>The target Z this move sets, exposed so an absorbing Add can fold in the final position.</summary>
    internal float NewZ => _newZ;

    /// <inheritdoc/>
    public override string Label => "Move player spawn";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapPlayerSpawn s = FindPlayerSpawn(doc, _id);
        if (!_captured) { _oldX = s.X; _oldZ = s.Z; _captured = true; }
        s.X = _newX;
        s.Z = _newZ;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        MapPlayerSpawn s = FindPlayerSpawn(doc, _id);
        s.X = _oldX;
        s.Z = _oldZ;
    }

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is MovePlayerSpawnCommand m && string.Equals(m._id, _id, StringComparison.Ordinal))
        {
            _newX = m._newX;
            _newZ = m._newZ;
            return true;
        }
        return false;
    }
}

/// <summary>Sets a player spawn's yaw (facing), in raw radians (no degree conversion in this editor). Mirrors
/// <see cref="RotatePlacementCommand"/>: successive scrubs of the same spawn's yaw coalesce into one undo step
/// that restores the pre-scrub yaw.</summary>
public sealed class SetPlayerSpawnYawCommand : EditorCommand
{
    readonly string _id;
    float _newYaw;
    float _oldYaw;
    bool _captured;

    /// <summary>Creates the command setting player spawn <paramref name="id"/>'s yaw to <paramref name="newYaw"/>.</summary>
    public SetPlayerSpawnYawCommand(string id, float newYaw)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _newYaw = newYaw;
    }

    /// <inheritdoc/>
    public override string Label => "Set player spawn yaw";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapPlayerSpawn s = FindPlayerSpawn(doc, _id);
        if (!_captured) { _oldYaw = s.Yaw; _captured = true; }
        s.Yaw = _newYaw;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => FindPlayerSpawn(doc, _id).Yaw = _oldYaw;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is SetPlayerSpawnYawCommand y && string.Equals(y._id, _id, StringComparison.Ordinal))
        {
            _newYaw = y._newYaw;
            return true;
        }
        return false;
    }
}

/// <summary>Toggles a player spawn's enabled flag.</summary>
public sealed class SetPlayerSpawnEnabledCommand : EditorCommand
{
    readonly string _id;
    readonly bool _enabled;
    bool _old;
    bool _captured;

    /// <summary>Creates the command setting player spawn <paramref name="id"/>'s enabled flag to
    /// <paramref name="enabled"/>.</summary>
    public SetPlayerSpawnEnabledCommand(string id, bool enabled)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _enabled = enabled;
    }

    /// <inheritdoc/>
    public override string Label => "Set player spawn enabled";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapPlayerSpawn s = FindPlayerSpawn(doc, _id);
        if (!_captured) { _old = s.Enabled; _captured = true; }
        s.Enabled = _enabled;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => FindPlayerSpawn(doc, _id).Enabled = _old;
}

/// <summary>Renames a player spawn. Player spawns are keyed by id, so the id-carrying selection follows the
/// rename. The target id must be unique: <see cref="Apply"/> throws (before it mutates) if a player spawn already
/// carries the new id, so a rejected rename lands no undo step. The in-Apply guard is load-bearing here because
/// the editor GUI executes renames without a validate-and-revert net. Renames never coalesce (no merge).</summary>
public sealed class RenamePlayerSpawnCommand : EditorCommand, IVisibilityEffect
{
    readonly string _oldId;
    readonly string _newId;

    /// <summary>Creates the command renaming player spawn <paramref name="oldId"/> to <paramref name="newId"/>.</summary>
    public RenamePlayerSpawnCommand(string oldId, string newId)
    {
        _oldId = oldId ?? throw new ArgumentNullException(nameof(oldId));
        _newId = newId ?? throw new ArgumentNullException(nameof(newId));
    }

    /// <inheritdoc/>
    public override string Label => "Rename player spawn";
    internal override bool AffectsWorld => false;
    VisibilityOp IVisibilityEffect.Effect => VisibilityOp.Rename(SelectionKind.PlayerSpawn, _oldId, _newId);

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        GuardNoPlayerSpawn(doc, _newId);   // reject a duplicate target before touching the source
        FindPlayerSpawn(doc, _oldId).Id = _newId;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => FindPlayerSpawn(doc, _newId).Id = _oldId;
}

// ---- exclusions (terrain-shape affecting) ----------------------------------------------------------------

/// <summary>Appends a scatter exclusion shape. Affects the streamed world (scatter inputs change).</summary>
public sealed class AddExclusionCommand : EditorCommand
{
    readonly MapExclusion _exclusion;

    /// <summary>Creates the command for the given exclusion (added on <see cref="Apply"/>).</summary>
    public AddExclusionCommand(MapExclusion exclusion) =>
        _exclusion = exclusion ?? throw new ArgumentNullException(nameof(exclusion));

    // Dirty-region margin (ShapeGeometry.BoundsMarginFor: base constant + the doc's largest scatter jitter),
    // captured at Apply. Revert reuses it: undo is LIFO, so any later jitter edit is undone first and the
    // captured value still matches the doc at Revert time.
    float? _boundsMargin;

    /// <inheritdoc/>
    public override string Label => "Add exclusion";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    /// <remarks>The added exclusion's shape bounds padded by the captured jitter-aware margin, or null before
    /// <see cref="Apply"/> has captured that margin or when its shape has no bounded AABB (no shape, or an empty
    /// polygon).</remarks>
    internal override RectArea? DirtyRegion =>
        _boundsMargin is float m && ShapeGeometry.TryBounds(_exclusion.Shape, m, out RectArea area) ? area : null;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        _boundsMargin = ShapeGeometry.BoundsMarginFor(doc);
        ApplyAppend(doc.Exclusions, _exclusion);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => RevertAppend(doc.Exclusions);
}

/// <summary>Removes the exclusion at the given index, restoring it at that index on revert. Affects the
/// streamed world.</summary>
public sealed class RemoveExclusionCommand : EditorCommand, IVisibilityEffect
{
    readonly int _index;
    MapExclusion? _removed;

    /// <summary>Creates the command for the exclusion list index to remove.</summary>
    public RemoveExclusionCommand(int index) => _index = index;

    /// <inheritdoc/>
    public override string Label => "Remove exclusion";
    internal override bool AffectsWorld => true;
    VisibilityOp IVisibilityEffect.Effect => VisibilityOp.RemoveAt(SelectionKind.Exclusion, _index);

    // Dirty-region margin (ShapeGeometry.BoundsMarginFor), captured at Apply alongside _removed. Revert reuses
    // it (LIFO undo keeps it current, see AddExclusionCommand's matching field).
    float? _boundsMargin;

    /// <inheritdoc/>
    /// <remarks>The removed exclusion's shape bounds padded by the captured jitter-aware margin, or null before
    /// <see cref="Apply"/> has captured both (the removed value is not known until then) or when its shape has no
    /// bounded AABB (the <see cref="RemoveFeatureCommand"/> capture-timing idiom).</remarks>
    internal override RectArea? DirtyRegion =>
        _removed is not null && _boundsMargin is float m && ShapeGeometry.TryBounds(_removed.Shape, m, out RectArea area)
            ? area : null;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        int count = doc.Exclusions.Count;
        if (_index < 0 || _index >= count)
            throw new ArgumentOutOfRangeException(nameof(_index), _index, $"RemoveExclusionCommand: index is out of range (count {count}).");
        _boundsMargin = ShapeGeometry.BoundsMarginFor(doc);
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

    // Dirty-region margin (ShapeGeometry.BoundsMarginFor), captured at Apply. Revert and post-merge reads reuse
    // it: undo is LIFO and a jitter edit never merges into a shape edit (different command types), so the
    // captured value always matches the doc when DirtyRegion is read.
    float? _boundsMargin;

    /// <inheritdoc/>
    /// <remarks>The union of the old and new shape bounds padded by the captured jitter-aware margin (both
    /// endpoints of a drag or scrub change scatter), or null before <see cref="Apply"/> has captured that margin
    /// or when either endpoint has no bounded AABB. The bounds are computed live from the CURRENT
    /// <see cref="_oldShape"/> / <see cref="_newShape"/> every read, never cached: <see cref="TryMerge"/>
    /// rewrites <see cref="_newShape"/> as a drag coalesces, so a cached region would stop covering the latest
    /// endpoint mid-drag (the <see cref="EditFeatureCommand"/> idiom).</remarks>
    internal override RectArea? DirtyRegion
    {
        get
        {
            if (_boundsMargin is not float m) return null;
            if (!ShapeGeometry.TryBounds(_oldShape, m, out RectArea oldArea)) return null;
            if (!ShapeGeometry.TryBounds(_newShape, m, out RectArea newArea)) return null;
            return FeatureGeometry.Union(oldArea, newArea);
        }
    }

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        _boundsMargin = ShapeGeometry.BoundsMarginFor(doc);
        doc.Exclusions[_index].Shape = _newShape;
    }

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
    MapShapeDoc? _shape;   // the exclusion's own shape, captured on Apply/Revert (this command never changes it)
    float? _boundsMargin;   // dirty-region margin (ShapeGeometry.BoundsMarginFor), captured alongside _shape

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
    /// <remarks>The exclusion's own shape bounds padded by the captured jitter-aware margin: this command changes
    /// which layers the exclusion applies to, not the shape, but scatter is still only rejected inside that
    /// shape, so the shape's bounds are the whole dirty reach. Null before the first
    /// <see cref="Apply"/>/<see cref="Revert"/> has captured the shape and margin (the
    /// <see cref="RemoveFeatureCommand"/> capture-timing idiom), or when the shape has no bounded AABB.</remarks>
    internal override RectArea? DirtyRegion =>
        _shape is not null && _boundsMargin is float m && ShapeGeometry.TryBounds(_shape, m, out RectArea area)
            ? area : null;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        _shape = doc.Exclusions[_index].Shape;
        _boundsMargin = ShapeGeometry.BoundsMarginFor(doc);
        doc.Exclusions[_index].Layers = _newLayers?.ToList();
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        _shape = doc.Exclusions[_index].Shape;
        _boundsMargin = ShapeGeometry.BoundsMarginFor(doc);
        doc.Exclusions[_index].Layers = _oldLayers?.ToList();
    }

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

// ---- scatter overrides (terrain-scatter affecting) ---------------------------------------------------------

/// <summary>Appends a scatter override (a region-scoped density multiplier / kind substitution tweak). Affects
/// the streamed world (scatter inputs change).</summary>
public sealed class AddScatterOverrideCommand : EditorCommand
{
    readonly MapScatterOverrideDoc _override;

    /// <summary>Creates the command for the given scatter override (added on <see cref="Apply"/>).</summary>
    public AddScatterOverrideCommand(MapScatterOverrideDoc value) =>
        _override = value ?? throw new ArgumentNullException(nameof(value));

    // Dirty-region margin (ShapeGeometry.BoundsMarginFor), captured at Apply and reused by Revert (LIFO undo
    // keeps it current, see AddExclusionCommand's matching field).
    float? _boundsMargin;

    /// <inheritdoc/>
    public override string Label => "Add scatter override";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    /// <remarks>The added override's shape bounds padded by the captured jitter-aware margin, or null before
    /// <see cref="Apply"/> has captured that margin or when its shape has no bounded AABB (no shape, or an empty
    /// polygon).</remarks>
    internal override RectArea? DirtyRegion =>
        _boundsMargin is float m && ShapeGeometry.TryBounds(_override.Shape, m, out RectArea area) ? area : null;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        _boundsMargin = ShapeGeometry.BoundsMarginFor(doc);
        ApplyAppend(doc.ScatterOverrides, _override);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => RevertAppend(doc.ScatterOverrides);
}

/// <summary>Removes the scatter override at the given index, restoring it at that index on revert. Affects the
/// streamed world.</summary>
public sealed class RemoveScatterOverrideCommand : EditorCommand, IVisibilityEffect
{
    readonly int _index;
    MapScatterOverrideDoc? _removed;

    /// <summary>Creates the command for the scatter override list index to remove.</summary>
    public RemoveScatterOverrideCommand(int index) => _index = index;

    /// <inheritdoc/>
    public override string Label => "Remove scatter override";
    internal override bool AffectsWorld => true;
    VisibilityOp IVisibilityEffect.Effect => VisibilityOp.RemoveAt(SelectionKind.ScatterOverride, _index);

    // Dirty-region margin (ShapeGeometry.BoundsMarginFor), captured at Apply alongside _removed. Revert reuses
    // it (LIFO undo keeps it current, see AddExclusionCommand's matching field).
    float? _boundsMargin;

    /// <inheritdoc/>
    /// <remarks>The removed override's shape bounds padded by the captured jitter-aware margin, or null before
    /// <see cref="Apply"/> has captured both (the removed value is not known until then) or when its shape has no
    /// bounded AABB (the <see cref="RemoveFeatureCommand"/> capture-timing idiom).</remarks>
    internal override RectArea? DirtyRegion =>
        _removed is not null && _boundsMargin is float m && ShapeGeometry.TryBounds(_removed.Shape, m, out RectArea area)
            ? area : null;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        int count = doc.ScatterOverrides.Count;
        if (_index < 0 || _index >= count)
            throw new ArgumentOutOfRangeException(nameof(_index), _index, $"RemoveScatterOverrideCommand: index is out of range (count {count}).");
        _boundsMargin = ShapeGeometry.BoundsMarginFor(doc);
        _removed = doc.ScatterOverrides[_index];
        doc.ScatterOverrides.RemoveAt(_index);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (_removed is null) throw new InvalidOperationException("Revert called before Apply.");
        doc.ScatterOverrides.Insert(_index, _removed);
    }
}

/// <summary>Replaces the shape of the scatter override at a given index with a new value (gizmo drag or parameter
/// scrub). The caller supplies both the new and old shape, cloned with the changed field (the
/// <see cref="EditExclusionShapeCommand"/> idiom). Successive edits of the same index coalesce (drag/scrub
/// coalescing). Affects the streamed world (scatter inputs change).</summary>
public sealed class EditScatterOverrideShapeCommand : EditorCommand
{
    readonly int _index;
    MapShapeDoc _newShape;
    readonly MapShapeDoc _oldShape;

    /// <summary>Creates the command replacing scatter override <paramref name="index"/>'s shape with
    /// <paramref name="newShape"/>, capturing <paramref name="oldShape"/> for revert.</summary>
    public EditScatterOverrideShapeCommand(int index, MapShapeDoc newShape, MapShapeDoc oldShape)
    {
        _index = index;
        _newShape = newShape ?? throw new ArgumentNullException(nameof(newShape));
        _oldShape = oldShape ?? throw new ArgumentNullException(nameof(oldShape));
    }

    /// <inheritdoc/>
    public override string Label => "Edit scatter override shape";
    internal override bool AffectsWorld => true;

    // Dirty-region margin (ShapeGeometry.BoundsMarginFor), captured at Apply. Revert and post-merge reads reuse
    // it (see EditExclusionShapeCommand's matching field).
    float? _boundsMargin;

    /// <inheritdoc/>
    /// <remarks>The union of the old and new shape bounds padded by the captured jitter-aware margin (both
    /// endpoints of a drag or scrub change scatter), or null before <see cref="Apply"/> has captured that margin
    /// or when either endpoint has no bounded AABB. The bounds are computed live from the CURRENT
    /// <see cref="_oldShape"/> / <see cref="_newShape"/> every read, never cached: <see cref="TryMerge"/>
    /// rewrites <see cref="_newShape"/> as a drag coalesces, so a cached region would stop covering the latest
    /// endpoint mid-drag (the <see cref="EditExclusionShapeCommand"/> idiom).</remarks>
    internal override RectArea? DirtyRegion
    {
        get
        {
            if (_boundsMargin is not float m) return null;
            if (!ShapeGeometry.TryBounds(_oldShape, m, out RectArea oldArea)) return null;
            if (!ShapeGeometry.TryBounds(_newShape, m, out RectArea newArea)) return null;
            return FeatureGeometry.Union(oldArea, newArea);
        }
    }

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        _boundsMargin = ShapeGeometry.BoundsMarginFor(doc);
        doc.ScatterOverrides[_index].Shape = _newShape;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.ScatterOverrides[_index].Shape = _oldShape;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is EditScatterOverrideShapeCommand e && e._index == _index)
        {
            _newShape = e._newShape;
            return true;
        }
        return false;
    }
}

/// <summary>Replaces the density/kinds/layers of the scatter override at a given index with a new whole value
/// (the <see cref="EditScatterLayerCommand"/> whole-value idiom, index-addressed the way
/// <see cref="EditExclusionShapeCommand"/> is). Unlike that idiom's usual caller-clones-it-all trust, this
/// constructor defensively deep-copies both values itself: it builds fresh <see cref="MapScatterOverrideDoc"/>
/// instances with copied Kinds/Layers lists (the <see cref="EditExclusionLayersCommand"/> copy discipline
/// extended to a whole value), so neither the command, the document, nor the caller's own instances can alias one
/// another. Shape is carried through unchanged, by reference: this command must NOT be used for shape edits,
/// because <see cref="Apply"/> replaces the WHOLE override including Shape, so a caller who wants to move or
/// resize the shape must go through <see cref="EditScatterOverrideShapeCommand"/> instead, whose own coalescing
/// lets a gizmo drag merge independently of any values edit in flight on the same override. Likewise Name is
/// carried through unchanged: a rename goes through <see cref="RenameScatterOverrideCommand"/>. Successive edits
/// of the same index coalesce (scrub coalescing). Affects the streamed world (scatter inputs change).</summary>
public sealed class EditScatterOverrideValuesCommand : EditorCommand
{
    readonly int _index;
    MapScatterOverrideDoc _newValue;
    readonly MapScatterOverrideDoc _oldValue;

    /// <summary>Creates the command replacing scatter override <paramref name="index"/>'s values with a deep copy
    /// of <paramref name="newValue"/>, capturing a deep copy of <paramref name="oldValue"/> for revert.</summary>
    public EditScatterOverrideValuesCommand(int index, MapScatterOverrideDoc newValue, MapScatterOverrideDoc oldValue)
    {
        _index = index;
        _newValue = Clone(newValue ?? throw new ArgumentNullException(nameof(newValue)));
        _oldValue = Clone(oldValue ?? throw new ArgumentNullException(nameof(oldValue)));
    }

    /// <inheritdoc/>
    public override string Label => "Edit scatter override values";
    internal override bool AffectsWorld => true;

    // Dirty-region margin (ShapeGeometry.BoundsMarginFor), captured at Apply and reused by Revert (LIFO undo
    // keeps it current, see AddExclusionCommand's matching field).
    float? _boundsMargin;

    /// <inheritdoc/>
    /// <remarks>The override's own shape bounds padded by the captured jitter-aware margin: Shape carries through
    /// unchanged (see the class doc comment), so <see cref="_newValue"/> and <see cref="_oldValue"/> share the
    /// same shape reference and either reads it. Null before <see cref="Apply"/> has captured the margin, or when
    /// the shape has no bounded AABB.</remarks>
    internal override RectArea? DirtyRegion =>
        _boundsMargin is float m && ShapeGeometry.TryBounds(_newValue.Shape, m, out RectArea area) ? area : null;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        _boundsMargin = ShapeGeometry.BoundsMarginFor(doc);
        doc.ScatterOverrides[_index] = _newValue;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.ScatterOverrides[_index] = _oldValue;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is EditScatterOverrideValuesCommand e && e._index == _index)
        {
            _newValue = e._newValue;
            return true;
        }
        return false;
    }

    // Deep-copies a scatter override's Kinds/Layers lists so the command's stored value never aliases the
    // caller's own instance (nor the document's), the EditExclusionLayersCommand discipline extended to a whole
    // value. Shape is shared by reference: this command never touches it (see the class doc comment).
    static MapScatterOverrideDoc Clone(MapScatterOverrideDoc v) => new MapScatterOverrideDoc
    {
        Name = v.Name,
        Shape = v.Shape,
        DensityMultiplier = v.DensityMultiplier,
        Kinds = v.Kinds?.ToList(),
        Layers = v.Layers?.ToList(),
    };
}

/// <summary>Renames the scatter override at a given index. Scatter overrides are index-addressed (no independent
/// id, the same idiom as <see cref="RenameExclusionCommand"/>), so this targets the list position rather than an
/// old-name lookup. Both names are nullable directly here, rather than the empty-string convention
/// <see cref="RenameExclusionCommand"/> uses: null and empty both normalize to unnamed, so a cleared name never
/// persists as a bloating empty name key. Successive renames of the same index coalesce into one undo step (a
/// text field committed on every keystroke stays one undo). The target name must be unique among named scatter
/// overrides: <see cref="Apply"/> throws (before it mutates) if
/// another scatter override already carries the new name, so a rejected rename lands no undo step (the guard
/// idiom). Renaming does not change the override's shape, density, kinds, or layer filter, so this does not
/// affect the streamed world.</summary>
public sealed class RenameScatterOverrideCommand : EditorCommand
{
    readonly int _index;
    string? _newName;
    readonly string? _oldName;

    /// <summary>Creates the command renaming the scatter override at <paramref name="index"/> to
    /// <paramref name="newName"/>, capturing <paramref name="oldName"/> for revert.</summary>
    public RenameScatterOverrideCommand(int index, string? newName, string? oldName)
    {
        _index = index;
        _newName = newName;
        _oldName = oldName;
    }

    /// <inheritdoc/>
    public override string Label => "Rename scatter override";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        string? normalized = NormalizeName(_newName);
        GuardNoScatterOverrideName(doc, normalized, _index);   // reject a duplicate target before touching the source
        doc.ScatterOverrides[_index].Name = normalized;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.ScatterOverrides[_index].Name = NormalizeName(_oldName);

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is RenameScatterOverrideCommand r && r._index == _index)
        {
            _newName = r._newName;
            return true;
        }
        return false;
    }
}

/// <summary>Moves a scatter override from one list position to another. Unlike <see cref="ReorderExclusionCommand"/>,
/// this DOES affect the streamed world: a scatter's override lookup resolves the FIRST matching override in list
/// order (first-match-wins, see <c>PropScatter</c>'s internal OverrideFor helper), so which override governs a
/// given patch of ground, and therefore the density multiplier and kind substitution the scatter applies there,
/// depends on order. Exclusions, by contrast, combine as a pure set union where order never changes which ground
/// ends up excluded, so that reasoning does not carry over here: <see cref="AffectsWorld"/> stays true and a
/// reorder forces a full viewport world rebuild. Both indices are range-guarded (non-negative in the constructor,
/// in-range against the live list at apply time, each with a precise <see cref="ArgumentOutOfRangeException"/>).
/// <see cref="Revert"/> moves it back (self-inverse), and it never coalesces (no merge).</summary>
public sealed class ReorderScatterOverrideCommand : EditorCommand, IVisibilityEffect
{
    readonly int _fromIndex;
    readonly int _toIndex;

    // The overrides spanning the inclusive [min(from,to), max(from,to)] index range, captured after Apply/Revert
    // moves them (see the DirtyRegion doc comment for why that range, not just the two endpoints). A shallow
    // GetRange copy: same object references as the live list, so a later mutation of one of these shapes (by a
    // different command entirely) is still read live through DirtyRegion's TryBounds call, never a cached value.
    List<MapScatterOverrideDoc>? _affected;

    // Dirty-region margin (ShapeGeometry.BoundsMarginFor), captured alongside _affected on both Apply and Revert.
    float? _boundsMargin;

    /// <summary>Creates the command moving the scatter override at <paramref name="fromIndex"/> to
    /// <paramref name="toIndex"/> in the scatter override list. Both must be non-negative.</summary>
    public ReorderScatterOverrideCommand(int fromIndex, int toIndex)
    {
        if (fromIndex < 0) throw new ArgumentOutOfRangeException(nameof(fromIndex), fromIndex, "Scatter override index must be non-negative.");
        if (toIndex < 0) throw new ArgumentOutOfRangeException(nameof(toIndex), toIndex, "Scatter override index must be non-negative.");
        _fromIndex = fromIndex;
        _toIndex = toIndex;
    }

    /// <inheritdoc/>
    public override string Label => "Reorder scatter override";
    internal override bool AffectsWorld => true;
    VisibilityOp IVisibilityEffect.Effect => VisibilityOp.Reorder(SelectionKind.ScatterOverride, _fromIndex, _toIndex);

    /// <inheritdoc/>
    /// <remarks>The union of every override shape's bounds (each padded by the captured jitter-aware margin)
    /// across the inclusive index range [min(from,to), max(from,to)], read live off <see cref="_affected"/>
    /// (captured after <see cref="Apply"/> / <see cref="Revert"/>, the <see cref="RemoveFeatureCommand"/>
    /// capture-timing idiom: null before either has run). A reorder's precise dirty reach is genuinely the whole
    /// range, not just the two endpoints: moving one override past the others also flips ITS relative
    /// first-match-wins order against every override sandwiched in between, so any of them could start (or stop)
    /// governing ground wherever it overlaps another override in the range, not only where the moved one overlaps
    /// something. The full range union is a cheap, honest cover for that (scatter overrides are few in practice).
    /// Narrowing to just the two endpoints would silently drop the sandwiched ones' own reach. Null when any
    /// override shape in the range has no bounded AABB (an empty polygon), which forces a full rebuild rather
    /// than risk an incomplete partial one.</remarks>
    internal override RectArea? DirtyRegion
    {
        get
        {
            if (_affected is null || _boundsMargin is not float m) return null;
            RectArea? union = null;
            foreach (MapScatterOverrideDoc o in _affected)
            {
                if (!ShapeGeometry.TryBounds(o.Shape, m, out RectArea area)) return null;
                union = union is RectArea acc ? FeatureGeometry.Union(acc, area) : area;
            }
            return union;
        }
    }

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        Move(doc, _fromIndex, _toIndex);
        CaptureAffected(doc);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        Move(doc, _toIndex, _fromIndex);
        CaptureAffected(doc);
    }

    // A list move (remove at `from`, re-insert at `to`), its own inverse. Range-guards both endpoints against the
    // live list up front so a bad index is a precise ArgumentOutOfRangeException, not an opaque list throw.
    static void Move(MapDocument doc, int from, int to)
    {
        int count = doc.ScatterOverrides.Count;
        if (from >= count) throw new ArgumentOutOfRangeException(nameof(from), from, $"Scatter override index is out of range (count {count}).");
        if (to >= count) throw new ArgumentOutOfRangeException(nameof(to), to, $"Scatter override index is out of range (count {count}).");
        MapScatterOverrideDoc scatterOverride = doc.ScatterOverrides[from];
        doc.ScatterOverrides.RemoveAt(from);
        doc.ScatterOverrides.Insert(to, scatterOverride);
    }

    // Snapshots the [min(from,to), max(from,to)] slice of the (now-moved) list into _affected for DirtyRegion,
    // plus the doc's jitter-aware bounds margin. The SET of overrides occupying that index range is identical
    // before and after Move (only elements strictly between from and to shift within the range, and the moved
    // element itself lands inside it), so reading the range post-move gives the same set DirtyRegion would need
    // pre-move.
    void CaptureAffected(MapDocument doc)
    {
        int lo = Math.Min(_fromIndex, _toIndex), hi = Math.Max(_fromIndex, _toIndex);
        _affected = doc.ScatterOverrides.GetRange(lo, hi - lo + 1);
        _boundsMargin = ShapeGeometry.BoundsMarginFor(doc);
    }
}

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
        List<string> refs = ScatterLayerReferences(doc, _name);
        if (refs.Count > 0)
            throw new InvalidOperationException(
                $"Cannot remove scatter layer '{_name}': it is still referenced by {string.Join(", ", refs)}. Retarget or remove those first.");
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
public sealed class EditScatterLayerCommand : EditorCommand
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

// ---- companion layers (terrain-scatter affecting) ---------------------------------------------------------

/// <summary>Appends a named companion layer (props ringing a scatter layer's host placements). Names are
/// unique-required, so <see cref="Apply"/> rejects a duplicate name before mutating (the add-guard idiom). Note
/// the layer's HostLayer must name a real scatter layer for the document to validate on save (the standard
/// validator's host-layer rule), the same save-time invariant the editor's HostLayer chooser relies on. Affects
/// the streamed world.</summary>
public sealed class AddCompanionLayerCommand : EditorCommand
{
    readonly MapCompanionLayer _layer;

    /// <summary>Creates the command for the given companion layer (added on <see cref="Apply"/>).</summary>
    public AddCompanionLayerCommand(MapCompanionLayer layer) =>
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));

    /// <inheritdoc/>
    public override string Label => "Add companion layer";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        GuardNoCompanionLayerName(doc, _layer.Name, -1);   // reject a duplicate name before touching the list
        ApplyAppend(doc.CompanionLayers, _layer);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => RevertAppend(doc.CompanionLayers);
}

/// <summary>Removes the companion layer with the given name, restoring it at its original index on revert.
/// Nothing else in the document references a companion layer (they are leaf consumers of a scatter layer, not a
/// reference target), so unlike <see cref="RemoveScatterLayerCommand"/> there is no referenced-removal to reject.
/// Affects the streamed world.</summary>
public sealed class RemoveCompanionLayerCommand : EditorCommand
{
    readonly string _name;
    MapCompanionLayer? _removed;
    int _index = -1;

    /// <summary>Creates the command for the companion layer name to remove.</summary>
    public RemoveCompanionLayerCommand(string name) =>
        _name = name ?? throw new ArgumentNullException(nameof(name));

    /// <inheritdoc/>
    public override string Label => "Remove companion layer";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        _index = IndexOfCompanionLayer(doc, _name);
        _removed = doc.CompanionLayers[_index];
        doc.CompanionLayers.RemoveAt(_index);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (_removed is null) throw new InvalidOperationException("Revert called before Apply.");
        doc.CompanionLayers.Insert(_index, _removed);
    }
}

/// <summary>Replaces the companion layer with a given name with a new whole value (the
/// <see cref="EditScatterLayerCommand"/> idiom). The caller supplies a DEEP clone (nested HostKinds / Kinds
/// copied, not shared) plus the live value for revert. The Name stays the lookup key and is not edited here (a
/// rename goes through <see cref="RenameCompanionLayerCommand"/>). The HostLayer, by contrast, IS edited here (it
/// is a plain field, validated at save time). Successive same-named edits coalesce (scrub coalescing). Affects
/// the streamed world.</summary>
public sealed class EditCompanionLayerCommand : EditorCommand
{
    readonly string _name;
    MapCompanionLayer _newValue;
    readonly MapCompanionLayer _oldValue;

    /// <summary>Creates the command replacing companion layer <paramref name="name"/> with
    /// <paramref name="newValue"/>, capturing <paramref name="oldValue"/> for revert. Both must carry the same
    /// Name as <paramref name="name"/> (the edit never renames), so the same-name lookup still resolves after apply.</summary>
    public EditCompanionLayerCommand(string name, MapCompanionLayer newValue, MapCompanionLayer oldValue)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _newValue = newValue ?? throw new ArgumentNullException(nameof(newValue));
        _oldValue = oldValue ?? throw new ArgumentNullException(nameof(oldValue));
    }

    /// <inheritdoc/>
    public override string Label => "Edit companion layer";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.CompanionLayers[IndexOfCompanionLayer(doc, _name)] = _newValue;

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.CompanionLayers[IndexOfCompanionLayer(doc, _name)] = _oldValue;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is EditCompanionLayerCommand e && string.Equals(e._name, _name, StringComparison.Ordinal))
        {
            _newValue = e._newValue;
            return true;
        }
        return false;
    }
}

/// <summary>Renames a companion layer. Nothing references a companion layer by name (they are leaf consumers), so
/// unlike <see cref="RenameScatterLayerCommand"/> there is no cascade: this just renames the one layer. The target
/// name must be unique among companion layers: <see cref="Apply"/> throws (before it mutates) if another companion
/// already carries the new name, so a rejected rename lands no undo step (the guard idiom). A chained rename
/// coalesces (the same-name-pair a per-keystroke commit produces, the <see cref="RenameRegionCommand"/> merge
/// idiom). Renaming changes nothing streamed, so it does not affect the world.</summary>
public sealed class RenameCompanionLayerCommand : EditorCommand
{
    readonly string _oldName;
    string _newName;

    /// <summary>Creates the command renaming companion layer <paramref name="oldName"/> to <paramref name="newName"/>.</summary>
    public RenameCompanionLayerCommand(string oldName, string newName)
    {
        _oldName = oldName ?? throw new ArgumentNullException(nameof(oldName));
        _newName = newName ?? throw new ArgumentNullException(nameof(newName));
    }

    /// <inheritdoc/>
    public override string Label => "Rename companion layer";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        GuardNoCompanionLayerName(doc, _newName, IndexOfCompanionLayer(doc, _oldName));   // reject a duplicate target before touching the source
        doc.CompanionLayers[IndexOfCompanionLayer(doc, _oldName)].Name = _newName;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.CompanionLayers[IndexOfCompanionLayer(doc, _newName)].Name = _oldName;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is RenameCompanionLayerCommand r && string.Equals(r._oldName, _newName, StringComparison.Ordinal))
        {
            _newName = r._newName;
            return true;
        }
        return false;
    }
}

// ---- regions (game-interpreted markers, not terrain-affecting) --------------------------------------------

/// <summary>Appends a named region marker. A region's name IS its identity and is unique-required (the
/// validator), so <see cref="Apply"/> rejects a duplicate name before it mutates anything (the add-guard idiom
/// shared with scatter and companion layers), leaving no undo step on a reject rather than deferring the
/// collision to save-time validation. Regions are game-interpreted, so this does not affect the streamed
/// world.</summary>
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
    public override void Apply(MapDocument doc)
    {
        GuardNoRegion(doc, _region.Name, -1);   // reject a duplicate name before touching the list
        ApplyAppend(doc.Regions, _region);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => RevertAppend(doc.Regions);
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
public sealed class RenameRegionCommand : EditorCommand, IVisibilityEffect
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
    // Reads _newName LIVE (not cached): a TryMerge folds a follow-on rename into this command, advancing _newName,
    // so the visibility effect the document events read after a merge always targets the merged final name.
    VisibilityOp IVisibilityEffect.Effect => VisibilityOp.Rename(SelectionKind.Region, _oldName, _newName);

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        GuardNoRegion(doc, _newName, IndexOfRegion(doc, _oldName));   // reject a duplicate target before touching the source
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

/// <summary>Edits the terrain's global scalar settings: any subset of water level, noise seed, biome blend width,
/// the gentle-noise frequency / amplitude, and the detail-noise frequency / octave count (<see cref="MapTerrain"/>).
/// One command carries all seven as nullable fields, and only the set ones (a non-null <c>new*</c>) are applied and
/// reverted, so a single-field inspector edit touches nothing else (only-set-fields apply). Each set field carries
/// its own captured old value for revert, supplied by the caller (the live value before the edit), the same idiom
/// the old water-only command used.
/// <para>
/// Successive terrain edits of one gesture coalesce into ONE undo step (scrub coalescing). Terrain is a singleton,
/// so there is no id / index to match: any two terrain edits merge. The merge is a per-field UNION with
/// first-old / last-new semantics: a field the absorbed command sets is folded in with its new value winning, and
/// its old value taken from whichever command FIRST set that field (a later same-field scrub keeps the original
/// old, so one undo still reverts the whole gesture to its pre-edit state). This means a water-only edit and a
/// seed-only edit of the same gesture collapse to one step carrying BOTH, each revertible to its own first-setter
/// old. Affects the streamed world: scatter honours the water level and the noise fields shape the terrain, so any
/// change forces a wholesale rebuild. The water surface itself derives live from the document, so it also updates
/// on the same edit.</para></summary>
public sealed class EditTerrainCommand : EditorCommand
{
    float? _newWaterLevel, _oldWaterLevel;
    int? _newSeed, _oldSeed;
    float? _newBiomeBlend, _oldBiomeBlend;
    float? _newGentleFrequency, _oldGentleFrequency;
    float? _newGentleAmplitude, _oldGentleAmplitude;
    float? _newDetailFrequency, _oldDetailFrequency;
    int? _newDetailOctaves, _oldDetailOctaves;

    /// <summary>Creates a terrain-scalar edit. Pass a <c>new*</c> / <c>old*</c> pair for each field this edit
    /// changes and leave the rest null: only fields with a non-null <c>new*</c> are applied and reverted. A field
    /// whose <c>new*</c> is supplied MUST also supply its <c>old*</c> (needed for revert), else the constructor
    /// throws. The <c>old*</c> is the live value the caller read before the edit, so a scrub merge preserves the
    /// first command's old per field.</summary>
    public EditTerrainCommand(
        float? newWaterLevel = null, float? oldWaterLevel = null,
        int? newSeed = null, int? oldSeed = null,
        float? newBiomeBlend = null, float? oldBiomeBlend = null,
        float? newGentleFrequency = null, float? oldGentleFrequency = null,
        float? newGentleAmplitude = null, float? oldGentleAmplitude = null,
        float? newDetailFrequency = null, float? oldDetailFrequency = null,
        int? newDetailOctaves = null, int? oldDetailOctaves = null)
    {
        RequirePair(newWaterLevel.HasValue, oldWaterLevel.HasValue, nameof(newWaterLevel));
        RequirePair(newSeed.HasValue, oldSeed.HasValue, nameof(newSeed));
        RequirePair(newBiomeBlend.HasValue, oldBiomeBlend.HasValue, nameof(newBiomeBlend));
        RequirePair(newGentleFrequency.HasValue, oldGentleFrequency.HasValue, nameof(newGentleFrequency));
        RequirePair(newGentleAmplitude.HasValue, oldGentleAmplitude.HasValue, nameof(newGentleAmplitude));
        RequirePair(newDetailFrequency.HasValue, oldDetailFrequency.HasValue, nameof(newDetailFrequency));
        RequirePair(newDetailOctaves.HasValue, oldDetailOctaves.HasValue, nameof(newDetailOctaves));
        _newWaterLevel = newWaterLevel; _oldWaterLevel = oldWaterLevel;
        _newSeed = newSeed; _oldSeed = oldSeed;
        _newBiomeBlend = newBiomeBlend; _oldBiomeBlend = oldBiomeBlend;
        _newGentleFrequency = newGentleFrequency; _oldGentleFrequency = oldGentleFrequency;
        _newGentleAmplitude = newGentleAmplitude; _oldGentleAmplitude = oldGentleAmplitude;
        _newDetailFrequency = newDetailFrequency; _oldDetailFrequency = oldDetailFrequency;
        _newDetailOctaves = newDetailOctaves; _oldDetailOctaves = oldDetailOctaves;
    }

    static void RequirePair(bool hasNew, bool hasOld, string field)
    {
        if (hasNew && !hasOld)
            throw new ArgumentException($"EditTerrainCommand '{field}' needs its matching old value for revert.", field);
    }

    /// <inheritdoc/>
    public override string Label => "Edit terrain";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        MapTerrain t = doc.Terrain;
        if (_newWaterLevel is float wl) t.WaterLevel = wl;
        if (_newSeed is int sd) t.Seed = sd;
        if (_newBiomeBlend is float bb) t.BiomeBlend = bb;
        if (_newGentleFrequency is float gf) t.GentleFrequency = gf;
        if (_newGentleAmplitude is float ga) t.GentleAmplitude = ga;
        if (_newDetailFrequency is float df) t.DetailFrequency = df;
        if (_newDetailOctaves is int oct) t.DetailOctaves = oct;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        MapTerrain t = doc.Terrain;
        // Revert only the fields this command actually set (gated on new, so a stray old with no new is ignored).
        if (_newWaterLevel is not null && _oldWaterLevel is float wl) t.WaterLevel = wl;
        if (_newSeed is not null && _oldSeed is int sd) t.Seed = sd;
        if (_newBiomeBlend is not null && _oldBiomeBlend is float bb) t.BiomeBlend = bb;
        if (_newGentleFrequency is not null && _oldGentleFrequency is float gf) t.GentleFrequency = gf;
        if (_newGentleAmplitude is not null && _oldGentleAmplitude is float ga) t.GentleAmplitude = ga;
        if (_newDetailFrequency is not null && _oldDetailFrequency is float df) t.DetailFrequency = df;
        if (_newDetailOctaves is not null && _oldDetailOctaves is int oct) t.DetailOctaves = oct;
    }

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        // Terrain is a singleton, so any two terrain edits of the same gesture coalesce (no id / index to match).
        // Each field folds in as a union: last-new wins, first-old is kept (see MergeField).
        if (next is not EditTerrainCommand t) return false;
        MergeField(ref _newWaterLevel, ref _oldWaterLevel, t._newWaterLevel, t._oldWaterLevel);
        MergeField(ref _newSeed, ref _oldSeed, t._newSeed, t._oldSeed);
        MergeField(ref _newBiomeBlend, ref _oldBiomeBlend, t._newBiomeBlend, t._oldBiomeBlend);
        MergeField(ref _newGentleFrequency, ref _oldGentleFrequency, t._newGentleFrequency, t._oldGentleFrequency);
        MergeField(ref _newGentleAmplitude, ref _oldGentleAmplitude, t._newGentleAmplitude, t._oldGentleAmplitude);
        MergeField(ref _newDetailFrequency, ref _oldDetailFrequency, t._newDetailFrequency, t._oldDetailFrequency);
        MergeField(ref _newDetailOctaves, ref _oldDetailOctaves, t._newDetailOctaves, t._oldDetailOctaves);
        return true;
    }

    // Fold one field of an absorbed terrain edit into this one. The incoming command not setting the field leaves
    // mine untouched. When it does set the field: I keep MY old if I already set it (first-old wins the revert),
    // else I adopt the incoming old (it was the first to set the field). Either way the incoming new wins.
    static void MergeField<T>(ref T? mineNew, ref T? mineOld, T? theirNew, T? theirOld) where T : struct
    {
        if (theirNew is null) return;
        if (mineNew is null) mineOld = theirOld;
        mineNew = theirNew;
    }
}

// ---- terrain biome bands (terrain-shape affecting) -------------------------------------------------------

/// <summary>Appends a terrain biome band (a world-Z-range biome slice, <see cref="MapBiomeBand"/>). Bands feed
/// the terrain field's biome selection and base-height / hill shaping, so this affects the streamed world. Appends
/// at the end (the <see cref="AddFeatureCommand"/> idiom), and <see cref="Revert"/> removes the slot it appended.</summary>
public sealed class AddBiomeBandCommand : EditorCommand
{
    readonly MapBiomeBand _band;

    /// <summary>Creates the command for the given band (added on <see cref="Apply"/>).</summary>
    public AddBiomeBandCommand(MapBiomeBand band) =>
        _band = band ?? throw new ArgumentNullException(nameof(band));

    /// <inheritdoc/>
    public override string Label => "Add biome band";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => ApplyAppend(doc.Terrain.Biomes, _band);

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => RevertAppend(doc.Terrain.Biomes);
}

/// <summary>Removes the terrain biome band at the given index, restoring it at that index on revert. The index is
/// range-guarded up front against the live band list, so a bad index is a precise <see cref="ArgumentException"/>
/// (with the parameter name) rather than the raw list's <see cref="ArgumentOutOfRangeException"/>, matching the
/// ke-mapedit RequireIndexInRange convention. Affects the streamed world.</summary>
public sealed class RemoveBiomeBandCommand : EditorCommand
{
    readonly int _index;
    MapBiomeBand? _removed;

    /// <summary>Creates the command for the biome-band list index to remove.</summary>
    public RemoveBiomeBandCommand(int index) => _index = index;

    /// <inheritdoc/>
    public override string Label => "Remove biome band";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        List<MapBiomeBand> bands = doc.Terrain.Biomes;
        if (_index < 0 || _index >= bands.Count)
            throw new ArgumentException(
                bands.Count == 0
                    ? $"biome band index {_index} is out of range: the terrain has no bands to address."
                    : $"biome band index {_index} is out of range. Valid range is [0, {bands.Count - 1}].",
                nameof(_index));
        _removed = bands[_index];
        bands.RemoveAt(_index);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (_removed is null) throw new InvalidOperationException("Revert called before Apply.");
        doc.Terrain.Biomes.Insert(_index, _removed);
    }
}

/// <summary>Replaces the terrain biome band at a given index with a new value (a whole-value edit, the
/// <see cref="EditFeatureCommand"/> idiom). The caller supplies both the new and old band (a clone with the one
/// changed field). Successive edits of the same index coalesce into one undo step (scrub coalescing). Affects the
/// streamed world.</summary>
public sealed class EditBiomeBandCommand : EditorCommand
{
    readonly int _index;
    MapBiomeBand _newValue;
    readonly MapBiomeBand _oldValue;

    /// <summary>Creates the command replacing band <paramref name="index"/> with <paramref name="newValue"/>,
    /// capturing <paramref name="oldValue"/> for revert.</summary>
    public EditBiomeBandCommand(int index, MapBiomeBand newValue, MapBiomeBand oldValue)
    {
        _index = index;
        _newValue = newValue ?? throw new ArgumentNullException(nameof(newValue));
        _oldValue = oldValue ?? throw new ArgumentNullException(nameof(oldValue));
    }

    /// <inheritdoc/>
    public override string Label => "Edit biome band";
    internal override bool AffectsWorld => true;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => doc.Terrain.Biomes[_index] = _newValue;

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => doc.Terrain.Biomes[_index] = _oldValue;

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        if (next is EditBiomeBandCommand b && b._index == _index)
        {
            _newValue = b._newValue;
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
    /// <remarks>The added feature's footprint, or null when it has no bounded footprint (a rim, a ridge, or a
    /// custom type, which force a full rebuild).</remarks>
    internal override RectArea? DirtyRegion =>
        FeatureGeometry.TryFootprint(_feature, out RectArea area) ? area : null;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc) => ApplyAppend(doc.Terrain.Features, _feature);

    /// <inheritdoc/>
    public override void Revert(MapDocument doc) => RevertAppend(doc.Terrain.Features);
}

/// <summary>Removes the terrain feature at the given index, restoring it at that index on revert. Affects the
/// streamed world.</summary>
public sealed class RemoveFeatureCommand : EditorCommand, IVisibilityEffect
{
    readonly int _index;
    MapFeature? _removed;

    /// <summary>Creates the command for the feature list index to remove.</summary>
    public RemoveFeatureCommand(int index) => _index = index;

    /// <inheritdoc/>
    public override string Label => "Remove terrain feature";
    internal override bool AffectsWorld => true;
    VisibilityOp IVisibilityEffect.Effect => VisibilityOp.RemoveAt(SelectionKind.Feature, _index);

    /// <inheritdoc/>
    /// <remarks>The removed feature's footprint, or null before <see cref="Apply"/> has captured it (the removed
    /// value is not known until then) or when it has no bounded footprint (a rim, ridge, or custom type). The editor
    /// reads <see cref="EditorCommand.DirtyRegion"/> only after the command has applied, so the captured value is
    /// available for both the initial execute and any later undo/redo.</remarks>
    internal override RectArea? DirtyRegion =>
        _removed is not null && FeatureGeometry.TryFootprint(_removed, out RectArea area) ? area : null;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        int count = doc.Terrain.Features.Count;
        if (_index < 0 || _index >= count)
            throw new ArgumentOutOfRangeException(nameof(_index), _index, $"RemoveFeatureCommand: index is out of range (count {count}).");
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
    /// <remarks>The union of the old and new feature footprints (both endpoints of a scrub or drag change terrain),
    /// or null when EITHER endpoint has no bounded footprint (a rim, ridge, or custom type on either side forces a
    /// full rebuild). Computed live from the CURRENT <see cref="_oldValue"/> / <see cref="_newValue"/> every read,
    /// never cached: <see cref="TryMerge"/> rewrites <see cref="_newValue"/> as a drag coalesces, so a cached region would
    /// stop covering the latest endpoint mid-drag.</remarks>
    internal override RectArea? DirtyRegion
    {
        get
        {
            if (!FeatureGeometry.TryFootprint(_oldValue, out RectArea oldArea)) return null;
            if (!FeatureGeometry.TryFootprint(_newValue, out RectArea newArea)) return null;
            return FeatureGeometry.Union(oldArea, newArea);
        }
    }

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
public sealed class ReorderFeatureCommand : EditorCommand, IVisibilityEffect
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
    VisibilityOp IVisibilityEffect.Effect => VisibilityOp.Reorder(SelectionKind.Feature, _fromIndex, _toIndex);

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
public sealed class ReorderExclusionCommand : EditorCommand, IVisibilityEffect
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
    VisibilityOp IVisibilityEffect.Effect => VisibilityOp.Reorder(SelectionKind.Exclusion, _fromIndex, _toIndex);

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
