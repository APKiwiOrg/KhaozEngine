using System;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

public sealed partial class AddPlacementCommand
{
    AllocateNativePlacementCommand? _nativeAllocation;
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo)
    {
        var allocation = _nativeAllocation ?? new AllocateNativePlacementCommand(_placement, _placement.NumericId is null);
        Action accept = ((INativePlacementCommand)allocation).Prepare(doc, undo);
        return () => { accept(); _nativeAllocation = allocation; };
    }
}

public sealed partial class RemovePlacementCommand
{
    readonly NativePlacementEdit _native = new();
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo) => _native.Remove(doc, _id, undo);
}

public sealed partial class MovePlacementCommand
{
    readonly NativePlacementEdit _native = new();
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo) =>
        _native.Change(doc, _id, undo, p => { p.X = _newX; p.Z = _newZ; p.Y = _newY; });
}

public sealed partial class RotatePlacementCommand
{
    readonly NativePlacementEdit _native = new();
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo) =>
        _native.Change(doc, _id, undo, p => p.Yaw = _newYaw);
}

public sealed partial class ScalePlacementCommand
{
    readonly NativePlacementEdit _native = new();
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo) =>
        _native.Change(doc, _id, undo, p => p.Scale = _newScale);
}

public sealed partial class RenamePlacementCommand
{
    readonly NativePlacementEdit _native = new();
    bool _nativeLabelApplied;
    Action INativePlacementCommand.Prepare(MapDocument doc, bool undo)
    {
        Action accept = _native.Change(doc, _oldId, undo, p => p.DisplayName = _newId);
        return () => { accept(); _nativeLabelApplied = true; };
    }
}
