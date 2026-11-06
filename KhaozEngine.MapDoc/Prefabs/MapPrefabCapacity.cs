namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Measured prefab codec admission. Value-taking guards share the production limits without allocating a
/// payload or materializing a collection merely to check its count.</summary>
internal static class MapPrefabCapacity
{
    internal static MapDocumentException CollectionRefusal(string field, long? count, long limit, string message,
        string? documentId = null, string? assetId = null) =>
        new(count is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.WorkCapacityExceeded, message)
        {
            FieldPath = assetId is null ? "$.prefabs." + field : "$." + field,
            Budget = field, ActualCount = count, Limit = count is null ? null : limit, DocumentId = documentId, AssetId = assetId,
        };

    internal static MapDocumentException AggregateRefusal(long edges, long patches, long records, long geometry, string path,
        MapPrefabExpansionLimits limits)
    {
        (string budget, long count, long limit) = edges > limits.MaxReferenceEdges ? ("ReferenceEdges", edges, limits.MaxReferenceEdges)
            : patches > limits.MaxPatches ? ("Patches", patches, limits.MaxPatches)
            : records > limits.MaxRecords ? ("Records", records, limits.MaxRecords)
            : ("GeometryElements", geometry, limits.MaxGeometryElements);
        return new(MapDocumentRefusal.WorkCapacityExceeded, "Prefab aggregate collection limit exceeded.")
        { Budget = "Aggregate." + budget, ActualCount = count, Limit = limit, FieldPath = path };
    }

    internal static void RequirePayloadBytes(long byteCount, string payloadKind, string? resourceId = null,
        long? maximumPayloadBytes = null)
    {
        long limit = maximumPayloadBytes ?? MapPrefabJson.Limits.MaxPayloadBytes;
        if (byteCount > limit)
            throw new MapDocumentException(MapDocumentRefusal.WorkCapacityExceeded, payloadKind + " payload byte limit exceeded.")
            {
                Budget = "PayloadBytes", ActualByteCount = byteCount, Limit = limit,
                FieldPath = "ByteLength", ResourceId = resourceId,
            };
    }

    internal static void RequireGeometry(long count, string path, MapPrefabExpansionLimits limits)
    {
        if (count > limits.MaxGeometryElements)
            throw new MapDocumentException(MapDocumentRefusal.WorkCapacityExceeded, "Prefab geometry limit exceeded.")
            { Budget = "GeometryElements", ActualCount = count, Limit = limits.MaxGeometryElements, FieldPath = path };
    }

    internal static void RequireCollection(long count, string name, string path, bool aggregate, MapPrefabExpansionLimits limits)
    {
        int limit = name switch
        {
            "instances" => limits.MaxInstances,
            "children" => limits.MaxChildren,
            "references" => limits.MaxReferenceEdges,
            "patches" => limits.MaxPatches,
            "entries" => limits.MaxBindings,
            "overrides" => limits.MaxOverrides,
            "transfers" or "attachments" or "materializedAttachments" => limits.MaxTransfers,
            _ => limits.MaxGeometryElements,
        };
        if (count > limit)
            throw new MapDocumentException(MapDocumentRefusal.WorkCapacityExceeded,
                "Prefab " + (aggregate ? "aggregate " : "") + name + " limit exceeded.")
            { Budget = aggregate ? "Aggregate." + name : name, ActualCount = count, Limit = limit, FieldPath = path };
    }
}
