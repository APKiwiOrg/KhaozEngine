using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapEditor;

public sealed partial class AddPlacementCommand
{
    AllocateNativePlacementCommand? _nativeAllocation;

    // A native Add publishes a copy, so a same-ID move must match that accepted copy, not the caller's payload,
    // which the caller still holds and may change. An analytic Add appends the payload itself, so its ID is live.
    string MergeId => _nativeAllocation?.AcceptedId ?? _placement.Id;

    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo, MapAssetClosure? assets)
    {
        var allocation = _nativeAllocation ?? new AllocateNativePlacementCommand(_placement, _placement.NumericId is null);
        Action accept = ((INativePlacementCommand)allocation).Prepare(doc, undo, assets);
        return () => { accept(); _nativeAllocation = allocation; };
    }
}

public sealed partial class RemovePlacementCommand : INativePlacementWriteSetCommand
{
    MapNativeWriteSet? INativePlacementWriteSetCommand.AcceptedWrites => _native.Writes;
    readonly NativePlacementEdit _native = new();
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo, MapAssetClosure? assets) => _native.Remove(doc, _id, undo);
}

public sealed partial class MovePlacementCommand : INativePlacementWriteSetCommand
{
    MapNativeWriteSet? INativePlacementWriteSetCommand.AcceptedWrites => _native.Writes;
    readonly NativePlacementEdit _native = new();
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo, MapAssetClosure? assets) =>
        _native.Change(doc, _id, undo, p => { p.X = _newX; p.Z = _newZ; p.Y = _newY; }, MapPlacementPoseFields.X | MapPlacementPoseFields.Y | MapPlacementPoseFields.Z, assets);
}

public sealed partial class RotatePlacementCommand : INativePlacementWriteSetCommand
{
    MapNativeWriteSet? INativePlacementWriteSetCommand.AcceptedWrites => _native.Writes;
    readonly NativePlacementEdit _native = new();
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo, MapAssetClosure? assets) =>
        _native.Change(doc, _id, undo, p => p.Yaw = _newYaw, MapPlacementPoseFields.Yaw, assets);
}

public sealed partial class ScalePlacementCommand : INativePlacementWriteSetCommand
{
    MapNativeWriteSet? INativePlacementWriteSetCommand.AcceptedWrites => _native.Writes;
    readonly NativePlacementEdit _native = new();
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo, MapAssetClosure? assets) =>
        _native.Change(doc, _id, undo, p => p.Scale = _newScale, MapPlacementPoseFields.Scale, assets);
}

public sealed partial class RenamePlacementCommand : INativePlacementWriteSetCommand
{
    MapNativeWriteSet? INativePlacementWriteSetCommand.AcceptedWrites => _native.Writes;
    readonly NativePlacementEdit _native = new();
    bool _nativeLabelApplied;
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo, MapAssetClosure? assets)
    {
        Action accept = _native.Change(doc, _oldId, undo, p => p.DisplayName = _newId);
        return () => { accept(); _nativeLabelApplied = true; };
    }
}

public sealed partial class MovePlacementCommand : INativePlacementHistoryMerge
{
    Action? INativePlacementHistoryMerge.PrepareMerge(IEditorCommand newer, NativeStagedPublication publication) =>
        newer is MovePlacementCommand m && m._id == _id
            ? _native.PrepareMerge(m._native, publication, _id, () => { _newX = m._newX; _newY = m._newY; _newZ = m._newZ; }) : null;
}
public sealed partial class RotatePlacementCommand : INativePlacementHistoryMerge
{
    Action? INativePlacementHistoryMerge.PrepareMerge(IEditorCommand newer, NativeStagedPublication publication) =>
        newer is RotatePlacementCommand r && r._id == _id
            ? _native.PrepareMerge(r._native, publication, _id, () => _newYaw = r._newYaw) : null;
}
public sealed partial class ScalePlacementCommand : INativePlacementHistoryMerge
{
    Action? INativePlacementHistoryMerge.PrepareMerge(IEditorCommand newer, NativeStagedPublication publication) =>
        newer is ScalePlacementCommand s && s._id == _id
            ? _native.PrepareMerge(s._native, publication, _id, () => _newScale = s._newScale) : null;
}
public sealed partial class AddPlacementCommand : INativePlacementHistoryMerge
{
    Action? INativePlacementHistoryMerge.PrepareMerge(IEditorCommand newer, NativeStagedPublication publication) =>
        _nativeAllocation is not null && newer is MovePlacementCommand m && m.Id == MergeId
            ? () => _nativeAllocation.MergeMove(m) : null;
}
