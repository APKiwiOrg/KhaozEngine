using System;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>Allocates at first acceptance. Apply/Revert validate locally, bound editor transactions also validate assets.</summary>
public sealed class AllocateNativePlacementCommand : EditorCommand, INativePlacementCommand
{
    readonly MapPlacement _placement;
    readonly bool _allocateNumericId;
    MapPlacement? _accepted;

    public AllocateNativePlacementCommand(MapPlacement placement, bool allocateNumericId)
    {
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        _allocateNumericId = allocateNumericId;
    }
    public override string Label => "Add placement";
    internal override bool AffectsWorld => false;
    internal override bool ChangesPlacements => true;
    public override void Apply(MapDocument doc) => NativePlacementTransaction.Run(doc, this, false, null, localOnly: true);
    public override void Revert(MapDocument doc) => NativePlacementTransaction.Run(doc, this, true, null, localOnly: true);

    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo)
    {
        if (undo)
        {
            if (_accepted is null) throw new InvalidOperationException("Revert called before Apply.");
            doc.Placements.Remove(NativePlacementTransaction.Find(doc, _accepted.Id));
            return () => { };
        }
        MapPlacement p = NativePlacementTransaction.Copy(_accepted ?? _placement);
        NativePlacementTransaction.RequireAbsent(doc, p.Id);
        if (_accepted is null && _allocateNumericId) p.NumericId = MapNumericIds.Allocate(doc);
        if (p.NumericId is { } id) MapNumericIds.Reserve(doc, new[] { id });
        doc.Placements.Add(p);
        MapPlacement accepted = NativePlacementTransaction.Copy(p);
        return () => _accepted = accepted;
    }

    /// <summary>The stable ID of the copy this command published on its first successful acceptance, or null
    /// before then. Later changes to the caller's payload never alter it.</summary>
    internal string? AcceptedId => _accepted?.Id;

    internal void MergeMove(MovePlacementCommand move)
    {
        if (_accepted is null) return;
        _accepted.X = move.NewX;
        _accepted.Z = move.NewZ;
        _accepted.Y = move.NewY;
    }
}

/// <summary>Changes only a native display label. Direct Apply/Revert validate locally, not against an asset closure.</summary>
public sealed class SetPlacementLabelCommand : EditorCommand, INativePlacementCommand
{
    readonly string _id;
    readonly string _label;
    readonly NativePlacementEdit _edit = new();
    public SetPlacementLabelCommand(string placementId, string label)
    {
        _id = placementId ?? throw new ArgumentNullException(nameof(placementId));
        _label = label ?? throw new ArgumentNullException(nameof(label));
    }
    public override string Label => "Set placement label";
    internal override bool AffectsWorld => false;
    internal override bool ChangesPlacements => true;
    public override void Apply(MapDocument doc) => NativePlacementTransaction.Run(doc, this, false, null, localOnly: true);
    public override void Revert(MapDocument doc) => NativePlacementTransaction.Run(doc, this, true, null, localOnly: true);
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo) =>
        _edit.Change(doc, _id, undo, p => p.DisplayName = _label);
}

/// <summary>Explicit stable-ID remap. Numeric identity is unchanged. Full asset checks belong to bound transactions.</summary>
public sealed class RemapPlacementIdCommand : EditorCommand, INativePlacementCommand, IVisibilityEffect
{
    readonly string _oldId;
    readonly string _newId;
    readonly NativePlacementEdit _edit = new();
    public RemapPlacementIdCommand(string oldId, string newId)
    {
        _oldId = oldId ?? throw new ArgumentNullException(nameof(oldId));
        _newId = newId ?? throw new ArgumentNullException(nameof(newId));
    }
    public override string Label => "Remap placement ID";
    internal override bool AffectsWorld => false;
    internal override bool ChangesPlacements => true;
    VisibilityOp IVisibilityEffect.Effect => VisibilityOp.Rename(SelectionKind.Placement, _oldId, _newId);
    public override void Apply(MapDocument doc) => NativePlacementTransaction.Run(doc, this, false, null, localOnly: true);
    public override void Revert(MapDocument doc) => NativePlacementTransaction.Run(doc, this, true, null, localOnly: true);
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo)
    {
        NativePlacementTransaction.RequireAbsent(doc, undo ? _oldId : _newId);
        Action accept = _edit.Change(doc, _oldId, undo, p => p.Id = _newId);
        NativePlacementReferences.Remap(doc, undo ? _newId : _oldId, undo ? _oldId : _newId);
        return accept;
    }
}
