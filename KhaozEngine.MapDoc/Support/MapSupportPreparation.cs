using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Support;

/// <summary>Prepares point-independent support facts once per acquisition, so many points share them.</summary>
public static class MapSupportPreparation
{
    public static MapPreparedSupport Prepare(MapScopedSurfaces view) => new(view);

    /// <summary>Prepares support for one foliage chunk scope. Records, footprints and seams are read once here, so the
    /// per-cell selections of a chunk preparation share them and no candidate ever reads them.</summary>
    internal static MapPreparedSupport PrepareFoliage(MapScopedSurfaces view)
    {
        var prepared = new MapPreparedSupport(view);
        _ = prepared.Footprints;
        _ = prepared.Seams;
        return prepared;
    }
}
