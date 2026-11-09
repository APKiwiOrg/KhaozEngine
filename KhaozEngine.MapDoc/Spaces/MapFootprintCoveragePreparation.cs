using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Retains cold all-footprint checks while restricting compiled context to the requested rectangle.</summary>
internal static class MapFootprintCoveragePreparation
{
    internal static MapFootprintPreparation Prepare(MapScopedSurfaces view, MapSpaceFootprint[] footprints,
        MapExactXz min, MapExactXz max, MapRefinementLimits limits, Water.MapWaterWorkMeter? meter)
    {
        return MapCommonRefinement.Prepare(view, footprints, f => f.SlotCells, limits, null, meter, Includes);

        bool Includes(MapSpaceFootprint footprint, int cell)
        {
            var extent = MapEffectiveGeometry.Bounds(MapEffectiveGeometry.CellPolygon(view, footprint.Lattice, cell));
            return extent.MaxX.CompareTo(min.X) > 0 && extent.MinX.CompareTo(max.X) < 0 &&
                extent.MaxZ.CompareTo(min.Z) > 0 && extent.MinZ.CompareTo(max.Z) < 0;
        }
    }
}
