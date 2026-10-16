using System.Collections.Generic;
using System.ComponentModel;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using ModelContextProtocol.Server;

namespace KhaozEngine.MapEdit.Tools;

/// <summary>General marker wire adapters over the shared marker command and the effective marker list. Coordinates
/// are metres and yaw is radians.</summary>
[McpServerToolType]
public sealed class MarkerTools(MutationService mutation, QueryService query)
{
    [McpServerTool(Name = "marker_set"), Description("Sets one standalone marker by its immutable ID, creating it when absent. markerJson is one closed " +
        "local marker in world terms: payload 1, or payload 2 with heightMode Explicit (Position.Y is the height) or Support (supportBinding and Position.Y 0). " +
        "The name is an editable member, never a key. A framed standalone marker stores the frame-local equivalent. A live prefab marker refuses, use prefab_override.")]
    public MapPrefabMutationResult MarkerSet(string markerJson) =>
        ToolGuard.Guard(() => mutation.MarkerApply(new MapSetMarker(MapPrefabJson.ReadMarker(markerJson))));

    [McpServerTool(Name = "marker_remove"), Description("Removes one standalone marker by ID. A transferred marker keeps its retired reservation. A live prefab marker refuses, use prefab_override.")]
    public MapPrefabMutationResult MarkerRemove(string id) => ToolGuard.Guard(() => mutation.MarkerApply(new MapRemoveMarker(id)));

    [McpServerTool(Name = "marker_list"), Description("Lists effective markers in ordinal ID order with resolved heights. Optional filters: exact name, role and tag, " +
        "enabled only, and a half-open XZ rectangle that includes minX and minZ and excludes maxX and maxZ. Give all four rectangle bounds or none.")]
    public IReadOnlyList<MapEffectiveMarker> MarkerList(string? name = null, string? role = null, string? tag = null, bool enabledOnly = false,
        double? minX = null, double? minZ = null, double? maxX = null, double? maxZ = null) => ToolGuard.Guard(() =>
        {
            int given = (minX is null ? 0 : 1) + (minZ is null ? 0 : 1) + (maxX is null ? 0 : 1) + (maxZ is null ? 0 : 1);
            if (given is not (0 or 4)) throw new MapDocumentException("marker_list takes minX, minZ, maxX and maxZ together or not at all.");
            return query.MarkerList(name, role, tag, enabledOnly, given == 4 ? new MapMarkerRect(minX!.Value, minZ!.Value, maxX!.Value, maxZ!.Value) : null);
        });
}
