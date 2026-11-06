namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Measured prefab codec admission. Value-taking guards share the production limits without allocating a
/// payload or materializing a collection merely to check its count.</summary>
internal static class MapPrefabCapacity
{
    internal static void RequirePayloadBytes(long byteCount, string payloadKind, string? resourceId = null)
    {
        if (byteCount > MapPrefabJson.Limits.MaxPayloadBytes)
            throw new MapDocumentException(MapDocumentRefusal.WorkCapacityExceeded, payloadKind + " payload byte limit exceeded.")
            {
                Budget = "PayloadBytes", ActualByteCount = byteCount, Limit = MapPrefabJson.Limits.MaxPayloadBytes,
                FieldPath = "ByteLength", ResourceId = resourceId,
            };
    }

    internal static void RequireGeometry(long count, string path)
    {
        if (count > MapPrefabJson.Limits.MaxGeometryElements)
            throw new MapDocumentException(MapDocumentRefusal.WorkCapacityExceeded, "Prefab geometry limit exceeded.")
            { Budget = "GeometryElements", ActualCount = count, Limit = MapPrefabJson.Limits.MaxGeometryElements, FieldPath = path };
    }

    internal static void RequireCollection(long count, string name, string path, bool aggregate)
    {
        int limit = name switch
        {
            "instances" => MapPrefabJson.Limits.MaxInstances,
            "children" => MapPrefabJson.Limits.MaxChildren,
            "references" => MapPrefabJson.Limits.MaxReferenceEdges,
            "patches" => MapPrefabJson.Limits.MaxPatches,
            "entries" => MapPrefabJson.Limits.MaxBindings,
            "overrides" => MapPrefabJson.Limits.MaxOverrides,
            "transfers" or "attachments" or "materializedAttachments" => MapPrefabJson.Limits.MaxTransfers,
            _ => MapPrefabJson.Limits.MaxGeometryElements,
        };
        if (count > limit)
            throw new MapDocumentException(MapDocumentRefusal.WorkCapacityExceeded,
                "Prefab " + (aggregate ? "aggregate " : "") + name + " limit exceeded.")
            { Budget = aggregate ? "Aggregate." + name : name, ActualCount = count, Limit = limit, FieldPath = path };
    }
}
