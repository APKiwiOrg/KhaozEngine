namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Shared exact vertex admission for boundary resolution and physical face emission.</summary>
internal static class MapSurfaceGeometryAdmission
{
    // Value-taking admission also lets headless tests inject a disagreeing producer vertex without
    // corrupting a validated patch or depending on floating-point cancellation.
    internal static void RequireSharedVertexHeight(MapExactPoint expected, MapSurfaceVertex vertex, MapPatchKey patch,
        string message = "inconsistent exact height for shared surface vertex", string? recordId = null)
    {
        if (expected != vertex.Exact)
            throw new MapDocumentException(MapDocumentRefusal.GeometryVertexHeightMismatch, message)
            { SurfaceId = patch.SurfaceId, Patch = patch, Address = vertex.Address, RecordId = recordId };
    }
}
