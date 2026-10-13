using System.Collections.Generic;
using System.ComponentModel;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using ModelContextProtocol.Server;

namespace KhaozEngine.MapEdit.Tools;

/// <summary>Closed prefab wire adapters. Coordinates are metres and yaw is radians.</summary>
[McpServerToolType]
public sealed class PrefabTools(PrefabService prefab, MutationService mutation, MapEditSession session)
{
    [McpServerTool(Name = "prefab_open"), Description("Opens a detached prefab asset session. Parent-dependent assets require an explicit parent or a referring instance's authored parent.")]
    public MapPrefabAsset PrefabOpen(string resourceId, string? parentRefJson = null) => ToolGuard.Guard(() =>
        prefab.Open(resourceId, parentRefJson is null ? null : MapPrefabJson.ReadRecordRef(parentRefJson)));

    [McpServerTool(Name = "prefab_edit"), Description("Replaces or adds one keyed local child, or removes it, without changing the open map.")]
    public MapPrefabAsset PrefabEdit(string childJson, bool remove = false) => ToolGuard.Guard(() => prefab.Edit(MapPrefabJson.ReadChild(childJson), remove));

    [McpServerTool(Name = "prefab_save"), Description("Publishes immutable prefab and manifest bytes, optionally updating every referring live instance in the open map as one native history entry.")]
    public MapPrefabPublishResult PrefabSave(string resourceId, string relativeDirectory, bool updateOpenMap = true) =>
        ToolGuard.Guard(() => prefab.Save(resourceId, relativeDirectory, updateOpenMap));

    [McpServerTool(Name = "prefab_extract"), Description("Extracts a complete selection of typed World references into a detached local prefab asset session.")]
    public MapPrefabAsset PrefabExtract(string selectedJson, float x, float y, float z, float yaw, float scale, string assetId) =>
        ToolGuard.Guard(() => prefab.Extract(MapPrefabJson.ReadReferences(selectedJson), new(new(x, y, z), yaw, scale), assetId));

    [McpServerTool(Name = "prefab_place"), Description("Places an immutable prefab using free XYZ metres, yaw radians and positive uniform scale.")]
    public MapPrefabMutationResult PrefabPlace(string instanceId, string resourceId, float x, float y, float z, float yaw, float scale,
        string? parentRefJson = null, bool numericLeaves = true) => ToolGuard.Guard(() => mutation.PrefabApply(new MapPlacePrefab(instanceId,
            resourceId, new(new(x, y, z), yaw, scale), parentRefJson is null ? null : MapPrefabJson.ReadRecordRef(parentRefJson), numericLeaves)));

    [McpServerTool(Name = "prefab_list"), Description("Lists the immutable prefab resources in the open map's verified closure.")]
    public IReadOnlyList<MapPrefabAssetInfo> PrefabList() => ToolGuard.Guard(prefab.List);

    [McpServerTool(Name = "prefab_override"), Description("Applies one closed keyed prefab override, retaining reservations and materialized ownership.")]
    public MapPrefabMutationResult PrefabOverride(string instanceId, string overrideJson) =>
        ToolGuard.Guard(() => mutation.PrefabApply(new MapSetPrefabOverride(instanceId, MapPrefabJson.ReadOverride(overrideJson))));

    [McpServerTool(Name = "prefab_unpack"), Description("Transfers live prefab children into standalone authored resources, retaining the removed source's reservations.")]
    public MapPrefabMutationResult PrefabUnpack(string instanceId) => ToolGuard.Guard(() => mutation.PrefabApply(new MapUnpackPrefab(instanceId)));

    [McpServerTool(Name = "prefab_undo"), Description("Undoes one whole-native-session history entry, including terrain, water, prefab and collider edits.")]
    public MapNativeEditEffects? PrefabUndo() => ToolGuard.Guard(session.UndoNative);

    [McpServerTool(Name = "prefab_redo"), Description("Redoes one whole-native-session history entry against its captured matching closure.")]
    public MapNativeEditEffects? PrefabRedo() => ToolGuard.Guard(session.RedoNative);
}
