using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Surfaces;

public static partial class MapPartialPaintCompiler
{
    readonly record struct Target(MapCompiledFace Face, MapExactTriangle Triangle, IReadOnlyList<MapWideXz> Parent);
    static MapPaintRegionSet CompileCore(MapScopedSurfaces view, MapSurfaceRef target, MapSurfaceRef[] paints, MapRefinementLimits limits)
    {
        IReadOnlyList<MapPaintRegion> regions;
        using (var preparation = MapSceneAdmission.Current?.BeginPreparation())
        {
            regions = CompileRegions(view, target, paints, limits);
            preparation?.Complete(checked(128L + regions.Sum(r => 128L + 48L * r.Polygon.Count)));
        }
        return new(regions, Digest(regions));
    }
    static Source[] CaptureSources(MapScopedSurfaces view, IEnumerable<(MapSurfaceRef Surface, MapSurfaceRef Adapter, MapSurfacePatch Patch, long Faces)> inputs, Work work)
    {
        using var preparation = MapSceneAdmission.Current?.BeginPreparation();
        var result = inputs.Select(p => (p.Surface, Geometry: MapSurfaceCompiler.Compile(p.Adapter, p.Patch))).GroupBy(p => p.Surface)
            .SelectMany(group => PaintSources(view, group.Key, group.Select(p => p.Geometry), work))
            .OrderByDescending(p => p.Priority).ThenBy(p => p.Id, System.StringComparer.Ordinal).ThenBy(p => p.Material.Face).ToArray();
        preparation?.Complete(checked(result.Sum(p => 128L + 64L * p.Polygon.Count)));
        return result;
    }
    static Target[] CaptureTargets(MapScopedSurfaces view, MapSurfaceRef target, MapSurfacePatch patch, MapSlotCellMask cells)
    {
        using var preparation = MapSceneAdmission.Current?.BeginPreparation();
        var local = MapSurfaceCompiler.Compile(target, patch, cells, MapSurfaceCompiler.MaxFacesPerPatch);
        var geometry = MapEffectiveGeometry.TransformPatch(view, local);
        MapSceneAdmission.Current?.ReserveBytes(checked(512L * geometry.Faces.Count));
        var rows = geometry.Faces.Select(face =>
        {
            var triangle = geometry.ExactTriangle(face);
            return new Target(face, triangle, Polygon(triangle));
        }).ToArray();
        preparation?.Complete(checked(512L * rows.Length));
        return rows;
    }
    static long TargetCopyBytes(long count) => checked(2L * Unsafe.SizeOf<Target>() * count);
}
