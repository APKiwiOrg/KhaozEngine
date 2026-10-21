using System;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Logical ground triangles for foliage compatibility sampling. Every present cell compiles through the physical
/// compiler's kernel, including legacy fallback cells (NoDraw or a zero underlay) that have no physical faces. The
/// result is a sampling input only. Nothing here installs faces, colliders or support.</summary>
public static class MapFoliageTriangles
{
    /// <summary>Compiles every present cell of <paramref name="patch"/>, fallback cells included, with the canonical
    /// face keys, vertices and paint coverage the physical compiler gives the same cell.</summary>
    public static MapCompiledPatch Compile(MapSurfaceRef surface, MapSurfacePatch patch)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(patch);
        return Compile(new MapValidatedSurfacePatch(surface, patch), MapSlotCellMask.All, MapSurfaceCompiler.MaxFacesPerPatch);
    }

    /// <summary>Compiles only the demanded slot cells, refusing above <paramref name="maxFaces"/> before emission.</summary>
    internal static MapCompiledPatch Compile(MapValidatedSurfacePatch validated, MapSlotCellMask cells, int maxFaces) =>
        MapSurfaceCompiler.Compile(validated, cells, maxFaces, logical: true);

    /// <summary>The logical faces the demanded cells would emit, counted without compiling them.</summary>
    internal static long CountFaces(MapValidatedSurfacePatch validated, MapSlotCellMask cells) =>
        MapSurfaceCompiler.CountFaces(validated, cells, logical: true);
}
