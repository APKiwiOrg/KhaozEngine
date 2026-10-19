using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Support;

/// <summary>Prepares point-independent support facts once per acquisition, so many points share them.</summary>
public static class MapSupportPreparation
{
    public static MapPreparedSupport Prepare(MapScopedSurfaces view) => new(view);
}
