using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Validation evidence for borrowed patch data that stays immutable for the caller's lifetime.</summary>
internal sealed class MapValidatedSurfacePatch
{
    internal MapSurfaceRef Surface { get; }
    internal MapSurfacePatch Patch { get; }

    internal MapValidatedSurfacePatch(MapSurfaceRef surface, MapSurfacePatch patch, MapBoundFaceWork? work = null)
    {
        MapSurfaceCompiler.Validate(surface, patch, work);
        Surface = surface;
        Patch = patch;
    }
}
