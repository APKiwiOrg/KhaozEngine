using System;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>Appends an authored placement to the document. Absorbs a same-id <see cref="MovePlacementCommand"/> that
/// immediately follows (place-and-adjust): the placed prop can be dragged into position within the same gesture and
/// the whole thing stays ONE undo step whose <see cref="Revert"/> removes the placement, restoring the pre-place
/// placement state. Native undo retains the allocation high-water mark. Native direct Apply/Revert validate
/// locally, while bound editor/history transactions also verify the asset closure.</summary>
public sealed partial class AddPlacementCommand : EditorCommand, INativePlacementCommand
{
    readonly MapPlacement _placement;

    /// <summary>Creates the command for the given placement (added on <see cref="Apply"/>).</summary>
    public AddPlacementCommand(MapPlacement placement) =>
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));

    /// <inheritdoc/>
    public override string Label => "Add placement";
    internal override bool AffectsWorld => false;

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        if (doc.ResolverIdentity is not null)
        {
            NativePlacementTransaction.Run(doc, this, false, null, localOnly: true);
            return;
        }
        ApplyAppendUnique(doc, _placement);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (doc.ResolverIdentity is not null)
        {
            NativePlacementTransaction.Run(doc, this, true, null, localOnly: true);
            return;
        }
        RevertAppend(doc.Placements);
    }

    /// <inheritdoc/>
    public override bool TryMerge(IEditorCommand next)
    {
        // Fold a same-id move into the Add: the placed prop's final position becomes part of the Add itself, so
        // place-and-adjust is one undo step and Revert still just removes the placement.
        if (next is MovePlacementCommand m && string.Equals(m.Id, _placement.Id, StringComparison.Ordinal))
        {
            _nativeAllocation?.MergeMove(m);
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
public sealed partial class RemovePlacementCommand : EditorCommand, INativePlacementCommand
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
        if (doc.ResolverIdentity is not null)
        {
            NativePlacementTransaction.Run(doc, this, false, null, localOnly: true);
            return;
        }
        MapPlacement p = FindPlacement(doc, _id);
        _index = doc.Placements.IndexOf(p);
        _removed = p;
        doc.Placements.RemoveAt(_index);
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (doc.ResolverIdentity is not null)
        {
            NativePlacementTransaction.Run(doc, this, true, null, localOnly: true);
            return;
        }
        if (_removed is null) throw new InvalidOperationException("Revert called before Apply.");
        doc.Placements.Insert(_index, _removed);
    }
}

/// <summary>Moves a placement to a new XZ (and optional Y). Successive moves of the same placement coalesce
/// into one undo step (drag coalescing).</summary>
public sealed partial class MovePlacementCommand : EditorCommand, INativePlacementCommand
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
        if (doc.ResolverIdentity is not null)
        {
            NativePlacementTransaction.Run(doc, this, false, null, localOnly: true);
            return;
        }
        MapPlacement p = FindPlacement(doc, _id);
        if (!_captured) { _oldX = p.X; _oldZ = p.Z; _oldY = p.Y; _captured = true; }
        p.X = _newX;
        p.Z = _newZ;
        p.Y = _newY;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (doc.ResolverIdentity is not null)
        {
            NativePlacementTransaction.Run(doc, this, true, null, localOnly: true);
            return;
        }
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
public sealed partial class RotatePlacementCommand : EditorCommand, INativePlacementCommand
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
        if (doc.ResolverIdentity is not null)
        {
            NativePlacementTransaction.Run(doc, this, false, null, localOnly: true);
            return;
        }
        MapPlacement p = FindPlacement(doc, _id);
        if (!_captured) { _oldYaw = p.Yaw; _captured = true; }
        p.Yaw = _newYaw;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (doc.ResolverIdentity is not null)
        {
            NativePlacementTransaction.Run(doc, this, true, null, localOnly: true);
            return;
        }
        FindPlacement(doc, _id).Yaw = _oldYaw;
    }

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
public sealed partial class ScalePlacementCommand : EditorCommand, INativePlacementCommand
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
        if (doc.ResolverIdentity is not null)
        {
            NativePlacementTransaction.Run(doc, this, false, null, localOnly: true);
            return;
        }
        MapPlacement p = FindPlacement(doc, _id);
        if (!_captured) { _oldScale = p.Scale; _captured = true; }
        p.Scale = _newScale;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (doc.ResolverIdentity is not null)
        {
            NativePlacementTransaction.Run(doc, this, true, null, localOnly: true);
            return;
        }
        FindPlacement(doc, _id).Scale = _oldScale;
    }

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

/// <summary>Renames the stable ID on analytic maps, or only DisplayName on native opted-in maps.
/// Analytic target IDs must be unique. Native ID changes use RemapPlacementIdCommand explicitly.
/// Renames never coalesce. Native direct Apply/Revert validate locally, not against an asset closure.</summary>
public sealed partial class RenamePlacementCommand : EditorCommand, INativePlacementCommand, IVisibilityEffect
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
    VisibilityOp IVisibilityEffect.Effect => VisibilityOp.Rename(SelectionKind.Placement, _oldId, _nativeLabelApplied ? _oldId : _newId);

    /// <inheritdoc/>
    public override void Apply(MapDocument doc)
    {
        if (doc.ResolverIdentity is not null)
        {
            NativePlacementTransaction.Run(doc, this, false, null, localOnly: true);
            return;
        }
        GuardNoPlacement(doc, _newId);   // reject a duplicate target before touching the source
        FindPlacement(doc, _oldId).Id = _newId;
    }

    /// <inheritdoc/>
    public override void Revert(MapDocument doc)
    {
        if (doc.ResolverIdentity is not null)
        {
            NativePlacementTransaction.Run(doc, this, true, null, localOnly: true);
            return;
        }
        FindPlacement(doc, _newId).Id = _oldId;
    }
}
