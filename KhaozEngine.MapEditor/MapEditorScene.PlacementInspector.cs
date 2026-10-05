using System;
using KhaozEngine.Gui;
using KhaozEngine.App;

namespace KhaozEngine.MapEditor;

public partial class MapEditorScene
{
    void BuildPlacementInspector(string id)
    {
        if (Placement(id) is null) return;
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Identity")));
        Func<string> cur;
        if (_document.Doc.ResolverIdentity is not null)
        {
            cur = () => id;
            _inspector.Rows.Add(new TextRow(LocalizedText.Raw("Label"),
                () => Placement(id)?.DisplayName ?? "",
                label => RunNativeEditAction(() => _document.Execute(new RenamePlacementCommand(id, label))),
                description: LocalizedText.Raw("Display label. Stable and numeric placement identities are unchanged.")));
        }
        else
        {
            cur = AddNameRow(SelectionKind.Placement, id,
            v => Placement(v) is not null, (oldId, newId) => new RenamePlacementCommand(oldId, newId),
            LocalizedText.Raw(
                "Unique id for this placement. Renaming it updates the outline node and the current selection to " +
                "follow the new id. Must be non-empty and not collide with another placement's id."));
        }
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("Transform")));
        AddFloatRow(LocalizedText.Raw("X"),
            () => Placement(cur())?.X ?? 0f, v => MovePlacement(cur(), x: v),
            description: LocalizedText.Raw("World-space X coordinate, in world units."));
        AddFloatRow(LocalizedText.Raw("Z"),
            () => Placement(cur())?.Z ?? 0f, v => MovePlacement(cur(), z: v),
            description: LocalizedText.Raw("World-space Z coordinate, in world units."));
        AddFloatRow(LocalizedText.Raw("Yaw"),
            () => Placement(cur())?.Yaw ?? 0f, v => _document.Execute(new RotatePlacementCommand(cur(), v)),
            description: LocalizedText.Raw(
                "Facing rotation around the vertical (Y) axis, in radians."));
        AddFloatRow(LocalizedText.Raw("Scale"),
            () => Placement(cur())?.Scale ?? 1f, v => _document.Execute(new ScalePlacementCommand(cur(), v)),
            min: 0.01f,
            description: LocalizedText.Raw(
                "Uniform scale multiplier applied to the placed kit's mesh. 1 is the kit's authored size, below " +
                "1 shrinks it, above 1 grows it."));
        _inspector.Rows.Add(new HeaderRow(LocalizedText.Raw("State")));
        AddVisibleRow(SelectionKind.Placement, cur);
    }

}
